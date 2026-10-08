using System.Text.Json;
using System.Text.Json.Serialization;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>
/// What one coherent ValheimCLI build (its core and command packs) provides, tied to each DLL's exact SHA256: the file name,
/// the BepInEx plugin GUIDs it declares and the extension commands it registers, with their result schema versions. An owned
/// client's <see cref="ClientRunPlan.CliManifest"/> names one, and <see cref="ClientRunPlan.Preflight()"/> then refuses,
/// before the game starts, an install whose ValheimCLI files are not that set (a file missing, another build of it, a second
/// copy, a renamed file declaring one of its plugins) or a set that lacks a capability the run uses, naming the capability
/// and the file that provides it. A file name, plugin GUID or version never stands for a capability: only a DLL with the
/// manifest's hash does. ValheimCLI can ship this file with its build; <see cref="Generate"/> writes one from a known build
/// by reading each DLL's metadata and IL, never loading it. <see cref="CliCapabilities.Require(GameActor, string[])"/> after
/// launch stays the authoritative check: this one only fails earlier.
/// </summary>
public sealed class CliCapabilityManifest
{
    /// <summary>The manifest format; 1 is the only one.</summary>
    public int Schema { get; set; } = 1;
    /// <summary>Where the set came from, for messages and evidence: a repository and commit, or a release.</summary>
    public string Build { get; set; } = "";
    /// <summary>The core and every pack of the set, each installed exactly once.</summary>
    public List<CliManifestFile> Files { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
    };

    /// <summary>Every capability (<c>owner/command</c>) of the set and its result schema version.</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, int> Capabilities =>
        Files.SelectMany(file => file.Commands()).ToDictionary(entry => entry.Path, entry => entry.Version, StringComparer.Ordinal);

