using System.Text.Json;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// The inputs of a targeted native regression: one owned client hosting one disposable world, one mod under test in two
/// or more arms (conventionally <c>parent</c> and <c>candidate</c>), its runtime dependencies and an optional game-side
/// probe. Where it runs is not here: the client's install, port, disposable copy, save folders and loader come from the
/// environment inventory's client environment (<see cref="TargetedRegression"/>), this machine when there is no file.
/// The scenario source (the rounds and their assertions) stays free of both, so the same source can be shared.
/// <see cref="Read"/> resolves relative paths against the file's own directory and refuses unknown fields.
/// </summary>
public sealed class RegressionInputs
{
    /// <summary>A short name for the run: letters, digits, <c>-</c> and <c>_</c>.</summary>
    public string Name { get; set; } = "";
    public RegressionClient Client { get; set; } = new();
    public RegressionFixture Fixture { get; set; } = new();
    public RegressionCli Cli { get; set; } = new();
    /// <summary>Every other DLL the run loads: the mod's dependencies and any plugin the scenario needs. A library (no plugin) may be listed.</summary>
    public List<RegressionFile> Plugins { get; set; } = [];
    /// <summary>An optional game-side test probe: a plugin that serves the scenario's observations.</summary>
    public RegressionFile? Probe { get; set; }
    public RegressionMod Mod { get; set; } = new();
    /// <summary>Config files to place in <c>BepInEx/config</c>, by file name. Without <c>valheimCLI.valheimCLI.cfg</c> one is written with the client's port.</summary>
    public Dictionary<string, string> Configs { get; set; } = [];
    /// <summary>BepInEx preloader patchers to place in <c>BepInEx/patchers</c>; none by default.</summary>
    public List<RegressionFile> Patchers { get; set; } = [];
    /// <summary>Assembly names a staged DLL references but only uses when present (a guarded soft integration); none by default.</summary>
    public List<string> OptionalReferences { get; set; } = [];
    /// <summary>Reasoned expected log lines and patterns of the run's own for this native regression (<see cref="LogClassification"/>); unclassified BepInEx errors fail by default.</summary>
    public Dictionary<string, LogClassification> LogScan { get; set; } = [];
    /// <summary>
    /// Optional: the game build and loader the client environment's install must have (<see cref="InstallPins"/>; its patchers
    /// value is not compared). With a loader package, the loader is the package's (<see cref="BepInExLoaderPackage.Loader"/>).
    /// </summary>
    public InstallPins? GamePins { get; set; }

