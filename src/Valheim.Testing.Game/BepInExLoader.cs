using System.Diagnostics;

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
    }
    /// <summary>The winhttp.dll proxy and a doorstop_config.ini that enables Doorstop and targets BepInEx's preloader.</summary>
    internal static void RequireWindowsLoader(string root, string kind)
    {
        RequireFile(root, WindowsProxy, "BepInEx's Doorstop loader is missing from the " + kind);
        RequireConfig(root, kind);
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
    // BepInExPack_Valheim installs have been seen with Doorstop 4's winhttp.dll and the Doorstop 3 file, so both are read.
    // Either section may enable the loader, none may disable it (a value other than true reads as false), and every stated
    // target must be BepInEx's preloader: with the proxy present but a disabled or redirected configuration, the game would
    // start without BepInEx. Windows paths compare case-insensitively, with either separator and relative or absolute.
    private static readonly (string Section, string Target)[] Sections = [("General", "target_assembly"), ("UnityDoorstop", "targetAssembly")];
    private static void RequireConfig(string root, string kind)
    {
        string path = Path.Combine(root, WindowsConfig);
        if (!File.Exists(path)) throw new FileNotFoundException($"BepInEx's Doorstop configuration is missing from the {kind}: " + WindowsConfig, path);
        string? section = null;
        var enabled = new List<(string Section, string Value)>();
        var targets = new List<(string Section, string Value)>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[') { section = line.Trim('[', ']').Trim(); continue; }
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
            if (!string.Equals(Normalize(root, target), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{WindowsConfig} [{targetSection}] targets {target}, not BepInEx's preloader; set it to {WindowsPreloader}, or the game starts without BepInEx.");
    }
    private const string WindowsPreloader = @"BepInEx\core\BepInEx.Preloader.dll";
    private static string Normalize(string root, string target) =>
        Path.GetFullPath(Path.Combine(root, target.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
}
