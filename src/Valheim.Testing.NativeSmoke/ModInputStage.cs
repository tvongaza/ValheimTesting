using System.Globalization;
using System.Text.Json;
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
/// Each selected role compiles and resolves against its own actor: the hosting client for <c>start</c>, the dedicated server for
/// <c>server-load</c> (and its explicit joining client, if selected), and the <c>--role</c> for <c>build</c>. Its third-party compile references come from that same loader
/// choice and the explicit <c>--dependency</c> and <c>--search-root</c> inputs, never from the game folder.
/// </summary>
internal static class ModInputStage
{
    internal enum Route { Build, Hosted, Dedicated }

    internal static readonly IReadOnlyList<string> SessionCapabilities = ["valheim.session/state", "valheim.session/join", "valheim.session/leave"];

    internal sealed record ExplicitInputs(IReadOnlyList<string> Configs, IReadOnlyDictionary<string, string> Environment)
    {
        internal static ExplicitInputs Empty { get; } = new([], new Dictionary<string, string>());
    }

    internal sealed record ConfigInput(string Destination, string Source, string File, string Sha256);
    internal sealed record RetainedInputs(IReadOnlyDictionary<string, string> Environment, IReadOnlyList<ConfigInput> Configs,
        string? RecordFile = null, string? RecordSha256 = null)
    {
        internal static RetainedInputs Empty { get; } = new(new Dictionary<string, string>(), []);
        internal void Verify()
        {
            foreach (var config in Configs)
            {
                RequireConfigFile(config.File);
                if (!FileHash.Sha256(config.File).Equals(config.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A retained --config input changed: " + config.File);
            }
            VerifyRecord();
        }

        // Evidence integrity is independent of the operational config pins enforced by HostedRuntimeStage.
        internal void VerifyRecord()
        {
            if (RecordFile != null && !FileHash.Sha256(RecordFile).Equals(RecordSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The private run-inputs record changed: " + RecordFile);
        }

        internal void ApplyTo(RegressionInputs inputs)
        {
            Verify();
            inputs.Configs = Configs.ToDictionary(config => config.Destination, config => config.File, StringComparer.Ordinal);
            inputs.Client.Environment = new Dictionary<string, string>(Environment, StringComparer.Ordinal);
        }
    }

    // All one-shot adapters and wrappers use this conversion; values are opaque, single-line, non-secret data.
    internal static ExplicitInputs ReadExplicit(IReadOnlyList<string> configs, IReadOnlyList<string> assignments,
        bool hosted, string? tokenVariable = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string assignment in assignments)
        {
            int equals = assignment.IndexOf('=');
            if (equals <= 0) throw new ArgumentException("--env needs NAME=VALUE.");
            string name = assignment[..equals], value = assignment[(equals + 1)..];
            if (!environment.TryAdd(name, value)) throw new ArgumentException("Repeated --env variable name (names ignore case).");
            if (value.Length == 0) throw new ArgumentException("--env needs a non-empty value; one-shot commands do not support empty values.");
            if (new[] { "{runtime}", "{world}", "{port}" }.Any(value.Contains))
                throw new ArgumentException("--env values cannot contain the server plan tokens {runtime}, {world} or {port}.");
        }
        GameLaunch.ValidateExplicitEnvironment(environment);
        foreach (string reserved in new[] { SmokeSessionContract.SessionTokenVariable, SmokeSessionContract.SelectedGuidsVariable,
                     SmokeSessionContract.PasswordVariable, tokenVariable }.OfType<string>())
            if (environment.ContainsKey(reserved)) throw new ArgumentException("--env cannot set a runner-owned authentication or session variable.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string config in configs)
        {
            string name = Path.GetFileName(config);
            if (name.Length == 0 || name.Contains('\\') ||
                hosted && !name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--config needs a flat file name" + (hosted ? " ending in .cfg." : "."));
            HostedRuntimeStage.ValidatePayloadPath("BepInEx/config/" + name);
            if (name.Equals(OwnedClientPreflight.CliConfig, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("ValheimCLI's config is the run's own; do not supply it as a mod config.");
            if (!names.Add(name)) throw new ArgumentException("--config destination names must be unique (names ignore case).");
        }
        return new(configs.ToArray(), environment);
    }

    private static void RequireConfigFile(string file)
    {
        // Match HostedRuntimeStage's regular-file admission, including its refusal of reparse points.
        if (!File.Exists(file) || (File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new FileNotFoundException("A selected --config file is missing or linked: " + file, file);
    }

    internal static RetainedInputs RetainExplicit(ExplicitInputs selected, Route route, string output, bool withClient, string? role = null)
    {
        if (selected.Configs.Count == 0 && selected.Environment.Count == 0) return RetainedInputs.Empty;
        var admitted = selected.Configs.Select(source =>
        {
            string file = Path.GetFullPath(source);
            RequireConfigFile(file);
            return (File: file, Name: Path.GetFileName(file), Sha: FileHash.Sha256(file));
        }).ToArray();
        string directory = Path.Combine(output, "inputs", "configs");
        var configs = new List<ConfigInput>();
        foreach (var source in admitted)
        {
            RequireConfigFile(source.File);
            Directory.CreateDirectory(directory);
            string copy = Path.Combine(directory, source.Name);
            File.Copy(source.File, copy);
            RequireConfigFile(source.File);
            RequireConfigFile(copy);
            if (!FileHash.Sha256(source.File).Equals(source.Sha, StringComparison.OrdinalIgnoreCase) ||
                !FileHash.Sha256(copy).Equals(source.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A selected --config changed while retaining its bytes: " + source.File);
            configs.Add(new(source.Name, source.File, copy, source.Sha));
        }
        Directory.CreateDirectory(output);
        string record = Path.Combine(output, "run-inputs.json");
        var environment = new Dictionary<string, string>(selected.Environment, StringComparer.Ordinal);
        File.WriteAllText(record, JsonSerializer.Serialize(new
        {
            schema = 1, meaning = "requested/staged non-secret inputs; plugin consumption requires separate evidence",
            environment, environmentRoles = role == "client" || route == Route.Hosted ? new[] { "client" } : withClient ? ["server", "client"] : ["server"],
            configRole = role ?? (route == Route.Hosted ? "client" : "server"), configs,
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"explicit run inputs: {configs.Count} config(s); environment names: " + string.Join(", ", environment.Keys));
        return new(environment, configs, record, FileHash.Sha256(record));
    }

    /// <summary>Whether the output was checked against the selected sources, so a refusal may be written into it.</summary>
    internal class Progress
    {
        public bool OutputChecked { get; set; }
    }

    internal sealed record RoleSelection(IReadOnlyList<string> Mods, IReadOnlyList<string> SearchRoots,
        IReadOnlyList<string> OptionalReferences, IReadOnlyList<string> Dependencies)
    {
        internal string? Project { get; init; }
        internal string? BuildInputs { get; init; }
        internal ExplicitInputs RunInputs { get; init; } = ExplicitInputs.Empty;
        internal IReadOnlyList<string> PluginFiles { get; init; } = [];
        internal IReadOnlyList<string> PluginDirectories { get; init; } = [];
    }

    internal sealed record SelectedRole(Actor Actor, string Core, IReadOnlyList<string> Mods, string Selection,
        string? BuildInputs, NativeDependencyLock Dependencies, RetainedInputs RunInputs, IReadOnlyList<HostedRuntimeFile> Files);

    /// <summary>A command's parsed inputs, and the test seams for what reads the real machine.</summary>
    internal sealed record Request(Route Route, string Output, IReadOnlyDictionary<string, string> Options, IReadOnlySet<string> Switches,
        IReadOnlyList<string> Mods, IReadOnlyList<string> SearchRoots, IReadOnlyList<string> OptionalReferences, IReadOnlyList<string> Dependencies)
    {
        /// <summary>The pinned-loader rule for an actor that names no package (<see cref="ShippedLoader.Choose"/> in a real run).</summary>
        public ShippedLoader.Rule? Shipped { get; init; }
        public GameBuild? InitialHostingBuild { get; init; }
        public FixtureBake.Admission? InitialWorldAdmission { get; init; }
        /// <summary>An A/B arm's server loader, chosen once before either arm.</summary>
        public ShippedLoader.Choice? FrozenServerLoader { get; init; }
        public Action<EnvironmentInventory, EnvironmentRecipe, string, string?>? ClientArchitecture { get; init; }
        public Progress Progress { get; init; } = new();
        public ExplicitInputs RunInputs { get; init; } = ExplicitInputs.Empty;
        public IReadOnlyList<string> PluginFiles { get; init; } = [];
        public IReadOnlyList<string> PluginDirectories { get; init; } = [];
        public RoleSelection? ClientSelection { get; init; }
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
        public Inspection? Admission { get; init; }
        public FixtureBake.Admission? WorldAdmission => Admission?.WorldAdmission;
        public GameBuild? HostingBuild => Admission?.HostingBuild;
        public string Core { get; init; } = "";
        public string CliManifest { get; init; } = "";
        public string CliFiles { get; init; } = "";
        public IReadOnlyList<string> SearchRoots { get; init; } = [];
        public IReadOnlyList<string> Capabilities { get; init; } = [];
        public Scenario? Scenario { get; init; }
        public string? SelectedInventory { get; init; }
        public string? SteamRuntimePackage { get; init; }
        public NativeDependencyLock? Dependencies { get; init; }
        public RetainedInputs RunInputs { get; init; } = RetainedInputs.Empty;
        public IReadOnlyList<HostedRuntimeFile> Files { get; init; } = [];
        public SelectedRole? ClientMods { get; init; }

        public IEnumerable<Actor> Actors => new[] { Server, Client }.OfType<Actor>();

        /// <summary>The stage's provenance keys: each actor's loader, the mod selection and the scenario.</summary>
        public void Record(IDictionary<string, string> provenance)
        {
            RunInputs.VerifyRecord();
            ClientMods?.RunInputs.VerifyRecord();
            if (ClientMods is { } selectedClient)
            {
                provenance["clientModSelection"] = selectedClient.Selection;
                if (selectedClient.BuildInputs != null) provenance["clientBuildInputs"] = selectedClient.BuildInputs;
                if (selectedClient.RunInputs.RecordFile != null)
                {
                    provenance["clientRunInputs"] = selectedClient.RunInputs.RecordFile;
                    provenance["clientRunInputsSha256"] = selectedClient.RunInputs.RecordSha256!;
                }
            }
            foreach (var actor in Actors) SmokeInputResolver.RecordLoader(provenance, actor.Role, actor.Loader, actor.Shipped);
            provenance["modSelection"] = ModSelection;
            if (RunInputs.RecordFile != null)
            {
                provenance["runInputs"] = RunInputs.RecordFile;
                provenance["runInputsSha256"] = RunInputs.RecordSha256!;
            }
            if (Scenario != null)
            {
                provenance["oneShotScenario"] = Scenario.Name;
                provenance["oneShotScenarioSha256"] = Scenario.Sha256;
            }
        }
    }

    // The read-only prefix of the existing choice flow. Detached startup uses it before staging its tool or inputs;
    // Choose consumes the same selection/admission path before loader or ValheimCLI materialization.
    internal sealed record Inspection(Choice Choice, string CompileRole, FixtureBake.Admission? WorldAdmission, GameBuild? HostingBuild);

    internal static Inspection Inspect(Request request)
    {
        var options = request.Options;
        // 1. The game and loader choice.
        string compileRole = request.Route switch
        {
            Route.Hosted => "client", Route.Dedicated => "server",
            _ => options.GetValueOrDefault("--role") ?? "client",
        };
        var choice = request.Route == Route.Dedicated || compileRole == "server"
            ? ChooseDedicated(options, serverOnly: request.Route == Route.Build || request.Switches.Contains("--server-only"), request.Output,
                request.Route == Route.Build ? "build" : "server-load")
            : ChooseHosted(options, request.Output, request.Route == Route.Build ? "build" : "start");
        var inventory = choice.Inventory;
        if (request.ClientSelection != null && (request.Route != Route.Dedicated || request.Switches.Contains("--server-only") ||
            choice.Client == null || inventory.Hosts[choice.Client.Host].Kind != "local"))
            throw new ArgumentException("Explicit client mod inputs require a local joining client install; remote role locks use the explicit campaign API.");
        FixtureBake.Admission? worldAdmission = null;
        GameBuild? hostingBuild = null;
        if (request.Route != Route.Build)
        {
            var hosting = choice.Server ?? choice.Client!;
            var build = hostingBuild = ReadHostingBuild(inventory, hosting);
            if (request.InitialHostingBuild is { } initial) WorldIdentity.RequireSameTarget(initial, build);
            if (options.TryGetValue("--world-fixture", out string? fixtureSource))
            {
                worldAdmission = request.InitialWorldAdmission ?? FixtureBake.Admit(fixtureSource, build);
                worldAdmission.World.VerifyTarget(build);
                FixtureBake.VerifyAdmission(worldAdmission);
            }
            else _ = DefaultSmokeWorld.Inspect(build);
        }
        return new(choice, compileRole, worldAdmission, hostingBuild);
    }

    /// <summary>
    /// Steps 1 and 2 alone: the game and loader choice and the output guard, with the core, the CLI inputs and the capability
    /// list the dependency lock is requested with. It builds no mod and writes nothing into the output; its read-only prefix admits the fixture against the selected host, then
    /// the whole stage it extracts the pinned loader and ValheimCLI bundle into the data root. A wrapper
    /// that needs the choice before it runs the stage (<c>server-load-ab</c> before its two arms, <c>server-load-phases</c>
    /// before its first phase) takes it from here instead of deciding its own (#745). <see cref="RunAsync"/> starts with it.
    /// </summary>
    public static Inputs Choose(Request request)
    {
        var options = request.Options;
        string output = request.Output;

        var admission = Inspect(request);
        var (choice, compileRole, worldAdmission, _) = admission;
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
        string core = CoreFor(compile);
        if (request.ClientSelection != null) _ = CoreFor(client!);
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
            Console.WriteLine($"server: {server.Recipe.Name} on {server.Recipe.Host} ({server.Reason}): install {server.Recipe.Install}; runtime {server.Recipe.Runtime} ({server.Recipe.RuntimeReason}); ValheimCLI port {server.Recipe.CliPort}, game port {server.Recipe.GamePort}");
        if (client != null)
            Console.WriteLine($"client: {client.Recipe.Name} on {client.Recipe.Host} ({client.Reason}): install {client.Recipe.Install}; runtime {client.Recipe.Runtime} ({client.Recipe.RuntimeReason}); ValheimCLI port {client.Recipe.CliPort}" +
                (choice.Join == null ? "" : "; joins " + choice.Join) + (architecture == null ? "" : "; architecture " + architecture));
        else if (request.Route == Route.Dedicated) Console.WriteLine("client: none (--server-only)");

        // 2. Every output is outside every selected source before anything is written.
        string cliManifest = "", cliFiles = "";
        if (request.Route != Route.Build) (cliManifest, cliFiles) = SmokeInputs.Cli(options);
        var protectedInputs = new[] { server?.LoaderRoot, client?.LoaderRoot }
            .Concat(RoleSources(Primary(request))).Concat(request.ClientSelection == null ? [] : RoleSources(request.ClientSelection)).ToArray();
        SmokeOutput.RefuseResolved(output, request.Route == Route.Build ? null : cliFiles, inventory, [server?.Recipe, client?.Recipe], protectedInputs);
        if (options.TryGetValue("--bake-fixture", out string? bake))
            FixtureBake.RefuseOutput(Path.GetFullPath(bake), new[] { output, server?.Recipe.Install, client?.Recipe.Install,
                options.TryGetValue("--world-fixture", out string? fixture) ? Path.GetFullPath(fixture) : null }.OfType<string>().ToArray());
        request.Progress.OutputChecked = true;

        // 6 (below) requests the dependency lock with these.
        IReadOnlyList<string> capabilities = request.Route == Route.Hosted ? CliCapabilities.HostedRounds
            : client == null ? [SessionCapabilities[0]] : SessionCapabilities;
        return new Inputs(request.Route, inventory, compile, server, client, architecture, choice.Join, [], "", options.GetValueOrDefault("--build-inputs"))
        {
            Admission = admission, Core = core, CliManifest = cliManifest, CliFiles = cliFiles, Capabilities = capabilities.ToList(), SteamRuntimePackage = steamRuntime,
        };
    }

    private static GameBuild ReadHostingBuild(EnvironmentInventory inventory, EnvironmentRecipe hosting)
    {
        if (inventory.Hosts[hosting.Host].Kind == "local")
            return GameVersionAgreement.ReadInPlaceAsync(hosting.Install, CancellationToken.None).GetAwaiter().GetResult();
        var host = LocalHostPreflight.DefaultProbes.HostFactory?.Invoke(hosting.Host)
            ?? new ResolvedEnvironment { Hosts = inventory.Hosts }.CreateHost(hosting.Host);
        var listing = HostInstall.ListAsync(host, hosting.Install, TimeSpan.FromSeconds(60), null, CancellationToken.None).GetAwaiter().GetResult();
        return GameVersionAgreement.ReadAsync(host, listing, TimeSpan.FromSeconds(60), CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static GameBuild VerifyAdmission(Inspection admission)
    {
        var current = ReadHostingBuild(admission.Choice.Inventory, admission.Choice.Server ?? admission.Choice.Client!);
        WorldIdentity.RequireSameTarget(admission.HostingBuild!, current);
        if (admission.WorldAdmission is { } world) FixtureBake.VerifyAdmission(world);
        return current;
    }

    internal static FixtureBake.Admission AdmitCheckpoint(Inputs selection, string fixture, bool original = false)
    {
        var current = VerifyAdmission(selection.Admission!);
        return original && selection.WorldAdmission is { } admitted ? admitted : FixtureBake.Admit(fixture, current);
    }

    /// <summary>
    /// The dependency lock of a selection of mods against the chosen game, core, ValheimCLI and capabilities: the one request
    /// shape of the stage's step 6 and of an A/B run's before and after locks. <paramref name="resolve"/> is a test seam.
    /// </summary>
    internal static NativeDependencyLock Resolve(Inputs inputs, IReadOnlyList<string> mods, IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> optionalReferences, Func<NativeDependencyRequest, NativeDependencyLock>? resolve = null) =>
        (resolve ?? NativeDependencyResolver.Resolve)(SmokeDependencyInputs.Request(mods, inputs.Compile.Recipe.Install, inputs.Core,
            inputs.CliManifest, inputs.CliFiles, searchRoots, optionalReferences, inputs.Capabilities));

    private static string CoreFor(Actor actor)
    {
        string core = Path.Combine(actor.LoaderRoot ?? actor.Recipe.Install, InstallPins.CoreDirectory);
        if (!Directory.Exists(core))
            throw new DirectoryNotFoundException($"The {actor.Role} install {actor.Recipe.Install} has no BepInEx ({InstallPins.CoreDirectory}) and no loader package was chosen. " +
                $"Give {LoaderOption(actor.Role)} FILE with a reviewed loader package, name one as environment {actor.Recipe.Name}'s loaderPackage, or install BepInExPack_Valheim into it; the mod is compiled and resolved against that core.");
        return core;
    }

    private static RoleSelection Primary(Request request) => new(request.Mods, request.SearchRoots, request.OptionalReferences, request.Dependencies)
    {
        Project = request.Options.GetValueOrDefault("--project"), BuildInputs = request.Options.GetValueOrDefault("--build-inputs"),
        RunInputs = request.RunInputs, PluginFiles = request.PluginFiles, PluginDirectories = request.PluginDirectories,
    };

    private static IEnumerable<string?> RoleSources(RoleSelection selected) => selected.Mods.Concat(selected.SearchRoots)
        .Concat(selected.RunInputs.Configs).Concat(selected.PluginFiles).Concat(selected.PluginDirectories)
        .Concat(ModBuild.ParseDependencies(selected.Dependencies).Values)
        .Concat(new[] { selected.BuildInputs, selected.Project == null ? null : Path.GetDirectoryName(Path.GetFullPath(selected.Project)) });

    private sealed class PreparingRole(RoleSelection selected, Actor actor, string core, string output)
    {
        internal RoleSelection Selected { get; } = selected;
        internal Actor Actor { get; } = actor;
        internal string Core { get; } = core;
        internal string Output { get; } = output;
        internal Dictionary<string, string> Given { get; } = ModBuild.ParseDependencies(selected.Dependencies);
        internal List<string> Mods { get; } = [];
        internal List<string> Roots { get; } = selected.SearchRoots.ToList();
        internal string Selection { get; set; } = "";
        internal string? BuildInputs { get; set; } = selected.BuildInputs;
        internal ModBuild.BuildRecord? Verified { get; set; }
        internal RetainedInputs Retained { get; set; } = RetainedInputs.Empty;
        internal IReadOnlyList<HostedRuntimeFile> Files { get; set; } = [];
    }

    private static PreparingRole InspectRole(RoleSelection selected, Actor actor, string core, string output,
        Route route, bool withClient, bool implicitSelection, bool buildOnly)
    {
        if (!implicitSelection && selected.Project == null && selected.Mods.Count == 0)
            throw new ArgumentException("An explicit role needs selected mods or a project; it never discovers a client mod implicitly.");
        var role = new PreparingRole(selected, actor, core, output);
        role.Retained = RetainExplicit(selected.RunInputs, route, output, withClient, implicitSelection ? null : actor.Role);
        role.Files = PayloadFiles(role.Retained, selected.PluginFiles, selected.PluginDirectories);
        if (!buildOnly && selected.Project == null)
        {
            SmokeModInput.Selection mods = implicitSelection ? SmokeModInput.Select(selected.Mods, Environment.CurrentDirectory)
                : new(selected.Mods.Select(Path.GetFullPath).ToArray(), "explicit --client-mod selection");
            role.Mods.AddRange(mods.Mods); role.Selection = mods.Reason;
            Console.WriteLine(actor.Role + " mod selection: " + role.Selection);
            role.Verified = CheckMod(role.Mods, role.BuildInputs, actor.Recipe.Install, core, role.Roots);
        }
        return role;
    }

    private static async Task BuildRoleAsync(PreparingRole role, CancellationToken cancellation, bool verify = true)
    {
        if (role.Selected.Project == null) return;
        var plan = ModBuild.Plan(role.Selected.Project, role.Actor.Recipe.Install, role.Given,
            Path.Combine(role.Output, "build"), role.Actor.LoaderRoot, role.Selected.SearchRoots);
        var record = await ModBuild.BuildAsync(plan, cancellation).ConfigureAwait(false);
        role.Mods.Add(record.Mod); role.BuildInputs = Path.Combine(plan.Output, "build-inputs.json");
        role.Selection = "built from --project " + Path.GetFullPath(role.Selected.Project);
        Console.WriteLine(role.Actor.Role + " built mod: " + record.Mod);
        if (verify) role.Verified = CheckMod(role.Mods, role.BuildInputs, role.Actor.Recipe.Install, role.Core, role.Roots);
    }

    private static SelectedRole ResolveRole(Inputs inputs, PreparingRole role)
    {
        var dependencies = Resolve(inputs with { Compile = role.Actor, Core = role.Core }, role.Mods, role.Roots, role.Selected.OptionalReferences);
        Directory.CreateDirectory(role.Output);
        if (role.BuildInputs != null) File.Copy(role.BuildInputs, Path.Combine(role.Output, "build-inputs.json"));
        dependencies.Write(Path.Combine(role.Output, "dependencies.lock.json"));
        if (!dependencies.Ready) throw new InvalidDataException("Dependency choices remain: " + SmokeDependencyInputs.Gaps(dependencies));
        if (role.Verified != null) ModBuild.RequireResolvedDependencies(role.Verified, dependencies);
        File.WriteAllText(Path.Combine(role.Output, "selected-plugins.json"), JsonSerializer.Serialize(new
        {
            role = role.Actor.Role, meaning = "selected expected plugins; observed loading requires strict pins and role logs",
            files = dependencies.Mods.Concat(dependencies.Plugins).Select(file => new
            {
                file.File, file.Sha256, plugins = PluginMetadata.Read(file.File).Plugins,
            }),
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return new(role.Actor, role.Core, role.Mods, role.Selection, role.BuildInputs, dependencies, role.Retained, role.Files);
    }

    public static async Task<Inputs> RunAsync(Request request, CancellationToken cancellation)
    {
        _ = ModBuild.ParseDependencies(request.Dependencies);
        if (request.ClientSelection != null) _ = ModBuild.ParseDependencies(request.ClientSelection.Dependencies);
        var inputs = Choose(request);
        string? cliManifest = request.Route == Route.Build ? null : inputs.CliManifest;
        if (request.Route != Route.Build)
        {
            foreach (var (name, path) in new[] { ("game", inputs.Compile.Recipe.Install), ("--cli-files", inputs.CliFiles) })
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException(name + " directory does not exist: " + path);
            if (!File.Exists(cliManifest)) throw new FileNotFoundException("--cli-manifest file does not exist: " + cliManifest, cliManifest);
        }
        // Admit both roles' ready-made inputs before host probes or either build, through the same selection/check owner.
        var primary = InspectRole(Primary(request), inputs.Compile, inputs.Core, request.Output, request.Route,
            inputs.Client != null, implicitSelection: true, buildOnly: request.Route == Route.Build);
        var client = request.ClientSelection == null ? null : InspectRole(request.ClientSelection, inputs.Client!, CoreFor(inputs.Client!),
            Path.Combine(request.Output, "client"), request.Route, withClient: true, implicitSelection: false, buildOnly: false);
        Scenario? scenario = request.Options.TryGetValue("--scenario", out string? scenarioFile)
            ? LoadScenario(request.Route, scenarioFile, request.Output, inputs.Client != null) : null;
        if (request.Route != Route.Build)
        {
            _ = VerifyAdmission(inputs.Admission!);
            var problems = await LocalHostPreflight.InspectAsync(inputs.Inventory,
                inputs.Actors.Select(actor => new LocalHostPreflight.Actor(actor.Role, actor.Recipe)), TimeSpan.FromSeconds(60), cancellation).ConfigureAwait(false);
            if (problems.Count != 0) throw new InvalidOperationException(string.Join("; ", problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}")));
        }
        await BuildRoleAsync(primary, cancellation, verify: request.Route != Route.Build).ConfigureAwait(false);
        inputs = inputs with { Mods = primary.Mods, ModSelection = primary.Selection, BuildInputs = primary.BuildInputs, RunInputs = primary.Retained, Files = primary.Files };
        if (request.Route == Route.Build)
        {
            if (primary.Selected.Project == null) throw new ArgumentException("Give --project MOD.csproj.");
            return inputs;
        }
        if (client != null) await BuildRoleAsync(client, cancellation).ConfigureAwait(false);
        if (request.Options.TryGetValue("--scenario-project", out string? scenarioProject))
            scenario = LoadScenario(request.Route, await OneShotScenario.BuildAsync(scenarioProject, Path.Combine(request.Output, "scenario-build"), cancellation)
                .ConfigureAwait(false), request.Output, inputs.Client != null);
        _ = VerifyAdmission(inputs.Admission!);
        string selectedInventory = request.Route == Route.Hosted ? RecordHostedClient(inputs.Inventory, inputs.Client!, request.Output)
            : SmokeInputResolver.RecordSelected(inputs.Inventory, request.Output, inputs.Actors.Select(actor => actor.Recipe));
        var resolved = ResolveRole(inputs, primary);
        var resolvedClient = client == null ? null : ResolveRole(inputs, client);
        return inputs with { SearchRoots = primary.Roots, Scenario = scenario, SelectedInventory = selectedInventory,
            Dependencies = resolved.Dependencies, ClientMods = resolvedClient };
    }

    // Companion files and retained configs have one mapping/admission rule for both roles.
    internal static IReadOnlyList<HostedRuntimeFile> PayloadFiles(RetainedInputs inputs, IReadOnlyList<string> files, IReadOnlyList<string> directories)
    {
        inputs.Verify();
        var result = inputs.Configs.Select(config => new HostedRuntimeFile(config.File, "BepInEx/config/" + config.Destination)
            { ExpectedSha256 = config.Sha256 }).ToList();
        void Add(string source, string relative)
        {
            if (Path.GetExtension(source).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin DLLs belong in selected mods or dependency resolution, not a sidecar: " + source);
            RequireConfigFile(source);
            HostedRuntimeStage.ValidatePayloadPath(relative);
            result.Add(new HostedRuntimeFile(source, relative) { ExpectedSha256 = FileHash.Sha256(source) });
        }
        foreach (string file in files.Select(Path.GetFullPath)) Add(file, "BepInEx/plugins/" + Path.GetFileName(file));
        foreach (string directory in directories.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))))
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("An explicitly selected plugin sidecar directory is missing: " + directory);
            FileHash.RefuseLink(directory);
            foreach (string relative in WorldFixture.Manifest(directory).Keys.Order(StringComparer.Ordinal))
                Add(Path.Combine(directory, relative), "BepInEx/plugins/" + Path.GetFileName(directory) + "/" + relative.Replace(Path.DirectorySeparatorChar, '/'));
        }
        if (result.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new InvalidDataException("Config and companion destinations must be unique within each role.");
        return result;
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
