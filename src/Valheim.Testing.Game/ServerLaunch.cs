using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Valheim.Testing.Game;

public enum ServerPlatform { Windows, Linux, MacOS }

// The machine building the launch. Each dedicated server runs only on its own OS (a Windows host may build a Linux launch
// for inspection). Other Unix hosts behave as Linux. Injectable so every host's branches are tested on any OS.
internal enum ServerHost { Windows, Linux, MacOS }

// Builds the direct launch DirectServerProcess needs from a copied BepInEx server runtime.
// It reproduces the variables BepInExPack_Valheim's start_server_bepinex.sh exports, without the
// script, so the started PID is the server's own and the session's PID handshake still holds.
// Arguments (-batchmode, -nographics, -savedir ...) remain the caller's plan.
public static class ServerLaunch
{
    public const string WindowsExecutable = "valheim_server.exe";
    public const string LinuxExecutable = "valheim_server.x86_64";
    /// <summary>
    /// The macOS dedicated server (Steam app 896660's macOS depot), relative to the runtime's root: a universal Mach-O
    /// (x86_64 and arm64) beside Unity's player and Mono libraries, with its <c>Data</c> folder next to it. Not an app bundle.
    /// </summary>
    public const string MacExecutable = "valheim_server/Valheim";
    private const string MacData = "valheim_server/Data";
    // The same two paths with this OS's separator, for full paths built from a runtime directory.
    private static readonly string MacExecutablePath = Path.Combine("valheim_server", "Valheim"), MacDataPath = Path.Combine("valheim_server", "Data");
    public const string DedicatedServerSteamAppId = "892970";
    private const string MacClientBundle = "Valheim.app";
    private const string MacServerHint = "On a Mac, run the macOS dedicated server (Steam app 896660 installed on macOS, " + MacExecutable + "); " +
        "the Windows and Linux servers need their own OS: the Linux server image in a container (docker --platform linux/amd64, see docker/linux-server) or a remote Windows/Linux host.";

    internal static ServerHost CurrentHost =>
        OperatingSystem.IsWindows() ? ServerHost.Windows : OperatingSystem.IsMacOS() ? ServerHost.MacOS : ServerHost.Linux;
    /// <summary>The dedicated-server platform this machine runs: Windows, macOS, or Linux for any other Unix.</summary>
    public static ServerPlatform LocalPlatform => Platform(CurrentHost);
    private static ServerPlatform Platform(ServerHost host) =>
        host == ServerHost.Windows ? ServerPlatform.Windows : host == ServerHost.MacOS ? ServerPlatform.MacOS : ServerPlatform.Linux;
    // A universal macOS server starts as its parent's architecture unless the slice is named; the machine's own is native
    // (arm64 on Apple Silicon, also when this runner itself runs under Rosetta).
    internal static ClientArchitecture MacArchitecture =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? ClientArchitecture.Arm64 : ClientArchitecture.X64;

    /// <summary>
    /// Decides the platform from the runtime's contents, never from the host: <see cref="WindowsExecutable"/>,
    /// <see cref="LinuxExecutable"/>, or <see cref="MacExecutable"/> with its <c>Data</c> folder. Refuses an ambiguous or
    /// empty runtime, and a macOS game client (a Valheim.app bundle, or a directory holding one) with
    /// <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public static ServerPlatform Detect(string runtimeDirectory)
    {
        string runtime = FullRuntime(runtimeDirectory);
        bool windows = File.Exists(Path.Combine(runtime, WindowsExecutable)), linux = File.Exists(Path.Combine(runtime, LinuxExecutable));
        bool mac = File.Exists(Path.Combine(runtime, MacExecutablePath)) && Directory.Exists(Path.Combine(runtime, MacDataPath));
        if ((windows ? 1 : 0) + (linux ? 1 : 0) + (mac ? 1 : 0) > 1)
            throw new InvalidOperationException($"Runtime contains more than one of {WindowsExecutable}, {LinuxExecutable} and {MacExecutable}; refusing to guess its platform.");
        if (windows) return ServerPlatform.Windows;
        if (linux) return ServerPlatform.Linux;
        if (mac) return ServerPlatform.MacOS;
        if (IsMacClient(runtime))
            throw new PlatformNotSupportedException($"{runtime} is the macOS Valheim game client ({MacClientBundle}), not a dedicated server; ClientLaunch builds its launch. " + MacServerHint);
        string? client = new[] { ClientLaunch.WindowsExecutable, ClientLaunch.LinuxExecutable }.FirstOrDefault(name => File.Exists(Path.Combine(runtime, name)));
        throw new FileNotFoundException($"Runtime contains none of {WindowsExecutable}, {LinuxExecutable} or {MacExecutable} (with {MacData})." +
            (client == null ? "" : $" It holds the game client {client}; launch it with ClientLaunch."), runtime);
    }

    /// <summary>
    /// Returns the detected server executable's full path. A server runs only on its own OS (a Windows host may resolve a
    /// Linux server for inspection); another host refuses with <see cref="PlatformNotSupportedException"/>. On Linux and
    /// macOS the server must carry the user-execute bit.
    /// </summary>
    public static string RequireExecutable(string runtimeDirectory) => RequireExecutable(runtimeDirectory, CurrentHost);
    internal static string RequireExecutable(string runtimeDirectory, ServerHost host) => Resolve(FullRuntime(runtimeDirectory), host).Executable;

