using System.Globalization;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>
/// The one mod-input stage of <c>build</c>, <c>start</c> and <c>server-load</c> (#673). A command parses its options and calls
/// <see cref="RunAsync"/>; this owns the whole sequence, in one order for every command:
/// <list type="number">
/// <item>the game and loader choice: the environments from the inventory (#546), each actor's loader and its core, the client's slice;</item>
/// <item>the output guard against every selected install, loader and account folder, before the first write;</item>
/// <item>the inputs given ready-made (the selected mod and its build inputs, a scenario DLL and its input check, #647), before any host is probed (#626);</item>
/// <item>the local host preflight, before any build;</item>
/// <item>building the mod (<c>--project</c>; <c>build</c> stops here, as it launches nothing) and the scenario, with the same checks;</item>
/// <item>the frozen environment choice, the dependency resolution and the provenance keys (<see cref="Inputs.Record"/>).</item>
/// </list>
/// The mod compiles and resolves against one actor: the hosting client for <c>start</c>, the dedicated server for
/// <c>server-load</c>, and the <c>--role</c> for <c>build</c>. Its third-party compile references come from that same loader
/// choice and the explicit <c>--dependency</c> and <c>--search-root</c> inputs, never from the game folder.
/// </summary>
internal static class ModInputStage
{
    internal enum Route { Build, Hosted, Dedicated }

    internal static readonly IReadOnlyList<string> SessionCapabilities = ["valheim.session/state", "valheim.session/join", "valheim.session/leave"];

    /// <summary>Whether the output was checked against the selected sources, so a refusal may be written into it.</summary>
    internal class Progress
    {
        public bool OutputChecked { get; set; }
    }

    /// <summary>A command's parsed inputs, and the test seams for what reads the real machine.</summary>
    internal sealed record Request(Route Route, string Output, IReadOnlyDictionary<string, string> Options, IReadOnlySet<string> Switches,
        IReadOnlyList<string> Mods, IReadOnlyList<string> SearchRoots, IReadOnlyList<string> OptionalReferences, IReadOnlyList<string> Dependencies)
    {
        /// <summary>The pinned-loader rule for an actor that names no package (<see cref="ShippedLoader.Choose"/> in a real run).</summary>
        public ShippedLoader.Rule? Shipped { get; init; }
        /// <summary>An A/B arm's server loader, chosen once before either arm.</summary>
        public ShippedLoader.Choice? FrozenServerLoader { get; init; }
        public Action<EnvironmentInventory, EnvironmentRecipe, string, string?>? ClientArchitecture { get; init; }
        public Progress Progress { get; init; } = new();
    }

    /// <summary>The environments a one-off runs on, chosen from the inventory with the reason for each.</summary>
    internal sealed record Choice(EnvironmentInventory Inventory, string? InventoryFile, EnvironmentRecipe? Server, string? ServerReason,
        EnvironmentRecipe? Client, string? ClientReason, string? Join);

    /// <summary>One chosen actor and its loader: a package (given, the environment's, or the pinned one), else (macOS) its install's own.</summary>
    internal sealed record Actor(string Role, EnvironmentRecipe Recipe, string Reason, string? Loader, ShippedLoader.Choice? Shipped, string? LoaderRoot);

    internal sealed record Scenario(object Runner, string Name, string File, string Sha256, object? ValidatedInputs);

    /// <summary>What the stage decided. A <see cref="Route.Build"/> result stops after the mod build.</summary>
    internal sealed record Inputs(Route Route, EnvironmentInventory Inventory, Actor Compile, Actor? Server, Actor? Client,
        string? ClientArchitecture, string? Join, IReadOnlyList<string> Mods, string ModSelection, string? BuildInputs)
    {
        public string Core { get; init; } = "";
        public string CliManifest { get; init; } = "";
        public string CliFiles { get; init; } = "";
        public IReadOnlyList<string> SearchRoots { get; init; } = [];
        public IReadOnlyList<string> Capabilities { get; init; } = [];
        public Scenario? Scenario { get; init; }
        public string? SelectedInventory { get; init; }
        public string? SteamRuntimePackage { get; init; }
        public NativeDependencyLock? Dependencies { get; init; }

        public IEnumerable<Actor> Actors => new[] { Server, Client }.OfType<Actor>();

        /// <summary>The stage's provenance keys: each actor's loader, the mod selection and the scenario.</summary>
        public void Record(IDictionary<string, string> provenance)
        {
            foreach (var actor in Actors) SmokeInputResolver.RecordLoader(provenance, actor.Role, actor.Loader, actor.Shipped);
            provenance["modSelection"] = ModSelection;
            if (Scenario != null)
            {
                provenance["oneShotScenario"] = Scenario.Name;
                provenance["oneShotScenarioSha256"] = Scenario.Sha256;
            }
        }
    }

