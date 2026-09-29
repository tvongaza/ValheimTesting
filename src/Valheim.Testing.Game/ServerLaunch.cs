using System.Diagnostics;

namespace Valheim.Testing.Game;

public enum ServerPlatform { Windows, Linux }

// The machine building the launch. macOS has no dedicated server of its own and cannot execute either one.
// Other Unix hosts behave as Linux. Injectable so every host's branches are tested on any OS.
internal enum ServerHost { Windows, Linux, MacOS }

// Builds the direct launch DirectServerProcess needs from a copied BepInEx server runtime.
// It reproduces the variables BepInExPack_Valheim's start_server_bepinex.sh exports, without the
// script, so the started PID is the server's own and the session's PID handshake still holds.
// Arguments (-batchmode, -nographics, -savedir ...) remain the caller's plan.
public static class ServerLaunch
{
    public const string WindowsExecutable = "valheim_server.exe";
    public const string LinuxExecutable = "valheim_server.x86_64";
    public const string DedicatedServerSteamAppId = "892970";
    private const string MacClientBundle = "Valheim.app";
    private const string NoMacServer = "There is no macOS dedicated server; run the Linux server image in a container " +
        "(docker --platform linux/amd64, see docker/linux-server) or use a remote Windows/Linux host.";

    internal static ServerHost CurrentHost =>
        OperatingSystem.IsWindows() ? ServerHost.Windows : OperatingSystem.IsMacOS() ? ServerHost.MacOS : ServerHost.Linux;

    /// <summary>
    /// Decides the platform from the runtime's contents, never from the host. Refuses an ambiguous or empty runtime,
    /// and a macOS game client (a Valheim.app bundle, or a directory holding one) with <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public static ServerPlatform Detect(string runtimeDirectory)
    {
        string runtime = FullRuntime(runtimeDirectory);
        bool windows = File.Exists(Path.Combine(runtime, WindowsExecutable)), linux = File.Exists(Path.Combine(runtime, LinuxExecutable));
        if (windows && linux) throw new InvalidOperationException($"Runtime contains both {WindowsExecutable} and {LinuxExecutable}; refusing to guess its platform.");
        if (!windows && !linux && IsMacClient(runtime))
            throw new PlatformNotSupportedException($"{runtime} is the macOS Valheim game client ({MacClientBundle}), not a dedicated server; ClientLaunch builds its launch. " + NoMacServer);
        if (windows || linux) return windows ? ServerPlatform.Windows : ServerPlatform.Linux;
        string? client = new[] { ClientLaunch.WindowsExecutable, ClientLaunch.LinuxExecutable }.FirstOrDefault(name => File.Exists(Path.Combine(runtime, name)));
        throw new FileNotFoundException($"Runtime contains neither {WindowsExecutable} nor {LinuxExecutable}." +
            (client == null ? "" : $" It holds the game client {client}; launch it with ClientLaunch."), runtime);
    }

    /// <summary>
    /// Returns the detected server executable's full path. On a Linux host a Linux server must carry the user-execute bit.
    /// A macOS host refuses with <see cref="PlatformNotSupportedException"/>: it can execute neither server.
    /// </summary>
    public static string RequireExecutable(string runtimeDirectory) => RequireExecutable(runtimeDirectory, CurrentHost);
    internal static string RequireExecutable(string runtimeDirectory, ServerHost host) => Resolve(FullRuntime(runtimeDirectory), host).Executable;

    /// <summary>
    /// Start info for one owned BepInEx dedicated server. BepInEx's preloader and core and the platform's Doorstop loader
    /// must be present; on Windows doorstop_config.ini must enable Doorstop and target BepInEx's preloader. Caller
    /// environment is applied first and may not set Doorstop's variables or pass <c>--doorstop-*</c> arguments; inherited
    /// Doorstop variables are removed. On Linux, Doorstop is enabled for BepInEx's preloader and the runtime's
    /// doorstop_libs/linux64 directories are prepended to any existing LD_LIBRARY_PATH/LD_PRELOAD, which are kept.
    /// SteamAppId defaults to the dedicated server's unless the caller sets it.
    /// Unlike <see cref="ClientLaunch"/>, a host may build another platform's launch: a Windows host builds a Linux launch
    /// for inspection only. A macOS host refuses with <see cref="PlatformNotSupportedException"/> rather than executing a
    /// Linux or Windows binary: use the Linux container or a remote Windows/Linux host.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string runtimeDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null) =>
        CreateStartInfo(runtimeDirectory, arguments, environment, CurrentHost);

