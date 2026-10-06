using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// What the inventory's this-machine default reads: the platform, the user's folders, Steam's registered path and the files
/// under it. The one seam between the detection and this machine; controlled tests replace it with in-memory data.
/// </summary>
internal interface ISteamLocator
{
    /// <summary><c>windows</c>, <c>linux</c> or <c>macos</c>.</summary>
    string Platform { get; }
    /// <summary>The user's home folder.</summary>
    string Home { get; }
    /// <summary>ValheimTesting's own folder on this machine: runs, leases and the host lock live under it.</summary>
    string DataRoot { get; }
    /// <summary>Windows: <c>HKCU\Software\Valve\Steam</c> <c>SteamPath</c>, as Steam wrote it; null elsewhere or when unset.</summary>
    string? RegistrySteamPath();
    /// <summary>Windows: <c>%ProgramFiles(x86)%</c>; null elsewhere.</summary>
    string? ProgramFilesX86 { get; }
    bool DirectoryExists(string path);
    bool FileExists(string path);
    /// <summary>The file's text, or null when it does not exist.</summary>
    string? ReadText(string path);
    /// <summary>
    /// <paramref name="path"/> as the file system spells it, when it exists: Steam's registered path is lower case on
    /// Windows (<c>c:/program files (x86)/steam</c>), and what preflight prints should be the folder the user sees.
    /// </summary>
    string OnDiskPath(string path);
}

/// <summary>This machine, read through the file system and (on Windows) the current user's registry.</summary>
internal sealed class LocalSteamLocator : ISteamLocator
{
    public string Platform => HostProfile.CurrentPlatform;
    public string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // Set only by Fakes.FakeDataRoot: the innermost open scope, whose folder a no-game test uses as ValheimTesting's own.
    // scripts/validate.cs watches the real folder with the same platform rules: change both together.
    internal static readonly AsyncLocal<Fakes.FakeDataRoot?> SimulatedDataRoot = new();
    public string DataRoot => SimulatedDataRoot.Value?.Directory ?? Platform switch
    {
        "windows" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValheimTesting"),
        "macos" => Path.Combine(Home, "Library", "Application Support", "ValheimTesting"),
        _ => Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(Home, ".local", "share"), "ValheimTesting"),
    };
    public string? RegistrySteamPath() => OperatingSystem.IsWindows() ? WindowsSteamPath() : null;
    [SupportedOSPlatform("windows")]
    private static string? WindowsSteamPath()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string;
    }
    public string? ProgramFilesX86 => OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) : null;
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    public string OnDiskPath(string path)
    {
        if (!Path.Exists(path)) return path;
        string full = Path.GetFullPath(path), root = Path.GetPathRoot(full) ?? "";
        // A drive letter as Windows shows it; each further part as its directory lists it (an exact match first).
        string current = root.Length >= 2 && root[1] == ':' ? char.ToUpperInvariant(root[0]) + root[1..] : root;
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false, AttributesToSkip = 0 };
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] found;
            try { found = Directory.GetFileSystemEntries(current, part, options); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return path; }
            // No match (an 8.3 short name such as RUNNER~1, which listings do not show): keep the part as given and go on.
            current = found.FirstOrDefault(entry => Path.GetFileName(entry) == part) ?? (found.Length == 1 ? found[0] : Path.Combine(current, part));
        }
        return current;
    }
}

/// <summary>One Steam app's install as found in this machine's Steam libraries, and every manifest looked for.</summary>
internal sealed record SteamAppInstall(string AppId, string? Install, IReadOnlyList<string> Tried);

/// <summary>
/// This machine's Steam: its root (Windows: the registered <c>SteamPath</c>, then <c>Program Files (x86)\Steam</c>; macOS
/// <c>~/Library/Application Support/Steam</c>; Linux <c>~/.local/share/Steam</c>, <c>~/.steam/steam</c>, then the Flatpak's),
/// every library its <c>steamapps/libraryfolders.vdf</c> lists, and where each wanted app is installed
/// (<c>steamapps/appmanifest_&lt;id&gt;.acf</c>'s <c>installdir</c> under <c>steamapps/common</c>).
/// </summary>
internal sealed record SteamDetection(string? Root, string? RootRule, IReadOnlyList<string> RootsTried, IReadOnlyList<string> Libraries,
    IReadOnlyDictionary<string, SteamAppInstall> Apps)
{
    /// <summary>The game client, Valheim.</summary>
    internal const string GameApp = "892970";
    /// <summary>Valheim Dedicated Server, a free Steam app of its own.</summary>
    internal const string DedicatedServerApp = "896660";

    private static readonly Regex PathEntry = new("\"path\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
    private static readonly Regex InstallDir = new("\"installdir\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);

    internal static SteamDetection Find(ISteamLocator machine, params string[] appIds)
    {
        var candidates = new List<(string Path, string Rule)>();
        if (machine.Platform == "windows")
        {
            if (machine.RegistrySteamPath() is { Length: > 0 } registered)
                candidates.Add((registered.Replace('/', '\\'), @"registry HKCU\Software\Valve\Steam SteamPath"));
            if (machine.ProgramFilesX86 is { Length: > 0 } x86) candidates.Add((HostInstall.Join(x86, "Steam"), "Program Files (x86)"));
        }
        else if (machine.Platform == "macos") candidates.Add((HostInstall.Join(machine.Home, "Library", "Application Support", "Steam"), "standard path"));
        else
        {
            candidates.Add((HostInstall.Join(machine.Home, ".local", "share", "Steam"), "standard path"));
            candidates.Add((HostInstall.Join(machine.Home, ".steam", "steam"), "standard path"));
            candidates.Add((HostInstall.Join(machine.Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"), "Flatpak path"));
        }
        var tried = candidates.Select(candidate => candidate.Path).ToList();
        var (root, rule) = candidates.FirstOrDefault(candidate => machine.DirectoryExists(candidate.Path));
        if (root != null) root = machine.OnDiskPath(root); // what is printed; libraries keep their vdf spelling
        var comparer = machine.Platform == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var libraries = new List<string>();
        if (root != null)
        {
            libraries.Add(root);
            string? folders = machine.ReadText(HostInstall.Join(root, "steamapps", "libraryfolders.vdf"));
            if (folders != null)
                foreach (Match match in PathEntry.Matches(folders))
                {
                    string library = Unescape(match.Groups[1].Value);
                    if (machine.Platform == "windows") library = library.Replace('/', '\\');
                    if (library.Length != 0 && !libraries.Contains(library, comparer)) libraries.Add(library);
                }
        }
        var apps = new Dictionary<string, SteamAppInstall>(StringComparer.Ordinal);
        foreach (string app in appIds)
        {
            var manifests = new List<string>();
            string? install = null;
            foreach (string library in libraries)
            {
                string manifest = HostInstall.Join(library, "steamapps", "appmanifest_" + app + ".acf");
                manifests.Add(manifest);
                if (machine.ReadText(manifest) is not { } text || InstallDir.Match(text) is not { Success: true } found) continue;
                string candidate = HostInstall.Join(library, "steamapps", "common", Unescape(found.Groups[1].Value));
                if (machine.DirectoryExists(candidate)) { install = machine.OnDiskPath(candidate); break; }
                manifests[^1] += $" (names {candidate}, which does not exist)";
            }
            apps[app] = new SteamAppInstall(app, install, manifests);
        }
        return new SteamDetection(root, rule, tried, libraries, apps);
    }

    // Steam's KeyValues text escapes a backslash and a quote.
    private static string Unescape(string value) => Regex.Replace(value, @"\\(.)", "$1");
}
