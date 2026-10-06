using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

// ServerActor is the one owned-server wiring (#258 step 1): every boot's launch, kept logs, process record, recorded
// connection and test access, over a placement. Here the placement launches a FakeOwnedServer (no game).
public sealed class ServerActorTests : IDisposable
{
    private const string TokenVariable = "TEST_SESSION_TOKEN";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "server-actor-" + Guid.NewGuid().ToString("N"));
    private string Runtime => Path.Combine(_root, "runtime");
    private string World => Path.Combine(_root, "world");
    private string Output => Path.Combine(_root, "out");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    // A Windows or Linux runtime with the server executable and BepInEx, which this machine's launch resolves (a Mac cannot).
    private ServerRunPlan Plan(Dictionary<string, string>? environment = null)
    {
        Directory.CreateDirectory(Runtime); Directory.CreateDirectory(World); Directory.CreateDirectory(Output);
        string server = Path.Combine(Runtime, OperatingSystem.IsWindows() ? GameLaunch.ServerWindowsExecutable : GameLaunch.ServerLinuxExecutable);
        File.WriteAllText(server, "server");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, File.GetUnixFileMode(server) | UnixFileMode.UserExecute);
        FakeInstalls.Server(Runtime); // BepInEx's core, and the Doorstop loader this machine's launch requires beside it.
        if (!OperatingSystem.IsWindows()) FakeInstalls.LinuxLoader(Runtime);
        else
        {
            File.WriteAllText(Path.Combine(Runtime, "winhttp.dll"), "MZ targetAssembly");
            File.WriteAllText(Path.Combine(Runtime, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        }
        return new ServerRunPlan
        {
            Scenario = "smoke", Arguments = ["-batchmode", "-savedir", "{world}"], Pins = new() { ["worlduid"] = "1" }, Port = 5577,
            StartupSeconds = 60, CommandSeconds = 5, QuitSeconds = 5, Environment = environment ?? [],
        };
    }

    // Launches the fake with the token the actor put in the launch's environment, and keeps each boot's launch.
    private sealed class FakePlacement(FakeOwnedServer server, string runtime, string world) : IServerPlacement
    {
        public List<GameLaunch> Launches { get; } = [];
        public string RuntimeDirectory => runtime;
        public string WorldDirectory => world;
        public ServerPlatform? Platform => null;
        public ServerBoot Start(int boot, GameLaunch launch, string output, CancellationToken cancellation)
        {
            Launches.Add(launch);
            var process = server.Launch(launch.Environment[TokenVariable]);
            return new ServerBoot(process, [new RunLog($"boot-{boot} BepInEx log", Path.Combine(output, $"boot-{boot}.game-0.log"), Required: true),
                new RunLog($"boot-{boot} stdout", Path.Combine(output, $"boot-{boot}.stdout.log"))], new() { ["pid"] = process.Id, ["world"] = world });
        }
        public IGameTransport Connect() => server.Connect();
        public StartupEvents? Events(ServerRunPlan plan) => null;
        public IGameHost? Host => null;
    }

    [Fact] public void EachBootRegistersItsLogsOnceWritesItsRecordsAndGetsTestAccessAgain()
    {
        if (OperatingSystem.IsMacOS()) return; // This fake runtime is Windows or Linux, which a Mac cannot run.
        var plan = Plan(new() { ["SAVES"] = "{world}" });
        var server = new FakeOwnedServer("test.mod", World);
        var placement = new FakePlacement(server, Runtime, World);
        using var actor = new ServerActor(placement, plan, Output, "test.mod/session", TokenVariable, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => actor.Game); // Nothing started yet.

        var first = actor.Start();
        Assert.Same(first, actor.Game);
        Assert.True(TestAccess.Read(first) is { Devcommands: true, CheatsAcknowledged: true });
        Assert.Equal(new[] { "boot-1 BepInEx log", "boot-1 stdout" }, actor.Logs.Select(log => log.Role));
        Assert.True(File.Exists(Path.Combine(Output, "connection-1.jsonl")));
        using (var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "boot-1.process.json"))))
            Assert.Equal(actor.StartedProcesses[0], record.RootElement.GetProperty("pid").GetInt32());
        // The plan's variables expanded for this runtime and world, and the boot's own session token beside them.
        Assert.Equal(World, placement.Launches[0].Environment["SAVES"]);
        Assert.Equal(server.Tokens[0], placement.Launches[0].Environment[TokenVariable]);

        var second = actor.Restart();
        Assert.Same(second, actor.Game);
        Assert.NotSame(first, second);
        // A new process has neither gate; the actor establishes both again before handing the boot over.
        Assert.True(TestAccess.Read(second) is { Devcommands: true, CheatsAcknowledged: true });
        Assert.Equal(new[] { "devcommands1", "confirmcheats1", "devcommands2", "confirmcheats2" },
            server.Events.Where(e => e.StartsWith("devcommands") || e.StartsWith("confirmcheats")));
        Assert.Equal(new[] { "boot-1 BepInEx log", "boot-1 stdout", "boot-2 BepInEx log", "boot-2 stdout" }, actor.Logs.Select(log => log.Role));
        Assert.True(File.Exists(Path.Combine(Output, "boot-2.process.json")));
        Assert.Single(actor.Stops);
        Assert.NotEqual(server.Tokens[0], server.Tokens[1]);

        actor.Dispose();
        Assert.Equal(2, actor.Stops.Count);
        Assert.Throws<InvalidOperationException>(() => actor.Game);
    }

    [Fact] public void ABootWhoseProcessRecordCannotBeWrittenIsStoppedAndFailsTheStart()
    {
        if (OperatingSystem.IsMacOS()) return; // This fake runtime is Windows or Linux, which a Mac cannot run.
        var plan = Plan();
        var server = new FakeOwnedServer("test.mod", World);
        // The output is missing, so boot-1.process.json cannot be written after the process started.
        using var actor = new ServerActor(new FakePlacement(server, Runtime, World), plan, Path.Combine(_root, "missing"), "test.mod/session", TokenVariable, CancellationToken.None);
        Assert.ThrowsAny<IOException>(() => actor.Start());
        Assert.Equal(new[] { "launch1", "stop1" }, server.Events.Where(e => e.StartsWith("launch") || e.StartsWith("stop")));
        Assert.Throws<InvalidOperationException>(() => actor.Game);
        Assert.False(actor.MayStillRun);
        // Its logs are still the actor's, so the teardown scan reads the failed boot's too.
        Assert.Equal(new[] { "boot-1 BepInEx log", "boot-1 stdout" }, actor.Logs.Select(log => log.Role));

        // A process that will not stop either: the original failure is reported, and the actor says the server may still run.
        var stubborn = new FakeOwnedServer("test.mod", World) { RefuseStop = true };
        using var kept = new ServerActor(new FakePlacement(stubborn, Runtime, World), plan, Path.Combine(_root, "missing"), "test.mod/session", TokenVariable, CancellationToken.None);
        Assert.ThrowsAny<IOException>(() => kept.Start());
        Assert.True(kept.MayStillRun);
    }
}
