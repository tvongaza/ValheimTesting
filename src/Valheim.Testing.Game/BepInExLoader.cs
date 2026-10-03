using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Valheim.Testing.Game;

// The BepInEx loader policy ServerLaunch and ClientLaunch share: the files a BepInEx launch needs, the Windows Doorstop
// configuration, and the caller settings that would disable or redirect the loader. It checks an install before launch;
// it cannot prove the loader ran, which the session's plugin pins do.
internal static class BepInExLoader
{
    internal static readonly string Preloader = Path.Combine("BepInEx", "core", "BepInEx.Preloader.dll");
    internal static readonly string Core = Path.Combine("BepInEx", "core", "BepInEx.dll");
    internal const string WindowsProxy = "winhttp.dll";
    internal const string WindowsConfig = "doorstop_config.ini";
    internal static readonly string LinuxLibrary = Path.Combine("doorstop_libs", "libdoorstop_x64.so");
    // Doorstop reads these; any other value would disable or redirect the loader and start the game without BepInEx.
    internal static readonly string[] Variables = ["DOORSTOP_ENABLED", "DOORSTOP_TARGET_ASSEMBLY", "DOORSTOP_DISABLE"];
    private const string ArgumentPrefix = "--doorstop-";

    /// <summary>BepInEx's preloader and core. <paramref name="kind"/> names the directory in messages ("runtime", "install").</summary>
    internal static void RequireCore(string root, string kind)
    {
        RequireFile(root, Preloader, "BepInEx is not installed in the " + kind);
        RequireFile(root, Core, "BepInEx is not installed in the " + kind);
        // Copying an extracted BepInEx/core directory into an existing core can create core/core.
        // The preloader walks it and loads 0Harmony20 twice, then exits before writing LogOutput.log.
        if (Directory.Exists(Path.Combine(root, "BepInEx", "core", "core")))
            throw new InvalidOperationException($"The {kind} has a nested BepInEx/core/core. Replace the loader as one coherent tree; do not copy core into an existing core directory.");
    }
    /// <summary>The winhttp.dll proxy and a doorstop_config.ini that enables Doorstop and targets BepInEx's preloader.</summary>
    internal static void RequireWindowsLoader(string root, string kind)
    {
        RequireFile(root, WindowsProxy, "BepInEx's Doorstop loader is missing from the " + kind);
        string path = Path.Combine(root, WindowsConfig);
        if (!File.Exists(path)) throw new FileNotFoundException($"BepInEx's Doorstop configuration is missing from the {kind}: " + WindowsConfig, path);
        RequireWindowsLoader(File.ReadAllBytes(Path.Combine(root, WindowsProxy)), File.ReadAllText(path), root, kind);
    }
    // Shared by the local install and a remote profile client's bounded file read. Only the input transport differs.
    internal static void RequireWindowsLoader(byte[] proxy, string config, string root, string kind) =>
        RequireConfig(root, kind, config.Split('\n'), proxy);
    internal static readonly string Patchers = Path.Combine("BepInEx", "patchers");
    /// <summary>
    /// Refuses a runtime whose <c>BepInEx/patchers</c> holds an entry (file or directory) that <paramref name="named"/> does
    /// not list, or lacks one it lists. Preloader patchers rewrite game assemblies before any plugin loads, so one left behind
    /// by a removed mod breaks the whole run (a <c>TypeLoadException</c> on a game type); a clean runtime has none unless the
    /// plan says so. Names compare case-insensitively on Windows.
    /// </summary>
    internal static void RequirePatchers(string root, IReadOnlyCollection<string> named, string kind)
    {
        CheckPatcherNames(named);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string directory = Path.Combine(root, Patchers);
        var present = Directory.Exists(directory) ? Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>().ToList() : new List<string>();
        var unnamed = present.Where(entry => !named.Contains(entry, comparer)).Order(StringComparer.Ordinal).ToList();
        if (unnamed.Count != 0)
            throw new InvalidOperationException($"The {kind}'s {Patchers} holds {string.Join(", ", unnamed)}, which the plan's patchers do not name. " +
                "A clean runtime is BepInEx core and your plugins with an empty patchers directory: remove what a removed mod left behind, or name each patcher the run needs.");
        var missing = named.Where(entry => !present.Contains(entry, comparer)).ToList();
        if (missing.Count != 0)
            throw new InvalidOperationException($"The plan names patchers the {kind}'s {Patchers} does not hold: {string.Join(", ", missing)}.");
    }
    /// <summary>Plan patcher entries are single names in BepInEx/patchers, each listed once.</summary>
    internal static void CheckPatcherNames(IReadOnlyCollection<string> named)
    {
        if (named == null) throw new ArgumentException("Patchers must be a list of names, empty for a clean runtime.");
        foreach (string? name in named)
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"Patchers are entry names directly in {Patchers}, not paths: \"{name}\".");
        if (named.Distinct(StringComparer.OrdinalIgnoreCase).Count() != named.Count) throw new ArgumentException("Name each patcher once.");
    }
    internal static void RequireFile(string root, string relative, string message)
    {
        if (!File.Exists(Path.Combine(root, relative))) throw new FileNotFoundException(message + ": " + relative, Path.Combine(root, relative));
    }

    /// <summary>
    /// Refuses caller settings that would override the loader: Doorstop's variables in <paramref name="environment"/> and
    /// <c>--doorstop-*</c> arguments. Returns the arguments, checked for nulls.
    /// </summary>
    internal static List<string> RefuseOverrides(IReadOnlyDictionary<string, string> environment, IEnumerable<string> arguments, StringComparer names, string launcher)
    {
        foreach (string name in Variables)
            if (environment.Keys.Any(key => names.Equals(key, name)))
                throw new ArgumentException(name + " would disable or redirect BepInEx's loader; remove it from the caller environment.", nameof(environment));
        var passed = arguments.Select(argument => argument ?? throw new ArgumentException("Null launch argument.", nameof(arguments))).ToList();
        string? doorstop = passed.FirstOrDefault(argument => argument.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase));
        if (doorstop != null)
            throw new ArgumentException(doorstop + " would override BepInEx's loader; " + launcher + " configures Doorstop itself.", nameof(arguments));
        return passed;
    }
    /// <summary>Applies the caller environment, then removes inherited Doorstop variables: only the ones the launcher sets may reach the game.</summary>
    internal static void ApplyEnvironment(ProcessStartInfo start, IReadOnlyDictionary<string, string> environment)
    {
        foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        foreach (string name in Variables) start.Environment.Remove(name);
    }
    // Keep every existing entry; an empty value adds no empty element (which would mean the working directory).
    internal static string Prepend(IDictionary<string, string?> environment, string name, params string[] entries)
    {
        environment.TryGetValue(name, out string? existing);
        return string.IsNullOrEmpty(existing) ? string.Join(':', entries) : string.Join(':', entries) + ":" + existing;
    }

    // Doorstop 4 reads [General] enabled and target_assembly; Doorstop 3 read [UnityDoorstop] enabled and targetAssembly.
    // Either section may enable the loader, none may disable it (a value other than true reads as false), and every stated
    // target must be BepInEx's preloader: with the proxy present but a disabled or redirected configuration, the game would
    // start without BepInEx. Windows paths compare case-insensitively, with either separator and relative or absolute.
    // The proxy reads only its own version's section, so when winhttp.dll shows which version it is, the configuration
    // must be written for that version (RequireMatchingProxy).
    private static readonly (string Section, string Target)[] Sections = [("General", "target_assembly"), ("UnityDoorstop", "targetAssembly")];
    private static void RequireConfig(string root, string kind, IEnumerable<string> lines, byte[] proxy)
    {
        string? section = null;
        var enabled = new List<(string Section, string Value)>();
        var targets = new List<(string Section, string Value)>();
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[') { section = line.Trim('[', ']').Trim(); sections.Add(section); continue; }
            int equals = line.IndexOf('=');
            var known = Sections.FirstOrDefault(entry => string.Equals(entry.Section, section, StringComparison.OrdinalIgnoreCase));
            if (equals < 0 || known.Section == null) continue;
            string key = line[..equals].Trim(), value = line[(equals + 1)..].Trim();
            if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase)) enabled.Add((known.Section, value));
            else if (key.Equals(known.Target, StringComparison.OrdinalIgnoreCase)) targets.Add((known.Section, value));
        }
        var disabled = enabled.FirstOrDefault(entry => !string.Equals(entry.Value, "true", StringComparison.OrdinalIgnoreCase));
        if (enabled.Count == 0 || disabled.Section != null)
            throw new InvalidOperationException($"{WindowsConfig} does not enable Doorstop (" +
                (disabled.Section != null ? $"[{disabled.Section}] enabled = {disabled.Value}" : "no [General] or [UnityDoorstop] enabled") +
                "); set enabled = true, or the game starts without BepInEx.");
        var stated = targets.Where(entry => entry.Value.Length != 0).ToList();
        if (stated.Count == 0)
            throw new InvalidOperationException($"{WindowsConfig} names no [General] target_assembly or [UnityDoorstop] targetAssembly; set it to {WindowsPreloader}.");
        string expected = Normalize(root, WindowsPreloader);
        foreach (var (targetSection, target) in stated)
        {
            if (!string.Equals(Normalize(root, target), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{WindowsConfig} [{targetSection}] targets {target}, not BepInEx's preloader; set it to {WindowsPreloader}, or the game starts without BepInEx.");
        }
        RequireMatchingProxy(proxy, kind, sections, enabled, stated);
    }

    // A Doorstop 4 winhttp.dll beside BepInExPack's older Doorstop 3 doorstop_config.ini (a mod manager's launch copies
    // its own proxy into the game folder) started the Windows client without BepInEx: no BepInEx log, the game at its menu.
    // Adding a [General] section to that file did not make it load. So a proxy that shows its version needs a file written
    // for that version: Doorstop 4 its [General] keys and no [UnityDoorstop] section, Doorstop 3 its [UnityDoorstop] keys.
    private static void RequireMatchingProxy(byte[] proxy, string kind, HashSet<string> sections, List<(string Section, string Value)> enabled, List<(string Section, string Value)> targets)
    {
        int? major = ProxyDoorstopMajor(proxy);
        if (major == null) return;
        string own = major == 4 ? "General" : "UnityDoorstop", other = major == 4 ? "UnityDoorstop" : "General";
        bool configured = enabled.Any(entry => entry.Section == own) && targets.Any(entry => entry.Section == own);
        if (configured && (major == 3 || !sections.Contains(other))) return;
        string version = ProxyFileVersion(proxy) is { } found ? " (file version " + found + ")" : "";
        string written = sections.Contains(other) ? $"written for Doorstop {7 - major} ([{other}])" : $"without the [{own}] enabled and {(major == 4 ? "target_assembly" : "targetAssembly")} it reads";
        throw new InvalidOperationException($"The {kind}'s {WindowsProxy} is Doorstop {major}{version}, which reads only [{own}] in {WindowsConfig}, but that file is {written}. " +
            "A proxy and configuration from different Doorstop versions start the game without BepInEx (a Doorstop 4 proxy beside a Doorstop 3 file did, even with a [General] section added). " +
            $"Install {WindowsProxy} and {WindowsConfig} from one BepInExPack, for example by restoring the pack's {WindowsProxy} after a mod manager replaced it.");
    }

    /// <summary>
    /// The Doorstop major version of a <c>winhttp.dll</c> proxy, from the configuration key its code reads (it holds the key
    /// as text): 4 for <c>target_assembly</c>, 3 for <c>targetAssembly</c>; null when it holds neither or both, which says
    /// nothing. A proxy's file version alone is not used: <c>.doorstop_version</c> beside it outlives a replaced proxy.
    /// </summary>
    internal static int? ProxyDoorstopMajor(byte[] proxy)
    {
        bool four = Holds(proxy, "target_assembly"), three = Holds(proxy, "targetAssembly");
        return four == three ? null : four ? 4 : 3;
    }
    private static bool Holds(byte[] bytes, string text) =>
        bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0 || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

    /// <summary>The file version in a Windows DLL's version resource (VS_FIXEDFILEINFO, read from its bytes), for messages; null without one.</summary>
    internal static string? ProxyFileVersion(byte[] dll)
    {
        ReadOnlySpan<byte> signature = [0xBD, 0x04, 0xEF, 0xFE]; // dwSignature 0xFEEF04BD, little-endian
        int at = dll.AsSpan().IndexOf(signature);
        if (at < 0 || at + 16 > dll.Length) return null;
        uint high = BinaryPrimitives.ReadUInt32LittleEndian(dll.AsSpan(at + 8)), low = BinaryPrimitives.ReadUInt32LittleEndian(dll.AsSpan(at + 12));
        return $"{high >> 16}.{high & 0xFFFF}.{low >> 16}";
    }
    private const string WindowsPreloader = @"BepInEx\core\BepInEx.Preloader.dll";
    private static string Normalize(string root, string target) =>
        Path.GetFullPath(Path.Combine(root, target.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
}
