using System.Globalization;
using Valheim.Testing.Game;

/// <summary>The option contract shared by the one-shot hosted and dedicated-server commands.</summary>
internal static class SmokeCommandOptions
{
    internal enum Command { Start, ServerLoad, ServerLoadAb }
    private enum Kind { Single, Repeat, Switch }
    private sealed record Spec(Kind Kind, bool Start = true, bool ServerLoad = true, bool ServerLoadAb = true);

    // A shared option has one arity and one validation rule regardless of the actor that runs it.
    private static readonly Dictionary<string, Spec> Table = new(StringComparer.Ordinal)
    {
        ["--mod"] = new(Kind.Repeat), ["--output"] = new(Kind.Single),
        ["--inventory"] = new(Kind.Single), ["--client-env"] = new(Kind.Single),
        ["--client-architecture"] = new(Kind.Single), ["--cli-manifest"] = new(Kind.Single),
        ["--cli-files"] = new(Kind.Single), ["--client-loader-package"] = new(Kind.Single),
        ["--search-root"] = new(Kind.Repeat), ["--optional-reference"] = new(Kind.Repeat),
        ["--expected-log-error"] = new(Kind.Single), ["--expected-log-reason"] = new(Kind.Single),
        ["--join-seconds"] = new(Kind.Single), ["--hold"] = new(Kind.Switch, ServerLoadAb: false),
        ["--game"] = new(Kind.Single, ServerLoad: false, ServerLoadAb: false),
        ["--source"] = new(Kind.Single, ServerLoad: false, ServerLoadAb: false),
        ["--compare-mod"] = new(Kind.Single, ServerLoad: false, ServerLoadAb: false),
        ["--compare-source"] = new(Kind.Single, ServerLoad: false, ServerLoadAb: false),
        ["--server"] = new(Kind.Single, Start: false), ["--client"] = new(Kind.Single, Start: false),
        ["--server-env"] = new(Kind.Single, Start: false), ["--join"] = new(Kind.Single, Start: false),
        ["--adapter"] = new(Kind.Single, Start: false), ["--loader-package"] = new(Kind.Single, Start: false),
        ["--config"] = new(Kind.Repeat, Start: false), ["--plugin-file"] = new(Kind.Repeat, Start: false),
        ["--plugin-dir"] = new(Kind.Repeat, Start: false),
        ["--server-only"] = new(Kind.Switch, Start: false),
        ["--preflight-only"] = new(Kind.Switch, Start: false, ServerLoadAb: false),
        ["--remove-mod"] = new(Kind.Single, Start: false, ServerLoad: false),
    };

    internal sealed class Parsed
    {
        internal Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, List<string>> Repeated { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Switches { get; } = new(StringComparer.Ordinal);
        internal List<string> List(string key) => Repeated.TryGetValue(key, out var list) ? list : [];
    }

    internal static bool TryRead(string[] args, Command command, bool allowImplicitMod, out Parsed? parsed, out string error)
    {
        parsed = null;
        error = "";
        var result = new Parsed();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (command == Command.ServerLoadAb && key == "--hold")
            { error = "--hold cannot run in a comparison: both arms must finish before their results can be compared. Use server-load --hold for interactive inspection."; return false; }
            if (command == Command.ServerLoadAb && key == "--preflight-only")
            { error = "--preflight-only runs no arm; preflight each arm with server-load instead."; return false; }
            if (!Table.TryGetValue(key, out var spec) || !(command switch
                { Command.Start => spec.Start, Command.ServerLoad => spec.ServerLoad, _ => spec.ServerLoadAb }))
            { error = command == Command.Start && key == "--loader-package"
                ? "start uses --client-loader-package (not --loader-package)."
                : "Unknown option: " + key; return false; }
            if (spec.Kind == Kind.Switch)
            {
                if (!result.Switches.Add(key)) { error = "Repeated option: " + key; return false; }
                continue;
            }
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            { error = "Option needs a value: " + key; return false; }
            string value = args[++i];
            if (spec.Kind == Kind.Repeat)
            {
                if (!result.Repeated.TryGetValue(key, out var list)) result.Repeated[key] = list = [];
                list.Add(value);
            }
            else if (!result.Options.TryAdd(key, value))
            { error = "Repeated option: " + key; return false; }
        }
        if (!allowImplicitMod && result.List("--mod").Count == 0) { error = "Missing: --mod"; return false; }
        if (command == Command.ServerLoadAb)
        {
            if (!result.Options.ContainsKey("--remove-mod")) { error = "Specify exactly one --remove-mod."; return false; }
            if (!result.Options.ContainsKey("--output")) { error = "Specify --output for the comparison's two arms."; return false; }
        }
        if (result.Options.ContainsKey("--expected-log-error") != result.Options.ContainsKey("--expected-log-reason"))
        { error = "--expected-log-error needs --expected-log-reason (and vice versa); an unexplained error is never ignored."; return false; }
        if (command == Command.Start && result.Options.ContainsKey("--compare-mod") != result.Options.ContainsKey("--compare-source"))
        { error = "--compare-mod needs --compare-source (and vice versa); both builds need provenance."; return false; }
        if (result.Options.TryGetValue("--join-seconds", out string? joinSeconds) &&
            (!int.TryParse(joinSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 10 or > 900))
        { error = "--join-seconds must be a whole number from 10 to 900."; return false; }
        if (result.Options.TryGetValue("--client-architecture", out string? architecture) && architecture is not ("x64" or "arm64"))
        { error = "--client-architecture must be x64 or arm64."; return false; }
        if (result.Switches.Contains("--server-only") &&
            new[] { "--client", "--client-env", "--client-loader-package", "--client-architecture", "--join", "--join-seconds" }
                .Any(result.Options.ContainsKey))
        { error = "--server-only runs no client: leave out --client, --client-env, --client-loader-package, --client-architecture, --join and --join-seconds."; return false; }
        parsed = result;
        return true;
    }

    internal static string Output(IReadOnlyDictionary<string, string> options, string? projectDirectory = null)
    {
        string output = options.TryGetValue("--output", out string? given) ? Path.GetFullPath(given)
            : Path.GetFullPath(Path.Combine(EnvironmentInventory.ThisMachine.DataRoot, "valheim-test-runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                "Z-" + Guid.NewGuid().ToString("N")[..8]));
        string project = Path.GetFullPath(projectDirectory ?? Environment.CurrentDirectory);
        if (Directory.Exists(project) && Directory.EnumerateFiles(project, "*.csproj", SearchOption.TopDirectoryOnly).Any())
        {
            string relative = Path.GetRelativePath(project, output);
            if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new ArgumentException("--output must be outside the mod project; generated adapter .cs files would be compiled into the mod: " + output);
        }
        if (Path.Exists(output)) throw new IOException("--output must be new; existing evidence will not be overwritten: " + output);
        return output;
    }
}
