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
    private string WritePlan(bool linux, Dictionary<string, object>? logScan = null, InstallPins? runtimePins = null, bool unpinned = false,
        object? client = null)
    {
        Directory.CreateDirectory(Runtime); Directory.CreateDirectory(Path.Combine(World, "worlds_local"));
        string server = Path.Combine(Runtime, linux ? GameLaunch.ServerLinuxExecutable : GameLaunch.ServerWindowsExecutable);
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
            ["logScan"] = logScan ?? [],
        };
        if (unpinned) plan["pinning"] = "none";
        else plan["runtimePins"] = runtimePins ?? InstallPins.Of(Runtime);
        if (client != null) plan["client"] = client;
        string path = Path.Combine(_root, "plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan));
        return path;
    }
    private static PinnedServerRunOptions<ServerRunPlan> Options(Func<TestRun<ServerRunPlan>, Task>? scenario = null,
        FakeOwnedServer? server = null, Action<ServerRunPlan>? checkPlan = null) => new()
    {
        Name = "toolkit-smoke",
        ReadPlan = path => { var plan = ServerRunPlan.Read<ServerRunPlan>(path); plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return plan; },
        SessionCapability = "test.mod/session", SessionTokenVariable = "TEST_SESSION_TOKEN",
        CheckPlan = checkPlan,
        Scenario = TestRun.Scenario(scenario ?? (_ => Task.CompletedTask)),
        SessionOverride = server == null ? null : _ => server.Session(TimeSpan.FromSeconds(60)),
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
        SessionCapability = "test.mod/session", SessionTokenVariable = "TEST_SESSION_TOKEN",
        Scenario = (_, _) => Task.CompletedTask,
        SessionOverride = server == null ? null : _ => server.Session(TimeSpan.FromSeconds(60)),
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
        Assert.Equal(new[] { "enough free disk space for the copies", "copy and verify pinned runtime", "copy and verify pinned world", "copied runtime has the plan's server executable",
                "copied runtime is the pinned game build, loader and patchers", "prepared only; no game launched",
                "remove the runtime copy, keeping what the run changed" },
            result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
        // Validate prepares and cleans up; it runs no scenario, so the scenario state is not reported as passed.
        Assert.Equal((true, true, false, true), (result.GetProperty("PreflightPassed").GetBoolean(), result.GetProperty("RuntimeReady").GetBoolean(),
            result.GetProperty("ScenarioPassed").GetBoolean(), result.GetProperty("CleanupVerified").GetBoolean()));
        Assert.Equal(new[] { "Preflight", "Setup", "Setup", "Setup", "Setup", "Setup", "Cleanup" },
            result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Phase").GetString()));
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
    // #256 amendment C: a restarted server process has neither cheat gate, so the session establishes test access on every
    // boot it starts, the scenario's restarts included; a server that never acknowledges fails its start, asked once.
    [Fact] public async Task TheDefaultRunEstablishesTestAccessOnEveryBootAndAServerThatNeverAcknowledgesFails()
    {
        if (OperatingSystem.IsMacOS()) return; // This fake runtime is Windows or Linux, which a Mac cannot run.
        string plan = WritePlan(linux: HostRunsLinux);
        var server = new FakeOwnedServer("test.mod");
        TestAccessState? afterRestart = null;
        Assert.Equal(0, await PinnedServerRun.MainAsync(["run", plan, Output], Options(server: server, scenario: run =>
        {
            afterRestart = TestAccess.Read(run.Session.Restart());
            return Task.CompletedTask;
        })));
        Assert.Equal(new[] { "devcommands1", "confirmcheats1", "devcommands2", "confirmcheats2" },
            server.Events.Where(e => e.StartsWith("devcommands") || e.StartsWith("confirmcheats")));
        Assert.True(afterRestart is { Devcommands: true, CheatsAcknowledged: true });
        Directory.Delete(Output, true);
        var stubborn = new FakeOwnedServer("test.mod") { IgnoreConfirmCheats = true };
        Assert.Equal(1, await PinnedServerRun.MainAsync(["run", plan, Output], Options(server: stubborn)));
        var start = Result().GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == "start and verify owned dedicated fixture");
        Assert.False(start.GetProperty("Passed").GetBoolean());
        Assert.Contains("The owned server started, but its test access was not established", start.GetProperty("Error").GetString());
        Assert.Single(stubborn.Events.Where(e => e.StartsWith("confirmcheats"))); // once, never retried
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
        int code = await PinnedServerRun.MainAsync(["run", plan, Output], Options(run => { seen = run.Server.GetType().Name; return Task.CompletedTask; }, server));
        Assert.Equal(0, code); Assert.Equal("GameActor", seen);
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
        int code = await PinnedServerRun.MainAsync(["run", plan, Output], Options(_ => throw new InvalidOperationException("fixture refused"), server));
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
        // The failed stop is in the Cleanup suite; the runtime was ready, and cleanup is not verified.
        var cleanup = junit.Elements("testsuite").Single(s => s.Attribute("name")!.Value.EndsWith(" / cleanup", StringComparison.Ordinal));
        Assert.Contains(cleanup.Elements("testcase"), c => c.Attribute("name")!.Value == "stop only owned server" && c.Element("failure") != null);
        Assert.Equal((true, false), (result.GetProperty("RuntimeReady").GetBoolean(), result.GetProperty("CleanupVerified").GetBoolean()));
        Assert.True(File.Exists(Path.Combine(Output, "input-hashes.json")));
        server.RefuseStop = false;
    }
    // #194: after a clean stop the runtime copy goes, keeping what the run wrote in it; the world copy and the evidence stay.
    private string Copy(JsonElement result, string name) => result.GetProperty("Provenance").GetProperty(name).GetString()!;
    [Fact] public async Task ACleanRunKeepsWhatItWroteInTheRuntimeAndRemovesTheCopy()
    {
        if (OperatingSystem.IsMacOS()) return;
        string plan = WritePlan(linux: HostRunsLinux);
        var server = new FakeOwnedServer("test.mod");
        int code = await PinnedServerRun.MainAsync(["run", plan, Output], Options(run =>
        {
            Directory.CreateDirectory(Path.Combine(run.RuntimeDirectory, "BepInEx", "cache"));
            File.WriteAllText(Path.Combine(run.RuntimeDirectory, "BepInEx", "cache", "audit.txt"), "written during the run");
            return Task.CompletedTask;
        }, server));
        Assert.Equal(0, code);
        var result = Result();
        Assert.False(Directory.Exists(Copy(result, "runtime")));
        Assert.True(Directory.Exists(Copy(result, "world")));
        Assert.Equal("written during the run", File.ReadAllText(Path.Combine(Output, "runtime-changes", "BepInEx", "cache", "audit.txt")));
        Assert.True(File.Exists(Path.Combine(Output, "runtime-changes", "changes.json")));
        Assert.StartsWith("removed " + Copy(result, "runtime"), Copy(result, "runtimeCopy"));
        Assert.True(long.Parse(Copy(result, "outputBytes")) > 0);
        Assert.True(long.Parse(Copy(result, "freeBytesBeforeCopies")) > 0);
    }
    [Fact] public async Task ValidateAndAFailedScenarioRemoveTheRuntimeCopyToo()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["validate", plan, Output], Options()));
        Assert.False(Directory.Exists(Copy(Result(), "runtime")));
        Assert.EndsWith("nothing was launched", Copy(Result(), "runtimeCopy"));
        Assert.False(Directory.Exists(Path.Combine(Output, "runtime-changes")));
        if (OperatingSystem.IsMacOS()) return;
        Directory.Delete(Output, true);
        var server = new FakeOwnedServer("test.mod");
        Assert.Equal(1, await PinnedServerRun.MainAsync(["run", plan, Output], Options(_ => throw new InvalidOperationException("fixture refused"), server)));
        var result = Result();
        Assert.False(Directory.Exists(Copy(result, "runtime"))); Assert.True(Directory.Exists(Copy(result, "world")));
    }
    [Fact] public async Task ARuntimeIsKeptWhenAskedOrWhenItsServerMayStillRun()
    {
        if (OperatingSystem.IsMacOS()) return;
        string plan = WritePlan(linux: HostRunsLinux);
        RunRetirement.KeepOverride.Value = true; // VALHEIM_TESTING_KEEP_RUNTIME=1, in this test's flow only
        try { Assert.Equal(0, await PinnedServerRun.MainAsync(["run", plan, Output], Options(server: new FakeOwnedServer("test.mod")))); }
        finally { RunRetirement.KeepOverride.Value = null; }
        var kept = Result();
        Assert.True(Directory.Exists(Copy(kept, "runtime"))); Assert.Contains("kept on request", Copy(kept, "runtimeCopy"));
        Assert.False(Directory.Exists(Path.Combine(Output, "runtime-changes")));

        string refused = Output + "-refused";
        var stubborn = new FakeOwnedServer("test.mod") { RefuseStop = true };
        Assert.Equal(1, await PinnedServerRun.MainAsync(["run", plan, refused], Options(server: stubborn)));
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(refused, "result.json"))).RootElement;
        Assert.True(Directory.Exists(Copy(result, "runtime")));
        Assert.Contains("did not stop cleanly", Copy(result, "runtimeCopy"));
        stubborn.RefuseStop = false;
    }
    // A runner that staged its own runtime copy (NativeSmoke) runs from it: one runtime copy, not two.
    private (string Plan, WorldFixture Staged) StagedPlan()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        var staged = WorldFixture.Copy(Runtime, Path.Combine(_root, "staged"), WorldFixture.Manifest(Runtime));
        Directory.CreateDirectory(Path.Combine(staged.DirectoryPath, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(staged.DirectoryPath, "BepInEx", "plugins", "staged.dll"), "staged by the runner");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(plan))!;
        json["runtime"] = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = staged.DirectoryPath,
            ["sha256"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(WorldFixture.Manifest(staged.DirectoryPath))),
        };
        json["runtimePins"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(InstallPins.Of(staged.DirectoryPath)));
        File.WriteAllText(plan, json.ToJsonString());
        return (plan, staged);
    }
    private static PinnedServerRunOptions<ServerRunPlan> WithStaged(PinnedServerRunOptions<ServerRunPlan> options, WorldFixture staged) => new()
    {
        Name = options.Name, ReadPlan = options.ReadPlan, SessionCapability = options.SessionCapability, SessionTokenVariable = options.SessionTokenVariable,
        Scenario = options.Scenario, SessionOverride = options.SessionOverride, StagedRuntime = staged,
    };
    [Fact] public async Task AStagedRuntimeIsRunInPlaceAndRetiredAgainstItsStagedState()
    {
        if (OperatingSystem.IsMacOS()) return;
        var (plan, staged) = StagedPlan();
        var options = WithStaged(Options(run =>
        {
            File.WriteAllText(Path.Combine(run.RuntimeDirectory, "toolkit-unity.log"), "written during the run");
            return Task.CompletedTask;
        }, new FakeOwnedServer("test.mod")), staged);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["run", plan, Output], options));
        var result = Result();
        var steps = result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Name").GetString()).ToList();
        Assert.Contains("verify the staged runtime copy", steps); Assert.DoesNotContain("copy and verify pinned runtime", steps);
        Assert.Equal(staged.DirectoryPath, Copy(result, "runtime"));
        Assert.Single(Directory.GetDirectories(Output, "valheim-test-*")); // the world copy only: the runtime was not copied again
        Assert.False(Directory.Exists(staged.DirectoryPath));
        var changes = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "runtime-changes", "changes.json"))).RootElement;
        Assert.Equal(new[] { "toolkit-unity.log" }, changes.GetProperty("Added").EnumerateArray().Select(e => e.GetString())); // not staged.dll
        Assert.True(staged.Preserve);
        staged.Dispose();
    }
    [Fact] public async Task AStagedRuntimeTheRunKeepsSurvivesItsCallersDispose()
    {
        if (OperatingSystem.IsMacOS()) return;
        var (plan, staged) = StagedPlan();
        var stubborn = new FakeOwnedServer("test.mod") { RefuseStop = true };
        Assert.Equal(1, await PinnedServerRun.MainAsync(["run", plan, Output], WithStaged(Options(server: stubborn), staged)));
        staged.Dispose(); // the runner's own using block
        Assert.True(Directory.Exists(staged.DirectoryPath));
        Assert.Contains("did not stop cleanly", Copy(Result(), "runtimeCopy"));
        stubborn.RefuseStop = false;
    }
    [Fact] public async Task AStagedRuntimeMustBeThePlansRuntime()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        using var other = WorldFixture.Copy(Runtime, Path.Combine(_root, "staged"), WorldFixture.Manifest(Runtime));
        Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", plan, Output], WithStaged(Options(), other)));
        var step = Assert.Single(Result().GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "verify the staged runtime copy");
        Assert.Contains("is not the staged runtime copy", step.GetProperty("Error").GetString());
        Assert.True(Directory.Exists(other.DirectoryPath));
    }
    [Fact] public async Task AFullDriveIsRefusedBeforeAnythingIsCopied()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        DiskSpace.AvailableOverride = _ => 1024;
        try { Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", plan, Output], Options())); }
        finally { DiskSpace.AvailableOverride = null; }
        var step = Result().GetProperty("Steps").EnumerateArray().First();
        Assert.Equal("enough free disk space for the copies", step.GetProperty("Name").GetString());
        Assert.False(step.GetProperty("Passed").GetBoolean());
        Assert.Contains("Not enough free disk space for this run's runtime and world copies", step.GetProperty("Error").GetString());
        Assert.Empty(Directory.GetDirectories(Output, "valheim-test-*"));
    }
    [Fact] public async Task APlanTheModRefusesIsRefusedBeforeCopying()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        int code = await PinnedServerRun.MainAsync(["validate", plan, Output], Options(checkPlan: _ => throw new ArgumentException("smoke plans run only")));
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
    [Fact] public async Task ALeftoverPatcherFailsValidationByThePatchersPin()
    {
        WritePlan(linux: HostRunsLinux); var pinned = InstallPins.Of(Runtime); // The runtime pinned clean, with no patchers.
        Directory.CreateDirectory(Path.Combine(Runtime, "BepInEx", "patchers"));
        File.WriteAllText(Path.Combine(Runtime, "BepInEx", "patchers", "RemovedMod.Preloader.dll"), "patcher");
        // Its manifest is regenerated with the leftover in it, so only the pin can tell.
        Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", WritePlan(linux: HostRunsLinux, runtimePins: pinned), Output], Options()));
        var step = Assert.Single(Result().GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "copied runtime is the pinned game build, loader and patchers");
        Assert.False(step.GetProperty("Passed").GetBoolean()); Assert.Contains("holding RemovedMod.Preloader.dll", step.GetProperty("Error").GetString());
    }
    // A scenario-registered log (an owned client's) is scanned with the run's at teardown: a patch on a method that does
    // not exist fails a passing scenario, unless the plan reclassifies it with a reason.
    private const string BrokenPatchLog = "[Message:   BepInEx] BepInEx 5.4.23.2\n[Error  :   BepInEx] Error loading [Broken Mod 1.0.0] : Exception has been thrown by the target of an invocation.\n" +
        "System.ArgumentException: Undefined target method for patch method static System.Void BrokenMod.Patches::Postfix()\n";
    private Func<TestRun<ServerRunPlan>, Task> RegistersClientLog(string text) => run =>
    {
        string path = Path.Combine(run.Output, "client-boot.game-0.log"); File.WriteAllText(path, text);
        run.GameSession.AddLog(new RunLog("client BepInEx log", path, Required: true));
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
            RuntimePins = new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) },
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

