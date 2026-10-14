using System.Buffers.Binary;
using System.Reflection;

namespace Valheim.Testing.Game;

// Also the host type: a client runs only on its own OS, so a host is the platform it can launch. Linux is a host type for
// the dedicated server alone; a client on it is refused (RequireClientHost). Other Unix hosts behave as Linux. The host is
// injectable so every host's branches are tested on any OS.
[ResultShape]
public enum ClientPlatform { Windows, Linux, MacOS }

// The Windows client is x64 only. The macOS client is universal, so its slice is chosen at launch and the Doorstop
// library inserted into it must contain the same one. X64 runs under Rosetta on Apple Silicon, with BepInExPack_Valheim's own
// loader and core: the compatibility path. Arm64 runs natively, with a Doorstop library that has an arm64 slice and a
// BepInEx core whose MonoMod can hook on arm64. Both are modded paths; Apple Silicon defaults to arm64.
[ResultShape]
public enum ClientArchitecture { X64, Arm64 }

// What a game-client install is: its platform, decided from its contents, its executable and the architectures it can launch
// as. ForClient builds the launch from it.
public sealed partial class GameLaunch
{
    public const string ClientWindowsExecutable = "valheim.exe";
    // Only to recognise a Linux client install and refuse it; no Linux client is launched.
    internal const string ClientLinuxExecutable = "valheim.x86_64";
    /// <summary>The one refusal of a Linux game client, whichever way it is asked for.</summary>
    internal const string LinuxClientRefusal = "Linux hosts run the dedicated server only; start the client on Windows or macOS.";
    public const string ClientMacBundle = "Valheim.app";
    public const string ConsoleArgument = "-console";
    internal const string LogFileArgument = "-logFile";
    /// <summary>
    /// The one name of the toolkit's Unity log (#822). An owned client launch passes <c>-logFile</c> with it in the run's own launch
    /// folder (<see cref="UnityLog"/>), never the install, so the game neither writes the user's own <c>Player.log</c> nor leaves a
    /// file in an install run in place. A dedicated server's plan names it in its runtime (<c>-logFile {runtime}/toolkit-unity.log</c>),
    /// and recovery keeps it from a runtime by this name.
    /// </summary>
    internal const string UnityLogName = "toolkit-unity.log";
    // A SIP-protected system binary: the kernel strips DYLD_* from its environment, so they are passed with -e.
    // It execs the game in place, so the started PID is still the game's.
    public const string MacArchLauncher = "/usr/bin/arch";
    private static readonly string MacClientExecutable = Path.Combine("Contents", "MacOS", "Valheim");
    // The pack's x64 library first (its route, under Rosetta on Apple Silicon), then libdoorstop.dylib at the install's
    // root, where a native install puts UnityDoorstop 4.5 or later: universal or arm64-only, what matters is its arm64 slice.
    // The Doorstop libraries a macOS install may load BepInEx through, relative with '/': BepInExPack's x64 one or a root one.
    internal static readonly string[] MacDoorstopFiles = ["doorstop_libs/libdoorstop_x64.dylib", "libdoorstop.dylib"];
    // Legacy MonoMod (before 25), which BepInExPack_Valheim's core uses, cannot apply detours on arm64: Apple Silicon keeps JIT
    // pages writable or executable, never both. Its reorganised releases (25 and later) can, so a native core is built on them.
    // Relative with '/', as a macOS host names it; refusals name it this way whichever machine runs the check.
    internal const string MacNativeDetour = "BepInEx/core/MonoMod.RuntimeDetour.dll";
    internal const int MacNativeDetourMajor = 25;

    /// <summary>The one place a Linux client is refused: Linux hosts run the dedicated server only.</summary>
    internal static void RequireClientHost(ClientPlatform host)
    {
        if (host == ClientPlatform.Linux) throw new PlatformNotSupportedException(LinuxClientRefusal);
    }

    internal static ClientPlatform CurrentClientHost =>
        OperatingSystem.IsWindows() ? ClientPlatform.Windows : OperatingSystem.IsMacOS() ? ClientPlatform.MacOS : ClientPlatform.Linux;

