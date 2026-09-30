using System.Buffers.Binary;
using System.Diagnostics;

namespace Valheim.Testing.Game;

// Also the host type: a client runs only on its own OS, so a host is the platform it can launch.
// Other Unix hosts behave as Linux. The host is injectable so every host's branches are tested on any OS.
public enum ClientPlatform { Windows, Linux, MacOS }

// Windows and Linux clients are x64 only. The macOS client is universal, so its slice is chosen at launch
// and the Doorstop library inserted into it must contain the same one.
public enum ClientArchitecture { X64, Arm64 }

// Builds the direct launch of one BepInEx game client from its install directory: the client twin of ServerLaunch.
// It reproduces what BepInExPack_Valheim's start_game_bepinex.sh exports on Linux and macOS, without the script,
// so the started PID is the game's own. On Windows the pack's winhttp.dll proxy loads BepInEx and no variable is needed.
// This only builds the ProcessStartInfo. A client needs an interactive desktop session with a display, a GPU and a
// running, signed-in Steam client; one started from a service or an SSH session usually has no display and fails
// or never shows a window. Starting it inside the user's session is a host adapter's job, not this class's.
public static class ClientLaunch
{
    public const string WindowsExecutable = "valheim.exe";
    public const string LinuxExecutable = "valheim.x86_64";
    public const string MacBundle = "Valheim.app";
    public const string GameSteamAppId = "892970";
    public const string ConsoleArgument = "-console";
    // A SIP-protected system binary: the kernel strips DYLD_* from its environment, so they are passed with -e.
    // It execs the game in place, so the started PID is still the game's.
    public const string MacArchLauncher = "/usr/bin/arch";
    private static readonly string MacExecutable = Path.Combine("Contents", "MacOS", "Valheim");
    // The pack's x64 library first (its supported route, under Rosetta on Apple Silicon), then the universal
    // libdoorstop.dylib of BepInEx's macOS build, the only one that can hold an arm64 slice.
    private static readonly string[] MacDoorstops = [Path.Combine("doorstop_libs", "libdoorstop_x64.dylib"), "libdoorstop.dylib"];

    internal static ClientPlatform CurrentHost =>
        OperatingSystem.IsWindows() ? ClientPlatform.Windows : OperatingSystem.IsMacOS() ? ClientPlatform.MacOS : ClientPlatform.Linux;

    /// <summary>
    /// Decides the platform from the install's contents, never from the host: <c>valheim.exe</c>, <c>valheim.x86_64</c>
    /// or a <c>Valheim.app</c> bundle. Refuses an install holding more than one, an empty one, the bundle itself instead
    /// of the directory holding it, and a dedicated-server runtime (which <see cref="ServerLaunch"/> launches).
    /// </summary>
    public static ClientPlatform Detect(string installDirectory)
    {
        string install = FullInstall(installDirectory);
        bool windows = File.Exists(Path.Combine(install, WindowsExecutable)), linux = File.Exists(Path.Combine(install, LinuxExecutable));
        bool mac = FindMacBundle(install) != null;
        if ((windows ? 1 : 0) + (linux ? 1 : 0) + (mac ? 1 : 0) > 1)
            throw new InvalidOperationException($"Install contains more than one of {WindowsExecutable}, {LinuxExecutable} and {MacBundle}; refusing to guess its platform.");
        if (windows) return ClientPlatform.Windows;
        if (linux) return ClientPlatform.Linux;
        if (mac) return ClientPlatform.MacOS;
        if (Path.GetExtension(install).Equals(".app", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(install, MacExecutable)))
            throw new ArgumentException($"{install} is the {MacBundle} bundle itself; pass the directory that holds it and BepInEx.", nameof(installDirectory));
        string? server = new[] { ServerLaunch.WindowsExecutable, ServerLaunch.LinuxExecutable, ServerLaunch.MacExecutable }.FirstOrDefault(name => File.Exists(Path.Combine(install, name)));
        if (server != null)
            throw new InvalidOperationException($"{install} is a dedicated-server runtime ({server}), not a game client; launch it with ServerLaunch.");
        throw new FileNotFoundException($"Install contains none of {WindowsExecutable}, {LinuxExecutable} or {MacBundle}.", install);
    }