    private static readonly Regex Token = new(@"^[A-Za-z0-9][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant);

    // Fields of the retired environment manifest that described the machine, and where each now comes from.
    private static readonly (string Path, string Now)[] Retired =
    [
        ("game", "the inventory's client environment's install (this machine's Valheim with no file; --game or an environments.json entry overrides it)"),
        ("install", "the client environment's runtime (the disposable copy is made there)"),
        ("loaderPackage", "the client environment's loaderPackage"),
        ("client.port", "the client environment's cliPort"),
        ("client.saveDirectory", "the client's host (its standard save folder)"),
        ("client.steamUserDataDirectory", "this machine's detected Steam userdata"),
    ];

    /// <summary>Reads and validates the inputs; relative paths are relative to the file's directory. A retired machine field is refused, naming where it now comes from.</summary>
    public static RegressionInputs Read(string path)
    {
        path = Path.GetFullPath(path);
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException error) { throw new ArgumentException($"{path} is not a regression's inputs: {error.Message}", error); }
        using (var document = parsed)
        {
            var found = new List<string>();
            foreach (var (field, now) in Retired)
            {
                var element = document.RootElement;
                bool present = true;
                foreach (string part in field.Split('.'))
                {
                    if (element.ValueKind != JsonValueKind.Object) { present = false; break; }
                    var match = element.EnumerateObject().FirstOrDefault(property => property.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                    if (match.Value.ValueKind == JsonValueKind.Undefined) { present = false; break; }
                    element = match.Value;
                }
                if (present) found.Add($"{field} now comes from {now}");
            }
            if (found.Count != 0)
                throw new ArgumentException($"{path} is a retired environment manifest: it names the machine, which the environment inventory now describes. Remove " +
                    string.Join("; ", found) + ".");
        }
        RegressionInputs inputs;
        try { inputs = ClientPlanFile.Read<RegressionInputs>(path); }
        catch (JsonException error) { throw new ArgumentException($"{path} is not a regression's inputs: {error.Message}", error); }
        inputs.Resolve(Path.GetDirectoryName(path)!);
        inputs.Validate();
        return inputs;
    }

    /// <summary>Writes the inputs as indented JSON.</summary>
    public void Write(string path) => ClientPlanFile.Write(path, this);

    private void Resolve(string directory)
    {
        string Full(string value) => value.Length == 0 || Path.IsPathFullyQualified(value) ? value : Path.GetFullPath(Path.Combine(directory, value));
        Fixture.Root = Full(Fixture.Root);
        if (Client.CharacterStore != null) Client.CharacterStore = Full(Client.CharacterStore);
        foreach (var file in Files()) file.File = Full(file.File);
        foreach (var arm in Mod.Arms.Values) arm.File = Full(arm.File);
        foreach (string key in Configs.Keys.ToList()) Configs[key] = Full(Configs[key]);
        if (Cli.Manifest != null) Cli.Manifest = Full(Cli.Manifest);
    }

    private IEnumerable<RegressionFile> Files() =>
        new[] { Cli.Core }.Concat(Cli.Packs).Concat(Plugins).Concat(Probe == null ? [] : [Probe]).Concat(Patchers);

    /// <summary>Refuses inputs that cannot describe one clean run, naming the field and the fix. Reads no file.</summary>
    public void Validate()
    {
        if (!Token.IsMatch(Name)) throw new ArgumentException("name: give the run a short name of letters, digits, - and _.");
        Client.Validate();
        Fixture.Validate();
        Cli.Validate();
        foreach (var (file, field) in Plugins.Select((file, i) => (file, $"plugins[{i}]"))) file.Validate(field);
        Probe?.Validate("probe");
        foreach (var (file, field) in Patchers.Select((file, i) => (file, $"patchers[{i}]"))) file.Validate(field);
        Mod.Validate();
        foreach (var (name, source) in Configs)
        {
            if (name.Length == 0 || name != Path.GetFileName(name) || !name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"configs: \"{name}\" is not a BepInEx config file name (<plugin guid>.cfg, no folder).");
            if (!Path.IsPathFullyQualified(source)) throw new ArgumentException($"configs.{name}: give the file to copy.");
        }
        foreach (string reference in OptionalReferences)
            if (string.IsNullOrWhiteSpace(reference) || reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"optionalReferences: \"{reference}\" is not an assembly name (no .dll).");
        LogScanner.CheckClassifications(LogScan);
        GamePins?.Validate("game");
        var names = Files().Where(file => !Patchers.Contains(file)).Select(file => Path.GetFileName(file.File)).Append(Mod.InstallAs)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (names != null) throw new ArgumentException($"Two staged plugins are both named {names.Key} in BepInEx/plugins; rename one copy, or list the file once.");
    }

    internal static bool Inside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    internal static void RequireToken(string value, string field)
    {
        if (!Token.IsMatch(value)) throw new ArgumentException($"{field}: \"{value}\" is not a name of letters, digits, - and _.");
    }
}

/// <summary>The owned client's disposable character and launch options; its install and port are the client environment's.</summary>
public sealed class RegressionClient
{
    /// <summary>An existing disposable <b>local</b> character's file name without <c>.fch</c>, never a cloud character.</summary>
    public string Character { get; set; } = "";
    public string[] LaunchArguments { get; set; } = [];
    public int StartSeconds { get; set; } = 300;
    public int JoinSeconds { get; set; } = 180;
    /// <summary>Optional registered, game-created disposable character to stage for the owned hosted run (checked against this machine's Steam userdata).</summary>
    public string? CharacterStore { get; set; }

