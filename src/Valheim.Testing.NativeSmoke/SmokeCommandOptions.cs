using System.Globalization;
using Valheim.Testing.Game;

/// <summary>The option contract shared by the one-shot hosted and dedicated-server commands.</summary>
internal static class SmokeCommandOptions
{
    internal enum Command { Start, ServerLoad, ServerLoadAb, Build }
    private enum Kind { Single, Repeat, Switch }
    private sealed record Spec(Kind Kind, bool Start = true, bool ServerLoad = true, bool ServerLoadAb = true, bool Build = false, bool ClientRole = false);

    // A shared option has one arity and one validation rule regardless of the actor that runs it.
    private static readonly Dictionary<string, Spec> Table = new(StringComparer.Ordinal)
    {
        ["--mod"] = new(Kind.Repeat), ["--output"] = new(Kind.Single, Build: true),
        ["--client-project"] = new(Kind.Single, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-build-inputs"] = new(Kind.Single, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-assert-command"] = new(Kind.Single, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-assert-line"] = new(Kind.Single, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-mod"] = new(Kind.Repeat, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-dependency"] = new(Kind.Repeat, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-search-root"] = new(Kind.Repeat, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-optional-reference"] = new(Kind.Repeat, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-config"] = new(Kind.Repeat, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-plugin-file"] = new(Kind.Repeat, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--client-plugin-dir"] = new(Kind.Repeat, Start: false, ServerLoadAb: false, ClientRole: true),
        ["--build-inputs"] = new(Kind.Single, ServerLoadAb: false),
        ["--project"] = new(Kind.Single, ServerLoadAb: false, Build: true),
        ["--dependency"] = new(Kind.Repeat, ServerLoadAb: false, Build: true),
        ["--scenario"] = new(Kind.Single, ServerLoadAb: false),
        ["--scenario-project"] = new(Kind.Single, ServerLoadAb: false),
        ["--adapter-project"] = new(Kind.Single, Start: false, ServerLoadAb: false),
        ["--adapter-property"] = new(Kind.Repeat, Start: false, ServerLoadAb: false),
        ["--session-capability"] = new(Kind.Single, Start: false, ServerLoadAb: false),
        ["--session-token-variable"] = new(Kind.Single, Start: false, ServerLoadAb: false),
        ["--server-startup-seconds"] = new(Kind.Single, Start: false),
        ["--inventory"] = new(Kind.Single, Build: true), ["--client-env"] = new(Kind.Single, Build: true),
        ["--client-architecture"] = new(Kind.Single), ["--cli-manifest"] = new(Kind.Single),
        ["--cli-files"] = new(Kind.Single), ["--client-loader-package"] = new(Kind.Single, Build: true),
        ["--search-root"] = new(Kind.Repeat, Build: true), ["--optional-reference"] = new(Kind.Repeat),
        ["--expected-log-error"] = new(Kind.Single), ["--expected-log-reason"] = new(Kind.Single),
        ["--join-seconds"] = new(Kind.Single), ["--hold"] = new(Kind.Switch, ServerLoadAb: false),
        ["--game"] = new(Kind.Single, ServerLoad: false, ServerLoadAb: false, Build: true),
        ["--source"] = new(Kind.Single, ServerLoad: false, ServerLoadAb: false),
        ["--compare-mod"] = new(Kind.Single, ServerLoad: false, ServerLoadAb: false),
        ["--compare-source"] = new(Kind.Single, ServerLoad: false, ServerLoadAb: false),
        ["--server"] = new(Kind.Single, Start: false, Build: true), ["--client"] = new(Kind.Single, Start: false),
        ["--server-env"] = new(Kind.Single, Start: false, Build: true), ["--join"] = new(Kind.Single, Start: false),
        ["--adapter"] = new(Kind.Single, Start: false), ["--loader-package"] = new(Kind.Single, Start: false, Build: true),
        ["--steam-runtime-package"] = new(Kind.Single, Start: false, ServerLoadAb: false),
        ["--config"] = new(Kind.Repeat), ["--env"] = new(Kind.Repeat), ["--plugin-file"] = new(Kind.Repeat, Start: false),
        ["--plugin-dir"] = new(Kind.Repeat, Start: false),
        ["--server-only"] = new(Kind.Switch, Start: false),
        ["--world-fixture"] = new(Kind.Single, ServerLoadAb: false),
        ["--bake-fixture"] = new(Kind.Single, Start: false, ServerLoadAb: false),
        ["--before-save-command"] = new(Kind.Single, Start: false, ServerLoadAb: false),
        ["--before-save-line"] = new(Kind.Single, Start: false, ServerLoadAb: false),
        ["--assert-command"] = new(Kind.Single, Start: false, ServerLoadAb: false),
        ["--assert-line"] = new(Kind.Single, Start: false, ServerLoadAb: false),
        ["--preflight-only"] = new(Kind.Switch, Start: false, ServerLoadAb: false),
        ["--remove-mod"] = new(Kind.Single, Start: false, ServerLoad: false),
        ["--role"] = new(Kind.Single, Start: false, ServerLoad: false, ServerLoadAb: false, Build: true),
    };

