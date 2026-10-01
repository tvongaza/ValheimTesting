using Valheim.Testing.Game;
using Xunit;

public sealed class NativeCleanClientRuntimeTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact]
    public void ClientCopyContainsOnlyCliAndPinsServerModsAbsent()
    {
        var request = new NativeDependencyRequest
        {
            Mods = [_rig.Parent], SearchRoots = [Path.Combine(_rig.Root, "deps")],
            GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(_rig.Game))!,
            BepInExCore = Path.Combine(_rig.Game, "BepInEx", "core"),
            CliManifest = _rig.CliManifest(save: true), CliFiles = Path.Combine(_rig.Root, "cli"),
            Capabilities = ["valheim.session/state", "valheim.session/join", "valheim.session/leave"],
        };
        var dependencies = NativeDependencyResolver.Resolve(request);
        Assert.True(dependencies.Ready, string.Join("; ", dependencies.Gaps.Select(gap => gap.Reason)));
        var original = WorldFixture.Manifest(_rig.Game);
        string staged;
        using (var runtime = NativeCleanClientRuntime.Prepare(_rig.Game, Path.Combine(_rig.Root, "clean-client"), dependencies, 5589))
        {
            staged = runtime.RuntimeDirectory;
            Assert.Equal(new[] { "Valheim.Cli.Standard.dll", "valheimCLI.dll" },
                Directory.GetFiles(Path.Combine(staged, "BepInEx", "plugins")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
            Assert.Empty(Directory.GetFiles(Path.Combine(staged, "BepInEx", "scripts")));
            Assert.Empty(Directory.GetFiles(Path.Combine(staged, "BepInEx", "patchers")));
            string config = File.ReadAllText(Path.Combine(staged, "BepInEx", "config", "valheimCLI.valheimCLI.cfg"));
            Assert.Contains("Port = 5589", config);
            Assert.Contains("AllowOnServerClients = true", config);
            var plan = runtime.Plan(5589, 2486, ["example.mod", "example.dependency"]);
            Assert.Equal("absent", plan.Pins["example.mod"]);
            Assert.Equal("absent", plan.Pins["example.dependency"]);
            Assert.Equal("127.0.0.1:2486", plan.Join);
            Assert.Equal(DefaultSmokeCharacter.Name, plan.Character);
            Assert.Equal(staged, plan.Install);
            Assert.Contains("must match", Assert.Throws<ArgumentException>(() =>
                runtime.Plan(5590, 2486, ["example.mod"])).Message);
        }
        Assert.False(Directory.Exists(staged));
        Assert.Equal(original.OrderBy(entry => entry.Key), WorldFixture.Manifest(_rig.Game).OrderBy(entry => entry.Key));
    }

    [Fact]
    public void ClientCannotPinItsOwnLoadedCliAsAbsent()
    {
        var dependencies = NativeDependencyResolver.Resolve(new NativeDependencyRequest
        {
            Mods = [_rig.Parent], SearchRoots = [Path.Combine(_rig.Root, "deps")],
            GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(_rig.Game))!,
            BepInExCore = Path.Combine(_rig.Game, "BepInEx", "core"),
            CliManifest = _rig.CliManifest(save: true), CliFiles = Path.Combine(_rig.Root, "cli"),
            Capabilities = ["valheim.session/state", "valheim.session/join", "valheim.session/leave"],
        });
        using var runtime = NativeCleanClientRuntime.Prepare(_rig.Game, Path.Combine(_rig.Root, "refused-client"), dependencies, 5589);
        Assert.Contains("would load server plugin", Assert.Throws<InvalidDataException>(() =>
            runtime.Plan(5589, 2486, ["valheimCLI.valheimCLI"])).Message);
    }
}