    public void Validate()
    {
        if (Character.Length == 0 || Character.Any(char.IsWhiteSpace) || Character.EndsWith(".fch", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("client.character: give the disposable local character's file name, without .fch.");
        if (CharacterStore != null && !Path.IsPathFullyQualified(CharacterStore)) throw new ArgumentException("client.characterStore: give the full path of a registered disposable character store.");
    }
}

/// <summary>The hosted fixture: a directory that holds exactly one world (<see cref="FixtureLayout.ExpectedTree"/>), and that world's UID.</summary>
public sealed class RegressionFixture
{
    public string Root { get; set; } = "";
    public string WorldUid { get; set; } = "";
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(Root)) throw new ArgumentException("fixture.root: give the directory that holds the one world folder.\n" + FixtureLayout.ExpectedTree);
        if (!long.TryParse(WorldUid, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("fixture.worldUid: give the fixture world's exact UID (WorldIdentity.Read(root).UidText), never one from its name.");
    }
}

/// <summary>The ValheimCLI core and only the packs the scenario needs, all staged in <c>BepInEx/plugins</c>.</summary>
public sealed class RegressionCli
{
    public RegressionFile Core { get; set; } = new();
    public List<RegressionFile> Packs { get; set; } = [];
    /// <summary>
    /// The <see cref="CliCapabilityManifest"/> of this ValheimCLI build. Set, the staged core and packs must be exactly its set
    /// and provide every ValheimCLI capability the run uses before anything launches; left out, only the live check runs.
    /// </summary>
    public string? Manifest { get; set; }
    public void Validate()
    {
        Core.Validate("cli.core");
        foreach (var (file, field) in Packs.Select((file, i) => (file, $"cli.packs[{i}]"))) file.Validate(field);
        if (Manifest != null && !Path.IsPathFullyQualified(Manifest)) throw new ArgumentException("cli.manifest: give the capability manifest's path.");
    }
}

/// <summary>A file to stage, by path and its SHA256.</summary>
public sealed class RegressionFile
{
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public void Validate(string field)
    {
        if (!Path.IsPathFullyQualified(File)) throw new ArgumentException($"{field}.file: give the DLL to stage.");
        if (Sha256.Length != 0 && (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))) throw new ArgumentException($"{field}.sha256: give the file's full SHA256.");
    }
}

/// <summary>The mod under test: the file name it is installed as and its arms, which differ only in this DLL.</summary>
public sealed class RegressionMod
{
    /// <summary>The DLL's name in <c>BepInEx/plugins</c>, the same for every arm.</summary>
    public string InstallAs { get; set; } = "";
    /// <summary>The builds compared, by name (conventionally <c>parent</c> and <c>candidate</c>), in run order.</summary>
    public Dictionary<string, RegressionArm> Arms { get; set; } = [];
    /// <summary>Deliberately runs the same build in two arms (a repeatability run); otherwise two arms with one hash are refused.</summary>
    public bool Repeatability { get; set; }