    /// <summary>
    /// Decides the platform from the install's contents, never from the host: <c>valheim.exe</c> or a <c>Valheim.app</c> bundle.
    /// Refuses an install holding more than one, an empty one, the bundle itself instead of the directory holding it, a
    /// dedicated-server runtime (which <see cref="GameLaunch.ForServer"/> launches) and a Linux client (<c>valheim.x86_64</c>) with
    /// <see cref="PlatformNotSupportedException"/>: Linux hosts run the dedicated server only.
    /// </summary>
    public static ClientPlatform DetectClient(string installDirectory)
    {
        string install = FullInstall(installDirectory);
        bool windows = File.Exists(Path.Combine(install, ClientWindowsExecutable)), linux = File.Exists(Path.Combine(install, ClientLinuxExecutable));
        bool mac = FindMacBundle(install) != null;
        if ((windows ? 1 : 0) + (linux ? 1 : 0) + (mac ? 1 : 0) > 1)
            throw new InvalidOperationException($"Install contains more than one of {ClientWindowsExecutable}, {ClientLinuxExecutable} and {ClientMacBundle}; refusing to guess its platform.");
        if (linux) RequireClientHost(ClientPlatform.Linux);
        if (windows) return ClientPlatform.Windows;
        if (mac) return ClientPlatform.MacOS;
        if (Path.GetExtension(install).Equals(".app", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(install, MacClientExecutable)))
            throw new ArgumentException($"{install} is the {ClientMacBundle} bundle itself; pass the directory that holds it and BepInEx.", nameof(installDirectory));
        string? server = new[] { ServerWindowsExecutable, ServerLinuxExecutable, ServerMacExecutable }.FirstOrDefault(name => File.Exists(Path.Combine(install, name)));
        if (server != null)
            throw new InvalidOperationException($"{install} is a dedicated-server runtime ({server}), not a game client; launch it with GameLaunch.ForServer.");
        throw new FileNotFoundException($"Install contains none of {ClientWindowsExecutable} or {ClientMacBundle}.", install);
    }

    /// <summary>
    /// Returns the detected client executable's full path (inside the bundle on macOS). A client runs only on its own OS:
    /// another host refuses with <see cref="PlatformNotSupportedException"/>, a Linux one too (Linux hosts run the dedicated server only). On macOS it must carry the user-execute bit.
    /// </summary>
    public static string RequireClientExecutable(string installDirectory) => RequireClientExecutable(installDirectory, CurrentClientHost);
    internal static string RequireClientExecutable(string installDirectory, ClientPlatform host) => ResolveClient(FullInstall(installDirectory), host).Executable;

    /// <summary>
    /// The architectures this install can be launched as, host aside: x64 for Windows; on macOS, the slices
    /// both the game executable and one of the install's Doorstop libraries contain, arm64 only when that core also runs
    /// natively (<see cref="GameLaunch.ForClient"/> explains the rule). Empty when no Doorstop library matches.
    /// </summary>
    public static IReadOnlyList<ClientArchitecture> ClientLaunchArchitectures(string installDirectory) => ClientLaunchArchitectures(installDirectory, null);

    // A one-shot run may select a loader package that replaces the install's own loader in its disposable copy.
    internal static IReadOnlyList<ClientArchitecture> ClientLaunchArchitectures(string installDirectory, string? loaderDirectory)
    {
        string install = FullInstall(installDirectory);
        if (DetectClient(install) != ClientPlatform.MacOS) return [ClientArchitecture.X64];
        string loader = loaderDirectory == null ? install : FullInstall(loaderDirectory);
        _ = MacExecutable(install); // A missing game is an error, not an empty list.
        // Each slice the one Mac check (RequireMacArchitecture) accepts.
        return Enum.GetValues<ClientArchitecture>().Where(architecture =>
        {
            try { RequireMacArchitecture(install, architecture, loader); return true; }
            // A slice that does not match, or no Doorstop library: not launchable. Any other read failure is an error.
            catch (Exception error) when (error is InvalidOperationException or FileNotFoundException) { return false; }
        }).ToList();
    }