    /// <summary>
    /// Reads and checks a manifest. Refuses a missing file (<see cref="FileNotFoundException"/>) and one that is not a
    /// well-formed schema-1 manifest (<see cref="InvalidDataException"/>, naming the file and the problem).
    /// </summary>
    public static CliCapabilityManifest Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"The ValheimCLI capability manifest {path} does not exist.", path);
        CliCapabilityManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<CliCapabilityManifest>(File.ReadAllText(path), Options); }
        catch (JsonException error) { throw new InvalidDataException($"The ValheimCLI capability manifest {path} is not valid JSON for schema 1: {error.Message}", error); }
        if (manifest == null) throw new InvalidDataException($"The ValheimCLI capability manifest {path} is empty.");
        try { manifest.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException($"The ValheimCLI capability manifest {path} is malformed: {error.Message}", error); }
        return manifest;
    }

    /// <summary>Writes the manifest after the same checks <see cref="Read"/> makes.</summary>
    public void Write(string path)
    {
        Validate();
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options) + "\n");
    }

    /// <summary>The format's rules; refuses with <see cref="ArgumentException"/>.</summary>
    public void Validate()
    {
        if (Schema != 1) throw new ArgumentException($"schema is {Schema}; this toolkit reads schema 1.");
        if (string.IsNullOrWhiteSpace(Build) || Build.Any(char.IsControl)) throw new ArgumentException("Name the build the set came from in build.");
        if (Files == null || Files.Count == 0) throw new ArgumentException("List the set's DLLs in files.");
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.File) || file.File != Path.GetFileName(file.File) || file.File.Contains('\\') || !file.File.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Each file is a DLL's bare file name, not \"{file?.File}\".");
            if (file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)) throw new ArgumentException($"{file.File}: sha256 is the DLL's full SHA256.");
            if (file.Plugins == null || file.Plugins.Count == 0 || file.Plugins.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException($"{file.File}: list the BepInEx plugin GUIDs it declares in plugins.");
            foreach (var (owner, commands) in file.Extensions ?? throw new ArgumentException($"{file.File}: extensions is an object, {{}} when it registers none."))
            {
                if (!ValidName(owner)) throw new ArgumentException($"{file.File}: \"{owner}\" is not an extension owner name.");
                if (!owners.TryAdd(owner, file.File)) throw new ArgumentException($"{owner} is registered by both {owners[owner]} and {file.File}.");
                if (commands == null || commands.Count == 0) throw new ArgumentException($"{file.File}: {owner} lists no commands.");
                foreach (var (command, version) in commands)
                    if (!ValidName(command) || version < 1) throw new ArgumentException($"{file.File}: {owner}/{command} needs a command name and a result version of 1 or more.");
            }
        }
        if (Files.Select(file => file.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Count) throw new ArgumentException("A file name is listed twice.");
        if (Files.Select(file => file.Sha256.ToLowerInvariant()).Distinct().Count() != Files.Count) throw new ArgumentException("Two files have the same SHA256.");
        if (Files.SelectMany(file => file.Plugins).Distinct(StringComparer.Ordinal).Count() != Files.Sum(file => file.Plugins.Count)) throw new ArgumentException("A plugin GUID is declared by two files.");
    }

    private static bool ValidName(string name) => name.Length != 0 && !name.Contains('/') && !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>
    /// Writes the manifest of a known, coherent build from its DLLs (<paramref name="files"/>, full paths): each file's SHA256,
    /// the GUIDs of its <c>[BepInPlugin]</c> attributes, and the extensions its IL registers through ValheimCLI's
    /// <c>ExtensionRegistry.Register(id, version, apiVersion, new ExtensionCommand(name, ...), ...)</c> with literal names,
    /// each command's result version being the constructor's last argument. Nothing is loaded or run. Console-module
    /// listings (a pack's <c>&lt;module&gt;/commands</c>) and registrations without literal names are not claimed. A file that
    /// declares no plugin, or a registration whose shape this reader does not know, is refused rather than guessed.
    /// <paramref name="listing"/>, when given, is a <c>cli_extensions</c> reply (with or without its <c>EXTENSIONS </c>
    /// prefix) from a client that loaded exactly these files: every owner the files register must be live there with exactly
    /// the same commands and versions, so the game confirms what the IL says.
    /// </summary>
    public static CliCapabilityManifest Generate(string build, IEnumerable<string> files, string? listing = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var manifest = new CliCapabilityManifest { Build = build };
        foreach (var file in CliBuildManifest.Generate(files))
            manifest.Files.Add(new CliManifestFile { File = file.File, Sha256 = file.Sha256, Plugins = [.. file.Plugins], Extensions = file.Extensions });
        try { manifest.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("These files do not make one set: " + error.Message, error); }
        if (listing != null) manifest.RequireListing(listing);
        return manifest;
    }

    /// <summary>
    /// Selects the core and only the packs that provide <paramref name="capabilities"/> from this pinned build. Each
    /// command must have result schema 1, which is what the native runner consumes. The selected manifest can be written
    /// beside an environment lock and checked against the staged files before launch; no DLL from another build is used.
    /// </summary>
    public CliCapabilityManifest ForCapabilities(IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        Validate();
        var wanted = capabilities.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Any(path => path == null || path.Split('/') is not [{ Length: > 0 } owner, { Length: > 0 } command] || !ValidName(owner) || !ValidName(command)))
            throw new ArgumentException("Name each capability as owner/command.", nameof(capabilities));
        var core = Files.Where(file => file.Plugins.Contains("valheimCLI.valheimCLI", StringComparer.Ordinal)).ToList();
        if (core.Count != 1) throw new InvalidOperationException($"The ValheimCLI build {Build} needs exactly one core declaring valheimCLI.valheimCLI; found {core.Count}.");
        var providers = Files.SelectMany(file => file.Commands().Select(command => (File: file, command.Path, command.Version)))
            .ToDictionary(entry => entry.Path, entry => (entry.File, entry.Version), StringComparer.Ordinal);
        var missing = wanted.Where(path => !providers.ContainsKey(path)).ToList();
        if (missing.Count != 0) throw new InvalidOperationException($"The ValheimCLI build {Build} lacks {string.Join(", ", missing)}. Supply one coherent core and pack set that provides those commands.");
        var wrongSchema = wanted.Where(path => providers[path].Version != 1).ToList();
        if (wrongSchema.Count != 0) throw new InvalidOperationException($"The ValheimCLI build {Build} provides {string.Join(", ", wrongSchema.Select(path => path + " with result version " + providers[path].Version))}; the runner needs result version 1.");
        var selected = new HashSet<CliManifestFile>(core);
        foreach (string path in wanted) selected.Add(providers[path].File);
        var result = new CliCapabilityManifest { Build = Build, Files = Files.Where(selected.Contains).ToList() };
        result.Validate();
        return result;
    }

    // The game's own listing must agree with every owner the files register.
    private void RequireListing(string listing)
    {
        string json = listing.StartsWith("EXTENSIONS ", StringComparison.Ordinal) ? listing["EXTENSIONS ".Length..] : listing;
        Dictionary<string, int> live;
        try { using var document = JsonDocument.Parse(json); live = CliCapabilities.Parse(document.RootElement); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("The listing is not a cli_extensions result: " + error.Message, error); }
        var problems = new List<string>();
        foreach (var file in Files)
            foreach (var owner in file.Extensions.Keys)
            {
                var stated = file.Commands().Where(entry => entry.Path.StartsWith(owner + "/", StringComparison.Ordinal)).ToDictionary(entry => entry.Path, entry => entry.Version);
                var listed = live.Where(entry => entry.Key.StartsWith(owner + "/", StringComparison.Ordinal)).ToDictionary(entry => entry.Key, entry => entry.Value);
                foreach (var path in stated.Keys.Union(listed.Keys).Order(StringComparer.Ordinal))
                {
                    bool inFile = stated.TryGetValue(path, out int fileVersion), inGame = listed.TryGetValue(path, out int gameVersion);
                    if (!inGame) problems.Add($"{path} (in {file.File}) is not live");
                    else if (!inFile) problems.Add($"{path} is live but {file.File} does not register it");
                    else if (fileVersion != gameVersion) problems.Add($"{path} is result version {gameVersion} live and {fileVersion} in {file.File}");
                }
            }
        if (problems.Count != 0)
            throw new InvalidDataException($"The live cli_extensions listing disagrees with what the files register: {string.Join("; ", problems)}. Give the listing of a client that loaded exactly these files.");
    }

    /// <summary>
    /// The static capability check of the owned client install <paramref name="install"/>. Every manifest file must be
    /// installed exactly once, by SHA256, in <c>BepInEx/plugins</c> or <c>BepInEx/scripts</c> (any subfolder); no other DLL
    /// there may have one of its file names or declare one of its plugin GUIDs (another build, stale or renamed); and the set
    /// must provide each of <paramref name="capabilities"/> (<c>owner/command</c>) with result version 1, as
    /// <see cref="CliCapabilities.Require(GameActor, string[])"/> requires live. Refuses with
    /// <see cref="InvalidOperationException"/>, naming every problem, each capability it costs and the file that provides it;
    /// <see cref="ArgumentException"/> for a malformed capability name. DLLs the manifest does not mention (the mods) are not
    /// judged. Returns what was checked, for the report.
    /// </summary>
    public CliManifestCheck Check(string install, IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var wanted = capabilities.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Any(path => path == null || path.Split('/') is not [{ Length: > 0 } owner, { Length: > 0 } command] || !ValidName(owner) || !ValidName(command)))
            throw new ArgumentException("Name each capability as owner/command.", nameof(capabilities));
        install = Path.GetFullPath(install);
        var location = Locate(OwnedClientPreflight.InstalledDlls(install), "in BepInEx/plugins or BepInEx/scripts",
            path => Path.GetRelativePath(install, path).Replace('\\', '/'));
        var problems = location.Problems.Select(problem => problem.Message).ToList();
        var lost = location.Problems.Select(problem => problem.File).Distinct().ToList();
        var located = location.Located.Select(found => Path.GetRelativePath(install, found.Path).Replace('\\', '/')).ToList();
        string where = $"the ValheimCLI capability manifest of {Build}";
        if (problems.Count != 0)
        {
            var costs = lost.Select(file => (File: file, Paths: wanted.Where(path => file.Commands().Any(entry => entry.Path == path)).ToList()))
                .Where(cost => cost.Paths.Count != 0).Select(cost => $"{string.Join(", ", cost.Paths)} from {Name(cost.File)}").ToList();
            throw new InvalidOperationException($"The client's ValheimCLI files are not the set {where} describes: {string.Join("; ", problems)}. " +
                (costs.Count == 0 ? "" : $"The run needs {string.Join(", and ", costs)}, which this install does not have as the manifest's build. ") +
                "Install the manifest's core and packs together, each once, from one build (or the manifest that build ships), before anything launches.");
        }
        RequireCapabilities(wanted);
        return new CliManifestCheck(Build, located, wanted);
    }

    /// <summary>
    /// The capability half of <see cref="Check"/> alone, from the manifest without an install: the set must provide each of
    /// <paramref name="capabilities"/> (<c>owner/command</c>) with result version 1. A disposable copy's set is checked so before the
    /// copy exists, and again with its files once it does. Refuses with <see cref="InvalidOperationException"/>.
    /// </summary>
    internal void RequireCapabilities(IEnumerable<string> capabilities)
    {
        var have = Capabilities;
        var missing = capabilities.Distinct(StringComparer.Ordinal).Where(path => !have.TryGetValue(path, out int version) || version != 1).ToList();
        if (missing.Count != 0)
            throw new InvalidOperationException($"The client's ValheimCLI set (the ValheimCLI capability manifest of {Build}) lacks {string.Join(", ", missing.Select(path => have.ContainsKey(path) ? path + " (another result schema)" : path))}. " +
                (have.Count == 0 ? "Its files register no extension commands at all, as an old monolithic ValheimCLI that predates command packs. " : "") +
                string.Join(" ", missing.Select(path => path.Split('/')[0]).Distinct(StringComparer.Ordinal).Select(CliCapabilities.Provider)) +
                " Install a ValheimCLI core with each pack the run needs, from one build, and use that build's manifest.");
    }

    /// <summary>
    /// The one static check that <paramref name="dlls"/> hold exactly this manifest's set: each file found once by SHA256 and
    /// declaring the manifest's plugin GUIDs, and no other DLL among them with one of its file names or GUIDs (another build,
    /// stale or renamed). DLLs that are neither (the mods) are not judged. <paramref name="where"/> says where the DLLs were
    /// looked for and <paramref name="show"/> how a path is named in a message. <paramref name="othersLoad"/>: whether the
    /// DLLs that are not the located set would load beside it (an install's plugins, a lock's files), so another build among
    /// them is a problem; false for a source folder only the located files are copied from. Every owned-client,
    /// dependency-lock and resolver check of a ValheimCLI set is this one; only the live
    /// <see cref="CliCapabilities.Require(GameActor, string[])"/> differs.
    /// </summary>
    internal CliSetLocation Locate(IEnumerable<string> dlls, string where, Func<string, string>? show = null, bool othersLoad = true)
    {
        ArgumentNullException.ThrowIfNull(dlls);
        Validate();
        show ??= path => path;
        var candidates = dlls.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal)
            .Select(path => (Path: path, Shown: show(path), Sha256: FileHash.Sha256(path))).ToList();
        var problems = new List<CliSetProblem>();
        var located = new List<CliLocatedFile>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            var exact = candidates.Where(dll => dll.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var dll in exact) claimed.Add(dll.Path);
            if (exact.Count > 1)
                problems.Add(new(file, $"{Name(file)} is installed {exact.Count} times ({string.Join(", ", exact.Select(dll => dll.Shown))}); BepInEx loads one and skips the rest, so keep one", exact.Select(dll => dll.Path).ToList()));
            else if (exact.Count == 1)
            {
                var declared = CliBuildManifest.Plugins(exact[0].Path) ?? [];
                if (declared.Order(StringComparer.Ordinal).SequenceEqual(file.Plugins.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                    located.Add(new(file, exact[0].Path));
                else
                    problems.Add(new(file, $"{exact[0].Shown} has the manifest's SHA256 for {file.File} but declares {(declared.Count == 0 ? "no BepInEx plugin" : string.Join(", ", declared))}, not {string.Join(", ", file.Plugins)}; regenerate the manifest from this build", [exact[0].Path]));
            }
            else if (!othersLoad || !candidates.Any(dll => SameFile(dll.Shown, file))) // else named below as another build
            {
                var named = candidates.Where(dll => SameFile(dll.Shown, file)).ToList();
                problems.Add(new(file, $"{Name(file)} is not installed {where}" +
                    string.Concat(named.Select(dll => $"; {dll.Shown} there has SHA256 {dll.Sha256}, another build")), named.Select(dll => dll.Path).ToList()));
            }
        }
        foreach (var dll in candidates.Where(dll => othersLoad && !claimed.Contains(dll.Path)))
        {
            var plugins = CliBuildManifest.Plugins(dll.Path) ?? [];
            var file = Files.FirstOrDefault(candidate => SameFile(dll.Shown, candidate)) ?? Files.FirstOrDefault(candidate => candidate.Plugins.Intersect(plugins, StringComparer.Ordinal).Any());
            if (file == null) continue;
            string declares = plugins.Count == 0 ? "" : $", declaring {string.Join(", ", plugins)},";
            problems.Add(new(file, $"{dll.Shown}{declares} has SHA256 {dll.Sha256}, not the manifest's {file.File} ({file.Sha256.ToLowerInvariant()}): another build of {Name(file)}", [dll.Path]));
        }
        return new CliSetLocation(located, problems);
    }

    private static bool SameFile(string relative, CliManifestFile file) => Path.GetFileName(relative).Equals(file.File, StringComparison.OrdinalIgnoreCase);
    private static string Name(CliManifestFile file) => $"{file.File} (plugin {string.Join(", ", file.Plugins)})";
}

