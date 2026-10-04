using System.Diagnostics;
using Valheim.Testing.Game;
using Xunit;

// The loader policy ServerLaunch and ClientLaunch share. Each launcher's own tests check that it applies it.
public class BepInExLoaderTests
{
    private const string Doorstop4 = "[General]\nenabled = true\ntarget_assembly={0}\n[UnityMono]\ndebug_enabled = false\n";
    private const string Doorstop3 = "[UnityDoorstop]\nenabled=true\ntargetAssembly={0}\nredirectOutputLog=false\n";

    [Theory]
    [InlineData(Doorstop4, @"BepInEx\core\BepInEx.Preloader.dll")]
    [InlineData(Doorstop3, @"BepInEx\core\BepInEx.Preloader.dll")]
    [InlineData(Doorstop4, "BepInEx/core/BepInEx.Preloader.dll")]
    [InlineData(Doorstop4, @".\BepInEx\core\BepInEx.Preloader.dll")]
    [InlineData(Doorstop3, @"bepinex\CORE\bepinex.preloader.DLL")]
    [InlineData(Doorstop4, @"BepInEx\plugins\..\core\BepInEx.Preloader.dll")]
    public void WindowsConfigTargetingThePreloaderIsAccepted(string format, string target)
    {
        using var root = new Root(string.Format(format, target));
        BepInExLoader.RequireWindowsLoader(root.Path, "install");
    }
    [Fact] public void WindowsConfigWithAnAbsolutePreloaderPathIsAccepted()
    {
        if (!OperatingSystem.IsWindows()) return; // A drive-rooted path only resolves as absolute on Windows.
        using var root = new Root("");
        root.Config(string.Format(Doorstop4, System.IO.Path.Combine(root.Path, "BepInEx", "core", "BepInEx.Preloader.dll")));
        BepInExLoader.RequireWindowsLoader(root.Path, "install");
    }
    [Fact] public void NestedCoreIsRefusedBeforeThePreloaderCanLoadHarmonyTwice()
    {
        using var root = new Root(string.Format(Doorstop3, @"BepInEx\core\BepInEx.Preloader.dll"));
        root.Add(@"BepInEx\core\core\0Harmony20.dll");
        var error = Assert.Throws<InvalidOperationException>(() => BepInExLoader.RequireCore(root.Path, "client"));
        Assert.Contains("nested BepInEx/core/core", error.Message);
    }
    // An existing DLL is not enough: Doorstop would load it instead of BepInEx.
    [Theory]
    [InlineData(Doorstop4, @"BepInEx\core\Other.Preloader.dll")]
    [InlineData(Doorstop3, @"BepInEx\core\Other.Preloader.dll")]
    [InlineData(Doorstop4, @"BepInEx\core\BepInEx.dll")]
    [InlineData(Doorstop4, @"..\BepInEx\core\BepInEx.Preloader.dll")]
    public void WindowsConfigTargetingAnotherExistingFileIsRefused(string format, string target)
    {
        using var root = new Root(string.Format(format, target));
        root.Add(@"BepInEx\core\Other.Preloader.dll"); root.Add(@"..\BepInEx\core\BepInEx.Preloader.dll");
        var error = Assert.Throws<InvalidOperationException>(() => BepInExLoader.RequireWindowsLoader(root.Path, "install"));
        Assert.Contains("not BepInEx's preloader", error.Message); Assert.Contains(target, error.Message);
    }
    [Fact] public void EveryStatedTargetMustBeThePreloader()
    {
        using var root = new Root(string.Format(Doorstop4, @"BepInEx\core\BepInEx.Preloader.dll") + string.Format(Doorstop3, @"BepInEx\core\Other.Preloader.dll"));
        Assert.Throws<InvalidOperationException>(() => BepInExLoader.RequireWindowsLoader(root.Path, "install"));
    }
    [Theory]
    [InlineData("[General]\nenabled = false\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n")]
    [InlineData("[UnityDoorstop]\nenabled=1\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n")]
    [InlineData("[General]\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n")]
    [InlineData("[General]\nenabled = true\n")]
    [InlineData("[General]\nenabled = true\ntarget_assembly=\n")]
    public void WindowsConfigThatDisablesOrNamesNoTargetIsRefused(string config)
    {
        using var root = new Root(config);
        var error = Assert.Throws<InvalidOperationException>(() => BepInExLoader.RequireWindowsLoader(root.Path, "runtime"));
        Assert.Contains("doorstop_config.ini", error.Message);
    }
    [Theory] [InlineData("winhttp.dll")] [InlineData("doorstop_config.ini")]
    public void WindowsLoaderFilesAreRequired(string missing)
    {
        using var root = new Root(string.Format(Doorstop4, @"BepInEx\core\BepInEx.Preloader.dll"));
        File.Delete(System.IO.Path.Combine(root.Path, missing));
        var error = Assert.Throws<FileNotFoundException>(() => BepInExLoader.RequireWindowsLoader(root.Path, "runtime"));
        Assert.Contains("runtime", error.Message);
    }
    [Theory] [InlineData("BepInEx/core/BepInEx.Preloader.dll")] [InlineData("BepInEx/core/BepInEx.dll")]
    public void BepInExCoreIsRequired(string missing)
    {
        using var root = new Root("");
        File.Delete(System.IO.Path.Combine(root.Path, missing));
        Assert.Throws<FileNotFoundException>(() => BepInExLoader.RequireCore(root.Path, "install"));
    }
    [Theory] [InlineData("DOORSTOP_ENABLED")] [InlineData("DOORSTOP_TARGET_ASSEMBLY")] [InlineData("DOORSTOP_DISABLE")] [InlineData("doorstop_disable")]
    public void LoaderVariablesAreRefused(string name)
    {
        var environment = new Dictionary<string, string> { [name] = "0" };
        Assert.Throws<ArgumentException>(() => BepInExLoader.RefuseOverrides(environment, [], StringComparer.OrdinalIgnoreCase, "Test"));
        if (name == name.ToUpperInvariant()) Assert.Throws<ArgumentException>(() => BepInExLoader.RefuseOverrides(environment, [], StringComparer.Ordinal, "Test"));
    }
    [Theory] [InlineData("--doorstop-enabled")] [InlineData("--doorstop-target-assembly")] [InlineData("--DOORSTOP-ENABLE")]
    public void DoorstopArgumentsAreRefused(string argument)
    {
        var error = Assert.Throws<ArgumentException>(() => BepInExLoader.RefuseOverrides(new Dictionary<string, string>(), ["-batchmode", argument, "false"], StringComparer.Ordinal, "Test"));
        Assert.Contains(argument, error.Message);
        Assert.Throws<ArgumentException>(() => BepInExLoader.RefuseOverrides(new Dictionary<string, string>(), ["-batchmode", null!], StringComparer.Ordinal, "Test"));
    }
    [Fact] public void InheritedLoaderVariablesAreRemovedAndCallerValuesApplied()
    {
        var start = new ProcessStartInfo();
        start.Environment["DOORSTOP_ENABLED"] = "0"; start.Environment["DOORSTOP_DISABLE"] = "1"; start.Environment["DOORSTOP_TARGET_ASSEMBLY"] = "other.dll";
        BepInExLoader.ApplyEnvironment(start, new Dictionary<string, string> { ["TEST_TOKEN"] = "abc" });
        Assert.Equal("abc", start.Environment["TEST_TOKEN"]);
        Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Root : IDisposable
    {
        private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "loader-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public Root(string config)
        {
            Path = System.IO.Path.Combine(_parent, "install"); Directory.CreateDirectory(Path);
            Add("winhttp.dll"); Add(@"BepInEx\core\BepInEx.Preloader.dll"); Add(@"BepInEx\core\BepInEx.dll"); Config(config);
        }
        public void Config(string text) => File.WriteAllText(System.IO.Path.Combine(Path, "doorstop_config.ini"), text);
        public void Add(string relative)
        {
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllText(path, "fake");
        }
        public void Dispose() => Directory.Delete(_parent, true);
    }
}

// A leftover preloader patcher rewrites game types before any plugin loads; a runtime's patchers are the plan's or none.
public sealed class BepInExPatchersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("patchers-").FullName;
    public void Dispose() => Directory.Delete(_root, true);
    private string Patchers => Path.Combine(_root, "BepInEx", "patchers");
    private void Add(string name, bool directory = false)
    {
        Directory.CreateDirectory(Patchers);
        if (directory) Directory.CreateDirectory(Path.Combine(Patchers, name)); else File.WriteAllText(Path.Combine(Patchers, name), "patcher");
    }

