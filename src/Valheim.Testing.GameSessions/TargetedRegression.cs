using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// One arm staged into the disposable install and preflighted: the strict client plan the runner hands to
/// <see cref="ClientRounds"/>, and the run manifest.
/// </summary>
public sealed class StagedArm
{
    internal StagedArm(string arm, ClientRunPlan plan, RunManifest manifest, IReadOnlyDictionary<string, string> tree, string install)
    { Arm = arm; Plan = plan; Manifest = manifest; Tree = tree; _install = install; }
    private readonly string _install;

    public string Arm { get; }
    /// <summary>The strict client plan: every staged plugin pinned by MD5 under its declared GUID, the install pins and the pinned fixture.</summary>
    public ClientRunPlan Plan { get; }
    public RunManifest Manifest { get; }
    /// <summary>Every staged file under <c>BepInEx/plugins</c>, <c>patchers</c>, <c>config</c> and <c>scripts</c>, by relative path and SHA256.</summary>
    public IReadOnlyDictionary<string, string> Tree { get; }

    /// <summary>
    /// Refuses unless the staged tree is still exactly the allowlist: an extra plugin loads even when the test never calls
    /// it, and a changed or removed file is another run. Call it right before the launch.
    /// </summary>
    public void Verify()
    {
        var found = TargetedRegression.StagedTree(_install);
        var problems = found.Keys.Except(Tree.Keys).Order(StringComparer.Ordinal).Select(path => $"{path} is not in the allowlist")
            .Concat(Tree.Keys.Except(found.Keys).Order(StringComparer.Ordinal).Select(path => $"{path} is missing"))
            .Concat(Tree.Where(file => found.TryGetValue(file.Key, out var hash) && hash != file.Value).OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => $"{file.Key} changed after staging")).ToList();
        if (problems.Count != 0)
            throw new InvalidOperationException($"The disposable install changed after it was staged for {Arm}: {string.Join("; ", problems)}. " +
                "A plugin outside the allowlist loads even if the test never calls it. Declare it in the manifest (plugins, probe or configs) and stage again, or stage again to remove it.");
    }

    /// <summary>The arm table and allowlist as lines for a person to review.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"{Manifest.Name}: arm {Arm}, mod {Manifest.ModPlugin} at {Manifest.ModCommit ?? "an unrecorded commit"}, sha256 {Manifest.ModSha256}{(Manifest.Repeatability ? " (repeatability run)" : "")}";
        foreach (var arm in Manifest.Arms) yield return $"  arm {arm.Arm,-12} commit {arm.Commit ?? "unrecorded",-12} artifact {arm.Artifact}  sha256 {arm.Sha256}  md5 {arm.Md5}";
        foreach (var file in Manifest.Allowlist) yield return $"  {file.Role,-12} {file.Path}  md5 {file.Md5}  {string.Join(", ", file.Plugins)}";
        yield return $"  fixture      {Manifest.World} uid {Manifest.WorldUid} ({Manifest.FixtureFiles} files)";
        yield return $"  capabilities {string.Join(", ", Manifest.Capabilities)}{(Manifest.LiveOnlyCapabilities.Count == 0 ? "" : "; live only: " + string.Join(", ", Manifest.LiveOnlyCapabilities))}";
        yield return $"  cli manifest {Manifest.CliManifest}";
    }

    /// <summary>The arm, mod build and allowlist hashes as report provenance.</summary>
    public void Record(IDictionary<string, string> provenance)
    {
        provenance["regression"] = Manifest.Name;
        provenance["arm"] = Arm;
        provenance["modPlugin"] = Manifest.ModPlugin;
        if (Manifest.ModCommit != null) provenance["modCommit"] = Manifest.ModCommit;
        provenance["modSha256"] = Manifest.ModSha256;
        provenance["allowlist"] = string.Join(", ", Manifest.Allowlist.Select(file => $"{file.Path}={file.Md5}"));
        provenance["worldUid"] = Manifest.WorldUid;
        Manifest.InstallPins.Record(provenance, "client");
    }
}

/// <summary>
/// Stages and preflights a targeted native regression from its <see cref="RegressionInputs"/> on the inventory's client environment, without the game, then
/// hands one arm to the existing strict-pinned <see cref="ClientRounds"/>.
/// <list type="number">
/// <item>The fixture root must hold exactly one world with the manifest's UID (<see cref="FixtureLayout"/>).</item>
/// <item>Every staged file must be its pinned SHA256; the manifest records every arm's source hash and the chosen arm is staged.</item>
/// <item>The shared hosted-runtime stage makes a fresh disposable copy per arm with <c>BepInEx/plugins</c>, <c>patchers</c>,
/// <c>config</c> and <c>scripts</c> rebuilt from the allowlist only: the ValheimCLI core and packs, the plugins, the probe and one arm.</item>
/// <item>Every hard <c>[BepInDependency]</c> any staged plugin declares must be met by a staged plugin's own
/// <c>[BepInPlugin]</c> (with its minimum version), no <c>[BepInIncompatibility]</c> may be staged, every plugin must load in
/// the client process, and every assembly reference must resolve to the game, BepInEx or a staged DLL. File names count for nothing.</item>
/// <item>The plan's own <see cref="ClientRunPlan.Validate"/> and <see cref="ClientRunPlan.Preflight(IEnumerable{string})"/> run on the
/// staged install, including the static ValheimCLI capability check against <see cref="RegressionCli.Manifest"/> when set.</item>
/// </list>
/// Nothing launches. <see cref="Run"/> stages a fresh copy for one arm, refuses any file outside the allowlist right before
/// the launch, and keeps <see cref="ClientRounds"/>' live capability check and strict per-command pins as the second gate.
/// </summary>
public sealed class TargetedRegression
{
    // Generated consumers use the public, direct client opener. The one-shot runner supplies its
    // desktop opener explicitly and performs its own desktop preflight before staging.
    internal Func<(bool Windows, int SessionId)> DirectClientSession { get; set; } = DirectClientDesktop.Current;
    // Controlled tests use a synthetic install without inspecting unrelated processes on the test machine.
    internal Func<IReadOnlyCollection<string>?, bool, Task>? ProcessCheck { get; init; }
    internal bool SkipHostLockForTest { get; init; }