    public void Validate()
    {
        if (InstallAs.Length == 0 || InstallAs != Path.GetFileName(InstallAs) || !InstallAs.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("mod.installAs: give the DLL's file name in BepInEx/plugins (no folder), the same for every arm.");
        if (Arms.Count == 0) throw new ArgumentException("mod.arms: name at least one arm, for example parent and candidate.");
        foreach (var (name, arm) in Arms) { RegressionInputs.RequireToken(name, "mod.arms"); arm.Validate("mod.arms." + name); }
        foreach (var same in Arms.GroupBy(arm => arm.Value.Sha256, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            if (!Repeatability)
                throw new ArgumentException($"mod.arms {string.Join(" and ", same.Select(arm => arm.Key))} are the same build (sha256 {same.Key}): comparing a build with itself proves nothing. " +
                    "Stage each arm's own build (the parent commit's for the parent), or set mod.repeatability to true for a deliberate repeatability run.");
    }
}

/// <summary>One arm's build of the mod under test: the file, its SHA256 and the source commit it was built from.</summary>
public sealed class RegressionArm
{
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>The source commit the arm was built from, when known; never a stand-in such as a file hash.</summary>
    public string? Commit { get; set; }
    public void Validate(string field)
    {
        if (!Path.IsPathFullyQualified(File)) throw new ArgumentException($"{field}.file: give the arm's built DLL.");
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)) throw new ArgumentException($"{field}.sha256: pin the arm's build by its full SHA256; the hash is what tells the arms apart.");
        if (Commit != null && (Commit.Length == 0 || Commit.Any(char.IsWhiteSpace))) throw new ArgumentException($"{field}.commit: give the source commit the arm was built from, or leave it out when it is not known.");
    }
}

/// <summary>One DLL in the staged allowlist, as the run manifest records it (install-relative path, no machine path).</summary>
[ResultShape]
public sealed record StagedFile(string Role, string Path, string Sha256, string Md5, string? Assembly, IReadOnlyList<string> Plugins);

/// <summary>
/// What one arm's run loads, for review and evidence: only the allowlist and the exact staged hashes; no machine path and no
/// pin for any plugin outside the allowlist.
/// </summary>
/// <param name="Capabilities">Every ValheimCLI extension command the run uses: checked against <c>cli.manifest</c> before launch, when set, and live.</param>
/// <param name="LiveOnlyCapabilities">The scenario's commands of other owners (a probe's or the mod's extensions): checked live only.</param>
/// <param name="CliManifest">What the static ValheimCLI check found, or why none ran.</param>
[ResultShape]
public sealed record RunManifest(string Name, string Arm, string ModPlugin, string? ModCommit, string ModSha256, bool Repeatability,
    IReadOnlyList<RunManifestArm> Arms, IReadOnlyList<StagedFile> Allowlist, IReadOnlyList<StagedFile> Configs,
    InstallPins InstallPins, string World, string WorldUid, int FixtureFiles, IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> LiveOnlyCapabilities, string CliManifest);

/// <summary>One arm of the comparison: its name, commit, distinct artifact name and SHA256.</summary>
[ResultShape]
public sealed record RunManifestArm(string Arm, string? Commit, string Artifact, string Sha256, string Md5);

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
/// <item>Every staged file must be its pinned SHA256; each arm is copied to its own artifact name first.</item>
/// <item>The disposable install is a copy of the prepared game with <c>BepInEx/plugins</c>, <c>patchers</c>, <c>config</c>
/// and <c>scripts</c> rebuilt from the allowlist only: the ValheimCLI core and packs, the plugins, the probe and one arm.</item>
/// <item>Every hard <c>[BepInDependency]</c> any staged plugin declares must be met by a staged plugin's own
/// <c>[BepInPlugin]</c> (with its minimum version), no <c>[BepInIncompatibility]</c> may be staged, every plugin must load in
/// the client process, and every assembly reference must resolve to the game, BepInEx or a staged DLL. File names count for nothing.</item>
/// <item>The plan's own <see cref="ClientRunPlan.Validate"/> and <see cref="ClientRunPlan.Preflight(IEnumerable{string})"/> run on the
/// staged install, including the static ValheimCLI capability check against <see cref="RegressionCli.Manifest"/> when set.</item>
/// </list>
/// Nothing launches. <see cref="Run"/> repeats the staging for one arm, refuses any file outside the allowlist right before
/// the launch, and keeps <see cref="ClientRounds"/>' live capability check and strict per-command pins as the second gate.
/// </summary>
public sealed class TargetedRegression
{
    /// <summary>The file that marks a disposable install as this tool's; an install without it is never changed.</summary>
    public const string MarkerFile = "valheim-testing-install.json";
    /// <summary>Where each arm's build is copied under its own artifact name, inside the disposable install.</summary>
    public const string ArtifactsDirectory = "valheim-testing-artifacts";
    internal const string CliPlugin = "valheimCLI.valheimCLI", CliConfig = "valheimCLI.valheimCLI.cfg", BepInExConfig = "BepInEx.cfg";
    private static readonly string[] StagedFolders = ["plugins", "patchers", "config", "scripts"];
    // What a copy of the prepared install leaves out: everything BepInEx loads or writes besides its core.
    // ValheimCLI's own extension owners: the Standard and World Tools packs register valheim.* and cli.*.
    private static bool OwnedByCli(string path) => path.StartsWith("valheim.", StringComparison.Ordinal) || path.StartsWith("cli.", StringComparison.Ordinal);
    private static readonly string[] NotCopied = ["plugins", "patchers", "config", "scripts", "cache", "DumpedAssemblies", "LogOutput.log", "LogOutput.log.1", "LogOutput.log.2"];

