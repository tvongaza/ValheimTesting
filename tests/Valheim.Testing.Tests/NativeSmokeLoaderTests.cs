using System.Text;
using Valheim.Testing.Game;
using Xunit;

public sealed class NativeSmokeLoaderTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact]
    public void UnmoddedSourceServerAndClientReceiveSeparatePinnedLoaders()
    {
        _rig.Write("game/valheim_server.exe", Encoding.UTF8.GetBytes("fake dedicated executable"));
        var serverLoader = Package("server-loader");
        var clientLoader = Package("client-loader");
        File.AppendAllText(Path.Combine(clientLoader.Root, "BepInEx", "core", "BepInEx.dll"), "client build");
        clientLoader = BepInExLoaderPackage.Capture(clientLoader.Root, "client-loader", "test");
        Assert.NotEqual(serverLoader.Files["BepInEx/core/BepInEx.dll"], clientLoader.Files["BepInEx/core/BepInEx.dll"]);
        Directory.Delete(Path.Combine(_rig.Game, "BepInEx"), recursive: true);
        var source = WorldFixture.Manifest(_rig.Game);
        var dependencies = Dependencies(serverLoader);
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(NativeServerRuntime.SessionAdapterPluginGuid)));
        string serverCopy, clientCopy;

        using (var server = NativeServerRuntime.Prepare(_rig.Game, Path.Combine(_rig.Root, "server-copy"),
                   dependencies, adapter, 5588, loaderPackage: serverLoader))
        using (var client = NativeCleanClientRuntime.Prepare(_rig.Game, Path.Combine(_rig.Root, "client-copy"),
                   dependencies, 5589, clientLoader))
        {
            serverCopy = server.RuntimeDirectory;
            clientCopy = client.RuntimeDirectory;
            Assert.Equal(serverLoader.Files["BepInEx/core/BepInEx.dll"],
                WorldFixture.Hash(Path.Combine(server.RuntimeDirectory, "BepInEx", "core", "BepInEx.dll")));
            Assert.Equal(clientLoader.Files["BepInEx/core/BepInEx.dll"],
                WorldFixture.Hash(Path.Combine(client.RuntimeDirectory, "BepInEx", "core", "BepInEx.dll")));
            foreach (var (runtime, loader) in new[] { (server.RuntimeDirectory, serverLoader), (client.RuntimeDirectory, clientLoader) })
                Assert.Equal(loader.Files["BepInEx/config/BepInEx.cfg"],
                    WorldFixture.Hash(Path.Combine(runtime, "BepInEx", "config", "BepInEx.cfg")));
            Assert.Equal("absent", client.Plan(5589, 2486, ["example.mod"]).Pins["example.mod"]);
        }
        Assert.Equal(source.OrderBy(item => item.Key), WorldFixture.Manifest(_rig.Game).OrderBy(item => item.Key));
        Assert.False(Directory.Exists(serverCopy));
        Assert.False(Directory.Exists(clientCopy));
    }

    [Fact]
    public void ChangedLoaderPackageIsRefusedBeforeMakingACopy()
    {
        _rig.Write("game/valheim_server.exe", Encoding.UTF8.GetBytes("fake dedicated executable"));
        var loader = Package("reviewed-loader");
        var dependencies = Dependencies(loader);
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(NativeServerRuntime.SessionAdapterPluginGuid)));
        File.AppendAllText(Path.Combine(loader.Root, "BepInEx", "core", "BepInEx.dll"), "changed");
        string serverOutput = Path.Combine(_rig.Root, "refused-server");
        string clientOutput = Path.Combine(_rig.Root, "refused-client");
        Assert.Contains("missing or changed", Assert.Throws<InvalidDataException>(() =>
            NativeServerRuntime.Prepare(_rig.Game, serverOutput, dependencies, adapter, 5588,
                loaderPackage: loader)).Message);
        Assert.Contains("missing or changed", Assert.Throws<InvalidDataException>(() =>
            NativeCleanClientRuntime.Prepare(_rig.Game, clientOutput, dependencies, 5589, loader)).Message);
        Assert.False(Directory.Exists(serverOutput));
        Assert.False(Directory.Exists(clientOutput));
    }

    [Fact]
    public void ALoaderCapturedFromTheSourceInstallIsNotASeparatePackage()
    {
        _rig.Write("game/valheim_server.exe", Encoding.UTF8.GetBytes("fake dedicated executable"));
        var loader = BepInExLoaderPackage.Capture(_rig.Game, "live-source", "test");
        var dependencies = Dependencies(loader);
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(NativeServerRuntime.SessionAdapterPluginGuid)));
        string output = Path.Combine(_rig.Root, "refused-live-package");
        Assert.Contains("outside the source server", Assert.Throws<InvalidOperationException>(() =>
            NativeServerRuntime.Prepare(_rig.Game, output, dependencies, adapter, 5588,
                loaderPackage: loader)).Message);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task RemovingOneModKeepsTheSameLoaderPackagesInBothArms()
    {
        _rig.Write("game/valheim_server.exe", Encoding.UTF8.GetBytes("fake dedicated executable"));
        var loader = Package("ab-server-loader");
        string manifest = Path.Combine(_rig.Root, "ab-loader.json");
        loader.Write(manifest);
        Directory.Delete(Path.Combine(_rig.Game, "BepInEx"), recursive: true);
        string partner = _rig.Write("partner/Partner.dll",
            RegressionRig.Assembly("Partner", new("example.partner")));
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(NativeServerRuntime.SessionAdapterPluginGuid)));
        var arms = new List<string[]>();
        int result = await ServerLoadComparison.RunAsync(
            ["--server", _rig.Game, "--mod", _rig.Parent, "--mod", partner,
                "--remove-mod", partner, "--loader-package", manifest,
                "--cli-manifest", _rig.CliManifest(save: true), "--cli-files", Path.Combine(_rig.Root, "cli"),
                "--search-root", Path.Combine(_rig.Root, "deps"), "--adapter", adapter,
                "--output", Path.Combine(_rig.Root, "ab-run")],
            arguments => { arms.Add(arguments); return Task.FromResult(0); });
        Assert.Equal(0, result);
        Assert.Equal(2, arms.Count);
        Assert.All(arms, arguments => Assert.Equal(manifest,
            arguments[Array.IndexOf(arguments, "--loader-package") + 1]));
        Assert.DoesNotContain(partner, arms[1]);
    }

    // #297: server-load reaches the pinned launch from the tool's own assemblies. No consumer project is generated or
    // built and nothing is restored before the game starts; with --adapter, the run needs no NuGet.org at all.
    [Fact]
    public async Task ServerLoadReachesTheLaunchWithoutBuildingAConsumer()
    {
        _rig.Write("game/valheim_server.exe", Encoding.UTF8.GetBytes("fake dedicated executable"));
        var loader = Package("offline-server-loader");
        string manifest = Path.Combine(_rig.Root, "offline-loader.json");
        loader.Write(manifest);
        Directory.Delete(Path.Combine(_rig.Game, "BepInEx"), recursive: true);
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(NativeServerRuntime.SessionAdapterPluginGuid)));
        string output = Path.Combine(_rig.Root, "offline-run");
        var launched = new List<string[]>();
        // The staged (macOS) path; the campaign route's equivalent is ServerLoadOneOffTests.
        int result = await ServerLoad.RunAsync(
            ["--server", _rig.Game, "--mod", _rig.Parent, "--loader-package", manifest, "--server-only",
                "--cli-manifest", _rig.CliManifest(save: true), "--cli-files", Path.Combine(_rig.Root, "cli"),
                "--search-root", Path.Combine(_rig.Root, "deps"), "--adapter", adapter, "--output", output],
            new ServerLoad.Seams(MacOS: true, Staged: (arguments, options) =>
            {
                launched.Add(arguments);
                Assert.True(File.Exists(arguments[1]), "The plan is written before the launch.");
                return Task.FromResult(0);
            }));
        Assert.Equal(0, result);
        Assert.Single(launched);
        Assert.Equal(Path.Combine(output, "plan.json"), launched[0][1]);
        Assert.False(Directory.Exists(Path.Combine(output, "consumer")));
    }

    // #297: start goes straight to the hosted run from the tool's own assemblies; no consumer project comes first. The
    // run stops at its first Setup step here (this test machine has no Steam, so no userdata to check the character
    // against), before anything outside the output changes. Its client is the --game override, written beside its inputs.
    [Fact]
    public void StartRunsTheHostedRunWithoutBuildingAConsumer()
    {
        string output = Path.Combine(_rig.Root, "offline-start");
        object? exit = typeof(SmokeProject).Assembly.EntryPoint!.Invoke(null, [new[]
        {
            "start", "--game", _rig.Game, "--mod", _rig.Parent, "--cli-manifest", _rig.CliManifest(save: true),
            "--cli-files", Path.Combine(_rig.Root, "cli"), "--search-root", Path.Combine(_rig.Root, "deps"), "--output", output,
        }]);
        Assert.Equal(1, exit);
        var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "evidence", "smoke", "result.json")));
        var steps = report.RootElement.GetProperty("Steps").EnumerateArray().ToList();
        Assert.Equal("stage only the registered disposable character", steps[0].GetProperty("Name").GetString());
        Assert.Contains("userdata", steps[0].GetProperty("Error").GetString());
        Assert.False(Directory.Exists(Path.Combine(output, "consumer")));
        // The inputs name no machine; the machine is the override beside them.
        Assert.DoesNotContain(_rig.Game.Replace("\\", "\\\\"), File.ReadAllText(Path.Combine(output, "regression.json")));
        Assert.True(File.Exists(Path.Combine(output, "environments.json")));
    }

    private NativeDependencyLock Dependencies(BepInExLoaderPackage loader)
    {
        var lockFile = NativeDependencyResolver.Resolve(new NativeDependencyRequest
        {
            Mods = [_rig.Parent], SearchRoots = [Path.Combine(_rig.Root, "deps")],
            GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(_rig.Game))!,
            BepInExCore = Path.Combine(loader.Root, "BepInEx", "core"),
            CliManifest = _rig.CliManifest(save: true), CliFiles = Path.Combine(_rig.Root, "cli"),
            Capabilities = ["valheim.session/state", "valheim.session/join", "valheim.session/leave"],
        });
        Assert.True(lockFile.Ready, string.Join("; ", lockFile.Gaps.Select(gap => gap.Reason)));
        return lockFile;
    }

    private BepInExLoaderPackage Package(string name)
    {
        var captured = BepInExLoaderPackage.Capture(_rig.Game, name, "test");
        string root = Path.Combine(_rig.Root, name);
        foreach (string relative in captured.Files.Keys)
        {
            string source = Path.Combine(_rig.Game, relative.Replace('/', Path.DirectorySeparatorChar));
            string target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        return BepInExLoaderPackage.Capture(root, name, "test");
    }
}