    private void RequireDirectClientDesktop() => DirectClientDesktop.Require(DirectClientSession());

    internal const string CliPlugin = "valheimCLI.valheimCLI", CliConfig = "valheimCLI.valheimCLI.cfg", BepInExConfig = "BepInEx.cfg";
    private static readonly string[] StagedFolders = ["plugins", "patchers", "config", "scripts"];
    // What a copy of the prepared install leaves out: everything BepInEx loads or writes besides its core.
    // ValheimCLI's own extension owners: the Standard and World Tools packs register valheim.* and cli.*.
    private static bool OwnedByCli(string path) => path.StartsWith("valheim.", StringComparison.Ordinal) || path.StartsWith("cli.", StringComparison.Ordinal);
    private readonly IGameHost _host = new LocalGameHost("this machine", OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash);
    private readonly RunJournal _journal = RunJournal.ThisProcess;
    private readonly string _lockPath;
    private HostLock? _hostLock;
    private readonly string _stage;
    private bool _prepared;
    private double _lastStageSeconds;
    private string? _activeGameRoot;
    private InstallPins? _activePins;

    public RegressionInputs Inputs { get; }
    /// <summary>The client environment the run uses (<see cref="EnvironmentRecipe.Name"/>) and why.</summary>
    public string ClientEnvironment { get; }
    /// <summary>The prepared Valheim install the disposable copy is made from: the client environment's install. Only read.</summary>
    public string Game { get; }
    /// <summary>The disposable loader profile or full game copy this runner owns in the client environment's runtime.</summary>
    public string Install { get; }
    private bool _copyGame;
    /// <summary>Opt in to a full disposable game copy before the first arm. By default the run stages a disposable loader profile.</summary>
    public bool CopyGame
    {
        get => _copyGame;
        set
        {
            if (_prepared) throw new InvalidOperationException("Cannot change launch mode while a regression arm is staged; remove the prepared arm first.");
            _copyGame = value;
        }
    }
    /// <summary>The client's ValheimCLI port: the client environment's.</summary>
    public int Port { get; }
    /// <summary>The client environment's loader package, when it names one.</summary>
    public string? LoaderPackage { get; }
    /// <summary>The effective client slice selected by the inputs or inventory.</summary>
    public string Architecture { get; }
    /// <summary>What the inventory detected and assumed for this machine (<see cref="EnvironmentInventory.Detected"/>), for a caller to print.</summary>
    public IReadOnlyList<string> Detected { get; }
    /// <summary>This machine's detected Steam <c>userdata</c>, which a registered character is checked against; null when none was detected.</summary>
    public string? SteamUserData { get; internal init; }
    // The client's save root (worlds_local, characters_local): this user's by default; tests replace it.
    internal string? SaveDirectory { get; init; }
    // Synthetic test installs are not notarized Steam apps; production always uses the real macOS assessment.
    internal Func<string, bool, TimeSpan, MacBundleInspection.Verdict>? BundleInspection { get; init; }
    /// <summary>
    /// The ValheimCLI capabilities the run uses: <see cref="CliCapabilities.HostedRounds"/> and the scenario's own whose owner is
    /// ValheimCLI's (<c>valheim.*</c> or <c>cli.*</c>). They are checked against <see cref="RegressionCli.Manifest"/> before
    /// launch, when it is set, and live once the client answers.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; }
    /// <summary>The scenario's capabilities of any other owner (a probe's or the mod's own extension): checked live before the first round.</summary>
    public IReadOnlyList<string> LiveOnlyCapabilities { get; }

