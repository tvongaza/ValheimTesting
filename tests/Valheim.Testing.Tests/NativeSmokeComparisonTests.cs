using Valheim.Testing.Game;
using Xunit;

public sealed class NativeSmokeComparisonTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public async Task OutputInsidePreparedInstallIsRefusedBeforeCreatingEvidenceOrRunningAnArm()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Game, "comparison-evidence");
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion), _ =>
        {
            calls++;
            return Task.FromResult(0);
        });
        Assert.Equal(3, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task OutputInsideSteamUserdataIsRefusedBeforeCreatingEvidenceOrRunningAnArm()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string steam = Path.Combine(_rig.Root, "steam-userdata");
        string output = Path.Combine(steam, "comparison-evidence");
        string[] args = [.. Arguments(output, _rig.Parent, companion), "--client", Path.Combine(_rig.Root, "client"), "--steam-userdata", steam];
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync(args, _ =>
        {
            calls++;
            return Task.FromResult(0);
        });
        Assert.Equal(3, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task IncompatiblePairStopsBeforeCreatingAnOwnedRun()
    {
        string clash = _rig.Write("clash/Clash.dll", RegressionRig.Assembly("Clash",
            new("example.clash") { Incompatible = ["example.mod"] }));
        string output = Path.Combine(_rig.Root, "incompatible-comparison");
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, clash));
        Assert.Equal(3, result);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task MissingHardDependencyStopsBeforeCreatingAnOwnedRun()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "missing-dependency-comparison");
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion, searchRoot: false));
        Assert.Equal(3, result);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task FailedFullSetStillRunsTheRemovalArm()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "runtime-failure-comparison");
        var arms = new List<string[]>();
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion), args =>
        {
            arms.Add(args);
            return Task.FromResult(arms.Count == 1 ? 1 : 0);
        });
        Assert.Equal(1, result);
        Assert.Equal(2, arms.Count);
        Assert.Contains(companion, arms[0]);
        Assert.DoesNotContain(companion, arms[1]);
        Assert.True(File.Exists(Path.Combine(output, "before-dependencies.lock.json")));
        Assert.True(File.Exists(Path.Combine(output, "after-dependencies.lock.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HoldIsRefusedWithoutShiftingArgumentsOrLaunchingAnArm(bool atEnd)
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, atEnd ? "trailing-hold" : "middle-hold");
        var args = Arguments(output, _rig.Parent, companion).ToList();
        args.Insert(atEnd ? args.Count : 2, "--hold");
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync([.. args], _ =>
        {
            calls++;
            return Task.FromResult(0);
        });
        Assert.Equal(3, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task BothArmsKeepParsedSearchRootsOptionalReferencesAndSwitches()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "parsed-comparison");
        string[] arguments = [.. Arguments(output, _rig.Parent, companion),
            "--server-only", "--optional-reference", "Optional.Plugin"];
        var arms = new List<ServerLoad.Arguments>();
        int result = await ServerLoadComparison.RunAsync(arguments, args =>
        {
            Assert.True(ServerLoad.TryRead(args, out var parsed, out string error), error);
            arms.Add(parsed!);
            return Task.FromResult(0);
        });
        Assert.Equal(0, result);
        Assert.Equal(2, arms.Count);
        Assert.All(arms, arm =>
        {
            Assert.True(arm.ServerOnly);
            Assert.Equal([Path.Combine(_rig.Root, "deps")], arm.Roots);
            Assert.Equal(["Optional.Plugin"], arm.Optional);
            Assert.DoesNotContain("--hold", arm.Switches);
        });
        Assert.Equal([_rig.Parent, companion], arms[0].Mods);
        Assert.Equal([_rig.Parent], arms[1].Mods);
        Assert.Equal(Path.Combine(output, "before"), arms[0].Options["--output"]);
        Assert.Equal(Path.Combine(output, "after"), arms[1].Options["--output"]);
    }

    [Fact] public async Task RefusedFullSetDoesNotLaunchTheRemovalArm()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "refused-comparison");
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion), _ =>
        {
            calls++;
            return Task.FromResult(3);
        });
        Assert.Equal(3, result);
        Assert.Equal(1, calls);
    }

    [Fact] public async Task FailedFullSetCannotHideAChangedSurvivingMod()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "changed-after-failure");
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion), _ =>
        {
            calls++;
            File.AppendAllText(_rig.Parent, "changed");
            return Task.FromResult(1);
        });
        Assert.Equal(3, result);
        Assert.Equal(1, calls);
    }

    private string[] Arguments(string output, string first, string second, bool searchRoot = true)
    {
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(SmokeSessionContract.SessionAdapterPluginGuid)));
        var args = new List<string>
        {
            "--server", _rig.Game, "--mod", first, "--mod", second, "--remove-mod", second,
            "--adapter", adapter, "--cli-manifest", _rig.CliManifest(save: true),
            "--cli-files", Path.Combine(_rig.Root, "cli"), "--output", output,
        };
        if (searchRoot) args.AddRange(["--search-root", Path.Combine(_rig.Root, "deps")]);
        return [.. args];
    }
}
