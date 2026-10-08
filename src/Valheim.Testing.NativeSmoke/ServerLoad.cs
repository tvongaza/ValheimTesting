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
    internal const string Usage = "valheim-test server-load --mod DLL [--mod DLL ...] [--output NEW_DIR] [--inventory FILE | --server DIR] [--client DIR] " +
        "[--server-env NAME] [--client-env NAME] [--server-only] [--join HOST:PORT] [--preflight-only] [--hold] [--loader-package FILE] [--client-loader-package FILE] " +
        "[--adapter DLL] [--cli-manifest FILE --cli-files DIR] [--search-root DIR ...] [--config FILE ...] [--plugin-file FILE ...] [--plugin-dir DIR ...] " +
        "[--optional-reference ASSEMBLY ...] [--expected-log-error EXACT_HEADER --expected-log-reason REASON]";
    private static readonly string[] Session = ["valheim.session/state", "valheim.session/join", "valheim.session/leave"];

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
        public HashSet<string> Switches { get; } = new(StringComparer.Ordinal);
        public bool ServerOnly => Switches.Contains("--server-only");
    }

    /// <summary>Test seams: the host preflight and campaign run in place of the real ones.</summary>
    internal sealed record Seams(
        Func<string, Task<CampaignPreflightReport>>? Inspect = null,
        Func<string, ServerRunPlan, Func<ServerRunPlan, IReadOnlyDictionary<string, ClientRunPlan>>, string, PinnedServerRunOptions<ServerRunPlan>, Task<int>>? Campaign = null,
        // The shipped-loader decision reads the real install on this machine, so it is on only for a real run (no seams)
        // and for a test that passes one.
        Func<string, string, ShippedLoader.Choice?>? Loader = null);

    public static async Task<int> RunAsync(string[] args, Seams? seams = null)
    {
        seams ??= new Seams(Loader: ShippedLoader.Instead);
        if (!TryRead(args, out var parsed, out string error))
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
            output = Output(parsed!);
            return await RunCampaignAsync(parsed!, output, clock, seams, state, cancel.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or FormatException or JsonException or OperationCanceledException)
        {
            Console.Error.WriteLine("REFUSED: " + failure.Message);
            // Only before anything was copied or launched, and never into a folder that was itself refused as protected.
            if (state.OutputChecked && !state.Started) MarkRefused(output, [failure.Message]);
            return 3;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    /// <summary>
    /// A refused run's output is marked as such (REFUSED.txt with the reasons), so the inputs a preflight wrote there
    /// (campaign.json, locks, adapter, world and character sources) never look like a prepared run. Nothing was copied to a
    /// host or launched.
    /// </summary>
    internal static void MarkRefused(string? output, IEnumerable<string> reasons)
    {
        if (output == null || !Directory.Exists(output)) return;
        try
        {
            File.WriteAllText(Path.Combine(output, "REFUSED.txt"),
                "This folder is a refused server-load, not a prepared run: nothing was copied to a host or launched. The inputs it wrote are kept for review.\n" +
                string.Concat(reasons.Select(reason => "- " + reason + "\n")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // the refusal itself was printed

    }

    /// <summary>Where a run is: its output checked against the installs it must stay out of, and whether anything was copied or started.</summary>
    internal sealed class RunState
    {
        public bool OutputChecked { get; set; }
        public bool Started { get; set; }
    }

    // A source loader refusal (a mismatched Doorstop pair, an incomplete BepInEx) names the reviewed-package fix for that actor.
    // Not when the actor already runs with a package: then the refusal is the package's own (another platform's loader), and
    // the hint would name the very option that caused it.
    private static string LoaderHint(CampaignPreflightProblem problem, string? serverLoader, string? clientLoader) =>
        problem.Input != "game and loader" ? "" // an unreadable --loader-package is the "loader" input
            : problem.Actor == "server" && serverLoader == null ? " (--loader-package FILE gives the server's disposable copy a reviewed loader)"
            : problem.Actor == "client" && clientLoader == null ? " (--client-loader-package FILE gives the client's disposable copy a reviewed loader)" : "";
    // --output, or a new timestamped directory under ./valheim-test-runs. Never an existing one.
    private static string Output(Arguments parsed)
    {
        string output = parsed.Options.TryGetValue("--output", out string? given) ? Path.GetFullPath(given)
            : Path.GetFullPath(Path.Combine("valheim-test-runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z"));
        if (Path.Exists(output)) throw new IOException("--output must be new; existing evidence will not be overwritten: " + output);
        return output;
    }

    /// <summary>The environments a one-off runs on, chosen from the inventory with the reason for each.</summary>
    internal sealed record Choice(EnvironmentInventory Inventory, string? InventoryFile, EnvironmentRecipe Server, string ServerReason,
        EnvironmentRecipe? Client, string? ClientReason, string? Join);

    /// <summary>
    /// The inventory (the file, this machine with <c>--server</c>/<c>--client</c> written as its override into
    /// <paramref name="output"/>, or this machine), its first server environment and, unless <c>--server-only</c>, its first
    /// client environment, or the ones named. The server must be on this machine: its install is what the mod is resolved
    /// and its adapter built against.
    /// </summary>
    internal static Choice Choose(Arguments parsed, string output)
    {
        string? file = parsed.Options.TryGetValue("--inventory", out string? named) ? Path.GetFullPath(named) : null;
        bool overrides = parsed.Options.ContainsKey("--server") || parsed.Options.ContainsKey("--client");
        if (file != null && overrides)
            throw new ArgumentException("--server and --client override this machine's environments; with --inventory, list the installs in the file.");
        if (overrides)
        {
            SmokeOutput.RefuseInside(output, new[] { "--server", "--client" }.Where(parsed.Options.ContainsKey).Select(key => parsed.Options[key]).ToArray());
            var environments = new JsonArray();
            JsonObject Entry(string name, string role, string? install)
            {
                var entry = new JsonObject { ["name"] = name, ["roles"] = new JsonArray(role) };
                if (install != null) entry["install"] = install;
                return entry;
            }
            environments.Add(Entry("local-server", "server", parsed.Options.TryGetValue("--server", out string? server) ? Path.GetFullPath(server) : null));
            if (!parsed.ServerOnly)
                environments.Add(Entry("local-client", "client", parsed.Options.TryGetValue("--client", out string? client) ? Path.GetFullPath(client) : null));
            Directory.CreateDirectory(output);
            file = Path.Combine(output, "environments.json");
            File.WriteAllText(file, new JsonObject { ["environments"] = environments }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        EnvironmentInventory inventory;
        try { inventory = EnvironmentInventory.Read(file); }
        catch (ArgumentException failure) when (file == null || overrides)
        {
            // This machine's environments: say how to go on without what was not found.
            throw new ArgumentException(failure.Message +
                (failure.Message.Contains("892970", StringComparison.Ordinal) && !parsed.ServerOnly ? " Give --client DIR, or --server-only to skip the client's join." : "") +
                (failure.Message.Contains("896660", StringComparison.Ordinal) ? " Or run `valheim-test start` for a hosted local world without a dedicated server." : ""), failure);
        }
        string where = file == null ? "this machine" : file;

        EnvironmentRecipe Pick(string role, string option, string hint)
        {
            if (parsed.Options.TryGetValue(option, out string? name))
                return inventory.Environments.FirstOrDefault(recipe => recipe.Name == name && recipe.Roles.Contains(role))
                    ?? throw new ArgumentException($"{option} {name}: the inventory ({where}) has no {role} environment of that name.");
            return inventory.Environments.FirstOrDefault(recipe => recipe.Roles.Contains(role))
                ?? throw new ArgumentException($"The inventory ({where}) has no {role} environment. " +
                    (inventory.Missing.Count == 0 ? "" : string.Join(" ", inventory.Missing) + " ") + hint);
        }
        var serverRecipe = Pick("server", "--server-env", "Install Valheim Dedicated Server from Steam (it is free), give --server DIR, or run `valheim-test start` for a hosted local world.");
        if (inventory.Hosts[serverRecipe.Host].Kind != "local")
            throw new ArgumentException($"Server environment {serverRecipe.Name} is on {serverRecipe.Host}, not this machine. server-load resolves the mod and builds its adapter " +
                "against the server's own install, so its server runs here; run a server elsewhere as a campaign (PinnedServerRun.RunCampaignAsync) or with PinnedServerRun --inventory.");
        string serverReason = parsed.Options.ContainsKey("--server-env") ? "named by --server-env" : "first server environment in inventory order";
        if (parsed.ServerOnly) return new Choice(inventory, file, serverRecipe, serverReason, null, null, null);

        var clientRecipe = Pick("client", "--client-env", "Give --client DIR, add a client environment, or use --server-only to skip the client's join.");
        string clientReason = parsed.Options.ContainsKey("--client-env") ? "named by --client-env" : "default; --server-only to skip";
        if (parsed.Options.TryGetValue("--join", out string? joinOption) &&
            !joinOption.EndsWith(":" + serverRecipe.GamePort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new ArgumentException($"--join {joinOption}: the server listens on game port {serverRecipe.GamePort}; give HOST:{serverRecipe.GamePort}.");
        string join = parsed.Options.TryGetValue("--join", out string? given) ? given
            : clientRecipe.Host == serverRecipe.Host ? "127.0.0.1:" + serverRecipe.GamePort.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException($"Client environment {clientRecipe.Name} is on {clientRecipe.Host}, another machine than the server: give --join HOST:{serverRecipe.GamePort} " +
                "with this machine's address as that host reaches it, or --server-only.");
        return new Choice(inventory, file, serverRecipe, serverReason, clientRecipe, clientReason, join);
    }

    private static async Task<int> RunCampaignAsync(Arguments parsed, string output, Stopwatch clock, Seams seams, RunState state, CancellationToken cancellation)
    {
        var choice = Choose(parsed, output);
        foreach (string line in choice.Inventory.Detected) Console.WriteLine("detected: " + line);
        var server = choice.Server;
        Console.WriteLine($"server: {server.Name} on {server.Host} ({choice.ServerReason}): install {server.Install}; ValheimCLI port {server.CliPort}, game port {server.GamePort}");
        Console.WriteLine(choice.Client is { } chosen
            ? $"client: {chosen.Name} on {chosen.Host} ({choice.ClientReason}): install {chosen.Install}; ValheimCLI port {chosen.CliPort}; joins {choice.Join}"
            : "client: none (--server-only)");

        string serverInstall = server.Install;
        // A loader package given, or the chosen environment's own (which the campaign applies too); else the install's BepInEx.
        var serverLoader = parsed.Options.TryGetValue("--loader-package", out string? serverLoaderFile) ? Path.GetFullPath(serverLoaderFile) : server.LoaderPackage;
        var clientLoader = parsed.Options.TryGetValue("--client-loader-package", out string? clientLoaderFile) ? Path.GetFullPath(clientLoaderFile) : choice.Client?.LoaderPackage;
        // An install on this machine whose own Doorstop proxy and configuration do not match (a mod manager swapped the proxy)
        // gets the BepInExPack this tool ships in its disposable copy, with one printed line; every other loader fault still refuses.
        var shipped = seams.Loader ?? ((_, _) => null);
        var serverAuto = serverLoader == null ? shipped("server", serverInstall) : null;
        var clientAuto = clientLoader == null && choice.Client is { } localClient && choice.Inventory.Hosts[localClient.Host].Kind == "local"
            ? shipped("client", localClient.Install) : null;
        serverLoader ??= serverAuto?.Manifest;
        clientLoader ??= clientAuto?.Manifest;
        string core = Path.Combine(serverLoader == null ? serverInstall : BepInExLoaderPackage.Read(serverLoader).Root, InstallPins.CoreDirectory);
        if (!Directory.Exists(core))
            throw new DirectoryNotFoundException($"The server install {serverInstall} has no BepInEx ({InstallPins.CoreDirectory}). Install BepInExPack_Valheim into it, " +
                "or give --loader-package with a reviewed loader package; the mod is resolved against that core.");
        var (cliManifest, cliFiles) = SmokeInputs.Cli(parsed.Options);
        string? adapter = parsed.Options.TryGetValue("--adapter", out string? adapterFile) ? Path.GetFullPath(adapterFile) : null;
        foreach (string path in new[] { adapter, cliManifest }.OfType<string>().Concat(parsed.Mods.Select(Path.GetFullPath)))
            if (!File.Exists(path)) throw new FileNotFoundException("A selected file is missing: " + path, path);
        SmokeOutput.RefuseInside(output, new[] { serverInstall, cliFiles, choice.Client?.Install }.OfType<string>().ToArray());
        state.OutputChecked = true;

        var dependencies = NativeDependencyResolver.Resolve(new NativeDependencyRequest
        {
            Mods = parsed.Mods.Select(Path.GetFullPath).ToList(), SearchRoots = parsed.Roots.Select(Path.GetFullPath).ToList(),
            GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(serverInstall))!, BepInExCore = core,
            CliManifest = cliManifest, CliFiles = cliFiles,
            Capabilities = choice.Client == null ? ["valheim.session/state"] : [.. Session],
            OptionalReferences = [.. parsed.Optional],
        });
        Directory.CreateDirectory(output);
        string serverLock = Path.Combine(output, "dependencies.lock.json");
        dependencies.Write(serverLock);
        if (!dependencies.Ready)
            throw new InvalidDataException("Dependency choices remain: " + string.Join("; ", dependencies.Gaps.Select(gap => gap.Kind + " " + gap.Name + ": " + gap.Reason)));
        adapter ??= await SmokeAdapter.BuildAsync(serverInstall, dependencies, output, cancellation, core).ConfigureAwait(false);

        // The server's own files beside its lock: the session adapter, explicit configs and plugin sidecars.
        var files = new List<HostedRuntimeFile> { new(adapter, "BepInEx/plugins/" + Path.GetFileName(adapter)) };
        foreach (string config in parsed.Configs.Select(Path.GetFullPath))
        {
            if (Path.GetFileName(config).Equals("valheimCLI.valheimCLI.cfg", StringComparison.OrdinalIgnoreCase))
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

        // The packaged world and, for the client, the packaged disposable character under a fresh file name.
        string worldRoot = Path.Combine(output, "world-source");
        DefaultSmokeWorld.PrepareServerSaveRoot(worldRoot);
        var campaign = new HostedCampaignManifest
        {
            Inventory = choice.InventoryFile ?? "", World = Path.Combine(worldRoot, "worlds_local"), WorldUid = DefaultSmokeWorld.Uid,
            Join = choice.Join ?? "127.0.0.1:" + server.GamePort.ToString(CultureInfo.InvariantCulture),
            Server = new HostedCampaignRole { DependencyLock = serverLock, EnvironmentCandidates = [server.Name], LoaderPackage = serverLoader, Files = files },
        };
        if (choice.Client is { } clientRecipe)
        {
            var clientLock = new NativeDependencyLock { CliFiles = dependencies.CliFiles, CliManifest = dependencies.CliManifest };
            string clientLockFile = Path.Combine(output, "client-dependencies.lock.json");
            clientLock.Write(clientLockFile);
            var store = DefaultSmokeCharacter.Prepare(Path.Combine(output, "character-source"));
            campaign.Clients["client"] = new HostedCampaignRole
            {
                DependencyLock = clientLockFile, EnvironmentCandidates = [clientRecipe.Name], LoaderPackage = clientLoader,
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
            Executable = ServerRunPlan.ExecutableFor(choice.Inventory.Hosts[server.Host].Platform switch
            {
                "windows" => ServerPlatform.Windows, "macos" => ServerPlatform.MacOS, _ => ServerPlatform.Linux,
            }),
            Arguments = ["-batchmode", "-nographics", "-name", DefaultSmokeWorld.Name, "-port", server.GamePort.ToString(CultureInfo.InvariantCulture),
                "-world", DefaultSmokeWorld.Name, "-password", password, "-public", "0", "-savedir", "{world}", "-logFile", "{runtime}/toolkit-unity.log"],
            Environment = new Dictionary<string, string> { [SmokeSessionContract.SelectedGuidsVariable] = string.Join(";", selectedGuids) },
            Port = server.CliPort,
            QuitSeconds = 20, // a disposable load smoke: no save-on-quit or crossplay retirement is asserted
        };
        if (parsed.Options.TryGetValue("--expected-log-error", out string? expectedError))
            plan.LogScan[LogScanner.UnknownError] = new LogClassification { Expected = [expectedError], Reason = parsed.Options["--expected-log-reason"] };
        ClientRunPlan? clientPlan = choice.Client == null ? null : new ClientRunPlan
        {
            Mode = "owned", PasswordVariable = SmokeSessionContract.PasswordVariable, Capabilities = [.. Session],
            // The clean client loads none of the server's plugins.
            Pins = serverGuids.ToDictionary(guid => guid, _ => "absent", StringComparer.Ordinal),
        };
        var clients = new Dictionary<string, ClientRunPlan>(StringComparer.Ordinal);
        if (clientPlan != null) clients["client"] = clientPlan;

        // The read-only host checks, before anything is copied: a client that cannot run is refused with the reason, never dropped.
        var report = await (seams.Inspect ?? (file => HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(60), cancellation: cancellation)))(campaignFile).ConfigureAwait(false);
        foreach (var actor in report.Actors.Where(actor => actor.CharactersDirectory != null))
            Console.WriteLine($"{actor.Name}: characters_local {actor.CharactersDirectory}; Steam userdata {actor.SteamUserDataDirectory}");
        if (!report.Ready)
        {
            var lines = report.Problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}" + LoaderHint(problem, serverLoader, clientLoader) +
                (problem.Actor is "client" or "clients" ? " (--server-only skips the client)" : "")).ToList();
            foreach (string line in lines) Console.Error.WriteLine("REFUSED " + line);
            MarkRefused(output, lines);
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
                    ReadPlan = _ => throw new InvalidOperationException("The one-off's plan is in memory."),
                    Mod = new(SmokeSessionContract.SessionCapability, SmokeSessionContract.SessionTokenVariable),
                    Provenance = (_, record) =>
                    {
                        if (serverLoader != null) record["serverLoaderPackage"] = BepInExLoaderPackage.Read(serverLoader).Identity;
                        if (clientLoader != null) record["clientLoaderPackage"] = BepInExLoaderPackage.Read(clientLoader).Identity;
                        if (serverAuto != null) record["serverLoaderShipped"] = serverAuto.Reason;
                        if (clientAuto != null) record["clientLoaderShipped"] = clientAuto.Reason;
                    },
                    Scenario = (session, plan) => Scenario(session, clientPlan, clock, parsed.Switches.Contains("--hold")),
                }).ConfigureAwait(false);
        }
        finally
        {
            if (clientPlan != null) Environment.SetEnvironmentVariable(SmokeSessionContract.PasswordVariable, previousPassword);
        }
        Finish(result, parsed.Mods.Count, clientPlan != null, clock, output);
        return result;
    }

    // Loaded with the packaged world, joinable, and (with a client) a clean client reading that world.
    private static Task Scenario(GameSession session, ClientRunPlan? client, Stopwatch clock, bool hold)
    {
        var server = session.Server!;
        session.Report.Step("selected server mods loaded and world identity matches", () =>
        {
            var worlds = server.Game.Execute("cli_world").Output.Select(Expectations.ParseWorld).OfType<WorldFacts>().ToArray();
            if (worlds.Length != 1 || worlds[0].Uid != DefaultSmokeWorld.Uid)
                throw new InvalidDataException("The dedicated server did not load the packaged smoke world UID.");
        });
        session.Report.Provenance["firstModLoadedSecondsFromCommand"] = clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        session.Report.Step("dedicated server accepts a game connection", () => server.WaitUntilJoinable(server.Game));
        if (client != null)
        {
            ClientSession? opened = null;
            new ClientRounds
            {
                Client = client, WorldUid = DefaultSmokeWorld.Uid, Report = session.Report, Output = session.Output,
                OwnedServer = server, Rounds = ["first"], ProtectPlayer = false, Cancellation = session.Cancellation,
            }.Run(server.Game, () => opened = session.OpenClient(client), round =>
            {
                round.Step("clean client can read the joined world", () =>
                {
                    var state = new SessionControl(round.Client).Read();
                    if (!state.WorldReady || !state.PlayerReady || state.WorldUid != DefaultSmokeWorld.Uid)
                        throw new InvalidDataException("The clean client has no ready player in the pinned world.");
                });
                if (hold)
                    round.Step("keep the owned server and client running until finish", () =>
                        ForegroundHold.HoldAsync(session.Report.Provenance["runId"], GameSession.ActorOutput(session.Output, "client"),
                            session.Cancellation,
                            [("server", server.CurrentProcess ?? throw new InvalidOperationException("The owned server process is missing.")),
                             ("client", opened?.OwnedProcess ?? throw new InvalidOperationException("The owned client process is missing."))])
                            .GetAwaiter().GetResult());
            });
        }
        else if (hold)
            session.Report.Step("keep the owned server running until finish", () =>
                ForegroundHold.HoldAsync(session.Report.Provenance["runId"], session.Output, session.Cancellation,
                    [("server", server.CurrentProcess ?? throw new InvalidOperationException("The owned server process is missing."))], client: false)
                    .GetAwaiter().GetResult());
        return Task.CompletedTask;
    }

    private static void Finish(int result, int mods, bool joined, Stopwatch clock, string output) =>
        Console.WriteLine((result == 0 ? (joined ? "SERVER_JOIN_PASS" : "SERVER_LOAD_PASS") : "SERVER_LOAD_FAIL") +
            $": {mods} selected mod(s), {clock.Elapsed.TotalSeconds:F1}s. " +
            (result != 0 ? "See the private evidence for the failed check." :
                joined ? "Clean client joined with selected server mods absent." : "A clean-client join has not run (--server-only).") +
            $" Private evidence ({DiskSpace.Format(DiskSpace.DirectoryBytes(output))}) in {output}");

    private static readonly HashSet<string> Single = new(StringComparer.Ordinal)
    {
        "--output", "--inventory", "--server", "--client", "--server-env", "--client-env", "--join", "--adapter", "--cli-manifest", "--cli-files",
        "--loader-package", "--client-loader-package", "--expected-log-error", "--expected-log-reason",
    };
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--server-only", "--preflight-only", "--hold" };

    internal static bool TryRead(string[] args, out Arguments? parsed, out string error)
    {
        parsed = null; error = "";
        var result = new Arguments();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (Flags.Contains(key))
            {
                if (!result.Switches.Add(key)) { error = "Repeated option: " + key; return false; }
                continue;
            }
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) { error = "Option needs a value: " + key; return false; }
            string value = args[++i];
            var list = key switch
            {
                "--mod" => result.Mods, "--search-root" => result.Roots, "--config" => result.Configs, "--plugin-file" => result.PluginFiles,
                "--plugin-dir" => result.PluginDirectories, "--optional-reference" => result.Optional, _ => null,
            };
            if (list != null) list.Add(value);
            else if (!Single.Contains(key) || !result.Options.TryAdd(key, value)) { error = "Unknown or repeated option: " + key; return false; }
        }
        if (result.Mods.Count == 0) { error = "Missing: --mod"; return false; }
        if (result.Options.ContainsKey("--expected-log-error") != result.Options.ContainsKey("--expected-log-reason"))
        { error = "An expected log error needs its full header and a written reason."; return false; }
        if (result.ServerOnly && (result.Options.ContainsKey("--client") || result.Options.ContainsKey("--client-env") ||
            result.Options.ContainsKey("--client-loader-package") || result.Options.ContainsKey("--join")))
        { error = "--server-only runs no client: leave out --client, --client-env, --client-loader-package and --join."; return false; }
        parsed = result;
        return true;
    }
}
