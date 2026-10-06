using System.Buffers.Binary;
using Valheim.Testing.Game;
using Xunit;

public class ServerLaunchTests
{
    // A host that builds a Linux launch: Linux itself, or Windows for inspection only. macOS refuses (tested below).
    // Launch-content tests inject it so they run unchanged on every OS, including macOS CI.
    private static readonly ServerHost LinuxLaunchHost = OperatingSystem.IsWindows() ? ServerHost.Windows : ServerHost.Linux;
    private const int X86_64 = 0x01000007, Arm64 = 0x0100000C;

    [Fact] public void CurrentHostFollowsTheOperatingSystem()
    {
        var expected = OperatingSystem.IsWindows() ? ServerHost.Windows : OperatingSystem.IsMacOS() ? ServerHost.MacOS : ServerHost.Linux;
        Assert.Equal(expected, GameLaunch.CurrentServerHost);
    }
    [Fact] public void WindowsRuntimeIsDetectedFromItsExecutable()
    {
        using var runtime = Runtime.Windows();
        Assert.Equal(ServerPlatform.Windows, GameLaunch.DetectServer(runtime.Root));
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.exe"), GameLaunch.RequireServerExecutable(runtime.Root, ServerHost.Windows));
    }
    [Fact] public void LinuxRuntimeIsDetectedFromItsExecutable()
    {
        using var runtime = Runtime.Linux();
        Assert.Equal(ServerPlatform.Linux, GameLaunch.DetectServer(runtime.Root));
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.x86_64"), GameLaunch.RequireServerExecutable(runtime.Root, LinuxLaunchHost));
    }
    [Fact] public void RuntimeWithBothExecutablesIsRefused()
    {
        using var runtime = Runtime.Linux(); runtime.Add("valheim_server.exe");
        Assert.Throws<InvalidOperationException>(() => GameLaunch.DetectServer(runtime.Root));
        Assert.Throws<InvalidOperationException>(() => GameLaunch.ForServer(runtime.Root, []).ToStartInfo());
    }
    [Fact] public void RuntimeWithoutAServerIsRefused()
    {
        using var runtime = new Runtime(); runtime.Add("valheim.exe"); runtime.Add("valheim.x86_64");
        var error = Assert.Throws<FileNotFoundException>(() => GameLaunch.DetectServer(runtime.Root));
        Assert.Contains("launch it with GameLaunch.ForClient", error.Message);
        Assert.Throws<DirectoryNotFoundException>(() => GameLaunch.DetectServer(Path.Combine(runtime.Root, "missing")));
    }
    [Fact] public void DirectoryNamedLikeTheExecutableIsNotAServer()
    {
        using var runtime = new Runtime(); Directory.CreateDirectory(Path.Combine(runtime.Root, "valheim_server.x86_64"));
        Assert.Throws<FileNotFoundException>(() => GameLaunch.DetectServer(runtime.Root));
    }
    [Fact] public void ExecuteBitIsRequiredExceptOnWindowsHosts()
    {
        using var runtime = Runtime.Linux(executable: false);
        string executable = Path.Combine(runtime.Root, "valheim_server.x86_64");
        // No Unix mode exists to read on a Windows host; the file's presence is the whole check.
        Assert.Equal(executable, GameLaunch.RequireServerExecutable(runtime.Root, ServerHost.Windows));
        if (OperatingSystem.IsWindows()) return; // Real file modes are needed for the Linux-host arm.
        Assert.Throws<InvalidOperationException>(() => GameLaunch.RequireServerExecutable(runtime.Root, ServerHost.Linux));
        Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalServer(runtime.Root, [], null, ServerHost.Linux, GameLaunch.MacServerArchitecture).ToStartInfo());
        File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        Assert.Equal(executable, GameLaunch.RequireServerExecutable(runtime.Root, ServerHost.Linux));
    }
    [Fact] public void WindowsLaunchUsesRuntimeAndCallerSettingsWithoutLinuxLoader()
    {
        using var runtime = Runtime.Windows();
        var start = GameLaunch.LocalServer(runtime.Root, ["-batchmode", "-nographics", "-savedir", "C:/saves with space"],
            new Dictionary<string, string> { ["TEST_TOKEN"] = "abc" }, ServerHost.Windows, GameLaunch.MacServerArchitecture).ToStartInfo();
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.exe"), start.FileName);
        Assert.Equal(runtime.Root, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "-batchmode", "-nographics", "-savedir", "C:/saves with space" }, start.ArgumentList);
        Assert.Equal("abc", start.Environment["TEST_TOKEN"]);
        Assert.Equal("892970", start.Environment["SteamAppId"]);
        // Windows loads BepInEx through winhttp.dll and doorstop_config.ini, not the environment.
        Assert.False(start.Environment.ContainsKey("DOORSTOP_TARGET_ASSEMBLY"));
    }
    [Fact] public void LinuxLaunchEnablesDoorstopAndPrependsToCallerLibraryPaths()
    {
        using var runtime = Runtime.Linux();
        var start = GameLaunch.LocalServer(runtime.Root, ["-batchmode", "-nographics"], new Dictionary<string, string>
        {
            ["LD_LIBRARY_PATH"] = "/opt/extra/lib:/opt/other/lib", ["LD_PRELOAD"] = "libcaller.so", ["TEST_TOKEN"] = "abc",
        }, LinuxLaunchHost, GameLaunch.MacServerArchitecture).ToStartInfo();
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.x86_64"), start.FileName);
        Assert.Equal(runtime.Root, start.WorkingDirectory);
        Assert.Equal("1", start.Environment["DOORSTOP_ENABLED"]);
        Assert.Equal(Path.Combine(runtime.Root, "BepInEx", "core", "BepInEx.Preloader.dll"), start.Environment["DOORSTOP_TARGET_ASSEMBLY"]);
        Assert.Equal(Path.Combine(runtime.Root, "linux64") + ":" + Path.Combine(runtime.Root, "doorstop_libs") + ":/opt/extra/lib:/opt/other/lib",
            start.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal("libdoorstop_x64.so:libcaller.so", start.Environment["LD_PRELOAD"]);
        Assert.Equal("abc", start.Environment["TEST_TOKEN"]);
        Assert.Equal("892970", start.Environment["SteamAppId"]);
    }
    [Fact] public void EmptyLibraryPathsAddNoEmptyEntry()
    {
        using var runtime = Runtime.Linux();
        var start = GameLaunch.LocalServer(runtime.Root, [], new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = "", ["LD_PRELOAD"] = "" }, LinuxLaunchHost, GameLaunch.MacServerArchitecture).ToStartInfo();
        // A trailing ':' would add the working directory to the search path.
        Assert.Equal(Path.Combine(runtime.Root, "linux64") + ":" + Path.Combine(runtime.Root, "doorstop_libs"), start.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal("libdoorstop_x64.so", start.Environment["LD_PRELOAD"]);
    }
    [Fact] public void CallerSteamAppIdIsKept()
    {
        using var runtime = Runtime.Linux();
        var start = GameLaunch.LocalServer(runtime.Root, [], new Dictionary<string, string> { ["SteamAppId"] = "123" }, LinuxLaunchHost, GameLaunch.MacServerArchitecture).ToStartInfo();
        Assert.Equal("123", start.Environment["SteamAppId"]);
    }
    [Theory]
    [InlineData("linux", "DOORSTOP_ENABLED")] [InlineData("linux", "DOORSTOP_TARGET_ASSEMBLY")] [InlineData("linux", "DOORSTOP_DISABLE")]
    [InlineData("windows", "DOORSTOP_DISABLE")] [InlineData("windows", "doorstop_enabled")] [InlineData("windows", "DOORSTOP_TARGET_ASSEMBLY")]
    public void CallerCannotDisableOrRedirectTheLoader(string platform, string name)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        Assert.Throws<ArgumentException>(() => GameLaunch.LocalServer(runtime.Root, [], new Dictionary<string, string> { [name] = "0" },
            platform == "linux" ? LinuxLaunchHost : ServerHost.Windows, GameLaunch.MacServerArchitecture).ToStartInfo());
    }
    [Theory] [InlineData("linux")] [InlineData("windows")]
    public void DoorstopArgumentsAreRefused(string platform)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        var error = Assert.Throws<ArgumentException>(() => GameLaunch.LocalServer(runtime.Root, ["-batchmode", "--doorstop-enabled", "false"], null,
            platform == "linux" ? LinuxLaunchHost : ServerHost.Windows, GameLaunch.MacServerArchitecture).ToStartInfo());
        Assert.Contains("--doorstop-enabled", error.Message);
    }
    // Set in this process, as a parent shell would: only the loader values GameLaunch.ForServer sets itself may reach the server.
    [Theory] [InlineData("linux")] [InlineData("windows")]
    public void InheritedLoaderVariablesNeverReachTheServer(string platform)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", "1");
        try
        {
            var start = GameLaunch.LocalServer(runtime.Root, [], null, platform == "linux" ? LinuxLaunchHost : ServerHost.Windows, GameLaunch.MacServerArchitecture).ToStartInfo();
            Assert.False(start.Environment.ContainsKey("DOORSTOP_DISABLE"));
            if (platform == "windows") Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase));
            else Assert.Equal(Path.Combine(runtime.Root, "BepInEx", "core", "BepInEx.Preloader.dll"), start.Environment["DOORSTOP_TARGET_ASSEMBLY"]);
        }
        finally { Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", null); }
    }
    [Theory]
    [InlineData("[General]\nenabled = false\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n")]
    [InlineData("[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\Other.Preloader.dll\n")]
    public void WindowsDoorstopConfigMustEnableBepInExsPreloader(string config)
    {
        using var runtime = Runtime.Windows(); runtime.Add("BepInEx/core/Other.Preloader.dll");
        File.WriteAllText(Path.Combine(runtime.Root, "doorstop_config.ini"), config);
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalServer(runtime.Root, [], null, ServerHost.Windows, GameLaunch.MacServerArchitecture).ToStartInfo());
        Assert.Contains("doorstop_config.ini", error.Message);
    }
    // The station's dedicated-server install carries Doorstop 3's section and key names.
    [Fact] public void WindowsDoorstop3ConfigIsAccepted()
    {
        using var runtime = Runtime.Windows();
        File.WriteAllText(Path.Combine(runtime.Root, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        File.WriteAllText(Path.Combine(runtime.Root, "winhttp.dll"), "MZ targetAssembly"); // Doorstop 3's own proxy
        Assert.EndsWith("valheim_server.exe", GameLaunch.LocalServer(runtime.Root, [], null, ServerHost.Windows, GameLaunch.MacServerArchitecture).ToStartInfo().FileName);
    }
    [Theory]
    [InlineData("linux", "BepInEx/core/BepInEx.Preloader.dll")] [InlineData("linux", "BepInEx/core/BepInEx.dll")] [InlineData("linux", "doorstop_libs/libdoorstop_x64.so")]
    [InlineData("windows", "BepInEx/core/BepInEx.Preloader.dll")] [InlineData("windows", "BepInEx/core/BepInEx.dll")]
    [InlineData("windows", "winhttp.dll")] [InlineData("windows", "doorstop_config.ini")]
    public void MissingBepInExLoaderIsRefusedRatherThanStartingVanilla(string platform, string missing)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        File.Delete(Path.Combine(runtime.Root, missing));
        Assert.Throws<FileNotFoundException>(() => GameLaunch.LocalServer(runtime.Root, [], null, platform == "linux" ? LinuxLaunchHost : ServerHost.Windows, GameLaunch.MacServerArchitecture).ToStartInfo());
    }
    [Fact] public void LinuxRuntimePathThatCannotBeListedIsRefused()
    {
        if (OperatingSystem.IsWindows()) return; // Windows paths are not valid in Linux search lists anyway.
        using var runtime = Runtime.Linux("server:copy");
        Assert.Throws<ArgumentException>(() => GameLaunch.LocalServer(runtime.Root, [], null, ServerHost.Linux, GameLaunch.MacServerArchitecture).ToStartInfo());
        // A Windows host only builds the launch for inspection and does not check.
        Assert.Equal(runtime.Root, GameLaunch.LocalServer(runtime.Root, [], null, ServerHost.Windows, GameLaunch.MacServerArchitecture).ToStartInfo().WorkingDirectory);
    }

    [Theory]
    [InlineData("Valheim.app", "Contents/MacOS/Valheim")] // the bundle itself
    [InlineData("Valheim copy.app", "Contents/MacOS/Valheim")] // a renamed bundle, recognised by its executable
    [InlineData("Valheim", "Valheim.app/Contents/MacOS/Valheim")] // a Steam install directory holding the bundle
    [InlineData("Valheim", "Valheim.app/Contents/Info.plist")] // a partial copy: the bundle directory is enough
    public void MacClientIsRefusedAsNotAServer(string name, string file)
    {
        using var runtime = new Runtime(name); runtime.Add(file);
        var error = Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.DetectServer(runtime.Root));
        Assert.Contains("not a dedicated server", error.Message);
        Assert.Contains("Steam app 896660", error.Message);
        Assert.Contains("GameLaunch.ForClient", error.Message);
        Assert.Contains("container", error.Message);
        Assert.Contains("remote Windows/Linux host", error.Message);
        foreach (var host in new[] { ServerHost.Windows, ServerHost.Linux, ServerHost.MacOS })
        {
            Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.RequireServerExecutable(runtime.Root, host));
            Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.LocalServer(runtime.Root, [], null, host, GameLaunch.MacServerArchitecture).ToStartInfo());
        }
    }
    [Fact] public void ServerExecutableWinsOverAMacClientBesideIt()
    {
        using var runtime = Runtime.Linux(); runtime.Add("Valheim.app/Contents/MacOS/Valheim");
        Assert.Equal(ServerPlatform.Linux, GameLaunch.DetectServer(runtime.Root));
    }
    [Fact] public void EmptyDirectoryNamedLikeAnAppIsStillAMissingServer()
    {
        using var runtime = new Runtime("Server.app");
        Assert.Throws<FileNotFoundException>(() => GameLaunch.DetectServer(runtime.Root));
    }
    [Theory] [InlineData("linux")] [InlineData("windows")]
    public void MacHostRefusesToLaunchEitherServer(string platform)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        // Detection reads the runtime's contents and still works on a Mac, e.g. to stage a copy for a container.
        Assert.Equal(platform == "linux" ? ServerPlatform.Linux : ServerPlatform.Windows, GameLaunch.DetectServer(runtime.Root));
        var error = Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.RequireServerExecutable(runtime.Root, ServerHost.MacOS));
        Assert.Contains("run the macOS dedicated server (Steam app 896660", error.Message);
        Assert.Contains("--platform linux/amd64", error.Message);
        Assert.Contains("remote Windows/Linux host", error.Message);
        Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.LocalServer(runtime.Root, ["-batchmode"], null, ServerHost.MacOS, GameLaunch.MacServerArchitecture).ToStartInfo());
    }
    [Fact] public void MacHostRefusesBeforeReadingTheExecuteBit()
    {
        // A missing execute bit would otherwise report chmod, which cannot help on a Mac.
        using var runtime = Runtime.Linux(executable: false);
        Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.RequireServerExecutable(runtime.Root, ServerHost.MacOS));
    }
    [Fact] public void PublicOverloadsRefuseOnAMacHostOnly()
    {
        using var runtime = Runtime.Linux();
        if (OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.RequireServerExecutable(runtime.Root));
            Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.ForServer(runtime.Root, []).ToStartInfo());
            return;
        }
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.x86_64"), GameLaunch.RequireServerExecutable(runtime.Root));
        Assert.Equal(runtime.Root, GameLaunch.ForServer(runtime.Root, []).ToStartInfo().WorkingDirectory);
    }

    // ---- the macOS dedicated server ----

    [Fact] public void MacRuntimeIsDetectedFromItsExecutableAndDataFolder()
    {
        using var runtime = Runtime.Mac();
        Assert.Equal(ServerPlatform.MacOS, GameLaunch.DetectServer(runtime.Root));
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server", "Valheim"), GameLaunch.RequireServerExecutable(runtime.Root, ServerHost.MacOS));
        Assert.Equal(ServerPlatform.MacOS, GameLaunch.DetectServer(runtime.Root)); // Detection reads contents on any host.
    }
    [Fact] public void MacExecutableWithoutItsDataFolderIsNotAServer()
    {
        using var runtime = new Runtime(); runtime.Add("valheim_server/Valheim", Fat(X86_64, Arm64));
        Assert.Contains("valheim_server/Data", Assert.Throws<FileNotFoundException>(() => GameLaunch.DetectServer(runtime.Root)).Message);
    }
    [Fact] public void MacRuntimeBesideAnotherServerIsRefused()
    {
        using var runtime = Runtime.Mac(); runtime.Add("valheim_server.x86_64");
        Assert.Contains("more than one", Assert.Throws<InvalidOperationException>(() => GameLaunch.DetectServer(runtime.Root)).Message);
    }
    [Theory] [InlineData("linux")] [InlineData("windows")]
    public void OnlyAMacHostRunsTheMacServer(string hostName)
    {
        using var runtime = Runtime.Mac();
        var host = hostName == "linux" ? ServerHost.Linux : ServerHost.Windows;
        var error = Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.RequireServerExecutable(runtime.Root, host));
        Assert.Contains("cannot run the MacOS dedicated server", error.Message);
        Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.LocalServer(runtime.Root, ["-batchmode"], null, host, ClientArchitecture.Arm64).ToStartInfo());
    }
    [Fact] public void MacLaunchStartsTheNativeSliceWithDoorstopInsertedAndTheServersLibraryPath()
    {
        if (OperatingSystem.IsWindows()) return; // A Windows path cannot be a macOS runtime (it holds ':').
        using var runtime = Runtime.Mac();
        var start = GameLaunch.LocalServer(runtime.Root, ["-batchmode", "-nographics", "-savedir", "/saves with space"],
            new Dictionary<string, string> { ["KEEP"] = "yes" }, ServerHost.MacOS, ClientArchitecture.Arm64).ToStartInfo();
        string executable = Path.Combine(runtime.Root, "valheim_server", "Valheim");
        Assert.Equal("/usr/bin/arch", start.FileName);
        Assert.Equal(runtime.Root, start.WorkingDirectory);
        Assert.Equal(new[] { "-arm64", "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(runtime.Root, "libdoorstop.dylib"),
            "-e", "DYLD_LIBRARY_PATH=" + Path.Combine(runtime.Root, "valheim_server"), executable,
            "-batchmode", "-nographics", "-savedir", "/saves with space" }, start.ArgumentList);
        Assert.Equal("1", start.Environment["DOORSTOP_ENABLED"]);
        Assert.Equal(Path.Combine(runtime.Root, "BepInEx", "core", "BepInEx.Preloader.dll"), start.Environment["DOORSTOP_TARGET_ASSEMBLY"]);
        Assert.Equal("892970", start.Environment["SteamAppId"]);
        Assert.Equal("yes", start.Environment["KEEP"]);
        // The kernel strips DYLD_* from arch's environment, so they travel only as -e arguments. The library path is the
        // server's own folder, not the root: with the root, Mono missed libmono-native.dylib.
        Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("DYLD_", StringComparison.Ordinal));
        Assert.DoesNotContain("DYLD_LIBRARY_PATH=" + runtime.Root, start.ArgumentList);
        var x64 = GameLaunch.LocalServer(runtime.Root, [], null, ServerHost.MacOS, ClientArchitecture.X64).ToStartInfo();
        Assert.Equal("-x86_64", x64.ArgumentList[0]);
    }
    [Fact] public void MacLaunchRefusesAServerOrDoorstopWithoutTheNativeSlice()
    {
        if (OperatingSystem.IsWindows()) return;
        using var intelOnly = Runtime.Mac(server: Thin(X86_64));
        Assert.Contains("no arm64 slice", Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalServer(intelOnly.Root, [], null, ServerHost.MacOS, ClientArchitecture.Arm64).ToStartInfo()).Message);
        using var stockDoorstop = Runtime.Mac(doorstop: Thin(X86_64));
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalServer(stockDoorstop.Root, [], null, ServerHost.MacOS, ClientArchitecture.Arm64).ToStartInfo());
        Assert.Contains("libdoorstop.dylib with an arm64 slice", error.Message);
        Assert.DoesNotContain("request x64", error.Message); // A server runs as the machine's slice; there is no other to request.
        using var noDoorstop = Runtime.Mac(withDoorstop: false);
        Assert.Throws<FileNotFoundException>(() => GameLaunch.LocalServer(noDoorstop.Root, [], null, ServerHost.MacOS, ClientArchitecture.Arm64).ToStartInfo());
    }
    [Fact] public void MacServerNeedsItsExecuteBitAndAListablePath()
    {
        if (OperatingSystem.IsWindows()) return; // Unix modes and ':' in a directory name.
        using var runtime = Runtime.Mac(executable: false);
        Assert.Contains("chmod u+x", Assert.Throws<InvalidOperationException>(() => GameLaunch.RequireServerExecutable(runtime.Root, ServerHost.MacOS)).Message);
        using var colon = Runtime.Mac("server:copy");
        Assert.Throws<ArgumentException>(() => GameLaunch.LocalServer(colon.Root, [], null, ServerHost.MacOS, ClientArchitecture.Arm64).ToStartInfo());
    }
    [Fact] public void LocalPlatformFollowsTheOperatingSystem()
    {
        var expected = OperatingSystem.IsWindows() ? ServerPlatform.Windows : OperatingSystem.IsMacOS() ? ServerPlatform.MacOS : ServerPlatform.Linux;
        Assert.Equal(expected, GameLaunch.LocalServerPlatform);
    }

    // ---- one launch type, one builder per role (#256) ----

    [Fact] public void LaunchAsDataIsOneTypeWithOneBuilderPerRole()
    {
        // Deleted with no facade: a type under an old name, or another public method that returns a launch or a start info, in
        // any toolkit assembly would be a second way to build a launch.
        var assemblies = new[] { typeof(GameLaunch).Assembly, typeof(PinnedServerRun).Assembly, typeof(SmokeInputs).Assembly };
        var types = assemblies.SelectMany(assembly => assembly.GetTypes()).ToList();
        foreach (string name in new[] { "ServerLaunch", "ClientLaunch", "HostServerLaunch", "HostClientLaunch" })
            Assert.DoesNotContain(types, type => type.Name == name);
        var builders = assemblies.SelectMany(assembly => assembly.GetExportedTypes())
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(method => method.ReturnType == typeof(GameLaunch) || method.ReturnType == typeof(System.Diagnostics.ProcessStartInfo))
            .Select(method => method.DeclaringType!.Name + "." + method.Name).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "GameLaunch.ForClient", "GameLaunch.ForServer", "GameLaunch.ToStartInfo" }, builders);
    }

    // ---- GameLaunch.ForServer: one builder for this machine and for a host ----

    [Theory] [InlineData("windows")] [InlineData("linux")]
    public void ALaunchHereAndALaunchOnAHostOfOnePlatformFollowTheSameRules(string platform)
    {
        bool windows = platform == "windows";
        using var runtime = windows ? Runtime.Windows() : Runtime.Linux();
        var environment = new Dictionary<string, string> { ["MY_MOD_TOKEN"] = "abc" };
        var here = GameLaunch.LocalServer(runtime.Root, ["-batchmode", "-name", "a b"], environment, windows ? ServerHost.Windows : LinuxLaunchHost, GameLaunch.MacServerArchitecture);
        var there = GameLaunch.ForServer(windows ? @"C:\runs\r\runtime" : "/srv/runs/r/runtime", ["-batchmode", "-name", "a b"], environment,
            windows ? ServerPlatform.Windows : ServerPlatform.Linux);
        Assert.Equal(here.Arguments, there.Arguments);
        Assert.Equal(here.Environment.Keys.Order(StringComparer.Ordinal), there.Environment.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(here.Environment["SteamAppId"], there.Environment["SteamAppId"]);
        Assert.Equal(here.Prepended.Keys.Order(StringComparer.Ordinal), there.Prepended.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(here.Unset, there.Unset);
        Assert.Equal(windows ? new[] { "DOORSTOP_ENABLED", "DOORSTOP_TARGET_ASSEMBLY", "DOORSTOP_DISABLE" } : new[] { "DOORSTOP_DISABLE" }, there.Unset);
        Assert.Equal(here.RequiredFiles, there.RequiredFiles);
        // The host renders what the script reads; this machine renders a start info, and neither renders the other.
        Assert.Throws<InvalidOperationException>(() => there.ToStartInfo());
        Assert.Equal(here.Executable, here.ToStartInfo().FileName);
    }

    [Fact] public void AHostLaunchKeepsTheHostsPathRules()
    {
        Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.ForServer("/srv/rt", [], hostPlatform: ServerPlatform.MacOS));
        // A server's task may have no network credentials (S4U): a share is refused, as before.
        Assert.Throws<ArgumentException>(() => GameLaunch.ForServer(@"\\nas\share\runtime\", [], hostPlatform: ServerPlatform.Windows));
        Assert.Equal(@"C:\valheim_server.exe", GameLaunch.ForServer(@"C:\", [], hostPlatform: ServerPlatform.Windows).Executable);
        Assert.Throws<ArgumentException>(() => GameLaunch.ForServer(@"C:\runs\""quoted""\runtime", [], hostPlatform: ServerPlatform.Windows));
        Assert.Throws<ArgumentException>(() => GameLaunch.ForServer(@"runs\runtime", [], hostPlatform: ServerPlatform.Windows));
        Assert.Throws<ArgumentException>(() => GameLaunch.ForServer("/srv/a=b", [], hostPlatform: ServerPlatform.Linux));
        Assert.Throws<ArgumentException>(() => GameLaunch.ForServer("/srv/rt", [], new Dictionary<string, string> { ["NOT A NAME"] = "x" }, ServerPlatform.Linux));
        // On Linux the executable is what the start script runs, "$runtime/$exe", so the journalled command line matches the process.
        foreach (string runtime in new[] { "/srv/rt/", "/" })
        {
            var launch = GameLaunch.ForServer(runtime, [], hostPlatform: ServerPlatform.Linux);
            Assert.Equal(launch.WorkingDirectory + "/" + GameLaunch.ServerLinuxExecutable, launch.Executable);
        }
    }

    [Fact] public void AWindowsHostLaunchSpecCarriesTheCommandLineAndTheUnsetDoorstopVariables()
    {
        var launch = GameLaunch.ForServer(@"C:\runs\r\runtime", ["-batchmode", "-name", "with spaces"], new Dictionary<string, string> { ["MY_MOD_TOKEN"] = "abc" }, ServerPlatform.Windows);
        var spec = launch.Spec().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' '))
            .Select(parts => (parts[0], System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])))).ToList();
        Assert.Equal(("exe", @"C:\runs\r\runtime\valheim_server.exe"), spec[0]);
        Assert.Equal(("dir", @"C:\runs\r\runtime"), spec[1]);
        Assert.Equal(("args", "-batchmode -name \"with spaces\""), spec[2]);
        Assert.Contains(("env", "MY_MOD_TOKEN=abc"), spec);
        Assert.Contains(("env", "SteamAppId=" + GameLaunch.SteamAppId), spec);
        Assert.Equal(new[] { "DOORSTOP_ENABLED", "DOORSTOP_TARGET_ASSEMBLY", "DOORSTOP_DISABLE" }, spec.Where(item => item.Item1 == "unset").Select(item => item.Item2));
        Assert.DoesNotContain(spec, item => item.Item1 is "arg" or "prepend");
    }

    private static byte[] Thin(int cpu)
    {
        var image = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0xFEEDFACF);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(4), cpu);
        return image;
    }
    private static byte[] Fat(params int[] cpus)
    {
        var image = new byte[8 + cpus.Length * 20];
        BinaryPrimitives.WriteUInt32BigEndian(image, 0xCAFEBABE);
        BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(4), cpus.Length);
        for (int i = 0; i < cpus.Length; i++) BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(8 + i * 20), cpus[i]);
        return image;
    }

    private sealed class Runtime : IDisposable
    {
        private readonly string _parent = Path.Combine(Path.GetTempPath(), "server-launch-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public Runtime(string name = "runtime") { Root = Path.Combine(_parent, name); Directory.CreateDirectory(Root); }
        public string Add(string relative)
        {
            string path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fake"); return path;
        }
        public string Add(string relative, byte[] content)
        {
            string path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content); return path;
        }
        // The macOS server as SteamCMD installs it (valheim_server/Valheim + Data), with BepInEx and a universal Doorstop at the root.
        public static Runtime Mac(string name = "runtime", byte[]? server = null, bool executable = true, byte[]? doorstop = null, bool withDoorstop = true)
        {
            var runtime = new Runtime(name);
            string path = runtime.Add("valheim_server/Valheim", server ?? Fat(X86_64, Arm64));
            runtime.Add("valheim_server/Data/Managed/assembly_valheim.dll"); runtime.Add("valheim_server/UnityPlayer.dylib");
            runtime.Add("BepInEx/core/BepInEx.Preloader.dll"); runtime.Add("BepInEx/core/BepInEx.dll");
            if (withDoorstop) runtime.Add("libdoorstop.dylib", doorstop ?? Fat(X86_64, Arm64));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, executable ? File.GetUnixFileMode(path) | UnixFileMode.UserExecute : File.GetUnixFileMode(path) & ~UnixFileMode.UserExecute);
            return runtime;
        }
        public static Runtime Windows()
        {
            var runtime = new Runtime();
            runtime.Add("valheim_server.exe"); File.WriteAllText(runtime.Add("winhttp.dll"), "MZ target_assembly"); runtime.Add("BepInEx/core/BepInEx.Preloader.dll"); runtime.Add("BepInEx/core/BepInEx.dll");
            File.WriteAllText(runtime.Add("doorstop_config.ini"), "[General]\nenabled = true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
            return runtime;
        }
        public static Runtime Linux(string name = "runtime", bool executable = true)
        {
            var runtime = new Runtime(name);
            string server = runtime.Add("valheim_server.x86_64");
            runtime.Add("doorstop_libs/libdoorstop_x64.so"); runtime.Add("BepInEx/core/BepInEx.Preloader.dll"); runtime.Add("BepInEx/core/BepInEx.dll");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(server, executable ? File.GetUnixFileMode(server) | UnixFileMode.UserExecute : File.GetUnixFileMode(server) & ~UnixFileMode.UserExecute);
            return runtime;
        }
        public void Dispose() => Directory.Delete(_parent, true);
    }
}