    /// <summary>
    /// A regression of <paramref name="inputs"/> on <paramref name="inventory"/>'s client environment: <paramref name="clientEnvironment"/>,
    /// or its first client environment. With no inventory, this machine (<see cref="EnvironmentInventory.Read(string?)"/>).
    /// The client runs here, so the environment must be on this machine (a <c>local</c> host).
    /// </summary>
    public TargetedRegression(RegressionInputs inputs, IEnumerable<string>? scenarioCapabilities = null,
        EnvironmentInventory? inventory = null, string? clientEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.Validate();
        Inputs = inputs;
        inventory ??= EnvironmentInventory.Read(null);
        var recipe = clientEnvironment != null
            ? inventory.Environments.FirstOrDefault(item => item.Name == clientEnvironment && item.Roles.Contains("client"))
                ?? throw new ArgumentException($"The inventory has no client environment {clientEnvironment}.")
            : inventory.Environments.FirstOrDefault(item => item.Roles.Contains("client"))
                ?? throw new ArgumentException("The inventory has no client environment. " + string.Join(" ", inventory.Missing));
        if (!inventory.Hosts.TryGetValue(recipe.Host, out var host) || host.Kind != "local")
            throw new ArgumentException($"Client environment {recipe.Name} is on {recipe.Host}, not this machine. A targeted regression runs its client here; " +
                "name a client environment on this machine.");
        ClientEnvironment = recipe.Name;
        _lockPath = host.Lock;
        Game = recipe.Install; Port = recipe.CliPort; LoaderPackage = recipe.LoaderPackage;
        Architecture = ClientArchitectureChoice.Select(inputs.Client.Architecture, recipe.Architecture,
            host.Platform, EnvironmentInventory.ThisMachine.OsArchitecture);
        string parent = Path.Combine(recipe.Runtime, "vt-prep-regression-" + inputs.Name + "-" + _journal.RunId);
        Install = Path.Combine(parent, "runtime");
        _stage = Path.Combine(parent, "staging");
        SteamUserData = inventory.SteamUserData;
        Detected = inventory.Detected;
        if (RegressionInputs.Inside(Install, Game) || RegressionInputs.Inside(Game, Install))
            throw new ArgumentException($"The disposable install {Install} and the game {Game} overlap: give the client environment a runtime outside its install.");
        if (LoaderPackage != null && inputs.Configs.Keys.Any(name => name.Equals(BepInExConfig, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"configs.{BepInExConfig}: the client environment's loader package pins BepInEx.cfg; edit and recapture that package instead of overriding its configuration during staging.");
        var scenario = (scenarioCapabilities ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (scenario.Any(path => path == null || path.Split('/') is not [{ Length: > 0 }, { Length: > 0 }]))
            throw new ArgumentException("Name each scenario capability as owner/command.", nameof(scenarioCapabilities));
        Capabilities = CliCapabilities.HostedRounds.Concat(scenario.Where(OwnedByCli)).Distinct(StringComparer.Ordinal).ToList();
        LiveOnlyCapabilities = scenario.Where(path => !OwnedByCli(path)).ToList();
    }

    /// <summary>
    /// The regression a run's <c>regression.json</c> describes, on the machine it ran on: the <c>environments.json</c> beside
    /// it when there is one (written when the run overrode this machine's client), otherwise this machine.
    /// </summary>
    public static TargetedRegression Read(string inputsFile, IEnumerable<string>? scenarioCapabilities = null)
    {
        string machine = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputsFile))!, "environments.json");
        return new TargetedRegression(RegressionInputs.Read(inputsFile), scenarioCapabilities, EnvironmentInventory.Read(File.Exists(machine) ? machine : null));
    }

    /// <summary>Stages and preflights every arm in turn, without the game; returns them in manifest order (the last stays staged).
    /// Call <see cref="Remove()"/> in a <c>finally</c> block to retire that copy and release the host lock.
    /// </summary>
    public IReadOnlyList<StagedArm> Preflight()
    {
        RequireDirectClientDesktop();
        return Inputs.Mod.Arms.Keys.Select(Stage).ToList();
    }

