using Valheim.Testing.Game;
using Xunit;

public class ServerLaunchTests
{
    [Fact] public void WindowsRuntimeIsDetectedFromItsExecutable()
    {
        using var runtime = Runtime.Windows();
        Assert.Equal(ServerPlatform.Windows, ServerLaunch.Detect(runtime.Root));
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.exe"), ServerLaunch.RequireExecutable(runtime.Root));
    }
    [Fact] public void LinuxRuntimeIsDetectedFromItsExecutable()
    {
        using var runtime = Runtime.Linux();
        Assert.Equal(ServerPlatform.Linux, ServerLaunch.Detect(runtime.Root));
        Assert.Equal(Path.Combine(runtime.Root, "valheim_server.x86_64"), ServerLaunch.RequireExecutable(runtime.Root));
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
        Assert.Throws<FileNotFoundException>(() => ServerLaunch.Detect(runtime.Root));
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
        if (OperatingSystem.IsWindows())
        {
            // No Unix mode exists to read; the file's presence is the whole check.
            Assert.Equal(executable, ServerLaunch.RequireExecutable(runtime.Root));
            return;
        }
        Assert.Throws<InvalidOperationException>(() => ServerLaunch.RequireExecutable(runtime.Root));
        Assert.Throws<InvalidOperationException>(() => ServerLaunch.CreateStartInfo(runtime.Root, []));
        File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        Assert.Equal(executable, ServerLaunch.RequireExecutable(runtime.Root));
    }
    [Fact] public void WindowsLaunchUsesRuntimeAndCallerSettingsWithoutLinuxLoader()
    {
        using var runtime = Runtime.Windows();
        var start = ServerLaunch.CreateStartInfo(runtime.Root, ["-batchmode", "-nographics", "-savedir", "C:/saves with space"],
            new Dictionary<string, string> { ["TEST_TOKEN"] = "abc" });
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
        });
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
        var start = ServerLaunch.CreateStartInfo(runtime.Root, [], new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = "", ["LD_PRELOAD"] = "" });
        // A trailing ':' would add the working directory to the search path.
        Assert.Equal(Path.Combine(runtime.Root, "linux64") + ":" + Path.Combine(runtime.Root, "doorstop_libs"), start.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal("libdoorstop_x64.so", start.Environment["LD_PRELOAD"]);
    }
    [Fact] public void CallerSteamAppIdIsKept()
    {
        using var runtime = Runtime.Linux();
        var start = ServerLaunch.CreateStartInfo(runtime.Root, [], new Dictionary<string, string> { ["SteamAppId"] = "123" });
        Assert.Equal("123", start.Environment["SteamAppId"]);
    }
    [Theory] [InlineData("DOORSTOP_ENABLED")] [InlineData("DOORSTOP_TARGET_ASSEMBLY")]
    public void CallerCannotRedirectTheLinuxLoader(string name)
    {
        using var runtime = Runtime.Linux();
        Assert.Throws<ArgumentException>(() => ServerLaunch.CreateStartInfo(runtime.Root, [], new Dictionary<string, string> { [name] = "0" }));
    }
    [Theory]
    [InlineData("linux", "BepInEx/core/BepInEx.Preloader.dll")] [InlineData("linux", "doorstop_libs/libdoorstop_x64.so")]
    [InlineData("windows", "BepInEx/core/BepInEx.Preloader.dll")] [InlineData("windows", "winhttp.dll")]
    public void MissingBepInExLoaderIsRefusedRatherThanStartingVanilla(string platform, string missing)
    {
        using var runtime = platform == "linux" ? Runtime.Linux() : Runtime.Windows();
        File.Delete(Path.Combine(runtime.Root, missing));
        Assert.Throws<FileNotFoundException>(() => ServerLaunch.CreateStartInfo(runtime.Root, []));
    }
    [Fact] public void LinuxRuntimePathThatCannotBeListedIsRefused()
    {
        if (OperatingSystem.IsWindows()) return; // Windows paths are not valid in Linux search lists anyway.
        using var runtime = Runtime.Linux("server:copy");
        Assert.Throws<ArgumentException>(() => ServerLaunch.CreateStartInfo(runtime.Root, []));
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
        public static Runtime Windows()
        {
            var runtime = new Runtime();
            runtime.Add("valheim_server.exe"); runtime.Add("winhttp.dll"); runtime.Add("BepInEx/core/BepInEx.Preloader.dll");
            return runtime;
        }
        public static Runtime Linux(string name = "runtime", bool executable = true)
        {
            var runtime = new Runtime(name);
            string server = runtime.Add("valheim_server.x86_64");
            runtime.Add("doorstop_libs/libdoorstop_x64.so"); runtime.Add("BepInEx/core/BepInEx.Preloader.dll");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(server, executable ? File.GetUnixFileMode(server) | UnixFileMode.UserExecute : File.GetUnixFileMode(server) & ~UnixFileMode.UserExecute);
            return runtime;
        }
        public void Dispose() => Directory.Delete(_parent, true);
    }
}
