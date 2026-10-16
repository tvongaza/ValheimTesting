using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using valheimCLI;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>
/// The one-off native check for a mod: its server load on an owned dedicated server and, unless <c>--server-only</c>, one
/// clean client's join with the mod absent. The actors come from the environment inventory (this machine with no file):
/// its first server and first client environment, the packaged smoke world and the packaged disposable character, run as
/// a campaign whose derived <c>campaign.json</c> is written into the output. Nothing has to be written by hand.
/// </summary>
internal static class ServerLoad
{
    internal static readonly string Usage = "valheim-test server-load [--mod DLL ... | --project MOD.csproj] [--scenario TEST.dll | --scenario-project TEST.csproj] [--adapter DLL | --adapter-project ADAPTER.csproj] [--adapter-property NAME={mod}|{cli} ...] [--session-capability OWNER/session --session-token-variable NAME] [--dependency NAME=DLL ...] [--output NEW_DIR] [--inventory FILE | --server DIR] [--client DIR] " +
        $"[--server-env NAME] [--client-env NAME] [--client-architecture x64|arm64] [--server-only] [--server-startup-seconds 1..{ServerRunPlan.MaximumStartupSeconds}] [--world-fixture DIR] [--bake-fixture NEW_DIR] [--before-save-command TEXT --before-save-line PREFIX] [--assert-command TEXT --assert-line PREFIX] [--join HOST:PORT] [--join-seconds 10..900] [--preflight-only] [--hold] [--loader-package FILE] [--steam-runtime-package FILE] [--client-loader-package FILE] " +
        "[--build-inputs FILE] [--cli-manifest FILE --cli-files DIR] [--search-root DIR|DLL ...] [--config FILE ...] [--plugin-file FILE ...] [--plugin-dir DIR ...] " +
        "[--optional-reference ASSEMBLY ...] [--expected-log-error EXACT_HEADER --expected-log-reason REASON]";

    /// <summary>The parsed command line: single options, repeatable inputs and switches.</summary>
    internal sealed class Arguments
    {
        public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
        public List<string> Mods { get; } = [];
        public List<string> Roots { get; } = [];
        public List<string> Configs { get; } = [];
        public List<string> PluginFiles { get; } = [];
        public List<string> PluginDirectories { get; } = [];
        public List<string> Optional { get; } = [];
        public List<string> BuildDependencies { get; } = [];
        public List<string> AdapterProperties { get; } = [];
        public HashSet<string> Switches { get; } = new(StringComparer.Ordinal);
        public bool ServerOnly => Switches.Contains("--server-only");
    }

    /// <summary>Test seams: the host preflight and campaign run in place of the real ones.</summary>
    internal sealed record Seams(
        Func<string, Task<CampaignPreflightReport>>? Inspect = null,
        Func<string, ServerRunPlan, Func<ServerRunPlan, IReadOnlyDictionary<string, ClientRunPlan>>, string, PinnedServerRunOptions<ServerRunPlan>, Task<int>>? Campaign = null,
        // The pinned-loader rule extracts the real pack into this machine's data root, so it is on only for a real run (no
        // seams) and for a test that passes one.
        ShippedLoader.Rule? Loader = null,
        Action<EnvironmentInventory, EnvironmentRecipe, string, string?>? ClientArchitecture = null,
        // An A/B run freezes its automatic server choice before either arm starts. Its command
        // still names the package, while this carries the reason into each arm's evidence.
        ShippedLoader.Choice? FrozenServerLoader = null,
        string? SummaryLabel = null,
        TextWriter? SummaryOutput = null);