    internal static readonly IReadOnlySet<string> ClientRoleOptions = Table
        .Where(option => option.Value.ClientRole).Select(option => option.Key).ToHashSet(StringComparer.Ordinal);

    internal sealed class Parsed
    {
        internal Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, List<string>> Repeated { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Switches { get; } = new(StringComparer.Ordinal);
        internal ModInputStage.ExplicitInputs RunInputs { get; set; } = ModInputStage.ExplicitInputs.Empty;
        internal ModInputStage.RoleSelection? ClientSelection { get; set; }
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
                { Command.Start => spec.Start, Command.ServerLoad => spec.ServerLoad, Command.Build => spec.Build, _ => spec.ServerLoadAb }))
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
        if (command == Command.Build)
        {
            if (!result.Options.ContainsKey("--project")) { error = "Give --project MOD.csproj."; return false; }
            string role = result.Options.GetValueOrDefault("--role", "client");
            if (role is not ("client" or "server")) { error = "--role must be client or server."; return false; }
            // A role's install, environment and loader options are the same ones start (client) and server-load (server) take.
            string[] other = role == "server" ? ["--game", "--client-env", "--client-loader-package"] : ["--server", "--server-env", "--loader-package"];
            if (other.FirstOrDefault(result.Options.ContainsKey) is { } wrong)
            { error = $"{wrong} selects the other role's install; --role {role} takes " + (role == "server" ? "--server, --server-env and --loader-package." : "--game, --client-env and --client-loader-package."); return false; }
        }
        foreach (string prefix in new[] { "--", "--client-" })
        {
            if (result.Options.ContainsKey(prefix + "project") && (result.List(prefix + "mod").Count != 0 || result.Options.ContainsKey(prefix + "build-inputs")))
            { error = prefix + "project builds the one selected mod and records its inputs; leave out " + prefix + "mod and " + prefix + "build-inputs."; return false; }
            if (result.List(prefix + "dependency").Count != 0 && !result.Options.ContainsKey(prefix + "project"))
            { error = prefix + "dependency supplies a private compile reference for " + prefix + "project; use " + prefix + "search-root for an already built mod."; return false; }
        }
        bool clientSelected = result.List("--client-mod").Count != 0 || result.Options.ContainsKey("--client-project");
        if (!clientSelected && ClientRoleOptions.Where(key => key is not ("--client-assert-command" or "--client-assert-line"))
            .Any(key => result.Options.ContainsKey(key) || result.List(key).Count != 0))
        { error = "Client dependency, config and companion inputs require --client-mod or --client-project."; return false; }
        if (result.Switches.Contains("--server-only") && ClientRoleOptions.Any(key => result.Options.ContainsKey(key) || result.List(key).Count != 0))
        { error = "--server-only runs no client: leave out client mod inputs and client assertions."; return false; }
        if (result.Options.ContainsKey("--scenario") && result.Options.ContainsKey("--scenario-project"))
        { error = "Choose --scenario DLL or --scenario-project TEST.csproj, not both."; return false; }
        if (result.Options.ContainsKey("--adapter") && result.Options.ContainsKey("--adapter-project"))
        { error = "Choose --adapter DLL or --adapter-project TEST.csproj, not both."; return false; }
        if (result.List("--adapter-property").Count != 0 && !result.Options.ContainsKey("--adapter-project"))
        { error = "--adapter-property requires --adapter-project."; return false; }
        if (result.Options.ContainsKey("--session-capability") != result.Options.ContainsKey("--session-token-variable"))
        { error = "--session-capability and --session-token-variable must be given together for a custom adapter."; return false; }
        if (result.Options.ContainsKey("--adapter-project") && !result.Options.ContainsKey("--session-capability"))
        { error = "--adapter-project needs its registered --session-capability OWNER/session and --session-token-variable NAME; the built-in adapter uses a different identity."; return false; }
        if ((result.Options.ContainsKey("--scenario") || result.Options.ContainsKey("--scenario-project")) && (result.Switches.Contains("--hold") ||
            result.Options.ContainsKey("--bake-fixture") || result.Options.ContainsKey("--assert-command") || result.Options.ContainsKey("--client-assert-command")))
        { error = "--scenario owns its assertions; leave out --hold, --bake-fixture and inline assertions."; return false; }
        if (command == Command.Start && (result.Options.ContainsKey("--scenario") || result.Options.ContainsKey("--scenario-project")) &&
            result.Options.ContainsKey("--compare-mod"))
        { error = "Run a hosted scenario against one mod build; leave out --compare-mod."; return false; }
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
        if (result.Options.TryGetValue("--server-startup-seconds", out string? startupSeconds) &&
            (!int.TryParse(startupSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int startup) ||
                startup < 1 || startup > ServerRunPlan.MaximumStartupSeconds))
        { error = $"--server-startup-seconds must be a whole number from 1 to {ServerRunPlan.MaximumStartupSeconds}."; return false; }
        if (result.Options.TryGetValue("--client-architecture", out string? architecture) && architecture is not ("x64" or "arm64"))
        { error = "--client-architecture must be x64 or arm64."; return false; }
        if (result.Switches.Contains("--server-only") &&
            new[] { "--client", "--client-env", "--client-loader-package", "--client-architecture", "--join", "--join-seconds" }
                .Any(result.Options.ContainsKey))
        { error = "--server-only runs no client: leave out --client, --client-env, --client-loader-package, --client-architecture, --join and --join-seconds."; return false; }
        if (result.Options.ContainsKey("--bake-fixture") && !result.Switches.Contains("--server-only"))
        { error = "--bake-fixture currently requires --server-only; a joined-client bake has not been verified."; return false; }
        if (result.Options.ContainsKey("--bake-fixture") &&
            (result.Switches.Contains("--hold") || result.Switches.Contains("--preflight-only")))
        { error = "--bake-fixture needs a completed server run and clean stop; leave out --hold and --preflight-only."; return false; }
        if (result.Options.ContainsKey("--before-save-command") != result.Options.ContainsKey("--before-save-line") ||
            result.Options.ContainsKey("--before-save-command") && !result.Options.ContainsKey("--bake-fixture"))
        { error = "--before-save-command and --before-save-line must be given together with --bake-fixture."; return false; }
        if (result.Options.TryGetValue("--before-save-command", out string? action) && action.IndexOfAny(['\0', '\r', '\n']) >= 0)
        { error = "--before-save-command must be one game command, without control characters."; return false; }

