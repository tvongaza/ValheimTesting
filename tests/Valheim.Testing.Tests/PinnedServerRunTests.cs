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

    private string WritePlan(bool linux)
    {
        Directory.CreateDirectory(Runtime); Directory.CreateDirectory(Path.Combine(World, "worlds_local"));
        string server = Path.Combine(Runtime, linux ? ServerLaunch.LinuxExecutable : ServerLaunch.WindowsExecutable);
        File.WriteAllText(server, "server");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, File.GetUnixFileMode(server) | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(World, "worlds_local", "Test.db"), "world");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var plan = new
        {
            scenario = "smoke",
            runtime = new { source = Runtime, sha256 = WorldFixture.Manifest(Runtime) },
            world = new { source = World, sha256 = WorldFixture.Manifest(World) },
            arguments = new[] { "-batchmode", "-nographics", "-savedir", "{world}" },
            pins = new Dictionary<string, string> { ["worlduid"] = "1" },
            port = port < 1024 ? 5577 : port,
        };
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
    private static bool HostRunsLinux => !OperatingSystem.IsWindows();

    [Fact] public async Task ValidateCopiesAndVerifiesTheFixturesWithoutLaunching()
    {
        string plan = WritePlan(linux: HostRunsLinux);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["validate", plan, Output], Options()));
        var result = Result();
        Assert.True(result.GetProperty("Passed").GetBoolean());
        Assert.Equal(new[] { "copy and verify pinned runtime", "copy and verify pinned world", "copied runtime has the plan's server executable", "prepared only; no game launched" },
            result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
        Assert.Equal("validate", result.GetProperty("Provenance").GetProperty("mode").GetString());
        Assert.True(File.Exists(Path.Combine(Output, "input-hashes.json"))); Assert.True(File.Exists(Path.Combine(Output, "junit.xml")));
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
        if (OperatingSystem.IsMacOS()) return; // No dedicated server runs on macOS; validate is covered above.
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
        Assert.Same(StartupEvents.BepInExPluginLoadFailures, events.Failures);
    }
    [Fact] public void TheGenericPlanRulesHold()
    {
        var plan = new ServerRunPlan
        {
            Runtime = new() { Source = Path.GetTempPath(), Sha256 = new() { ["a"] = new string('a', 64) } },
            World = new() { Source = Path.GetTempPath(), Sha256 = new() { ["b"] = new string('b', 64) } },
            Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"], Pins = new() { ["worlduid"] = "1", ["my.mod"] = new string('1', 32) },
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
