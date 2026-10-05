using System.Runtime.InteropServices;

namespace Valheim.Testing.Game;

public enum ServerPlatform { Windows, Linux, MacOS }

// The machine building the launch. Each dedicated server runs only on its own OS (a Windows host may build a Linux launch
// for inspection). Other Unix hosts behave as Linux. Injectable so every host's branches are tested on any OS.
internal enum ServerHost { Windows, Linux, MacOS }

// What a dedicated-server runtime is: its platform, decided from its files, and its executable. GameLaunch.ForServer builds
// the launch from it. Arguments (-batchmode, -nographics, -savedir ...) remain the caller's plan.
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
            throw new PlatformNotSupportedException($"{runtime} is the macOS Valheim game client ({MacClientBundle}), not a dedicated server; GameLaunch.ForClient builds its launch. " + MacServerHint);
        string? client = new[] { ClientLaunch.WindowsExecutable, ClientLaunch.LinuxExecutable }.FirstOrDefault(name => File.Exists(Path.Combine(runtime, name)));
        throw new FileNotFoundException($"Runtime contains none of {WindowsExecutable}, {LinuxExecutable} or {MacExecutable} (with {MacData})." +
            (client == null ? "" : $" It holds the game client {client}; launch it with GameLaunch.ForClient."), runtime);
    }

    /// <summary>
    /// Returns the detected server executable's full path. A server runs only on its own OS (a Windows host may resolve a
    /// Linux server for inspection); another host refuses with <see cref="PlatformNotSupportedException"/>. On Linux and
    /// macOS the server must carry the user-execute bit.
    /// </summary>
    public static string RequireExecutable(string runtimeDirectory) => RequireExecutable(runtimeDirectory, CurrentHost);
    internal static string RequireExecutable(string runtimeDirectory, ServerHost host) => Resolve(FullRuntime(runtimeDirectory), host).Executable;

    internal static (ServerPlatform Platform, string Executable) Resolve(string runtime, ServerHost host)
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
    internal static string FullRuntime(string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeDirectory);
        string runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory));
        if (!Directory.Exists(runtime)) throw new DirectoryNotFoundException("Server runtime directory does not exist: " + runtime);
        return runtime;
    }
}
