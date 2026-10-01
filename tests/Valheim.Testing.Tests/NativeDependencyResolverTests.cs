using Valheim.Testing.Game;
using Xunit;

public sealed class NativeDependencyResolverTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public void StandalonePluginNeedsNoDependencyDllAndRelativeRequestPathsAreResolved()
    {
        string mod = _rig.Write("standalone/Alone.dll", RegressionRig.Assembly("Alone", new("example.alone")));
        var request = Request(mod);
        request.SearchRoots = [];
        string directory = Path.Combine(_rig.Root, "request");
        Directory.CreateDirectory(directory);
        request.Mods = [Path.GetRelativePath(directory, mod)];
        request.GameManaged = Path.GetRelativePath(directory, request.GameManaged);
        request.BepInExCore = Path.GetRelativePath(directory, request.BepInExCore);
        request.CliManifest = Path.GetRelativePath(directory, request.CliManifest);
        request.CliFiles = Path.GetRelativePath(directory, request.CliFiles);
        string file = Path.Combine(directory, "setup.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(request));
        var resolved = NativeDependencyResolver.Resolve(NativeDependencyRequest.Read(file));
        Assert.True(resolved.Ready, string.Join("; ", resolved.Gaps.Select(gap => gap.Reason)));
        Assert.Empty(resolved.Plugins);
        Assert.Equal(mod, Assert.Single(resolved.Mods).File);
    }

    [Fact] public void HardPluginAndLibraryClosureArePinnedFromExplicitRoots()
    {
        var request = Request(_rig.Parent);
        var plan = NativeDependencyResolver.Resolve(request);
        Assert.True(plan.Ready, string.Join("; ", plan.Gaps.Select(gap => gap.Reason)));
        Assert.Equal(new[] { "Dependency.dll" }, plan.Plugins.Select(file => Path.GetFileName(file.File)));
        Assert.Equal(new[] { "valheimCLI.dll", "Valheim.Cli.Standard.dll" }, plan.CliFiles.Select(file => Path.GetFileName(file.File)));
        Assert.Contains("example.soft", plan.OptionalCandidates); // Soft plugins are reported, never auto-staged.
        Assert.Contains("hard [BepInDependency]", plan.Plugins[0].Reason);
        string lockFile = Path.Combine(_rig.Root, "dependency-lock.json");
        plan.Write(lockFile);
        Assert.True(NativeDependencyLock.ReadReady(lockFile).Ready);
        var environment = _rig.Manifest();
        environment.Plugins.Clear();
        plan.ApplyTo(environment, Path.Combine(_rig.Root, "selected-cli.json"));
        Assert.Single(environment.Plugins);
        Assert.Equal("Dependency.dll", Path.GetFileName(environment.Plugins[0].File));
        new TargetedRegression(environment).Stage("parent");
        File.AppendAllText(plan.Plugins[0].File, "changed");
        Assert.Contains("missing or changed", Assert.Throws<InvalidDataException>(() => NativeDependencyLock.ReadReady(lockFile)).Message);
    }

    [Fact] public void AnEditedLockCannotStageAnExtraCliPackOutsideTheSelectedManifest()
    {
        var plan = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        Assert.True(plan.Ready);
        plan.CliFiles.Add(plan.CliFiles[0]);
        string path = Path.Combine(_rig.Root, "edited-lock.json");
        plan.Write(path);
        Assert.Contains("exactly the ValheimCLI core and packs", Assert.Throws<InvalidDataException>(() => NativeDependencyLock.ReadReady(path)).Message);
        Assert.Throws<InvalidDataException>(() => plan.ApplyTo(_rig.Manifest(), Path.Combine(_rig.Root, "should-not-be-written.json")));
        Assert.False(File.Exists(Path.Combine(_rig.Root, "should-not-be-written.json")));
    }

    [Fact] public void AnEditedLockWithoutTheCliCoreIsNotReady()
    {
        var plan = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        var core = Assert.Single(plan.CliManifest.Files.Where(file => file.Plugins.Contains("valheimCLI.valheimCLI")));
        core.Plugins = ["a.different.plugin"];
        string path = Path.Combine(_rig.Root, "no-core-lock.json");
        plan.Write(path);
        Assert.Contains("exactly one ValheimCLI core", Assert.Throws<InvalidDataException>(() => NativeDependencyLock.ReadReady(path)).Message);
    }

    [Fact] public void UniqueReferencedLibraryIsIncludedAndAmbiguityIsLeftForAnExplicitChoice()
    {
        string mod = _rig.Write("uses/Uses.dll", RegressionRig.Assembly("Uses", new("example.uses"), reference: typeof(FactAttribute)));
        string library = _rig.Write("library-one/xunit.core.dll", RegressionRig.Assembly("xunit.core", null));
        var request = Request(mod);
        request.SearchRoots = [Path.Combine(_rig.Root, "library-one")];
        var unique = NativeDependencyResolver.Resolve(request);
        Assert.True(unique.Ready, string.Join("; ", unique.Gaps.Select(gap => gap.Reason)));
        Assert.Equal(library, Assert.Single(unique.Plugins).File);
        Assert.Contains("assembly reference xunit.core", unique.Plugins[0].Reason);

        _rig.Write("library-two/xunit.core.dll", RegressionRig.Assembly("xunit.core", null, marker: "Other"));
        request.SearchRoots.Add(Path.Combine(_rig.Root, "library-two"));
        var ambiguous = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(ambiguous.Gaps);
        Assert.Equal("assembly", gap.Kind);
        Assert.Equal("xunit.core", gap.Name);
        Assert.Equal(2, gap.Candidates.Count);
        Assert.Empty(ambiguous.Plugins);
    }

    [Fact] public void SoftReferenceRequiresExplicitConfirmationBeforeItCanBeOmitted()
    {
        string mod = _rig.Write("soft/Uses.dll", RegressionRig.Assembly("Uses", new("example.uses") { Soft = ["example.soft"] }, reference: typeof(FactAttribute)));
        _rig.Write("soft/xunit.core.dll", RegressionRig.Assembly("xunit.core", new("example.soft")));
        var request = Request(mod);
        request.SearchRoots = [Path.Combine(_rig.Root, "soft")];
        var plan = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal("optional-reference", gap.Kind);
        Assert.Contains("Confirm optionalReferences", gap.Reason);
        Assert.Empty(plan.Plugins);
        request.OptionalReferences = ["xunit.core"];
        var confirmed = NativeDependencyResolver.Resolve(request);
        Assert.True(confirmed.Ready);
        Assert.Empty(confirmed.Plugins);
        Assert.Equal(new[] { "xunit.core" }, confirmed.OptionalReferences);
    }

    [Fact] public void MissingCliCapabilityNamesThePackProblemBeforeAnyInstallIsWritten()
    {
        var request = Request(_rig.Parent);
        request.Capabilities = ["valheim.world/terrain"];
        var error = Assert.Throws<InvalidOperationException>(() => NativeDependencyResolver.Resolve(request));
        Assert.Contains("lacks valheim.world/terrain", error.Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void ManifestPackMissingFromLocalBuildIsAnUnresolvedGap()
    {
        var request = Request(_rig.Parent);
        string coreOnly = Path.Combine(_rig.Root, "cli-core-only");
        Directory.CreateDirectory(coreOnly);
        File.Copy(_rig.Manifest().Cli.Core.File, Path.Combine(coreOnly, "valheimCLI.dll"));
        request.CliFiles = coreOnly;
        var plan = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal("cli", gap.Kind);
        Assert.Equal("Valheim.Cli.Standard.dll", gap.Name);
        Assert.Contains("pinned ValheimCLI build", gap.Reason);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void DuplicatePluginIdentityIsNotSilentlySelected()
    {
        string twin = _rig.Write("twin/Twin.dll", RegressionRig.Assembly("Twin", new("example.mod")));
        var request = Request(_rig.Parent);
        request.Mods.Add(twin);
        var plan = NativeDependencyResolver.Resolve(request);
        Assert.Equal("duplicate-plugin", Assert.Single(plan.Gaps).Kind);
        Assert.Contains("example.mod", plan.Gaps[0].Reason);
    }

    [Fact] public void ComparisonMayChangeOnlyThePrimaryMod()
    {
        var before = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        Assert.True(before.Ready);
        var after = new NativeDependencyLock
        {
            Mods = [new("/alternate/Parent.dll", new string('a', 64), "selected mod")],
            Plugins = [.. before.Plugins], CliFiles = [.. before.CliFiles],
            OptionalReferences = [.. before.OptionalReferences],
        };
        before.RequireSameFixedInputs(after);

        after.Plugins = [new("/alternate/Dependency.dll", new string('b', 64), "dependency")];
        Assert.Contains("plugin dependencies differ", Assert.Throws<InvalidDataException>(() => before.RequireSameFixedInputs(after)).Message);
        after.Plugins = [.. before.Plugins];
        after.Mods.Add(new("/alternate/Companion.dll", new string('c', 64), "selected mod"));
        Assert.Contains("companion mods differ", Assert.Throws<InvalidDataException>(() => before.RequireSameFixedInputs(after)).Message);
        after.Mods.RemoveAt(1);
        after.CliFiles.RemoveAt(0);
        Assert.Contains("ValheimCLI files differ", Assert.Throws<InvalidDataException>(() => before.RequireSameFixedInputs(after)).Message);
        after.CliFiles = [.. before.CliFiles];
        after.OptionalReferences.Add("optional.integration");
        Assert.Contains("optional references differ", Assert.Throws<InvalidDataException>(() => before.RequireSameFixedInputs(after)).Message);
    }

    private NativeDependencyRequest Request(string mod) => new()
    {
        Mods = [mod], SearchRoots = [Path.Combine(_rig.Root, "deps")],
        GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(_rig.Game))!,
        BepInExCore = Path.Combine(_rig.Game, "BepInEx", "core"),
        CliManifest = _rig.CliManifest(save: true), CliFiles = Path.Combine(_rig.Root, "cli"),
        Capabilities = ["valheim.session/state", "valheim.session/save"],
    };
}
