using System.Text;
using Valheim.Testing.Game;
using Xunit;

public sealed class NativeServerRuntimeTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public void StagesOnlySelectedServerFilesAndLeavesTheSourceUntouched()
    {
        _rig.Write("game/valheim_server.exe", Encoding.UTF8.GetBytes("fake dedicated executable"));
        _rig.Write("game/BepInEx/scripts/OldScript.dll", Encoding.UTF8.GetBytes("unselected script"));
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new("valheim.testing.native-smoke")));
        var request = new NativeDependencyRequest
        {
            Mods = [_rig.Parent], SearchRoots = [Path.Combine(_rig.Root, "deps")],
            GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(_rig.Game))!,
            BepInExCore = Path.Combine(_rig.Game, "BepInEx", "core"),
            CliManifest = _rig.CliManifest(save: true), CliFiles = Path.Combine(_rig.Root, "cli"),
            Capabilities = ["valheim.session/state", "valheim.session/save"],
        };
        var dependencies = NativeDependencyResolver.Resolve(request);
        Assert.True(dependencies.Ready, string.Join("; ", dependencies.Gaps.Select(gap => gap.Reason)));
        var original = WorldFixture.Manifest(_rig.Game);
        string worldRoot = Path.Combine(_rig.Root, "server-world");
        DefaultSmokeWorld.PrepareServerSaveRoot(worldRoot);
        string settings = _rig.Write("settings/example.mod.cfg", Encoding.UTF8.GetBytes("[Smoke]\nGenerate = false\n"));
        string staged;
        using (var runtime = NativeServerRuntime.Prepare(_rig.Game, Path.Combine(_rig.Root, "server-output"), dependencies, adapter, 5588, [settings]))
        {
            staged = runtime.RuntimeDirectory;
            string plugins = Path.Combine(staged, "BepInEx", "plugins");
            Assert.Equal(new[] { "Dependency.dll", "ExampleMod.dll", "NativeSmoke.SessionAdapter.dll", "Valheim.Cli.Standard.dll", "valheimCLI.dll" },
                Directory.GetFiles(plugins).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { "example.mod" }, runtime.SelectedGuids);
            Assert.Equal(5, runtime.Pins.Count);
            Assert.Empty(Directory.GetFiles(Path.Combine(staged, "BepInEx", "scripts")));
            Assert.Empty(Directory.GetFiles(Path.Combine(staged, "BepInEx", "patchers")));
            Assert.Contains("Port = 5588", File.ReadAllText(Path.Combine(staged, "BepInEx", "config", "valheimCLI.valheimCLI.cfg")));
            Assert.Equal(File.ReadAllBytes(settings), File.ReadAllBytes(Path.Combine(staged, "BepInEx", "config", "example.mod.cfg")));
            Assert.NotEmpty(runtime.Manifest());
            var plan = runtime.Plan(worldRoot, 5588);
            Assert.Equal(DefaultSmokeWorld.Uid, plan.Pins["worlduid"]);
            Assert.Equal("example.mod", plan.Environment[NativeServerRuntime.SelectedGuidsVariable]);
            Assert.Equal(runtime.Pins.Count + 1, plan.Pins.Count);
            Assert.Contains("-nographics", plan.Arguments);
            Assert.Contains("-savedir", plan.Arguments);
            Assert.Equal(worldRoot, plan.World.Source);
            Assert.Equal(staged, plan.Runtime.Source);
            Assert.Contains("must match", Assert.Throws<ArgumentException>(() =>
                runtime.Plan(worldRoot, 5589)).Message);
        }
        Assert.False(Directory.Exists(staged));
        Assert.Equal("[Smoke]\nGenerate = false\n", File.ReadAllText(settings));
        Assert.Equal(original.OrderBy(entry => entry.Key), WorldFixture.Manifest(_rig.Game).OrderBy(entry => entry.Key));
        Assert.True(File.Exists(Path.Combine(_rig.Game, "BepInEx", "plugins", "Unrelated.dll")));
        string duplicate = _rig.Write("other/example.mod.cfg", Encoding.UTF8.GetBytes("different"));
        string refused = Path.Combine(_rig.Root, "duplicate-config-output");
        Assert.Contains("share filename", Assert.Throws<InvalidDataException>(() =>
            NativeServerRuntime.Prepare(_rig.Game, refused, dependencies, adapter, 5588, [settings, duplicate])).Message);
        Assert.False(Directory.Exists(refused));
    }

    [Fact] public void DuplicateAdapterIdentityIsRefusedBeforeCopying()
    {
        _rig.Write("game/valheim_server.exe", Encoding.UTF8.GetBytes("fake dedicated executable"));
        string primary = _rig.Write("parent/Imposter.dll",
            RegressionRig.Assembly("Imposter", new(NativeServerRuntime.SessionAdapterPluginGuid)));
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(NativeServerRuntime.SessionAdapterPluginGuid)));
        var dependencies = new NativeDependencyLock
        {
            Mods = [new(primary, WorldFixture.Hash(primary), "selected mod")],
        };
        string output = Path.Combine(_rig.Root, "refused-server-output");
        Assert.Contains("declare plugin " + NativeServerRuntime.SessionAdapterPluginGuid, Assert.Throws<InvalidDataException>(() =>
            NativeServerRuntime.Prepare(_rig.Game, output, dependencies, adapter, 5588)).Message);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public void UnrelatedAdapterIsRefusedBeforeCopying()
    {
        _rig.Write("game/valheim_server.exe", Encoding.UTF8.GetBytes("fake dedicated executable"));
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new("another.testing.plugin")));
        var dependencies = new NativeDependencyLock
        {
            Mods = [new(_rig.Parent, WorldFixture.Hash(_rig.Parent), "selected mod")],
        };
        string output = Path.Combine(_rig.Root, "wrong-adapter-output");
        Assert.Contains(NativeServerRuntime.SessionAdapterPluginGuid, Assert.Throws<InvalidDataException>(() =>
            NativeServerRuntime.Prepare(_rig.Game, output, dependencies, adapter, 5588)).Message);
        Assert.False(Directory.Exists(output));
    }
}