    /// <summary>
    /// Stages <paramref name="arm"/> into the disposable install and runs every static check (see the class summary).
    /// Refuses with the corrective action at the first problem; nothing is launched.
    /// </summary>
    public StagedArm Stage(string arm)
    {
        var env = Inputs;
        if (!env.Mod.Arms.TryGetValue(arm, out var chosen))
            throw new ArgumentException($"There is no arm \"{arm}\"; the manifest names {string.Join(", ", env.Mod.Arms.Keys)}.");
        var identity = FixtureLayout.Discover(env.Fixture.Root, env.Fixture.WorldUid);
        var fixture = WorldFixture.Manifest(env.Fixture.Root);

        // Every source is the pinned build before anything is copied.
        var sources = new List<(string Role, RegressionFile File, string Sha256)>();
        void Source(string role, RegressionFile file, string field) => sources.Add((role, file, RequirePinned(file.File, file.Sha256, field)));
        Source("cli-core", env.Cli.Core, "cli.core");
        foreach (var (pack, i) in env.Cli.Packs.Select((pack, i) => (pack, i))) Source("cli-pack", pack, $"cli.packs[{i}]");
        foreach (var (plugin, i) in env.Plugins.Select((plugin, i) => (plugin, i))) Source("plugin", plugin, $"plugins[{i}]");
        if (env.Probe != null) Source("probe", env.Probe, "probe");
        foreach (var (name, other) in env.Mod.Arms) RequireArm(name, other);
        foreach (var (name, path) in env.Configs) if (!File.Exists(path)) throw new FileNotFoundException($"configs.{name}: {path} does not exist.", path);
        var patchers = env.Patchers.Select((file, i) => (File: file, Sha256: RequirePinned(file.File, file.Sha256, $"patchers[{i}]"))).ToList();

        // The metadata decides what each DLL is, never its name.
        var metadata = new Dictionary<string, PluginAssembly>(StringComparer.Ordinal);
        PluginAssembly Read(string path) => metadata.TryGetValue(path, out var known) ? known : metadata[path] = PluginMetadata.Read(path);
        var armPlugins = env.Mod.Arms.ToDictionary(entry => entry.Key, entry => Read(entry.Value.File));
        foreach (var (name, read) in armPlugins)
            if (read.Plugins.Count == 0) throw new InvalidOperationException($"mod.arms.{name}: {env.Mod.Arms[name].File} declares no [BepInPlugin]; it is not a build of the mod under test.");
        var guids = armPlugins.Select(entry => (entry.Key, Guids: string.Join(", ", entry.Value.Plugins.Select(p => p.Guid).Order(StringComparer.Ordinal)))).ToList();
        if (guids.Select(entry => entry.Guids).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new InvalidOperationException($"The arms declare different plugins ({string.Join("; ", guids.Select(entry => $"{entry.Key}: {entry.Guids}"))}): they are not builds of one mod. Stage the same mod's parent and candidate builds.");
        var core = Read(env.Cli.Core.File);
        if (!core.Plugins.Any(plugin => plugin.Guid == CliPlugin))
            throw new InvalidOperationException($"cli.core: {env.Cli.Core.File} declares {Declared(core)}, not ValheimCLI's core plugin {CliPlugin}. Give the core valheimCLI.dll of one coherent ValheimCLI build.");
        foreach (var (pack, i) in env.Cli.Packs.Select((pack, i) => (pack, i)))
            if (Read(pack.File).Plugins.Count == 0) throw new InvalidOperationException($"cli.packs[{i}]: {pack.File} declares no [BepInPlugin]; a ValheimCLI pack is a plugin.");
        if (env.Probe != null && Read(env.Probe.File).Plugins.Count == 0)
            throw new InvalidOperationException($"probe: {env.Probe.File} declares no [BepInPlugin]; a probe is a plugin.");

        // A direct regression.json consumer gets the same refusal as valheim-test start, before its large game copy.
        ClientArchitectureChoice.Require(Game, Architecture, LoaderPackage);
        // Check the signed source app before staging through HostedRuntimeStage:
        // otherwise an old preloader log inside the source bundle becomes a Gatekeeper "damaged" dialog at launch.
        if (OperatingSystem.IsMacOS() && Directory.Exists(Path.Combine(Game, GameLaunch.ClientMacBundle)))
        {
            var verdict = BundleInspection?.Invoke(Game, false, TimeSpan.FromSeconds(Inputs.Client.StartSeconds))
                ?? MacBundleInspection.Inspect(Game, TimeSpan.FromSeconds(Inputs.Client.StartSeconds));
            string? refusal = MacBundleInspection.SourceRefusal(verdict);
            if (refusal != null) throw new InvalidOperationException(refusal);
        }
        // HostedRuntimeStage is the one owner of the disposable copy, its loader and the Mac bundle repair.
        // Select exactly this regression's allowlist; LocalClientCopy selects a broader installed set through the
        // same stage. No source plugin is inherited merely because it happens to be in the prepared game.
        var placements = new List<(string Role, string Source, string Sha256, string Relative, PluginAssembly Metadata)>();
        foreach (var (role, file, sha256) in sources)
            placements.Add((role, file.File, sha256, "BepInEx/plugins/" + Path.GetFileName(file.File), Read(file.File)));
        placements.Add(("mod", chosen.File, chosen.Sha256.ToLowerInvariant(), "BepInEx/plugins/" + env.Mod.InstallAs, armPlugins[arm]));
        var selected = placements.Select(file => new HostedRuntimeFile(file.Source, file.Relative)).ToList();
        selected.AddRange(patchers.Select(file => new HostedRuntimeFile(file.File.File, "BepInEx/patchers/" + Path.GetFileName(file.File.File))));
        selected.AddRange(env.Configs.Select(file => new HostedRuntimeFile(file.Value, "BepInEx/config/" + file.Key)));
        string generatedConfig = Path.Combine(Path.GetTempPath(), "vt-regression-" + Guid.NewGuid().ToString("N") + ".cfg");
        selected = CliServerConfig.Stage(selected, "Regression client", Port, generatedConfig);
        try
        {
            try { PrepareCopy(selected); }
            finally { File.Delete(generatedConfig); }
            string install = Install;
            string gameRoot = _activeGameRoot ?? throw new InvalidOperationException("The game root was not recorded after staging.");
            var staged = new List<(StagedFile File, PluginAssembly Metadata)>();
            foreach (var (role, source, sha256, relative, read) in placements)
            {
                string target = Path.Combine(install, relative.Replace('/', Path.DirectorySeparatorChar));
                RequireCopied(target, sha256);
                staged.Add((new(role, relative, sha256, FileHash.Md5(target), read.AssemblyName,
                    read.Plugins.Select(p => $"{p.Guid} {p.Version}").ToList()), read));
            }
            var allowlist = staged.Select(entry => entry.File).ToList();
            var arms = env.Mod.Arms.Select(entry => new RunManifestArm(entry.Key, entry.Value.Commit,
                $"{entry.Key}-{env.Mod.InstallAs}", entry.Value.Sha256.ToLowerInvariant(), FileHash.Md5(entry.Value.File))).ToList();
            string config = Path.Combine(install, "BepInEx", "config");
            var configs = Directory.EnumerateFiles(config).Order(StringComparer.Ordinal)
                .Select(path => new StagedFile("config", "BepInEx/config/" + Path.GetFileName(path), FileHash.Sha256(path), FileHash.Md5(path), null, [])).ToList();

            // The one declared-dependency rule (DependencyRule, as the resolver applies it), over exactly what was staged.
            string managed = Path.GetDirectoryName(InstallPins.GameAssembly(gameRoot))!;
            var provided = new[] { managed, Path.Combine(install, InstallPins.CoreDirectory) }
                .SelectMany(folder => Directory.EnumerateFiles(folder, "*.dll")).Select(Path.GetFileNameWithoutExtension).OfType<string>();
            var unmet = DependencyRule.Check(staged.Select(entry => (entry.File.Path, entry.Metadata)).ToList(), provided, Inputs.OptionalReferences, "valheim");
            if (unmet.Count != 0) throw new InvalidOperationException("The staged plugins' declared dependencies are not met: " + string.Join("; ", unmet.Select(problem => problem.Message)) + ".");
            string saveDirectory = SaveDirectory ?? HostedWorld.DefaultSaveDirectory(GameLaunch.DetectClient(gameRoot));
            string character = Path.Combine(saveDirectory, "characters_local", env.Client.Character + ".fch");
            if (env.Client.CharacterStore != null) DisposableCharacterStore.Open(env.Client.CharacterStore).Get(env.Client.Character);
            else if (!File.Exists(character))
                throw new InvalidOperationException($"client.character: {env.Client.Character}.fch is not in {Path.GetDirectoryName(character)}. Stage the disposable local character (never a cloud one) before the run, or name the one that is staged.");
            var installPins = _activePins ?? throw new InvalidOperationException("The game and loader pins were not recorded after staging.");
            var plan = new ClientRunPlan
            {
                Mode = "owned", Install = gameRoot, PreparedLoaderRoot = gameRoot == install ? null : install,
                PreparedLaunchMode = CopyGame ? "copy" : "profile",
                PreparedSourceGameRoot = CopyGame ? null : Path.GetFullPath(Game),
                PreparedSourceGameHash = CopyGame ? null : installPins.Game,
                Port = Port, Character = env.Client.Character,
                Architecture = Architecture,
                LaunchArguments = env.Client.LaunchArguments, Environment = env.Client.Environment,
                StartSeconds = env.Client.StartSeconds, JoinSeconds = env.Client.JoinSeconds,
                Pins = staged.SelectMany(file => file.Metadata.Plugins.Select(plugin => (plugin.Guid, file.File.Md5))).ToDictionary(pin => pin.Guid, pin => pin.Md5, StringComparer.Ordinal),
                InstallPins = installPins,
                CliManifest = env.Cli.Manifest, // Required (RegressionCli.Validate): the static check always runs on what was staged.
                Prepared = true, // The disposable install staged above, with the regression's ValheimCLI set.
                Capabilities = Capabilities.Except(CliCapabilities.HostedRounds).ToArray(), // The hosted rounds add their own.
                HostWorld = new HostWorldPlan
                {
                    World = new PinnedDirectory { Source = Path.GetFullPath(env.Fixture.Root), Sha256 = new Dictionary<string, string>(fixture) },
                    WorldUid = env.Fixture.WorldUid, SaveDirectory = SaveDirectory,
                },
            };
            plan.Validate();
            plan.Preflight(CliCapabilities.HostedRounds);
            string cliManifest = plan.CheckCliManifest(CliCapabilities.HostedRounds)?.ToString() ?? plan.CliPreflight;

            var tree = StagedTree(install);
            var manifest = new RunManifest(env.Name, arm, string.Join(", ", armPlugins[arm].Plugins.Select(p => p.Guid)), chosen.Commit, chosen.Sha256.ToLowerInvariant(),
                env.Mod.Repeatability, arms, allowlist, configs, installPins, identity.Name, identity.UidText, fixture.Count, Capabilities, LiveOnlyCapabilities, cliManifest);
            return new StagedArm(arm, plan, manifest, tree, install);
        }
        catch (Exception failure)
        {
            // A rejected preflight must not strand a valid but unusable game copy or a live journal entry.
            try { if (_prepared || _hostLock != null) Remove(); }
            catch (Exception cleanup) { throw new AggregateException("Regression preflight and copy cleanup both failed.", failure, cleanup); }
            throw;
        }
    }

    /// <summary>
    /// Runs one arm: stages and preflights it, writes <c>run-manifest.json</c>, then <see cref="ClientRounds"/> with
    /// <paramref name="rounds"/> and <paramref name="measure"/>, refusing a changed install right before the launch and
    /// requiring the scenario's capabilities live before its first round. Scans the client's logs and writes
    /// <c>result.json</c> and <c>junit.xml</c> in every outcome. <paramref name="output"/> must not exist yet.
    /// Call <see cref="Remove(ScenarioReport, string)"/> after the last arm to retire the copy and release the host lock.
    /// <paramref name="afterPinnedClientOpened"/> runs once the owned client has reached its menu with the selected
    /// plugins verified; a caller can record cold-start timing there without treating later world entry as plugin load.
    /// </summary>
    public ScenarioReport Run(string arm, string output, string scenario, IReadOnlyList<string> rounds, Action<ClientRound> measure,
        CancellationToken cancellation = default, Action<ScenarioReport>? afterPinnedClientOpened = null) =>
        Run(arm, output, scenario, rounds, measure, cancellation, afterPinnedClientOpened, null, null);

    /// <summary>
    /// The same run with an owned-client placement supplied by the host layer. A Windows SSH runner can use a desktop task
    /// while retaining the same staging, rounds, evidence, log scan and teardown. The opener must add its kept logs to
    /// the log collection it receives even when startup fails after the process exists. With
    /// <paramref name="deferReportWrite"/>, the caller must write the returned report after its own cleanup verdict.
    /// </summary>
    internal ScenarioReport Run(string arm, string output, string scenario, IReadOnlyList<string> rounds, Action<ClientRound> measure,
        CancellationToken cancellation, Action<ScenarioReport>? afterPinnedClientOpened,
        Func<ClientRunPlan, string, ICollection<RunLog>, CancellationToken, ClientSession>? openClient,
        Action? afterStaged,
        Action<CharacterStageEvent, string, string, string, string>? characterJournal = null,
        bool deferReportWrite = false)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (openClient == null) RequireDirectClientDesktop();
        output = Path.GetFullPath(output);
        if (Path.Exists(output)) throw new IOException($"{output} already exists; give every run a new evidence directory.");
        Directory.CreateDirectory(output);
        var report = new ScenarioReport(scenario);
        var logs = new List<RunLog>();
        RegisteredCharacterStage? characterStage = null;
        LocalClientJournal? directClientJournal = null;
        try
        {
            report.Provenance["clientEnvironment"] = ClientEnvironment;
            if (LoaderPackage is { } loaderPath)
                report.Provenance["bepInExPackage"] = BepInExLoaderPackage.Read(loaderPath).Identity;
            if (Inputs.Client.CharacterStore is { } store)
            {
                string save = SaveDirectory ?? HostedWorld.DefaultSaveDirectory(GameLaunch.DetectClient(Game));
                report.Step(StepPhase.Setup, "stage only the registered disposable character", () => characterStage = RegisteredCharacterStage.InstallRegistered(
                    store, Inputs.Client.Character, Path.Combine(save, "characters_local"),
                    SteamUserData ?? throw new DirectoryNotFoundException("No Steam userdata was detected on this machine; a registered character is checked against it for a same-named Steam Cloud character."),
                    Inputs.Client.Character, characterJournal ?? JournalCharacterEvent));
                report.Provenance["characterSource"] = "registered disposable store";
            }
            StagedArm? stagedArm = null;
            report.Step(StepPhase.Setup, $"stage arm {arm} from the allowlist and preflight it, before the game starts", () => stagedArm = Stage(arm));
            afterStaged?.Invoke();
            var staged = stagedArm!;
            staged.Record(report.Provenance);
            report.Provenance["launchMode"] = CopyGame ? "copy" : "profile";
            report.Provenance["disposableStageSeconds"] = _lastStageSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(output, "run-manifest.json"), JsonSerializer.Serialize(staged.Manifest, ManifestJson));
            new ClientRounds { Client = staged.Plan, Report = report, Output = output, Rounds = rounds, Cancellation = cancellation }.Run(() =>
            {
                staged.Verify();
                ClientSession client;
                if (openClient == null)
                {
                    // Direct consumers must record the exact process too, not only the staged copy.
                    directClientJournal = new LocalClientJournal(staged.Plan, output, desktopTask: false);
                    client = ClientSession.Open(staged.Plan, output, logs, cancellation,
                        directClientJournal.Begin, directClientJournal.Started);
                }
                else client = openClient(staged.Plan, output, logs, cancellation);
                report.RecordPlugins("client", staged.Plan.Pins); // confirmed at its menu
                if (LoaderPackage != null)
                {
                    report.Provenance["bepInExMenuSmoke"] = "passed: fresh BepInEx log, pinned plugins and main menu";
                }
                afterPinnedClientOpened?.Invoke(report);
                return client;
            }, round =>
            {
                // ValheimCLI's own commands were required when the client answered; a probe's are live once it registered them.
                if (round.Index == 0 && LiveOnlyCapabilities.Count != 0)
                    round.Step(StepPhase.Setup, "the client offers the scenario's probe and mod commands", () => CliCapabilities.Require(round.Client, LiveOnlyCapabilities));
                measure(round);
            });
        }
        catch (Exception error)
        {
            if (report.Steps.All(step => step.Passed)) report.RecordFailure("runner failed", error);
            Console.Error.WriteLine(error.Message);
        }
        finally
        {
            if (directClientJournal != null)
                try { directClientJournal.Complete(); }
                catch (Exception error) { report.RecordFailure(StepPhase.Cleanup, "owned client process was not proved stopped", error); }
            if (characterStage != null)
                try { report.Step(StepPhase.Cleanup, "remove only the staged test character", characterStage.Dispose); }
                catch (Exception error) { if (report.Steps.All(step => step.Passed)) report.RecordFailure(StepPhase.Cleanup, "character cleanup failed", error); }
            if (logs.Count != 0) report.ScanLogs(logs, Inputs.LogScan);
            // A failed preflight can already have retired its copy. Whoever removes a successful arm records that later.
            report.Provenance["disposableInstall"] = _prepared ? "kept after this arm" : "no owned runtime left after this arm";
            // The one-shot command adds its process-journal and final copy-cleanup verdict before writing.
            // Direct API callers retain the usual report-on-every-outcome behavior.
            if (!deferReportWrite) report.Write(output);
        }
        return report;
    }

    private void JournalCharacterEvent(CharacterStageEvent point, string characters, string userData, string name, string expectedSha256) =>
        _journal.AppendLocal("client", CharacterEntry(point, characters, userData, name, expectedSha256));

    internal static JournalEntry CharacterEntry(CharacterStageEvent point, string characters, string userData, string name, string expectedSha256) =>
        point switch
        {
            CharacterStageEvent.Intended => JournalEntry.Of(JournalEntry.CharacterIntended,
                ("characters", characters), ("userData", userData), ("fileName", name),
                ("characterKind", "regression"), ("local", "true"), ("expectedSha256", expectedSha256)),
            CharacterStageEvent.Done => JournalEntry.Of(JournalEntry.CharacterDone,
                ("fileName", name), ("staged", "true")),
            _ => JournalEntry.Of(JournalEntry.CharacterRetired, ("fileName", name)),
        };

    /// <summary>
    /// Deletes the disposable install as a <see cref="StepPhase.Cleanup"/> step of the last arm's report, records what
    /// happened to it (<c>disposableInstall</c>) and writes that report again to <paramref name="lastArmOutput"/>, so its
    /// <c>result.json</c> says whether cleanup was verified. Rethrows a refusal after recording it.
    /// </summary>
    public void Remove(ScenarioReport lastArm, string lastArmOutput)
    {
        ArgumentNullException.ThrowIfNull(lastArm);
        try
        {
            lastArm.Step(StepPhase.Cleanup, "remove the disposable install", Remove);
            lastArm.Provenance["disposableInstall"] = "removed";
        }
        catch (Exception error)
        {
            lastArm.Provenance["disposableInstall"] = "kept, removal refused: " + error.Message;
            throw;
        }
        finally { lastArm.Write(lastArmOutput); }
    }

    /// <summary>Retires the copy made by this runner through the shared hosted-runtime owner.</summary>
    public void Remove()
    {
        if (!_prepared)
        {
            if (Directory.Exists(Install) || Directory.Exists(_stage))
                throw new InvalidOperationException($"The disposable install or staging directory at {Install} was not proven cleaned by this runner. Use env recover for a run that was interrupted; this runner never removes a copy by its name alone.");
        }
        else RetirePrepared();
        if (_hostLock is { } held)
        {
            var released = held.ReleaseAsync().GetAwaiter().GetResult();
            if (released.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(released);
            _journal.AppendLocal("run", JournalEntry.Of(JournalEntry.LockReleased, ("lock", held.Path), ("claimant", held.Owner)));
            _hostLock = null;
        }
    }

    internal static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void PrepareCopy(IReadOnlyList<HostedRuntimeFile> selected)
    {
        string source = Path.GetFullPath(Game);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"game: {source} does not exist. Give the Valheim install for the disposable run.");
        var package = LoaderPackage == null ? null : BepInExLoaderPackage.Read(LoaderPackage);
        if (package != null && (RegressionInputs.Inside(package.Root, source) || RegressionInputs.Inside(source, package.Root)))
            throw new InvalidOperationException($"The pinned BepInEx package {package.Root} overlaps the game {source}; extract one reviewed loader set outside the live game before staging.");
        if (package != null && (RegressionInputs.Inside(package.Root, Install) || RegressionInputs.Inside(Install, package.Root)))
            throw new InvalidOperationException($"The disposable install {Install} overlaps the pinned BepInEx package {package.Root}; keep the package outside the install so cleanup cannot delete it.");
        if (package == null && !Directory.Exists(Path.Combine(source, InstallPins.CoreDirectory)))
            throw new InvalidOperationException($"game: {source} has no {InstallPins.CoreDirectory}. Install BepInEx (BepInExPack_Valheim) in the prepared game first, or name a reviewed loader package.");
        if (Inputs.GamePins != null)
        {
            var chosen = new InstallPins { Game = InstallPins.GameHash(source),
                Loader = package?.Loader ?? InstallPins.Of(source).Loader, Patchers = Inputs.GamePins.Patchers };
            Inputs.GamePins.Compare(chosen, "prepared game and selected loader package", "Managed");
        }
        if (_prepared) RetirePrepared();
        else if (Directory.Exists(Install) || Directory.Exists(_stage))
            throw new IOException($"The disposable install already exists: {Install}. Inspect env status and recover its owner; it is never replaced by name.");
        // Keep the host exclusive from the first copy through the final arm's client and copy retirement.
        // The journal lets env recover release a lock left by an interrupted direct consumer.
        if (_hostLock == null && !SkipHostLockForTest)
        {
            var held = _host.AcquireLockAsync(_lockPath, "regression " + _journal.RunId, HostedTimeouts.Quick).GetAwaiter().GetResult();
            try { _journal.AppendLocal("run", JournalEntry.Of(JournalEntry.LockHeld, ("lock", held.Path), ("claimant", held.Owner))); }
            catch
            {
                held.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
            _hostLock = held;
        }
        RequireStopped(null, clientSession: true);
        string parent = Path.GetDirectoryName(Install)!;
        Directory.CreateDirectory(Path.GetDirectoryName(parent)!);
        if (CopyGame)
        {
            var capacity = HostCopyCapacityProbe.InspectAsync(_host, source, Path.GetDirectoryName(parent)!, LocalClientCopy.StepTimeout).GetAwaiter().GetResult();
            HostCopyCapacityProbe.RequireCombined(_host.Name, [(Inputs.Name, capacity)]);
        }
        string actor = "regression-" + Inputs.Name;
        _journal.AppendLocal(actor, JournalEntry.Of(JournalEntry.CopyIntended,
            ("runtime", Install), ("stage", _stage), ("parent", parent), ("launchMode", CopyGame ? "copy" : "profile")));
        try
        {
            Func<IGameHost, string, TimeSpan, CancellationToken, Task<MacBundleInspection.Verdict>>? repair = BundleInspection == null
                ? null : (_, install, timeout, _) => Task.FromResult(BundleInspection(install, true, timeout));
            var copyClock = Stopwatch.StartNew();
            HostListing listing;
            if (CopyGame)
            {
                listing = HostedRuntimeStage.PrepareAsync(_host, HostedRuntimeKind.Client, source, Install, _stage,
                    selected, LocalClientCopy.StepTimeout, loaderPackage: package, repairMac: repair).GetAwaiter().GetResult();
                _activeGameRoot = Install;
                _activePins = HostInstall.Pins(listing);
            }
            else
            {
                var profile = HostedRuntimeStage.PrepareProfileAsync(_host, HostedRuntimeKind.Client, source, Install, _stage,
                    selected, LocalClientCopy.StepTimeout, loaderPackage: package).GetAwaiter().GetResult();
                listing = profile.Loader;
                _activeGameRoot = profile.GameRoot;
                _activePins = profile.Pins;
            }
            _lastStageSeconds = copyClock.Elapsed.TotalSeconds;
            _journal.AppendLocal(actor, JournalEntry.Of(JournalEntry.CopyDone, ("runtime", Install),
                ("files", listing.Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("source", source),
                ("launchMode", CopyGame ? "copy" : "profile")));
            _prepared = true;
        }
        catch (Exception error) when (error is not AggregateException)
        {
            // HostedRuntimeStage proved cleanup before it rethrew. An aggregate failure retains the journal for recovery.
            if (!Directory.Exists(Install) && !Directory.Exists(_stage))
                _journal.AppendLocal(actor, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", Install), ("failed", "true")));
            throw;
        }
    }

    private void RetirePrepared()
    {
        RequireStopped([_activeGameRoot ?? Install], clientSession: false);
        HostedRuntimeStage.RetireAsync(_host, Install, _stage, LocalClientCopy.StepTimeout).GetAwaiter().GetResult();
        _journal.AppendLocal("regression-" + Inputs.Name, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", Install)));
        _prepared = false;
        _activeGameRoot = null;
        _activePins = null;
    }

    private void RequireStopped(IReadOnlyCollection<string>? runtimes, bool clientSession)
    {
        if (ProcessCheck is { } controlled) controlled(runtimes, clientSession).GetAwaiter().GetResult();
        else HostedRuntimeStage.RequireStoppedAsync(_host, LocalClientCopy.StepTimeout,
            runtimes: runtimes, clientSession: clientSession).GetAwaiter().GetResult();
    }

    /// <summary>Every file under the install's <c>BepInEx/plugins</c>, <c>patchers</c>, <c>config</c> and <c>scripts</c>, by relative path and SHA256.</summary>
    internal static Dictionary<string, string> StagedTree(string install)
    {
        var tree = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string folder in StagedFolders)
        {
            string root = Path.Combine(install, "BepInEx", folder);
            if (!Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => !FileHash.IsMacMetadata(path)))
                tree[Path.GetRelativePath(install, file).Replace('\\', '/')] = FileHash.Sha256(file);
        }
        return tree;
    }

    // ---- hashes ----

    private static string RequirePinned(string path, string sha256, string field)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{field}: {path} does not exist. Build or download it, or correct the path.", path);
        string actual = FileHash.Sha256(path);
        if (sha256.Length == 0)
            throw new InvalidOperationException($"{field}: pin {path} by its SHA256. It is {actual} now; check that this is the build you intend, then add \"sha256\": \"{actual}\".");
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{field}: {path} is sha256 {actual}, but the manifest pins {sha256.ToLowerInvariant()}: it is another build than the one reviewed. Stage the pinned build, or review this one and pin {actual}.");
        return actual;
    }

    private static void RequireArm(string name, RegressionArm arm)
    {
        if (!File.Exists(arm.File)) throw new FileNotFoundException($"mod.arms.{name}: {arm.File} does not exist. Build {(arm.Commit != null ? "commit " + arm.Commit : "it")}, or correct the path.", arm.File);
        string actual = FileHash.Sha256(arm.File);
        if (!actual.Equals(arm.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"mod.arms.{name}: {arm.File} is sha256 {actual}, but the manifest pins {arm.Sha256.ToLowerInvariant()}{(arm.Commit != null ? " for commit " + arm.Commit : "")}: " +
                $"the file is another build. Rebuild {name}{(arm.Commit != null ? " from " + arm.Commit : "")} and stage that file, or review this one and pin {actual}.");
    }

    private static void RequireCopied(string target, string sha256)
    {
        if (!FileHash.Sha256(target).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException($"{target} changed while it was copied; stage again.");
    }

    // ---- what the staged DLLs declare ----

    private static string Declared(PluginAssembly assembly) =>
        assembly.Plugins.Count == 0 ? "no plugin" : string.Join(", ", assembly.Plugins.Select(plugin => plugin.Guid));
}
