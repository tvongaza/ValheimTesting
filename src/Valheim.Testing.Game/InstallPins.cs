using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>
/// Whether a run's environment is pinned. <see cref="Strict"/> is the default of every plan and actor and the mode to
/// rely on. <see cref="None"/> is an explicit opt-out for trying the toolkit before maintaining pins: it is never a
/// default and no environment variable sets it; it prints <see cref="Warning"/> when it takes effect and every report
/// and evidence file of the run carries <see cref="NotPinned"/>.
/// </summary>
public static class EnvironmentPinning
{
    public const string Strict = "strict";
    public const string None = "none";
    /// <summary>The marker an unpinned run's reports and evidence files carry.</summary>
    public const string NotPinned = "environment not pinned";

    /// <summary>
    /// True for <see cref="Strict"/>, false for <see cref="None"/>; anything else (null, empty, another spelling) is refused,
    /// so the opt-out is never implied. <paramref name="owner"/> names the setting in the message, for example "The plan's".
    /// </summary>
    public static bool IsStrict(string? value, string owner) => value switch
    {
        Strict => true,
        None => false,
        _ => throw new ArgumentException($"{owner} pinning is \"{Strict}\" (the default when omitted) or \"{None}\", not \"{value}\"."),
    };

    /// <summary>What an unpinned run prints when the opt-out takes effect.</summary>
    public static string Warning(string what) =>
        $"WARNING: {NotPinned}: {what} runs with pinning \"{None}\". Nothing checks the game build, BepInEx, the plugins or the world it runs against, " +
        "so its result is not evidence for any particular build. Use strict pins for results you rely on.";

    internal static void Warn(string what) => Console.Error.WriteLine(Warning(what));

    /// <summary>Adds <c>pinning</c> and, for an unpinned run, the <c>environment</c> marker to an evidence file's fields.</summary>
    internal static Dictionary<string, object?> Stamp(Dictionary<string, object?> evidence, bool pinned)
    {
        evidence["pinning"] = pinned ? Strict : None;
        if (!pinned) evidence["environment"] = NotPinned;
        return evidence;
    }
}

/// <summary>
/// What a game install or server runtime holds that ValheimCLI cannot report in game, pinned on disk before launch. Each
/// value is a full SHA256; <see cref="Of"/> computes them.
/// <list type="bullet">
/// <item><see cref="Game"/>: the game's own code, every <c>assembly_*.dll</c> in the Managed folder that holds
/// <c>assembly_valheim.dll</c> (<c>&lt;executable&gt;_Data/Managed</c>, in a macOS bundle
/// <c>Valheim.app/Contents/Resources/Data/Managed</c>): <c>assembly_valheim</c>, <c>assembly_utils</c>,
/// <c>assembly_guiutils</c>, <c>assembly_postprocessing</c> and the rest. A game update or another branch
/// (<c>default_old</c>, <c>default_pre1_0</c>) that changes any of them changes this pin; one that changes only assets
/// or Unity's own assemblies does not.</item>
/// <item><see cref="Loader"/>: BepInEx's loader as one set (<see cref="IsLoaderFile"/>): the Windows Doorstop proxy
/// <c>winhttp.dll</c> and its <c>doorstop_config.ini</c>, a root <c>libdoorstop.dylib</c>, <c>doorstop_libs</c> and
/// <c>BepInEx/core</c>. Another BepInEx, BepInExPack or Doorstop build changes it, and so does a proxy a mod manager
/// replaced beside the pack's configuration. <see cref="BepInExLoaderPackage.Loader"/> is the same value for a reviewed
/// loader package, so a plan, a package and every check of an install share one loader identity.
/// <c>BepInEx/config/BepInEx.cfg</c> is configuration, not loader: BepInEx rewrites it when it binds its settings.</item>
/// <item><see cref="Patchers"/>: the contents of <c>BepInEx/patchers</c>, which rewrite game assemblies before any plugin
/// loads (the plan's patcher names say which entries it holds; this says which builds).</item>
/// </list>
/// Each value is the SHA256 of a listing: one line per file, <c>&lt;sha256&gt;  &lt;relative path&gt;\n</c> with
/// <c>/</c> separators, ordered by path (ordinal), as <c>sha256sum</c> prints it; <see cref="Loader"/>'s paths are relative
/// to the install's root, the others' to their folder. Finder's <c>.DS_Store</c> and
/// AppleDouble <c>._*</c> files are left out. An empty or absent folder hashes the empty listing.
/// </summary>
public sealed class InstallPins
{
    public const string GameAssemblyName = "assembly_valheim.dll";
    /// <summary>The game's own assemblies in its Managed folder, which <see cref="Game"/> covers.</summary>
    public const string GameAssemblies = "assembly_*.dll";
    public static readonly string CoreDirectory = Path.Combine("BepInEx", "core");