    [Fact] public void NoOrAnEmptyPatchersDirectoryIsClean()
    {
        BepInExLoader.RequirePatchers(_root, [], "runtime");
        Directory.CreateDirectory(Patchers);
        BepInExLoader.RequirePatchers(_root, [], "runtime");
    }
    [Fact] public void NamedPatchersAreAccepted()
    {
        Add("HookGenPatcher", directory: true); Add("Other.Patcher.dll");
        BepInExLoader.RequirePatchers(_root, ["Other.Patcher.dll", "HookGenPatcher"], "runtime");
    }
    [Fact] public void ALeftoverPatcherIsRefusedByName()
    {
        Add("HookGenPatcher", directory: true); Add("RemovedMod.Preloader.dll");
        var error = Assert.Throws<InvalidOperationException>(() => BepInExLoader.RequirePatchers(_root, ["HookGenPatcher"], "runtime"));
        Assert.Contains("RemovedMod.Preloader.dll", error.Message); Assert.DoesNotContain("HookGenPatcher,", error.Message);
        Assert.Contains("empty patchers directory", error.Message);
    }
    [Fact] public void ANamedPatcherThatIsNotThereIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BepInExLoader.RequirePatchers(_root, ["HookGenPatcher"], "runtime"));
        Assert.Contains("HookGenPatcher", error.Message);
    }
    [Theory]
    [InlineData("sub/Patcher.dll")] [InlineData(@"sub\Patcher.dll")] [InlineData("..")] [InlineData(" ")]
    public void PatcherNamesAreSingleEntries(string name) =>
        Assert.Throws<ArgumentException>(() => BepInExLoader.CheckPatcherNames([name]));
    [Fact] public void APatcherIsNamedOnce() => Assert.Throws<ArgumentException>(() => BepInExLoader.CheckPatcherNames(["A.dll", "a.dll"]));
}
