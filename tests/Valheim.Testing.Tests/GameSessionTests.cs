using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;
using Valheim.Testing.GameSessions.Fakes;

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

    private bool _patchApplied = true;
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
            .Extension("test.mod", "harmony", _ => new
            {
                source = "harmony-patches", complete = true, owner = "test.mod",
                methods = !_patchApplied ? Array.Empty<object>() : new object[]
                {
                    new { method = "Terminal::InitTerminal()", patches = new[] { new { owner = "test.mod", kind = "postfix", priority = 400, index = 0,
                        before = Array.Empty<string>(), after = Array.Empty<string>(), patch = "Test.Mod+Commands::Postfix()" } } },
                },
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
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True ghostReplicated=True"))
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
            var session = ClientSession.Launch(plan, output, () => process, () => transport, (_, token) => ready(name, token), cancellation);
            lock (test._clientProcesses) test._openedClients.Add(name);
            return session;
        }
    }
    private readonly HashSet<string> _openedClients = [];
    private bool OpenedClients(string name) { lock (_clientProcesses) return _openedClients.Contains(name); }

    private GameSession Session(ScenarioReport report, Func<string, CancellationToken, Task>? ready = null, int startSeconds = 30, params string[] clients)
    {
        var placement = new Placement(this, ready ?? ((_, _) => Task.CompletedTask));
        return new GameSession(report, _root, WorldUid, Server,
            clients.Select(name => (name, (Func<CancellationToken, ClientActor>)(token => new ClientActor(name, Plan(startSeconds), GameSession.ActorOutput(_root, name), placement, token)))), CancellationToken.None);
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
        // Each started client's confirmed plugin pins are in the result (#259 step 2a); this server was given no plan.
        Assert.Equal(["client-a", "client-b"], report.Plugins.Keys);
        Assert.Equal(new Dictionary<string, string> { ["my.mod"] = "absent", ["valheimCLI.valheimCLI"] = new string('a', 32) }, report.Plugins["client-b"]);
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

    // #446: a dedicated server's campaign opens its clients at once (OpenClientsAsync); each writes its process and command
    // records into its own folder, so neither overwrites the other's (on Windows, a sharing violation) and each record is its own.
    [Fact] public async Task CampaignClientsOpenedAtOnceWriteTheirEvidenceIntoTheirOwnFolders()
    {
        var report = new ScenarioReport("session");
        int started = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Each client's menu waits until the other's launch has begun too: opened one after another, this would time out.
        var placement = new Placement(this, async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        });
        await using var session = new GameSession(report, _root, WorldUid, Server, [], CancellationToken.None)
        {
            CampaignClients = ["client-a", "client-b"], ResolveClient = (_, name) => (name!, placement),
        };
        await session.StartAsync();
        var opened = await session.OpenClientsAsync(new Dictionary<string, ClientRunPlan> { ["client-a"] = Plan(), ["client-b"] = Plan() });
        foreach (string name in new[] { "client-a", "client-b" })
        {
            using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, name, "client-process.json")));
            int pid; lock (_clientProcesses) pid = _clientProcesses[name].Id;
            Assert.Equal(pid, record.RootElement.GetProperty("pid").GetInt32()); // Its own launch's record, not the other's.
            Assert.Equal(opened[name].ProcessId, pid);
            Assert.True(File.Exists(Path.Combine(_root, name, "client-commands.jsonl")), name);
        }
        Assert.NotEqual(_clientProcesses["client-a"].Id, _clientProcesses["client-b"].Id);
        Assert.False(File.Exists(Path.Combine(_root, "client-process.json")));
        Assert.Empty(Directory.EnumerateFiles(_root, "client-commands*.jsonl", SearchOption.TopDirectoryOnly));
    }

    // A session whose declared clients would share a folder is refused before anything starts.
    [Fact] public async Task DeclaredClientsSharingAFolderAreRefusedBeforeAnythingStarts()
    {
        var placement = new Placement(this, (_, _) => Task.CompletedTask);
        await using var session = new GameSession(new ScenarioReport("session"), _root, WorldUid, Server,
            new[] { "client-a", "client-b" }.Select(name => (name, (Func<CancellationToken, ClientActor>)(token => new ClientActor(name, Plan(), _root, placement, token)))),
            CancellationToken.None);
        Assert.Contains("its own folder", (await Assert.ThrowsAsync<InvalidOperationException>(session.StartAsync)).Message);
        Assert.Empty(_serverProcesses); Assert.Empty(_clientProcesses);
    }

    // Names that differ only in case would share a folder on Windows and macOS.
    [Fact] public async Task DeclaredClientsWhoseNamesDifferOnlyInCaseAreRefused()
    {
        var placement = new Placement(this, (_, _) => Task.CompletedTask);
        await using var session = new GameSession(new ScenarioReport("session"), _root, WorldUid, Server,
            new[] { "client-a", "Client-A" }.Select(name => (name, (Func<CancellationToken, ClientActor>)(token => new ClientActor(name, Plan(), GameSession.ActorOutput(_root, name), placement, token)))),
            CancellationToken.None);
        Assert.Contains("share one evidence folder", (await Assert.ThrowsAsync<InvalidOperationException>(session.StartAsync)).Message);
        Assert.Empty(_serverProcesses); Assert.Empty(_clientProcesses);
    }

    [Theory] [InlineData("..")] [InlineData(".")] [InlineData("a/b")] [InlineData("a\\b")] [InlineData("")] [InlineData("a:b")] [InlineData("a b")]
    public void AnActorsEvidenceFolderIsOneFolderNameOnEveryHost(string actor) =>
        Assert.ThrowsAny<ArgumentException>(() => GameSession.ActorOutput(_root, actor));

    [Fact] public void AnActorsEvidenceFolderIsCreatedInTheOutput()
    {
        Assert.Equal(Path.Combine(_root, "client-a"), GameSession.ActorOutput(_root, "client-a"));
        Assert.True(Directory.Exists(Path.Combine(_root, "client-a")));
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
        var session = new GameSession(report, _root, WorldUid, Server, [("client-a", token => new ClientActor("client-a", Plan(), GameSession.ActorOutput(_root, "client-a"), placement, token))], CancellationToken.None)
        { DisposeOnFailedStart = false };
        await Assert.ThrowsAsync<InvalidOperationException>(session.StartAsync);
        Assert.Empty(Steps(report, StepPhase.Cleanup)); // Left to the runner's bounded cleanup.
        await session.DisposeAsync();
        Assert.Equal("stop only owned server", Steps(report, StepPhase.Cleanup)[^1]);
        Assert.All(_serverProcesses, process => Assert.Equal(1, process.Stops));
    }

    private static readonly ModDeclaration Declared = new("test.mod/session", "TEST_TOKEN")
    {
        HarmonyCapability = "test.mod/harmony", Owner = "test.mod", Patches = [new("Terminal::InitTerminal", "postfix", "Test.Mod+Commands::Postfix")],
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task TheDeclaredPatchesAreCheckedOnTheServerAtRuntimeReady(bool applied, bool serverPinsMod)
    {
        _patchApplied = applied;
        var report = new ScenarioReport("session");
        var session = new GameSession(report, _root, WorldUid, Server, [], CancellationToken.None) { Mod = Declared, ServerPinsMod = serverPinsMod };
        const string step = "server: the mod's Harmony patches are applied";
        if (applied || !serverPinsMod)
        {
            await session.StartAsync();
            // A server whose pins leave the mod out gets no check: there is nothing of it to patch in.
            Assert.Equal(serverPinsMod, Steps(report, StepPhase.Setup).Contains(step));
            Assert.True(report.RuntimeReady);
            await session.DisposeAsync();
            return;
        }
        // Loaded but not patched in: Setup fails at runtime-ready, no scenario step can run, and the session is torn down.
        var error = await Assert.ThrowsAnyAsync<Exception>(session.StartAsync);
        Assert.Contains("Terminal::InitTerminal", error.Message);
        Assert.False(report.RuntimeReady);
        Assert.Equal(step, report.Steps.Single(s => !s.Passed).Name);
        Assert.Equal("stop only owned server", Steps(report, StepPhase.Cleanup)[^1]);
    }

    [Fact] public void ADeclarationNamesItsHarmonyCheckWholeAndItsOwnerPin()
    {
        var partial = new ModDeclaration("test.mod/session", "TEST_TOKEN") { HarmonyCapability = "test.mod/harmony" };
        Assert.Throws<ArgumentException>(partial.Validate);
        Assert.Throws<ArgumentException>((Declared with { Patches = [new("Terminal::InitTerminal", "postfixx")] }).Validate);
        new ModDeclaration("test.mod/session", "TEST_TOKEN").Validate(); // No Harmony check declared: nothing to refuse.
        Assert.False(new ModDeclaration("test.mod/session", "TEST_TOKEN").ChecksPatches);
        // The pin is the plugin's GUID, which may differ from the Harmony ID.
        Assert.True((Declared with { Plugin = "test.mod.plugin" }).PinnedIn(new Dictionary<string, string> { ["test.mod.plugin"] = new string('a', 32) }));
        Assert.True(Declared.PinnedIn(new Dictionary<string, string> { ["test.mod"] = new string('a', 32) }));
        Assert.False(Declared.PinnedIn(new Dictionary<string, string> { ["test.mod"] = "absent" }));
        Assert.False(Declared.PinnedIn(new Dictionary<string, string>()));
    }

    [Fact] public async Task OpeningClientsAtOnceCancelsTheOthersWhenOneFailsAndClosesTheOpened()
    {
        var report = new ScenarioReport("session");
        using var bWaits = new ManualResetEventSlim();
        var placement = new Placement(this, (name, token) =>
        {
            if (name == "client-b") { bWaits.Set(); return Task.Delay(Timeout.Infinite, token); }
            if (name == "client-c") return Task.CompletedTask;
            // a fails only once b is waiting and c has opened.
            Assert.True(bWaits.Wait(TimeSpan.FromSeconds(20)) && SpinWait.SpinUntil(() => OpenedClients("client-c"), TimeSpan.FromSeconds(20)));
            throw new InvalidOperationException("client-a refused");
        });
        await using var session = new GameSession(report, _root, WorldUid, Server, [], CancellationToken.None) { ResolveClient = (_, name) => (name ?? "client", placement) };
        await session.StartAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenClientsAsync(new Dictionary<string, ClientRunPlan>
        {
            ["client-a"] = Plan(600), ["client-b"] = Plan(600), ["client-c"] = Plan(600),
        }));
        Assert.Equal("client-a refused", error.Message);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(60)); // b's wait ended with a's failure, not its 600 s.
        Assert.Equal(3, _clientProcesses.Count);
        Assert.All(_clientProcesses.Values, process => Assert.Equal(1, process.Stops)); // c opened and was closed; a and b stopped their own.
        Assert.False(session.Cancellation.IsCancellationRequested); // The session itself goes on.
    }

    // #448: the siblings a failed open cancels fail with OperationCanceledException; the caller still gets the failure that ended
    // the opens, every time, whether the failing client is named first or last. Ten sessions with four waiting siblings each make
    // the old outcome (the aggregate's first exception: sometimes a sibling's cancellation) likely.
    [Fact] public async Task OpeningClientsAtOnceRethrowsTheFailureNotASiblingsCancellation()
    {
        string[] waiting = ["client-b", "client-c", "client-d", "client-e"];
        for (int round = 0; round < 10; round++)
        {
            string failing = round % 2 == 0 ? "client-a" : "client-z";
            using var allWaiting = new CountdownEvent(waiting.Length);
            var placement = new Placement(this, (name, token) =>
            {
                if (name != failing) { allWaiting.Signal(); return Task.Delay(Timeout.Infinite, token); }
                if (!allWaiting.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("the siblings never waited");
                throw new InvalidOperationException(failing + " refused");
            });
            await using var session = new GameSession(new ScenarioReport("session"), Path.Combine(_root, "round-" + round), WorldUid, Server, [], CancellationToken.None)
            { ResolveClient = (_, name) => (name ?? "client", placement) };
            await session.StartAsync();
            var names = failing == "client-a" ? [failing, .. waiting] : waiting.Append(failing).ToArray();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenClientsAsync(names.ToDictionary(name => name, _ => Plan(600))));
            Assert.Equal(failing + " refused", error.Message);
        }
    }

    [Fact] public async Task AScriptedWorldServesAsTheSessionsServer()
    {
        var report = new ScenarioReport("scripted");
        var world = new ScriptedTransport();
        var server = new FakeWorld(() => world.Actor("server", "cli_expect worlduid=" + WorldUid));
        await using var session = FakeGameSession.Create(report, _root, WorldUid, server, server.Boot,
            (plan, name, output) => throw new InvalidOperationException("no clients here"), serverLog: Path.Combine(_root, "server.log"),
            lobby: _ => new CrossplayLobby("ENTITY42", "lobby-1"));
        await session.StartAsync();
        Assert.Equal(Path.Combine(_root, "server.log"), session.Server!.LiveLog);
        Assert.Equal("lobby-1", session.Server.Lobby(session.Server.Game).LobbyId);
        var first = session.Server.Game;
        Assert.NotSame(first, session.Server.Restart());
        Assert.Equal(TimeSpan.FromMilliseconds(10), session.Interval);
        Assert.Null(session.ClientLog(Plan()));
    }

    private sealed class FakeWorld(Func<GameActor> boot) : IOwnedServer
    {
        public GameActor Boot() => boot();
        public void WaitUntilJoinable(GameActor server) { }
        public GameActor Restart() => boot();
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
