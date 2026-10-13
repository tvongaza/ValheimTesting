using System.Text.Json;
using System.Text.Json.Serialization;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>
/// What one pinned ValheimCLI build provides, tied to its plugin's exact SHA256: ValheimCLI is one plugin
/// (<see cref="PluginFile"/>, declaring <see cref="PluginGuid"/>), and the manifest records its SHA256 and the extension commands
/// it registers, with their result schema versions. An owned client's <see cref="ClientRunPlan.CliManifest"/> names one, and
/// <see cref="ClientRunPlan.Preflight()"/> then refuses, before the game starts, an install whose ValheimCLI plugin is not that
/// build (missing, another build of it, a second copy, a renamed file declaring its GUID) or lacks a capability the run uses,
/// naming the capability. A file name, plugin GUID or version never stands for a capability: only a DLL with the manifest's hash
/// does. ValheimCLI ships this file with its build; <see cref="Generate"/> writes one from a known build by reading the DLL's
/// metadata and IL, never loading it. <see cref="Read"/> is the one place that refuses anything but a one-plugin build (an
/// old split core-and-pack manifest among them), so every command that reads a manifest refuses it with the same error.
/// <see cref="CliCapabilities.Require(GameActor, string[])"/> after launch stays the authoritative check: this one only fails earlier.
/// </summary>
public sealed class CliCapabilityManifest
{
    /// <summary>ValheimCLI's BepInEx plugin GUID: the one name of the core plugin (its pin key and config file name derive from it).</summary>
    public const string PluginGuid = "valheimCLI.valheimCLI";
    /// <summary>ValheimCLI's plugin file name.</summary>
    public const string PluginFile = "valheimCLI.dll";

    /// <summary>The manifest format; 1 is the only one.</summary>
    public int Schema { get; set; } = 1;
    /// <summary>Where the build came from, for messages and evidence: a repository and commit, or a release.</summary>
    public string Build { get; set; } = "";

    // The format's "files" list, as ValheimCLI writes it. A one-plugin build lists exactly one file; Validate refuses any other.
    [JsonInclude, JsonPropertyName("files")] private List<CliManifestFile> Files { get; set; } = [];

    /// <summary>The build's one plugin: its file name, SHA256, plugin GUID and the extensions it registers.</summary>
    [JsonIgnore] public CliManifestFile Plugin
    {
        get { RequireOnePlugin(); return Files[0]; }
        set => Files = [value];
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
    };

    /// <summary>Every capability (<c>owner/command</c>) of the plugin and its result schema version.</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, int> Capabilities =>
        Plugin.Commands().ToDictionary(entry => entry.Path, entry => entry.Version, StringComparer.Ordinal);

