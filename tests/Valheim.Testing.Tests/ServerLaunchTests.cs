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
        Assert.Equal(expected, ServerLaunch.CurrentHost);
    }
    [Fact] public void WindowsRuntimeIsDetectedFromItsExecutable()
    {
        using var runtime = Runtime.Windows();
        Assert.Equal(ServerPlatform.Windows, ServerLaunch.Detect(runtime.Root));
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.exe"), ServerLaunch.RequireExecutable(runtime.Root, ServerHost.Windows));
    }
    [Fact] public void LinuxRuntimeIsDetectedFromItsExecutable()
    {
        using var runtime = Runtime.Linux();
        Assert.Equal(ServerPlatform.Linux, ServerLaunch.Detect(runtime.Root));
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.x86_64"), ServerLaunch.RequireExecutable(runtime.Root, LinuxLaunchHost));
    }
    [Fact] public void RuntimeWithBothExecutablesIsRefused()
    {
        using var runtime = Runtime.Linux(); runtime.Add("valheim_server.exe");
        Assert.Throws<InvalidOperationException>(() => ServerLaunch.Detect(runtime.Root));
        Assert.Throws<InvalidOperationException>(() => ServerLaunch.CreateStartInfo(runtime.Root, []));
    }
    [Fact] public void RuntimeWithoutAServerIsRefused()
    {
        using var runtime = new Runtime(); runtime.Add("valheim.exe"); runtime.Add("valheim.x86_64");
        var error = Assert.Throws<FileNotFoundException>(() => ServerLaunch.Detect(runtime.Root));
        Assert.Contains("launch it with ClientLaunch", error.Message);
        Assert.Throws<DirectoryNotFoundException>(() => ServerLaunch.Detect(Path.Combine(runtime.Root, "missing")));
    }
    [Fact] public void DirectoryNamedLikeTheExecutableIsNotAServer()
    {
        using var runtime = new Runtime(); Directory.CreateDirectory(Path.Combine(runtime.Root, "valheim_server.x86_64"));
        Assert.Throws<FileNotFoundException>(() => ServerLaunch.Detect(runtime.Root));
    }
    [Fact] public void ExecuteBitIsRequiredExceptOnWindowsHosts()
    {
        using var runtime = Runtime.Linux(executable: false);
        string executable = Path.Combine(runtime.Root, "valheim_server.x86_64");
        // No Unix mode exists to read on a Windows host; the file's presence is the whole check.
        Assert.Equal(executable, ServerLaunch.RequireExecutable(runtime.Root, ServerHost.Windows));
        if (OperatingSystem.IsWindows()) return; // Real file modes are needed for the Linux-host arm.
        Assert.Throws<InvalidOperationException>(() => ServerLaunch.RequireExecutable(runtime.Root, ServerHost.Linux));
        Assert.Throws<InvalidOperationException>(() => ServerLaunch.CreateStartInfo(runtime.Root, [], null, ServerHost.Linux));
        File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        Assert.Equal(executable, ServerLaunch.RequireExecutable(runtime.Root, ServerHost.Linux));
    }
    [Fact] public void WindowsLaunchUsesRuntimeAndCallerSettingsWithoutLinuxLoader()
    {
        using var runtime = Runtime.Windows();
        var start = ServerLaunch.CreateStartInfo(runtime.Root, ["-batchmode", "-nographics", "-savedir", "C:/saves with space"],
            new Dictionary<string, string> { ["TEST_TOKEN"] = "abc" }, ServerHost.Windows);
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
        var start = ServerLaunch.CreateStartInfo(runtime.Root, ["-batchmode", "-nographics"], new Dictionary<string, string>
        {
            ["LD_LIBRARY_PATH"] = "/opt/extra/lib:/opt/other/lib", ["LD_PRELOAD"] = "libcaller.so", ["TEST_TOKEN"] = "abc",
        }, LinuxLaunchHost);
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
        var start = ServerLaunch.CreateStartInfo(runtime.Root, [], new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = "", ["LD_PRELOAD"] = "" }, LinuxLaunchHost);
        // A trailing ':' would add the working directory to the search path.
        Assert.Equal(Path.Combine(runtime.Root, "linux64") + ":" + Path.Combine(runtime.Root, "doorstop_libs"), start.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal("libdoorstop_x64.so", start.Environment["LD_PRELOAD"]);
    }
    [Fact] public void CallerSteamAppIdIsKept()
    {
        using var runtime = Runtime.Linux();
        var start = ServerLaunch.CreateStartInfo(runtime.Root, [], new Dictionary<string, string> { ["SteamAppId"] = "123" }, LinuxLaunchHost);
        Assert.Equal("123", start.Environment["SteamAppId"]);
    }
    [Theory]
    [InlineData("linux", "DOORSTOP_ENABLED")] [InlineData("linux", "DOORSTOP_TARGET_ASSEMBLY")] [InlineData("linux", "DOORSTOP_DISABLE")]
    [InlineData("windows", "DOORSTOP_DISABLE")] [InlineData("windows", "doorstop_enabled")] [InlineData("windows", "DOORSTOP_TARGET_ASSEMBLY")]
    public void CallerCannotDisableOrRedirectTheLoader(string platform, string name)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        Assert.Throws<ArgumentException>(() => ServerLaunch.CreateStartInfo(runtime.Root, [], new Dictionary<string, string> { [name] = "0" },
            platform == "linux" ? LinuxLaunchHost : ServerHost.Windows));
    }
    [Theory] [InlineData("linux")] [InlineData("windows")]
    public void DoorstopArgumentsAreRefused(string platform)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        var error = Assert.Throws<ArgumentException>(() => ServerLaunch.CreateStartInfo(runtime.Root, ["-batchmode", "--doorstop-enabled", "false"], null,
            platform == "linux" ? LinuxLaunchHost : ServerHost.Windows));
        Assert.Contains("--doorstop-enabled", error.Message);
    }
    // Set in this process, as a parent shell would: only the loader values ServerLaunch sets itself may reach the server.
    [Theory] [InlineData("linux")] [InlineData("windows")]
    public void InheritedLoaderVariablesNeverReachTheServer(string platform)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", "1");
        try
        {
            var start = ServerLaunch.CreateStartInfo(runtime.Root, [], null, platform == "linux" ? LinuxLaunchHost : ServerHost.Windows);
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
        var error = Assert.Throws<InvalidOperationException>(() => ServerLaunch.CreateStartInfo(runtime.Root, [], null, ServerHost.Windows));
        Assert.Contains("doorstop_config.ini", error.Message);
    }
    // The station's dedicated-server install carries Doorstop 3's section and key names.
    [Fact] public void WindowsDoorstop3ConfigIsAccepted()
    {
        using var runtime = Runtime.Windows();
        File.WriteAllText(Path.Combine(runtime.Root, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        File.WriteAllText(Path.Combine(runtime.Root, "winhttp.dll"), "MZ targetAssembly"); // Doorstop 3's own proxy
        Assert.EndsWith("valheim_server.exe", ServerLaunch.CreateStartInfo(runtime.Root, [], null, ServerHost.Windows).FileName);
    }
    [Theory]
    [InlineData("linux", "BepInEx/core/BepInEx.Preloader.dll")] [InlineData("linux", "BepInEx/core/BepInEx.dll")] [InlineData("linux", "doorstop_libs/libdoorstop_x64.so")]
    [InlineData("windows", "BepInEx/core/BepInEx.Preloader.dll")] [InlineData("windows", "BepInEx/core/BepInEx.dll")]
    [InlineData("windows", "winhttp.dll")] [InlineData("windows", "doorstop_config.ini")]
    public void MissingBepInExLoaderIsRefusedRatherThanStartingVanilla(string platform, string missing)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        File.Delete(Path.Combine(runtime.Root, missing));
        Assert.Throws<FileNotFoundException>(() => ServerLaunch.CreateStartInfo(runtime.Root, [], null, platform == "linux" ? LinuxLaunchHost : ServerHost.Windows));
    }
    [Fact] public void LinuxRuntimePathThatCannotBeListedIsRefused()
    {
        if (OperatingSystem.IsWindows()) return; // Windows paths are not valid in Linux search lists anyway.
        using var runtime = Runtime.Linux("server:copy");
        Assert.Throws<ArgumentException>(() => ServerLaunch.CreateStartInfo(runtime.Root, [], null, ServerHost.Linux));
        // A Windows host only builds the launch for inspection and does not check.
        Assert.Equal(runtime.Root, ServerLaunch.CreateStartInfo(runtime.Root, [], null, ServerHost.Windows).WorkingDirectory);
    }

    [Theory]
    [InlineData("Valheim.app", "Contents/MacOS/Valheim")] // the bundle itself
    [InlineData("Valheim copy.app", "Contents/MacOS/Valheim")] // a renamed bundle, recognised by its executable
    [InlineData("Valheim", "Valheim.app/Contents/MacOS/Valheim")] // a Steam install directory holding the bundle
    [InlineData("Valheim", "Valheim.app/Contents/Info.plist")] // a partial copy: the bundle directory is enough
    public void MacClientIsRefusedAsNotAServer(string name, string file)
    {
        using var runtime = new Runtime(name); runtime.Add(file);
        var error = Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.Detect(runtime.Root));
        Assert.Contains("not a dedicated server", error.Message);
        Assert.Contains("Steam app 896660", error.Message);
        Assert.Contains("ClientLaunch", error.Message);
        Assert.Contains("container", error.Message);
        Assert.Contains("remote Windows/Linux host", error.Message);
        foreach (var host in new[] { ServerHost.Windows, ServerHost.Linux, ServerHost.MacOS })
        {
            Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.RequireExecutable(runtime.Root, host));
            Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.CreateStartInfo(runtime.Root, [], null, host));
        }
    }
    [Fact] public void ServerExecutableWinsOverAMacClientBesideIt()
    {
        using var runtime = Runtime.Linux(); runtime.Add("Valheim.app/Contents/MacOS/Valheim");
        Assert.Equal(ServerPlatform.Linux, ServerLaunch.Detect(runtime.Root));
    }
    [Fact] public void EmptyDirectoryNamedLikeAnAppIsStillAMissingServer()
    {
        using var runtime = new Runtime("Server.app");
        Assert.Throws<FileNotFoundException>(() => ServerLaunch.Detect(runtime.Root));
    }
    [Theory] [InlineData("linux")] [InlineData("windows")]
    public void MacHostRefusesToLaunchEitherServer(string platform)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        // Detection reads the runtime's contents and still works on a Mac, e.g. to stage a copy for a container.
        Assert.Equal(platform == "linux" ? ServerPlatform.Linux : ServerPlatform.Windows, ServerLaunch.Detect(runtime.Root));
        var error = Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.RequireExecutable(runtime.Root, ServerHost.MacOS));
        Assert.Contains("run the macOS dedicated server (Steam app 896660", error.Message);
        Assert.Contains("--platform linux/amd64", error.Message);
        Assert.Contains("remote Windows/Linux host", error.Message);
        Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.CreateStartInfo(runtime.Root, ["-batchmode"], null, ServerHost.MacOS));
    }
    [Fact] public void MacHostRefusesBeforeReadingTheExecuteBit()
    {
        // A missing execute bit would otherwise report chmod, which cannot help on a Mac.
        using var runtime = Runtime.Linux(executable: false);
        Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.RequireExecutable(runtime.Root, ServerHost.MacOS));
    }
    [Fact] public void PublicOverloadsRefuseOnAMacHostOnly()
    {
        using var runtime = Runtime.Linux();
        if (OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.RequireExecutable(runtime.Root));
            Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.CreateStartInfo(runtime.Root, []));
            return;
        }
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.x86_64"), ServerLaunch.RequireExecutable(runtime.Root));
        Assert.Equal(runtime.Root, ServerLaunch.CreateStartInfo(runtime.Root, []).WorkingDirectory);
    }

    // ---- the macOS dedicated server ----

    [Fact] public void MacRuntimeIsDetectedFromItsExecutableAndDataFolder()
    {
        using var runtime = Runtime.Mac();
        Assert.Equal(ServerPlatform.MacOS, ServerLaunch.Detect(runtime.Root));
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server", "Valheim"), ServerLaunch.RequireExecutable(runtime.Root, ServerHost.MacOS));
        Assert.Equal(ServerPlatform.MacOS, ServerLaunch.Detect(runtime.Root)); // Detection reads contents on any host.
    }
    [Fact] public void MacExecutableWithoutItsDataFolderIsNotAServer()
    {
        using var runtime = new Runtime(); runtime.Add("valheim_server/Valheim", Fat(X86_64, Arm64));
        Assert.Contains("valheim_server/Data", Assert.Throws<FileNotFoundException>(() => ServerLaunch.Detect(runtime.Root)).Message);
    }
    [Fact] public void MacRuntimeBesideAnotherServerIsRefused()
    {
        using var runtime = Runtime.Mac(); runtime.Add("valheim_server.x86_64");
        Assert.Contains("more than one", Assert.Throws<InvalidOperationException>(() => ServerLaunch.Detect(runtime.Root)).Message);
    }
    [Theory] [InlineData("linux")] [InlineData("windows")]
    public void OnlyAMacHostRunsTheMacServer(string hostName)
    {
        using var runtime = Runtime.Mac();
        var host = hostName == "linux" ? ServerHost.Linux : ServerHost.Windows;
        var error = Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.RequireExecutable(runtime.Root, host));
        Assert.Contains("cannot run the MacOS dedicated server", error.Message);
        Assert.Throws<PlatformNotSupportedException>(() => ServerLaunch.CreateStartInfo(runtime.Root, ["-batchmode"], null, host, ClientArchitecture.Arm64));
    }
    [Fact] public void MacLaunchStartsTheNativeSliceWithDoorstopInsertedAndTheServersLibraryPath()
    {
        if (OperatingSystem.IsWindows()) return; // A Windows path cannot be a macOS runtime (it holds ':').
        using var runtime = Runtime.Mac();
        var start = ServerLaunch.CreateStartInfo(runtime.Root, ["-batchmode", "-nographics", "-savedir", "/saves with space"],
            new Dictionary<string, string> { ["KEEP"] = "yes" }, ServerHost.MacOS, ClientArchitecture.Arm64);
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
        var x64 = ServerLaunch.CreateStartInfo(runtime.Root, [], null, ServerHost.MacOS, ClientArchitecture.X64);
        Assert.Equal("-x86_64", x64.ArgumentList[0]);
    }
    [Fact] public void MacLaunchRefusesAServerOrDoorstopWithoutTheNativeSlice()
    {
        if (OperatingSystem.IsWindows()) return;
        using var intelOnly = Runtime.Mac(server: Thin(X86_64));
        Assert.Contains("no arm64 slice", Assert.Throws<InvalidOperationException>(() => ServerLaunch.CreateStartInfo(intelOnly.Root, [], null, ServerHost.MacOS, ClientArchitecture.Arm64)).Message);
        using var stockDoorstop = Runtime.Mac(doorstop: Thin(X86_64));
        var error = Assert.Throws<InvalidOperationException>(() => ServerLaunch.CreateStartInfo(stockDoorstop.Root, [], null, ServerHost.MacOS, ClientArchitecture.Arm64));
        Assert.Contains("libdoorstop.dylib with an arm64 slice", error.Message);
        Assert.DoesNotContain("request x64", error.Message); // A server runs as the machine's slice; there is no other to request.
        using var noDoorstop = Runtime.Mac(withDoorstop: false);
        Assert.Throws<FileNotFoundException>(() => ServerLaunch.CreateStartInfo(noDoorstop.Root, [], null, ServerHost.MacOS, ClientArchitecture.Arm64));
    }
    [Fact] public void MacServerNeedsItsExecuteBitAndAListablePath()
    {
        if (OperatingSystem.IsWindows()) return; // Unix modes and ':' in a directory name.
        using var runtime = Runtime.Mac(executable: false);
        Assert.Contains("chmod u+x", Assert.Throws<InvalidOperationException>(() => ServerLaunch.RequireExecutable(runtime.Root, ServerHost.MacOS)).Message);
        using var colon = Runtime.Mac("server:copy");
        Assert.Throws<ArgumentException>(() => ServerLaunch.CreateStartInfo(colon.Root, [], null, ServerHost.MacOS, ClientArchitecture.Arm64));
    }
    [Fact] public void LocalPlatformFollowsTheOperatingSystem()
    {
        var expected = OperatingSystem.IsWindows() ? ServerPlatform.Windows : OperatingSystem.IsMacOS() ? ServerPlatform.MacOS : ServerPlatform.Linux;
        Assert.Equal(expected, ServerLaunch.LocalPlatform);
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
