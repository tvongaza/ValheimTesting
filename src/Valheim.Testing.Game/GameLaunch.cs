using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// One BepInEx game process's launch, as data: the executable, its working directory and arguments, the variables set for it,
/// the entries put in front of a search list's existing value, and the inherited variables removed. It reproduces what
/// BepInExPack_Valheim's start scripts export, without the script, so the started process ID is the game's own.
/// <see cref="ForServer"/> builds one for a dedicated server. A launch is built for this machine (render it with
/// <see cref="ToStartInfo"/>) or, given a host platform, for an <see cref="IGameHost"/>, where <see cref="HostServer.StartAsync(IGameHost, GameLaunch, string, TimeSpan, IReadOnlyList{string}, string, CancellationToken)"/> starts it.
/// </summary>
/// <remarks>
/// Both kinds share one rule set. The caller's Doorstop variables and <c>--doorstop-*</c> arguments are refused, inherited Doorstop
/// variables are removed apart from the ones the launch sets, and <c>SteamAppId</c> defaults to 892970. On Linux and macOS,
/// Doorstop is enabled for BepInEx's preloader and its library directories are put in front of the existing value. A launch for
/// this machine checks the runtime's files before it is built. A launch for a host checks only the path rules there, and
/// the host checks <see cref="RequiredFiles"/> when it starts.
/// </remarks>
public sealed class GameLaunch
{
    private static readonly Regex VariableName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private GameLaunch(bool server, ClientPlatform platform, bool forHost, string workingDirectory, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> prepended, IReadOnlyList<string> requiredFiles,
        ClientArchitecture macArchitecture = ClientArchitecture.X64)
    {
        IsServer = server; Platform = platform; ForHost = forHost; WorkingDirectory = workingDirectory; Executable = executable; Arguments = arguments;
        Environment = environment; Prepended = prepended; RequiredFiles = requiredFiles; MacArchitecture = macArchitecture;
        Unset = BepInExLoader.Variables.Where(name => !environment.ContainsKey(name)).ToList();
    }

    /// <summary>The runtime or install directory: the process's working directory.</summary>
    public string WorkingDirectory { get; }
    /// <summary>
    /// The game executable's full path. On macOS, <see cref="ToStartInfo"/> starts it through <c>/usr/bin/arch</c>, so the start
    /// info's file name is that launcher.
    /// </summary>
    public string Executable { get; }
    /// <summary>The game's arguments, in order.</summary>
    public IReadOnlyList<string> Arguments { get; }
    /// <summary>Variables set for the game, the caller's first.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }
    /// <summary>Entries put in front of a search list's existing value (<c>LD_LIBRARY_PATH</c>, <c>LD_PRELOAD</c>, macOS's <c>DYLD_*</c>), as the pack's scripts do.</summary>
    public IReadOnlyDictionary<string, string> Prepended { get; }
    /// <summary>Inherited variables removed before the launch: Doorstop's, apart from the ones set here.</summary>
    public IReadOnlyList<string> Unset { get; }
    /// <summary>Files, relative to <see cref="WorkingDirectory"/>, the game and BepInEx's loader need.</summary>
    public IReadOnlyList<string> RequiredFiles { get; }

