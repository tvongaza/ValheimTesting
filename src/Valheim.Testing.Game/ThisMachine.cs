using System.Runtime.Versioning;
using System.Runtime.InteropServices;
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
    /// <summary>The machine's CPU architecture, even when this process runs under translation.</summary>
    Architecture OsArchitecture { get; }
    /// <summary>The user's home folder.</summary>
    string Home { get; }
    /// <summary>ValheimTesting's own folder on this machine: journals, leases and the host lock live under it.</summary>
    string DataRoot { get; }
    /// <summary>Windows: <c>HKCU\Software\Valve\Steam</c> <c>SteamPath</c>, as Steam wrote it; null elsewhere or when unset.</summary>
    string? RegistrySteamPath();
    bool DirectoryExists(string path);
    bool FileExists(string path);
    /// <summary>The file's text, or null when it does not exist.</summary>
    string? ReadText(string path);
    /// <summary>
    /// <paramref name="path"/> as the file system spells it, when it exists: Steam's registered path is lower case on
    /// Windows (<c>c:/program files (x86)/steam</c>), and what preflight prints should be the folder the user sees.
    /// </summary>
    string OnDiskPath(string path);
    /// <summary>
    /// The final path reached through a link or junction at <paramref name="path"/> or one of its parents. A path that has
    /// no linked component is returned unchanged.
    /// </summary>
    string FinalPath(string path);
    /// <summary>
    /// The folder a link at <paramref name="path"/> finally points to, or null when it is no link: Linux's <c>~/.steam/steam</c> links
    /// to the folder Steam's <c>libraryfolders.vdf</c> lists under its own name.
    /// </summary>
    string? LinkTarget(string path);
    /// <summary>Windows: the actual volume and filesystem containing the path or its nearest existing parent.</summary>
    WindowsStorageVolume StorageVolume(string path);
    /// <summary>Linux: the mount, device and filesystem containing the path or its nearest existing parent (<see cref="LinuxStorageProbe"/>).</summary>
    LinuxStorageVolume LinuxStorageVolume(string path);
}

