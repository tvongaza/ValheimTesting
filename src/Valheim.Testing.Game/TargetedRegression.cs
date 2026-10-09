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

    internal static bool Inside(string path, string root) => ProtectedPaths.Contains(root, path);

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
    /// <summary>The owned client's requested slice; empty inherits its inventory environment.</summary>
    public string Architecture { get; set; } = "";
    /// <summary>Non-secret variables set only in the disposable client's process. Loader overrides are refused.</summary>
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
    public int StartSeconds { get; set; } = ClientTimeouts.DefaultStartSeconds;
    public int JoinSeconds { get; set; } = ClientTimeouts.DefaultJoinSeconds;
    /// <summary>Optional registered, game-created disposable character to stage for the owned hosted run (checked against this machine's Steam userdata).</summary>
    public string? CharacterStore { get; set; }

    public void Validate()
    {
        ClientTimeouts.RequireStartAndJoin(StartSeconds, JoinSeconds);
        if (Architecture is not ("" or "x64" or "arm64"))
            throw new ArgumentException("client.architecture: use x64 or arm64.");
        if (Character.Length == 0 || Character.Any(char.IsWhiteSpace) || Character.EndsWith(".fch", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("client.character: give the disposable local character's file name, without .fch.");
        if (CharacterStore != null && !Path.IsPathFullyQualified(CharacterStore)) throw new ArgumentException("client.characterStore: give the full path of a registered disposable character store.");
        GameLaunch.ValidateClientEnvironment(Environment, LaunchArguments);
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
    /// The <see cref="CliCapabilityManifest"/> of this ValheimCLI build, required: the staged core and packs must be exactly its
    /// set and provide every ValheimCLI capability the run uses before anything launches (#296: the static check always runs).
    /// <c>valheim-test start</c> writes the manifest of the set it stages; a hand-written file names its build's.
    /// </summary>
    public string? Manifest { get; set; }
    public void Validate()
    {
        Core.Validate("cli.core");
        foreach (var (file, field) in Packs.Select((file, i) => (file, $"cli.packs[{i}]"))) file.Validate(field);
        if (Manifest == null) throw new ArgumentException("cli.manifest: name the capability manifest of the staged ValheimCLI core and packs (valheim-test start writes it); the static check runs on every staged set.");
        if (!Path.IsPathFullyQualified(Manifest)) throw new ArgumentException("cli.manifest: give the capability manifest's path.");
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