    public static async Task<Inputs> RunAsync(Request request, CancellationToken cancellation)
    {
        var options = request.Options;
        string output = request.Output;
        var given = ModBuild.ParseDependencies(request.Dependencies);

        // 1. The game and loader choice.
        string compileRole = request.Route switch
        {
            Route.Hosted => "client", Route.Dedicated => "server",
            _ => options.GetValueOrDefault("--role") ?? "client",
        };
        var choice = request.Route == Route.Dedicated || compileRole == "server"
            ? ChooseDedicated(options, serverOnly: request.Route == Route.Build || request.Switches.Contains("--server-only"), output,
                request.Route == Route.Build ? "build" : "server-load")
            : ChooseHosted(options, output, request.Route == Route.Build ? "build" : "start");
        var inventory = choice.Inventory;
        Actor? server = choice.Server is { } serverRecipe ? Select(request, inventory, "server", serverRecipe, choice.ServerReason!) : null;
        if (server != null && request.FrozenServerLoader is { } frozen)
        {
            if (server.Loader == null || !Path.GetFullPath(server.Loader).Equals(Path.GetFullPath(frozen.Manifest),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("The A/B arm's pinned server loader differs from the package chosen before either arm started.");
            server = server with { Shipped = frozen };
        }
        Actor? client = choice.Client is { } clientRecipe ? Select(request, inventory, "client", clientRecipe, choice.ClientReason!) : null;
        var compile = (compileRole == "server" ? server : client)!;
        string game = compile.Recipe.Install;
        // The core the mod compiles and resolves against: the chosen package's, else the install's own. A vanilla install
        // (a profile run leaves it so) needs a package, given or named as the environment's loaderPackage.
        string core = Path.Combine(compile.LoaderRoot ?? game, InstallPins.CoreDirectory);
        if (!Directory.Exists(core))
            throw new DirectoryNotFoundException($"The {compile.Role} install {game} has no BepInEx ({InstallPins.CoreDirectory}) and no loader package was chosen. " +
                $"Give {LoaderOption(compile.Role)} FILE with a reviewed loader package, name one as environment {compile.Recipe.Name}'s loaderPackage, or install BepInExPack_Valheim into it; the mod is compiled and resolved against that core.");
        // A Mac dedicated server's Steam runtime libraries are part of the server's runtime choice.
        string? steamRuntime = options.TryGetValue("--steam-runtime-package", out string? steam) ? Path.GetFullPath(steam) : null;
        if (steamRuntime != null)
        {
            if (server == null || inventory.Hosts[server.Recipe.Host].Platform != "macos")
                throw new InvalidDataException("--steam-runtime-package requires a Mac dedicated server profile.");
            _ = MacSteamRuntimePackage.Read(steamRuntime);
        }
        // The slice a launched client runs; a build launches nothing.
        string? architecture = null;
        if (client != null && request.Route != Route.Build)
        {
            architecture = SmokeInputResolver.SelectClientArchitecture(options.GetValueOrDefault("--client-architecture"), client.Recipe, inventory);
            if (request.ClientArchitecture is { } check) check(inventory, client.Recipe, architecture, client.Loader);
            else SmokeInputResolver.RequireClientArchitecture(inventory, client.Recipe, architecture, client.Loader);
        }
        foreach (string line in inventory.Detected) Console.WriteLine("detected: " + line);
        if (server != null)
            Console.WriteLine($"server: {server.Recipe.Name} on {server.Recipe.Host} ({server.Reason}): install {server.Recipe.Install}; ValheimCLI port {server.Recipe.CliPort}, game port {server.Recipe.GamePort}");
        if (client != null)
            Console.WriteLine($"client: {client.Recipe.Name} on {client.Recipe.Host} ({client.Reason}): install {client.Recipe.Install}; ValheimCLI port {client.Recipe.CliPort}" +
                (choice.Join == null ? "" : "; joins " + choice.Join) + (architecture == null ? "" : "; architecture " + architecture));
        else if (request.Route == Route.Dedicated) Console.WriteLine("client: none (--server-only)");

        // 2. Every output is outside every selected source before anything is written.
        string? cliManifest = null, cliFiles = null;
        if (request.Route != Route.Build) (cliManifest, cliFiles) = SmokeInputs.Cli(options);
        SmokeOutput.RefuseResolved(output, cliFiles, inventory, [server?.Recipe, client?.Recipe], server?.LoaderRoot, client?.LoaderRoot);
        if (options.TryGetValue("--bake-fixture", out string? bake))
            FixtureBake.RefuseOutput(Path.GetFullPath(bake), new[] { output, server?.Recipe.Install, client?.Recipe.Install,
                options.TryGetValue("--world-fixture", out string? fixture) ? Path.GetFullPath(fixture) : null }.OfType<string>().ToArray());
        request.Progress.OutputChecked = true;

        // 3. The inputs that already exist are checked before any host is probed, so an unrelated slow or transient desktop
        // or Steam check cannot mask a pure input error (#626): the selected mod and its build inputs, the scenario DLL.
        var mods = new List<string>();
        string selection = "";
        string? buildInputs = options.GetValueOrDefault("--build-inputs");
        var roots = request.SearchRoots.ToList();
        ModBuild.BuildRecord? verified = null;
        Scenario? scenario = null;
        bool buildsMod = options.ContainsKey("--project");
        if (request.Route != Route.Build)
        {
            foreach (var (name, path) in new[] { ("game", game), ("--cli-files", cliFiles!) })
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException(name + " directory does not exist: " + path);
            if (!File.Exists(cliManifest)) throw new FileNotFoundException("--cli-manifest file does not exist: " + cliManifest, cliManifest);
            if (!buildsMod)
            {
                var selected = SmokeModInput.Select(request.Mods, Environment.CurrentDirectory);
                mods.AddRange(selected.Mods);
                selection = selected.Reason;
                Console.WriteLine("mod selection: " + selection);
                verified = CheckMod(mods, buildInputs, game, core, roots);
            }
            if (options.TryGetValue("--scenario", out string? scenarioFile))
                scenario = LoadScenario(request.Route, scenarioFile, output, client != null);
        }

        // 4. The read-only host checks, before any build: a run that cannot start is refused before minutes of compiling.
        var inputs = new Inputs(request.Route, inventory, compile, server, client, architecture, choice.Join, mods, selection, buildInputs)
            { Core = core, SteamRuntimePackage = steamRuntime };
        if (request.Route != Route.Build)
        {
            var problems = await LocalHostPreflight.InspectAsync(inventory,
                inputs.Actors.Select(actor => new LocalHostPreflight.Actor(actor.Role, actor.Recipe)),
                TimeSpan.FromSeconds(60), cancellation).ConfigureAwait(false);
            if (problems.Count != 0)
                throw new InvalidOperationException(string.Join("; ", problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}")));
        }

        // 5. The builds, against the compile actor's install and loader; each output gets the same checks as a given one.
        if (buildsMod)
        {
            string project = options["--project"];
            var plan = ModBuild.Plan(project, game, given, Path.Combine(output, "build"), compile.LoaderRoot, request.SearchRoots);
            var record = await ModBuild.BuildAsync(plan, cancellation).ConfigureAwait(false);
            mods.Add(record.Mod);
            buildInputs = Path.Combine(plan.Output, "build-inputs.json");
            selection = "built from --project " + Path.GetFullPath(project);
            Console.WriteLine("built mod: " + record.Mod);
            Console.WriteLine("mod selection: " + selection);
            inputs = inputs with { ModSelection = selection, BuildInputs = buildInputs };
            if (request.Route == Route.Build) return inputs;
            verified = CheckMod(mods, buildInputs, game, core, roots);
        }
        else if (request.Route == Route.Build) throw new ArgumentException("Give --project MOD.csproj.");
        if (options.TryGetValue("--scenario-project", out string? scenarioProject))
            scenario = LoadScenario(request.Route, await OneShotScenario.BuildAsync(scenarioProject, Path.Combine(output, "scenario-build"), cancellation)
                .ConfigureAwait(false), output, client != null);

        // 6. The chosen set frozen beside the run (the later run reads it, even if an inventory file or Steam detection
        // changes before it), then the dependency lock.
        string selectedInventory = request.Route == Route.Hosted
            ? RecordHostedClient(inventory, client!, output)
            : SmokeInputResolver.RecordSelected(inventory, output, inputs.Actors.Select(actor => actor.Recipe));
        IReadOnlyList<string> capabilities = request.Route == Route.Hosted ? CliCapabilities.HostedRounds
            : client == null ? [SessionCapabilities[0]] : SessionCapabilities;
        var dependencies = NativeDependencyResolver.Resolve(SmokeDependencyInputs.Request(mods, game, core, cliManifest!, cliFiles!,
            roots, request.OptionalReferences, capabilities));
        Directory.CreateDirectory(output);
        if (buildInputs != null) File.Copy(buildInputs, Path.Combine(output, "build-inputs.json"));
        dependencies.Write(Path.Combine(output, "dependencies.lock.json"));
        if (!dependencies.Ready)
            throw new InvalidDataException("Dependency choices remain: " + SmokeDependencyInputs.Gaps(dependencies));
        if (verified != null) ModBuild.RequireResolvedDependencies(verified, dependencies);
        return inputs with
        {
            CliManifest = cliManifest!, CliFiles = cliFiles!, SearchRoots = roots, Capabilities = capabilities.ToList(),
            Scenario = scenario, SelectedInventory = selectedInventory, Dependencies = dependencies,
        };
    }

    // The selected mod files exist, and recorded build inputs still match this game and the chosen loader; their pinned
    // third-party files become exact search roots.
    private static ModBuild.BuildRecord? CheckMod(IReadOnlyList<string> mods, string? buildInputs, string game, string core, List<string> roots)
    {
        foreach (string mod in mods)
            if (!File.Exists(mod)) throw new FileNotFoundException("--mod file does not exist: " + mod, mod);
        if (buildInputs == null) return null;
        if (mods.Count != 1) throw new ArgumentException("--build-inputs requires exactly one --mod DLL.");
        var verified = ModBuild.Verify(buildInputs, game, mods[0]);
        roots.AddRange(ModBuild.ThirdPartyRoots(verified));
        Console.WriteLine("build inputs verified: " + buildInputs);
        ModBuild.RequireLoaderMatches(verified, core);
        return verified;
    }

    // The scenario and its own input check (#647). Its need for a client must match the run's.
    private static Scenario LoadScenario(Route route, string file, string output, bool withClient)
    {
        Scenario scenario;
        if (route == Route.Hosted)
        {
            var hosted = OneShotScenario.LoadHosted(file, output);
            scenario = new Scenario(hosted.Runner, hosted.Runner.Name, hosted.File, hosted.Sha256, null);
        }
        else
        {
            var dedicated = OneShotScenario.Load(file, output);
            if (dedicated.Runner.RequiresClient != withClient)
                throw new ArgumentException(dedicated.Runner.RequiresClient
                    ? $"Scenario {dedicated.Runner.Name} needs the clean client; leave out --server-only."
                    : $"Scenario {dedicated.Runner.Name} uses only the server; give --server-only.");
            scenario = new Scenario(dedicated.Runner, dedicated.Runner.Name, dedicated.File, dedicated.Sha256, null);
        }
        return scenario with { ValidatedInputs = OneShotScenario.ValidateInputs(scenario.Runner, scenario.Name) };
    }

    private static string LoaderOption(string role) => role == "server" ? "--loader-package" : "--client-loader-package";

    // A role's loader: its loader option, else the environment's package, else the pinned pack on a Windows or Linux host (#711).
    private static Actor Select(Request request, EnvironmentInventory inventory, string role, EnvironmentRecipe recipe, string reason)
    {
        var (manifest, shipped) = SmokeInputResolver.Loader(role, recipe, inventory.Hosts[recipe.Host].Platform,
            request.Options.GetValueOrDefault(LoaderOption(role)), request.Shipped);
        if (manifest != null && !File.Exists(manifest))
            throw new FileNotFoundException($"The {role} loader package does not exist: {manifest}", manifest);
        if (shipped != null) ShippedLoader.Print(role, shipped);
        return new Actor(role, recipe, reason, manifest, shipped, manifest == null ? null : BepInExLoaderPackage.Read(manifest).Root);
    }

    /// <summary>
    /// The hosting client: <c>--client-env</c>, or the first, of the <c>--inventory</c> file's or this machine's (Valheim from
    /// Steam), with <c>--game</c> as this machine's install override. It must be on this machine.
    /// </summary>
    internal static Choice ChooseHosted(IReadOnlyDictionary<string, string> options, string output, string command = "start")
    {
        EnvironmentInventory inventory;
        string? file;
        try
        {
            (inventory, file) = SmokeInputResolver.ReadInventory(options, output,
                [new SmokeInputResolver.LocalOverride("local-client", "client", options.GetValueOrDefault("--game"))],
                ["--game"], keepOverrideFile: false,
                "--game overrides this machine's client; with --inventory, set its install in the file. --client-loader-package may still override the selected client's loader.");
        }
        catch (ArgumentException failure) when (!options.ContainsKey("--inventory"))
        {
            throw new ArgumentException(failure.Message + (failure.Message.Contains("892970", StringComparison.Ordinal) ? " Give --game DIR." : ""), failure);
        }
        var client = SmokeInputResolver.Pick(inventory, "client", options.GetValueOrDefault("--client-env"), "--client-env",
            file ?? "this machine", "Give --game DIR.", localOnly: true, command: command);
        return new Choice(inventory, file, null, null, client,
            options.ContainsKey("--client-env") ? "named by --client-env" : "first client environment in inventory order", null);
    }

    /// <summary>
    /// The dedicated server and, unless <paramref name="serverOnly"/>, its clean client: the inventory (the file, this machine
    /// with <c>--server</c>/<c>--client</c> as its override, or this machine), its first server and client environment, or the
    /// ones named. The server must be on this machine: its install is what the mod is resolved and its adapter built against.
    /// </summary>
    internal static Choice ChooseDedicated(IReadOnlyDictionary<string, string> options, bool serverOnly, string output, string command = "server-load")
    {
        bool overrides = options.ContainsKey("--server") || options.ContainsKey("--client");
        EnvironmentInventory inventory;
        string? file;
        try
        {
            var local = new List<SmokeInputResolver.LocalOverride> { new("local-server", "server", options.GetValueOrDefault("--server")) };
            if (!serverOnly) local.Add(new("local-client", "client", options.GetValueOrDefault("--client")));
            (inventory, file) = SmokeInputResolver.ReadInventory(options, output, local, ["--server", "--client"], keepOverrideFile: false,
                "--server and --client override this machine's environments; with --inventory, list the installs in the file.");
        }
        catch (ArgumentException failure) when (!options.ContainsKey("--inventory") || overrides)
        {
            // This machine's environments: say how to go on without what was not found.
            throw new ArgumentException(failure.Message +
                (failure.Message.Contains("892970", StringComparison.Ordinal) && !serverOnly ? " Give --client DIR, or --server-only to skip the client's join." : "") +
                (failure.Message.Contains("896660", StringComparison.Ordinal) && command == "server-load" ? " Or run `valheim-test start` for a hosted local world without a dedicated server." : ""), failure);
        }
        string where = file ?? "this machine";
        var server = SmokeInputResolver.Pick(inventory, "server", options.GetValueOrDefault("--server-env"),
            "--server-env", where, "Install Valheim Dedicated Server from Steam (it is free) or give --server DIR" +
                (command == "server-load" ? ", or run `valheim-test start` for a hosted local world." : "."),
            localOnly: true, command: command, remoteHint: command == "server-load"
                ? "server-load resolves the mod and builds its adapter against the server's own install, so its server runs here; run a server elsewhere as a campaign (PinnedServerRun.RunCampaignAsync) or with PinnedServerRun --inventory."
                : null);
        string serverReason = options.ContainsKey("--server-env") ? "named by --server-env" : "first server environment in inventory order";
        if (serverOnly) return new Choice(inventory, file, server, serverReason, null, null, null);

        var client = SmokeInputResolver.Pick(inventory, "client", options.GetValueOrDefault("--client-env"),
            "--client-env", where, "Give --client DIR, add a client environment, or use --server-only to skip the client's join.",
            localOnly: false, command: "server-load");
        string clientReason = options.ContainsKey("--client-env") ? "named by --client-env" : "default; --server-only to skip";
        if (options.TryGetValue("--join", out string? joinOption) &&
            !joinOption.EndsWith(":" + server.GamePort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new ArgumentException($"--join {joinOption}: the server listens on game port {server.GamePort}; give HOST:{server.GamePort}.");
        string join = options.TryGetValue("--join", out string? given) ? given
            : client.Host == server.Host ? "127.0.0.1:" + server.GamePort.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException($"Client environment {client.Name} is on {client.Host}, another machine than the server: give --join HOST:{server.GamePort} " +
                "with this machine's address as that host reaches it, or --server-only.");
        return new Choice(inventory, file, server, serverReason, client, clientReason, join);
    }

    // A hosted run reads its client, with the loader this stage chose, from a one-environment inventory beside its inputs.
    internal static string RecordHostedClient(EnvironmentInventory inventory, Actor client, string output)
    {
        var recipe = client.Recipe;
        recipe.LoaderPackage = client.Loader;
        var recorded = new EnvironmentInventory
        {
            Hosts = new() { [recipe.Host] = inventory.Hosts[recipe.Host] }, Environments = [recipe],
            LeaseHost = recipe.Host, LeaseDirectory = inventory.LeaseHost == recipe.Host && inventory.LeaseDirectory.Length != 0 ? inventory.LeaseDirectory
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recipe.Runtime))!, "leases"),
        };
        return SmokeInputResolver.RecordSelected(recorded, output, [recipe]);
    }
}