    /// <summary>
    /// Reads and checks a manifest. Refuses a missing file (<see cref="FileNotFoundException"/>) and one that is not a
    /// well-formed schema-1 manifest of a one-plugin build (<see cref="InvalidDataException"/>, naming the file and the problem).
    /// </summary>
    public static CliCapabilityManifest Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"The ValheimCLI capability manifest {path} does not exist.", path);
        CliCapabilityManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<CliCapabilityManifest>(File.ReadAllText(path), Options); }
        catch (JsonException error) { throw new InvalidDataException($"The ValheimCLI capability manifest {path} is not valid JSON for schema 1: {error.Message}", error); }
        if (manifest == null) throw new InvalidDataException($"The ValheimCLI capability manifest {path} is empty.");
        manifest.RequireValid(path);
        return manifest;
    }

    /// <summary>
    /// <see cref="Validate"/>, refused as <see cref="Read"/> refuses: an <see cref="InvalidDataException"/> naming where the
    /// manifest came from (<paramref name="source"/>: its path, or the file that embeds it, such as a dependency lock).
    /// </summary>
    internal void RequireValid(string source)
    {
        try { Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException($"The ValheimCLI capability manifest {source} is malformed: {error.Message}", error); }
    }

    /// <summary>Writes the manifest after the same checks <see cref="Read"/> makes.</summary>
    public void Write(string path)
    {
        Validate();
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options) + "\n");
    }

    /// <summary>The format's rules, a one-plugin build among them; refuses with <see cref="ArgumentException"/>.</summary>
    public void Validate()
    {
        if (Schema != 1) throw new ArgumentException($"schema is {Schema}; this toolkit reads schema 1.");
        if (string.IsNullOrWhiteSpace(Build) || Build.Any(char.IsControl)) throw new ArgumentException("Name the build the plugin came from in build.");
        var plugin = Plugin;
        if (plugin.Sha256 is not { Length: 64 } || !plugin.Sha256.All(Uri.IsHexDigit)) throw new ArgumentException($"{plugin.File}: sha256 is the DLL's full SHA256.");
        foreach (var (owner, commands) in plugin.Extensions ?? throw new ArgumentException($"{plugin.File}: extensions is an object, {{}} when it registers none."))
        {
            if (!ValidName(owner)) throw new ArgumentException($"{plugin.File}: \"{owner}\" is not an extension owner name.");
            if (commands == null || commands.Count == 0) throw new ArgumentException($"{plugin.File}: {owner} lists no commands.");
            foreach (var (command, version) in commands)
                if (!ValidName(command) || version < 1) throw new ArgumentException($"{plugin.File}: {owner}/{command} needs a command name and a result version of 1 or more.");
        }
    }

    // The one place that decides "a one-plugin ValheimCLI build" (#670): files lists exactly one valheimCLI.dll declaring exactly
    // valheimCLI.valheimCLI. An old split core-and-pack manifest, or any other shape, is refused here and nowhere else.
    private void RequireOnePlugin()
    {
        if (Files is [{ File: PluginFile, Plugins: [PluginGuid] }]) return;
        string listed = Files == null || Files.Count == 0 ? "no DLL" : string.Join(", ", Files.Select(file =>
            file == null ? "null" : $"{file.File} ({(file.Plugins == null || file.Plugins.Count == 0 ? "no plugin" : string.Join(", ", file.Plugins))})"));
        throw new ArgumentException($"files lists {listed}: not a one-plugin build. This toolkit reads one {PluginFile} declaring {PluginGuid}; " +
            $"replace the old core and first-party pack DLLs together with one pinned {PluginFile} and its matching capability manifest.");
    }

    private static bool ValidName(string name) => name.Length != 0 && !name.Contains('/') && !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>
    /// Writes the manifest of a known build from its DLL (<paramref name="files"/>, full paths: the one plugin): its SHA256,
    /// the GUIDs of its <c>[BepInPlugin]</c> attributes, and the extensions its IL registers through ValheimCLI's
    /// <c>ExtensionRegistry.Register(id, version, apiVersion, new ExtensionCommand(name, ...), ...)</c> with literal names,
    /// each command's result version being the constructor's last argument. Nothing is loaded or run. Console-module
    /// listings and registrations without literal names are not claimed. A file that declares no plugin, a registration whose
    /// shape this reader does not know, or anything but one plugin file (<see cref="Read"/>'s rule) is refused rather than guessed.
    /// <paramref name="listing"/>, when given, is a <c>cli_extensions</c> reply (with or without its <c>EXTENSIONS </c>
    /// prefix) from a client that loaded exactly this plugin: every owner it registers must be live there with exactly
    /// the same commands and versions, so the game confirms what the IL says.
    /// </summary>
    public static CliCapabilityManifest Generate(string build, IEnumerable<string> files, string? listing = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var manifest = new CliCapabilityManifest
        {
            Build = build,
            Files = CliBuildManifest.Generate(files).Select(file => new CliManifestFile { File = file.File, Sha256 = file.Sha256, Plugins = [.. file.Plugins], Extensions = file.Extensions }).ToList(),
        };
        try { manifest.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("These files are not one ValheimCLI build: " + error.Message, error); }
        if (listing != null) manifest.RequireListing(listing);
        return manifest;
    }

    // The game's own listing must agree with every owner the plugin registers.
    private void RequireListing(string listing)
    {
        string json = listing.StartsWith("EXTENSIONS ", StringComparison.Ordinal) ? listing["EXTENSIONS ".Length..] : listing;
        Dictionary<string, int> live;
        try { using var document = JsonDocument.Parse(json); live = CliCapabilities.Parse(document.RootElement); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("The listing is not a cli_extensions result: " + error.Message, error); }
        var problems = new List<string>();
        var file = Plugin;
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
            throw new InvalidDataException($"The live cli_extensions listing disagrees with what the plugin registers: {string.Join("; ", problems)}. Give the listing of a client that loaded exactly this plugin.");
    }

    /// <summary>
    /// The static capability check of the owned client install <paramref name="install"/>. The manifest's plugin must be
    /// installed exactly once, by SHA256, in <c>BepInEx/plugins</c> or <c>BepInEx/scripts</c> (any subfolder); no other DLL
    /// there may have its file name or declare its plugin GUID (another build, stale or renamed); and it must provide each of
    /// <paramref name="capabilities"/> (<c>owner/command</c>) with result version 1, as
    /// <see cref="CliCapabilities.Require(GameActor, string[])"/> requires live. Refuses with
    /// <see cref="InvalidOperationException"/>, naming every problem and each capability it costs;
    /// <see cref="ArgumentException"/> for a malformed capability name. DLLs that are not ValheimCLI (the mods) are not
    /// judged. Returns what was checked, for the report.
    /// </summary>
    public CliManifestCheck Check(string install, IEnumerable<string> capabilities)
    {
        var wanted = Wanted(capabilities);
        install = Path.GetFullPath(install);
        var location = Locate(OwnedClientPreflight.InstalledDlls(install), "in BepInEx/plugins or BepInEx/scripts",
            path => Path.GetRelativePath(install, path).Replace('\\', '/'));
        if (location.Problems.Count != 0)
        {
            var costs = wanted.Where(Capabilities.ContainsKey).ToList();
            throw new InvalidOperationException($"The client's ValheimCLI plugin is not the build the ValheimCLI capability manifest of {Build} describes: " +
                $"{string.Join("; ", location.Problems.Select(problem => problem.Message))}. " +
                (costs.Count == 0 ? "" : $"The run needs {string.Join(", ", costs)} from {Name(Plugin)}, which this install does not have as the manifest's build. ") +
                $"Install the manifest's pinned {PluginFile} once, with no other ValheimCLI plugin, and retry before launch.");
        }
        RequireCapabilities(wanted);
        return new CliManifestCheck(Build, location.Located.Select(found => Path.GetRelativePath(install, found.Path).Replace('\\', '/')).ToList(), wanted);
    }

    /// <summary>
    /// The capability half of <see cref="Check"/> alone, from the manifest without an install: the plugin must provide each of
    /// <paramref name="capabilities"/> (<c>owner/command</c>) with result version 1. A disposable copy's plugin is checked so before
    /// the copy exists, and again with its files once it does; the dependency resolver checks a request's commands so. Refuses
    /// with <see cref="InvalidOperationException"/>; <see cref="ArgumentException"/> for a malformed capability name.
    /// </summary>
    internal void RequireCapabilities(IEnumerable<string> capabilities)
    {
        var have = Capabilities;
        var missing = Wanted(capabilities).Where(path => !have.TryGetValue(path, out int version) || version != 1).ToList();
        if (missing.Count != 0)
            throw new InvalidOperationException($"The client's ValheimCLI plugin (the ValheimCLI capability manifest of {Build}) lacks {string.Join(", ", missing.Select(path => have.TryGetValue(path, out int version) ? $"{path} (result version {version}; the runner needs 1)" : path))}. " +
                (have.Count == 0 ? "Its plugin registers no extension commands; it predates the required built-in API. " : "") +
                string.Join(" ", missing.Select(path => path.Split('/')[0]).Distinct(StringComparer.Ordinal).Select(CliCapabilities.Provider)) +
                $" Install the pinned default ValheimCLI plugin and use its matching capability manifest.");
    }

    private static List<string> Wanted(IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var wanted = capabilities.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Any(path => path == null || path.Split('/') is not [{ Length: > 0 } owner, { Length: > 0 } command] || !ValidName(owner) || !ValidName(command)))
            throw new ArgumentException("Name each capability as owner/command.", nameof(capabilities));
        return wanted;
    }

    /// <summary>
    /// The one static check that <paramref name="dlls"/> hold exactly this manifest's plugin: found once by SHA256 and declaring
    /// the manifest's plugin GUID, and no other DLL among them with its file name or GUID (another build, stale or renamed).
    /// DLLs that are neither (the mods) are not judged. <paramref name="where"/> says where the DLLs were looked for and
    /// <paramref name="show"/> how a path is named in a message. <paramref name="othersLoad"/>: whether the DLLs that are not
    /// the located plugin would load beside it (an install's plugins, a lock's files), so another build among them is a problem;
    /// false for a source folder only the located file is copied from. Every owned-client, dependency-lock and resolver check
    /// of a ValheimCLI plugin is this one; only the live <see cref="CliCapabilities.Require(GameActor, string[])"/> differs.
    /// </summary>
    internal CliSetLocation Locate(IEnumerable<string> dlls, string where, Func<string, string>? show = null, bool othersLoad = true)
    {
        ArgumentNullException.ThrowIfNull(dlls);
        Validate();
        var file = Plugin;
        show ??= path => path;
        var candidates = dlls.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal)
            .Select(path => (Path: path, Shown: show(path), Sha256: FileHash.Sha256(path))).ToList();
        var problems = new List<CliSetProblem>();
        var located = new List<CliLocatedFile>();
        var exact = candidates.Where(dll => dll.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)).ToList();
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
        foreach (var dll in candidates.Where(dll => othersLoad && !exact.Contains(dll)))
        {
            var plugins = CliBuildManifest.Plugins(dll.Path) ?? [];
            // ValheimCLI is one plugin: any other DLL declaring a ValheimCLI plugin GUID (an old first-party pack) would load beside it.
            var others = plugins.Where(guid => CliBuildManifest.IsCliPlugin(guid) && !file.Plugins.Contains(guid, StringComparer.Ordinal)).ToList();
            if (!SameFile(dll.Shown, file) && others.Count != 0 && !file.Plugins.Intersect(plugins, StringComparer.Ordinal).Any())
            {
                problems.Add(new(file, $"{dll.Shown} declares ValheimCLI plugin {string.Join(", ", others)} beside {Name(file)}; ValheimCLI is that one plugin, so remove it", [dll.Path]));
                continue;
            }
            if (!SameFile(dll.Shown, file) && !file.Plugins.Intersect(plugins, StringComparer.Ordinal).Any()) continue;
            string declares = plugins.Count == 0 ? "" : $", declaring {string.Join(", ", plugins)},";
            problems.Add(new(file, $"{dll.Shown}{declares} has SHA256 {dll.Sha256}, not the manifest's {file.File} ({file.Sha256.ToLowerInvariant()}): another build of {Name(file)}", [dll.Path]));
        }
        return new CliSetLocation(located, problems);
    }

    private static bool SameFile(string relative, CliManifestFile file) => Path.GetFileName(relative).Equals(file.File, StringComparison.OrdinalIgnoreCase);
    private static string Name(CliManifestFile file) => $"{file.File} (plugin {string.Join(", ", file.Plugins)})";
}

/// <summary>What <see cref="CliCapabilityManifest.Locate"/> found: the manifest's plugin located once (or not), and every problem, with the paths it saw.</summary>
internal sealed record CliSetLocation(IReadOnlyList<CliLocatedFile> Located, IReadOnlyList<CliSetProblem> Problems);
internal sealed record CliLocatedFile(CliManifestFile File, string Path);
internal sealed record CliSetProblem(CliManifestFile File, string Message, IReadOnlyList<string> Candidates);

/// <summary>The one plugin of a <see cref="CliCapabilityManifest"/>.</summary>
public sealed class CliManifestFile
{
    /// <summary>The DLL's file name, <see cref="CliCapabilityManifest.PluginFile"/>.</summary>
    public string File { get; set; } = "";
    /// <summary>The DLL's full SHA256: its identity. Another build, even with the same name and plugin GUID, has another.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>The BepInEx plugin GUIDs the DLL declares: <see cref="CliCapabilityManifest.PluginGuid"/>.</summary>
    public List<string> Plugins { get; set; } = [];
    /// <summary>The extensions the DLL registers: owner, then each command's result schema version.</summary>
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
