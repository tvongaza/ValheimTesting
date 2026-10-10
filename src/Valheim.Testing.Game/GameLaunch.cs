using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// One BepInEx game process's launch, as data: the executable, its working directory and arguments, the variables set for it,
/// the entries put in front of a search list's existing value, and the inherited variables removed. It reproduces what
/// BepInExPack_Valheim's start scripts export, without the script, so the started process ID is the game's own.
/// <see cref="ForServer"/> builds one for a dedicated server and <see cref="ForClient"/> one for a game client. A launch is built
/// for this machine (render it with <see cref="ToStartInfo"/>) or, given a host platform, for a host of a multi-machine session,
/// where the hosting layer's server or interactive-client start runs it.
/// It also says what an install on this machine is: <see cref="DetectServer"/> and <see cref="DetectClient"/> read the platform from
/// the files, <see cref="RequireServerExecutable"/> and <see cref="RequireClientExecutable"/> the executable, and
/// <see cref="ClientLaunchArchitectures"/> the slices a client can start as.
/// </summary>
/// <remarks>
/// Both kinds share one rule set. The caller's Doorstop variables and <c>--doorstop-*</c> arguments are refused, inherited Doorstop
/// variables are removed apart from the ones the launch sets, and <c>SteamAppId</c> defaults to 892970. On Linux and macOS,
/// Doorstop is enabled for BepInEx's preloader and its library directories are put in front of the existing value. A launch for
/// this machine checks the runtime's or install's files before it is built. A launch for a host checks only the path rules there, and
/// the host checks <see cref="RequiredFiles"/> when it starts.
/// </remarks>
public sealed partial class GameLaunch
{
    private static readonly Regex VariableName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>Validate a disposable client's caller variables before its install is copied or a process starts.</summary>
    internal static void ValidateClientEnvironment(IReadOnlyDictionary<string, string> environment, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(environment);
        BepInExLoader.RefuseOverrides(environment, arguments, StringComparer.OrdinalIgnoreCase, nameof(GameLaunch));
        foreach (var (name, value) in environment)
        {
            if (!VariableName.IsMatch(name ?? "")) throw new ArgumentException($"'{name}' is not a client environment variable name.", nameof(environment));
            if (value == null || value.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw new ArgumentException($"Client environment variable '{name}' needs a single-line value without NUL.", nameof(environment));
        }
    }

    private GameLaunch(bool server, ClientPlatform platform, bool forHost, string workingDirectory, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> prepended, IReadOnlyList<string> requiredFiles,
        ClientArchitecture macArchitecture = ClientArchitecture.X64, IEnumerable<string>? secretVariables = null)
    {
        IsServer = server; Platform = platform; ForHost = forHost; WorkingDirectory = workingDirectory; Executable = executable; Arguments = arguments;
        Environment = environment; Prepended = prepended; RequiredFiles = requiredFiles; MacArchitecture = macArchitecture;
        Unset = BepInExLoader.Variables.Where(name => !environment.ContainsKey(name)).ToList();
        SecretVariables = Secrets(secretVariables, environment, prepended, Unset, platform == ClientPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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
    /// <summary>Files the game and BepInEx loader need: relative to <see cref="WorkingDirectory"/> for a normal install, or absolute loader paths when a disposable profile supplies it.</summary>
    public IReadOnlyList<string> RequiredFiles { get; }
    /// <summary>
    /// Names of variables whose values come from this process's environment at launch and reach only the game's environment (a
    /// client's join password, for example); never logged or written as evidence. A launch for this machine inherits them anyway.
    /// </summary>
    public IReadOnlyList<string> SecretVariables { get; }

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
    /// <paramref name="runtime"/> is then a runtime directory here, whose files decide the platform (<see cref="DetectServer"/>).
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
    /// With <paramref name="hostPlatform"/> (Windows or Linux) the launch is for a host of that platform. <paramref name="runtime"/> is then an absolute
    /// path there: a drive path on Windows, and on Linux a path without ':', ';' or '=', which the loader's search lists and
    /// <c>env</c> cannot hold. The caller's variables must be plain names, and on Linux they may not set
    /// <c>LD_LIBRARY_PATH</c>/<c>LD_PRELOAD</c>, which the host session supplies.
    /// </summary>
    public static GameLaunch ForServer(string runtime, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null,
        ServerPlatform? hostPlatform = null) =>
        hostPlatform is { } platform ? ServerOnHost(platform, runtime, arguments, environment) : LocalServer(runtime, arguments, environment, CurrentServerHost, MacServerArchitecture);

    // A launch for this machine, built as if on builtOn (so every machine's branches are tested on any OS).
    internal static GameLaunch LocalServer(string runtimeDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment, ServerHost builtOn,
        ClientArchitecture macArchitecture, string? loaderDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string runtime = FullRuntime(runtimeDirectory);
        string loader = loaderDirectory == null ? runtime : FullRuntime(loaderDirectory);
        var (platform, executable) = ResolveServer(runtime, builtOn);
        if (platform == ServerPlatform.Windows && loader.Equals(runtime, StringComparison.OrdinalIgnoreCase)) loader = runtime;
        if (platform == ServerPlatform.Windows && !loader.Equals(runtime, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A Windows Doorstop proxy must be beside the server executable; use a launch folder that owns both.", nameof(loaderDirectory));
        string loaderKind = loader == runtime ? "runtime" : "loader";
        BepInExLoader.RequireCore(loader, loaderKind);
        string? macDoorstop = null;
        if (platform == ServerPlatform.Windows) BepInExLoader.RequireWindowsLoader(loader, loaderKind);
        else if (platform == ServerPlatform.Linux) BepInExLoader.RequireFile(loader, BepInExLoader.LinuxLibrary, "BepInEx's Doorstop loader is missing from the runtime");
        else
        {
            macDoorstop = MacDoorstop(loader, executable, macArchitecture, client: false);
            // DYLD_INSERT_LIBRARIES splits on ':', so such a path cannot be listed.
            // Injected host types let cross-platform tests inspect a Mac launch on Windows. Only paths on an
            // actual Mac can reach DYLD's colon-separated list; a Windows drive prefix in a fake cannot.
            if (!OperatingSystem.IsWindows() && (runtime.Contains(':') || loader.Contains(':')))
                throw new ArgumentException("A macOS runtime or loader path cannot contain ':'.", nameof(runtimeDirectory));
        }
        environment ??= new Dictionary<string, string>();
        var names = builtOn == ServerHost.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var passed = BepInExLoader.RefuseOverrides(environment, arguments, names, nameof(GameLaunch));
        // Both lists split on ':' (LD_LIBRARY_PATH also on ';'), so such a path cannot be represented.
        // Checked where the launch can run; a Windows host only builds this for inspection.
        if (platform == ServerPlatform.Linux && builtOn != ServerHost.Windows && (runtime.IndexOfAny([':', ';']) >= 0 || loader.IndexOfAny([':', ';']) >= 0))
            throw new ArgumentException("A Linux runtime path cannot contain ':' or ';'.", nameof(runtimeDirectory));

        var os = platform == ServerPlatform.Windows ? ClientPlatform.Windows : platform == ServerPlatform.Linux ? ClientPlatform.Linux : ClientPlatform.MacOS;
        var (set, prepended, required) = Loader(server: true, os, environment, names, LocalJoin(runtime), LocalJoin(loader),
            !loader.Equals(runtime, platform == ServerPlatform.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            Relative(runtime, executable), macDoorstop == null ? null : Relative(loader, macDoorstop));
        return new GameLaunch(server: true, os, forHost: false, runtime, executable, passed, set, prepended, required, macArchitecture);
    }

    private static GameLaunch ServerOnHost(ServerPlatform platform, string runtime, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (platform == ServerPlatform.MacOS)
            throw new PlatformNotSupportedException("A macOS dedicated server starts only on this machine: build its launch without a host platform.");
        bool windows = platform == ServerPlatform.Windows;
        string root = HostRoot(windows, runtime, nameof(runtime), "runtime", server: true);
        var names = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var passed = BepInExLoader.RefuseOverrides(environment ?? new Dictionary<string, string>(), arguments, names, nameof(GameLaunch));
        RequireHostArguments(passed, lineBreaks: false);
        // The server's start script prepends to the host session's value only, so a caller's LD_LIBRARY_PATH/LD_PRELOAD would be lost.
        RequireHostEnvironment(environment, refuseLoaderPaths: !windows);
        var os = windows ? ClientPlatform.Windows : ClientPlatform.Linux;
        string executable = windows ? ServerWindowsExecutable : ServerLinuxExecutable;
        var join = (string relative) => HostJoin(root, windows, relative);
        var (set, prepended, required) = Loader(server: true, os, environment, names, join, join, false, executable, null);
        return new GameLaunch(server: true, os, forHost: true, root, HostJoin(root, windows, executable), passed, set, prepended, required);
    }

    /// <summary>
    /// The launch of one BepInEx game client. A client needs an interactive desktop session with a display, a GPU and a running,
    /// signed-in Steam client. Starting it inside that session is <see cref="ClientSession"/>'s job on this machine and
    /// an interactive client's on a host. <c>-console</c> is added first unless <paramref name="console"/> is false or the
    /// caller passed it; other arguments follow unchanged. On Windows the pack's <c>winhttp.dll</c> proxy loads BepInEx and no
    /// variable is needed. On Linux, Doorstop is enabled for BepInEx's preloader, <c>doorstop_libs</c> goes in front of
    /// <c>LD_LIBRARY_PATH</c> and the library in front of <c>LD_PRELOAD</c>.
    /// Without <paramref name="hostPlatform"/> it is for this machine. <paramref name="install"/> is then the install directory here,
    /// whose contents decide the platform (<see cref="DetectClient"/>). BepInEx's preloader and the platform's Doorstop
    /// loader must be present, and on Windows <c>doorstop_config.ini</c> must enable Doorstop and target BepInEx's preloader. macOS
    /// starts the bundle through <c>/usr/bin/arch</c> as the requested <paramref name="architecture"/>, inserting a Doorstop library
    /// that has that slice. <c>arch</c> fails rather than run another slice, so an arm64 request never falls back to Rosetta. On
    /// Apple Silicon, arm64 is the default; x64 selects the Rosetta compatibility path explicitly. Arm64 also needs a BepInEx core
    /// whose <c>MonoMod.RuntimeDetour.dll</c> is version 25 or later (legacy MonoMod cannot hook on arm64); an install without it is
    /// refused here, before anything starts. The machine must be the client's own OS.
    /// With <paramref name="hostPlatform"/> (Windows or Linux) the launch is for a host of that platform.
    /// <paramref name="install"/> is then an absolute path there: a drive or UNC path on Windows, and on Linux a path without
    /// ':', ';' or '='. The caller's variables must be plain names; on Linux a caller's <c>LD_LIBRARY_PATH</c>/<c>LD_PRELOAD</c>
    /// stays behind the loader's entries. A macOS client cannot be started in its desktop session from another machine and
    /// is refused. Windows and Linux clients are x64 only.
    /// </summary>
    /// <param name="secretVariables">For a host only: variables (for example the join password's) read from this process at launch and given only to
    /// the game; never logged or written as evidence. A launch for this machine inherits this process's environment and refuses them.</param>
    public static GameLaunch ForClient(string install, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null,
        ClientPlatform? hostPlatform = null, ClientArchitecture? architecture = null, bool console = true, IEnumerable<string>? secretVariables = null)
    {
        string platformName = hostPlatform switch
        {
            ClientPlatform.Windows => "windows",
            ClientPlatform.Linux => "linux",
            ClientPlatform.MacOS => "macos",
            _ => EnvironmentInventory.ThisMachine.Platform,
        };
        ClientArchitecture selected = architecture ?? EnvironmentInventory.DefaultClientLaunchArchitecture(platformName,
            hostPlatform is null ? EnvironmentInventory.ThisMachine.OsArchitecture : null);
        if (hostPlatform is { } platform) return ClientOnHost(platform, install, arguments, environment, selected, console, secretVariables);
        if (secretVariables?.Any() == true)
            throw new ArgumentException("A launch for this machine inherits this process's environment; secret variables are named only for a host's launch.", nameof(secretVariables));
        return LocalClient(install, arguments, environment, selected, console, CurrentClientHost);
    }

    // A launch for this machine, built as if on builtOn (so every machine's branches are tested on any OS).
    internal static GameLaunch LocalClient(string installDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment,
        ClientArchitecture architecture, bool console, ClientPlatform builtOn, string? loaderDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string install = FullInstall(installDirectory);
        string loader = loaderDirectory == null ? install : FullInstall(loaderDirectory);
        var (platform, executable) = ResolveClient(install, builtOn);
        if (platform == ClientPlatform.Windows && loader.Equals(install, StringComparison.OrdinalIgnoreCase)) loader = install;
        RequireX64(platform, architecture);
        if (platform == ClientPlatform.Windows && !loader.Equals(install, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A Windows Doorstop proxy must be beside the client executable; use a launch folder that owns both.", nameof(loaderDirectory));
        string loaderKind = loader == install ? "install" : "loader";
        BepInExLoader.RequireCore(loader, loaderKind);
        string? macDoorstop = null;
        if (platform == ClientPlatform.Windows) BepInExLoader.RequireWindowsLoader(loader, loaderKind);
        else if (platform == ClientPlatform.Linux) BepInExLoader.RequireFile(loader, BepInExLoader.LinuxLibrary, "BepInEx's Doorstop loader is missing from the install");
        else macDoorstop = RequireMacArchitecture(install, architecture, loader);

        environment ??= new Dictionary<string, string>();
        var names = builtOn == ClientPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var passed = WithConsole(BepInExLoader.RefuseOverrides(environment, arguments, names, nameof(GameLaunch)), console);
        // Search lists split on ':' (LD_LIBRARY_PATH also on ';'), so such a path cannot be represented. Checked only
        // where the launch can run; a Windows machine builds these launches only when a test injects the host.
        if (platform != ClientPlatform.Windows && !OperatingSystem.IsWindows()
            && (install.IndexOfAny(platform == ClientPlatform.Linux ? [':', ';'] : [':']) >= 0 ||
                loader.IndexOfAny(platform == ClientPlatform.Linux ? [':', ';'] : [':']) >= 0))
            throw new ArgumentException($"A {platform} install path cannot contain ':'" + (platform == ClientPlatform.Linux ? " or ';'." : "."), nameof(installDirectory));

        var (set, prepended, required) = Loader(server: false, platform, environment, names, LocalJoin(install), LocalJoin(loader),
            !loader.Equals(install, platform == ClientPlatform.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            Relative(install, executable), macDoorstop == null ? null : Relative(loader, macDoorstop));
        return new GameLaunch(server: false, platform, forHost: false, install, executable, passed, set, prepended, required, architecture);
    }

    private static GameLaunch ClientOnHost(ClientPlatform platform, string install, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment,
        ClientArchitecture architecture, bool console, IEnumerable<string>? secretVariables)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (platform == ClientPlatform.MacOS)
            throw new PlatformNotSupportedException("A macOS client cannot be started in its desktop session from another machine: a process started over SSH is not in the " +
                "logged-in user's GUI session and cannot reach its Steam client. Run the test runner inside that session and use ClientSession.Launch, or use a Windows or Linux client host.");
        RequireX64(platform, architecture);
        bool windows = platform == ClientPlatform.Windows;
        string root = HostRoot(windows, install, nameof(install), "install", server: false);
        var names = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var passed = WithConsole(BepInExLoader.RefuseOverrides(environment ?? new Dictionary<string, string>(), arguments, names, nameof(GameLaunch)), console);
        // A Windows client's arguments travel as one quoted command line, which can hold a line break.
        RequireHostArguments(passed, lineBreaks: windows);
        // The client's start script puts the loader's entries in front of the caller's own value.
        RequireHostEnvironment(environment, refuseLoaderPaths: false);
        string executable = windows ? ClientWindowsExecutable : ClientLinuxExecutable;
        var join = (string relative) => HostJoin(root, windows, relative);
        var (set, prepended, required) = Loader(server: false, platform, environment, names, join, join, false, executable, null);
        return new GameLaunch(server: false, platform, forHost: true, root, HostJoin(root, windows, executable), passed, set, prepended, required, secretVariables: secretVariables);
    }

    // The loader's part of every launch, one rule set for both roles, here and on a host: the caller's variables first, then
    // SteamAppId's default, Doorstop's variables on Linux and macOS, the entries the pack's scripts put in front of a search list,
    // and the files the launch needs. The game and loader joins are the same for a copied install, but differ for a profile;
    // executable is relative to the game, and macDoorstop to the loader.
    private static (Dictionary<string, string> Set, Dictionary<string, string> Prepended, string[] Required) Loader(bool server, ClientPlatform platform,
        IReadOnlyDictionary<string, string>? caller, StringComparer names, Func<string, string> gameJoin, Func<string, string> loaderJoin,
        bool separateRoots, string executable, string? macDoorstop)
    {
        var set = new Dictionary<string, string>(names);
        foreach (var (name, value) in caller ?? new Dictionary<string, string>()) set[name] = value;
        set.TryAdd("SteamAppId", SteamAppId);
        var prepended = new Dictionary<string, string>(names);
        string RequiredLoader(string relative) => separateRoots ? loaderJoin(relative) : relative;
        if (platform == ClientPlatform.Windows) return (set, prepended, [executable, .. BepInExLoader.LoaderFiles(ClientPlatform.Windows).Select(RequiredLoader)]);
        set["DOORSTOP_ENABLED"] = "1";
        set["DOORSTOP_TARGET_ASSEMBLY"] = loaderJoin(BepInExLoader.CorePreloader);
        if (platform == ClientPlatform.Linux)
        {
            // Same effective order as the pack's scripts: (the server's linux64, then) doorstop_libs, then the existing value.
            prepended["LD_LIBRARY_PATH"] = server ? gameJoin("linux64") + ":" + loaderJoin("doorstop_libs") : loaderJoin("doorstop_libs");
            prepended["LD_PRELOAD"] = "libdoorstop_x64.so";
            return (set, prepended, [executable, .. BepInExLoader.LoaderFiles(ClientPlatform.Linux).Select(RequiredLoader)]);
        }
        // macOS, on this machine only. With Doorstop injected, Mono finds the server's libmono-native.dylib only through the library
        // path, and the server keeps it beside itself, not at the root: without it, or with the root, the server never started
        // (30 Sep 2026, build 25527701). The client's Doorstop library is named by full path, so it needs no library path.
        if (server) prepended["DYLD_LIBRARY_PATH"] = gameJoin(executable[..executable.LastIndexOf('/')]);
        prepended["DYLD_INSERT_LIBRARIES"] = loaderJoin(macDoorstop!);
        return (set, prepended, [executable, RequiredLoader(BepInExLoader.CorePreloader), RequiredLoader(BepInExLoader.CoreLibrary), RequiredLoader(macDoorstop!)]);
    }

    private static Func<string, string> LocalJoin(string root) => relative => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    // -console first unless left out or already passed (in any case, as the game reads it).
    private static List<string> WithConsole(List<string> passed, bool console)
    {
        if (console && !passed.Contains(ConsoleArgument, StringComparer.OrdinalIgnoreCase)) passed.Insert(0, ConsoleArgument);
        return passed;
    }

    private static void RequireX64(ClientPlatform platform, ClientArchitecture architecture)
    {
        if (platform != ClientPlatform.MacOS && architecture != ClientArchitecture.X64)
            throw new ArgumentException($"The {platform} client is x64 only; {architecture} exists for the macOS client alone.", nameof(architecture));
    }

    // A secret is a plain variable name given once that the launch does not set, prepend or unset itself.
    private static List<string> Secrets(IEnumerable<string>? names, IReadOnlyDictionary<string, string> set, IReadOnlyDictionary<string, string> prepended,
        IReadOnlyList<string> unset, StringComparer comparer)
    {
        var secrets = new List<string>();
        foreach (string name in names ?? [])
        {
            if (!VariableName.IsMatch(name ?? "")) throw new ArgumentException($"'{name}' is not a variable name.", "secretVariables");
            if (set.ContainsKey(name!) || prepended.ContainsKey(name!) || unset.Contains(name!, comparer) || secrets.Contains(name!, comparer))
                throw new ArgumentException($"{name} is given twice or set by the launch itself; a secret variable is only a secret.", "secretVariables");
            secrets.Add(name!);
        }
        return secrets;
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
            start.FileName = MacArchLauncher;
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

    // An absolute path on a Windows or Linux host, without its trailing separator. A server's task may log on without network
    // credentials (S4U), so its Windows runtime is a drive path; a client runs with the user's own credentials, so a share is an
    // install too. ':' and ';' separate a Linux search list's entries, and '=' would make env read the game's path as a variable.
    private static string HostRoot(bool windows, string path, string parameter, string kind, bool server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameter);
        if (server && !windows && Regex.IsMatch(path, @"^([A-Za-z]:|\\\\)"))
            throw new PlatformNotSupportedException($"A Windows {kind} path cannot be launched on a Linux host.");
        if (path.Any(char.IsControl) || (windows ? !Regex.IsMatch(path, server ? @"^[A-Za-z]:[\\/]" : @"^([A-Za-z]:[\\/]|\\\\[^\\/])") || path.Contains('"') : !path.StartsWith('/')))
            throw new ArgumentException($"The {kind} must be an absolute {(windows ? server ? "Windows drive" : "Windows" : "Linux")} path on the host.", parameter);
        if (!windows && path.IndexOfAny([':', ';', '=']) >= 0) throw new ArgumentException($"A Linux {kind} path cannot contain ':', ';' or '='.", parameter);
        string trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0 || trimmed.EndsWith(':') || trimmed == "\\" ? path : trimmed;
    }

    // On Linux exactly what the start scripts run ("$runtime/$exe"), so the command line's hash matches the started process.
    private static string HostJoin(string root, bool windows, string relative) =>
        windows ? root.TrimEnd('\\', '/') + "\\" + relative.Replace('/', '\\') : root + "/" + relative;

    // NUL ends a string anywhere; a line break would end a Linux argument (one spec line each) early.
    private static void RequireHostArguments(List<string> passed, bool lineBreaks)
    {
        foreach (string argument in passed)
            if (argument.Contains('\0') || (!lineBreaks && argument.Any(ch => ch is '\n' or '\r')))
                throw new ArgumentException(lineBreaks ? "A launch argument cannot contain NUL." : "A launch argument cannot contain NUL or a line break.", "arguments");
    }

    // A host shell sets these by name, so a name must be a plain variable name and a value cannot hold NUL.
    private static void RequireHostEnvironment(IReadOnlyDictionary<string, string>? environment, bool refuseLoaderPaths)
    {
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            if (!VariableName.IsMatch(name ?? "")) throw new ArgumentException($"'{name}' is not a variable name.", nameof(environment));
            ArgumentNullException.ThrowIfNull(value, name);
            if (value.Contains('\0')) throw new ArgumentException($"{name} cannot contain a NUL character.", nameof(environment));
            if (refuseLoaderPaths && name is "LD_LIBRARY_PATH" or "LD_PRELOAD") throw new ArgumentException($"{name} is set by the launch for BepInEx's loader; leave it out of the environment.", nameof(environment));
        }
    }
}
