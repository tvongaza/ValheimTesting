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
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(SmokeSessionContract.SessionAdapterPluginGuid)));
        string output = Path.Combine(_rig.Root, "offline-run");
        var launched = new List<string[]>();
        // The campaign path needs no generated consumer even when a reviewed loader package is selected.
        int result = await ServerLoad.RunAsync(
            ["--server", _rig.Game, "--mod", _rig.Parent, "--loader-package", manifest, "--server-only",
                "--cli-manifest", _rig.CliManifest(save: true), "--cli-files", Path.Combine(_rig.Root, "cli"),
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
            "--cli-manifest", _rig.CliManifest(save: true), "--cli-files", Path.Combine(_rig.Root, "cli"),
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
            "--cli-manifest", _rig.CliManifest(save: true),
            "--cli-files", Path.Combine(_rig.Root, "cli"), "--search-root", Path.Combine(_rig.Root, "deps"), "--output", output,
        }]);
        Assert.Equal(1, exit);
        var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "evidence", "smoke", "result.json")));
        string runId = report.RootElement.GetProperty("Provenance").GetProperty("runId").GetString()!;
        string fixtureJournal = File.ReadAllText(Path.Combine(_rig.Root, "journal", runId, WorldFixture.Actor + ".jsonl"));
        Assert.Contains(JournalEntry.CopyIntended, fixtureJournal);
        Assert.Contains(JournalEntry.CopyRetired, fixtureJournal);
        Assert.Contains(JournalEntry.RunEnded, File.ReadAllText(Path.Combine(_rig.Root, "journal", runId, "run.jsonl")));
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
