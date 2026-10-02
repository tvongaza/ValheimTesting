using Valheim.Testing.Game;
using Xunit;

public sealed class NativeSmokeComparisonTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

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

    private string[] Arguments(string output, string first, string second, bool searchRoot = true)
    {
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(NativeServerRuntime.SessionAdapterPluginGuid)));
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