    /// <summary>Checks a source game and the loader that will be staged into its disposable copy, before copying either.</summary>
    internal static void RequireClientArchitecture(string installDirectory, ClientArchitecture architecture, string? loaderDirectory = null)
    {
        string install = FullInstall(installDirectory);
        if (DetectClient(install) == ClientPlatform.MacOS)
            _ = RequireMacArchitecture(install, architecture, loaderDirectory);
        else if (architecture != ClientArchitecture.X64)
            throw new ArgumentException("arm64 requires a macOS Valheim client; this install is x64 only.");
    }

    internal static (ClientPlatform Platform, string Executable) ResolveClient(string install, ClientPlatform host)
    {
        RequireClientHost(host);
        var platform = DetectClient(install);
        string executable = platform == ClientPlatform.Windows
            ? Path.Combine(install, ClientWindowsExecutable)
            : Path.Combine(FindMacBundle(install)!, MacClientExecutable);
        // Refused before any file mode is read: exec of another OS's binary fails with an opaque error.
        if (platform != host)
            throw new PlatformNotSupportedException($"This {host} host cannot run the {platform} Valheim client {executable}; " +
                $"start it on a {platform} machine. GameLaunch does not support Wine, Proton or other cross-OS launches.");
        if (!File.Exists(executable)) throw new FileNotFoundException($"{ClientMacBundle} has no executable: {MacClientExecutable}", executable);
        // The outer check is the real OS (Unix modes exist); the inner one is the host this launch is built for.
        if (!OperatingSystem.IsWindows())
        {
            if (host != ClientPlatform.Windows && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
                throw new InvalidOperationException($"{executable} is not executable; restore its mode (chmod u+x) in the install.");
        }
        return (platform, executable);
    }

    // The first Doorstop library with the requested slice. dyld cannot insert a library into a process of another architecture.
    // Shared with GameLaunch.ForServer for the macOS dedicated server, whose Doorstop library sits at the runtime's root the same way;
    // a server runs as the machine's own slice, so only a client (client true) is offered the x64 alternative.
    internal static string MacDoorstop(string install, string executable, ClientArchitecture architecture, bool client = true)
    {
        RequireMacGameSlice(MachOArchitectures(executable), architecture, executable);
        var present = MacDoorstopFiles.Where(relative => File.Exists(Path.Combine(install, Native(relative))))
            .Select(relative => (relative, MachOArchitectures(Path.Combine(install, Native(relative))))).ToList();
        return Path.Combine(install, Native(SelectMacDoorstop(present, architecture, "install", client)));
    }

    // The one Mac game-slice decision (#657). Every caller reads the slices where the game is (this machine's file through
    // MachOArchitectures, a host's through lipo, MacSlices) and asks here: plan validation and the launch on this machine,
    // and the remote preflight. A universal game never falls back to the other slice.
    internal static void RequireMacGameSlice(IReadOnlySet<ClientArchitecture> slices, ClientArchitecture architecture, string executable)
    {
        if (!slices.Contains(architecture))
            throw new InvalidOperationException($"{executable} has no {SliceName(architecture)} slice (found: {SliceList(slices)}).");
    }

    // The one Mac Doorstop selector (#657): of the Doorstop libraries present in a loader (relative paths with '/', from
    // MacDoorstopFiles, each with its slices, read wherever the loader is), the first with the requested slice, in
    // MacDoorstopFiles order. dyld cannot insert a library into a process of another architecture. where names the loader in a
    // refusal. Used for an install or profile on this machine, a reviewed loader package before staging, and a host's loader.
    internal static string SelectMacDoorstop(IReadOnlyCollection<(string Relative, IReadOnlySet<ClientArchitecture> Slices)> present,
        ClientArchitecture architecture, string where, bool client = true)
    {
        if (present.Count == 0)
            throw new FileNotFoundException($"BepInEx's Doorstop loader is missing from the {where}: " + string.Join(" or ", MacDoorstopFiles));
        foreach (string relative in MacDoorstopFiles)
            if (present.Any(item => item.Relative == relative && item.Slices.Contains(architecture))) return relative;
        string slices = string.Join("; ", MacDoorstopFiles.SelectMany(relative => present.Where(item => item.Relative == relative))
            .Select(item => item.Relative + ": " + SliceList(item.Slices)));
        string hint = architecture == ClientArchitecture.Arm64
            ? "A native arm64 launch needs a libdoorstop.dylib with an arm64 slice (UnityDoorstop 4.5 or later, universal or arm64-only) at the root, " +
              "and a BepInEx core that runs natively (MonoMod 25 or later)" + (client ? "; or request x64 to run under Rosetta." : ".")
            : "An x86_64 launch needs BepInExPack_Valheim's doorstop_libs/libdoorstop_x64.dylib or a libdoorstop.dylib with an x86_64 slice, and Rosetta on Apple Silicon.";
        throw new InvalidOperationException($"No Doorstop library in the {where} has an {SliceName(architecture)} slice ({slices}). " + hint);
    }

    // The launch's architecture check for a macOS install on this machine, shared with plan validation (ClientRunPlan.Validate)
    // so a plan the launch would refuse is refused before a runner starts anything: the game's and a Doorstop library's slice,
    // and for arm64 a native core. Returns the Doorstop library to insert.
    internal static string RequireMacArchitecture(string install, ClientArchitecture architecture, string? loaderDirectory = null)
    {
        string loader = loaderDirectory ?? install;
        string doorstop = MacDoorstop(loader, MacExecutable(install), architecture);
        if (architecture == ClientArchitecture.Arm64 && MacNativeCoreProblem(loader) is { } problem) throw new InvalidOperationException(problem);
        return doorstop;
    }

    private static string MacExecutable(string install)
    {
        string executable = Path.Combine(FindMacBundle(install) ?? throw new FileNotFoundException($"Install contains no {ClientMacBundle}.", install), MacClientExecutable);
        if (!File.Exists(executable)) throw new FileNotFoundException($"{ClientMacBundle} has no executable: {MacClientExecutable}", executable);
        return executable;
    }

    // Why this install's BepInEx core cannot run natively on arm64, or null when it can. Only the managed version is read:
    // no assembly is loaded. Whether every plugin's own hooks and native libraries work on arm64 is not something a file says.
    internal static string? MacNativeCoreProblem(string install)
    {
        string path = Path.Combine(install, Native(MacNativeDetour));
        return MacNativeCoreProblem(File.Exists(path), File.Exists(path) ? AssemblyVersion(path) : null);
    }

    // The one native-core rule (#657), for a core read on this machine or fetched from a host: MonoMod.RuntimeDetour present,
    // and version 25 or later.
    internal static string? MacNativeCoreProblem(bool present, Version? version)
    {
        if (!present)
            return $"A native arm64 launch needs a BepInEx core built on MonoMod {MacNativeDetourMajor} or later, and the install has no {MacNativeDetour}. Install a native core, or request x64 to run under Rosetta.";
        if (version != null && version.Major >= MacNativeDetourMajor) return null;
        return $"{MacNativeDetour} is {(version == null ? "not a .NET assembly" : "version " + version)}: MonoMod before {MacNativeDetourMajor} cannot apply Harmony hooks on arm64, " +
            $"which is why BepInExPack_Valheim's core runs only under Rosetta. A native arm64 launch needs a BepInEx core rebuilt on MonoMod {MacNativeDetourMajor} or later; or request x64 to run under Rosetta.";
    }

    // A managed assembly's version, read without loading it; null for a file that is not one.
    internal static Version? AssemblyVersion(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version; }
        catch (Exception error) when (error is BadImageFormatException or FileLoadException) { return null; }
    }

    // The slices lipo -archs names on a host (x86_64, arm64), as MachOArchitectures gives them for a file here; others are ignored.
    internal static IReadOnlySet<ClientArchitecture> MacSlices(string names)
    {
        var listed = names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Enum.GetValues<ClientArchitecture>().Where(architecture => listed.Contains(SliceName(architecture))).ToHashSet();
    }

    private static string Native(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    // The plan's name for an architecture (ClientRunPlan.Architecture, evidence files).
    internal static string PlanName(ClientArchitecture architecture) => architecture == ClientArchitecture.Arm64 ? "arm64" : "x64";

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
        Directory.EnumerateDirectories(install).FirstOrDefault(path => Path.GetFileName(path).Equals(ClientMacBundle, StringComparison.OrdinalIgnoreCase));
    internal static string FullInstall(string installDirectory) => FullDirectory(installDirectory, "Client install");
}
