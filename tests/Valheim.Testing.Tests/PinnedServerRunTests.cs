using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The whole runner lifecycle on temporary fixtures: validate for real, launching modes through the internal session
// seam with the fake owned server (no game).
public sealed class PinnedServerRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pinned-run-" + Guid.NewGuid().ToString("N"));
    private string Runtime => Path.Combine(_root, "runtime");
    private string World => Path.Combine(_root, "world");
    private string Output => Path.Combine(_root, "out");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    // runtimePins: the pins to write (default: the runtime's own). Unpinned: pinning "none", no pins and no fixture manifests.
    private string WritePlan(bool linux, string[]? patchers = null, Dictionary<string, object>? logScan = null, InstallPins? runtimePins = null, bool unpinned = false,
        object? client = null)
    {
        Directory.CreateDirectory(Runtime); Directory.CreateDirectory(Path.Combine(World, "worlds_local"));
        string server = Path.Combine(Runtime, linux ? ServerLaunch.LinuxExecutable : ServerLaunch.WindowsExecutable);
        File.WriteAllText(server, "server");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, File.GetUnixFileMode(server) | UnixFileMode.UserExecute);
        FakeInstalls.Server(Runtime);
        File.WriteAllText(Path.Combine(World, "worlds_local", "Test.db"), "world");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var plan = new Dictionary<string, object>
        {
            ["scenario"] = "smoke",
            ["runtime"] = new { source = Runtime, sha256 = unpinned ? new Dictionary<string, string>() : WorldFixture.Manifest(Runtime) },
            ["world"] = new { source = World, sha256 = unpinned ? new Dictionary<string, string>() : WorldFixture.Manifest(World) },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}" },
            ["pins"] = unpinned ? new Dictionary<string, string>() : new Dictionary<string, string> { ["worlduid"] = "1" },
            ["port"] = port < 1024 ? 5577 : port,
            ["patchers"] = patchers ?? [],
            ["logScan"] = logScan ?? [],
        };
        if (unpinned) plan["pinning"] = "none";
        else plan["runtimePins"] = runtimePins ?? InstallPins.Of(Runtime);
        if (client != null) plan["client"] = client;
        string path = Path.Combine(_root, "plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan));
        return path;
    }
    private static PinnedServerRunOptions<ServerRunPlan> Options(Func<PinnedServerRunContext<ServerRunPlan>, Task>? scenario = null,
        FakeOwnedServer? server = null, Action<string, ServerRunPlan>? checkMode = null) => new()
    {
        Name = "toolkit-smoke",
        ReadPlan = path => { var plan = ServerRunPlan.Read<ServerRunPlan>(path); plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return plan; },
        SessionCapability = "test.mod/session", SessionTokenVariable = "TEST_SESSION_TOKEN",
        PrepareModes = ["prepare-fixture"], CheckMode = checkMode, EnableDevcommands = false,
        Scenario = scenario ?? (_ => Task.CompletedTask),
        SessionOverride = server == null ? null : run => server.Session(TimeSpan.FromSeconds(60)),
    };
    private JsonElement Result() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement;
    // A mod plan with a client section, read and validated the way a mod runner's ReadPlan does.
    private static PinnedServerRunOptions<CrossplayPlanTests.ClientPlan> ClientOptions(FakeOwnedServer? server = null) => new()
    {
        Name = "toolkit-smoke",
        ReadPlan = path =>
        {
            var plan = ServerRunPlan.Read<CrossplayPlanTests.ClientPlan>(path);
            plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); plan.Client?.Validate();
            return plan;
        },
        SessionCapability = "test.mod/session", SessionTokenVariable = "TEST_SESSION_TOKEN", EnableDevcommands = false,
        Scenario = _ => Task.CompletedTask,
        SessionOverride = server == null ? null : run => server.Session(TimeSpan.FromSeconds(60)),
    };
    private static object MacClient(string install, string architecture) => new
    {
        mode = "owned", install, architecture, port = 5556, join = "127.0.0.1:2456", character = "Tester", pinning = "none",
    };
    private static bool HostRunsLinux => !OperatingSystem.IsWindows();

    [Fact] public async Task ValidateCopiesAndVerifiesTheFixturesWithoutLaunching()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["validate", plan, Output], Options()));
        var result = Result();
        Assert.True(result.GetProperty("Passed").GetBoolean());
        Assert.Equal(new[] { "copy and verify pinned runtime", "copy and verify pinned world", "copied runtime has the plan's server executable",
                "copied runtime's BepInEx patchers are the plan's", "copied runtime is the pinned game build, BepInEx core and patchers", "prepared only; no game launched" },
            result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
        Assert.Equal("validate", result.GetProperty("Provenance").GetProperty("mode").GetString());
        Assert.Equal(InstallPins.Of(Runtime).Game, result.GetProperty("Provenance").GetProperty("runtimeGameSha256").GetString());
        Assert.Equal("strict", result.GetProperty("Pinning").GetString());
        Assert.True(File.Exists(Path.Combine(Output, "input-hashes.json"))); Assert.True(File.Exists(Path.Combine(Output, "junit.xml")));
    }
    // The launch's architecture check runs when the plan is read: validate refuses the plan, and run refuses it before the server starts.
    [Fact] public async Task AnArm64ClientWithoutTheNativeCoreIsRefusedAtValidateAndBeforeTheServerStarts()
    {
        using var legacy = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.LegacyDetour);
        string plan = WritePlan(linux: HostRunsLinux, client: MacClient(legacy.Root, "arm64"));
        Assert.Contains("MonoMod before 25", Assert.Throws<ArgumentException>(() => ClientOptions().ReadPlan(plan)).Message);
        Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", plan, Output], ClientOptions()));
        Assert.False(Directory.Exists(Output)); // Refused before anything was copied or written.
        var server = new FakeOwnedServer("test.mod");
        Assert.Equal(1, await PinnedServerRun.MainAsync(["run", plan, Output], ClientOptions(server)));
        Assert.Empty(server.Events); Assert.False(Directory.Exists(Output));
        using var native = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.NativeDetour);
        string nativePlan = WritePlan(linux: HostRunsLinux, client: MacClient(native.Root, "arm64"));
        Assert.Equal(ClientArchitecture.Arm64, ClientOptions().ReadPlan(nativePlan).Client!.LaunchArchitecture);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["validate", nativePlan, Output], ClientOptions()));
    }
    [Fact] public async Task ExistingEvidenceIsNeverOverwrittenAndBadUsageIsRefused()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        Directory.CreateDirectory(Output); File.WriteAllText(Path.Combine(Output, "keep.txt"), "evidence");
        Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", plan, Output], Options()));
        Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(Output).Select(Path.GetFileName));
        Assert.Equal(2, await PinnedServerRun.MainAsync(["launch", plan, Output + "2"], Options()));
        Assert.Equal(2, await PinnedServerRun.MainAsync(["validate", plan], Options()));
    }
    [Fact] public async Task AChangedFixtureFailsBeforeAnythingLaunches()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        File.WriteAllText(Path.Combine(World, "worlds_local", "Test.db"), "edited after pinning");
        Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", plan, Output], Options()));
        Assert.False(Result().GetProperty("Passed").GetBoolean());
    }
    [Fact] public async Task RunStartsTheOwnedServerRunsTheScenarioAndStopsOnlyThatServer()
    {
        if (OperatingSystem.IsMacOS()) return; // This fake runtime is Windows or Linux, which a Mac cannot run; validate is covered above.
        string plan = WritePlan(linux: HostRunsLinux);
        var server = new FakeOwnedServer("test.mod"); string? seen = null;
        int code = await PinnedServerRun.MainAsync(["run", plan, Output], Options(run => { seen = run.Mode + ":" + run.Server.GetType().Name; return Task.CompletedTask; }, server));
        Assert.Equal(0, code); Assert.Equal("run:GameActor", seen);
        Assert.Contains("stop1", server.Events);
        var result = Result();
        Assert.Contains("stop only owned server", result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
        Assert.Equal("1", result.GetProperty("Provenance").GetProperty("ownedPids").GetString());
    }
    [Fact] public async Task AFailingScenarioFailsTheRunButStillStopsTheServer()
    {
        if (OperatingSystem.IsMacOS()) return;
        string plan = WritePlan(linux: HostRunsLinux);
        var server = new FakeOwnedServer("test.mod");
        int code = await PinnedServerRun.MainAsync(["prepare-fixture", plan, Output], Options(_ => throw new InvalidOperationException("fixture refused"), server));
        Assert.Equal(1, code); Assert.Contains("stop1", server.Events);
        Assert.Contains(Result().GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "runner failed" && s.GetProperty("Error").GetString() == "fixture refused");
    }
    [Fact] public async Task AFailedTeardownFailsAPassingRunAndKeepsItsEvidence()
    {
        if (OperatingSystem.IsMacOS()) return;
        string plan = WritePlan(linux: HostRunsLinux);
        var server = new FakeOwnedServer("test.mod") { RefuseStop = true };
        int code = await PinnedServerRun.MainAsync(["run", plan, Output], Options(server: server));
        Assert.Equal(1, code); Assert.Contains("stop1", server.Events);
        var result = Result();
        Assert.False(result.GetProperty("Passed").GetBoolean());
        var stop = Assert.Single(result.GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "stop only owned server");
        Assert.False(stop.GetProperty("Passed").GetBoolean()); Assert.Equal("Fake server refused to stop.", stop.GetProperty("Error").GetString());
        var junit = System.Xml.Linq.XDocument.Load(Path.Combine(Output, "junit.xml")).Root!;
        Assert.Equal("1", junit.Attribute("failures")!.Value);
        Assert.Contains(junit.Elements("testcase"), c => c.Attribute("name")!.Value == "stop only owned server" && c.Element("failure") != null);
        Assert.True(File.Exists(Path.Combine(Output, "input-hashes.json")));
        server.RefuseStop = false;
    }
    [Fact] public async Task AModeThePlanDoesNotAllowIsRefusedBeforeCopying()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        int code = await PinnedServerRun.MainAsync(["validate", plan, Output], Options(checkMode: (mode, _) => throw new ArgumentException("smoke plans run only")));
        Assert.Equal(1, code); Assert.False(Directory.Exists(Output));
    }
    [Fact] public void ADedicatedServerWaitsOnThisBootsLogAndTheLoadedWorldPush()
    {
        string runtime = Path.Combine(Path.GetTempPath(), "runtime");
        var events = new ServerRunPlan { Port = 5591 }.DedicatedStartupEvents(runtime);
        Assert.Equal(Path.Combine(runtime, "BepInEx", "LogOutput.log"), events.CliLog);
        Assert.Equal(new[] { StateWait.InWorldNoPlayer }, events.ReadyStates);
        Assert.NotNull(events.States);
        Assert.Matches(events.Listening, "[Info   :valheimCLI] Command server listening on 127.0.0.1:5591");
        Assert.Same(StartupEvents.StartupFailures, events.Failures);
    }
    [Fact] public async Task ALeftoverPatcherFailsValidationUnlessThePlanNamesIt()
    {
        Directory.CreateDirectory(Path.Combine(Runtime, "BepInEx", "patchers"));
        File.WriteAllText(Path.Combine(Runtime, "BepInEx", "patchers", "RemovedMod.Preloader.dll"), "patcher");
        string plan = WritePlan(linux: HostRunsLinux);
        Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", plan, Output], Options()));
        var step = Assert.Single(Result().GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "copied runtime's BepInEx patchers are the plan's");
        Assert.False(step.GetProperty("Passed").GetBoolean()); Assert.Contains("RemovedMod.Preloader.dll", step.GetProperty("Error").GetString());
        // Named, the same runtime passes.
        plan = WritePlan(linux: HostRunsLinux, patchers: ["RemovedMod.Preloader.dll"]);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["validate", plan, Output + "-named"], Options()));
    }
    // A scenario-registered log (an owned client's) is scanned with the run's at teardown: a patch on a method that does
    // not exist fails a passing scenario, unless the plan reclassifies it with a reason.
    private const string BrokenPatchLog = "[Message:   BepInEx] BepInEx 5.4.23.2\n[Error  :   BepInEx] Error loading [Broken Mod 1.0.0] : Exception has been thrown by the target of an invocation.\n" +
        "System.ArgumentException: Undefined target method for patch method static System.Void BrokenMod.Patches::Postfix()\n";
    private Func<PinnedServerRunContext<ServerRunPlan>, Task> RegistersClientLog(string text) => run =>
    {
        string path = Path.Combine(run.Output, "client-boot.game-0.log"); File.WriteAllText(path, text);
        run.Logs.Add(new RunLog("client BepInEx log", path, Required: true));
        return Task.CompletedTask;
    };
    [Fact] public async Task TheTeardownScanFailsAPassingScenarioOnABrokenPatch()
    {
        if (OperatingSystem.IsMacOS()) return;
        string plan = WritePlan(linux: HostRunsLinux);
        var server = new FakeOwnedServer("test.mod");
        Assert.Equal(1, await PinnedServerRun.MainAsync(["run", plan, Output], Options(RegistersClientLog(BrokenPatchLog), server)));
        Assert.Contains("stop1", server.Events);
        var result = Result();
        var scan = Assert.Single(result.GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "scan run logs");
        Assert.False(scan.GetProperty("Passed").GetBoolean());
        Assert.Contains("client BepInEx log: harmony-undefined-target x1, first at line 3", scan.GetProperty("Error").GetString());
        Assert.Equal("client BepInEx log", Assert.Single(result.GetProperty("Logs").EnumerateArray()).GetProperty("Role").GetString());
    }
    [Fact] public async Task ACleanLogOrAReclassifiedPatternPassesTheScan()
    {
        if (OperatingSystem.IsMacOS()) return;
        string plan = WritePlan(linux: HostRunsLinux);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["run", plan, Output], Options(RegistersClientLog("[Message:   BepInEx] BepInEx 5.4.23.2\n"), new FakeOwnedServer("test.mod"))));
        Assert.Contains(Result().GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "scan run logs" && s.GetProperty("Passed").GetBoolean());
        plan = WritePlan(linux: HostRunsLinux, logScan: new() { ["harmony-undefined-target"] = new { severity = "Warning", reason = "The broken patch is the mod's known, reported issue." } });
        string output = Output + "-reclassified";
        Assert.Equal(0, await PinnedServerRun.MainAsync(["run", plan, output], Options(RegistersClientLog(BrokenPatchLog), new FakeOwnedServer("test.mod"))));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json")));
        var count = result.RootElement.GetProperty("Logs")[0].GetProperty("Counts").EnumerateArray().Single(c => c.GetProperty("Pattern").GetString() == "harmony-undefined-target");
        Assert.Equal(1, count.GetProperty("Count").GetInt32()); Assert.Equal("Warning", count.GetProperty("Severity").GetString());
        Assert.Equal("The broken patch is the mod's known, reported issue.", count.GetProperty("Reason").GetString());
    }
    [Fact] public async Task AnUnknownLogScanNameIsRefusedBeforeCopying()
    {
        string plan = WritePlan(linux: HostRunsLinux, logScan: new() { ["no-such-pattern"] = new { severity = "Warning", reason = "x" } });
        Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", plan, Output], Options()));
        Assert.False(Directory.Exists(Output));
    }
    [Fact] public void TheGenericPlanRulesHold()
    {
        var plan = new ServerRunPlan
        {
            Runtime = new() { Source = Path.GetTempPath(), Sha256 = new() { ["a"] = new string('a', 64) } },
            World = new() { Source = Path.GetTempPath(), Sha256 = new() { ["b"] = new string('b', 64) } },
            Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"], Pins = new() { ["worlduid"] = "1", ["my.mod"] = new string('1', 32) },
            RuntimePins = new() { Game = new string('c', 64), BepInExCore = new string('d', 64), Patchers = new string('e', 64) },
        };
        plan.ValidateServerPlan(["my.mod"], "TOKEN");
        Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan(["other.mod"], "TOKEN"));
        plan.Environment["TOKEN"] = "x"; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")); plan.Environment.Clear();
        plan.Environment["doorstop_enabled"] = "0"; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")); plan.Environment.Clear();
        plan.Arguments = ["-batchmode", "-savedir", "{world}"]; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN"));
        plan.Arguments = ["-batchmode", "-nographics", "-savedir", "{world}", "-savedir", "{world}"]; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN"));
        plan.Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"];
        plan.Pins["worldfiles"] = "x"; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")); plan.Pins.Remove("worldfiles");
        plan.Pins["loose.mod"] = "any"; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN"));
    }
}
