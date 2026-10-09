using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class BepInExLoaderPackageTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public void AReviewedPackageReplacesTheCopiedGamesLoaderWithoutChangingTheSource()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        string sourceCore = Path.Combine(_rig.Game, "BepInEx", "core", "BepInEx.dll");
        File.WriteAllText(sourceCore, "another live core");
        string changed = FileHash.Sha256(sourceCore);

        var environment = _rig.Manifest();
        _rig.LoaderPackage = manifest;
        var staged = _rig.Regression(environment).Stage("parent");
        Assert.Equal(package.Files["BepInEx/core/BepInEx.dll"], FileHash.Sha256(Path.Combine(_rig.Install, "BepInEx", "core", "BepInEx.dll")));
        Assert.Equal(changed, FileHash.Sha256(sourceCore));
        Assert.Contains(package.Identity, File.ReadAllText(Path.Combine(_rig.Install, TargetedRegression.MarkerFile)));
        // One loader identity: the install the package was applied to has the package's loader pin, which its identity names.
        Assert.Equal(package.Loader, InstallPins.Of(_rig.Install).Loader);
        Assert.EndsWith("(" + package.Loader + ")", package.Identity);
        staged.Verify();
        _rig.Regression(environment).Stage("candidate").Verify(); // The package remains selected across arms.
    }

    [Fact] public void ChangedPackageBytesAreRejectedBeforeTheDisposableInstallIsCreated()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        string source = Path.Combine(package.Root, "BepInEx", "core", "BepInEx.dll");
        File.AppendAllText(source, "changed");
        var environment = _rig.Manifest();
        _rig.LoaderPackage = manifest;
        Assert.Contains("missing or changed", Assert.Throws<InvalidDataException>(() => _rig.Regression(environment).Stage("parent")).Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void APlainGameWithoutBepInExCanUseAnExplicitLoaderPackage()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        Directory.Delete(Path.Combine(_rig.Game, "BepInEx", "core"), recursive: true);
        var environment = _rig.Manifest();
        _rig.LoaderPackage = manifest;
        _rig.Regression(environment).Stage("parent").Verify();
        Assert.False(Directory.Exists(Path.Combine(_rig.Game, "BepInEx", "core")));
        Assert.Equal(package.Files["BepInEx/core/BepInEx.dll"], FileHash.Sha256(Path.Combine(_rig.Install, "BepInEx", "core", "BepInEx.dll")));
    }

    [Fact] public void AManifestCannotOverrideThePinnedLoaderConfiguration()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        var environment = _rig.Manifest();
        _rig.LoaderPackage = manifest;
        environment.Configs["BepInEx.cfg"] = Path.Combine(package.Root, "BepInEx", "config", "BepInEx.cfg");
        Assert.Contains("edit and recapture", Assert.Throws<ArgumentException>(() => _rig.Regression(environment)).Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void TheLiveGameCannotBeThePinnedPackage()
    {
        string manifest = Path.Combine(_rig.Root, "loader.json");
        BepInExLoaderPackage.Capture(_rig.Game, "live-game", "unreviewed").Write(manifest);
        var environment = _rig.Manifest();
        _rig.LoaderPackage = manifest;
        Assert.Contains("extract one reviewed loader set", Assert.Throws<InvalidOperationException>(() => _rig.Regression(environment).Stage("parent")).Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void ANewPinnedPackageInvalidatesAndRebuildsTheDisposableInstall()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        var environment = _rig.Manifest();
        _rig.LoaderPackage = manifest;
        _rig.Regression(environment).Stage("parent").Verify();

        string core = Path.Combine(package.Root, "BepInEx", "core", "BepInEx.dll");
        File.AppendAllText(core, "new reviewed loader version");
        package = BepInExLoaderPackage.Capture(package.Root, package.Name, "5.4.2203");
        package.Write(manifest);
        _rig.Regression(environment).Stage("parent").Verify();

        Assert.Equal(package.Files["BepInEx/core/BepInEx.dll"], FileHash.Sha256(Path.Combine(_rig.Install, "BepInEx", "core", "BepInEx.dll")));
        Assert.Contains(package.Identity, File.ReadAllText(Path.Combine(_rig.Install, TargetedRegression.MarkerFile)));
    }

    [Fact] public void GamePinsNameThePackagesLoaderWhenOneIsSelected()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        File.WriteAllText(Path.Combine(_rig.Game, "BepInEx", "core", "BepInEx.dll"), "another live core");
        var environment = _rig.Manifest();
        _rig.LoaderPackage = manifest;
        // The live game's own loader is not what the disposable install will run.
        environment.GamePins = new InstallPins { Game = InstallPins.GameHash(_rig.Game), Loader = InstallPins.Of(_rig.Game).Loader, Patchers = new string('0', 64) };
        Assert.Contains("the loader differs", Assert.Throws<InvalidOperationException>(() => _rig.Regression(environment).Stage("parent")).Message);
        Assert.False(Directory.Exists(_rig.Install));
        // The control: the package's loader is.
        environment.GamePins.Loader = package.Loader;
        _rig.Regression(environment).Stage("parent").Verify();
    }

    [Fact] public void ThePackagesLoaderLeavesOutItsBepInExSettings()
    {
        var package = Package();
        Assert.Contains("BepInEx/config/BepInEx.cfg", package.Files.Keys);
        string loader = package.Loader;
        File.AppendAllText(Path.Combine(package.Root, "BepInEx", "config", "BepInEx.cfg"), "\n[Logging.Disk]\nEnabled = true\n");
        var recaptured = BepInExLoaderPackage.Capture(package.Root, package.Name, package.Version);
        Assert.NotEqual(package.Files["BepInEx/config/BepInEx.cfg"], recaptured.Files["BepInEx/config/BepInEx.cfg"]);
        Assert.Equal(loader, recaptured.Loader);
        // Negative control: another Doorstop library is a new loader.
        Directory.CreateDirectory(Path.Combine(package.Root, "doorstop_libs"));
        File.WriteAllText(Path.Combine(package.Root, "doorstop_libs", "libdoorstop_x64.so"), "another doorstop");
        Assert.NotEqual(loader, BepInExLoaderPackage.Capture(package.Root, package.Name, package.Version).Loader);
    }

    [Fact] public void AManifestWithUppercaseHashesHasTheSameLoader()
    {
        // Get-FileHash writes uppercase hex, which Validate accepts; the loader pin is lowercase like every listing's.
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "upper.json");
        new BepInExLoaderPackage { Name = package.Name, Version = package.Version, Root = package.Root,
            Files = package.Files.ToDictionary(file => file.Key, file => file.Value.ToUpperInvariant(), StringComparer.Ordinal) }.Write(manifest);
        Assert.Equal(package.Loader, BepInExLoaderPackage.Read(manifest).Loader);
    }

    // A package is a complete loader for some platform, by the launches' one list of loader files (BepInExLoader.LoaderFiles).
    [Fact] public void APackageThatIsNoPlatformsCompleteLoaderIsRefused()
    {
        string root = Path.Combine(_rig.Root, "partial-loader");
        void Write(string relative) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relative))!); File.WriteAllText(Path.Combine(root, relative), relative); }
        Write("BepInEx/core/BepInEx.dll"); Write("BepInEx/core/BepInEx.Preloader.dll"); Write("doorstop_libs/readme.txt");
        var error = Assert.Throws<InvalidDataException>(() => BepInExLoaderPackage.Capture(root, "partial", "1"));
        Assert.Contains("not a complete loader for any platform", error.Message);
        Assert.Contains("Linux lacks doorstop_libs/libdoorstop_x64.so", error.Message);
        Assert.Contains("Windows lacks winhttp.dll, doorstop_config.ini", error.Message);
        Write("doorstop_libs/libdoorstop_x64.so");
        BepInExLoaderPackage.Capture(root, "partial", "1"); // The control: with Linux's Doorstop library it is Linux's loader.
        // A macOS-only package (a root libdoorstop.dylib) is complete too, on every OS that captures it.
        string mac = Path.Combine(_rig.Root, "mac-loader");
        foreach (string relative in new[] { "BepInEx/core/BepInEx.dll", "BepInEx/core/BepInEx.Preloader.dll", "libdoorstop.dylib" })
        { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(mac, relative))!); File.WriteAllText(Path.Combine(mac, relative), relative); }
        var macPackage = BepInExLoaderPackage.Capture(mac, "mac", "1");
        macPackage.RequireFor(ClientPlatform.MacOS, "package");
        Assert.Contains("lacks doorstop_libs/libdoorstop_x64.so", Assert.Throws<FileNotFoundException>(() => macPackage.RequireFor(ClientPlatform.Linux, "package")).Message);
        File.Delete(Path.Combine(root, "BepInEx", "core", "BepInEx.Preloader.dll"));
        Assert.Contains("lacks BepInEx/core/BepInEx.Preloader.dll", Assert.Throws<InvalidDataException>(() => BepInExLoaderPackage.Capture(root, "partial", "1")).Message);
    }

    private BepInExLoaderPackage Package()
    {
        var source = BepInExLoaderPackage.Capture(_rig.Game, "BepInExPack_Valheim", "5.4.2202");
        string root = Path.Combine(_rig.Root, "extracted-package");
        foreach (string relative in source.Files.Keys)
        {
            string from = Path.Combine(_rig.Game, relative.Replace('/', Path.DirectorySeparatorChar));
            string to = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
        }
        return BepInExLoaderPackage.Capture(root, "BepInExPack_Valheim", "5.4.2202");
    }
}