/// <summary>What <see cref="CliCapabilityManifest.Locate"/> found: each manifest file located once, and every problem, with the paths it saw.</summary>
internal sealed record CliSetLocation(IReadOnlyList<CliLocatedFile> Located, IReadOnlyList<CliSetProblem> Problems);
internal sealed record CliLocatedFile(CliManifestFile File, string Path);
internal sealed record CliSetProblem(CliManifestFile File, string Message, IReadOnlyList<string> Candidates);

/// <summary>One DLL of a <see cref="CliCapabilityManifest"/>.</summary>
public sealed class CliManifestFile
{
    /// <summary>The DLL's file name, as the build ships it (<c>Valheim.Cli.Standard.dll</c>).</summary>
    public string File { get; set; } = "";
    /// <summary>The DLL's full SHA256: its identity. Another build, even with the same name and plugin GUID, has another.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>The BepInEx plugin GUIDs the DLL declares (<c>valheimCLI.standard</c>).</summary>
    public List<string> Plugins { get; set; } = [];
    /// <summary>The extensions the DLL registers: owner, then each command's result schema version. Empty for the core.</summary>
    public SortedDictionary<string, SortedDictionary<string, int>> Extensions { get; set; } = new(StringComparer.Ordinal);

    internal IEnumerable<(string Path, int Version)> Commands() =>
        Extensions.SelectMany(owner => owner.Value.Select(command => (owner.Key + "/" + command.Key, command.Value)));
}

/// <summary>What <see cref="CliCapabilityManifest.Check"/> found: the manifest's build, its files where they are installed, and the capabilities checked.</summary>
[ResultShape]
public sealed record CliManifestCheck(string Build, IReadOnlyList<string> Files, IReadOnlyList<string> Capabilities)
{
    /// <summary>One line for the report's provenance.</summary>
    public override string ToString() =>
        $"{Build}: {string.Join(", ", Files)}" + (Capabilities.Count == 0 ? "" : $"; provides {string.Join(", ", Capabilities)}");
}
