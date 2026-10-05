using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Valheim.Testing.Game;

// A Windows Doorstop proxy and configuration that cannot start BepInEx together: from different Doorstop versions (a mod
// manager's proxy beside BepInExPack's file), or a proxy no check recognises. The one loader fault a reviewed package can
// stand in for without hiding another (BepInExLoaderPackage.DoorstopMismatch).
internal sealed class DoorstopPairingException(string message) : InvalidOperationException(message);

// The BepInEx loader policy every launch (GameLaunch) shares: the files a BepInEx launch needs, the Windows Doorstop
// configuration, and the caller settings that would disable or redirect the loader. It checks an install before launch;
// it cannot prove the loader ran, which the session's plugin pins do.
internal static class BepInExLoader
{
    // The loader's paths relative to an install, written with '/' (the form a host listing and a remote check use); the
    // OS-separator forms below are derived from them for this machine's file system.
    internal const string CorePreloader = "BepInEx/core/BepInEx.Preloader.dll", CoreLibrary = "BepInEx/core/BepInEx.dll",
        LinuxDoorstop = "doorstop_libs/libdoorstop_x64.so";
    internal static readonly string Preloader = Local(CorePreloader);
    internal static readonly string Core = Local(CoreLibrary);
    internal const string WindowsProxy = "winhttp.dll";
    internal const string WindowsConfig = "doorstop_config.ini";
    internal static readonly string LinuxLibrary = Local(LinuxDoorstop);
    private static string Local(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);
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
    // Shared by the local install and a remote client's bounded file read. Only the input transport differs.
    internal static void RequireWindowsLoader(byte[] proxy, string config, string root, string kind) =>
        RequireConfig(root, kind, config.Split('\n'), proxy);
    internal static readonly string Patchers = Path.Combine("BepInEx", "patchers");
    /// <summary>
    /// The files a BepInEx launch needs on a Windows or Linux host, relative with <c>/</c>: the preloader and core, and the
    /// platform's Doorstop (the Windows proxy and its configuration, or the Linux <c>libdoorstop_x64.so</c>). The one list the
    /// launches for a host (<see cref="GameLaunch.RequiredFiles"/>) and the checks of an
    /// install on another host use; a macOS install needs either of its Doorstop libraries (<see cref="RequireLoaderFiles"/>).
    /// </summary>
    internal static string[] LoaderFiles(ClientPlatform platform) => platform switch
    {
        ClientPlatform.Windows => [CorePreloader, CoreLibrary, WindowsProxy, WindowsConfig],
        ClientPlatform.Linux => [CorePreloader, CoreLibrary, LinuxDoorstop],
        _ => throw new ArgumentException("A macOS install needs one of its Doorstop libraries, not a fixed list.", nameof(platform)),
    };

    /// <summary>
    /// Refuses an install on another host whose <paramref name="present"/> files (its listing, or what an existence check found)
    /// lack one of <see cref="LoaderFiles"/>, naming each missing one; a macOS install needs the preloader, the core and either
    /// Doorstop library <see cref="GameLaunch.ForClient"/> accepts (the root <c>libdoorstop.dylib</c> or BepInExPack's
    /// <c>doorstop_libs/libdoorstop_x64.dylib</c>).
    /// </summary>
    internal static void RequireLoaderFiles(ClientPlatform platform, Func<string, bool> present, string kind)
    {
        var missing = MissingLoaderFiles(platform, present);
        if (missing.Count != 0)
            throw new FileNotFoundException($"BepInEx's loader is incomplete in the {kind}: it lacks {string.Join(", ", missing)}. Install a coherent BepInExPack, or name a reviewed loaderPackage.");
    }

    /// <summary>
    /// Which of the files a <paramref name="platform"/> launch needs <paramref name="present"/> lacks (<see cref="RequireLoaderFiles"/>),
    /// relative with <c>/</c>, for a message: macOS's alternative Doorstop libraries are one entry, "<c>a or b</c>".
    /// </summary>
    internal static List<string> MissingLoaderFiles(ClientPlatform platform, Func<string, bool> present) => platform == ClientPlatform.MacOS
        ? new[] { CorePreloader, CoreLibrary }.Where(file => !present(file))
            .Concat(ClientLaunch.MacDoorstopFiles.Any(present) ? [] : [string.Join(" or ", ClientLaunch.MacDoorstopFiles)]).ToList()
        : LoaderFiles(platform).Where(file => !present(file)).ToList();

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
    // The proxy reads only its own version's section, so the configuration must be written for the version winhttp.dll
    // shows; a proxy that shows none is refused (RequireMatchingProxy).
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
    // A proxy that shows no version cannot be paired with its file, so it fails too.
    private static void RequireMatchingProxy(byte[] proxy, string kind, HashSet<string> sections, List<(string Section, string Value)> enabled, List<(string Section, string Value)> targets)
    {
        int? major = ProxyDoorstopMajor(proxy);
        // Every Doorstop 3 and 4 proxy holds exactly one of the two keys; one holding neither or both cannot be paired with
        // its configuration, so it is refused rather than passed unchecked.
        if (major == null)
            throw new DoorstopPairingException($"The {kind}'s {WindowsProxy} is not a Doorstop proxy this check recognises: it holds " +
                "neither or both of the keys Doorstop reads (targetAssembly for Doorstop 3, target_assembly for Doorstop 4), so its configuration cannot be matched to it. " +
                $"Install {WindowsProxy} and {WindowsConfig} from one BepInExPack.");
        string own = major == 4 ? "General" : "UnityDoorstop", other = major == 4 ? "UnityDoorstop" : "General";
        bool configured = enabled.Any(entry => entry.Section == own) && targets.Any(entry => entry.Section == own);
        if (configured && (major == 3 || !sections.Contains(other))) return;
        string version = ProxyFileVersion(proxy) is { } found ? " (file version " + found + ")" : "";
        string written = sections.Contains(other) ? $"written for Doorstop {7 - major} ([{other}])" : $"without the [{own}] enabled and {(major == 4 ? "target_assembly" : "targetAssembly")} it reads";
        throw new DoorstopPairingException($"The {kind}'s {WindowsProxy} is Doorstop {major}{version}, which reads only [{own}] in {WindowsConfig}, but that file is {written}. " +
            "A proxy and configuration from different Doorstop versions start the game without BepInEx (a Doorstop 4 proxy beside a Doorstop 3 file did, even with a [General] section added). " +
            $"Install {WindowsProxy} and {WindowsConfig} from one BepInExPack, for example by restoring the pack's {WindowsProxy} after a mod manager replaced it.");
    }

    /// <summary>
    /// The Doorstop major version of a <c>winhttp.dll</c> proxy, from the configuration key its code reads (it holds the key
    /// as text): 4 for <c>target_assembly</c>, 3 for <c>targetAssembly</c>; null when it holds neither or both, which
    /// <see cref="RequireMatchingProxy"/> refuses. A proxy's file version alone is not used: <c>.doorstop_version</c> beside it outlives a replaced proxy.
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
    private static readonly string WindowsPreloader = CorePreloader.Replace('/', '\\');
    private static string Normalize(string root, string target) =>
        Path.GetFullPath(Path.Combine(root, target.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
}