        if (result.Options.ContainsKey("--bake-fixture") && !result.Options.ContainsKey("--assert-command"))
        { error = "--bake-fixture needs --assert-command and --assert-line to prove the mod's generated state is ready before saving."; return false; }

        try
        {
            foreach (string prefix in new[] { "--", "--client-" })
                SmokeAssertion.Validate(result.Options.GetValueOrDefault(prefix + "assert-command"), result.Options.GetValueOrDefault(prefix + "assert-line"), prefix);
            _ = ModBuild.ParseDependencies(result.List("--client-dependency"));
            if (clientSelected)
                result.ClientSelection = new(result.List("--client-mod"), result.List("--client-search-root"), result.List("--client-optional-reference"), result.List("--client-dependency"))
                {
                    Project = result.Options.GetValueOrDefault("--client-project"), BuildInputs = result.Options.GetValueOrDefault("--client-build-inputs"),
                    RunInputs = ModInputStage.ReadExplicit(result.List("--client-config"), [], false),
                    PluginFiles = result.List("--client-plugin-file"), PluginDirectories = result.List("--client-plugin-dir"),
                };
            result.RunInputs = ModInputStage.ReadExplicit(result.List("--config"), result.List("--env"),
                command == Command.Start, result.Options.GetValueOrDefault("--session-token-variable"));
        }
        catch (ArgumentException failure) { error = failure.Message; return false; }
        parsed = result;
        return true;
    }

    internal static string Output(IReadOnlyDictionary<string, string> options, Func<string, bool>? containsProject = null)
    {
        containsProject ??= parent => Directory.EnumerateFiles(parent, "*.csproj", SearchOption.TopDirectoryOnly).Any();
        bool explicitOutput = options.TryGetValue("--output", out string? given);
        string output = explicitOutput ? Path.GetFullPath(given!)
            : Path.GetFullPath(Path.Combine(EnvironmentInventory.ThisMachine.DataRoot, "valheim-test-runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                "Z-" + Guid.NewGuid().ToString("N")[..8]));
        for (string? parent = Path.GetDirectoryName(output); parent != null; parent = Path.GetDirectoryName(parent))
        {
            if (!Directory.Exists(parent)) continue;
            bool insideProject;
            try { insideProject = containsProject(parent); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                string workaround = explicitOutput
                    ? "Choose a new directory outside that tree, or omit --output to use the ValheimTesting data folder."
                    : "Choose a writable ValheimTesting data folder outside that tree.";
                throw new IOException($"Cannot inspect output ancestor {parent} for a mod project: {error.Message} {workaround}", error);
            }
            if (insideProject)
                throw new ArgumentException("--output must be outside the mod project; generated adapter .cs files would be compiled into the mod: " + output);
        }
        if (Path.Exists(output)) throw new IOException("--output must be new; existing evidence will not be overwritten: " + output);
        return output;
    }
}

// One paired assertion rule and one strict execution path for either actor; actor selection belongs to the scenario.
internal static class SmokeAssertion
{
    internal static void Validate(string? command, string? line, string prefix)
    {
        if ((command == null) != (line == null)) throw new ArgumentException(prefix + "assert-command and " + prefix + "assert-line must be given together.");
        if (command == null) return;
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(line) ||
            command.IndexOfAny(['\0', '\r', '\n']) >= 0 || line!.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException(prefix + "assert-command and " + prefix + "assert-line require nonempty single-line text without control characters.");
    }

    internal static void Execute(GameActor actor, string command, string line, string role) =>
        actor.Execute(command).RequireLine(line, "The " + role + " observation did not match the declared assertion");
}