    public RegressionInputs Inputs { get; }
    /// <summary>The client environment the run uses (<see cref="EnvironmentRecipe.Name"/>) and why.</summary>
    public string ClientEnvironment { get; }
    /// <summary>The prepared Valheim install the disposable copy is made from: the client environment's install. Only read.</summary>
    public string Game { get; }
    /// <summary>The disposable install this runner creates and owns, in the client environment's runtime.</summary>
    public string Install { get; }
    /// <summary>The client's ValheimCLI port: the client environment's.</summary>
    public int Port { get; }
    /// <summary>The client environment's loader package, when it names one.</summary>
    public string? LoaderPackage { get; }
    /// <summary>What the inventory detected and assumed for this machine (<see cref="EnvironmentInventory.Detected"/>), for a caller to print.</summary>
    public IReadOnlyList<string> Detected { get; }
    /// <summary>This machine's detected Steam <c>userdata</c>, which a registered character is checked against; null when none was detected.</summary>
    public string? SteamUserData { get; internal init; }
    // The client's save root (worlds_local, characters_local): this user's by default; tests replace it.
    internal string? SaveDirectory { get; init; }
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
        Game = recipe.Install; Port = recipe.CliPort; LoaderPackage = recipe.LoaderPackage;
        Install = Path.Combine(recipe.Runtime, "regression-" + inputs.Name);
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

    /// <summary>Stages and preflights every arm in turn, without the game; returns them in manifest order (the last stays staged).</summary>
    public IReadOnlyList<StagedArm> Preflight() => Inputs.Mod.Arms.Keys.Select(Stage).ToList();

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

        string install = PrepareInstall();
        string plugins = Path.Combine(install, "BepInEx", "plugins");
        var staged = new List<(StagedFile File, PluginAssembly Metadata)>();
        void Place(string role, string source, string sha256, string name)
        {
            string target = Path.Combine(plugins, name);
            File.Copy(source, target);
            RequireCopied(target, sha256);
            var read = Read(source);
            staged.Add((new(role, "BepInEx/plugins/" + name, sha256, FileHash.Md5(target), read.AssemblyName, read.Plugins.Select(p => $"{p.Guid} {p.Version}").ToList()), read));
        }
        foreach (var (role, file, sha256) in sources) Place(role, file.File, sha256, Path.GetFileName(file.File));

        // Each arm under its own artifact name, then only the chosen one installed.
        string artifacts = Path.Combine(install, ArtifactsDirectory);
        if (Directory.Exists(artifacts)) Directory.Delete(artifacts, recursive: true);
        Directory.CreateDirectory(artifacts);
        var arms = new List<RunManifestArm>();
        foreach (var (name, build) in env.Mod.Arms)
        {
            string artifact = $"{name}-{env.Mod.InstallAs}";
            File.Copy(build.File, Path.Combine(artifacts, artifact));
            RequireCopied(Path.Combine(artifacts, artifact), build.Sha256);
            arms.Add(new(name, build.Commit, artifact, build.Sha256.ToLowerInvariant(), FileHash.Md5(Path.Combine(artifacts, artifact))));
        }
        Place("mod", Path.Combine(artifacts, $"{arm}-{env.Mod.InstallAs}"), chosen.Sha256.ToLowerInvariant(), env.Mod.InstallAs);
        var allowlist = staged.Select(entry => entry.File).ToList();