    public string Game { get; set; } = "";
    public string Loader { get; set; } = "";
    public string Patchers { get; set; } = "";

    // Removed (#295): the core-only hash missed the Doorstop proxy and its configuration, whose mismatch started games without
    // BepInEx; loader covers them with the core. A plan that still names it is refused with what to do.
    [JsonInclude, JsonPropertyName("bepinexCore"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private JsonElement? RemovedBepInExCore
    {
        get => null;
        set => throw new ArgumentException("The pins' bepinexCore was removed (ValheimTesting #295): loader pins BepInEx/core together with the Doorstop " +
            "proxy, its configuration and libraries as one hash. Replace bepinexCore with loader, computed by InstallPins.Of(<install>) " +
            "(or BepInExLoaderPackage.Loader for a reviewed loader package).");
    }

    /// <summary>Every value is a full SHA256. <paramref name="what"/> names the install in the message ("runtime", "client install").</summary>
    public void Validate(string what)
    {
        foreach (var (name, value) in Values)
            if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit))
                throw new ArgumentException($"Pin the {what}'s {name} by full SHA256 (InstallPins.Of computes them from the install).");
    }
    private IEnumerable<(string Name, string Value)> Values => [("game", Game), ("loader", Loader), ("patchers", Patchers)];

    /// <summary>The pins of the install at <paramref name="root"/>: its game assemblies, loader and patchers, as found.</summary>
    public static InstallPins Of(string root)
    {
        root = Path.GetFullPath(root);
        string core = Path.Combine(root, CoreDirectory);
        if (!Directory.Exists(core)) throw new DirectoryNotFoundException("BepInEx is not installed here: " + core);
        return new()
        {
            Game = GameHash(root),
            Loader = LoaderHash(root),
            Patchers = DirectoryHash(Path.Combine(root, BepInExLoader.Patchers)),
        };
    }