/// <summary>This machine, read through the file system and (on Windows) the current user's registry.</summary>
internal sealed class LocalSteamLocator : ISteamLocator
{
    public string Platform => HostProfile.CurrentPlatform;
    public Architecture OsArchitecture => RuntimeInformation.OSArchitecture;
    public string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // Set only by Fakes.FakeDataRoot: the innermost open scope, whose folder a no-game test uses as ValheimTesting's own.
    // scripts/validate.cs watches the real folder with the same platform rules: change both together.
    internal static readonly AsyncLocal<Fakes.FakeDataRoot?> SimulatedDataRoot = new();
    // Test seam: the folder every test of an assembly uses in place of the real one when no FakeDataRoot scope is open (its
    // module initializer sets it). The one owner of ValheimTesting's folder on this machine: the pinned bundle and pack, the
    // run journal, holds and detached runs all resolve through DataRoot.
    internal static string? DataRootDefault { get; set; }
    public string DataRoot => SimulatedDataRoot.Value?.Directory ?? DataRootDefault ?? Platform switch
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
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    public string? LinkTarget(string path)
    {
        try { return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
    public string FinalPath(string path)
    {
        string full = Path.GetFullPath(path);
        var suffix = new Stack<string>();
        string? current = full;
        while (current != null)
        {
            string trimmed = Path.TrimEndingDirectorySeparator(current);
            // A volume root is never a link, and Windows refuses to resolve one (C:\ throws "Could not find a part of the path").
            if (Path.GetDirectoryName(trimmed) == null) break;
            try
            {
                FileSystemInfo? target = Directory.Exists(trimmed)
                    ? Directory.ResolveLinkTarget(trimmed, returnFinalTarget: true)
                    : File.Exists(trimmed) ? File.ResolveLinkTarget(trimmed, returnFinalTarget: true) : null;
                if (target != null)
                {
                    string resolved = target.FullName;
                    foreach (string part in suffix) resolved = Path.Combine(resolved, part);
                    return resolved;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new IOException($"Could not resolve links in {path}: {error.Message}", error); }
            string? parent = Path.GetDirectoryName(trimmed);
            if (parent == null || parent == current || parent == trimmed) break;
            suffix.Push(Path.GetFileName(trimmed));
            current = parent;
        }
        return full;
    }
    public WindowsStorageVolume StorageVolume(string path) => WindowsStorageProbe.Inspect(path);
    public LinuxStorageVolume LinuxStorageVolume(string path) => LinuxStorageProbe.Inspect(path);
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
/// This machine's Steam (#709: the one owner of where Steam's root is). Its root is only ever the native default: on Windows the
/// user's registered <c>HKCU\Software\Valve\Steam</c> <c>SteamPath</c>, on macOS <c>~/Library/Application Support/Steam</c>, on
/// Linux <c>~/.steam/steam</c>. Steam anywhere else (a Flatpak, another folder) is not searched: what is needed from it is given
/// explicitly, and <see cref="NotFound"/> says so. Then every library its <c>steamapps/libraryfolders.vdf</c> lists, and where each
/// wanted app is installed (<c>steamapps/appmanifest_&lt;id&gt;.acf</c>'s <c>installdir</c> under <c>steamapps/common</c>). A host's
/// scripts find the root by the same rule through <see cref="BashRoot"/> or <see cref="PowerShellRoot"/>.
/// </summary>
internal sealed record SteamDetection(string? Root, string RootRule, string? Reason, IReadOnlyList<string> Libraries,
    IReadOnlyDictionary<string, SteamAppInstall> Apps)
{
    /// <summary>The game client, Valheim.</summary>
    internal const string GameApp = "892970";
    /// <summary>Valheim Dedicated Server, a free Steam app of its own.</summary>
    internal const string DedicatedServerApp = "896660";
    /// <summary>Where Windows Steam registers its root for the user.</summary>
    internal const string RegistryValue = @"HKCU\Software\Valve\Steam SteamPath";

    /// <summary>
    /// A macOS host's copy of the rule, for the client scripts (a Linux host runs the dedicated server only, and no script there
    /// reads Steam): sets <c>steam</c> to the script's <c>steam</c> variable when it is given (a folder named explicitly), else to
    /// <c>~/Library/Application Support/Steam</c>. Whether it exists is the script's to check.
    /// </summary>
    internal static readonly string BashRoot = """
        if [ -z "${steam:-}" ]; then steam="${HOME:-/nonexistent}/Library/Application Support/Steam"; fi

        """.ReplaceLineEndings("\n");

    /// <summary>
    /// A Windows host's (pwsh) copy of the rule: sets <c>$steam</c> to the script's <c>steam</c> variable when it is given, else to
    /// the user's registered <c>SteamPath</c> (written by Steam with forward slashes), with backslashes; empty when Steam has never
    /// run for the user.
    /// </summary>
    internal static readonly string PowerShellRoot = """
        if (-not $steam) { $steam = [string][Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Valve\Steam', 'SteamPath', $null) }
        $steam = ([string]$steam) -replace '/', '\'

        """.ReplaceLineEndings("\n");

    /// <summary>When <see cref="Root"/> is null: the one refusal, saying why and what to do instead.</summary>
    internal string? NotFound => Reason == null ? null : "Steam was not found: " + Reason +
        ". Steam in another folder (a Flatpak or a custom one) is not searched: list the environment in an inventory file with its install.";

    private static readonly Regex PathEntry = new("\"path\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
    private static readonly Regex InstallDir = new("\"installdir\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);

    /// <summary>The root alone, by the rule above: no library or manifest is read.</summary>
    internal static (string? Root, string Rule, string? Reason) FindRoot(ISteamLocator machine)
    {
        string? root = machine.Platform switch
        {
            "windows" => machine.RegistrySteamPath() is { Length: > 0 } registered ? registered.Replace('/', '\\') : null,
            "macos" => HostPath.Join(machine.Home, "Library", "Application Support", "Steam"),
            _ => HostPath.Join(machine.Home, ".steam", "steam"),
        };
        string rule = machine.Platform == "windows" ? "registry " + RegistryValue : "standard path";
        string? reason = root == null ? RegistryValue + " is not set (Steam has not run for this user)"
            : machine.DirectoryExists(root) ? null
            : machine.Platform == "windows" ? $"{RegistryValue} names {root}, which does not exist" : $"its standard path {root} does not exist";
        return (reason != null ? null : machine.OnDiskPath(root!), rule, reason); // what is printed; libraries keep their vdf spelling
    }

    internal static SteamDetection Find(ISteamLocator machine, params string[] appIds)
    {
        var (root, rule, reason) = FindRoot(machine);
        var comparer = machine.Platform == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var libraries = new List<string>();
        if (root != null)
        {
            libraries.Add(root);
            // The root's own library, listed under the name a link at the root points to, is the same library.
            string? linked = machine.LinkTarget(root);
            string? folders = machine.ReadText(HostPath.Join(root, "steamapps", "libraryfolders.vdf"));
            if (folders != null)
                foreach (Match match in PathEntry.Matches(folders))
                {
                    string library = Unescape(match.Groups[1].Value);
                    if (machine.Platform == "windows") library = library.Replace('/', '\\');
                    if (library.Length != 0 && !libraries.Contains(library, comparer) && !comparer.Equals(library, linked)) libraries.Add(library);
                }
        }
        var apps = new Dictionary<string, SteamAppInstall>(StringComparer.Ordinal);
        foreach (string app in appIds)
        {
            var manifests = new List<string>();
            string? install = null;
            foreach (string library in libraries)
            {
                string manifest = HostPath.Join(library, "steamapps", "appmanifest_" + app + ".acf");
                manifests.Add(manifest);
                if (machine.ReadText(manifest) is not { } text || InstallDir.Match(text) is not { Success: true } found) continue;
                string candidate = HostPath.Join(library, "steamapps", "common", Unescape(found.Groups[1].Value));
                if (machine.DirectoryExists(candidate)) { install = machine.OnDiskPath(candidate); break; }
                manifests[^1] += $" (names {candidate}, which does not exist)";
            }
            apps[app] = new SteamAppInstall(app, install, manifests);
        }
        return new SteamDetection(root, rule, reason, libraries, apps);
    }

    // Steam's KeyValues text escapes a backslash and a quote.
    private static string Unescape(string value) => Regex.Replace(value, @"\\(.)", "$1");
}