    /// <summary>
    /// Returns the detected client executable's full path (inside the bundle on macOS). A client runs only on its own OS:
    /// another host refuses with <see cref="PlatformNotSupportedException"/>. On Linux and macOS it must carry the user-execute bit.
    /// </summary>
    public static string RequireExecutable(string installDirectory) => RequireExecutable(installDirectory, CurrentHost);
    internal static string RequireExecutable(string installDirectory, ClientPlatform host) => Resolve(FullInstall(installDirectory), host).Executable;

    /// <summary>
    /// The architectures this install can be launched as, host aside: x64 for Windows and Linux; on macOS, the slices
    /// both the game executable and one of its Doorstop libraries contain. Empty when no Doorstop library matches.
    /// </summary>
    public static IReadOnlyList<ClientArchitecture> LaunchArchitectures(string installDirectory)
    {
        string install = FullInstall(installDirectory);
        if (Detect(install) != ClientPlatform.MacOS) return [ClientArchitecture.X64];
        var game = MachOArchitectures(Path.Combine(FindMacBundle(install)!, MacExecutable));
        var doorstops = MacDoorstops.Select(relative => Path.Combine(install, relative)).Where(File.Exists).SelectMany(MachOArchitectures).ToHashSet();
        return Enum.GetValues<ClientArchitecture>().Where(architecture => game.Contains(architecture) && doorstops.Contains(architecture)).ToList();
    }

