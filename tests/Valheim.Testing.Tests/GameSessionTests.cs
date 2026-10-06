using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// GameSession (#258 step 3): the server and every client start at once, one failure cancels and settles the rest, the
// barriers are named and bounded Setup steps, and teardown closes clients before their server. No game: the server is an
// owned session over scripted replies, the clients fake processes over scripted transports.
public sealed class GameSessionTests : IDisposable
{
    private const string WorldUid = "4242";
    private readonly string _root = Directory.CreateTempSubdirectory("game-session-").FullName;
    private string Save => Path.Combine(_root, "world");
    public void Dispose() { _serverLaunched.Dispose(); Directory.Delete(_root, recursive: true); }

    private readonly List<FakeOwnedProcess> _serverProcesses = [];
    private readonly ManualResetEventSlim _serverLaunched = new();
    private readonly Dictionary<string, FakeOwnedProcess> _clientProcesses = [];
    private readonly Dictionary<string, ScriptedTransport> _clientTransports = [];

    private int ServerPid() { lock (_serverProcesses) return _serverProcesses[^1].Id; }

    // An owned server whose session adapter answers its token, pid and save root and accepts connections, with test access.
    private ServerActor Server(CancellationToken cancellation)
    {
        Directory.CreateDirectory(Save);
        string token = ""; bool devcommands = false, cheats = false;
        var session = new OwnedServerSession(launched =>
        {
            token = launched; devcommands = false; cheats = false;
            lock (_serverProcesses) { var process = new FakeOwnedProcess(500 + _serverProcesses.Count); _serverProcesses.Add(process); _serverLaunched.Set(); return process; }
        }, () => new ScriptedTransport()
            .Extension("test.mod", "session", _ => new
            {
                source = "owned-test-session", token, pid = ServerPid(), saveRoot = Path.GetFullPath(Save), complete = true, dedicated = true, acceptingConnections = true,
            })
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .On("confirmcheats", _ => { cheats = true; return ScriptedTransport.Ok(); })
            .On("cli_access", _ => ScriptedTransport.Ok("ACCESS " + JsonSerializer.Serialize(new
            {
                schemaVersion = 1, complete = true, devcommands, cheatsAcknowledged = cheats, allowOnServerClients = false, server = true, dedicated = true,
                joinedClient = false, localPlayer = false, profileAvailable = true,
            }))),
            Save, "cli_expect worlduid=" + WorldUid, "test.mod/session", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), cancellation);
        return new ServerActor(session);
    }

    private static ClientRunPlan Plan(int startSeconds = 30) => new()
    {
        Mode = "owned", Install = Path.GetFullPath("client-install"), Port = 5556, Join = "127.0.0.1:2456", Character = "Tester", StartSeconds = startSeconds,
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
    };

    // A client at its menu that can join and leave the server's world, as ValheimCLI's session commands answer.
    private ScriptedTransport ClientTransport()
    {
        bool joined = false;
        return new ScriptedTransport()
            .ClientAccess(() => joined)
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Tester' (tester-copy, Local)"))
            .Extension("valheim.session", "join", _ => { joined = true; return new { source = "session-join", complete = true, action = "join" }; }, readOnly: false)
            .Extension("valheim.session", "leave", _ => { joined = false; return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = joined ? "world-present" : "menu", worldUid = joined ? WorldUid : null, worldPresent = joined,
                worldReady = joined, server = false, dedicated = false, localPlayer = joined, playerReady = joined, saving = false, loadError = false,
                connectionStatus = joined ? "Connected" : "None",
            })
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"))
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0,40,0 ms=3"));
    }

    // Each client opens a fake process to its menu once ready says so (given the time left and the session's token).
    private sealed class Placement(GameSessionTests test, Func<string, CancellationToken, Task> ready) : IClientPlacement
    {
        public ClientSession Open(string name, ClientRunPlan plan, string output, CancellationToken cancellation)
        {
            var transport = test.ClientTransport();
            FakeOwnedProcess process;
            lock (test._clientProcesses) { process = test._clientProcesses[name] = new FakeOwnedProcess(700 + test._clientProcesses.Count); test._clientTransports[name] = transport; }
            string own = Directory.CreateDirectory(Path.Combine(output, name)).FullName;
            return ClientSession.Launch(plan, own, () => process, () => transport, (_, token) => ready(name, token), cancellation);
        }
    }

    private GameSession Session(ScenarioReport report, Func<string, CancellationToken, Task>? ready = null, int startSeconds = 30, params string[] clients)
    {
        var placement = new Placement(this, ready ?? ((_, _) => Task.CompletedTask));
        return new GameSession(report, _root, WorldUid, Server,
            clients.Select(name => (name, (Func<CancellationToken, ClientActor>)(token => new ClientActor(name, Plan(startSeconds), _root, placement, token)))), CancellationToken.None);
    }

    private static List<string> Steps(ScenarioReport report, StepPhase phase) => report.Steps.Where(step => step.Phase == phase).Select(step => step.Name).ToList();

    [Fact] public async Task TheServerAndEveryClientStartAtOnce()
    {
        var report = new ScenarioReport("session");
        using var both = new CountdownEvent(2);
        // Each client's menu waits until the other's start has begun too: started one after another, this would time out.
        await using var session = Session(report, (_, token) => { both.Signal(); return both.Wait(TimeSpan.FromSeconds(20), token) ? Task.CompletedTask : throw new TimeoutException("the clients did not start together"); },
            clients: ["client-a", "client-b"]);
        await session.StartAsync();
        Assert.True(report.Passed);
        Assert.Equal(new[] { "client client-a at its menu, plugins pinned", "client client-b at its menu, plugins pinned", "start and verify owned dedicated fixture" },
            Steps(report, StepPhase.Setup).Order(StringComparer.Ordinal));
        Assert.NotNull(session.Server!.Game);
        Assert.Same(session.Clients["client-b"].Session!.Actor, session.Clients["client-b"].Game);
    }

    [Fact] public async Task OneFailedStartCancelsTheOthersThenClosesClientsBeforeTheServerAndRethrowsIt()
    {
        var report = new ScenarioReport("session");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // client-a is refused once the server launched and client-b is waiting; client-b would wait its whole 600 s start for a
        // menu that never comes.
        using var bWaits = new ManualResetEventSlim();
        await using var session = Session(report, (name, token) =>
        {
            if (name == "client-b") { bWaits.Set(); return Task.Delay(Timeout.Infinite, token); }
            Assert.True(_serverLaunched.Wait(TimeSpan.FromSeconds(20)) && bWaits.Wait(TimeSpan.FromSeconds(20)));
            throw new InvalidOperationException("client-a refused");
        }, startSeconds: 600, clients: ["client-a", "client-b"]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(session.StartAsync);
        Assert.Equal("client-a refused", error.Message);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(60)); // Cancelled, not run out.
        Assert.True(session.Cancellation.IsCancellationRequested);
        Assert.Equal(1, _clientProcesses["client-b"].Stops); // Its startup ended on the session's token and stopped its process.
        Assert.All(session.Clients.Values, client => Assert.Null(client.Session));
        Assert.Equal(1, Assert.Single(_serverProcesses).Stops); // A server that had started is stopped too, after its clients.
        Assert.Equal("stop only owned server", Steps(report, StepPhase.Cleanup)[^1]);
        Assert.False(report.Passed);
        await session.DisposeAsync(); // Already torn down: nothing more.
        Assert.Single(Steps(report, StepPhase.Cleanup), "stop only owned server");
    }

    [Fact] public async Task JoinJoinAllAndRejoinAreNamedSetupSteps()
    {
        var report = new ScenarioReport("session");
        await using var session = Session(report, clients: ["client-a", "client-b"]);
        await session.StartAsync();
        await session.JoinAll();
        await session.Rejoin("client-a", protect: false);
        var setup = Steps(report, StepPhase.Setup);
        Assert.Equal(new[] { "client client-a in world 4242, protected", "client client-b in world 4242, protected", "client client-a back at its menu", "client client-a in world 4242" },
            setup.Where(step => step.StartsWith("client client-a in", StringComparison.Ordinal) || step.StartsWith("client client-b in", StringComparison.Ordinal) || step.EndsWith("back at its menu", StringComparison.Ordinal)));
        Assert.Equal(2, setup.Count(step => step == "the server accepts game connections for client client-a"));
        Assert.Equal(2, _clientTransports["client-a"].Count("cli_extension valheim.session/join"));
        Assert.Equal(1, _clientTransports["client-a"].Count("cli_extension valheim.session/leave"));
        Assert.True(report.Passed);
    }

    [Fact] public async Task ASessionStartsOnceAndAFailedServerStopIsReported()
    {
        var report = new ScenarioReport("session");
        var session = Session(report, clients: ["client-a"]);
        await session.StartAsync();
        // A second start must not count as a failed start, which would tear the running session down.
        await Assert.ThrowsAsync<InvalidOperationException>(session.StartAsync);
        Assert.NotNull(session.Clients["client-a"].Session);
        Assert.False(session.Cancellation.IsCancellationRequested);
        _serverProcesses[0].StopFailure = () => new IOException("the server would not stop");
        await session.DisposeAsync();
        Assert.False(session.ServerStopped); // The runner keeps the runtime and the host lock.
        Assert.False(report.CleanupVerified);
        Assert.True(session.Cancellation.IsCancellationRequested); // Anything left running was told to stop.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Join("client-a"));
    }

    [Fact] public async Task AJoinNamesTheClientsItKnowsAndACrossplayJoinNeedsItsLobby()
    {
        await using var session = Session(new ScenarioReport("session"), clients: ["client-a"]);
        Assert.Contains("client-a", (await Assert.ThrowsAsync<ArgumentException>(() => session.Join("client-z"))).Message);
        session.Clients["client-a"].Plan.Crossplay = true;
        Assert.Contains("lobby", (await Assert.ThrowsAsync<ArgumentException>(() => session.Join("client-a"))).Message);
    }

    [Fact] public async Task ABarrierThatRunsOutNamesItselfAndFailsSetup()
    {
        var report = new ScenarioReport("session");
        await using var session = Session(report);
        var error = await Assert.ThrowsAsync<WaitTimeoutException>(() => session.Barrier("the peer sees the ward", token => Task.Delay(Timeout.Infinite, token), TimeSpan.FromMilliseconds(50)));
        Assert.Contains("the peer sees the ward", error.Target);
        var step = Assert.Single(report.Steps);
        Assert.Equal(("the peer sees the ward", false, StepPhase.Setup), (step.Name, step.Passed, step.Phase));
        Assert.False(report.RuntimeReady);
        // A barrier that is met passes as its own step.
        await session.Barrier("the ward is placed", _ => Task.CompletedTask, TimeSpan.FromSeconds(5));
        Assert.True(report.Steps[^1].Passed);
    }

    [Fact] public async Task AClientTheScenarioOpensIsTheSessionsToCloseAtTeardown()
    {
        var report = new ScenarioReport("session");
        var placement = new Placement(this, (_, _) => Task.CompletedTask);
        var session = new GameSession(report, _root, WorldUid, Server, [], CancellationToken.None) { ResolveClient = (_, name) => (name ?? "client", placement) };
        await session.StartAsync();
        var opened = session.OpenClient(Plan(), "client-x");
        Assert.False(opened.Closed);
        await session.DisposeAsync();
        Assert.True(opened.Closed);
        Assert.Equal(new[] { "stop only the owned client client-x", "stop only owned server" }, Steps(report, StepPhase.Cleanup));
        Assert.Throws<ObjectDisposedException>(() => session.OpenClient(Plan()));
    }

    [Fact] public async Task ARunnerWithItsOwnCleanupTearsAFailedStartDownItself()
    {
        var report = new ScenarioReport("session");
        var placement = new Placement(this, (_, _) => throw new InvalidOperationException("refused"));
        var session = new GameSession(report, _root, WorldUid, Server, [("client-a", token => new ClientActor("client-a", Plan(), _root, placement, token))], CancellationToken.None)
        { DisposeOnFailedStart = false };
        await Assert.ThrowsAsync<InvalidOperationException>(session.StartAsync);
        Assert.Empty(Steps(report, StepPhase.Cleanup)); // Left to the runner's bounded cleanup.
        await session.DisposeAsync();
        Assert.Equal("stop only owned server", Steps(report, StepPhase.Cleanup)[^1]);
        Assert.All(_serverProcesses, process => Assert.Equal(1, process.Stops));
    }

    [Fact] public async Task TeardownClosesTheClientsInReverseThenStopsTheServer()
    {
        var report = new ScenarioReport("session");
        var session = Session(report, clients: ["client-a", "client-b"]);
        await session.StartAsync();
        await session.DisposeAsync();
        Assert.Equal(new[] { "stop only the owned client client-b", "stop only the owned client client-a", "stop only owned server" }, Steps(report, StepPhase.Cleanup));
        Assert.All(_clientProcesses.Values, process => Assert.Equal(1, process.Stops));
        Assert.Equal(1, Assert.Single(_serverProcesses).Stops);
        Assert.True(report.CleanupVerified);
        await session.DisposeAsync(); // Once.
        Assert.Equal(3, Steps(report, StepPhase.Cleanup).Count);
    }
}
