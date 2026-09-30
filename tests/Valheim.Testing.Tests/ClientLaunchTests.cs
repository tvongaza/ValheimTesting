using System.Buffers.Binary;
using Valheim.Testing.Game;
using Xunit;

public class ClientLaunchTests
{
    // Mach-O CPU types; the fake executables and libraries below carry real headers so slice detection is exercised.
    private const int X86_64 = 0x01000007, Arm64 = 0x0100000C;

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
        Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows));
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
        Assert.Contains("ServerLaunch", error.Message);
        foreach (var host in Enum.GetValues<ClientPlatform>())
            Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, host));
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
        Assert.Throws<PlatformNotSupportedException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, host));
    }
    [Theory] [InlineData(ClientPlatform.Linux)] [InlineData(ClientPlatform.MacOS)]
    public void ExecuteBitIsRequiredOnUnixHosts(ClientPlatform platform)
    {
        if (OperatingSystem.IsWindows()) return; // Real file modes are needed.
        using var install = Install.For(platform, executable: false);
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.RequireExecutable(install.Root, platform));
        Assert.Contains("chmod u+x", error.Message);
        Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, platform));
    }

    [Fact] public void WindowsLaunchAddsConsoleAndSteamAppIdAndNoLoaderVariables()
    {
        using var install = Install.Windows();
        var start = ClientLaunch.CreateStartInfo(install.Root, ["+connect", "127.0.0.1:2456", "C:/path with space"],
            new Dictionary<string, string> { ["TEST_TOKEN"] = "abc" }, ClientArchitecture.X64, true, ClientPlatform.Windows);
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
            ClientLaunch.CreateStartInfo(install.Root, ["-windowed", "-Console"], null, ClientArchitecture.X64, true, ClientPlatform.Windows).ArgumentList);
        Assert.Equal(new[] { "-windowed" },
            ClientLaunch.CreateStartInfo(install.Root, ["-windowed"], null, ClientArchitecture.X64, false, ClientPlatform.Windows).ArgumentList);
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
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows));
        Assert.Contains("doorstop_config.ini", error.Message);
    }
    [Fact] public void WindowsDoorstopEnabledOutsideItsSectionsDoesNotCount()
    {
        using var install = Install.Windows();
        install.Add("doorstop_config.ini", "[General]\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n[UnityMono]\nenabled = true\ndebug_enabled = true\n");
        Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows));
    }
    // The file BepInExPack_Valheim installs next to Doorstop 4.4's winhttp.dll still uses Doorstop 3's section and key names.
    [Fact] public void WindowsDoorstop3ConfigIsAccepted()
    {
        using var install = Install.Windows();
        install.Add("doorstop_config.ini", "[UnityDoorstop]\n# Specifies whether assembly executing is enabled\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\nredirectOutputLog=false\nignoreDisableSwitch=false\n# dllSearchPathOverride=\n");
        Assert.EndsWith("valheim.exe", ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows).FileName);
    }
    [Theory]
    [InlineData("[UnityDoorstop]\nenabled=false\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n")]
    [InlineData("[UnityDoorstop]\nenabled=true\n")]
    [InlineData("[General]\nenabled = true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n[UnityDoorstop]\nenabled=false\n")]
    public void WindowsDoorstopDisabledOrUntargetedInEitherFormatIsRefused(string config)
    {
        using var install = Install.Windows();
        install.Add("doorstop_config.ini", config);
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows));
        Assert.Contains("doorstop_config.ini", error.Message);
    }
    // An existing DLL is not enough: Doorstop would load it instead of BepInEx.
    [Fact] public void WindowsDoorstop3TargetMustBeBepInExsPreloader()
    {
        using var install = Install.Windows(); install.Add("BepInEx/core/Other.Preloader.dll");
        install.Add("doorstop_config.ini", "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\Other.Preloader.dll\n");
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows));
        Assert.Contains("Other.Preloader.dll", error.Message);
    }
    [Fact] public void WindowsDoorstopTargetMustBeBepInExsPreloader()
    {
        using var install = Install.Windows(); install.Add("BepInEx/core/Other.Preloader.dll");
        install.Add("doorstop_config.ini", DoorstopConfig("enabled = true", "target_assembly=BepInEx\\core\\Other.Preloader.dll"));
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.Windows));
        Assert.Contains("Other.Preloader.dll", error.Message);
    }
    // Set in this process, as a parent shell would: only the loader values ClientLaunch sets itself may reach the game.
    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)]
    public void InheritedLoaderVariablesNeverReachTheClient(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", "1");
        try { Assert.False(ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, platform).Environment.ContainsKey("DOORSTOP_DISABLE")); }
        finally { Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", null); }
    }

    [Fact] public void LinuxLaunchEnablesDoorstopAndPrependsToCallerLibraryPaths()
    {
        using var install = Install.Linux();
        var start = ClientLaunch.CreateStartInfo(install.Root, ["-windowed"], new Dictionary<string, string>
        {
            ["LD_LIBRARY_PATH"] = "/opt/extra/lib:/opt/other/lib", ["LD_PRELOAD"] = "libcaller.so", ["DISPLAY"] = ":0",
        }, ClientArchitecture.X64, true, ClientPlatform.Linux);
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
        var start = ClientLaunch.CreateStartInfo(install.Root, [], new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = "", ["LD_PRELOAD"] = "" },
            ClientArchitecture.X64, true, ClientPlatform.Linux);
        Assert.Equal(Path.Combine(install.Root, "doorstop_libs"), start.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal("libdoorstop_x64.so", start.Environment["LD_PRELOAD"]);
    }

    [Fact] public void MacLaunchStartsTheBundleThroughArchWithThePackLibrary()
    {
        using var install = Install.Mac();
        var start = ClientLaunch.CreateStartInfo(install.Root, ["-windowed"], null, ClientArchitecture.X64, true, ClientPlatform.MacOS);
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
        var start = ClientLaunch.CreateStartInfo(install.Root, [], new Dictionary<string, string>
        {
            ["DYLD_INSERT_LIBRARIES"] = "/opt/libcaller.dylib", ["DYLD_FRAMEWORK_PATH"] = "/opt/frameworks",
        }, ClientArchitecture.X64, true, ClientPlatform.MacOS);
        var arguments = start.ArgumentList.ToList();
        var before = arguments[..arguments.IndexOf(install.Executable)];
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "doorstop_libs", "libdoorstop_x64.dylib") + ":/opt/libcaller.dylib" },
            ExportPair(before, "DYLD_INSERT_LIBRARIES"));
        Assert.Equal(new[] { "-e", "DYLD_FRAMEWORK_PATH=/opt/frameworks" }, ExportPair(before, "DYLD_FRAMEWORK_PATH"));
        Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("DYLD_", StringComparison.Ordinal));
    }
    [Fact] public void MacArm64UsesTheUniversalLibraryAndX64KeepsThePackOne()
    {
        using var install = Install.Mac(universalDoorstop: true);
        var arm = ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS).ArgumentList.ToList();
        Assert.Equal("-arm64", arm[0]);
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "libdoorstop.dylib") }, ExportPair(arm, "DYLD_INSERT_LIBRARIES"));
        var x64 = ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.MacOS).ArgumentList.ToList();
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(install.Root, "doorstop_libs", "libdoorstop_x64.dylib") }, ExportPair(x64, "DYLD_INSERT_LIBRARIES"));
        Assert.Equal(new[] { ClientArchitecture.X64, ClientArchitecture.Arm64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }
    [Fact] public void MacArm64WithoutAnArm64DoorstopIsRefused()
    {
        using var install = Install.Mac();
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS));
        Assert.Contains("arm64 slice", error.Message);
        Assert.Contains("libdoorstop_x64.dylib: x86_64", error.Message);
        Assert.Contains("Rosetta", error.Message);
        Assert.Equal(new[] { ClientArchitecture.X64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }
    [Fact] public void MacGameWithoutTheRequestedSliceIsRefused()
    {
        using var install = Install.Mac(game: Thin(X86_64), universalDoorstop: true);
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS));
        Assert.Contains("has no arm64 slice (found: x86_64)", error.Message);
        Assert.Equal(new[] { ClientArchitecture.X64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }
    [Fact] public void MacDoorstopThatIsNotMachOIsRefused()
    {
        using var install = Install.Mac();
        install.Add("doorstop_libs/libdoorstop_x64.dylib", "fake");
        var error = Assert.Throws<InvalidOperationException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, ClientPlatform.MacOS));
        Assert.Contains("no x86_64 or arm64 Mach-O slice", error.Message);
        Assert.Empty(ClientLaunch.LaunchArchitectures(install.Root));
    }
    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)]
    public void Arm64IsRefusedForWindowsAndLinuxClients(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        Assert.Throws<ArgumentException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.Arm64, true, platform));
        Assert.Equal(new[] { ClientArchitecture.X64 }, ClientLaunch.LaunchArchitectures(install.Root));
    }

    [Theory]
    [InlineData(ClientPlatform.Windows, "DOORSTOP_DISABLE")] [InlineData(ClientPlatform.Windows, "doorstop_disable")]
    [InlineData(ClientPlatform.Linux, "DOORSTOP_ENABLED")] [InlineData(ClientPlatform.Linux, "DOORSTOP_TARGET_ASSEMBLY")]
    [InlineData(ClientPlatform.MacOS, "DOORSTOP_ENABLED")] [InlineData(ClientPlatform.MacOS, "DOORSTOP_DISABLE")]
    public void CallerCannotDisableOrRedirectTheLoader(ClientPlatform platform, string name)
    {
        using var install = Install.For(platform);
        Assert.Throws<ArgumentException>(() => ClientLaunch.CreateStartInfo(install.Root, [], new Dictionary<string, string> { [name] = "0" },
            ClientArchitecture.X64, true, platform));
    }
    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)] [InlineData(ClientPlatform.MacOS)]
    public void DoorstopArgumentsAreRefused(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        var error = Assert.Throws<ArgumentException>(() => ClientLaunch.CreateStartInfo(install.Root, ["--doorstop-enabled", "false"], null, ClientArchitecture.X64, true, platform));
        Assert.Contains("--doorstop-enabled", error.Message);
        Assert.Throws<ArgumentException>(() => ClientLaunch.CreateStartInfo(install.Root, ["-windowed", null!], null, ClientArchitecture.X64, true, platform));
    }
    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)] [InlineData(ClientPlatform.MacOS)]
    public void CallerSteamAppIdIsKept(ClientPlatform platform)
    {
        using var install = Install.For(platform);
        var start = ClientLaunch.CreateStartInfo(install.Root, [], new Dictionary<string, string> { ["SteamAppId"] = "123" }, ClientArchitecture.X64, true, platform);
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
        Assert.Throws<FileNotFoundException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, platform));
    }
    [Theory] [InlineData(ClientPlatform.Linux, "client:copy")] [InlineData(ClientPlatform.Linux, "client;copy")] [InlineData(ClientPlatform.MacOS, "client:copy")]
    public void InstallPathThatCannotBeListedIsRefused(ClientPlatform platform, string name)
    {
        if (OperatingSystem.IsWindows()) return; // Such names are not valid Windows paths.
        using var install = Install.For(platform, name: name);
        Assert.Throws<ArgumentException>(() => ClientLaunch.CreateStartInfo(install.Root, [], null, ClientArchitecture.X64, true, platform));
    }
    [Fact] public void PublicOverloadsUseTheCurrentHost()
    {
        var host = ClientLaunch.CurrentHost;
        using (var own = Install.For(host))
        {
            Assert.Equal(own.Executable, ClientLaunch.RequireExecutable(own.Root));
            Assert.Equal(own.Root, ClientLaunch.CreateStartInfo(own.Root, []).WorkingDirectory);
        }
        using var other = Install.For(host == ClientPlatform.Windows ? ClientPlatform.Linux : ClientPlatform.Windows);
        Assert.Throws<PlatformNotSupportedException>(() => ClientLaunch.CreateStartInfo(other.Root, []));
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

    private static string[] ExportPair(List<string> arguments, string name)
    {
        int value = arguments.FindIndex(argument => argument.StartsWith(name + "=", StringComparison.Ordinal));
        Assert.True(value > 0, name + " is not exported to arch");
        return [arguments[value - 1], arguments[value]];
    }
    private static string DoorstopConfig(string enabled, string target) =>
        $"# General options for Unity Doorstop\n[General]\n\n# Enable Doorstop?\n{enabled}\n\n{target}\n\n[UnityMono]\ndebug_enabled = false\n";
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

    private sealed class Install : IDisposable
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
            install.Add("winhttp.dll"); install.Add("doorstop_config.ini", DoorstopConfig("enabled = true", "target_assembly=BepInEx\\core\\BepInEx.Preloader.dll"));
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
        // A universal game with the pack's x64-only Doorstop library, optionally beside the universal one of BepInEx's macOS build.
        public static Install Mac(string name = "install", bool executable = true, string bundle = "Valheim.app", byte[]? game = null, bool universalDoorstop = false)
        {
            var install = new Install(name);
            install.Executable = install.Add(bundle + "/Contents/MacOS/Valheim", game ?? Fat(X86_64, Arm64));
            install.Add("doorstop_libs/libdoorstop_x64.dylib", Thin(X86_64));
            if (universalDoorstop) install.Add("libdoorstop.dylib", Fat(X86_64, Arm64));
            install.AddCore();
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
