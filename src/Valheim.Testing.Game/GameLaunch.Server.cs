using System.Runtime.InteropServices;

namespace Valheim.Testing.Game;

public enum ServerPlatform { Windows, Linux, MacOS }

// The machine building the launch. Each dedicated server runs only on its own OS (a Windows host may build a Linux launch
// for inspection). Other Unix hosts behave as Linux. Injectable so every host's branches are tested on any OS.
internal enum ServerHost { Windows, Linux, MacOS }

// What a dedicated-server runtime is: its platform, decided from its files, and its executable. ForServer builds the launch
// from it. Arguments (-batchmode, -nographics, -savedir ...) remain the caller's plan.
public sealed partial class GameLaunch
{
    public const string ServerWindowsExecutable = "valheim_server.exe";
    public const string ServerLinuxExecutable = "valheim_server.x86_64";
    /// <summary>
    /// The macOS dedicated server (Steam app 896660's macOS depot), relative to the runtime's root: a universal Mach-O
    /// (x86_64 and arm64) beside Unity's player and Mono libraries, with its <c>Data</c> folder next to it. Not an app bundle.
    /// </summary>
    public const string ServerMacExecutable = "valheim_server/Valheim";
    private const string MacServerData = "valheim_server/Data";
    // The same two paths with this OS's separator, for full paths built from a runtime directory.
    private static readonly string MacServerExecutablePath = Path.Combine("valheim_server", "Valheim"), MacServerDataPath = Path.Combine("valheim_server", "Data");
    /// <summary>The game's Steam app id. Both the client and the dedicated server read it from <c>SteamAppId</c>.</summary>
    public const string SteamAppId = "892970";
    private const string MacServerHint = "On a Mac, run the macOS dedicated server (Steam app 896660 installed on macOS, " + ServerMacExecutable + "); " +
        "the Windows and Linux servers need their own OS: the Linux server image in a container (docker --platform linux/amd64, see docker/linux-server) or a remote Windows/Linux host.";

    internal static ServerHost CurrentServerHost =>
        OperatingSystem.IsWindows() ? ServerHost.Windows : OperatingSystem.IsMacOS() ? ServerHost.MacOS : ServerHost.Linux;
    /// <summary>The dedicated-server platform this machine runs: Windows, macOS, or Linux for any other Unix.</summary>
    public static ServerPlatform LocalServerPlatform => ServerPlatformOn(CurrentServerHost);
    private static ServerPlatform ServerPlatformOn(ServerHost host) =>
        host == ServerHost.Windows ? ServerPlatform.Windows : host == ServerHost.MacOS ? ServerPlatform.MacOS : ServerPlatform.Linux;
    // A universal macOS server starts as its parent's architecture unless the slice is named; the machine's own is native
    // (arm64 on Apple Silicon, also when this runner itself runs under Rosetta).
    internal static ClientArchitecture MacServerArchitecture =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? ClientArchitecture.Arm64 : ClientArchitecture.X64;

    /// <summary>
    /// Decides the platform from the runtime's contents, never from the host: <see cref="ServerWindowsExecutable"/>,
    /// <see cref="ServerLinuxExecutable"/>, or <see cref="ServerMacExecutable"/> with its <c>Data</c> folder. Refuses an ambiguous or
    /// empty runtime, and a macOS game client (a Valheim.app bundle, or a directory holding one) with
    /// <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public static ServerPlatform DetectServer(string runtimeDirectory)
    {
        string runtime = FullRuntime(runtimeDirectory);
        bool windows = File.Exists(Path.Combine(runtime, ServerWindowsExecutable)), linux = File.Exists(Path.Combine(runtime, ServerLinuxExecutable));
        bool mac = File.Exists(Path.Combine(runtime, MacServerExecutablePath)) && Directory.Exists(Path.Combine(runtime, MacServerDataPath));
        if ((windows ? 1 : 0) + (linux ? 1 : 0) + (mac ? 1 : 0) > 1)
            throw new InvalidOperationException($"Runtime contains more than one of {ServerWindowsExecutable}, {ServerLinuxExecutable} and {ServerMacExecutable}; refusing to guess its platform.");
        if (windows) return ServerPlatform.Windows;
        if (linux) return ServerPlatform.Linux;
        if (mac) return ServerPlatform.MacOS;
        if (IsMacClient(runtime))
            throw new PlatformNotSupportedException($"{runtime} is the macOS Valheim game client ({ClientMacBundle}), not a dedicated server; GameLaunch.ForClient builds its launch. " + MacServerHint);
        string? client = new[] { ClientWindowsExecutable, ClientLinuxExecutable }.FirstOrDefault(name => File.Exists(Path.Combine(runtime, name)));
        throw new FileNotFoundException($"Runtime contains none of {ServerWindowsExecutable}, {ServerLinuxExecutable} or {ServerMacExecutable} (with {MacServerData})." +
            (client == null ? "" : $" It holds the game client {client}; launch it with GameLaunch.ForClient."), runtime);
    }

    /// <summary>
    /// Returns the detected server executable's full path. A server runs only on its own OS (a Windows host may resolve a
    /// Linux server for inspection); another host refuses with <see cref="PlatformNotSupportedException"/>. On Linux and
    /// macOS the server must carry the user-execute bit.
    /// </summary>
    public static string RequireServerExecutable(string runtimeDirectory) => RequireServerExecutable(runtimeDirectory, CurrentServerHost);
    internal static string RequireServerExecutable(string runtimeDirectory, ServerHost host) => ResolveServer(FullRuntime(runtimeDirectory), host).Executable;

    internal static (ServerPlatform Platform, string Executable) ResolveServer(string runtime, ServerHost host)
    {
        var platform = DetectServer(runtime);
        string executable = Path.Combine(runtime, platform switch { ServerPlatform.Windows => ServerWindowsExecutable, ServerPlatform.Linux => ServerLinuxExecutable, _ => MacServerExecutablePath });
        // Refused before any file mode is read: exec of a Mach-O elsewhere, or of an ELF or PE binary on macOS, fails with an
        // opaque error. Windows and Linux hosts build each other's launch, as before (a Windows host a Linux one for inspection).
        var own = ServerPlatformOn(host);
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
        string.Equals(Path.GetFileName(runtime), ClientMacBundle, StringComparison.OrdinalIgnoreCase)
        || File.Exists(Path.Combine(runtime, MacClientExecutable))
        || Directory.Exists(Path.Combine(runtime, ClientMacBundle));
    internal static string FullRuntime(string runtimeDirectory) => FullDirectory(runtimeDirectory, "Server runtime");
    // The full path without a trailing separator, of a directory that must exist (kind names it in the message).
    private static string FullDirectory(string directory, string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(kind + " directory does not exist: " + full);
        return full;
    }
}