    internal static ProcessStartInfo CreateStartInfo(string runtimeDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment, ServerHost host)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string runtime = FullRuntime(runtimeDirectory);
        var (platform, executable) = Resolve(runtime, host);
        BepInExLoader.RequireCore(runtime, "runtime");
        if (platform == ServerPlatform.Windows) BepInExLoader.RequireWindowsLoader(runtime, "runtime");
        else BepInExLoader.RequireFile(runtime, BepInExLoader.LinuxLibrary, "BepInEx's Doorstop loader is missing from the runtime");
        environment ??= new Dictionary<string, string>();
        var names = host == ServerHost.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var passed = BepInExLoader.RefuseOverrides(environment, arguments, names, nameof(ServerLaunch));

        var start = new ProcessStartInfo(executable) { WorkingDirectory = runtime, UseShellExecute = false };
        foreach (string argument in passed) start.ArgumentList.Add(argument);
        // Inherited Doorstop values would reach the server too; only the ones set below may.
        BepInExLoader.ApplyEnvironment(start, environment);
        if (!environment.Keys.Any(key => names.Equals(key, "SteamAppId"))) start.Environment["SteamAppId"] = DedicatedServerSteamAppId;
        if (platform == ServerPlatform.Linux)
        {
            // Both lists split on ':' (LD_LIBRARY_PATH also on ';'), so such a path cannot be represented.
            // Checked where the launch can run; a Windows host only builds this for inspection.
            if (host != ServerHost.Windows && runtime.IndexOfAny([':', ';']) >= 0)
                throw new ArgumentException("A Linux runtime path cannot contain ':' or ';'.", nameof(runtimeDirectory));
            start.Environment["DOORSTOP_ENABLED"] = "1";
            start.Environment["DOORSTOP_TARGET_ASSEMBLY"] = Path.Combine(runtime, BepInExLoader.Preloader);
            // Same effective order as the pack's script: linux64, then doorstop_libs, then the existing value.
            start.Environment["LD_LIBRARY_PATH"] = BepInExLoader.Prepend(start.Environment, "LD_LIBRARY_PATH", Path.Combine(runtime, "linux64"), Path.Combine(runtime, "doorstop_libs"));
            start.Environment["LD_PRELOAD"] = BepInExLoader.Prepend(start.Environment, "LD_PRELOAD", "libdoorstop_x64.so");
        }
        return start;
    }

    private static (ServerPlatform Platform, string Executable) Resolve(string runtime, ServerHost host)
    {
        var platform = Detect(runtime);
        string executable = Path.Combine(runtime, platform == ServerPlatform.Windows ? WindowsExecutable : LinuxExecutable);
        // Refused before any file mode is read: exec of an ELF or PE binary on macOS would fail with an opaque error.
        if (host == ServerHost.MacOS)
            throw new PlatformNotSupportedException($"This macOS host cannot run the {platform} dedicated server {executable}. " + NoMacServer);
        // Windows has no execute bit to read; there a Linux runtime can only be staged or inspected.
        // The outer check is the real OS (Unix modes exist); the inner one is the host this launch is built for.
        if (!OperatingSystem.IsWindows())
        {
            if (host != ServerHost.Windows && platform == ServerPlatform.Linux && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
                throw new InvalidOperationException($"{LinuxExecutable} is not executable; restore its mode (chmod u+x) in the runtime copy.");
        }
        return (platform, executable);
    }
    // The bundle itself (any name, recognised by its executable), a directory named Valheim.app, or a Steam install holding one.
    private static bool IsMacClient(string runtime) =>
        string.Equals(Path.GetFileName(runtime), MacClientBundle, StringComparison.OrdinalIgnoreCase)
        || File.Exists(Path.Combine(runtime, "Contents", "MacOS", "Valheim"))
        || Directory.Exists(Path.Combine(runtime, MacClientBundle));
    private static string FullRuntime(string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeDirectory);
        string runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory));
        if (!Directory.Exists(runtime)) throw new DirectoryNotFoundException("Server runtime directory does not exist: " + runtime);
        return runtime;
    }
}
