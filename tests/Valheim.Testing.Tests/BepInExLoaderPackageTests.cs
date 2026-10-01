using Valheim.Testing.Game;
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
        string changed = WorldFixture.Hash(sourceCore);

        var environment = _rig.Manifest();
        environment.LoaderPackage = manifest;
        var staged = new TargetedRegression(environment).Stage("parent");
        Assert.Equal(package.Files["BepInEx/core/BepInEx.dll"], WorldFixture.Hash(Path.Combine(_rig.Install, "BepInEx", "core", "BepInEx.dll")));
        Assert.Equal(changed, WorldFixture.Hash(sourceCore));
        Assert.Contains(package.Identity, File.ReadAllText(Path.Combine(_rig.Install, TargetedRegression.MarkerFile)));
        staged.Verify();
        new TargetedRegression(environment).Stage("candidate").Verify(); // The package remains selected across arms.
    }

    [Fact] public void ChangedPackageBytesAreRejectedBeforeTheDisposableInstallIsCreated()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        string source = Path.Combine(package.Root, "BepInEx", "core", "BepInEx.dll");
        File.AppendAllText(source, "changed");
        var environment = _rig.Manifest();
        environment.LoaderPackage = manifest;
        Assert.Contains("missing or changed", Assert.Throws<InvalidDataException>(() => new TargetedRegression(environment).Stage("parent")).Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void APlainGameWithoutBepInExCanUseAnExplicitLoaderPackage()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        Directory.Delete(Path.Combine(_rig.Game, "BepInEx", "core"), recursive: true);
        var environment = _rig.Manifest();
        environment.LoaderPackage = manifest;
        new TargetedRegression(environment).Stage("parent").Verify();
        Assert.False(Directory.Exists(Path.Combine(_rig.Game, "BepInEx", "core")));
        Assert.Equal(package.Files["BepInEx/core/BepInEx.dll"], WorldFixture.Hash(Path.Combine(_rig.Install, "BepInEx", "core", "BepInEx.dll")));
    }

    [Fact] public void AManifestCannotOverrideThePinnedLoaderConfiguration()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        var environment = _rig.Manifest();
        environment.LoaderPackage = manifest;
        environment.Configs["BepInEx.cfg"] = Path.Combine(package.Root, "BepInEx", "config", "BepInEx.cfg");
        Assert.Contains("edit and recapture", Assert.Throws<ArgumentException>(() => new TargetedRegression(environment)).Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void TheLiveGameCannotBeThePinnedPackage()
    {
        string manifest = Path.Combine(_rig.Root, "loader.json");
        BepInExLoaderPackage.Capture(_rig.Game, "live-game", "unreviewed").Write(manifest);
        var environment = _rig.Manifest();
        environment.LoaderPackage = manifest;
        Assert.Contains("extract one reviewed loader set", Assert.Throws<InvalidOperationException>(() => new TargetedRegression(environment).Stage("parent")).Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void ANewPinnedPackageInvalidatesAndRebuildsTheDisposableInstall()
    {
        var package = Package();
        string manifest = Path.Combine(_rig.Root, "loader.json");
        package.Write(manifest);
        var environment = _rig.Manifest();
        environment.LoaderPackage = manifest;
        new TargetedRegression(environment).Stage("parent").Verify();

        string core = Path.Combine(package.Root, "BepInEx", "core", "BepInEx.dll");
        File.AppendAllText(core, "new reviewed loader version");
        package = BepInExLoaderPackage.Capture(package.Root, package.Name, "5.4.2203");
        package.Write(manifest);
        new TargetedRegression(environment).Stage("parent").Verify();

        Assert.Equal(package.Files["BepInEx/core/BepInEx.dll"], WorldFixture.Hash(Path.Combine(_rig.Install, "BepInEx", "core", "BepInEx.dll")));
        Assert.Contains(package.Identity, File.ReadAllText(Path.Combine(_rig.Install, TargetedRegression.MarkerFile)));
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
