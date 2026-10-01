using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using valheimCLI;
using Valheim.Testing.Game;

/// <summary>The server half of #156: pinned selected mods on an owned dedicated server. A clean-client join follows.</summary>
internal static class ServerLoad
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryRead(args, out var options, out var mods, out var roots, out var configs,
                out var pluginFiles, out var pluginDirectories, out var optional, out string error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: native-smoke server-load --server DIR --mod DLL [--mod DLL ...] --adapter DLL --cli-manifest FILE --cli-files DIR --output NEW_DIR [--client DIR --steam-userdata DIR --client-cli-port 5689] [--search-root DIR ...] [--config FILE ...] [--plugin-file FILE ...] [--plugin-dir DIR ...] [--optional-reference ASSEMBLY ...] [--cli-port 5688] [--game-port 2486] [--expected-log-error EXACT_HEADER --expected-log-reason REASON]");
            return 2;
        }
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, press) => { press.Cancel = true; cancel.Cancel(); };
        var clock = Stopwatch.StartNew();
        try
        {
            string output = Path.GetFullPath(options!["--output"]);
            if (Path.Exists(output)) throw new IOException("--output must be new; existing evidence will not be overwritten: " + output);
            string server = Path.GetFullPath(options["--server"]);
            string adapter = Path.GetFullPath(options["--adapter"]);
            string cliManifest = Path.GetFullPath(options["--cli-manifest"]);
            string cliFiles = Path.GetFullPath(options["--cli-files"]);
            int cliPort = options.TryGetValue("--cli-port", out string? cliValue) ? int.Parse(cliValue, CultureInfo.InvariantCulture) : 5688;
            int gamePort = options.TryGetValue("--game-port", out string? gameValue) ? int.Parse(gameValue, CultureInfo.InvariantCulture) : 2486;
            bool joinClient = options.ContainsKey("--client");
            int clientCliPort = options.TryGetValue("--client-cli-port", out string? clientValue) ? int.Parse(clientValue, CultureInfo.InvariantCulture) : 5689;
            if (Math.Abs(cliPort - gamePort) < 3) throw new ArgumentException("Choose a CLI port away from the game's three-port range.");
            if (joinClient && (clientCliPort == cliPort || Math.Abs(clientCliPort - gamePort) < 3))
                throw new ArgumentException("The clean client's CLI port must differ from the server CLI and game ports.");
            foreach (string path in new[] { server, cliFiles })
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("A server or ValheimCLI directory is missing: " + path);
            foreach (string path in new[] { adapter, cliManifest }.Concat(mods!.Select(Path.GetFullPath)))
                if (!File.Exists(path)) throw new FileNotFoundException("A selected file is missing: " + path, path);
            var request = new NativeDependencyRequest
            {
                Mods = mods!.Select(Path.GetFullPath).ToList(), SearchRoots = roots!.Select(Path.GetFullPath).ToList(),
                GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(server))!,
                BepInExCore = Path.Combine(server, InstallPins.CoreDirectory),
                CliManifest = cliManifest, CliFiles = cliFiles,
                Capabilities = joinClient
                    ? ["valheim.session/state", "valheim.session/join", "valheim.session/leave"]
                    : ["valheim.session/state"],
                OptionalReferences = [.. optional!],
            };
            var dependencies = NativeDependencyResolver.Resolve(request);
            Directory.CreateDirectory(output);
            dependencies.Write(Path.Combine(output, "dependencies.lock.json"));
            if (!dependencies.Ready)
                throw new InvalidDataException("Dependency choices remain: " + string.Join("; ", dependencies.Gaps.Select(gap => gap.Kind + " " + gap.Name + ": " + gap.Reason)));
            string world = Path.Combine(output, "world-source");
            DefaultSmokeWorld.PrepareServerSaveRoot(world);
            using var runtime = NativeServerRuntime.Prepare(server, Path.Combine(output, "staged-runtime"), dependencies, adapter,
                cliPort, configs!.Select(Path.GetFullPath).ToList(), pluginFiles!.Select(Path.GetFullPath).ToList(),
                pluginDirectories!.Select(Path.GetFullPath).ToList());
            using var clientRuntime = joinClient
                ? NativeCleanClientRuntime.Prepare(Path.GetFullPath(options["--client"]), Path.Combine(output, "staged-client"), dependencies, clientCliPort)
                : null;
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var plan = runtime.Plan(world, cliPort, gamePort, password);
            var absentGuids = runtime.Pins.Keys.Except(clientRuntime?.CliPins.Keys ?? [], StringComparer.Ordinal)
                .Where(guid => guid != NativeServerRuntime.SessionAdapterPluginGuid).Order(StringComparer.Ordinal).ToArray();
            ClientRunPlan? clientPlan = clientRuntime?.Plan(clientCliPort, gamePort, absentGuids);
            DisposableCharacterStore? character = clientRuntime == null ? null
                : DefaultSmokeCharacter.Prepare(Path.Combine(output, "character-source"));
            if (options.TryGetValue("--expected-log-error", out string? expectedError))
                plan.LogScan[LogScanner.UnknownError] = new LogClassification
                { Expected = [expectedError], Reason = options["--expected-log-reason"] };
            string planFile = Path.Combine(output, "plan.json");
            File.WriteAllText(planFile, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
            string? previousPassword = Environment.GetEnvironmentVariable(NativeCleanClientRuntime.PasswordVariable);
            if (joinClient) Environment.SetEnvironmentVariable(NativeCleanClientRuntime.PasswordVariable, password);
            int result;
            try
            {
                result = await PinnedServerRun.MainAsync(["run", planFile, Path.Combine(output, "evidence")],
                    new PinnedServerRunOptions<ServerRunPlan>
                    {
                        Name = "native-smoke-server-load",
                        ReadPlan = path =>
                        {
                            var read = ServerRunPlan.Read<ServerRunPlan>(path);
                            read.ValidateServerPlan(read.Pins.Keys.Where(key => key != "worlduid"), NativeServerRuntime.SessionTokenVariable);
                            return read;
                        },
                        SessionCapability = NativeServerRuntime.SessionCapability,
                        SessionTokenVariable = NativeServerRuntime.SessionTokenVariable,
                        EnableDevcommands = false,
                        Scenario = run =>
                        {
                            run.Report.Step("selected server mods loaded and world identity matches", () =>
                            {
                                var worlds = run.Server.Execute("cli_world").Output.Select(Expectations.ParseWorld).OfType<WorldFacts>().ToArray();
                                if (worlds.Length != 1 || worlds[0].Uid != DefaultSmokeWorld.Uid)
                                    throw new InvalidDataException("The dedicated server did not load the packaged smoke world UID.");
                            });
                            run.Report.Provenance["firstModLoadedSecondsFromCommand"] =
                                clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
                            run.Report.Step("dedicated server accepts a game connection", () =>
                                OwnedServerSession.WaitUntilJoinable(run.Server, NativeServerRuntime.SessionCapability,
                                    TimeSpan.FromSeconds(run.Plan.StartupSeconds), run.Cancellation));
                            if (clientPlan != null)
                            {
                                string saves = HostedWorld.DefaultSaveDirectory(ClientLaunch.Detect(clientPlan.Install));
                                using var stagedCharacter = DefaultSmokeCharacter.StageForRun(character!,
                                    Path.Combine(saves, "characters_local"), options["--steam-userdata"]);
                                new ClientRounds
                                {
                                    Client = clientPlan, WorldUid = DefaultSmokeWorld.Uid, Report = run.Report, Output = run.Output,
                                    WaitUntilJoinable = serverActor => OwnedServerSession.WaitUntilJoinable(serverActor,
                                        NativeServerRuntime.SessionCapability, TimeSpan.FromSeconds(run.Plan.StartupSeconds), run.Cancellation),
                                    RestartServer = run.Session.Restart, Rounds = ["first"], ProtectPlayer = false,
                                    Cancellation = run.Cancellation,
                                }.Run(run.Server, () => run.OpenClient(clientPlan), round =>
                                    round.Step("clean client can read the joined world", () =>
                                    {
                                        var state = new SessionControl(round.Client).Read();
                                        if (!state.WorldReady || !state.PlayerReady || state.WorldUid != DefaultSmokeWorld.Uid)
                                            throw new InvalidDataException("The clean client has no ready player in the pinned world.");
                                    }));
                            }
                            return Task.CompletedTask;
                        },
                    });
            }
            finally
            {
                if (joinClient) Environment.SetEnvironmentVariable(NativeCleanClientRuntime.PasswordVariable, previousPassword);
            }
            Console.WriteLine((result == 0 ? (joinClient ? "SERVER_JOIN_PASS" : "SERVER_LOAD_PASS") : "SERVER_LOAD_FAIL") +
                $": {mods!.Count} selected mod(s), {clock.Elapsed.TotalSeconds:F1}s. " +
                (result != 0 ? "See the private evidence for the failed check." :
                    joinClient ? "Clean client joined with selected server mods absent." : "A clean-client join has not run.") +
                $" Private evidence in {output}");
            return result;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
    }

    private static bool TryRead(string[] args, out Dictionary<string, string>? options, out List<string>? mods,
        out List<string>? roots, out List<string>? configs, out List<string>? pluginFiles,
        out List<string>? pluginDirectories, out List<string>? optional, out string error)
    {
        options = null; mods = null; roots = null; configs = null; pluginFiles = null; pluginDirectories = null; optional = null; error = "";
        var required = new HashSet<string>(StringComparer.Ordinal) { "--server", "--mod", "--adapter", "--cli-manifest", "--cli-files", "--output" };
        var allowed = new HashSet<string>(required, StringComparer.Ordinal)
        { "--cli-port", "--game-port", "--client", "--steam-userdata", "--client-cli-port", "--expected-log-error", "--expected-log-reason" };
        if (args.Length % 2 != 0) { error = "Every option needs one value."; return false; }
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = new List<string>(); var searches = new List<string>();
        var configFiles = new List<string>(); var omissions = new List<string>();
        var sidecarFiles = new List<string>(); var sidecarDirectories = new List<string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            string key = args[i], value = args[i + 1];
            if (string.IsNullOrWhiteSpace(value)) { error = "Empty option: " + key; return false; }
            if (key == "--mod") { selected.Add(value); found.TryAdd(key, value); }
            else if (key == "--search-root") searches.Add(value);
            else if (key == "--config") configFiles.Add(value);
            else if (key == "--plugin-file") sidecarFiles.Add(value);
            else if (key == "--plugin-dir") sidecarDirectories.Add(value);
            else if (key == "--optional-reference") omissions.Add(value);
            else if (!allowed.Contains(key) || !found.TryAdd(key, value))
            { error = "Unknown or repeated option: " + key; return false; }
        }
        var missing = required.Where(key => !found.ContainsKey(key)).ToList();
        if (missing.Count != 0) { error = "Missing: " + string.Join(", ", missing); return false; }
        if (found.ContainsKey("--expected-log-error") != found.ContainsKey("--expected-log-reason"))
        { error = "An expected log error needs its full header and a written reason."; return false; }
        if (found.ContainsKey("--client") != found.ContainsKey("--steam-userdata"))
        { error = "--client needs --steam-userdata (and vice versa) to stage a disposable character safely."; return false; }
        if (found.ContainsKey("--client-cli-port") && !found.ContainsKey("--client"))
        { error = "--client-cli-port needs --client."; return false; }
        options = found; mods = selected; roots = searches; configs = configFiles;
        pluginFiles = sidecarFiles; pluginDirectories = sidecarDirectories; optional = omissions;
        return true;
    }
}
