using System.Text;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class NativeSmokeLoaderTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    private readonly IDisposable _preflight = LocalHostPreflight.ReplaceDefaultProbesForTest(new(
        Processes: (_, _, _, _) => Task.CompletedTask, Port: (_, _, _, _) => Task.CompletedTask,
        Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
        Desktop: _ => Task.CompletedTask, MacDesktop: () => { }, SteamRunning: () => true,
        Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null));
    public void Dispose() { _preflight.Dispose(); _rig.Dispose(); }

    [Fact]
    public async Task OneShotScenarioBuildCannotWriteInsideTheSelectedCliBundle()
    {
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_rig.Root, "source-guard-journal"));
        _rig.Write("game/" + ServerRunPlan.ExecutableFor(HostProfile.CurrentPlatform switch
        {
            "windows" => ServerPlatform.Windows, "macos" => ServerPlatform.MacOS, _ => ServerPlatform.Linux,
        }), Encoding.UTF8.GetBytes("fake dedicated executable"));
        string manifest = _rig.CliManifest(save: true, full: true);
        string cli = Path.Combine(_rig.Root, "cli");
        string project = _rig.Write("source-guard-project/Scenario.csproj", Encoding.UTF8.GetBytes(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"));
        string serverOutput = Path.Combine(cli, "bad-server-build");
        int server = await ServerLoad.RunAsync(
            ["--server", _rig.Game, "--mod", _rig.Parent, "--server-only", "--scenario-project", project,
                "--cli-manifest", manifest, "--cli-files", cli, "--output", serverOutput],
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(new CampaignPreflightReport([]))));
        Assert.Equal(3, server);
        Assert.False(Path.Exists(serverOutput));

        string hostedOutput = Path.Combine(cli, "bad-hosted-build");
        object? hosted = typeof(SmokeProject).Assembly.EntryPoint!.Invoke(null, [new[]
        {
            "start", "--game", _rig.Game, "--mod", _rig.Parent, "--scenario-project", project,
            "--client-architecture", "x64", "--cli-manifest", manifest, "--cli-files", cli, "--output", hostedOutput,
        }]);
        Assert.Equal(3, hosted);
        Assert.False(Path.Exists(hostedOutput));

        var loader = Package("source-guard-loader");
        string loaderManifest = Path.Combine(_rig.Root, "source-guard-loader.json");
        loader.Write(loaderManifest);
        string serverLoaderOutput = Path.Combine(loader.Root, "bad-server-build");
        Assert.Equal(3, await ServerLoad.RunAsync(
            ["--server", _rig.Game, "--mod", _rig.Parent, "--server-only", "--scenario-project", project,
                "--loader-package", loaderManifest, "--cli-manifest", manifest, "--cli-files", cli,
                "--output", serverLoaderOutput],
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(new CampaignPreflightReport([])))));
        Assert.False(Path.Exists(serverLoaderOutput));

        string hostedLoaderOutput = Path.Combine(loader.Root, "bad-hosted-build");
        hosted = typeof(SmokeProject).Assembly.EntryPoint!.Invoke(null, [new[]
        {
            "start", "--game", _rig.Game, "--mod", _rig.Parent, "--scenario-project", project,
            "--client-loader-package", loaderManifest, "--client-architecture", "x64",
            "--cli-manifest", manifest, "--cli-files", cli, "--output", hostedLoaderOutput,
        }]);
        Assert.Equal(3, hosted);
        Assert.False(Path.Exists(hostedLoaderOutput));
    }

    [Fact]
    public async Task EveryOneShotCommandLocksTheSameCompleteCliBundle()
    {
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_rig.Root, "all-packs-journal"));
        _rig.Write("game/" + ServerRunPlan.ExecutableFor(HostProfile.CurrentPlatform switch
        {
            "windows" => ServerPlatform.Windows, "macos" => ServerPlatform.MacOS, _ => ServerPlatform.Linux,
        }), Encoding.UTF8.GetBytes("fake dedicated executable"));
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(SmokeSessionContract.SessionAdapterPluginGuid)));
        string manifest = _rig.CliManifest(save: true, full: true);
        string files = Path.Combine(_rig.Root, "cli");
        string[] shared = ["--mod", _rig.Parent, "--cli-manifest", manifest, "--cli-files", files,
            "--search-root", Path.Combine(_rig.Root, "deps")];
        string start = Path.Combine(_rig.Root, "all-packs-start");
        string[] startArgs = ["start", "--game", _rig.Game, "--client-architecture", "x64", "--output", start, .. shared];
        object? started = typeof(SmokeProject).Assembly.EntryPoint!.Invoke(null, [startArgs]);
        Assert.Equal(1, started); // The rig has no registered Steam character; resolution already wrote its lock.

        string server = Path.Combine(_rig.Root, "all-packs-server");
        string[] serverArgs = ["--server", _rig.Game, "--adapter", adapter, "--server-only", "--preflight-only",
            "--output", server, .. shared];
        var ready = new CampaignPreflightReport([]);
        Assert.Equal(0, await ServerLoad.RunAsync(serverArgs,
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(ready))));

        string baked = Path.Combine(_rig.Root, "all-packs-bake");
        var bakeArgs = serverArgs.Where(arg => arg != "--preflight-only").ToList();
        bakeArgs[bakeArgs.IndexOf(server)] = baked;
        Assert.Equal(1, await ServerLoad.RunAsync([.. bakeArgs, "--bake-fixture", Path.Combine(_rig.Root, "baked"),
            "--assert-command", "mymod_new_alias", "--assert-line", "READY"],
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(ready),
                Campaign: (_, _, _, _, _) => Task.FromResult(1))));

        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string comparison = Path.Combine(_rig.Root, "all-packs-ab");
        Assert.Equal(0, await ServerLoadComparison.RunAsync(["--server", _rig.Game, "--adapter", adapter,
            "--server-only", "--output", comparison, "--mod", _rig.Parent, "--mod", companion,
            "--remove-mod", companion, "--cli-manifest", manifest, "--cli-files", files,
            "--search-root", Path.Combine(_rig.Root, "deps")], _ => Task.FromResult(0)));

        static (string File, string Hash)[] Pins(string path)
        {
            var resolved = NativeDependencyLock.ReadReady(path);
            Assert.Equal(resolved.CliManifest.Files.Count, resolved.CliFiles.Count);
            return resolved.CliFiles.Select(file => (Path.GetFileName(file.File), file.Sha256)).ToArray();
        }
        var expected = Pins(Path.Combine(start, "dependencies.lock.json"));
        Assert.Equal(5, expected.Length); // Core, Standard, World Tools, Observe, and otherwise-unused Reflection.
        foreach (string path in new[] { Path.Combine(server, "dependencies.lock.json"),
            Path.Combine(baked, "dependencies.lock.json"), Path.Combine(comparison, "before-dependencies.lock.json"),
            Path.Combine(comparison, "after-dependencies.lock.json") })
            Assert.Equal(expected, Pins(path));
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
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(SmokeSessionContract.SessionAdapterPluginGuid)));
        var arms = new List<string[]>();
        int result = await ServerLoadComparison.RunAsync(
            ["--server", _rig.Game, "--mod", _rig.Parent, "--mod", partner,
                "--remove-mod", partner, "--loader-package", manifest,
                "--cli-manifest", _rig.CliManifest(save: true, full: true), "--cli-files", Path.Combine(_rig.Root, "cli"),
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
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(SmokeSessionContract.SessionAdapterPluginGuid)));
        string output = Path.Combine(_rig.Root, "offline-run");
        var launched = new List<string[]>();
        // The campaign path needs no generated consumer even when a reviewed loader package is selected.
        int result = await ServerLoad.RunAsync(
            ["--server", _rig.Game, "--mod", _rig.Parent, "--loader-package", manifest, "--server-only",
                "--cli-manifest", _rig.CliManifest(save: true, full: true), "--cli-files", Path.Combine(_rig.Root, "cli"),
                "--search-root", Path.Combine(_rig.Root, "deps"), "--adapter", adapter, "--output", output],
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(new CampaignPreflightReport([])),
                Campaign: (campaign, _, _, _, _) =>
            {
                launched.Add([campaign]);
                Assert.True(File.Exists(campaign), "The campaign is written before the launch.");
                return Task.FromResult(0);
            }));
        Assert.Equal(0, result);
        Assert.Single(launched);
        Assert.Equal(Path.Combine(output, "campaign.json"), launched[0][0]);
        Assert.False(Directory.Exists(Path.Combine(output, "consumer")));
    }

    [Fact]
    public void StartRefusesMissingSteamBeforeWritingAnOutput()
    {
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_rig.Root, "steam-refusal-journal"));
        using var probes = LocalHostPreflight.ReplaceDefaultProbesForTest(new(
            Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
            Port: (_, _, _, _) => Task.CompletedTask, Processes: (_, _, _, _) => Task.CompletedTask,
            Desktop: _ => Task.CompletedTask, MacDesktop: () => { }, SteamRunning: () => false,
            Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null));
        string output = Path.Combine(_rig.Root, "missing-steam");
        object? exit = typeof(SmokeProject).Assembly.EntryPoint!.Invoke(null, [new[]
        {
            "start", "--game", _rig.Game, "--mod", _rig.Parent, "--client-architecture", "x64",
            "--cli-manifest", _rig.CliManifest(save: true, full: true), "--cli-files", Path.Combine(_rig.Root, "cli"),
            "--search-root", Path.Combine(_rig.Root, "deps"), "--output", output,
        }]);
        Assert.Equal(3, exit);
        Assert.False(Directory.Exists(output));
    }

    // #297: start goes straight to the hosted run from the tool's own assemblies; no consumer project comes first. The
    // run stops at its first Setup step here (this test machine has no Steam, so no userdata to check the character
    // against), before anything outside the output changes. Its client is the --game override, written beside its inputs.
    [Fact]
    public void StartRunsTheHostedRunWithoutBuildingAConsumer()
    {
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_rig.Root, "journal"));
        // This test stops while staging the disposable character, before launching a client. The local-host probes
        // above keep its result independent of the CI worker's Steam and desktop state.
        string output = Path.Combine(_rig.Root, "offline-start");
        object? exit = typeof(SmokeProject).Assembly.EntryPoint!.Invoke(null, [new[]
        {
            "start", "--game", _rig.Game, "--mod", _rig.Parent, "--client-architecture", "x64", // Synthetic Mac loader is x64-only.
            "--cli-manifest", _rig.CliManifest(save: true, full: true),
            "--cli-files", Path.Combine(_rig.Root, "cli"), "--search-root", Path.Combine(_rig.Root, "deps"), "--output", output,
        }]);
        Assert.Equal(1, exit);
        var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "evidence", "smoke", "result.json")));
        string runId = report.RootElement.GetProperty("Provenance").GetProperty("runId").GetString()!;
        // The registered character refusal precedes ownership of a character, game or fixture copy.
        // No empty run journal is needed for a refusal before any owned resource exists.
        Assert.False(Directory.Exists(Path.Combine(_rig.Root, "journal", runId)));
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
