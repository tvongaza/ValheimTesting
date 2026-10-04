// One round of a mod's edit-build-test loop: build, install into a game copy, launch, run a strict test plan, and
// summarise the log. A .NET file-based script; the SDK that builds the mod runs it on every OS:
//
//   dotnet run --file tools/dev-loop/dev-loop.cs -- [--live] [--fail-on warning|error] <MyMod.csproj> [test-plan.yaml]
//
// smoke-plan.yaml, next to this script, is a strict plan to start from.
//
//   1. refuses unless VALHEIM_PATH names a game folder with BepInEx/plugins that is not a Steam library install
//      (--live allows one), and valheim-cli --status reports local_process=false (the game is stopped)
//   2. dotnet build -c Release (stops on a failed build)
//   3. with a plan: writes a temporary copy of VALHEIM_EXPECTATIONS whose pin for this mod is the md5 of the DLL just
//      built (every other line kept)
//   4. copies the built DLL (and its .pdb) into BepInEx/plugins and checks the installed copy is the build
//   5. launches the game and runs the plan with valheim-cli --expect-strict <derived pins> --test ... --launch
//      (without a plan: launches and waits until the console is ready)
//   6. counts the warnings and errors in BepInEx/LogOutput.log by level and source, and lists the most frequent
//      messages: a plan that passed while the mod logged errors is worth a look. --fail-on fails such a round
//
// The game must not be running: a running game keeps the old build (and on Windows locks the file).
//
// It needs a .NET 10 SDK (file-based apps). A global.json that pins an older SDK where you run it stops dotnet before
// the script starts; run it from a folder without that pin, with full paths.
//
// The SDK starts a file-based script in the script's own folder, not yours. Relative paths (the project, the plan,
// VALHEIM_PATH, VALHEIM_EXPECTATIONS, VALHEIM_CLI) are read from the folder your shell passes as PWD, and the tools run
// there; bash and zsh keep it current, PowerShell and cmd do not, so there give full paths ("$PWD\MyMod.csproj"),
// and the tools run in the project's folder.
//
// Exit code: the build's if it failed, otherwise the plan's (or the launch's); 1 no built or installed DLL, or
// --fail-on found a line at that level; 3 no VALHEIM_PATH, a Steam install without --live, no BepInEx/plugins, or no
// log to check with --fail-on; 4 usage, no project or plan file, pins file, or a relative path without PWD; 5 the
// game is running or its state is unknown.
//
// Environment:
//   VALHEIM_PATH      required: the game folder that contains BepInEx, a copy you own (valheim-cli launches it too)
//   VALHEIM_CLI       valheim-cli executable (default: valheim-cli on PATH)
//   VALHEIM_EXPECTATIONS  required pins file when running a test plan; strict mode. It must pin this mod exactly once;
//                     that pin is replaced by the fresh build's md5, never by what the game reports
//   VALHEIM_PLUGIN_KEY  the key that pins this mod in that file: its GUID, name or DLL name (default: the built DLL's
//                     name without .dll)
//   VALHEIM_CLI_PORT  valheimCLI port (default 5555)
//   CONFIGURATION     build configuration (default Release)
//   STOP_AFTER=1      quit the game after a plan that passed
//   PROGRESS          heartbeat interval, e.g. 30s (valheim-cli's default 15s; 0 disables)
//   STALL             end a plan's wait after this long without change (valheim-cli's default 120s; 0 disables)
//
// Replaces dev-loop.sh, dev-loop.ps1 and log-summary.sh (moved from ValheimCLI commit ee4cd23 on 28 Sep 2026).
// valheim-cli itself comes from a ValheimCLI release or build; see tools/dev-loop/README.md.
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

#if !DEV_LOOP_TESTS
return DevLoop.Run(args, Environment.GetEnvironmentVariable, DevLoop.RunProcess, Console.Out, Console.Error);
#endif

/// <summary>What a tool printed and how it ended.</summary>
public sealed record ToolRun(int ExitCode, string Output, string Errors);

/// <summary>
/// Runs <paramref name="tool"/> with <paramref name="arguments"/> in <paramref name="directory"/> (null: this process's);
/// with <paramref name="capture"/> it returns what the tool printed, otherwise the tool writes to this console. Throws
/// when the tool cannot start.
/// </summary>
public delegate ToolRun ToolRunner(string tool, IReadOnlyList<string> arguments, bool capture, string? directory);

public static class DevLoop
{
    public const string Usage = "usage: dotnet run --file tools/dev-loop/dev-loop.cs -- [--live] [--fail-on warning|error] <MyMod.csproj> [test-plan.yaml]";

    private const int TopMessages = 15;

