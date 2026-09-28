using System.Diagnostics;

namespace Valheim.Testing.Game;

public enum ServerPlatform { Windows, Linux }

// Builds the direct launch DirectServerProcess needs from a copied BepInEx server runtime.
// It reproduces the variables BepInExPack_Valheim's start_server_bepinex.sh exports, without the
// script, so the started PID is the server's own and the session's PID handshake still holds.
// Arguments (-batchmode, -nographics, -savedir ...) remain the caller's plan.
public static class ServerLaunch
{
    public const string WindowsExecutable = "valheim_server.exe";
    public const string LinuxExecutable = "valheim_server.x86_64";
    public const string DedicatedServerSteamAppId = "892970";
    private static readonly string Preloader = Path.Combine("BepInEx", "core", "BepInEx.Preloader.dll");
    private static readonly string LinuxDoorstop = Path.Combine("doorstop_libs", "libdoorstop_x64.so");
    private const string WindowsDoorstop = "winhttp.dll";

    /// <summary>Decides the platform from the runtime's contents, never from the host. Refuses an ambiguous or empty runtime.</summary>
    public static ServerPlatform Detect(string runtimeDirectory)
    {
        string runtime = FullRuntime(runtimeDirectory);
        bool windows = File.Exists(Path.Combine(runtime, WindowsExecutable)), linux = File.Exists(Path.Combine(runtime, LinuxExecutable));
        if (windows && linux) throw new InvalidOperationException($"Runtime contains both {WindowsExecutable} and {LinuxExecutable}; refusing to guess its platform.");
        if (!windows && !linux) throw new FileNotFoundException($"Runtime contains neither {WindowsExecutable} nor {LinuxExecutable}.", runtime);
        return windows ? ServerPlatform.Windows : ServerPlatform.Linux;
    }

    /// <summary>Returns the detected server executable's full path. On a non-Windows host a Linux server must carry the user-execute bit.</summary>
    public static string RequireExecutable(string runtimeDirectory) => Resolve(FullRuntime(runtimeDirectory)).Executable;

    /// <summary>
    /// Start info for one owned BepInEx dedicated server. Caller environment is applied first. On Linux, Doorstop is
    /// enabled for BepInEx's preloader and the runtime's doorstop_libs/linux64 directories are prepended to any existing
    /// LD_LIBRARY_PATH/LD_PRELOAD, which are kept. SteamAppId defaults to the dedicated server's unless the caller sets it.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string runtimeDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string runtime = FullRuntime(runtimeDirectory);
        var (platform, executable) = Resolve(runtime);
        RequireFile(runtime, Preloader, "BepInEx is not installed in the runtime");
        RequireFile(runtime, platform == ServerPlatform.Windows ? WindowsDoorstop : LinuxDoorstop, "BepInEx's Doorstop loader is missing from the runtime");
        environment ??= new Dictionary<string, string>();
        var names = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (string name in new[] { "DOORSTOP_ENABLED", "DOORSTOP_TARGET_ASSEMBLY" })
            if (platform == ServerPlatform.Linux && environment.Keys.Any(key => names.Equals(key, name)))
                throw new ArgumentException(name + " is set by ServerLaunch for BepInEx; remove it from the caller environment.", nameof(environment));

        var start = new ProcessStartInfo(executable) { WorkingDirectory = runtime, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument ?? throw new ArgumentException("Null launch argument.", nameof(arguments)));
        foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        if (!environment.Keys.Any(key => names.Equals(key, "SteamAppId"))) start.Environment["SteamAppId"] = DedicatedServerSteamAppId;
        if (platform == ServerPlatform.Linux)
        {
            // Both lists split on ':' (LD_LIBRARY_PATH also on ';'), so such a path cannot be represented.
            // Checked where the launch can run; a Windows host only builds this for inspection.
            if (!OperatingSystem.IsWindows() && runtime.IndexOfAny([':', ';']) >= 0)
                throw new ArgumentException("A Linux runtime path cannot contain ':' or ';'.", nameof(runtimeDirectory));
            start.Environment["DOORSTOP_ENABLED"] = "1";
            start.Environment["DOORSTOP_TARGET_ASSEMBLY"] = Path.Combine(runtime, Preloader);
            // Same effective order as the pack's script: linux64, then doorstop_libs, then the existing value.
            start.Environment["LD_LIBRARY_PATH"] = Prepend(start.Environment, "LD_LIBRARY_PATH", Path.Combine(runtime, "linux64"), Path.Combine(runtime, "doorstop_libs"));
            start.Environment["LD_PRELOAD"] = Prepend(start.Environment, "LD_PRELOAD", "libdoorstop_x64.so");
        }
        return start;
    }

    private static (ServerPlatform Platform, string Executable) Resolve(string runtime)
    {
        var platform = Detect(runtime);
        string executable = Path.Combine(runtime, platform == ServerPlatform.Windows ? WindowsExecutable : LinuxExecutable);
        // Windows has no execute bit to read; there a Linux runtime can only be staged or inspected.
        if (!OperatingSystem.IsWindows())
        {
            if (platform == ServerPlatform.Linux && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
                throw new InvalidOperationException($"{LinuxExecutable} is not executable; restore its mode (chmod u+x) in the runtime copy.");
        }
        return (platform, executable);
    }
    private static string FullRuntime(string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeDirectory);
        string runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory));
        if (!Directory.Exists(runtime)) throw new DirectoryNotFoundException("Server runtime directory does not exist: " + runtime);
        return runtime;
    }
    private static void RequireFile(string runtime, string relative, string message)
    {
        if (!File.Exists(Path.Combine(runtime, relative))) throw new FileNotFoundException(message + ": " + relative, Path.Combine(runtime, relative));
    }
    // Keep every existing entry; an empty value adds no empty element (which would mean the working directory).
    private static string Prepend(IDictionary<string, string?> environment, string name, params string[] entries)
    {
        environment.TryGetValue(name, out string? existing);
        return string.IsNullOrEmpty(existing) ? string.Join(':', entries) : string.Join(':', entries) + ":" + existing;
    }
}