    internal bool IsServer { get; }
    // The operating system the launch runs on (ClientPlatform is also the host type).
    internal ClientPlatform Platform { get; }
    internal bool ForHost { get; }
    // A macOS launch on this machine: the slice /usr/bin/arch starts.
    internal ClientArchitecture MacArchitecture { get; }
    /// <summary>True when the arguments hold <c>-crossplay</c> (in any case, as the game reads it): a host start then refuses a host whose <c>libparty.so</c> cannot load.</summary>
    internal bool Crossplay => Arguments.Any(argument => argument.Equals("-crossplay", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The launch of one owned BepInEx dedicated server. Without <paramref name="hostPlatform"/> it is for this machine.
    /// <paramref name="runtime"/> is then a runtime directory here, whose files decide the platform (<see cref="ServerLaunch.Detect"/>).
    /// BepInEx's preloader and core and the platform's Doorstop loader must be present, and on Windows
    /// <c>doorstop_config.ini</c> must enable Doorstop and target BepInEx's preloader. On macOS the server starts through
    /// <c>/usr/bin/arch</c> as the machine's own architecture (arm64 on Apple Silicon). Doorstop is enabled for BepInEx's preloader,
    /// with a Doorstop library at the runtime's root that has that slice inserted (<c>DYLD_INSERT_LIBRARIES</c>, passed with
    /// <c>-e</c> because the kernel strips DYLD_* from the SIP-protected <c>arch</c>). The stock BepInExPack's Doorstop and core are
    /// x86_64-only, so a native launch needs a universal <c>libdoorstop.dylib</c> and a BepInEx core that runs natively. On Linux
    /// the runtime's <c>linux64</c> and <c>doorstop_libs</c> go in front of <c>LD_LIBRARY_PATH</c>, and
    /// <c>libdoorstop_x64.so</c> in front of <c>LD_PRELOAD</c>. A caller's own values are kept behind them. A server runs only
    /// on its own OS, except that a Windows machine may build a Linux launch for inspection. Any other mismatch refuses with
    /// <see cref="PlatformNotSupportedException"/>.
    /// With <paramref name="hostPlatform"/> (Windows or Linux) the launch is for an <see cref="IGameHost"/> of that platform. <paramref name="runtime"/> is then an absolute
    /// path there: a drive path on Windows, and on Linux a path without ':', ';' or '=', which the loader's search lists and
    /// <c>env</c> cannot hold. The caller's variables must be plain names, and on Linux they may not set
    /// <c>LD_LIBRARY_PATH</c>/<c>LD_PRELOAD</c>, which the host session supplies.
    /// </summary>
    public static GameLaunch ForServer(string runtime, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null,
        ServerPlatform? hostPlatform = null) =>
        hostPlatform is { } platform ? ServerOnHost(platform, runtime, arguments, environment) : LocalServer(runtime, arguments, environment, ServerLaunch.CurrentHost, ServerLaunch.MacArchitecture);

    // A launch for this machine, built as if on builtOn (so every machine's branches are tested on any OS).
    internal static GameLaunch LocalServer(string runtimeDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment, ServerHost builtOn,
        ClientArchitecture macArchitecture)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string runtime = ServerLaunch.FullRuntime(runtimeDirectory);
        var (platform, executable) = ServerLaunch.Resolve(runtime, builtOn);
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
        var names = builtOn == ServerHost.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var passed = BepInExLoader.RefuseOverrides(environment, arguments, names, nameof(GameLaunch));
        // Both lists split on ':' (LD_LIBRARY_PATH also on ';'), so such a path cannot be represented.
        // Checked where the launch can run; a Windows host only builds this for inspection.
        if (platform == ServerPlatform.Linux && builtOn != ServerHost.Windows && runtime.IndexOfAny([':', ';']) >= 0)
            throw new ArgumentException("A Linux runtime path cannot contain ':' or ';'.", nameof(runtimeDirectory));

        var set = new Dictionary<string, string>(names);
        foreach (var (name, value) in environment) set[name] = value;
        if (!set.ContainsKey("SteamAppId")) set["SteamAppId"] = ServerLaunch.DedicatedServerSteamAppId;
        var prepended = new Dictionary<string, string>(names);
        string relative = Path.GetRelativePath(runtime, executable).Replace('\\', '/');
        string[] required;
        if (platform == ServerPlatform.Windows) required = [relative, .. BepInExLoader.LoaderFiles(ClientPlatform.Windows)];
        else
        {
            set["DOORSTOP_ENABLED"] = "1";
            set["DOORSTOP_TARGET_ASSEMBLY"] = Path.Combine(runtime, BepInExLoader.Preloader);
            if (platform == ServerPlatform.Linux)
            {
                // Same effective order as the pack's script: linux64, then doorstop_libs, then the existing value.
                prepended["LD_LIBRARY_PATH"] = Path.Combine(runtime, "linux64") + ":" + Path.Combine(runtime, "doorstop_libs");
                prepended["LD_PRELOAD"] = "libdoorstop_x64.so";
                required = [relative, .. BepInExLoader.LoaderFiles(ClientPlatform.Linux)];
            }
            else
            {
                // With Doorstop injected, Mono finds libmono-native.dylib only through the library path, and the server keeps it
                // beside itself, not at the root (the Mac client's launcher value): without it, or with the root, the server never
                // started (30 Sep 2026, build 25527701). Vanilla, without Doorstop, needs neither.
                prepended["DYLD_LIBRARY_PATH"] = Path.GetDirectoryName(executable)!;
                prepended["DYLD_INSERT_LIBRARIES"] = macDoorstop!;
                required = [relative, BepInExLoader.CorePreloader, BepInExLoader.CoreLibrary, Path.GetRelativePath(runtime, macDoorstop!).Replace('\\', '/')];
            }
        }
        return new GameLaunch(server: true, platform == ServerPlatform.Windows ? ClientPlatform.Windows : platform == ServerPlatform.Linux ? ClientPlatform.Linux : ClientPlatform.MacOS,
            forHost: false, runtime, executable, passed, set, prepended, required, macArchitecture);
    }

    private static GameLaunch ServerOnHost(ServerPlatform platform, string runtime, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (platform == ServerPlatform.MacOS)
            throw new PlatformNotSupportedException("A macOS dedicated server starts only on this machine: build its launch without a host platform.");
        bool windows = platform == ServerPlatform.Windows;
        // A server's task may log on without network credentials (S4U), so a Windows runtime is a drive path, never a share.
        string root = HostRoot(windows, runtime, nameof(runtime), "runtime", share: false);
        var names = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var passed = HostArguments(environment, arguments, names);
        var set = HostEnvironment(environment, names, windows);
        if (!set.ContainsKey("SteamAppId")) set["SteamAppId"] = ServerLaunch.DedicatedServerSteamAppId;
        var prepended = new Dictionary<string, string>(names);
        string executable;
        string[] required;
        if (windows)
        {
            executable = HostJoin(root, true, ServerLaunch.WindowsExecutable);
            required = [ServerLaunch.WindowsExecutable, .. BepInExLoader.LoaderFiles(ClientPlatform.Windows)];
        }
        else
        {
            executable = HostJoin(root, false, ServerLaunch.LinuxExecutable);
            set["DOORSTOP_ENABLED"] = "1";
            set["DOORSTOP_TARGET_ASSEMBLY"] = HostJoin(root, false, BepInExLoader.CorePreloader);
            // Same effective order as the pack's script: linux64, then doorstop_libs, then the existing value.
            prepended["LD_LIBRARY_PATH"] = HostJoin(root, false, "linux64") + ":" + HostJoin(root, false, "doorstop_libs");
            prepended["LD_PRELOAD"] = "libdoorstop_x64.so";
            required = [ServerLaunch.LinuxExecutable, .. BepInExLoader.LoaderFiles(ClientPlatform.Linux)];
        }
        return new GameLaunch(server: true, windows ? ClientPlatform.Windows : ClientPlatform.Linux, forHost: true, root, executable, passed, set, prepended, required);
    }

    /// <summary>
    /// The start info for a launch built for this machine. The caller's variables are set, inherited Doorstop variables are removed
    /// apart from the ones the launch sets, and <see cref="Prepended"/> entries go in front of this process's own value. On Windows a
    /// dedicated server gets a console of its own (with no window), so stopping it signals that console rather than the runner's. A
    /// launch for a host refuses with <see cref="InvalidOperationException"/>.
    /// </summary>
    public ProcessStartInfo ToStartInfo()
    {
        if (ForHost) throw new InvalidOperationException($"This launch is for a host; {(IsServer ? "HostServer.StartAsync" : "InteractiveClient.StartAsync")} starts it there.");
        // An SSH parent can make a server ignore Ctrl+C, so ProcessQuit uses Ctrl+Break in that launch context.
        var start = new ProcessStartInfo(Executable) { WorkingDirectory = WorkingDirectory, UseShellExecute = false, CreateNoWindow = IsServer && Platform == ClientPlatform.Windows };
        // Inherited Doorstop values would reach the game too; only the ones set below may.
        foreach (string name in Unset) start.Environment.Remove(name);
        foreach (var (name, value) in Environment) start.Environment[name] = value;
        foreach (var (name, entries) in Prepended) start.Environment[name] = BepInExLoader.Prepend(start.Environment, name, entries);
        if (Platform == ClientPlatform.MacOS)
        {
            // An explicit slice, because a universal game otherwise starts as the parent's architecture, which the library may lack.
            start.FileName = ClientLaunch.MacArchLauncher;
            start.ArgumentList.Add(MacArchitecture == ClientArchitecture.Arm64 ? "-arm64" : "-x86_64");
            foreach (string name in start.Environment.Keys.Where(key => key.StartsWith("DYLD_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList())
            {
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add(name + "=" + start.Environment[name]);
                start.Environment.Remove(name);
            }
            start.ArgumentList.Add(Executable);
        }
        foreach (string argument in Arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>The hash of the command line the started game has on its host (<see cref="HostProcessProbe.ExpectedCommandLineSha256"/>).</summary>
    internal string CommandLineSha256() => HostProcessProbe.ExpectedCommandLineSha256(Platform == ClientPlatform.Windows, Executable, Arguments);

    /// <summary>
    /// The launch as the host start scripts read it: one line per item, <c>kind base64(UTF-8)</c>, in the order exe, dir, the
    /// arguments (Windows: one <c>args</c> command line; Linux: one <c>arg</c> each), unset, env and prepend. A script ignores the
    /// kinds it does not use. Never written to disk by this library, and the host scripts delete their copy once read: arguments may
    /// hold a server password.
    /// </summary>
    internal string Spec()
    {
        var text = new StringBuilder();
        void Line(string kind, string value) => text.Append(kind).Append(' ').Append(InteractiveClient.Base64(value)).Append('\n');
        Line("exe", Executable);
        Line("dir", WorkingDirectory);
        if (Platform == ClientPlatform.Windows) Line("args", WindowsCommandLine.Join(Arguments));
        else foreach (string argument in Arguments) Line("arg", argument);
        foreach (string name in Unset) Line("unset", name);
        foreach (var (name, value) in Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Line("env", name + "=" + value);
        foreach (var (name, value) in Prepended.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Line("prepend", name + "=" + value);
        return text.ToString();
    }

    // An absolute path on a Windows (drive, or UNC when share) or Linux host, without its trailing separator. ':' and ';' separate a
    // Linux search list's entries, and '=' would make env read the game's path as a variable.
    private static string HostRoot(bool windows, string path, string parameter, string kind, bool share)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameter);
        if (!windows && Regex.IsMatch(path, @"^([A-Za-z]:|\\\\)"))
            throw new PlatformNotSupportedException($"A Windows {kind} path cannot be launched on a Linux host.");
        if (path.Any(char.IsControl) || (windows ? !Regex.IsMatch(path, share ? @"^([A-Za-z]:[\\/]|\\\\[^\\/])" : @"^[A-Za-z]:[\\/]") || path.Contains('"') : !path.StartsWith('/')))
            throw new ArgumentException($"The {kind} must be an absolute {(windows ? share ? "Windows" : "Windows drive" : "Linux")} path on the host.", parameter);
        if (!windows && path.IndexOfAny([':', ';', '=']) >= 0) throw new ArgumentException($"A Linux {kind} path cannot contain ':', ';' or '='.", parameter);
        string trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0 || trimmed.EndsWith(':') || trimmed == "\\" ? path : trimmed;
    }

    // On Linux exactly what the start scripts run ("$runtime/$exe"), so the command line's hash matches the started process.
    private static string HostJoin(string root, bool windows, string relative) =>
        windows ? root.TrimEnd('\\', '/') + "\\" + relative.Replace('/', '\\') : root + "/" + relative;

    // The arguments travel through a spec line; a line break would end a Linux argument early, and NUL ends a string anywhere.
    private static List<string> HostArguments(IReadOnlyDictionary<string, string>? environment, IEnumerable<string> arguments, StringComparer names)
    {
        var passed = BepInExLoader.RefuseOverrides(environment ?? new Dictionary<string, string>(), arguments, names, nameof(GameLaunch));
        foreach (string argument in passed)
            if (argument.Contains('\0') || argument.Any(ch => ch is '\n' or '\r')) throw new ArgumentException("A launch argument cannot contain NUL or a line break.", nameof(arguments));
        return passed;
    }

    // A host shell sets these by name, so a name must be a plain variable name and a value cannot hold NUL. On Linux the host
    // session's own LD_LIBRARY_PATH/LD_PRELOAD are what the launch prepends to.
    private static Dictionary<string, string> HostEnvironment(IReadOnlyDictionary<string, string>? environment, StringComparer names, bool windows)
    {
        var set = new Dictionary<string, string>(names);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            if (!VariableName.IsMatch(name ?? "")) throw new ArgumentException($"'{name}' is not a variable name.", nameof(environment));
            ArgumentNullException.ThrowIfNull(value, name);
            if (value.Contains('\0')) throw new ArgumentException($"{name} cannot contain a NUL character.", nameof(environment));
            if (!windows && name is "LD_LIBRARY_PATH" or "LD_PRELOAD") throw new ArgumentException($"{name} is set by the launch for BepInEx's loader; leave it out of the environment.", nameof(environment));
            set[name!] = value;
        }
        return set;
    }
}