    /// <summary>
    /// Start info for one owned BepInEx dedicated server. BepInEx's preloader and core and the platform's Doorstop loader
    /// must be present; on Windows doorstop_config.ini must enable Doorstop and target BepInEx's preloader. On macOS the
    /// server starts through <c>/usr/bin/arch</c> as the machine's own architecture (arm64 on Apple Silicon), with Doorstop
    /// enabled for BepInEx's preloader and a Doorstop library at the runtime's root that has that slice inserted
    /// (<c>DYLD_INSERT_LIBRARIES</c>, passed with <c>-e</c> because the kernel strips DYLD_* from the SIP-protected
    /// <c>arch</c>); the stock BepInExPack's Doorstop and core are x86_64-only, so a native launch needs a universal
    /// <c>libdoorstop.dylib</c> and a BepInEx core that runs natively. Caller
    /// environment is applied first and may not set Doorstop's variables or pass <c>--doorstop-*</c> arguments; inherited
    /// Doorstop variables are removed. On Linux, Doorstop is enabled for BepInEx's preloader and the runtime's
    /// doorstop_libs/linux64 directories are prepended to any existing LD_LIBRARY_PATH/LD_PRELOAD, which are kept.
    /// SteamAppId defaults to the dedicated server's unless the caller sets it.
    /// Unlike <see cref="ClientLaunch"/>, a host may build another platform's launch: a Windows host builds a Linux launch
    /// for inspection only. Any other mismatch refuses with <see cref="PlatformNotSupportedException"/> rather than executing
    /// another OS's binary.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string runtimeDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null) =>
        CreateStartInfo(runtimeDirectory, arguments, environment, CurrentHost, MacArchitecture);

    internal static ProcessStartInfo CreateStartInfo(string runtimeDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment, ServerHost host) =>
        CreateStartInfo(runtimeDirectory, arguments, environment, host, MacArchitecture);

    internal static ProcessStartInfo CreateStartInfo(string runtimeDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment, ServerHost host,
        ClientArchitecture macArchitecture)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string runtime = FullRuntime(runtimeDirectory);
        var (platform, executable) = Resolve(runtime, host);
        BepInExLoader.RequireCore(runtime, "runtime");
        string? macDoorstop = null;
        if (platform == ServerPlatform.Windows) BepInExLoader.RequireWindowsLoader(runtime, "runtime");
        else if (platform == ServerPlatform.Linux) BepInExLoader.RequireFile(runtime, BepInExLoader.LinuxLibrary, "BepInEx's Doorstop loader is missing from the runtime");
        else
        {
            macDoorstop = ClientLaunch.MacDoorstop(runtime, executable, macArchitecture, client: false);
            // DYLD_INSERT_LIBRARIES splits on ':', so such a path cannot be listed.
            if (runtime.Contains(':')) throw new ArgumentException("A macOS runtime path cannot contain ':'.", nameof(runtimeDirectory));
        }
        environment ??= new Dictionary<string, string>();
        var names = host == ServerHost.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var passed = BepInExLoader.RefuseOverrides(environment, arguments, names, nameof(ServerLaunch));

        // On Windows the server gets a console of its own (with no window): a clean stop sends Ctrl+C to that console
        // (QuitRequest.Interrupt), and a Ctrl+C in the runner's console no longer reaches the server.
        var start = new ProcessStartInfo(executable) { WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = platform == ServerPlatform.Windows };
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
        else if (platform == ServerPlatform.MacOS)
        {
            start.Environment["DOORSTOP_ENABLED"] = "1";
            start.Environment["DOORSTOP_TARGET_ASSEMBLY"] = Path.Combine(runtime, BepInExLoader.Preloader);
            // With Doorstop injected, Mono finds libmono-native.dylib only through the library path, and the server keeps it
            // beside itself, not at the root (the Mac client's launcher value): without it, or with the root, the server never
            // started (30 Sep 2026, build 25527701). Vanilla, without Doorstop, needs neither.
            start.Environment["DYLD_LIBRARY_PATH"] = BepInExLoader.Prepend(start.Environment, "DYLD_LIBRARY_PATH", Path.GetDirectoryName(executable)!);
            start.Environment["DYLD_INSERT_LIBRARIES"] = BepInExLoader.Prepend(start.Environment, "DYLD_INSERT_LIBRARIES", macDoorstop!);
            start.FileName = ClientLaunch.MacArchLauncher;
            start.ArgumentList.Clear();
            start.ArgumentList.Add(macArchitecture == ClientArchitecture.Arm64 ? "-arm64" : "-x86_64");
            foreach (string name in start.Environment.Keys.Where(key => key.StartsWith("DYLD_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList())
            {
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add(name + "=" + start.Environment[name]);
                start.Environment.Remove(name);
            }
            start.ArgumentList.Add(executable);
            foreach (string argument in passed) start.ArgumentList.Add(argument);
        }
        return start;
    }

    private static (ServerPlatform Platform, string Executable) Resolve(string runtime, ServerHost host)
    {
        var platform = Detect(runtime);
        string executable = Path.Combine(runtime, platform switch { ServerPlatform.Windows => WindowsExecutable, ServerPlatform.Linux => LinuxExecutable, _ => MacExecutablePath });
        // Refused before any file mode is read: exec of a Mach-O elsewhere, or of an ELF or PE binary on macOS, fails with an
        // opaque error. Windows and Linux hosts build each other's launch, as before (a Windows host a Linux one for inspection).
        var own = Platform(host);
        if (platform != own && (platform == ServerPlatform.MacOS || own == ServerPlatform.MacOS))
            throw new PlatformNotSupportedException($"This {own} host cannot run the {platform} dedicated server {executable}. " + MacServerHint);
        // Windows has no execute bit to read; there a Linux runtime can only be staged or inspected.
        // The outer check is the real OS (Unix modes exist); the inner one is the host this launch is built for.
        if (!OperatingSystem.IsWindows())
        {
            if (host != ServerHost.Windows && platform != ServerPlatform.Windows && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
                throw new InvalidOperationException($"{Path.GetRelativePath(runtime, executable)} is not executable; restore its mode (chmod u+x) in the runtime copy.");
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