// VALHEIM_TESTING_KEEP_RUNTIME is process-wide: this test runs alone and restores it.
[CollectionDefinition(nameof(KeepRuntimeVariable), DisableParallelization = true)]
public sealed class KeepRuntimeVariable { }

[Collection(nameof(KeepRuntimeVariable))]
public sealed class KeepRuntimeVariableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keep-runtime-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact] public async Task TheVariableKeepsTheRuntimeForAnyRunner()
    {
        string runtime = Path.Combine(_root, "runtime"), world = Path.Combine(_root, "world"), output = Path.Combine(_root, "out");
        Directory.CreateDirectory(runtime); Directory.CreateDirectory(Path.Combine(world, "worlds_local"));
        bool linux = !OperatingSystem.IsWindows();
        string server = Path.Combine(runtime, linux ? GameLaunch.ServerLinuxExecutable : GameLaunch.ServerWindowsExecutable);
        File.WriteAllText(server, "server");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, File.GetUnixFileMode(server) | UnixFileMode.UserExecute);
        FakeInstalls.Server(runtime);
        File.WriteAllText(Path.Combine(world, "worlds_local", "Test.db"), "world");
        string plan = Path.Combine(_root, "plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["scenario"] = "smoke", ["runtime"] = new { source = runtime, sha256 = WorldFixture.Manifest(runtime) },
            ["world"] = new { source = world, sha256 = WorldFixture.Manifest(world) },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}" },
            ["pins"] = new Dictionary<string, string> { ["worlduid"] = "1" }, ["port"] = 5577, ["runtimePins"] = InstallPins.Of(runtime),
        }));
        var options = new PinnedServerRunOptions<ServerRunPlan>
        {
            Name = "toolkit-smoke", SessionCapability = "test.mod/session", SessionTokenVariable = "TEST_SESSION_TOKEN",
            ReadPlan = path => { var read = ServerRunPlan.Read<ServerRunPlan>(path); read.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return read; },
            Scenario = (_, _) => Task.CompletedTask,
        };
        string? previous = Environment.GetEnvironmentVariable(PinnedServerRun.KeepRuntimeVariable);
        Environment.SetEnvironmentVariable(PinnedServerRun.KeepRuntimeVariable, "1");
        try { Assert.Equal(0, await PinnedServerRun.MainAsync(["validate", plan, output], options)); }
        finally { Environment.SetEnvironmentVariable(PinnedServerRun.KeepRuntimeVariable, previous); }
        var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement.GetProperty("Provenance");
        Assert.True(Directory.Exists(provenance.GetProperty("runtime").GetString()));
        Assert.Contains("kept on request", provenance.GetProperty("runtimeCopy").GetString());
    }
}