    /// <summary>
    /// Start info for one BepInEx game client. BepInEx's preloader and the platform's Doorstop loader must be present; on
    /// Windows doorstop_config.ini must enable Doorstop and target BepInEx's preloader. <c>-console</c> is added first unless
    /// <paramref name="console"/> is false or the caller passed it; other arguments follow unchanged. Caller environment is
    /// applied first and may not set Doorstop's variables or pass <c>--doorstop-*</c> arguments. SteamAppId defaults to the
    /// game's unless the caller sets it. Linux enables Doorstop and prepends doorstop_libs to LD_LIBRARY_PATH and the
    /// library to LD_PRELOAD. macOS enables Doorstop and starts the bundle through <c>/usr/bin/arch</c> as the requested
    /// <paramref name="architecture"/>, inserting a Doorstop library that has that slice. The host must be the client's own OS.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string installDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null,
        ClientArchitecture architecture = ClientArchitecture.X64, bool console = true) =>
        CreateStartInfo(installDirectory, arguments, environment, architecture, console, CurrentHost);

    internal static ProcessStartInfo CreateStartInfo(string installDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment,
        ClientArchitecture architecture, bool console, ClientPlatform host)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string install = FullInstall(installDirectory);
        var (platform, executable) = Resolve(install, host);
        if (platform != ClientPlatform.MacOS && architecture != ClientArchitecture.X64)
            throw new ArgumentException($"The {platform} client is x64 only; {architecture} exists for the macOS client alone.", nameof(architecture));
        BepInExLoader.RequireCore(install, "install");
        string? macDoorstop = null;
        if (platform == ClientPlatform.Windows) BepInExLoader.RequireWindowsLoader(install, "install");
        else if (platform == ClientPlatform.Linux) BepInExLoader.RequireFile(install, BepInExLoader.LinuxLibrary, "BepInEx's Doorstop loader is missing from the install");
        else macDoorstop = MacDoorstop(install, executable, architecture);

        environment ??= new Dictionary<string, string>();
        var names = host == ClientPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var passed = BepInExLoader.RefuseOverrides(environment, arguments, names, nameof(ClientLaunch));
        if (console && !passed.Contains(ConsoleArgument, StringComparer.OrdinalIgnoreCase)) passed.Insert(0, ConsoleArgument);
        // Search lists split on ':' (LD_LIBRARY_PATH also on ';'), so such a path cannot be represented. Checked only
        // where the launch can run; a Windows machine builds these launches only when a test injects the host.
        if (platform != ClientPlatform.Windows && !OperatingSystem.IsWindows()
            && install.IndexOfAny(platform == ClientPlatform.Linux ? [':', ';'] : [':']) >= 0)
            throw new ArgumentException($"A {platform} install path cannot contain ':'" + (platform == ClientPlatform.Linux ? " or ';'." : "."), nameof(installDirectory));

        var start = new ProcessStartInfo(executable) { WorkingDirectory = install, UseShellExecute = false };
        // Inherited Doorstop values would reach the game too; only the ones set below may.
        BepInExLoader.ApplyEnvironment(start, environment);
        if (!environment.Keys.Any(key => names.Equals(key, "SteamAppId"))) start.Environment["SteamAppId"] = GameSteamAppId;
        if (platform != ClientPlatform.Windows)
        {
            start.Environment["DOORSTOP_ENABLED"] = "1";
            start.Environment["DOORSTOP_TARGET_ASSEMBLY"] = Path.Combine(install, BepInExLoader.Preloader);
        }
        if (platform == ClientPlatform.Linux)
        {
            // Same effective order as the pack's script: doorstop_libs, then the existing value.
            start.Environment["LD_LIBRARY_PATH"] = BepInExLoader.Prepend(start.Environment, "LD_LIBRARY_PATH", Path.Combine(install, "doorstop_libs"));
            start.Environment["LD_PRELOAD"] = BepInExLoader.Prepend(start.Environment, "LD_PRELOAD", "libdoorstop_x64.so");
        }
        else if (platform == ClientPlatform.MacOS)
        {
            // Named by full path, so the script's DYLD_LIBRARY_PATH entry is not needed. An explicit slice, because a
            // universal game otherwise starts as the parent's architecture, which the library may lack.
            start.Environment["DYLD_INSERT_LIBRARIES"] = BepInExLoader.Prepend(start.Environment, "DYLD_INSERT_LIBRARIES", macDoorstop!);
            start.FileName = MacArchLauncher;
            start.ArgumentList.Add(architecture == ClientArchitecture.Arm64 ? "-arm64" : "-x86_64");
            foreach (string name in start.Environment.Keys.Where(key => key.StartsWith("DYLD_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList())
            {
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add(name + "=" + start.Environment[name]);
                start.Environment.Remove(name);
            }
            start.ArgumentList.Add(executable);
        }
        foreach (string argument in passed) start.ArgumentList.Add(argument);
        return start;
    }

    private static (ClientPlatform Platform, string Executable) Resolve(string install, ClientPlatform host)
    {
        var platform = Detect(install);
        string executable = platform switch
        {
            ClientPlatform.Windows => Path.Combine(install, WindowsExecutable),
            ClientPlatform.Linux => Path.Combine(install, LinuxExecutable),
            _ => Path.Combine(FindMacBundle(install)!, MacExecutable),
        };
        // Refused before any file mode is read: exec of another OS's binary fails with an opaque error.
        if (platform != host)
            throw new PlatformNotSupportedException($"This {host} host cannot run the {platform} Valheim client {executable}; " +
                $"start it on a {platform} machine. ClientLaunch does not support Wine, Proton or other cross-OS launches.");
        if (!File.Exists(executable)) throw new FileNotFoundException($"{MacBundle} has no executable: {MacExecutable}", executable);
        // The outer check is the real OS (Unix modes exist); the inner one is the host this launch is built for.
        if (!OperatingSystem.IsWindows())
        {
            if (host != ClientPlatform.Windows && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
                throw new InvalidOperationException($"{executable} is not executable; restore its mode (chmod u+x) in the install.");
        }
        return (platform, executable);
    }

    // The first Doorstop library with the requested slice. dyld cannot insert a library into a process of another architecture.
    // Shared with ServerLaunch for the macOS dedicated server, whose Doorstop library sits at the runtime's root the same way.
    internal static string MacDoorstop(string install, string executable, ClientArchitecture architecture)
    {
        var game = MachOArchitectures(executable);
        if (!game.Contains(architecture))
            throw new InvalidOperationException($"{executable} has no {SliceName(architecture)} slice (found: {SliceList(game)}).");
        var found = MacDoorstops.Select(relative => Path.Combine(install, relative)).Where(File.Exists).ToList();
        if (found.Count == 0)
            throw new FileNotFoundException("BepInEx's Doorstop loader is missing from the install: " + string.Join(" or ", MacDoorstops), Path.Combine(install, MacDoorstops[0]));
        string? match = found.FirstOrDefault(path => MachOArchitectures(path).Contains(architecture));
        if (match != null) return match;
        string slices = string.Join("; ", found.Select(path => Path.GetRelativePath(install, path) + ": " + SliceList(MachOArchitectures(path))));
        string hint = architecture == ClientArchitecture.Arm64
            ? "A native arm64 launch needs the universal libdoorstop.dylib of BepInEx's macOS build and a BepInEx core that runs natively; otherwise launch as X64 under Rosetta."
            : "An x86_64 launch needs BepInExPack_Valheim's doorstop_libs/libdoorstop_x64.dylib or a universal libdoorstop.dylib, and Rosetta on Apple Silicon.";
        throw new InvalidOperationException($"No Doorstop library in the install has an {SliceName(architecture)} slice ({slices}). " + hint);
    }

    // The CPU slices of a thin 64-bit or universal (fat) Mach-O file. Any other file has none.
    internal static IReadOnlySet<ClientArchitecture> MachOArchitectures(string path)
    {
        const uint Fat = 0xCAFEBABE, Fat64 = 0xCAFEBABF, Thin64 = 0xFEEDFACF;
        var found = new HashSet<ClientArchitecture>();
        using var file = File.OpenRead(path);
        var header = new byte[8];
        if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return found;
        var cpus = new List<int>();
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is Fat or Fat64)
        {
            // Universal headers are big-endian. A large count is not a universal binary (Java class files share the magic).
            uint count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            int size = magic == Fat ? 20 : 32;
            if (count is 0 or > 16) return found;
            var entries = new byte[count * size];
            if (file.ReadAtLeast(entries, entries.Length, throwOnEndOfStream: false) < entries.Length) return found;
            for (int i = 0; i < count; i++) cpus.Add(BinaryPrimitives.ReadInt32BigEndian(entries.AsSpan(i * size)));
        }
        else if (BinaryPrimitives.ReadUInt32LittleEndian(header) == Thin64) cpus.Add(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)));
        foreach (int cpu in cpus)
        {
            if (cpu == 0x01000007) found.Add(ClientArchitecture.X64); // CPU_TYPE_X86_64
            else if (cpu == 0x0100000C) found.Add(ClientArchitecture.Arm64); // CPU_TYPE_ARM64
        }
        return found;
    }

    private static string SliceName(ClientArchitecture architecture) => architecture == ClientArchitecture.Arm64 ? "arm64" : "x86_64";
    private static string SliceList(IReadOnlySet<ClientArchitecture> slices) =>
        slices.Count == 0 ? "no x86_64 or arm64 Mach-O slice" : string.Join(", ", slices.Order().Select(SliceName));
    // Steam installs name it Valheim.app; a case-insensitive match also finds it on a case-sensitive file system.
    private static string? FindMacBundle(string install) =>
        Directory.EnumerateDirectories(install).FirstOrDefault(path => Path.GetFileName(path).Equals(MacBundle, StringComparison.OrdinalIgnoreCase));
    private static string FullInstall(string installDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(installDirectory);
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        if (!Directory.Exists(install)) throw new DirectoryNotFoundException("Client install directory does not exist: " + install);
        return install;
    }
}