    public static async Task<int> RunAsync(string[] args, Seams? seams = null)
    {
        seams ??= new Seams(Loader: ShippedLoader.Choose);
        if (!TryRead(args, out var parsed, out string error, allowImplicitMod: true))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: " + Usage);
            return 2;
        }
        if (parsed!.Switches.Contains("--hold") && parsed.Switches.Contains("--preflight-only"))
        {
            Console.Error.WriteLine("--hold needs a running game; leave out --preflight-only.");
            return 2;
        }
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, press) => { press.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        var clock = Stopwatch.StartNew();
        string? output = null;
        var state = new RunState();
        try
        {
            // The scheduled supervisor must become this run's journal runner before ModInputStage's local journal preflight.
            // Otherwise the still-live starter looks like another process's live run and the supervisor refuses itself. It holds the
            // run's control lock until it ends (#811).
            using var supervisor = DetachedSession.ConfiguredRun is { } detachedRun ? DetachedSession.AdoptRun(detachedRun) : null;
            if (supervisor != null) DetachedSession.VerifyStagedInputs();
            output = Output(parsed!);
            if (parsed.Options.TryGetValue("--adapter", out string? adapterFile) && !File.Exists(Path.GetFullPath(adapterFile)))
                throw new FileNotFoundException("A selected file is missing: " + Path.GetFullPath(adapterFile), Path.GetFullPath(adapterFile));
            // The server and client, their loaders, the mod, its build inputs, the scenario and the dependency lock: one
            // stage, shared with build and start.
            var inputs = await ModInputStage.RunAsync(new ModInputStage.Request(ModInputStage.Route.Dedicated, output, parsed.Options,
                parsed.Switches, parsed.Mods, parsed.Roots, parsed.Optional, parsed.BuildDependencies)
            {
                Shipped = seams.Loader, FrozenServerLoader = seams.FrozenServerLoader, ClientArchitecture = seams.ClientArchitecture, Progress = state,
            }, cancel.Token).ConfigureAwait(false);
            return await RunCampaignAsync(parsed, inputs, output, clock, seams, state, cancel.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or FormatException or JsonException or OperationCanceledException)
        {
            Console.Error.WriteLine("REFUSED: " + failure.Message);
            // The marker means no passing result was established; a copy or launch may already have occurred.
            // Never write into a folder that was itself refused as protected.
            if (state.OutputChecked) SmokeOutput.MarkRefused(output, "server-load", [failure.Message]);
            return 3;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    /// <summary>Where a run is: its output checked against the installs it must stay out of, and whether anything was copied or started.</summary>
    internal sealed class RunState : ModInputStage.Progress
    {
        public bool Started { get; set; }
    }

    // A source loader refusal (a macOS install with an incomplete BepInEx) names the reviewed-package fix for that actor.
    // Not when the actor already runs with a package: then the refusal is the package's own (another platform's loader), and
    // the hint would name the very option that caused it.
    private static string LoaderHint(CampaignPreflightProblem problem, string? serverLoader, string? clientLoader) =>
        problem.Input != "game and loader" ? "" // an unreadable --loader-package is the "loader" input
            : problem.Actor == "server" && serverLoader == null ? " (--loader-package FILE gives the server's disposable profile a reviewed loader)"
            : problem.Actor == "client" && clientLoader == null ? " (--client-loader-package FILE gives the client's disposable profile a reviewed loader)" : "";
    // --output, or a new timestamped directory under ValheimTesting's data root. Never an existing one.
    private static string Output(Arguments parsed)
        => SmokeCommandOptions.Output(parsed.Options);

    private static async Task<int> RunCampaignAsync(Arguments parsed, ModInputStage.Inputs inputs, string output, Stopwatch clock, Seams seams, RunState state, CancellationToken cancellation)
    {
        var serverActor = inputs.Server!;
        var server = serverActor.Recipe;
        var client = inputs.Client;
        string serverInstall = server.Install;
        string core = inputs.Core;
        var dependencies = inputs.Dependencies!;
        string? serverLoader = serverActor.Loader, clientLoader = client?.Loader;
        string? clientArchitecture = inputs.ClientArchitecture;
        string? bakeDestination = parsed.Options.TryGetValue("--bake-fixture", out string? requestedBake)
            ? Path.GetFullPath(requestedBake) : null;
        string? fixtureSource = parsed.Options.TryGetValue("--world-fixture", out string? requestedFixture)
            ? Path.GetFullPath(requestedFixture) : null;
        string? steamRuntimePackage = inputs.SteamRuntimePackage;
        string? adapter = parsed.Options.TryGetValue("--adapter", out string? adapterFile) ? Path.GetFullPath(adapterFile) : null;
        AdapterSourceInputs.Record? adapterSource = null;
        var mods = inputs.Mods;
        var custom = inputs.Scenario;
        var serverScenario = (IOneShotServerScenario?)custom?.Runner;
        string serverLock = Path.Combine(output, "dependencies.lock.json");
        var bakedBuild = bakeDestination != null ? FixtureBake.CaptureBuild(mods, serverLock, dependencies) : null;
        // Reject a bad or changed world before building an adapter or staging either game actor.
        var fixture = FixtureBake.Prepare(fixtureSource, output);
        if (parsed.Options.TryGetValue("--adapter-project", out string? adapterProject))
        {
            string cliCore = dependencies.CliPlugin.File;
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CliDll"] = cliCore,
                ["ModDll"] = mods[0],
                ["ValheimPath"] = serverInstall,
                ["ValheimManaged"] = Path.GetDirectoryName(InstallPins.GameAssembly(serverInstall))!,
                ["BepInExCore"] = core,
            };
            foreach (string given in parsed.AdapterProperties)
            {
                int equal = given.IndexOf('=');
                if (equal < 1 || equal == given.Length - 1)
                    throw new ArgumentException("--adapter-property must be NAME={mod} or NAME={cli}.");
                string value = given[(equal + 1)..] switch
                {
                    "{mod}" => mods[0],
                    "{cli}" => cliCore,
                    _ => throw new ArgumentException("--adapter-property accepts only {mod} or {cli}, so it cannot bypass the pinned inputs."),
                };
                if (properties.ContainsKey(given[..equal]))
                    throw new ArgumentException("Duplicate or reserved --adapter-property: " + given[..equal]);
                properties.Add(given[..equal], value);
            }
            string adapterOutput = Path.Combine(output, "adapter-build");
            adapter = await OneShotScenario.BuildAsync(adapterProject, adapterOutput, cancellation, properties,
                captureAdapterSources: true, testedBinaries: mods.Select(Path.GetFullPath).Append(cliCore).ToArray())
                .ConfigureAwait(false);
            string sourceManifest = Path.Combine(adapterOutput, "source-inputs", "manifest.json");
            adapterSource = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(File.ReadAllText(sourceManifest))
                ?? throw new InvalidDataException("The adapter source-input manifest is empty.");
            if (adapterSource.AdapterSha256 != FileHash.Sha256(adapter))
                throw new InvalidDataException("The adapter DLL changed after its source inputs were recorded.");
            File.WriteAllText(Path.Combine(adapterOutput, "inputs.json"), JsonSerializer.Serialize(new
            {
                project = Path.GetFullPath(adapterProject),
                mod = new { file = mods[0], sha256 = FileHash.Sha256(mods[0]) },
                cli = new { file = cliCore, sha256 = FileHash.Sha256(cliCore) },
                properties,
                adapter = new { file = adapter, sha256 = FileHash.Sha256(adapter) },
                sourceInputSha256 = adapterSource.SourceInputSha256,
                compiledSourceSha256 = adapterSource.CompiledSourceSha256,
                evaluatedBuildSha256 = adapterSource.EvaluatedBuildSha256,
                sourceManifest,
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        adapter ??= await SmokeAdapter.BuildAsync(serverInstall, dependencies, output, cancellation, core).ConfigureAwait(false);

        // The server's own files beside its lock: the session adapter, explicit configs and plugin sidecars.
        var files = new List<HostedRuntimeFile> { new(adapter, "BepInEx/plugins/" + Path.GetFileName(adapter)) };
        foreach (string config in parsed.Configs.Select(Path.GetFullPath))
        {
            if (Path.GetFileName(config).Equals(OwnedClientPreflight.CliConfig, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ValheimCLI's config is the run's own; do not supply it as a mod config.");
            files.Add(new HostedRuntimeFile(config, "BepInEx/config/" + Path.GetFileName(config)));
        }
        foreach (string sidecar in parsed.PluginFiles.Select(Path.GetFullPath))
        {
            if (Path.GetExtension(sidecar).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin DLLs belong in --mod or a dependency search root, not a sidecar: " + sidecar);
            files.Add(new HostedRuntimeFile(sidecar, "BepInEx/plugins/" + Path.GetFileName(sidecar)));
        }
        foreach (string directory in parsed.PluginDirectories.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))))
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("An explicitly selected plugin sidecar directory is missing: " + directory);
            foreach (string relative in WorldFixture.Manifest(directory).Keys.Order(StringComparer.Ordinal))
            {
                if (Path.GetExtension(relative).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Plugin sidecar directories cannot contain DLLs; select each plugin or library through dependency resolution.");
                files.Add(new HostedRuntimeFile(Path.Combine(directory, relative),
                    "BepInEx/plugins/" + Path.GetFileName(directory) + "/" + relative.Replace(Path.DirectorySeparatorChar, '/')));
            }
        }
        var selectedGuids = dependencies.Mods.SelectMany(file => PluginMetadata.Read(file.File).Plugins).Select(plugin => plugin.Guid).ToList();
        var serverGuids = dependencies.Mods.Concat(dependencies.Plugins).SelectMany(file => PluginMetadata.Read(file.File).Plugins).Select(plugin => plugin.Guid)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (selectedGuids.Count == 0) throw new InvalidDataException("No deliberately selected server plugin GUID was found: each --mod must declare a BepInEx plugin.");
        if (selectedGuids.Any(guid => guid.Contains(';'))) throw new InvalidDataException("A selected server plugin GUID contains the session list separator.");

        // A pinned copy of the chosen world; only the copy can be changed by this run.
        string worldUid = fixture.Identity.UidText, worldName = fixture.Identity.Name;
        var campaign = new HostedCampaignManifest
        {
            Inventory = inputs.SelectedInventory ?? "", World = Path.Combine(output, "world-source", "worlds_local"), WorldUid = worldUid,
            Join = inputs.Join ?? "127.0.0.1:" + server.GamePort.ToString(CultureInfo.InvariantCulture),
            Server = new HostedCampaignRole { DependencyLock = serverLock, EnvironmentCandidates = [server.Name], LoaderPackage = serverLoader,
                SteamRuntimePackage = steamRuntimePackage, Files = files },
        };
        if (client?.Recipe is { } clientRecipe)
        {
            var clientLock = new NativeDependencyLock { CliFiles = dependencies.CliFiles, CliManifest = dependencies.CliManifest };
            string clientLockFile = Path.Combine(output, "client-dependencies.lock.json");
            clientLock.Write(clientLockFile);
            // This run owns a new store built from the embedded unvisited seed. Hosted staging only copies from that
            // store, so a prior run cannot carry a logout position for a fixture copy that shares the same world UID.
            var store = DefaultSmokeCharacter.Prepare(Path.Combine(output, "character-source"));
            campaign.Clients["client"] = new HostedCampaignRole
            {
                DependencyLock = clientLockFile, EnvironmentCandidates = [clientRecipe.Name], LoaderPackage = clientLoader,
                Architecture = clientArchitecture!,
                Character = new HostedCampaignCharacter
                {
                    Store = store.Root, RegisteredName = DefaultSmokeCharacter.Name,
                    FileName = "vt" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
                },
            };
        }
        string campaignFile = Path.Combine(output, "campaign.json");
        var json = JsonSerializer.SerializeToNode(campaign, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
        if (campaign.Inventory.Length == 0) json.Remove("inventory"); // this machine
        foreach (var role in new[] { json["server"] }.Concat(json["clients"]!.AsObject().Select(pair => pair.Value)).OfType<JsonObject>())
            foreach (string empty in role.Where(pair => pair.Value == null).Select(pair => pair.Key).ToList()) role.Remove(empty);
        File.WriteAllText(campaignFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var plan = new ServerRunPlan
        {
            Scenario = "native-smoke-server-load",
            Executable = ServerRunPlan.ExecutableFor(inputs.Inventory.Hosts[server.Host].Platform switch
            {
                "windows" => ServerPlatform.Windows, "macos" => ServerPlatform.MacOS, _ => ServerPlatform.Linux,
            }),
            Arguments = ["-batchmode", "-nographics", "-name", worldName, "-port", server.GamePort.ToString(CultureInfo.InvariantCulture),
                "-world", worldName, "-password", password, "-public", "0", "-savedir", "{world}", "-logFile", "{runtime}/" + GameLaunch.UnityLogName],
            Environment = new Dictionary<string, string> { [SmokeSessionContract.SelectedGuidsVariable] = string.Join(";", selectedGuids) },
            Port = server.CliPort,
            StartupSeconds = parsed.Options.TryGetValue("--server-startup-seconds", out string? startupSeconds)
                ? int.Parse(startupSeconds, CultureInfo.InvariantCulture) : 300,
            QuitSeconds = bakeDestination == null ? 20 : 300, // A bake must let the final world save at quit finish.
        };
        if (parsed.Options.TryGetValue("--expected-log-error", out string? expectedError))
            plan.LogScan[LogScanner.UnknownError] = new LogClassification { Expected = [expectedError], Reason = parsed.Options["--expected-log-reason"] };
        ClientRunPlan? clientPlan = client == null ? null : new ClientRunPlan
        {
            Mode = "owned", PasswordVariable = SmokeSessionContract.PasswordVariable, Capabilities = [.. ModInputStage.SessionCapabilities],
            Architecture = clientArchitecture!,
            JoinSeconds = parsed.Options.TryGetValue("--join-seconds", out string? seconds)
                ? int.Parse(seconds, CultureInfo.InvariantCulture) : 180,
            // The clean client loads none of the server's plugins.
            Pins = serverGuids.ToDictionary(guid => guid, _ => "absent", StringComparer.Ordinal),
        };
        var clients = new Dictionary<string, ClientRunPlan>(StringComparer.Ordinal);
        if (clientPlan != null) clients["client"] = clientPlan;

        // The read-only host checks, before anything is copied: a client that cannot run is refused with the reason, never dropped.
        var report = await (seams.Inspect ?? (file => HostedCampaignPreparation.InspectAfterLocalPreflightAsync(file,
            HostedTimeouts.Quick, cancellation)))(campaignFile).ConfigureAwait(false);
        foreach (var actor in report.Actors.Where(actor => actor.CharactersDirectory != null))
            Console.WriteLine($"{actor.Name}: characters_local {actor.CharactersDirectory}; Steam userdata {actor.SteamUserDataDirectory}");
        if (!report.Ready)
        {
            var lines = report.Problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}" + LoaderHint(problem, serverLoader, clientLoader) +
                (problem.Actor is "client" or "clients" ? " (--server-only skips the client)" : "")).ToList();
            foreach (string line in lines) Console.Error.WriteLine("REFUSED " + line);
            SmokeOutput.MarkRefused(output, "server-load", lines);
            return 3;
        }
        // Only once the preflight passed: the unbound plans beside campaign.json, for an editable consumer (`valheim-test init server`) to run the same campaign.
        // Private: plan.json holds the server's password.
        File.WriteAllText(Path.Combine(output, "plan.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        if (clientPlan != null)
            File.WriteAllText(Path.Combine(output, "client-plan.json"), JsonSerializer.Serialize(clientPlan, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PREFLIGHT PASSED: the derived campaign is {campaignFile}");
        if (parsed.Switches.Contains("--preflight-only")) return 0;

        string? previousPassword = Environment.GetEnvironmentVariable(SmokeSessionContract.PasswordVariable);
        if (clientPlan != null) Environment.SetEnvironmentVariable(SmokeSessionContract.PasswordVariable, password);
        int result;
        try
        {
            state.Started = true;
            result = await (seams.Campaign ?? PinnedServerRun.RunCampaignAsync)(campaignFile, plan, _ => clients, Path.Combine(output, "evidence"),
                new PinnedServerRunOptions<ServerRunPlan>
                {
                    Name = "native-smoke-server-load",
                    RunId = DetachedSession.ConfiguredRun,
                    CheckpointFixture = DetachedSession.ConfiguredRun != null,
                    CleanupBudget = bakeDestination == null ? null : TimeSpan.FromMinutes(10),
                    ReadPlan = _ => throw new InvalidOperationException("The one-off's plan is in memory."),
                    Mod = new(parsed.Options.GetValueOrDefault("--session-capability") ?? SmokeSessionContract.SessionCapability,
                        parsed.Options.GetValueOrDefault("--session-token-variable") ?? SmokeSessionContract.SessionTokenVariable),
                    Provenance = (_, record) =>
                    {
                        inputs.Record(record);
                        if (adapterSource != null)
                        {
                            record["adapterSourceInputSha256"] = adapterSource.SourceInputSha256;
                            record["adapterSha256"] = adapterSource.AdapterSha256;
                        }
                    },
                    Scenario = serverScenario == null
                        ? (session, plan) => Scenario(session, clientPlan, clock, parsed.Switches.Contains("--hold"), worldUid,
                            bakeDestination != null, parsed.Options.GetValueOrDefault("--before-save-command"), parsed.Options.GetValueOrDefault("--before-save-line"),
                            parsed.Options.GetValueOrDefault("--assert-command"), parsed.Options.GetValueOrDefault("--assert-line"), dependencies)
                        : async (session, plan) =>
                        {
                            await Scenario(session, null, clock, false, worldUid, false, null, null, null, null).ConfigureAwait(false);
                            await serverScenario!.RunAsync(session, new OneShotServerContext
                            { ServerPlan = plan, ClientPlan = clientPlan, WorldUid = worldUid,
                                ValidatedInputs = custom!.ValidatedInputs }).ConfigureAwait(false);
                        },
                }).ConfigureAwait(false);
        }
        finally
        {
            if (clientPlan != null) Environment.SetEnvironmentVariable(SmokeSessionContract.PasswordVariable, previousPassword);
        }
        NativeSmokeLogSummary.WriteReport(Path.Combine(output, "evidence"), seams.SummaryLabel, seams.SummaryOutput);
        if (result == 0 && bakeDestination != null)
        {
            try
            {
                FixtureBake.Export(Path.Combine(output, "evidence"), bakeDestination, fixture, bakedBuild!, cancellation,
                    output, serverInstall, client?.Recipe.Install ?? output);
                Console.WriteLine("Baked fixture: " + bakeDestination);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Console.Error.WriteLine("Fixture export cancelled after the game run; no fixture was published.");
                result = 1;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Fixture export failed after the game run: " + error.Message);
                // The game report is already final: keep the post-run export failure beside it.
                try { File.WriteAllText(Path.Combine(output, "evidence", "fixture-export-failed.txt"), error + "\n"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                result = 1;
            }
        }
        Finish(result, mods.Count, clientPlan != null, clock, output);
        return result;
    }

    // Loaded with the packaged world, joinable, and (with a client) a clean client reading that world.
    private static Task Scenario(GameSession session, ClientRunPlan? client, Stopwatch clock, bool hold, string worldUid, bool bake,
        string? beforeSaveCommand, string? beforeSaveLine, string? assertCommand, string? assertLine, NativeDependencyLock? dependencies = null)
    {
        var server = session.Server!;
        session.Report.Step("selected server mods loaded and world identity matches", () =>
        {
            var worlds = server.Game.Execute("cli_world").Output.Select(Expectations.ParseWorld).OfType<WorldFacts>().ToArray();
            if (worlds.Length != 1 || worlds[0].Uid != worldUid)
                throw new InvalidDataException("The dedicated server did not load the chosen fixture world UID.");
        });
        session.Report.Provenance["firstModLoadedSecondsFromCommand"] = clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        session.Report.Step("dedicated server accepts a game connection", () => server.WaitUntilJoinable(server.Game));
        if (beforeSaveCommand != null)
            session.Report.Step("one pinned mod command completes before the fixture save", () =>
                server.Game.Execute(beforeSaveCommand).RequireLine(beforeSaveLine!, "The mod did not confirm its one-time bake command"));
        if (assertCommand != null)
            session.Report.Step("server observation matches the declared assertion", () =>
                server.Game.Execute(assertCommand).RequireLine(assertLine!, "The server observation did not match the declared assertion"));
        if (client != null)
        {
            ClientSession? opened = null;
            new ClientRounds
            {
                Client = client, WorldUid = worldUid, Report = session.Report, Output = session.Output,
                OwnedServer = server, Rounds = ["first"], ProtectPlayer = false, Cancellation = session.Cancellation,
            }.Run(server.Game, () => opened = session.OpenClient(client), round =>
            {
                round.Step("clean client can read the joined world", () =>
                {
                    var state = new SessionControl(round.Client).Read();
                    if (!state.WorldReady || !state.PlayerReady || state.WorldUid != worldUid)
                        throw new InvalidDataException("The clean client has no ready player in the pinned world.");
                });
                if (hold)
                    round.Step("keep the owned server and client running until finish", () =>
                        // The client's own evidence folder, where its session published any command lease: the hold's hint names it.
                        ForegroundHold.HoldAsync(session.Report.Provenance["runId"],
                            opened?.Output ?? throw new InvalidOperationException("The owned client's evidence folder is missing."),
                            session.Cancellation,
                            [("server", server.CurrentProcess ?? throw new InvalidOperationException("The owned server process is missing.")),
                             ("client", opened?.OwnedProcess ?? throw new InvalidOperationException("The owned client process is missing."))],
                            serverEvidence: session.Output)
                            .GetAwaiter().GetResult());
            });
        }
        else if (hold)
            session.Report.Step("keep the owned server running until finish", () =>
                ForegroundHold.HoldAsync(session.Report.Provenance["runId"], session.Output, session.Cancellation,
                    [("server", server.CurrentProcess ?? throw new InvalidOperationException("The owned server process is missing."))], client: false,
                    serverEvidence: session.Output, checkpoints: DetachedSession.ConfiguredRun != null ? DetachedCheckpoints.For(session, worldUid, dependencies ?? throw new InvalidOperationException("No detached dependency lock.")) : null)
                    .GetAwaiter().GetResult());
        if (bake)
            session.Report.Step("confirm an advancing save before exporting the fixture", () =>
                session.Report.Provenance["bakeSaveNumber"] = new SessionControl(server.Game)
                    .Save(worldUid, TimeSpan.FromSeconds(90)).ToString(CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    private static void Finish(int result, int mods, bool joined, Stopwatch clock, string output) =>
        Console.WriteLine((result == 0 ? (joined ? "SERVER_JOIN_PASS" : "SERVER_LOAD_PASS") : "SERVER_LOAD_FAIL") +
            $": {mods} selected mod(s), {clock.Elapsed.TotalSeconds:F1}s. " +
            (result != 0 ? "See the private evidence for the failed check." :
                joined ? "Clean client joined with selected server mods absent." : "A clean-client join has not run (--server-only).") +
            $" Private evidence ({DiskSpace.Format(DiskSpace.DirectoryBytes(output))}) in {output}");

    internal static bool TryRead(string[] args, out Arguments? parsed, out string error, bool allowImplicitMod = false)
    {
        parsed = null;
        if (!SmokeCommandOptions.TryRead(args, SmokeCommandOptions.Command.ServerLoad, allowImplicitMod, out var read, out error))
            return false;
        parsed = FromParsed(read!);
        return true;
    }

    internal static Arguments FromParsed(SmokeCommandOptions.Parsed read)
    {
        var result = new Arguments();
        foreach (var (key, value) in read.Options) result.Options.Add(key, value);
        result.Mods.AddRange(read.List("--mod"));
        result.Roots.AddRange(read.List("--search-root"));
        result.Configs.AddRange(read.List("--config"));
        result.PluginFiles.AddRange(read.List("--plugin-file"));
        result.PluginDirectories.AddRange(read.List("--plugin-dir"));
        result.Optional.AddRange(read.List("--optional-reference"));
        result.BuildDependencies.AddRange(read.List("--dependency"));
        result.AdapterProperties.AddRange(read.List("--adapter-property"));
        result.Switches.UnionWith(read.Switches);
        return result;
    }
}