    /// <summary>One round; any other failure (a copy, a hash, a tool that does not start) ends it with exit 1.</summary>
    public static int Run(string[] args, Func<string, string?> environment, ToolRunner tools, TextWriter output, TextWriter error)
    {
        try
        {
            return Round(args, environment, tools, output, error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            error.WriteLine("ERROR: " + exception.Message);
            return 1;
        }
    }

    private static int Round(string[] args, Func<string, string?> environment, ToolRunner tools, TextWriter output, TextWriter error)
    {
        string? Setting(string name, string? fallback = null) => environment(name) is { Length: > 0 } value ? value : fallback;
        int Fail(int code, string message)
        {
            error.WriteLine(message);
            return code;
        }

        bool live = false;
        string? failOn = null;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--live") live = true;
            else if (args[i] == "--fail-on" && i + 1 < args.Length && args[i + 1] is "warning" or "error") failOn = args[++i];
            else if (args[i].StartsWith("-", StringComparison.Ordinal)) return Fail(4, Usage);
            else positional.Add(args[i]);
        }
        if (positional.Count is < 1 or > 2) return Fail(4, Usage);

        // The caller's folder: the SDK runs this script in its own. Without it a relative path is refused, not read from
        // tools/dev-loop.
        string? caller = Setting("PWD") is { } pwd && Path.IsPathRooted(pwd) && Directory.Exists(pwd) ? pwd : null;
        var relative = new List<string>();
        string Full(string path)
        {
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            if (caller == null) relative.Add(path);
            return Path.GetFullPath(Path.Combine(caller ?? "", path));
        }
        // The tools run in the caller's folder, or the project's when the shell passed none: never in tools/dev-loop,
        // whose global.json and relative paths are not the mod's.
        string? toolDirectory = null;
        ToolRun Tool(string tool, IReadOnlyList<string> arguments, bool capture) => tools(tool, arguments, capture, toolDirectory);

        string project = Full(positional[0]);
        string? plan = positional.Count == 2 ? Full(positional[1]) : null;
        string cli = Setting("VALHEIM_CLI", "valheim-cli")!;
        if (cli.IndexOfAny(new[] { '/', '\\' }) >= 0) cli = Full(cli);
        string port = Setting("VALHEIM_CLI_PORT", "5555")!;
        string config = Setting("CONFIGURATION", "Release")!;
        string? pins = Setting("VALHEIM_EXPECTATIONS") is { } pinsSetting ? Full(pinsSetting) : null;
        string? gameSetting = Setting("VALHEIM_PATH");
        string game = gameSetting == null ? "" : Full(gameSetting);
        if (relative.Count != 0)
            return Fail(4, $"ERROR: relative path {string.Join(", ", relative)}: the SDK runs this script in its own folder and your shell passed no PWD; give full paths");
        toolDirectory = caller ?? Path.GetDirectoryName(project);

        // A stale PWD (PowerShell does not keep it current) shows here, as a path that is not what was meant.
        foreach (string file in plan == null ? new[] { project } : new[] { project, plan })
            if (!File.Exists(file)) return Fail(4, $"ERROR: no file at {file}");
        if (plan != null && (pins == null || !File.Exists(pins) || new FileInfo(pins).Length == 0))
            return Fail(4, "ERROR: set VALHEIM_EXPECTATIONS to a non-empty pins file before running a test plan");
        if (gameSetting == null)
            return Fail(3, "ERROR: set VALHEIM_PATH to the game folder to install into and launch: a copy you own, not your Steam install");
        if (!live && LooksLikeSteamInstall(game))
            return Fail(3, $"ERROR: {game} is a Steam library install; point VALHEIM_PATH at a copy you own, or pass --live to install into it");
        string plugins = Path.Combine(game, "BepInEx", "plugins");
        if (!Directory.Exists(plugins)) return Fail(3, $"ERROR: no BepInEx/plugins under {game} (set VALHEIM_PATH)");

        // --status exits nonzero when the plugin is unavailable, even when its independent local-process check found
        // the game. Inspect that evidence separately; an absent or unreadable status is not permission to deploy.
        string status;
        try { status = Tool(cli, new[] { "--port", port, "--status" }, true).Output; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { status = ""; }
        switch (GameStopped(status))
        {
            case false: return Fail(5, "ERROR: Valheim is running; quit it first so the new build is the one that loads");
            case null: return Fail(5, "ERROR: cannot establish that Valheim is stopped; check --status before deploying");
        }

        output.WriteLine($"== build ({config})");
        output.Flush();
        int built = Tool("dotnet", new[] { "build", project, "-c", config, "-nologo", "-v", "quiet" }, false).ExitCode;
        if (built != 0) return built;
        ToolRun target = Tool("dotnet", new[] { "msbuild", project, "-getProperty:TargetPath", "-p:Configuration=" + config }, true);
        if (target.ExitCode != 0) return Fail(target.ExitCode, target.Errors + target.Output);
        string dll = target.Output.Trim();
        if (dll.Length == 0 || !File.Exists(dll)) return Fail(1, $"ERROR: the build reported {dll}, which does not exist");
        string dllName = Path.GetFileName(dll);
        string md5 = Md5(dll);

        string? derived = null;
        try
        {
            if (plan != null)
            {
                // Pin the artifact this script deploys, not whatever the game reports.
                string key = Setting("VALHEIM_PLUGIN_KEY", Path.GetFileNameWithoutExtension(dll))!;
                string? text = DerivePins(File.ReadAllLines(pins!), key, md5);
                if (text == null)
                    return Fail(4, $"ERROR: {pins} must pin {key} exactly once; set VALHEIM_PLUGIN_KEY to the GUID, name or DLL name it uses");
                derived = Path.Combine(Path.GetTempPath(), "dev-loop-pins-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(derived, text);
                output.WriteLine($"== pins: {key}={md5} (derived copy of {pins})");
            }

            output.WriteLine($"== install {dllName} -> {plugins}");
            string installed = Path.Combine(plugins, dllName);
            File.Copy(dll, installed, overwrite: true);
            string pdb = Path.ChangeExtension(dll, ".pdb");
            if (File.Exists(pdb)) File.Copy(pdb, Path.Combine(plugins, Path.GetFileName(pdb)), overwrite: true);
            if (Md5(installed) != md5) return Fail(1, $"ERROR: the installed {dllName} differs from the build");

            var run = new List<string> { "--port", port };
            if (plan != null)
            {
                run.AddRange(new[] { "--expect-strict", derived!, "--test", plan, "--launch" });
                if (Setting("STOP_AFTER") == "1") run.Add("--stop-after");
            }
            else run.AddRange(new[] { "--launch", "--timeout", "300s" });
            if (Setting("PROGRESS") is { } progress) run.AddRange(new[] { "--progress", progress });
            if (Setting("STALL") is { } stall) run.AddRange(new[] { "--stall", stall });

            output.WriteLine(plan != null ? $"== launch and run {plan}" : "== launch");
            output.Flush();
            int result = Tool(cli, run, false).ExitCode;

            output.WriteLine("== log");
            string log = Path.Combine(game, "BepInEx", "LogOutput.log");
            if (!File.Exists(log))
            {
                error.WriteLine($"ERROR: no log at {log}");
                return result == 0 && failOn != null ? 3 : result;
            }
            LogSummary summary = SummariseLog(log);
            summary.WriteTo(output, TopMessages);
            int bad = summary.Count("Fatal") + summary.Count("Error") + (failOn == "warning" ? summary.Count("Warning") : 0);
            if (result == 0 && failOn != null && bad > 0)
                return Fail(1, $"ERROR: the log has {bad} line(s) at {failOn} level or worse (--fail-on {failOn})");
            return result;
        }
        finally
        {
            // Best effort: a scanner holding the file must not replace the round's exit code.
            try { if (derived != null) File.Delete(derived); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { error.WriteLine($"WARNING: could not remove {derived}: {exception.Message}"); }
        }
    }

    /// <summary>
    /// Whether <c>valheim-cli --status</c> output says the game is stopped: false when any line reports a local game
    /// process, true when one reports none, null when it says neither (no answer is not permission to deploy).
    /// </summary>
    public static bool? GameStopped(string status)
    {
        if (Regex.IsMatch(status, @"(^|\s)local_process=true(\s|$)")) return false;
        if (Regex.IsMatch(status, @"(^|\s)local_process=false(\s|$)")) return true;
        return null;
    }

    /// <summary>
    /// The pins file with the one line whose key is <paramref name="key"/> (case-insensitive, spaces around it ignored)
    /// replaced by <c>key=md5</c>, or null unless exactly one line pins that key.
    /// </summary>
    public static string? DerivePins(IEnumerable<string> lines, string key, string md5)
    {
        var result = new StringBuilder();
        int replaced = 0;
        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq >= 0 && string.Equals(line[..eq].Trim(' ', '\t'), key, StringComparison.OrdinalIgnoreCase))
            {
                result.Append(key).Append('=').Append(md5).Append("   # derived by dev-loop.cs from the build it deployed\n");
                replaced++;
            }
            else result.Append(line).Append('\n');
        }
        return replaced == 1 ? result.ToString() : null;
    }

    /// <summary>
    /// Whether <paramref name="game"/>, as written or with every link or junction along it followed, sits in a Steam
    /// library's <c>steamapps/common</c>: the install Steam updates and every other launch of the game uses.
    /// </summary>
    public static bool LooksLikeSteamInstall(string game)
    {
        static bool InLibrary(string path)
        {
            string[] parts = path.Split('/', '\\');
            for (int i = 0; i + 1 < parts.Length; i++)
                if (parts[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("common", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        if (InLibrary(game)) return true;
        try { return InLibrary(WithLinksFollowed(Path.GetFullPath(game))); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary><paramref name="path"/> with the first link or junction along it replaced by its target, until none is left.</summary>
    private static string WithLinksFollowed(string path)
    {
        for (int hop = 0; hop < 40; hop++)
        {
            var below = new List<string>();
            string? followed = null;
            for (DirectoryInfo? directory = new(path); directory != null; directory = directory.Parent)
            {
                if (directory.Exists && directory.LinkTarget != null && directory.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    below.Reverse();
                    followed = Path.Combine(below.Prepend(target.FullName).ToArray());
                    break;
                }
                below.Add(directory.Name);
            }
            if (followed == null) return path;
            path = followed;
        }
        return path;
    }

    /// <summary>The warning, error and fatal lines of one BepInEx log, by level and source and by masked message.</summary>
    public sealed record LogSummary(IReadOnlyDictionary<(string Level, string Source), int> Rows, IReadOnlyDictionary<string, int> Messages)
    {
        public int Count(string level) => Rows.Where(row => row.Key.Level == level).Sum(row => row.Value);

        public void WriteTo(TextWriter output, int top)
        {
            output.WriteLine($"{"LEVEL",-8} {"COUNT",6}  SOURCE");
            foreach (var row in Rows.OrderByDescending(row => row.Value).ThenBy(row => row.Key.Level, StringComparer.Ordinal).ThenBy(row => row.Key.Source, StringComparer.Ordinal))
                output.WriteLine($"{row.Key.Level,-8} {row.Value,6}  {row.Key.Source}");
            output.WriteLine();
            output.WriteLine($"total: fatal={Count("Fatal")} error={Count("Error")} warning={Count("Warning")}");
            if (Messages.Count == 0) return;
            output.WriteLine();
            output.WriteLine("most frequent messages (numbers shown as #):");
            foreach (var message in Messages.OrderByDescending(message => message.Value).ThenBy(message => message.Key, StringComparer.Ordinal).Take(top))
                output.WriteLine($"{message.Value,6}x  {message.Key}");
        }
    }

    /// <summary>
    /// Counts a BepInEx log's <c>[Warning  :   MyMod] message</c> lines (and Error and Fatal) by level and source, and by
    /// message with its numbers masked, so "zone 3,-4" and "zone 5,2" count as one. Stack-trace lines carry no prefix
    /// and are not counted. BepInEx rewrites the log at every game start, so it holds exactly one run.
    /// </summary>
    public static LogSummary SummariseLog(string log)
    {
        var rows = new Dictionary<(string, string), int>();
        var messages = new Dictionary<string, int>(StringComparer.Ordinal);
        // The game may still be writing the log.
        using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            Match match = Regex.Match(line, @"^\[(Warning|Error|Fatal) *: *([^\]]*)\] (.*)$");
            if (!match.Success) continue;
            string level = match.Groups[1].Value, source = match.Groups[2].Value.TrimEnd();
            rows[(level, source)] = rows.GetValueOrDefault((level, source)) + 1;
            string text = Regex.Replace(match.Groups[3].Value, @"-?[0-9]+(\.[0-9]+)?", "#");
            if (text.Length > 140) text = text[..137] + "...";
            string message = $"[{level}: {source}] {text}";
            messages[message] = messages.GetValueOrDefault(message) + 1;
        }
        return new LogSummary(rows, messages);
    }

    public static string Md5(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>The <see cref="ToolRunner"/> that starts real processes.</summary>
    public static ToolRun RunProcess(string tool, IReadOnlyList<string> arguments, bool capture, string? directory)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = capture, RedirectStandardError = capture };
        if (directory != null) start.WorkingDirectory = directory;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // Ctrl+C reaches the tool too; this process outlives it, so the round still cleans up and reports the tool's exit.
        ConsoleCancelEventHandler outlive = (_, cancel) => cancel.Cancel = true;
        Console.CancelKeyPress += outlive;
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException(tool + " did not start");
            if (!capture)
            {
                process.WaitForExit();
                return new ToolRun(process.ExitCode, "", "");
            }
            Task<string> errors = process.StandardError.ReadToEndAsync();
            string text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return new ToolRun(process.ExitCode, text, errors.Result);
        }
        finally
        {
            Console.CancelKeyPress -= outlive;
        }
    }
}