    /// <summary>
    /// Plugin pins derived from the staged files instead of typed by hand (a plan's <c>pins</c>, not part of an
    /// <see cref="InstallPins"/> object): each value of <paramref name="files"/> is a path
    /// relative to <paramref name="install"/> (<c>BepInEx/plugins/Jotunn.dll</c>) and becomes that file's MD5, the value
    /// <c>cli_manifest</c> and <c>cli_expect</c> compare (<see cref="FileHash.Md5"/>), or is <c>absent</c> and stays
    /// so. Refuses a path that is not a file there, naming the DLLs its folder holds, so a mistyped candidate name fails before
    /// anything launches rather than as a pin the game cannot meet.
    /// </summary>
    public static Dictionary<string, string> Plugins(string install, IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        install = Path.GetFullPath(install);
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (plugin, file) in files)
        {
            if (file == "absent") { pins[plugin] = file; continue; }
            if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file)) throw new ArgumentException($"{plugin}: give its file relative to the install, or absent.", nameof(files));
            string path = Path.GetFullPath(Path.Combine(install, file.Replace('\\', Path.DirectorySeparatorChar)));
            string relative = Path.GetRelativePath(install, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException($"{plugin}: {file} leaves the install; give a file under {install}.", nameof(files));
            if (!File.Exists(path))
            {
                string folder = Path.GetDirectoryName(path)!;
                var there = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.dll").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList() : new List<string>();
                throw new FileNotFoundException($"{plugin}: {file} is not in the install {install}; its folder holds {(there.Count == 0 ? "no DLL" : string.Join(", ", there))}. Name the file the run stages.", path);
            }
            pins[plugin] = FileHash.Md5(path);
        }
        return pins;
    }

    /// <summary>
    /// Refuses the install at <paramref name="root"/> unless its game assembly, loader and patchers are these pins,
    /// naming each that differs with the value found. <paramref name="kind"/> names the install ("runtime", "client install").
    /// Returns what it found.
    /// </summary>
    public InstallPins Check(string root, string kind)
    {
        Validate(kind);
        return Compare(Of(root), kind, Path.GetRelativePath(Path.GetFullPath(root), Path.GetDirectoryName(GameAssembly(root))!));
    }

    /// <summary>
    /// <see cref="Check"/> for pins found elsewhere (an install on another host): refuses <paramref name="found"/> unless it is
    /// these pins. <paramref name="gameFolder"/> names the Managed folder in the message. Returns <paramref name="found"/>.
    /// </summary>
    internal InstallPins Compare(InstallPins found, string kind, string gameFolder)
    {
        Validate(kind);
        var differences = new List<string>();
        if (!Same(found.Game, Game))
            differences.Add($"the game build differs ({gameFolder}/{GameAssemblies} is {found.Game}, pinned game {Game}): a game update or another branch");
        if (!Same(found.Loader, Loader))
            differences.Add($"the loader differs ({LoaderFilesText} is {found.Loader}, pinned loader {Loader}): another BepInEx, BepInExPack or Doorstop build, or a proxy or configuration replaced on its own");
        if (!Same(found.Patchers, Patchers))
            differences.Add($"the patchers differ ({BepInExLoader.Patchers} is {found.Patchers}, pinned patchers {Patchers})");
        if (differences.Count != 0)
            throw new InvalidOperationException($"The {kind} is not the pinned one: {string.Join("; ", differences)}. Restore the pinned install, or review the change and pin the new values.");
        return found;
    }
    private static bool Same(string found, string pinned) => string.Equals(found, pinned, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The game's code assembly under <paramref name="root"/>: <c>*_Data/Managed/assembly_valheim.dll</c>, in a macOS
    /// bundle <c>*.app/Contents/Resources/Data/Managed/assembly_valheim.dll</c>, or in the macOS dedicated server
    /// <c>valheim_server/Data/Managed/assembly_valheim.dll</c>. None or more than one is refused.
    /// </summary>
    public static string GameAssembly(string root)
    {
        root = Path.GetFullPath(root);
        var found = Directory.EnumerateDirectories(root, "*_Data").Select(data => Path.Combine(data, "Managed", GameAssemblyName))
            .Concat(Directory.EnumerateDirectories(root, "*.app").Select(bundle => Path.Combine(bundle, "Contents", "Resources", "Data", "Managed", GameAssemblyName)))
            .Append(Path.Combine(root, "valheim_server", "Data", "Managed", GameAssemblyName))
            .Where(File.Exists).Order(StringComparer.Ordinal).ToList();
        if (found.Count == 1) return found[0];
        if (found.Count == 0) throw new FileNotFoundException($"No game assembly ({GameAssemblyName} in a *_Data/Managed folder) under {root}.", root);
        throw new InvalidOperationException($"More than one game assembly under {root}: {string.Join(", ", found)}; refusing to guess which runs.");
    }

    /// <summary>
    /// The <see cref="Game"/> value: the listing hash of every <see cref="GameAssemblies"/> file directly in the Managed
    /// folder that holds <see cref="GameAssembly"/>.
    /// </summary>
    public static string GameHash(string root)
    {
        string managed = Path.GetDirectoryName(GameAssembly(root))!;
        // Filtered by name here rather than by the search pattern, so every platform matches alike (case-sensitive, as a shell glob).
        return ListingHash(managed, Directory.EnumerateFiles(managed).Where(path =>
            Path.GetFileName(path) is var name && name.StartsWith("assembly_", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Whether <paramref name="relative"/> (a path under an install's root with <c>/</c> separators) is one of BepInEx's loader
    /// files that <see cref="Loader"/> covers: <c>winhttp.dll</c>, <c>doorstop_config.ini</c>, <c>libdoorstop.dylib</c>, or a
    /// file under <c>BepInEx/core</c> or <c>doorstop_libs</c>. <paramref name="comparison"/> is the host's path comparison.
    /// </summary>
    internal static bool IsLoaderFile(string relative, StringComparison comparison = StringComparison.Ordinal) => LoaderPath(relative, comparison) != null;
    /// <summary>
    /// <paramref name="relative"/> with its loader file or folder in this spelling (<c>BepInEx/Core/x.dll</c> as
    /// <c>BepInEx/core/x.dll</c> under <see cref="StringComparison.OrdinalIgnoreCase"/>), so a case-insensitive host's
    /// listings hash alike whichever spelling they carry; null when it is not a loader file.
    /// </summary>
    internal static string? LoaderPath(string relative, StringComparison comparison)
    {
        if (LoaderRootFiles.FirstOrDefault(file => relative.Equals(file, comparison)) is { } root) return root;
        return LoaderFolders.FirstOrDefault(folder => relative.StartsWith(folder + "/", comparison)) is { } folder ? folder + relative[folder.Length..] : null;
    }
    // Derived from the files a launch needs (BepInExLoader.LoaderFiles, ClientLaunch.MacDoorstopFiles): each one at the root,
    // and the whole folder of each one in a folder, so the identity covers every required file and what loads beside it.
    private static readonly string[] Required =
        [.. BepInExLoader.LoaderFiles(ClientPlatform.Windows), .. BepInExLoader.LoaderFiles(ClientPlatform.Linux), .. ClientLaunch.MacDoorstopFiles];
    internal static readonly string[] LoaderRootFiles = Required.Where(file => !file.Contains('/')).Distinct(StringComparer.Ordinal).ToArray();
    internal static readonly string[] LoaderFolders = Required.Where(file => file.Contains('/')).Select(file => file[..file.LastIndexOf('/')])
        .Distinct(StringComparer.Ordinal).ToArray();
    /// <summary>The loader's files and folders, relative with <c>/</c>: what replacing the loader removes.</summary>
    internal static IEnumerable<string> LoaderEntries => LoaderRootFiles.Concat(LoaderFolders);
    private static readonly string LoaderFilesText = string.Join(", ", LoaderEntries.SkipLast(1)) + " and " + LoaderEntries.Last();

    /// <summary>The loader files (<see cref="IsLoaderFile"/>) under the install at <paramref name="root"/>, as full paths, Mac metadata left out.</summary>
    internal static IEnumerable<string> LoaderFiles(string root) =>
        LoaderRootFiles.Select(file => Path.Combine(root, file)).Where(File.Exists)
            .Concat(LoaderFolders.Select(folder => Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar))).Where(Directory.Exists)
                .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)))
            .Where(path => !IsMacMetadata(path));

    /// <summary>The <see cref="Loader"/> value of the install at <paramref name="root"/>: the listing hash of its loader files, relative to the root.</summary>
    public static string LoaderHash(string root) => ListingHash(Path.GetFullPath(root), LoaderFiles(Path.GetFullPath(root)));

    /// <summary>The SHA256 of a folder's listing (see <see cref="InstallPins"/>); an empty or absent folder hashes the empty listing.</summary>
    public static string DirectoryHash(string directory) =>
        ListingHash(directory, Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) : []);

    /// <summary>Finder's <c>.DS_Store</c> and AppleDouble <c>._*</c> files, which copying through macOS adds; never part of a listing.</summary>
    public static bool IsMacMetadata(string path)
    {
        string name = Path.GetFileName(path);
        return name == ".DS_Store" || name.StartsWith("._", StringComparison.Ordinal);
    }

    private static string ListingHash(string directory, IEnumerable<string> files) =>
        ListingHash(files.Where(path => !IsMacMetadata(path))
            .Select(path => (Relative: Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'), Sha256: (Func<string>)(() => WorldFixture.Hash(path)))));

    /// <summary>The listing hash of files already hashed elsewhere: relative paths with <c>/</c> separators and their SHA256.</summary>
    internal static string ListingHash(IEnumerable<(string Relative, string Sha256)> files) =>
        ListingHash(files.Where(file => !IsMacMetadata(file.Relative)).Select(file => (file.Relative, (Func<string>)(() => file.Sha256))));

    private static string ListingHash(IEnumerable<(string Relative, Func<string> Sha256)> files)
    {
        var listing = new StringBuilder();
        foreach (var file in files.OrderBy(file => file.Relative, StringComparer.Ordinal))
            listing.Append(file.Sha256()).Append("  ").Append(file.Relative).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(listing.ToString()))).ToLowerInvariant();
    }

    /// <summary>The values as report provenance, under <paramref name="prefix"/> (for example <c>runtime</c>: <c>runtimeGameSha256</c>).</summary>
    public void Record(IDictionary<string, string> provenance, string prefix)
    {
        provenance[prefix + "GameSha256"] = Game;
        provenance[prefix + "LoaderSha256"] = Loader;
        provenance[prefix + "PatchersSha256"] = Patchers;
    }
}
