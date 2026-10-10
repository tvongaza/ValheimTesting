using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>An OS-owned server hold that survives the command which started it.</summary>
internal static class DetachedSession
{
    internal const string Usage = "valheim-test detach server-load --server DIR --mod DLL --server-only --output NEW_DIR [assertion and timing options] | " +
        "valheim-test detach status | valheim-test detach finish --run ID | valheim-test detach recover --token TOKEN";
    private const string ReadyVariable = "VALHEIM_TEST_DETACH_READY";
    private const string WindowsTaskPrefix = "ValheimTesting-detached-";
    private sealed record Ready(string Run, string Evidence, int Pid, string Started);
    private sealed record StartedRun(string Run, int Pid, string Started);
    internal sealed record Launch(string Token, string Label, string Output, string ReadyFile, string StandardOut,
        string StandardError, string ToolDirectory, string InputDirectory, string Plist, string Domain, string? Run = null,
        int StarterPid = 0, string StarterStarted = "");
    private static string Root => Path.Combine(CliBundle.DataRoot, "runs", "detached");
    [DllImport("libc")] private static extern uint geteuid();

    internal static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        { error.WriteLine("REFUSED: detached sessions currently require a local macOS launchd or Windows Task Scheduler owner; foreground --hold works on other hosts."); return 3; }
        if (args is ["status"]) return Status(output, error);
        if (args is ["finish", "--run", var run]) return await FinishAsync(run, output, error).ConfigureAwait(false);
        if (args is ["recover", "--token", var recoveryToken]) return await RecoverAsync(recoveryToken, output, error).ConfigureAwait(false);
        if (args is not ["server-load", .. var serverArgs])
        { error.WriteLine("Usage: " + Usage); return 2; }
        if (!TryServerArgs(serverArgs, out string selectedOutput, out string reason))
        { error.WriteLine("Usage: " + Usage + "\nREFUSED: " + reason); return 2; }
        try
        {
            // The runner itself needs the desktop token on Windows. Otherwise its nested owned
            // server task would be created from a non-elevated session-0 S4U token and refused.
            if (OperatingSystem.IsWindows())
                await InteractiveClient.RequireWindowsDesktopSessionAsync(new LocalGameHost("this machine", HostShell.WindowsPowerShell))
                    .ConfigureAwait(false);
            Directory.CreateDirectory(Root);
            string token = Guid.NewGuid().ToString("N");
            string label = OperatingSystem.IsWindows() ? WindowsTaskPrefix + token : "tv.valheimtesting.detached." + token;
            string prefix = Path.Combine(Root, token);
            using var starter = Process.GetCurrentProcess();
            var launch = new Launch(token, label, selectedOutput, prefix + ".ready.json", prefix + ".out.log", prefix + ".err.log",
                prefix + ".tool", prefix + ".inputs", prefix + (OperatingSystem.IsWindows() ? ".ps1" : ".plist"),
                OperatingSystem.IsWindows() ? "windows/task" : "gui/" + geteuid().ToString(CultureInfo.InvariantCulture),
                StarterPid: starter.Id, StarterStarted: starter.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture));
            string record = prefix + ".launch.json";
            // This intent precedes even the tool snapshot: an interrupted copy is visible and
            // recoverable by token, provided the exact starter and OS job are both gone.
            string pending = record + ".new";
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, launch);
            File.Move(pending, record);
            try
            {
                StageTool(launch.ToolDirectory);
                string[] stagedArgs = StageMods(serverArgs, launch.InputDirectory);
                var command = OwnCommand(launch.ToolDirectory);
                // An OS service, rather than a child of this terminal or SSH session, owns the runner and its host lock.
                var childEnvironment = new List<string> { ReadyVariable + "=" + launch.ReadyFile,
                    "PATH=" + Environment.GetEnvironmentVariable("PATH") };
                foreach (string key in new[] { "DOTNET_ROOT", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH" })
                    if (Environment.GetEnvironmentVariable(key) is { } value) childEnvironment.Add(key + "=" + value);
                string[] program = [command.Program, .. command.Prefix, "server-load", .. stagedArgs, "--hold"];
                if (OperatingSystem.IsWindows())
                    File.WriteAllText(launch.Plist, WindowsLauncher(launch, childEnvironment, program), new System.Text.UTF8Encoding(true));
                else File.WriteAllText(launch.Plist, Plist(launch, ["/usr/bin/env", .. childEnvironment, .. program]));
            }
            catch
            {
                // Nothing was submitted to the OS yet; these are exclusively this token's files.
                if (Directory.Exists(launch.ToolDirectory)) Directory.Delete(launch.ToolDirectory, recursive: true);
                if (Directory.Exists(launch.InputDirectory)) Directory.Delete(launch.InputDirectory, recursive: true);
                File.Delete(launch.Plist); File.Delete(record);
                throw;
            }
            int submitted;
            try
            {
                submitted = OperatingSystem.IsWindows()
                    ? await RegisterWindowsTaskAsync(launch).ConfigureAwait(false)
                    : await RegisterMacJobAsync(launch).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                error.WriteLine("REFUSED: " + failure.Message + " Inspect env status, then use detach recover --token " + token + ".");
                return 3;
            }
            if (submitted != 0)
            {
                error.WriteLine("REFUSED: the OS did not accept the detached server owner (exit " + submitted +
                    "). The exact owner record remains at " + record + "; inspect it, then use detach recover --token " + token + ".");
                return 3;
            }
            Ready ready;
            try { ready = await WaitReadyAsync(launch).ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or TimeoutException or InvalidDataException)
            {
                error.WriteLine("REFUSED: " + failure.Message + " Private startup logs: " + launch.StandardOut + " and " + launch.StandardError +
                    ". Check valheim-test env status, then use detach recover --token " + token + ".");
                return 3; // It may own a game. Never remove or kill it merely because the handshake timed out.
            }
            if (!ExactOwner(ready))
            {
                error.WriteLine("REFUSED: the held runner exited or changed identity before detach returned; inspect env status and " + record);
                return 3;
            }
            File.WriteAllText(record + ".new", JsonSerializer.Serialize(launch with { Run = ready.Run }));
            File.Move(record + ".new", record, overwrite: true);
            output.WriteLine("DETACHED run " + ready.Run + "; evidence " + ready.Evidence);
            output.WriteLine("Finish and verify cleanup: valheim-test detach finish --run " + ready.Run);
            return 0;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { error.WriteLine("REFUSED: " + failure.Message); return 3; }
    }

    // The launchd job has no caller working directory. Refuse relative paths and implicit mod discovery before it starts.
    internal static bool TryServerArgs(string[] args, out string output, out string error)
    {
        output = ""; error = "";
        if (args.Contains("--hold") || args.Contains("--preflight-only") || args.Contains("--bake-fixture"))
        { error = "detach owns the hold; preflight-only and fixture export cannot be held."; return false; }
        if (args.Count(arg => arg == "--output") != 1 || args.Count(arg => arg == "--server") != 1 ||
            !args.Contains("--server-only") || !args.Contains("--mod"))
        { error = "give one absolute --output and --server, --server-only, and at least one absolute --mod."; return false; }
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" or "--plugin-file" or "--plugin-dir" or "--search-root" or "--adapter" or
                "--cli-manifest" or "--cli-files" or "--loader-package" or "--world-fixture" or "--inventory")
            { error = "detached server input staging currently supports --mod, --server and --output only; use foreground --hold for " + args[i] + "."; return false; }
            if (args[i] is not ("--output" or "--server" or "--mod")) continue;
            if (++i >= args.Length || !Path.IsPathFullyQualified(args[i]))
            { error = args[i - 1] + " needs an absolute path in a detached run."; return false; }
            if (args[i - 1] == "--output") output = Path.GetFullPath(args[i]);
        }
        if (output.Length == 0 || Directory.Exists(output) || File.Exists(output))
        { error = "--output must name a new absolute directory."; return false; }
        return true;
    }

    // A launchd service cannot rely on macOS granting access to a user's Documents checkout. Copy
    // each selected mod's directory so its adjacent managed dependencies resolve from the same snapshot.
    internal static string[] StageMods(string[] args, string target)
    {
        var staged = (string[])args.Clone();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        Directory.CreateDirectory(target);
        try
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "--mod") continue;
                string file = args[++i], directory = Path.GetDirectoryName(file)!;
                if (!File.Exists(file)) throw new FileNotFoundException("A detached mod does not exist: " + file, file);
                if (!sources.TryGetValue(directory, out string? copy))
                {
                    copy = Path.Combine(target, sources.Count.ToString(CultureInfo.InvariantCulture));
                    var manifest = WorldFixture.Manifest(directory);
                    if (manifest.Count > 10000 || manifest.Keys.Sum(relative => new FileInfo(Path.Combine(directory, relative)).Length) > 512L << 20)
                        throw new InvalidDataException("A detached mod directory is too large to stage (10,000 files or 512 MiB): " + directory);
                    Directory.CreateDirectory(copy);
                    foreach (var (relative, hash) in manifest)
                    {
                        string destination = Path.Combine(copy, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(Path.Combine(directory, relative), destination);
                        if (!FileHash.Sha256(destination).Equals(hash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("The detached mod changed during staging: " + relative);
                    }
                    WorldFixture.Verify(directory, manifest);
                    sources.Add(directory, copy);
                }
                staged[i] = Path.Combine(copy, Path.GetFileName(file));
            }
            return staged;
        }
        catch { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); throw; }
    }

    // Called by ForegroundHold only after the exact run's marker has been created.
    internal static void SignalHeld(string run, string evidence, int pid, string started,
        string? readyOverride = null, string? rootOverride = null)
    {
        string? path = readyOverride ?? Environment.GetEnvironmentVariable(ReadyVariable);
        if (path == null) return;
        if (!Path.IsPathFullyQualified(path) || !Path.GetDirectoryName(path)!.Equals(rootOverride ?? Root, StringComparison.Ordinal))
            throw new InvalidOperationException("The detached readiness path is outside this machine's run data.");
        string temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, new Ready(run, evidence, pid, started));
        File.Move(temporary, path);
    }

    // The journal adopts this run ID immediately afterwards. A runner that dies during setup has
    // no held marker yet, but the next shell can recover this exact run without guessing from text.
    internal static void SignalRun(string run)
    {
        string? ready = Environment.GetEnvironmentVariable(ReadyVariable);
        if (ready == null) return;
        SignalRunAt(run, ready, Root);
    }

    internal static void SignalRunAt(string run, string ready, string root)
    {
        if (!RunJournal.SafeName(run) || !Path.IsPathFullyQualified(ready) ||
            !Path.GetDirectoryName(ready)!.Equals(root, StringComparison.Ordinal) ||
            !ready.EndsWith(".ready.json", StringComparison.Ordinal))
            throw new InvalidOperationException("The detached run ID or owner path is invalid.");
        string path = ready[..^".ready.json".Length] + ".run.json";
        using var process = Process.GetCurrentProcess();
        var started = new StartedRun(run, process.Id, process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture));
        using (var stream = new FileStream(path + ".new", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, started);
        File.Move(path + ".new", path);
    }

    private static void StageTool(string target)
    {
        string assembly = Assembly.GetEntryAssembly()?.Location ?? "";
        if (!Path.IsPathFullyQualified(assembly) || !File.Exists(assembly))
            throw new InvalidOperationException("The valheim-test DLL path is unavailable for staging.");
        string source = Path.GetDirectoryName(assembly)!;
        string temporary = target + ".new";
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The runner build contains a link: " + file);
                string hash = FileHash.Sha256(file), copy = Path.Combine(temporary, Path.GetFileName(file));
                File.Copy(file, copy);
                if (FileHash.Sha256(copy) != hash || FileHash.Sha256(file) != hash)
                    throw new IOException("The runner changed while staging its detached copy: " + file);
            }
            if (!File.Exists(Path.Combine(temporary, Path.GetFileName(assembly))) ||
                !File.Exists(Path.Combine(temporary, Path.GetFileNameWithoutExtension(assembly) + ".runtimeconfig.json")))
                throw new InvalidDataException("The runner build has no complete .NET runtime configuration.");
            Directory.Move(temporary, target);
        }
        catch { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); throw; }
    }

    private static (string Program, string[] Prefix) OwnCommand(string toolDirectory)
    {
        string process = Environment.ProcessPath ?? throw new InvalidOperationException("The valheim-test executable path is unavailable.");
        string assembly = Assembly.GetEntryAssembly()?.Location ?? "";
        if (!Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (Path.Combine(toolDirectory, Path.GetFileName(process)), []);
        return (process, [Path.Combine(toolDirectory, Path.GetFileName(assembly))]);
    }

    private static async Task<int> RunCommand(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + start.FileName);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task<int> RegisterMacJobAsync(Launch launch)
    {
        var bootstrap = new ProcessStartInfo("launchctl") { UseShellExecute = false };
        bootstrap.ArgumentList.Add("bootstrap"); bootstrap.ArgumentList.Add(launch.Domain); bootstrap.ArgumentList.Add(launch.Plist);
        return await RunCommand(bootstrap).ConfigureAwait(false);
    }

    private static string Ps(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // The scheduled task owns the PowerShell wrapper, which waits for the runner. It has no clock trigger:
    // registration is followed by exactly one demand start, and finish removes it after journal recovery.
    internal static string WindowsLauncher(Launch launch, IReadOnlyList<string> environment, IReadOnlyList<string> program)
    {
        var script = new System.Text.StringBuilder("$ErrorActionPreference = 'Stop'\n");
        foreach (string item in environment)
        {
            int equals = item.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !item[..equals].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
                throw new ArgumentException("Invalid detached environment variable.");
            script.Append("$env:").Append(item[..equals]).Append(" = ").Append(Ps(item[(equals + 1)..])).Append('\n');
        }
        script.Append("& ").Append(Ps(program[0]));
        foreach (string argument in program.Skip(1)) script.Append(' ').Append(Ps(argument));
        script.Append(" 1>> ").Append(Ps(launch.StandardOut)).Append(" 2>> ").Append(Ps(launch.StandardError)).Append('\n');
        script.Append("exit $LASTEXITCODE\n");
        return script.ToString();
    }

    internal static string WindowsRegistration(Launch launch) => $$"""
        $ErrorActionPreference = 'Stop'
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $folder = $service.GetFolder('\')
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $logonType = 3 # the one preflighted desktop, including when this command came from SSH/session 0
        $definition = $service.NewTask(0)
        $definition.RegistrationInfo.Description = 'ValheimTesting: one owned detached server run; removed after finish.'
        $definition.Principal.UserId = $identity.Name
        $definition.Principal.LogonType = $logonType
        $definition.Principal.RunLevel = 0
        $definition.Settings.Hidden = $true
        $definition.Settings.Enabled = $true
        $definition.Settings.AllowDemandStart = $true
        $definition.Settings.DisallowStartIfOnBatteries = $false
        $definition.Settings.StopIfGoingOnBatteries = $false
        $definition.Settings.ExecutionTimeLimit = 'PT0S'
        $action = $definition.Actions.Create(0)
        $action.Path = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
        $action.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + {{Ps(launch.Plist)}} + '"'
        $action.WorkingDirectory = {{Ps(launch.ToolDirectory)}}
        $task = $folder.RegisterTaskDefinition({{Ps(launch.Label)}}, $definition, 1, $identity.Name, $null, $logonType)
        [void]$task.Run($null)
        """;

    private static async Task<int> RegisterWindowsTaskAsync(Launch launch)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(WindowsRegistration(launch))));
        using var process = Process.Start(start) ?? throw new IOException("Could not start Windows Task Scheduler registration.");
        string diagnostic = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(diagnostic))
            throw new InvalidOperationException("Windows detached task registration failed: " + diagnostic.Trim());
        return process.ExitCode;
    }

    private static async Task<string> WindowsTaskStateAsync(Launch launch)
    {
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $service = New-Object -ComObject Schedule.Service
            $service.Connect()
            try { $task = $service.GetFolder('\').GetTask({{Ps(launch.Label)}}) }
            catch [System.Runtime.InteropServices.COMException] {
                if ($_.Exception.HResult -eq -2147024894) { 'missing'; exit 0 }
                throw
            }
            if ($task.State -in @(2, 4) -or [int64]$task.LastTaskResult -in @(0x41301, 0x41325)) {
                'running'
            } elseif ([int64]$task.LastTaskResult -eq 0x41303) { 'not-started' }
            else { 'stopped ' + $task.LastTaskResult }
            """;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new IOException("Could not inspect the Windows detached task.");
        string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException("The Windows detached task could not be inspected: " + stderr.Trim());
        return stdout.Trim();
    }

    private static async Task<int> RemoveWindowsTaskAsync(Launch launch)
    {
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $service = New-Object -ComObject Schedule.Service
            $service.Connect()
            $service.GetFolder('\').DeleteTask({{Ps(launch.Label)}}, 0)
            """;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        return await RunCommand(start).ConfigureAwait(false);
    }

    internal static string Plist(Launch launch, string[] program)
    {
        var plist = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "Label"), new XElement("string", launch.Label),
                new XElement("key", "ProgramArguments"), new XElement("array", program.Select(arg => new XElement("string", arg))),
                new XElement("key", "RunAtLoad"), new XElement("true"),
                new XElement("key", "KeepAlive"), new XElement("false"),
                new XElement("key", "StandardOutPath"), new XElement("string", launch.StandardOut),
                new XElement("key", "StandardErrorPath"), new XElement("string", launch.StandardError))));
        return plist.ToString() + "\n";
    }

    private static async Task<Ready> WaitReadyAsync(Launch launch)
    {
        using var watcher = new FileSystemWatcher(Root, Path.GetFileName(launch.ReadyFile))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FileSystemEventHandler onChange = (_, _) => changed.TrySetResult();
        RenamedEventHandler onRename = (_, _) => changed.TrySetResult();
        watcher.Created += onChange; watcher.Changed += onChange; watcher.Renamed += onRename;
        try
        {
            DateTime started = DateTime.UtcNow;
            DateTime deadline = started + TimeSpan.FromMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(launch.ReadyFile))
                {
                    var ready = JsonSerializer.Deserialize<Ready>(File.ReadAllText(launch.ReadyFile))
                        ?? throw new InvalidDataException("The detached readiness record is empty.");
                    if (!RunJournal.SafeName(ready.Run) || ready.Pid <= 0 || !Path.IsPathFullyQualified(ready.Evidence))
                        throw new InvalidDataException("The detached readiness record is invalid.");
                    var startedRun = JsonSerializer.Deserialize<StartedRun>(File.ReadAllText(RunFile(launch)))
                        ?? throw new InvalidDataException("The detached run-start record is empty.");
                    if (startedRun.Run != ready.Run || startedRun.Pid != ready.Pid || startedRun.Started != ready.Started)
                        throw new InvalidDataException("The detached held owner differs from the runner that started the run.");
                    return ready;
                }
                if (OperatingSystem.IsWindows())
                {
                    string state = await WindowsTaskStateAsync(launch).ConfigureAwait(false);
                    if (state == "not-started" && DateTime.UtcNow - started > TimeSpan.FromSeconds(30))
                        throw new IOException("The Windows detached task did not start within thirty seconds.");
                    if (state.StartsWith("stopped", StringComparison.Ordinal))
                        throw new IOException("The Windows detached task ended before the game entered its held state (" + state + ").");
                }
                else
                {
                    var probe = new ProcessStartInfo("launchctl") { UseShellExecute = false, RedirectStandardOutput = true };
                    probe.ArgumentList.Add("list"); probe.ArgumentList.Add(launch.Label);
                    using var process = Process.Start(probe) ?? throw new IOException("Could not inspect the launchd job.");
                    string listing = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    if (process.ExitCode != 0 || listing.Contains("\"LastExitStatus\"", StringComparison.Ordinal) &&
                        !listing.Contains("\"PID\"", StringComparison.Ordinal))
                        throw new IOException("The launchd runner ended before the game entered its held state.");
                }
                await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                if (changed.Task.IsCompleted) changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            throw new TimeoutException("The detached runner did not reach its held state within ten minutes.");
        }
        finally { watcher.Created -= onChange; watcher.Changed -= onChange; watcher.Renamed -= onRename; }
    }

    private static bool ExactOwner(Ready ready)
        => ExactOwner(ready.Pid, ready.Started);

    private static bool ExactOwner(int pid, string started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) == started;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static IEnumerable<(string File, Launch Record)> Records()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (string file in Directory.EnumerateFiles(Root, "*.launch.json"))
        {
            Launch record = JsonSerializer.Deserialize<Launch>(File.ReadAllText(file))
                ?? throw new InvalidDataException("The detached record is empty: " + file);
            if (record.Token is not { Length: 32 } || !record.Token.All(Uri.IsHexDigit))
                throw new InvalidDataException("The detached record has an invalid token: " + file);
            string prefix = Path.Combine(Root, record.Token);
            if (record.Label != (OperatingSystem.IsWindows() ? WindowsTaskPrefix : "tv.valheimtesting.detached.") + record.Token ||
                file != prefix + ".launch.json" || record.ReadyFile != prefix + ".ready.json" ||
                record.StandardOut != prefix + ".out.log" || record.StandardError != prefix + ".err.log" ||
                record.ToolDirectory != prefix + ".tool" || record.InputDirectory != prefix + ".inputs" ||
                record.Plist != prefix + (OperatingSystem.IsWindows() ? ".ps1" : ".plist") ||
                record.Domain != (OperatingSystem.IsWindows() ? "windows/task" : "gui/" + geteuid().ToString(CultureInfo.InvariantCulture)) ||
                record.Output is null || !Path.IsPathFullyQualified(record.Output) ||
                record.StarterPid <= 0 || record.StarterStarted.Length == 0)
                throw new InvalidDataException("The detached record is not one this machine created: " + file);
            yield return (file, record);
        }
    }

    private static string? RecordedRun(Launch record)
    {
        if (record.Run != null) return record.Run;
        if (File.Exists(record.ReadyFile)) return JsonSerializer.Deserialize<Ready>(File.ReadAllText(record.ReadyFile))?.Run;
        string path = Path.Combine(Root, record.Token + ".run.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<StartedRun>(File.ReadAllText(path))?.Run : null;
    }

    private static int Status(TextWriter output, TextWriter error)
    {
        try { foreach (var (_, record) in Records())
        {
            Ready? ready = null;
            if (File.Exists(record.ReadyFile))
                try { ready = JsonSerializer.Deserialize<Ready>(File.ReadAllText(record.ReadyFile)); }
                catch (Exception failure) when (failure is IOException or JsonException) { }
            StartedRun? started = File.Exists(RunFile(record))
                ? JsonSerializer.Deserialize<StartedRun>(File.ReadAllText(RunFile(record))) : null;
            string state = ready != null && ExactOwner(ready) ? "LIVE " :
                started != null && ExactOwner(started.Pid, started.Started) ? "STARTING " : "CHECK ";
            output.WriteLine(state +
                (RecordedRun(record) ?? record.Token) + ": output " + record.Output + "; OS owner " + record.Label);
        } }
        catch (Exception failure) when (failure is IOException or InvalidDataException or JsonException)
        { error.WriteLine("REFUSED: " + failure.Message); return 3; }
        output.WriteLine("Use valheim-test env status for owned game and recovery state.");
        return 0;
    }

    private static async Task<int> FinishAsync(string run, TextWriter output, TextWriter error)
    {
        if (!RunJournal.SafeName(run)) { error.WriteLine("Usage: " + Usage); return 2; }
        try
        {
            var matches = Records().Where(item => RecordedRun(item.Record) == run).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Expected exactly one detached record for run " + run + ". Use detach status and env status.");
            var (file, record) = matches[0];
            Ready? ready = File.Exists(record.ReadyFile)
                ? JsonSerializer.Deserialize<Ready>(File.ReadAllText(record.ReadyFile)) : null;
            StartedRun? started = File.Exists(RunFile(record))
                ? JsonSerializer.Deserialize<StartedRun>(File.ReadAllText(RunFile(record))) : null;
            if (ready?.Run != run && started?.Run != run) throw new InvalidDataException("The detached run identity changed.");
            bool live = ready != null ? ExactOwner(ready) : started != null && ExactOwner(started.Pid, started.Started);
            int ownerExit = 0;
            if (live)
            {
                if (ready == null) throw new InvalidOperationException("The detached runner is still setting up; it has no held marker to finish yet.");
                ForegroundHold.Request(run);
                using var owner = Process.GetProcessById(ready.Pid);
                using var wait = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                await owner.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                ownerExit = owner.ExitCode;
            }
            // An owner that died unexpectedly still goes through the existing exact-process recovery.
            // If its game lives on, recovery refuses and the launchd record remains for inspection.
            if (Directory.Exists(Path.Combine(RunJournal.LocalDirectory, run)))
            {
                int clean = await EnvCommand.RunAsync(["recover", "--run", run], output, error).ConfigureAwait(false);
                if (clean != 0) throw new InvalidOperationException("The run still owns state; inspect env status before removing its OS owner.");
            }
            bool copiedLogs = Directory.Exists(record.Output);
            if (copiedLogs)
            {
                CopyLog(record.StandardOut, Path.Combine(record.Output, "detached-stdout.log"));
                CopyLog(record.StandardError, Path.Combine(record.Output, "detached-stderr.log"));
            }
            bool ownerMissing = OperatingSystem.IsWindows()
                ? await WindowsTaskStateAsync(record).ConfigureAwait(false) == "missing"
                : await MacJobMissingAsync(record).ConfigureAwait(false);
            if (!ownerMissing && await RemoveOwnerAsync(record).ConfigureAwait(false) != 0)
                throw new IOException("Could not remove the completed detached owner " + record.Label);
            if (Directory.Exists(record.ToolDirectory)) Directory.Delete(record.ToolDirectory, recursive: true);
            if (Directory.Exists(record.InputDirectory)) Directory.Delete(record.InputDirectory, recursive: true);
            File.Delete(record.ReadyFile);
            if (copiedLogs) { File.Delete(record.StandardOut); File.Delete(record.StandardError); }
            File.Delete(RunFile(record)); File.Delete(RunFile(record) + ".new"); File.Delete(record.Plist); File.Delete(file);
            if (!copiedLogs) output.WriteLine("Private startup logs kept at " + record.StandardOut + " and " + record.StandardError);
            output.WriteLine("DETACHED FINISHED run " + run + "; owned state verified clear.");
            return ownerExit == 0 ? 0 : 1;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception)
        { error.WriteLine("REFUSED: " + failure.Message); return 3; }
    }

    private static string RunFile(Launch record) => Path.Combine(Root, record.Token + ".run.json");

    private static async Task<int> RemoveOwnerAsync(Launch record)
    {
        if (OperatingSystem.IsWindows()) return await RemoveWindowsTaskAsync(record).ConfigureAwait(false);
        var remove = new ProcessStartInfo("launchctl") { UseShellExecute = false };
        remove.ArgumentList.Add("bootout"); remove.ArgumentList.Add(record.Domain + "/" + record.Label);
        return await RunCommand(remove).ConfigureAwait(false);
    }

    private static async Task<int> RecoverAsync(string token, TextWriter output, TextWriter error)
    {
        if (token is not { Length: 32 } || !token.All(Uri.IsHexDigit))
        { error.WriteLine("Usage: " + Usage); return 2; }
        try
        {
            var matches = Records().Where(item => item.Record.Token == token).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("No exact detached owner record for token " + token + ".");
            var (file, record) = matches[0];
            string? run = RecordedRun(record);
            if (run != null) return await FinishAsync(run, output, error).ConfigureAwait(false);
            if (ExactOwner(record.StarterPid, record.StarterStarted))
                throw new InvalidOperationException("The detached starter is still staging or registering this task; retry after it exits.");
            // A run ID is recorded before its journal or game can start. A missing ID is safe to
            // retire only after the OS task has stopped; never kill a task merely to clean files.
            if (OperatingSystem.IsWindows())
            {
                if (await WindowsTaskStateAsync(record).ConfigureAwait(false) == "running")
                    throw new InvalidOperationException("The Windows detached task is still running; inspect its private logs first.");
            }
            else
            {
                var probe = new ProcessStartInfo("launchctl") { UseShellExecute = false, RedirectStandardOutput = true };
                probe.ArgumentList.Add("list"); probe.ArgumentList.Add(record.Label);
                using var process = Process.Start(probe) ?? throw new IOException("Could not inspect the launchd job.");
                string listing = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode == 0 && listing.Contains("\"PID\"", StringComparison.Ordinal))
                    throw new InvalidOperationException("The launchd owner is running or cannot be inspected; inspect its private logs first.");
            }
            bool ownerMissing = OperatingSystem.IsWindows()
                ? await WindowsTaskStateAsync(record).ConfigureAwait(false) == "missing"
                : await MacJobMissingAsync(record).ConfigureAwait(false);
            if (!ownerMissing && await RemoveOwnerAsync(record).ConfigureAwait(false) != 0)
                throw new IOException("Could not remove the stopped detached owner " + record.Label);
            if (Directory.Exists(record.ToolDirectory)) Directory.Delete(record.ToolDirectory, recursive: true);
            if (Directory.Exists(record.InputDirectory)) Directory.Delete(record.InputDirectory, recursive: true);
            File.Delete(RunFile(record) + ".new"); File.Delete(record.Plist); File.Delete(file);
            output.WriteLine("DETACHED RECOVERED token " + token + "; no run or game had been journalled. Private startup logs kept at " +
                record.StandardOut + " and " + record.StandardError);
            return 0;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        { error.WriteLine("REFUSED: " + failure.Message); return 3; }
    }

    private static async Task<bool> MacJobMissingAsync(Launch record)
    {
        var probe = new ProcessStartInfo("launchctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        probe.ArgumentList.Add("print"); probe.ArgumentList.Add(record.Domain + "/" + record.Label);
        using var process = Process.Start(probe) ?? throw new IOException("Could not inspect the launchd job.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await stdout.ConfigureAwait(false);
        string diagnostic = await stderr.ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode == 0) return false;
        if (diagnostic.Contains("Could not find service \"" + record.Label + "\"", StringComparison.Ordinal)) return true;
        throw new IOException("The launchd job could not be inspected: " + diagnostic.Trim());
    }

    private static void CopyLog(string source, string target)
    {
        if (!File.Exists(source)) return; // An owner refused before it opened either redirected stream.
        if (File.Exists(target))
        {
            if (FileHash.Sha256(source) != FileHash.Sha256(target))
                throw new IOException("A different detached log already exists: " + target);
            return; // finish can resume after a later cleanup error.
        }
        File.Copy(source, target);
    }
}
