using System.Reflection;

/// <summary>
/// The tool's <c>--help</c> and <c>--version</c> (#955, #957): answered before any command runs, so neither reads an inventory,
/// takes a host lock or writes a journal. <c>--help</c> (or <c>-h</c>) prints the usage of the command it follows, or every
/// command's at the root, and exits 0; <c>--version</c> (or <c>-V</c>) prints this tool's package version and source commit.
/// </summary>
internal static class ToolCommandLine
{
    internal static readonly string StartUsage = $"valheim-test start [--mod DLL ... | --project MOD.csproj | --no-mod] [--config FILE ...] [--env NAME=VALUE ...] [--plugin-file FILE ...] [--plugin-dir DIR ...] [--scenario TEST.dll | --scenario-project TEST.csproj] [--dependency NAME=DLL ...] [--build-inputs FILE] [--output NEW_DIR] [--world-fixture DIR | --world-seed SEED [--world-name NAME]] [--inventory FILE | --game DIR] [--client-env NAME] [--client-architecture x64|arm64] [--join-seconds {Valheim.Testing.Game.ClientTimeouts.MinimumJoinSeconds}..{Valheim.Testing.Game.ClientTimeouts.MaximumJoinSeconds}] [--hold [--menu-only]] [--source COMMIT] [--cli-manifest FILE --cli-files DIR] [--compare-mod DLL --compare-source COMMIT] [--search-root DIR|DLL ...] [--optional-reference ASSEMBLY ...] [--client-loader-package FILE] [--expected-log-error EXACT_HEADER --expected-log-reason REASON]";
    internal const string ServerLoadAbUsage = "valheim-test server-load-ab --mod DLL --mod DLL --remove-mod DLL --output NEW_DIR [server-load options except --hold and --preflight-only]";
    internal const string InitUsage = "valheim-test init [server] --output NEW_DIR";

    // Each command's one usage line, by the word that names it.
    private static readonly Dictionary<string, string> Usages = new(StringComparer.Ordinal)
    {
        ["start"] = StartUsage,
        ["server-load"] = ServerLoad.Usage,
        ["server-load-ab"] = ServerLoadAbUsage,
        ["server-load-phases"] = ServerLoadPhases.Usage,
        ["init"] = InitUsage,
        ["build"] = ModBuild.Usage,
        ["env"] = EnvCommand.Usage,
        ["finish"] = ForegroundHold.FinishUsage,
        ["detach"] = DetachedSession.Usage,
        ["cli"] = OwnedCliCommand.Usage,
        ["save-world"] = WorldSave.Usage,
        ["session"] = SessionCommand.Usage,
        ["steam-runtime"] = MacSteamRuntimeCommand.Usage,
    };

    /// <summary>This tool's version as its package states it, with the source commit when the build recorded one.</summary>
    internal static string Version
    {
        get
        {
            string informational = typeof(ToolCommandLine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(ToolCommandLine).Assembly.GetName().Version?.ToString() ?? "unknown";
            int plus = informational.IndexOf('+');
            return plus < 0 ? informational : $"{informational[..plus]} (commit {informational[(plus + 1)..]})";
        }
    }

    /// <summary>
    /// True when <paramref name="args"/> ask for help or the version: the answer is written to <paramref name="output"/> and
    /// <paramref name="exitCode"/> is 0. A command word followed by <c>--help</c> or <c>-h</c> anywhere gets that command's usage;
    /// arguments with no command word are <c>start</c>'s, as the tool reads them.
    /// </summary>
    internal static bool TryAnswer(string[] args, TextWriter output, out int exitCode)
    {
        exitCode = 0;
        if (args is ["--version" or "-V"])
        {
            output.WriteLine("valheim-test " + Version);
            return true;
        }
        if (args is ["help" or "--help" or "-h"])
        {
            Root(output);
            return true;
        }
        if (!args.Any(arg => arg is "--help" or "-h")) return false;
        // A word that is no command (a typo such as sever-load) is left to start's parser, which names it, rather than answered as start.
        if (!Usages.ContainsKey(args[0]) && !args[0].StartsWith('-')) return false;
        string command = Usages.ContainsKey(args[0]) ? args[0] : "start";
        output.WriteLine("Usage: " + Usages[command]);
        if (command == "start") output.WriteLine("Without a command word the tool runs start. valheim-test --help lists every command.");
        return true;
    }

    private static void Root(TextWriter output)
    {
        output.WriteLine("valheim-test start [--mod DLL ... | --no-mod] [--config FILE ...] [--env NAME=VALUE ...] [--plugin-file FILE ...] [--plugin-dir DIR ...] [--output NEW_DIR] [--world-fixture DIR | --world-seed SEED [--world-name NAME]] [--inventory FILE | --game DIR] [--client-env NAME] [--client-architecture x64|arm64] [setup options] (without --mod or --no-mod, the current project's one built plugin)");
        output.WriteLine("--no-mod explicitly runs a CLI-only control with no mod under test; no mod build, dependency search, scenario or comparison inputs are allowed.");
        output.WriteLine("--config stages non-secret config files on start's hosting client or server-load's server; --client-config targets server-load's explicit client mod set. --plugin-file FILE stages a mod's non-DLL companion as BepInEx/plugins/<name>, --plugin-dir DIR as BepInEx/plugins/<folder name>/..., on the same actor as --config. --env sets non-empty literal values on every owned game process. Inputs are retained privately; omit credentials.");
        output.WriteLine(ServerLoad.Usage + " (a dedicated server and one joining client; client is CLI-only by default, with explicit --client-mod or --client-project support; inventory selects the hosts, otherwise this machine)");
        output.WriteLine(ServerLoadPhases.Usage + " (ordered disposable server-only saves)");
        output.WriteLine(ServerLoadAbUsage);
        output.WriteLine(InitUsage + " (editable NuGet.org-only consumer)");
        output.WriteLine(ModBuild.Usage + " (private references and a mod build, with no live-game deployment)");
        output.WriteLine(EnvCommand.Usage + " (list, preflight and status read only; recover and teardown clear what a run left)");
        output.WriteLine(ForegroundHold.FinishUsage + " (asks the live owner of a held run to finish and clean up)");
        output.WriteLine(DetachedSession.Usage + " (experimental local detached dedicated server; Task Scheduler on Windows)");
        output.WriteLine(OwnedCliCommand.Usage + " (one strictly pinned command to a running owned local client or server)");
        output.WriteLine(WorldSave.Usage + " (saves a held start run's hosted world and copies it out for start --world-fixture)");
        output.WriteLine(SessionCommand.Usage + " (read only; --hosts adds the host checks)");
        output.WriteLine(MacSteamRuntimeCommand.Usage + " (review and pin SteamCMD's Mac server libraries)");
        output.WriteLine("valheim-test COMMAND --help prints that command's usage; valheim-test --version prints this tool's version.");
    }
}
