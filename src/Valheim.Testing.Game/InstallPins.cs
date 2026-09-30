using System.Security.Cryptography;
using System.Text;

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
/// <item><see cref="BepInExCore"/>: <c>BepInEx/core</c>, the loader itself (another BepInEx or BepInExPack version).</item>
/// <item><see cref="Patchers"/>: the contents of <c>BepInEx/patchers</c>, which rewrite game assemblies before any plugin
/// loads (the plan's patcher names say which entries it holds; this says which builds).</item>
/// </list>
/// Each value is the SHA256 of a listing: one line per file, <c>&lt;sha256&gt;  &lt;relative path&gt;\n</c> with
/// <c>/</c> separators, ordered by path (ordinal), as <c>sha256sum</c> prints it. Finder's <c>.DS_Store</c> and
/// AppleDouble <c>._*</c> files are left out. An empty or absent folder hashes the empty listing.
/// </summary>
public sealed class InstallPins
{
    public const string GameAssemblyName = "assembly_valheim.dll";
    /// <summary>The game's own assemblies in its Managed folder, which <see cref="Game"/> covers.</summary>
    public const string GameAssemblies = "assembly_*.dll";
    public static readonly string CoreDirectory = Path.Combine("BepInEx", "core");

    public string Game { get; set; } = "";
    public string BepInExCore { get; set; } = "";
    public string Patchers { get; set; } = "";

    /// <summary>Every value is a full SHA256. <paramref name="what"/> names the install in the message ("runtime", "client install").</summary>
    public void Validate(string what)
    {
        foreach (var (name, value) in Values)
            if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit))
                throw new ArgumentException($"Pin the {what}'s {name} by full SHA256 (InstallPins.Of computes them from the install).");
    }
    private IEnumerable<(string Name, string Value)> Values => [("game", Game), ("bepinexCore", BepInExCore), ("patchers", Patchers)];

    /// <summary>The pins of the install at <paramref name="root"/>: its game assemblies, BepInEx core and patchers, as found.</summary>
    public static InstallPins Of(string root)
    {
        root = Path.GetFullPath(root);
        string core = Path.Combine(root, CoreDirectory);
        if (!Directory.Exists(core)) throw new DirectoryNotFoundException("BepInEx is not installed here: " + core);
        return new()
        {
            Game = GameHash(root),
            BepInExCore = DirectoryHash(core),
            Patchers = DirectoryHash(Path.Combine(root, BepInExLoader.Patchers)),
        };
    }

    /// <summary>
    /// Refuses the install at <paramref name="root"/> unless its game assembly, BepInEx core and patchers are these pins,
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
        if (!Same(found.BepInExCore, BepInExCore))
            differences.Add($"BepInEx core differs ({CoreDirectory} is {found.BepInExCore}, pinned bepinexCore {BepInExCore}): another BepInEx or BepInExPack build");
        if (!Same(found.Patchers, Patchers))
            differences.Add($"the patchers differ ({BepInExLoader.Patchers} is {found.Patchers}, pinned patchers {Patchers})");
        if (differences.Count != 0)
            throw new InvalidOperationException($"The {kind} is not the pinned one: {string.Join("; ", differences)}. Restore the pinned install, or review the change and pin the new values.");
        return found;
    }
    private static bool Same(string found, string pinned) => string.Equals(found, pinned, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The game's code assembly under <paramref name="root"/>: <c>*_Data/Managed/assembly_valheim.dll</c>, or in a macOS
    /// bundle <c>*.app/Contents/Resources/Data/Managed/assembly_valheim.dll</c>. None or more than one is refused.
    /// </summary>
    public static string GameAssembly(string root)
    {
        root = Path.GetFullPath(root);
        var found = Directory.EnumerateDirectories(root, "*_Data").Select(data => Path.Combine(data, "Managed", GameAssemblyName))
            .Concat(Directory.EnumerateDirectories(root, "*.app").Select(bundle => Path.Combine(bundle, "Contents", "Resources", "Data", "Managed", GameAssemblyName)))
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
        provenance[prefix + "BepInExCoreSha256"] = BepInExCore;
        provenance[prefix + "PatchersSha256"] = Patchers;
    }
}
