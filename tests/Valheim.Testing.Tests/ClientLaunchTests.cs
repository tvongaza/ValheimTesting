using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using Valheim.Testing.Game;
using Xunit;

public class ClientLaunchTests
{
    // Mach-O CPU types; the fake executables and libraries below carry real headers so slice detection is exercised.
    internal const int X86_64 = 0x01000007, Arm64 = 0x0100000C;
    // BepInExPack_Valheim's core ships legacy MonoMod; the native Apple Silicon core, the reorganised MonoMod 25.
    internal static readonly Version LegacyDetour = new(22, 1, 29, 1), NativeDetour = new(25, 3, 4, 0);

    [Fact] public void CurrentHostFollowsTheOperatingSystem()
    {
        var expected = OperatingSystem.IsWindows() ? ClientPlatform.Windows : OperatingSystem.IsMacOS() ? ClientPlatform.MacOS : ClientPlatform.Linux;
        Assert.Equal(expected, ClientLaunch.CurrentHost);
    }
    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)] [InlineData(ClientPlatform.MacOS)]
    public void EachClientIsDetectedFromItsInstall(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        Assert.Equal(platform, ClientLaunch.Detect(install.Root));
        Assert.Equal(install.Executable, ClientLaunch.RequireExecutable(install.Root, platform));
    }
    [Fact] public void MacBundleNameMatchesWhateverItsCase()
    {
        using var install = Install.Mac(bundle: "valheim.app");
        Assert.Equal(ClientPlatform.MacOS, ClientLaunch.Detect(install.Root));
        Assert.Equal(install.Executable, ClientLaunch.RequireExecutable(install.Root, ClientPlatform.MacOS));
    }
    [Theory] [InlineData("valheim.x86_64")] [InlineData("Valheim.app/Contents/MacOS/Valheim")]
    public void InstallWithMoreThanOneClientIsRefused(string second)
    {
        using var install = Install.Windows(); install.Add(second);
        Assert.Throws<InvalidOperationException>(() => ClientLaunch.Detect(install.Root));
        Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo());
    }
    [Fact] public void EmptyOrMissingInstallIsRefused()
    {
        using var install = new Install();
        Assert.Throws<FileNotFoundException>(() => ClientLaunch.Detect(install.Root));
        Assert.Throws<DirectoryNotFoundException>(() => ClientLaunch.Detect(Path.Combine(install.Root, "missing")));
        Directory.CreateDirectory(Path.Combine(install.Root, "valheim.exe"));
        Assert.Throws<FileNotFoundException>(() => ClientLaunch.Detect(install.Root)); // a directory is not the executable
    }
    [Theory] [InlineData("valheim_server.exe")] [InlineData("valheim_server.x86_64")] [InlineData("valheim_server/Valheim")]
    public void DedicatedServerRuntimeIsRefusedWithAPointerToServerLaunch(string server)
    {
        using var install = new Install(); install.Add(server); install.Add("BepInEx/core/BepInEx.Preloader.dll");
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.Detect(install.Root));
        Assert.Contains("dedicated-server runtime", error.Message);
        Assert.Contains("GameLaunch.ForServer", error.Message);
        foreach (var host in Enum.GetValues<ClientPlatform>())
            Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, host).ToStartInfo());
    }
    [Fact] public void ClientExecutableWinsOverAServerBesideIt()
    {
        using var install = Install.Linux(); install.Add("valheim_server.x86_64");
        Assert.Equal(ClientPlatform.Linux, ClientLaunch.Detect(install.Root));
    }
    [Fact] public void MacBundleItselfIsRefusedWithTheDirectoryToPass()
    {
        using var install = Install.Mac();
        var error = Assert.Throws<ArgumentException>(() => ClientLaunch.Detect(Path.Combine(install.Root, "Valheim.app")));
        Assert.Contains("pass the directory that holds it", error.Message);
    }
    [Fact] public void MacBundleWithoutItsExecutableIsRefused()
    {
        using var install = Install.Mac();
        File.Delete(install.Executable);
        Assert.Equal(ClientPlatform.MacOS, ClientLaunch.Detect(install.Root));
        Assert.Throws<FileNotFoundException>(() => ClientLaunch.RequireExecutable(install.Root, ClientPlatform.MacOS));
    }

    [Theory]
    [InlineData(ClientPlatform.Windows, ClientPlatform.Linux)] [InlineData(ClientPlatform.Windows, ClientPlatform.MacOS)]
    [InlineData(ClientPlatform.Linux, ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux, ClientPlatform.MacOS)]
    [InlineData(ClientPlatform.MacOS, ClientPlatform.Windows)] [InlineData(ClientPlatform.MacOS, ClientPlatform.Linux)]
    public void ClientRunsOnlyOnItsOwnOperatingSystem(ClientPlatform platform, ClientPlatform host)
    {
        using var install = Install.For(platform, executable: false);
        // Detection reads the install's contents and works on any host, e.g. to check a copy before it is moved.
        Assert.Equal(platform, ClientLaunch.Detect(install.Root));
        // Refused before the execute bit is read, which would otherwise suggest chmod.
        var error = Assert.Throws<PlatformNotSupportedException>(() => ClientLaunch.RequireExecutable(install.Root, host));
        Assert.Contains($"This {host} host cannot run the {platform} Valheim client", error.Message);
        Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, host).ToStartInfo());
    }
    [Theory] [InlineData(ClientPlatform.Linux)] [InlineData(ClientPlatform.MacOS)]
    public void ExecuteBitIsRequiredOnUnixHosts(ClientPlatform platform)
    {
        if (OperatingSystem.IsWindows()) return; // Real file modes are needed.
        using var install = Install.For(platform, executable: false);
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.RequireExecutable(install.Root, platform));
        Assert.Contains("chmod u+x", error.Message);
        Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, platform).ToStartInfo());
    }

    [Fact] public void WindowsLaunchAddsConsoleAndSteamAppIdAndNoLoaderVariables()
    {
        using var install = Install.Windows();
        var start = GameLaunch.LocalClient(install.Root, ["+connect", "127.0.0.1:2456", "C:/path with space"], new Dictionary<string, string> { ["TEST_TOKEN"] = "abc" }, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo();
        Assert.Equal(install.Executable, start.FileName);
        Assert.Equal(install.Root, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "-console", "+connect", "127.0.0.1:2456", "C:/path with space" }, start.ArgumentList);
        Assert.Equal("abc", start.Environment["TEST_TOKEN"]);
        Assert.Equal("892970", start.Environment["SteamAppId"]);
        // Windows loads BepInEx through winhttp.dll and doorstop_config.ini, not the environment.
        Assert.False(start.Environment.ContainsKey("DOORSTOP_ENABLED"));
        Assert.False(start.Environment.ContainsKey("DOORSTOP_TARGET_ASSEMBLY"));
        Assert.False(start.Environment.ContainsKey("DOORSTOP_DISABLE"));
    }
    [Fact] public void ConsoleIsAddedOnceAndCanBeLeftOut()
    {
        using var install = Install.Windows();
        Assert.Equal(new[] { "-windowed", "-Console" },
            GameLaunch.LocalClient(install.Root, ["-windowed", "-Console"], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo().ArgumentList);
        Assert.Equal(new[] { "-windowed" },
            GameLaunch.LocalClient(install.Root, ["-windowed"], null, ClientArchitecture.X64, false, ClientPlatform.Windows).ToStartInfo().ArgumentList);
    }
    [Theory]
    [InlineData("enabled = false", "target_assembly=BepInEx\\core\\BepInEx.Preloader.dll")]
    [InlineData("enabled = 1", "target_assembly=BepInEx\\core\\BepInEx.Preloader.dll")]
    [InlineData("", "target_assembly=BepInEx\\core\\BepInEx.Preloader.dll")]
    [InlineData("enabled = true", "")]
    [InlineData("# enabled = true", "target_assembly=BepInEx\\core\\BepInEx.Preloader.dll")]
    public void WindowsDoorstopConfigMustEnableTheLoader(string enabled, string target)
    {
        using var install = Install.Windows();
        install.Add("doorstop_config.ini", DoorstopConfig(enabled, target));
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo());
        Assert.Contains("doorstop_config.ini", error.Message);
    }
    [Fact] public void WindowsDoorstopEnabledOutsideItsSectionsDoesNotCount()
    {
        using var install = Install.Windows();
        install.Add("doorstop_config.ini", "[General]\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n[UnityMono]\nenabled = true\ndebug_enabled = true\n");
        Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo());
    }
    // The Doorstop 3 layout (a Doorstop 3.4 proxy holding targetAssembly beside a [UnityDoorstop] file, as in the reviewed
    // loader package). BepInExPack_Valheim 5.4.2333 is the Doorstop 4 layout: a 4.4 proxy holding target_assembly beside [General].
    [Fact] public void WindowsDoorstop3ConfigIsAccepted()
    {
        using var install = Install.Windows();
        install.Add("doorstop_config.ini", "[UnityDoorstop]\n# Specifies whether assembly executing is enabled\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\nredirectOutputLog=false\nignoreDisableSwitch=false\n# dllSearchPathOverride=\n");
        install.Add("winhttp.dll", "MZ targetAssembly"); // Doorstop 3's own proxy
        Assert.EndsWith("valheim.exe", GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo().FileName);
    }
    [Theory]
    [InlineData("[UnityDoorstop]\nenabled=false\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n")]
    [InlineData("[UnityDoorstop]\nenabled=true\n")]
    [InlineData("[General]\nenabled = true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n[UnityDoorstop]\nenabled=false\n")]
    public void WindowsDoorstopDisabledOrUntargetedInEitherFormatIsRefused(string config)
    {
        using var install = Install.Windows();
        install.Add("doorstop_config.ini", config);
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo());
        Assert.Contains("doorstop_config.ini", error.Message);
    }
    // An existing DLL is not enough: Doorstop would load it instead of BepInEx.
    [Fact] public void WindowsDoorstop3TargetMustBeBepInExsPreloader()
    {
        using var install = Install.Windows(); install.Add("BepInEx/core/Other.Preloader.dll");
        install.Add("doorstop_config.ini", "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\Other.Preloader.dll\n");
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo());
        Assert.Contains("Other.Preloader.dll", error.Message);
    }
    [Fact] public void WindowsDoorstopTargetMustBeBepInExsPreloader()
    {
        using var install = Install.Windows(); install.Add("BepInEx/core/Other.Preloader.dll");
        install.Add("doorstop_config.ini", DoorstopConfig("enabled = true", "target_assembly=BepInEx\\core\\Other.Preloader.dll"));
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ToStartInfo());
        Assert.Contains("Other.Preloader.dll", error.Message);
    }
    // Set in this process, as a parent shell would: only the loader values ClientLaunch sets itself may reach the game.
    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)]
    public void InheritedLoaderVariablesNeverReachTheClient(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", "1");
        try { Assert.False(GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, platform).ToStartInfo().Environment.ContainsKey("DOORSTOP_DISABLE")); }
        finally { Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", null); }
    }

    [Fact] public void LinuxLaunchEnablesDoorstopAndPrependsToCallerLibraryPaths()
    {
        using var install = Install.Linux();
        var start = GameLaunch.LocalClient(install.Root, ["-windowed"], new Dictionary<string, string>
        {
            ["LD_LIBRARY_PATH"] = "/opt/extra/lib:/opt/other/lib", ["LD_PRELOAD"] = "libcaller.so", ["DISPLAY"] = ":0",
        }, ClientArchitecture.X64, true, ClientPlatform.Linux).ToStartInfo();
        Assert.Equal(install.Executable, start.FileName);
        Assert.Equal(install.Root, start.WorkingDirectory);
        Assert.Equal(new[] { "-console", "-windowed" }, start.ArgumentList);
        Assert.Equal("1", start.Environment["DOORSTOP_ENABLED"]);
        Assert.Equal(Path.Combine(install.Root, "BepInEx", "core", "BepInEx.Preloader.dll"), start.Environment["DOORSTOP_TARGET_ASSEMBLY"]);
        // The game script adds only doorstop_libs; linux64 is the dedicated server's addition.
        Assert.Equal(Path.Combine(install.Root, "doorstop_libs") + ":/opt/extra/lib:/opt/other/lib", start.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal("libdoorstop_x64.so:libcaller.so", start.Environment["LD_PRELOAD"]);
        Assert.Equal(":0", start.Environment["DISPLAY"]);
        Assert.Equal("892970", start.Environment["SteamAppId"]);
    }
    [Fact] public void EmptyLibraryPathsAddNoEmptyEntry()
    {
        using var install = Install.Linux();
        var start = GameLaunch.LocalClient(install.Root, [], new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = "", ["LD_PRELOAD"] = "" }, ClientArchitecture.X64, true, ClientPlatform.Linux).ToStartInfo();
        Assert.Equal(Path.Combine(install.Root, "doorstop_libs"), start.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal("libdoorstop_x64.so", start.Environment["LD_PRELOAD"]);
    }

    [Fact] public void MacLaunchStartsTheBundleThroughArchWithThePackLibrary()
    {
        using var install = Install.Mac();
        var start = GameLaunch.LocalClient(install.Root, ["-windowed"], null, ClientArchitecture.X64, true, ClientPlatform.MacOS).ToStartInfo();
        Assert.Equal("/usr/bin/arch", start.FileName);
        Assert.Equal(install.Root, start.WorkingDirectory);
        var arguments = start.ArgumentList.ToList();
        int game = arguments.IndexOf(install.Executable);
        Assert.Equal("-x86_64", arguments[0]);
        Assert.Equal(new[] { "-console", "-windowed" }, arguments[(game + 1)..]);
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "doorstop_libs", "libdoorstop_x64.dylib") },
            ExportPair(arguments[..game], "DYLD_INSERT_LIBRARIES"));
        // arch would drop DYLD_* from its own environment; they reach the game only through -e.
        Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("DYLD_", StringComparison.Ordinal));
        Assert.Equal("1", start.Environment["DOORSTOP_ENABLED"]);
        Assert.Equal(Path.Combine(install.Root, "BepInEx", "core", "BepInEx.Preloader.dll"), start.Environment["DOORSTOP_TARGET_ASSEMBLY"]);
        Assert.Equal("892970", start.Environment["SteamAppId"]);
    }
    [Fact] public void MacCallerDyldVariablesAreForwardedThroughArch()
    {
        using var install = Install.Mac();
        var start = GameLaunch.LocalClient(install.Root, [], new Dictionary<string, string>
        {
            ["DYLD_INSERT_LIBRARIES"] = "/opt/libcaller.dylib", ["DYLD_FRAMEWORK_PATH"] = "/opt/frameworks",
        }, ClientArchitecture.X64, true, ClientPlatform.MacOS).ToStartInfo();
        var arguments = start.ArgumentList.ToList();
        var before = arguments[..arguments.IndexOf(install.Executable)];
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "doorstop_libs", "libdoorstop_x64.dylib") + ":/opt/libcaller.dylib" },
            ExportPair(before, "DYLD_INSERT_LIBRARIES"));
        Assert.Equal(new[] { "-e", "DYLD_FRAMEWORK_PATH=/opt/frameworks" }, ExportPair(before, "DYLD_FRAMEWORK_PATH"));
        Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("DYLD_", StringComparison.Ordinal));
    }
    [Fact] public void MacArm64UsesTheUniversalLibraryAndX64KeepsThePackOne()
    {
        using var install = Install.Mac(universalDoorstop: true, core: NativeDetour);
        var arm = GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS).ToStartInfo().ArgumentList.ToList();
        Assert.Equal("-arm64", arm[0]);
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "libdoorstop.dylib") }, ExportPair(arm, "DYLD_INSERT_LIBRARIES"));
        var x64 = GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.MacOS).ToStartInfo().ArgumentList.ToList();
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "doorstop_libs", "libdoorstop_x64.dylib") }, ExportPair(x64, "DYLD_INSERT_LIBRARIES"));
        Assert.Equal(new[] { ClientArchitecture.X64, ClientArchitecture.Arm64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }
    [Fact] public void MacArm64WithoutAnArm64DoorstopIsRefused()
    {
        using var install = Install.Mac();
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS).ToStartInfo());
        Assert.Contains("arm64 slice", error.Message);
        Assert.Contains("libdoorstop_x64.dylib: x86_64", error.Message);
        Assert.Contains("UnityDoorstop 4.5 or later, universal or arm64-only", error.Message);
        Assert.Contains("request x64 to run under Rosetta", error.Message);
        Assert.Equal(new[] { ClientArchitecture.X64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }
    // What a native install looks like: an arm64-only (or universal) Doorstop at the root, the pack's x64 library removed.
    [Fact] public void MacArm64OnlyDoorstopLaunchesNativelyAndRefusesX64()
    {
        using var install = Install.Mac(packDoorstop: false, arm64Doorstop: true, core: NativeDetour);
        var arm = GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS).ToStartInfo().ArgumentList.ToList();
        Assert.Equal("-arm64", arm[0]);
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "libdoorstop.dylib") }, ExportPair(arm, "DYLD_INSERT_LIBRARIES"));
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.MacOS).ToStartInfo());
        Assert.Contains("No Doorstop library in the install has an x86_64 slice (libdoorstop.dylib: arm64)", error.Message);
        Assert.Equal(new[] { ClientArchitecture.Arm64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }
    [Fact] public void MacArm64OnlyDoorstopBesideThePackLibraryServesEachArchitecture()
    {
        using var install = Install.Mac(arm64Doorstop: true, core: NativeDetour);
        var arm = GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS).ToStartInfo().ArgumentList.ToList();
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "libdoorstop.dylib") }, ExportPair(arm, "DYLD_INSERT_LIBRARIES"));
        var x64 = GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.MacOS).ToStartInfo().ArgumentList.ToList();
        Assert.Equal("-x86_64", x64[0]);
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "doorstop_libs", "libdoorstop_x64.dylib") }, ExportPair(x64, "DYLD_INSERT_LIBRARIES"));
        Assert.Equal(new[] { ClientArchitecture.X64, ClientArchitecture.Arm64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }
    // The pack's core with an arm64 Doorstop: dyld would load it natively and the first Harmony patch would fail, so it never starts.
    [Fact] public void MacArm64WithTheLegacyMonoModCoreIsRefusedAndX64StillLaunches()
    {
        using var install = Install.Mac(universalDoorstop: true, core: LegacyDetour);
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS).ToStartInfo());
        Assert.Contains("MonoMod.RuntimeDetour.dll is version 22.1.29.1", error.Message);
        Assert.Contains("MonoMod before 25 cannot apply Harmony hooks on arm64", error.Message);
        Assert.Contains("request x64 to run under Rosetta", error.Message);
        Assert.Equal(new[] { ClientArchitecture.X64 }, ClientLaunch.LaunchArchitectures(install.Root));
        Assert.Equal("-x86_64", GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.MacOS).ToStartInfo().ArgumentList[0]);
    }
    [Fact] public void MacArm64WithoutAReadableMonoModCoreIsRefused()
    {
        using var install = Install.Mac(universalDoorstop: true);
        var missing = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS).ToStartInfo());
        Assert.Contains("has no " + Path.Combine("BepInEx", "core", "MonoMod.RuntimeDetour.dll"), missing.Message);
        install.Add("BepInEx/core/MonoMod.RuntimeDetour.dll", "fake");
        var unreadable = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS).ToStartInfo());
        Assert.Contains("MonoMod.RuntimeDetour.dll is not a .NET assembly", unreadable.Message);
        Assert.Equal(new[] { ClientArchitecture.X64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }
    // Unchanged by the native path: an x64 launch reads no MonoMod version, whatever core the install has.
    [Fact] public void MacX64NeverReadsTheCoresMonoModVersion()
    {
        using var install = Install.Mac(universalDoorstop: true);
        install.Add("BepInEx/core/MonoMod.RuntimeDetour.dll", "fake");
        var start = GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.MacOS).ToStartInfo();
        Assert.Equal("-x86_64", start.ArgumentList[0]);
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "doorstop_libs", "libdoorstop_x64.dylib") }, ExportPair(start.ArgumentList.ToList(), "DYLD_INSERT_LIBRARIES"));
    }
    [Fact] public void MacGameWithoutTheRequestedSliceIsRefused()
    {
        using var install = Install.Mac(game: Thin(X86_64), universalDoorstop: true, core: NativeDetour);
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS).ToStartInfo());
        Assert.Contains("has no arm64 slice (found: x86_64)", error.Message);
        Assert.Equal(new[] { ClientArchitecture.X64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }
    [Fact] public void MacDoorstopThatIsNotMachOIsRefused()
    {
        using var install = Install.Mac();
        install.Add("doorstop_libs/libdoorstop_x64.dylib", "fake");
        var error = Assert.Throws<InvalidOperationException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.MacOS).ToStartInfo());
        Assert.Contains("no x86_64 or arm64 Mach-O slice", error.Message);
        Assert.Empty(ClientLaunch.LaunchArchitectures(install.Root));
    }
    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)]
    public void Arm64IsRefusedForWindowsAndLinuxClients(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        Assert.Throws<ArgumentException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.Arm64, true, platform).ToStartInfo());
        Assert.Equal(new[] { ClientArchitecture.X64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }

    [Theory]
    [InlineData(ClientPlatform.Windows, "DOORSTOP_DISABLE")] [InlineData(ClientPlatform.Windows, "doorstop_disable")]
    [InlineData(ClientPlatform.Linux, "DOORSTOP_ENABLED")] [InlineData(ClientPlatform.Linux, "DOORSTOP_TARGET_ASSEMBLY")]
    [InlineData(ClientPlatform.MacOS, "DOORSTOP_ENABLED")] [InlineData(ClientPlatform.MacOS, "DOORSTOP_DISABLE")]
    public void CallerCannotDisableOrRedirectTheLoader(ClientPlatform platform, string name)
    {
        using var install = Install.For(platform);
        Assert.Throws<ArgumentException>(() => GameLaunch.LocalClient(install.Root, [], new Dictionary<string, string> { [name] = "0" }, ClientArchitecture.X64, true, platform).ToStartInfo());
    }
    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)] [InlineData(ClientPlatform.MacOS)]
    public void DoorstopArgumentsAreRefused(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        var error = Assert.Throws<ArgumentException>(() => GameLaunch.LocalClient(install.Root, ["--doorstop-enabled", "false"], null, ClientArchitecture.X64, true, platform).ToStartInfo());
        Assert.Contains("--doorstop-enabled", error.Message);
        Assert.Throws<ArgumentException>(() => GameLaunch.LocalClient(install.Root, ["-windowed", null!], null, ClientArchitecture.X64, true, platform).ToStartInfo());
    }
    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)] [InlineData(ClientPlatform.MacOS)]
    public void CallerSteamAppIdIsKept(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        var start = GameLaunch.LocalClient(install.Root, [], new Dictionary<string, string> { ["SteamAppId"] = "123" }, ClientArchitecture.X64, true, platform).ToStartInfo();
        Assert.Equal("123", start.Environment["SteamAppId"]);
    }
    [Theory]
    [InlineData(ClientPlatform.Windows, "BepInEx/core/BepInEx.Preloader.dll")] [InlineData(ClientPlatform.Windows, "BepInEx/core/BepInEx.dll")]
    [InlineData(ClientPlatform.Windows, "winhttp.dll")] [InlineData(ClientPlatform.Windows, "doorstop_config.ini")]
    [InlineData(ClientPlatform.Linux, "BepInEx/core/BepInEx.Preloader.dll")] [InlineData(ClientPlatform.Linux, "doorstop_libs/libdoorstop_x64.so")]
    [InlineData(ClientPlatform.MacOS, "BepInEx/core/BepInEx.Preloader.dll")] [InlineData(ClientPlatform.MacOS, "doorstop_libs/libdoorstop_x64.dylib")]
    public void MissingBepInExLoaderIsRefusedRatherThanStartingVanilla(ClientPlatform platform, string missing)
    {
        using var install = Install.For(platform);
        File.Delete(Path.Combine(install.Root, missing));
        Assert.Throws<FileNotFoundException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, platform).ToStartInfo());
    }
    [Theory] [InlineData(ClientPlatform.Linux, "client:copy")] [InlineData(ClientPlatform.Linux, "client;copy")] [InlineData(ClientPlatform.MacOS, "client:copy")]
    public void InstallPathThatCannotBeListedIsRefused(ClientPlatform platform, string name)
    {
        if (OperatingSystem.IsWindows()) return; // Such names are not valid Windows paths.
        using var install = Install.For(platform, name: name);
        Assert.Throws<ArgumentException>(() => GameLaunch.LocalClient(install.Root, [], null, ClientArchitecture.X64, true, platform).ToStartInfo());
    }
    [Fact] public void PublicOverloadsUseTheCurrentHost()
    {
        var host = ClientLaunch.CurrentHost;
        using (var own = Install.For(host))
        {
            Assert.Equal(own.Executable, ClientLaunch.RequireExecutable(own.Root));
            Assert.Equal(own.Root, GameLaunch.ForClient(own.Root, []).ToStartInfo().WorkingDirectory);
        }
        using var other = Install.For(host == ClientPlatform.Windows ? ClientPlatform.Linux : ClientPlatform.Windows);
        Assert.Throws<PlatformNotSupportedException>(() => GameLaunch.ForClient(other.Root, []).ToStartInfo());
    }

    [Fact] public void MachOSlicesAreReadFromThinAndUniversalImages()
    {
        using var install = new Install();
        Assert.Equal(new[] { ClientArchitecture.X64 }, ClientLaunch.MachOArchitectures(install.Add("thin", Thin(X86_64))));
        Assert.Equal(new[] { ClientArchitecture.Arm64 }, ClientLaunch.MachOArchitectures(install.Add("arm", Thin(Arm64))));
        Assert.Equal(new[] { ClientArchitecture.X64, ClientArchitecture.Arm64 }, ClientLaunch.MachOArchitectures(install.Add("fat", Fat(X86_64, Arm64))).Order());
        Assert.Empty(ClientLaunch.MachOArchitectures(install.Add("text", "fake")));
        Assert.Empty(ClientLaunch.MachOArchitectures(install.Add("short", new byte[] { 0xCA, 0xFE })));
        // Java class files share the universal magic; their version field reads as a huge slice count.
        Assert.Empty(ClientLaunch.MachOArchitectures(install.Add("class", new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x41 })));
    }

    internal static string[] ExportPair(List<string> arguments, string name)
    {
        int value = arguments.FindIndex(argument => argument.StartsWith(name + "=", StringComparison.Ordinal));
        Assert.True(value > 0, name + " is not exported to arch");
        return [arguments[value - 1], arguments[value]];
    }
    private static string DoorstopConfig(string enabled, string target) =>
        $"# General options for Unity Doorstop\n[General]\n\n# Enable Doorstop?\n{enabled}\n\n{target}\n\n[UnityMono]\ndebug_enabled = false\n";
    internal static byte[] Thin(int cpu)
    {
        var image = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0xFEEDFACF);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(4), cpu);
        return image;
    }
    // ---- GameLaunch.ForClient: one builder for this machine and for a host ----

    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)]
    public void ALaunchHereAndALaunchOnAHostOfOnePlatformFollowTheSameRules(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        var environment = new Dictionary<string, string> { ["MY_MOD_TOKEN"] = "abc" };
        var here = GameLaunch.LocalClient(install.Root, ["+connect", "a b"], environment, ClientArchitecture.X64, true, platform);
        var there = GameLaunch.ForClient(platform == ClientPlatform.Windows ? @"C:\Games\Valheim" : "/home/steam/valheim", ["+connect", "a b"], environment, platform,
            secretVariables: ["VT_JOIN_PASSWORD"]);
        Assert.Equal(new[] { "-console", "+connect", "a b" }, there.Arguments);
        Assert.Equal(here.Arguments, there.Arguments);
        Assert.Equal(here.Environment.Keys.Order(StringComparer.Ordinal), there.Environment.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(ClientLaunch.GameSteamAppId, there.Environment["SteamAppId"]);
        Assert.Equal(here.Environment["SteamAppId"], there.Environment["SteamAppId"]);
        Assert.Equal(here.Prepended.Keys.Order(StringComparer.Ordinal), there.Prepended.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(here.Unset, there.Unset);
        Assert.Equal(here.RequiredFiles, there.RequiredFiles);
        Assert.Equal(new[] { "VT_JOIN_PASSWORD" }, there.SecretVariables);
        // This machine's launch inherits this process's environment, so it names no secret.
        Assert.Empty(here.SecretVariables);
        Assert.Throws<ArgumentException>(() => GameLaunch.ForClient(install.Root, [], secretVariables: ["VT_JOIN_PASSWORD"]));
        Assert.Throws<InvalidOperationException>(() => there.ToStartInfo());
        Assert.Equal(here.Executable, here.ToStartInfo().FileName);
        Assert.False(here.ToStartInfo().CreateNoWindow);
    }

    [Fact] public void AHostClientLaunchKeepsTheHostsRules()
    {
        // Only macOS has another slice, and a macOS client cannot be started from another machine.
        Assert.Throws<ArgumentException>(() => GameLaunch.ForClient("/home/steam/valheim", [], hostPlatform: ClientPlatform.Linux, architecture: ClientArchitecture.Arm64));
        // The client runs with the user's own credentials in the desktop session, so a share is an install.
        Assert.Equal(@"\\nas\games\Valheim\valheim.exe", GameLaunch.ForClient(@"\\nas\games\Valheim", [], hostPlatform: ClientPlatform.Windows).Executable);
        // A Windows client's arguments are one quoted command line: a line break fits, NUL never does.
        Assert.Equal("two\nlines", GameLaunch.ForClient(@"C:\Games\Valheim", ["two\nlines"], hostPlatform: ClientPlatform.Windows).Arguments[1]);
        Assert.Throws<ArgumentException>(() => GameLaunch.ForClient(@"C:\Games\Valheim", ["nul\0"], hostPlatform: ClientPlatform.Windows));
        Assert.Throws<ArgumentException>(() => GameLaunch.ForClient(@"C:\Games\""Valheim""", [], hostPlatform: ClientPlatform.Windows));
        Assert.Throws<ArgumentException>(() => GameLaunch.ForClient("/home/steam/valheim", [], new Dictionary<string, string> { ["NOT A NAME"] = "x" }, ClientPlatform.Linux));
        // A Windows install on a Linux host is a bad path for a client, as it always was (a server's says the platform is wrong).
        Assert.Throws<ArgumentException>(() => GameLaunch.ForClient(@"C:\Games\Valheim", [], hostPlatform: ClientPlatform.Linux));
        // On Linux the executable is what the start script runs, "$install/$exe", so the journalled command line matches the process.
        var launch = GameLaunch.ForClient("/home/steam/valheim/", [], hostPlatform: ClientPlatform.Linux);
        Assert.Equal(launch.WorkingDirectory + "/" + ClientLaunch.LinuxExecutable, launch.Executable);
    }

    internal static byte[] Fat(params int[] cpus)
    {
        var image = new byte[8 + cpus.Length * 20];
        BinaryPrimitives.WriteUInt32BigEndian(image, 0xCAFEBABE);
        BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(4), cpus.Length);
        for (int i = 0; i < cpus.Length; i++) BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(8 + i * 20), cpus[i]);
        return image;
    }

    // A .NET assembly with only a name and version, as BepInEx's core holds MonoMod.RuntimeDetour.dll; nothing loads it.
    internal static byte[] ManagedAssembly(string name, Version version)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = version }, typeof(object).Assembly);
        assembly.DefineDynamicModule(name).DefineType("Marker", TypeAttributes.Public).CreateType();
        using var image = new MemoryStream();
        assembly.Save(image);
        return image.ToArray();
    }

    internal sealed class Install : IDisposable
    {
        private readonly string _parent = Path.Combine(Path.GetTempPath(), "client-launch-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Executable { get; private set; } = "";
        public Install(string name = "install") { Root = Path.Combine(_parent, name); Directory.CreateDirectory(Root); }
        public string Add(string relative, string content = "fake") => Add(relative, System.Text.Encoding.UTF8.GetBytes(content));
        public string Add(string relative, byte[] content)
        {
            string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content); return path;
        }
        public static Install For(ClientPlatform platform, bool executable = true, string name = "install") => platform switch
        {
            ClientPlatform.Windows => Windows(name),
            ClientPlatform.Linux => Linux(name, executable),
            _ => Mac(name, executable),
        };
        public static Install Windows(string name = "install")
        {
            var install = new Install(name);
            install.Executable = install.Add("valheim.exe");
            install.Add("winhttp.dll", "MZ target_assembly"); install.Add("doorstop_config.ini", DoorstopConfig("enabled = true", "target_assembly=BepInEx\\core\\BepInEx.Preloader.dll"));
            install.AddCore();
            return install;
        }
        public static Install Linux(string name = "install", bool executable = true)
        {
            var install = new Install(name);
            install.Executable = install.Add("valheim.x86_64");
            install.Add("doorstop_libs/libdoorstop_x64.so"); install.AddCore();
            install.SetExecutable(executable);
            return install;
        }
        // A universal game with the pack's x64-only Doorstop library (unless packDoorstop is false), optionally with a universal
        // or arm64-only libdoorstop.dylib at the root, and a core whose MonoMod.RuntimeDetour.dll has the given version.
        public static Install Mac(string name = "install", bool executable = true, string bundle = "Valheim.app", byte[]? game = null, bool universalDoorstop = false,
            bool packDoorstop = true, bool arm64Doorstop = false, Version? core = null)
        {
            var install = new Install(name);
            install.Executable = install.Add(bundle + "/Contents/MacOS/Valheim", game ?? Fat(X86_64, Arm64));
            if (packDoorstop) install.Add("doorstop_libs/libdoorstop_x64.dylib", Thin(X86_64));
            if (universalDoorstop) install.Add("libdoorstop.dylib", Fat(X86_64, Arm64));
            if (arm64Doorstop) install.Add("libdoorstop.dylib", Thin(Arm64));
            install.AddCore();
            if (core != null) install.Add("BepInEx/core/MonoMod.RuntimeDetour.dll", ManagedAssembly("MonoMod.RuntimeDetour", core));
            install.SetExecutable(executable);
            return install;
        }
        private void AddCore() { Add("BepInEx/core/BepInEx.Preloader.dll"); Add("BepInEx/core/BepInEx.dll"); }
        private void SetExecutable(bool executable)
        {
            if (OperatingSystem.IsWindows()) return;
            var mode = File.GetUnixFileMode(Executable);
            File.SetUnixFileMode(Executable, executable ? mode | UnixFileMode.UserExecute : mode & ~UnixFileMode.UserExecute);
        }
        public void Dispose() => Directory.Delete(_parent, true);
    }
}