        foreach (var (file, sha256) in patchers)
        {
            string target = Path.Combine(install, "BepInEx", "patchers", Path.GetFileName(file.File));
            File.Copy(file.File, target);
            RequireCopied(target, sha256);
        }
        string config = Path.Combine(install, "BepInEx", "config");
        foreach (var (name, source) in env.Configs) File.Copy(source, Path.Combine(config, name), overwrite: true);
        if (!env.Configs.ContainsKey(CliConfig))
            File.WriteAllText(Path.Combine(config, CliConfig), $"[Server]\nEnabled = true\nPort = {Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
        var configs = Directory.EnumerateFiles(config).Order(StringComparer.Ordinal)
            .Select(path => new StagedFile("config", "BepInEx/config/" + Path.GetFileName(path), FileHash.Sha256(path), FileHash.Md5(path), null, [])).ToList();

        // The one declared-dependency rule (DependencyRule, as the resolver applies it), over exactly what was staged.
        string managed = Path.GetDirectoryName(InstallPins.GameAssembly(install))!;
        var provided = new[] { managed, Path.Combine(install, InstallPins.CoreDirectory) }
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.dll")).Select(Path.GetFileNameWithoutExtension).OfType<string>();
        var unmet = DependencyRule.Check(staged.Select(entry => (entry.File.Path, entry.Metadata)).ToList(), provided, Inputs.OptionalReferences, "valheim");
        if (unmet.Count != 0) throw new InvalidOperationException("The staged plugins' declared dependencies are not met: " + string.Join("; ", unmet.Select(problem => problem.Message)) + ".");
        string saveDirectory = SaveDirectory ?? HostedWorld.DefaultSaveDirectory(GameLaunch.DetectClient(install));
        string character = Path.Combine(saveDirectory, "characters_local", env.Client.Character + ".fch");
        if (env.Client.CharacterStore != null) DisposableCharacterStore.Open(env.Client.CharacterStore).Get(env.Client.Character);
        else if (!File.Exists(character))
            throw new InvalidOperationException($"client.character: {env.Client.Character}.fch is not in {Path.GetDirectoryName(character)}. Stage the disposable local character (never a cloud one) before the run, or name the one that is staged.");
        var installPins = InstallPins.Of(install);
        var plan = new ClientRunPlan
        {
            Mode = "owned", Install = install, Port = Port, Character = env.Client.Character,
            LaunchArguments = env.Client.LaunchArguments, StartSeconds = env.Client.StartSeconds, JoinSeconds = env.Client.JoinSeconds,
            Pins = staged.SelectMany(file => file.Metadata.Plugins.Select(plugin => (plugin.Guid, file.File.Md5))).ToDictionary(pin => pin.Guid, pin => pin.Md5, StringComparer.Ordinal),
            InstallPins = installPins,
            CliManifest = env.Cli.Manifest,
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
        WriteMarker(install, arm, tree);
        var manifest = new RunManifest(env.Name, arm, string.Join(", ", armPlugins[arm].Plugins.Select(p => p.Guid)), chosen.Commit, chosen.Sha256.ToLowerInvariant(),
            env.Mod.Repeatability, arms, allowlist, configs, installPins, identity.Name, identity.UidText, fixture.Count, Capabilities, LiveOnlyCapabilities, cliManifest);
        return new StagedArm(arm, plan, manifest, tree, install);
    }

    /// <summary>
    /// Runs one arm: stages and preflights it, writes <c>run-manifest.json</c>, then <see cref="ClientRounds"/> with
    /// <paramref name="rounds"/> and <paramref name="measure"/>, refusing a changed install right before the launch and
    /// requiring the scenario's capabilities live before its first round. Scans the client's logs and writes
    /// <c>result.json</c> and <c>junit.xml</c> in every outcome. <paramref name="output"/> must not exist yet.
    /// <paramref name="afterPinnedClientOpened"/> runs once the owned client has reached its menu with the selected
    /// plugins verified; a caller can record cold-start timing there without treating later world entry as plugin load.
    /// </summary>
    public ScenarioReport Run(string arm, string output, string scenario, IReadOnlyList<string> rounds, Action<ClientRound> measure,
        CancellationToken cancellation = default, Action<ScenarioReport>? afterPinnedClientOpened = null)
    {
        ArgumentNullException.ThrowIfNull(measure);
        output = Path.GetFullPath(output);
        if (Path.Exists(output)) throw new IOException($"{output} already exists; give every run a new evidence directory.");
        Directory.CreateDirectory(output);
        var report = new ScenarioReport(scenario);
        report.Provenance["toolkit"] = ToolkitVersion; // A public bundle must not pin an older toolkit than this.
        var logs = new List<RunLog>();
        RegisteredCharacterStage? characterStage = null;
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
                    Inputs.Client.Character));
                report.Provenance["characterSource"] = "registered disposable store";
            }
            StagedArm? stagedArm = null;
            report.Step(StepPhase.Setup, $"stage arm {arm} from the allowlist and preflight it, before the game starts", () => stagedArm = Stage(arm));
            var staged = stagedArm!;
            staged.Record(report.Provenance);
            File.WriteAllText(Path.Combine(output, "run-manifest.json"), JsonSerializer.Serialize(staged.Manifest, ManifestJson));
            new ClientRounds { Client = staged.Plan, Report = report, Output = output, Rounds = rounds, Cancellation = cancellation }.Run(() =>
            {
                staged.Verify();
                var client = ClientSession.Open(staged.Plan, output, logs, cancellation);
                if (LoaderPackage != null)
                {
                    MarkLoaderSmoke(staged.Plan.Install);
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
            if (characterStage != null)
                try { report.Step(StepPhase.Cleanup, "remove only the staged test character", characterStage.Dispose); }
                catch (Exception error) { if (report.Steps.All(step => step.Passed)) report.RecordFailure(StepPhase.Cleanup, "character cleanup failed", error); }
            if (logs.Count != 0) report.ScanLogs(logs, Inputs.LogScan);
            // Kept for the next arm. Whoever removes it (TargetedRegression.Remove) records that in the last arm's report.
            report.Provenance["disposableInstall"] = "kept after this arm";
            report.Write(output);
        }
        return report;
    }

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

    /// <summary>Deletes the disposable install, only when it carries this tool's marker.</summary>
    public void Remove()
    {
        string install = Path.GetFullPath(Install);
        if (!Directory.Exists(install)) return;
        RequireOwned(install);
        Directory.Delete(install, recursive: true);
    }

    /// <summary>This toolkit's package and version, as a run's provenance records it (<c>Valheim.Testing.Game</c> and the package version, without build metadata).</summary>
    public static string ToolkitVersion =>
        "Valheim.Testing.Game " + (typeof(TargetedRegression).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unknown");

    internal static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- the disposable install ----

    private sealed record Marker(string Tool, string GameSha256, string? LoaderSha256, string? Arm,
        Dictionary<string, string>? Tree, Dictionary<string, string>? LoaderFiles, string? LoaderPackage = null, bool LoaderSmoke = false);

    // A copy of the prepared game with BepInEx's loadable folders empty; reused while its game build and loader are the selected ones.
    private string PrepareInstall()
    {
        var env = Inputs;
        string game = Path.GetFullPath(Game), install = Path.GetFullPath(Install);
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException($"game: {game} does not exist. Give the Valheim install to copy into the disposable run.");
        var package = LoaderPackage == null ? null : BepInExLoaderPackage.Read(LoaderPackage);
        if (package != null && (RegressionInputs.Inside(package.Root, game) || RegressionInputs.Inside(game, package.Root)))
            throw new InvalidOperationException($"The pinned BepInEx package {package.Root} overlaps the game {game}; extract one reviewed loader set outside the live game before staging.");
        if (package != null && (RegressionInputs.Inside(package.Root, install) || RegressionInputs.Inside(install, package.Root)))
            throw new InvalidOperationException($"The disposable install {install} overlaps the pinned BepInEx package {package.Root}; keep the package outside the install so cleanup cannot delete it.");
        if (package == null && !Directory.Exists(Path.Combine(game, InstallPins.CoreDirectory)))
            throw new InvalidOperationException($"game: {game} has no {InstallPins.CoreDirectory}. Install BepInEx (BepInExPack_Valheim) in the prepared game first; the disposable install is copied from it.");
        // The selected loader: the game's own, or the package's, which an install it is applied to has (BepInExLoaderPackage.Loader).
        var pins = package == null ? InstallPins.Of(game) : new InstallPins
        {
            Game = InstallPins.GameHash(game),
            Loader = package.Loader,
            Patchers = InstallPins.DirectoryHash(Path.Combine(game, BepInExLoader.Patchers)),
        };
        if (env.GamePins != null)
            env.GamePins.Compare(new InstallPins { Game = pins.Game, Loader = pins.Loader, Patchers = env.GamePins.Patchers }, "prepared game and selected loader package", "Managed");
        if (Directory.Exists(install))
        {
            var marker = RequireOwned(install);
            bool current = marker.GameSha256 == pins.Game && marker.LoaderSha256 == pins.Loader && marker.LoaderPackage == package?.Identity &&
                Directory.Exists(Path.Combine(install, InstallPins.CoreDirectory)) &&
                InstallPins.Of(install) is var found && found.Game == pins.Game && found.Loader == pins.Loader &&
                (package == null ? LoaderCopied(game, install, marker.LoaderFiles) : package.Matches(install));
            if (!current) Directory.Delete(install, recursive: true);
        }
        if (!Directory.Exists(install))
        {
            try
            {
                Copy(game, install, game);
                package?.Apply(install);
                WriteMarker(install, null, null, InstallPins.Of(install), LoaderFiles(install), package?.Identity);
            }
            catch
            {
                // This run just created the install. A failed copy must not strand an unmarked partial install that a
                // retry would correctly refuse to touch.
                if (Directory.Exists(install)) Directory.Delete(install, recursive: true);
                throw;
            }
        }
        foreach (string folder in StagedFolders.Append("cache"))
        {
            string path = Path.Combine(install, "BepInEx", folder);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        foreach (string folder in new[] { "plugins", "patchers", "config" }) Directory.CreateDirectory(Path.Combine(install, "BepInEx", folder));
        string settings = package == null ? Path.Combine(game, "BepInEx", "config", BepInExConfig)
            : Path.Combine(package.Root, "BepInEx", "config", BepInExConfig);
        if (File.Exists(settings)) File.Copy(settings, Path.Combine(install, "BepInEx", "config", BepInExConfig));
        var copied = InstallPins.Of(install);
        if (copied.Game != pins.Game || copied.Loader != pins.Loader)
            throw new InvalidOperationException($"The disposable install {install} does not match the selected game and loader after copying (game {copied.Game} vs {pins.Game}, loader {copied.Loader} vs {pins.Loader}). Remove it and stage again.");
        return install;
    }

    // Remember the source file set as well as its contents: removal of an old Doorstop proxy must invalidate the copy too.
    // The install may acquire unrelated runtime files at its root, so compare it to the source snapshot rather than requiring
    // the install's root directory to have exactly the same entries.
    private static Dictionary<string, string> LoaderFiles(string game)
    {
        string libraries = Path.Combine(game, "doorstop_libs");
        return Directory.EnumerateFiles(game).Concat(Directory.Exists(libraries) ? Directory.EnumerateFiles(libraries, "*", SearchOption.AllDirectories) : [])
            .Where(path => !FileHash.IsMacMetadata(path))
            .ToDictionary(path => Path.GetRelativePath(game, path).Replace('\\', '/'), FileHash.Sha256, StringComparer.Ordinal);
    }

    private static bool LoaderCopied(string game, string install, Dictionary<string, string>? recorded)
    {
        if (recorded == null) return false; // An older marker cannot prove which files were copied.
        var current = LoaderFiles(game);
        return current.Count == recorded.Count && current.All(file =>
            recorded.TryGetValue(file.Key, out string? hash) && hash == file.Value &&
            File.Exists(Path.Combine(install, file.Key)) && FileHash.Sha256(Path.Combine(install, file.Key)) == file.Value);
    }

    private static void Copy(string source, string target, string root)
    {
        Directory.CreateDirectory(target);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            string relative = Path.GetRelativePath(root, entry);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Length == 2 && parts[0] == "BepInEx" && NotCopied.Contains(parts[1], StringComparer.OrdinalIgnoreCase)) continue;
            string destination = Path.Combine(target, Path.GetFileName(entry));
            var info = new FileInfo(entry);
            if (info.LinkTarget != null)
            {
                if (Directory.Exists(entry)) Directory.CreateSymbolicLink(destination, info.LinkTarget); else File.CreateSymbolicLink(destination, info.LinkTarget);
            }
            else if (Directory.Exists(entry)) Copy(entry, destination, root);
            else File.Copy(entry, destination);
        }
    }

    private static Marker RequireOwned(string install)
    {
        string path = Path.Combine(install, MarkerFile);
        Marker? marker = null;
        try { if (File.Exists(path)) marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), ManifestJson); }
        catch (JsonException) { }
        if (marker?.Tool != nameof(TargetedRegression))
            throw new InvalidOperationException($"install: {install} exists and is not a disposable install this tool created (no {MarkerFile}). It is never changed: give a new directory, or remove that one yourself if it is disposable.");
        return marker;
    }

    private static void WriteMarker(string install, string? arm, Dictionary<string, string>? tree, InstallPins? pins = null,
        Dictionary<string, string>? loaderFiles = null, string? loaderPackage = null)
    {
        var previous = pins == null ? RequireOwned(install) : null;
        var marker = new Marker(nameof(TargetedRegression), pins?.Game ?? previous!.GameSha256, pins?.Loader ?? previous!.LoaderSha256,
            arm, tree, loaderFiles ?? previous?.LoaderFiles, loaderPackage ?? previous?.LoaderPackage, previous?.LoaderSmoke ?? false);
        File.WriteAllText(Path.Combine(install, MarkerFile), JsonSerializer.Serialize(marker, ManifestJson));
    }

    private static void MarkLoaderSmoke(string install)
    {
        var marker = RequireOwned(install);
        File.WriteAllText(Path.Combine(install, MarkerFile), JsonSerializer.Serialize(marker with { LoaderSmoke = true }, ManifestJson));
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
