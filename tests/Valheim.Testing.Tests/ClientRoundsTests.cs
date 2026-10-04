using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// ClientRounds, both kinds of client, with a fake owned process and no game, Steam or network connection:
// - a client joining scripted servers: the order of the round steps, what stops the rounds (a failed save, restart or
//   measurement), the evidence each failure leaves, and that the client is closed exactly once in every outcome;
// - a client hosting a fixture world itself: the world is placed in a temporary client data directory (never over a world
//   already there, and inside a FakeClientDataDirectory scope so every platform's save-directory rule still runs), hosted,
//   saved, restarted and moved into the evidence;
// - the one teardown, identical for both.
public sealed class ClientRoundsTests : IDisposable
{
    private const string WorldUid = "4242", Name = "HostFixture";
    private static readonly HeightExpectation Point = new(100, -40, 42.5f);
    private readonly string _root = Directory.CreateTempSubdirectory("client-rounds-").FullName;
    private string Output => Path.Combine(_root, "out");
    private string Fixture => Path.Combine(_root, "fixture");
    private string Save => Path.Combine(_root, "client-data");
    private string Worlds => Path.Combine(Save, "worlds_local");
    private readonly List<ScriptedTransport> _servers = [];
    private ScriptedTransport _client;
    private readonly FakeOwnedProcess _process = new(77);
    private readonly PreflightInstall _install = PreflightInstall.Create(); // An owned hosting client's install that passes the preflight.
    private bool _saves = true;
    private int _restarts, _opens;
    public void Dispose() { _install.Dispose(); Directory.Delete(_root, recursive: true); }

    public ClientRoundsTests()
    {
        Directory.CreateDirectory(Output); Directory.CreateDirectory(Fixture); Directory.CreateDirectory(Worlds);
        File.WriteAllBytes(Path.Combine(Fixture, Name + ".fwl"), OwnedRunPreflightTests.Metadata(Name, long.Parse(WorldUid)));
        File.WriteAllText(Path.Combine(Fixture, Name + ".db"), "fixture world");
        File.WriteAllText(Path.Combine(Worlds, "MyWorld.fwl"), "the user's world"); // Never touched.
        File.WriteAllText(Path.Combine(Worlds, "MyWorld.db"), "the user's world");
        bool joined = false;
        _client = new ScriptedTransport()
            .ClientAccess(() => joined)
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Test character' (tester-copy, Local)"))
            .Extension("valheim.session", "join", _ => { joined = true; return new { source = "session-join", complete = true, action = "join" }; }, readOnly: false)
            .Extension("valheim.session", "leave", _ => { joined = false; return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .Extension("valheim.session", "teleport-signals", _ => new { source = "teleport-signals", complete = true })
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = joined ? "world-present" : "menu", worldUid = joined ? WorldUid : null, worldPresent = joined,
                worldReady = joined, server = false, dedicated = false, localPlayer = joined, playerReady = joined, saving = false, loadError = false,
                connectionStatus = joined ? "Connected" : "None",
            })
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"))
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0,40,0 ms=3"))
            .Extension("valheim.world", "player-support", _ => new
            {
                source = "local-player-support", complete = true, x = 0f, y = 40f, z = 0f, speed = 0f,
                grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            })
            .OnPrefix("cli_wait_teleportable ", _ => ScriptedTransport.Ok("OK: TELEPORTABLE ms=500"))
            .On("cli_teleport_trace_arm", _ => ScriptedTransport.Ok("OK: TELEPORT_TRACE_ARM id=7"))
            .OnPrefix("cli_teleport_trace_wait ", _ => ScriptedTransport.Ok("OK: TELEPORT_TRACE id=7 distant=True requestedMs=0 movedMs=2000 areaReadyMs=3400 floorReadyMs=3450 doneMs=3500 floorAtDone=True final=100,42.5,-40"))
            .Extension("valheim.world", "player-support-wait", _ => new
            {
                source = "local-player-support", complete = true, x = Point.X, y = Point.Height, z = Point.Z, speed = 0f,
                grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            });
    }

    private static ClientRunPlan Plan(string mode = "owned") => new()
    {
        Mode = mode, Install = mode == "owned" ? Path.GetFullPath("client-install") : "", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
    };

    private GameActor Server()
    {
        var transport = new ScriptedTransport().Saves(_saves)
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport"))
            .Extension("test.mod", "session", _ => new { source = "owned-test-session", complete = true, acceptingConnections = true });
        _servers.Add(transport);
        return transport.Actor("server", "cli_expect worlduid=" + WorldUid);
    }

    private ClientRounds Rounds(ScenarioReport report, ClientRunPlan plan, Func<GameActor>? restart = null, HeightExpectation? arrival = null, IReadOnlyList<string>? names = null) => new()
    {
        Client = plan, WorldUid = WorldUid, Report = report, Output = Output,
        OwnedServer = new TestOwnedServer(restart ?? (() => { _restarts++; return Server(); })),
        Arrival = arrival ?? Point, Rounds = names ?? ["first", "after-restart"],
    };

    private Func<ClientSession> Open(ClientRunPlan plan) => () =>
    {
        _opens++;
        return plan.Owned
            ? ClientSession.Launch(plan, Output, () => _process, () => _client, (_, _) => Task.CompletedTask)
            : ClientSession.Attach(plan, Output, _client);
    };

    // The mod's measurement: one step and one evidence file per round, failing where asked after writing its evidence.
    private static Action<ClientRound> Measure(string? failIn = null, Action<ClientRound>? also = null) => round => round.Step("measure", () =>
    {
        also?.Invoke(round);
        round.Write("reading", new { round = round.Name, server = round.Server.Name });
        if (round.Name == failIn) throw new InvalidOperationException("3 of 10 samples differ; see " + round.Name + "-reading.json.");
    });

    private ClientRunPlan HostPlan(string mode = "owned", bool crossplay = false) => new()
    {
        Mode = mode, Install = mode == "owned" ? _install.Root : "", Port = 5556, Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = mode == "owned" ? _install.CliMd5 : new string('a', 32), ["my.mod"] = "absent" },
        InstallPins = mode == "owned" ? InstallPins.Of(_install.Root) : null,
        HostWorld = new() { World = new() { Source = Fixture, Sha256 = new(WorldFixture.Manifest(Fixture)) }, WorldUid = WorldUid, Crossplay = crossplay, SaveDirectory = Save },
    };

    // The client: at its menu until a world starts; then one loading reading, one reading with the world ready but no
    // player yet (as a host reports), then its player. Leaving writes a backup beside the world, as the game does.
    private sealed class Game
    {
        public bool Hosting, SaveAdvances = true, StartsSteamWorld;
        public string? StartReply;
        public string LoadedUid = WorldUid;
        public int Readings, SaveNumber = 5;
        public ScriptedTransport Transport { get; }
        // leaveOffered false: the host's ValheimCLI has no valheim.session/leave (an older Standard pack).
        public Game(string worlds, bool leaveOffered = true)
        {
            Transport = new ScriptedTransport()
                .ClientAccess(() => Hosting, () => Hosting)
                .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Tester' (tester, Local)"))
                .OnPrefix("cli_start_host_world ", command =>
                {
                    string[] words = command.Split(' ');
                    bool crossplay = words[5] == "true" && !StartsSteamWorld;
                    if (StartReply != null) return ScriptedTransport.Failed(StartReply);
                    Hosting = true; Readings = 0;
                    return ScriptedTransport.Ok($"OK: Starting hosted world '{words[1]}' using character 'Tester' (tester, Local); open=true, public=False, crossplay={(crossplay ? "True" : "False")}, backend={(crossplay ? "PlayFab" : "Steamworks")}, passwordSet=False");
                })
                .Extension("valheim.session", "state", _ =>
                {
                    int reading = Hosting ? ++Readings : 0;
                    bool present = Hosting && reading > 1, player = Hosting && reading > 2;
                    return new
                    {
                        source = "session-state", complete = true, phase = present ? "world-present" : Hosting ? "loading" : "menu", worldUid = present ? LoadedUid : null,
                        worldPresent = present, worldReady = present, server = present, dedicated = false, localPlayer = player, playerReady = player,
                        saving = false, loadError = false, connectionStatus = present ? "Connected" : "None",
                    };
                })
                .Extension("valheim.session", "save", _ => new { source = "session-save", complete = true, worldUid = LoadedUid, saved = true, before = SaveNumber, after = SaveAdvances ? ++SaveNumber : SaveNumber, milliseconds = 10 }, readOnly: false)
                .Extension(leaveOffered ? "valheim.session" : "other.extension", "leave", _ =>
                {
                    Hosting = false;
                    File.WriteAllText(Path.Combine(worlds, Name + ".db.old"), "backup");
                    return new { source = "session-leave", complete = true, action = "leave" };
                }, readOnly: false)
                .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"));
        }
    }

    private ClientRounds Hosted(ScenarioReport report, ClientRunPlan plan) => new() { Client = plan, Report = report, Output = Output };
    // The hosting client keeps its data in the temporary folder, as a real client keeps it in its user's own directory.
    private void RunHosted(ScenarioReport report, ClientRunPlan plan, Game game, Action<ClientRound> measure)
    {
        using var data = new FakeClientDataDirectory(Save);
        Hosted(report, plan).Run(Open(plan, game), measure);
    }
    private Func<ClientSession> Open(ClientRunPlan plan, Game game) => () =>
    {
        _opens++;
        return plan.Owned
            ? ClientSession.Launch(plan, Output, () => _process, () => game.Transport, (_, _) => Task.CompletedTask)
            : ClientSession.Attach(plan, Output, game.Transport);
    };
    private string[] OurWorldFiles() => Directory.EnumerateFileSystemEntries(Worlds).Select(Path.GetFileName).Where(f => f!.StartsWith(Name, StringComparison.Ordinal)).Select(f => f!).ToArray();

    private static string[] Failed(ScenarioReport report) => report.Steps.Where(s => !s.Passed).Select(s => s.Name).ToArray();
    private bool Wrote(string name) => File.Exists(Path.Combine(Output, name));

    [Fact]
    public void LoadOnlyJoinCanLeaveProtectionOffWithoutSkippingWorldAndPluginChecks()
    {
        var plan = Plan();
        var report = new ScenarioReport("load-only");
        new ClientRounds
        {
            Client = plan, WorldUid = WorldUid, Report = report, Output = Output,
            OwnedServer = new TestOwnedServer(Server), Rounds = ["first"], ProtectPlayer = false,
        }.Run(Server(), Open(plan), Measure());

        Assert.True(report.Passed);
        Assert.Equal(0, _client.Count("cli_set_player_safety true"));
        Assert.Equal(1, _client.Count("cli_extension valheim.session/join"));
        Assert.Equal(1, _client.Count("cli_extension valheim.session/leave"));
        Assert.Equal(1, _process.Stops);
    }

    [Fact] public void TwoRoundsJoinMeasureSaveRestartRejoinAndStopTheOwnedClientOnce()
    {
        var report = new ScenarioReport("rounds");
        var first = Server();
        var checkedAfterRestart = new List<GameActor>();
        var last = Rounds(report, Plan()).Run(first, Open(Plan()), Measure(),
            afterRestart: round => round.Step("the server kept the change", () => checkedAfterRestart.Add(round.Server)));

        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Equal(new[]
        {
            "launch the owned client to its menu, plugins pinned",
            "first: the server accepts game connections", "first: join the owned server with the disposable character, protected",
            "first: arrive at the measurement point", "first: measure",
            "confirmed world save", "first: the client leaves to its menu", "restart only the owned server",
            "after-restart: the server kept the change",
            "after-restart: the server accepts game connections", "after-restart: join the owned server with the disposable character, protected",
            "after-restart: arrive at the measurement point", "after-restart: measure",
            "after-restart: the client leaves to its menu",
            "stop only the owned client",
        }, report.Steps.Select(s => s.Name));
        // The second round runs on the restarted server, and the helper hands it back.
        Assert.Equal(1, _restarts); Assert.Same(checkedAfterRestart.Single(), last);
        Assert.Equal(new[] { 1, 0 }, _servers.Select(s => s.Count("cli_save")));
        Assert.Equal(new[] { 1, 1 }, _servers.Select(s => s.Count("cli_teleport_peer")));
        Assert.Equal(2, _client.Count("cli_extension valheim.session/join"));
        Assert.Equal(2, _client.Count("cli_extension valheim.session/leave"));
        Assert.Equal(1, _process.Stops); Assert.True(_client.Disposed);
        foreach (string file in new[] { "first-arrival.json", "first-reading.json", "after-restart-arrival.json", "after-restart-reading.json" }) Assert.True(Wrote(file), file);
        Assert.True(JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "first-arrival.json"))).RootElement.GetProperty("grounded").GetBoolean());
        Assert.Equal("first,after-restart", report.Provenance["clientRoundsCompleted"]);
        Assert.Equal("x64", report.Provenance["clientArchitecture"]);
    }

    [Fact] public void EachRoundsArrivalMakesOneWaitPerPhaseAndWritesItsTrace()
    {
        var plan = Plan();
        var report = new ScenarioReport("signal-rounds");
        Rounds(report, plan).Run(Server(), Open(plan), Measure());
        Assert.True(report.Passed);
        Assert.Equal(2, _client.Count("cli_wait_teleportable"));
        Assert.Equal(2, _client.Count("cli_teleport_trace_wait"));
        Assert.Equal(2, _client.Count("cli_extension valheim.world/player-support-wait"));
        Assert.Equal(2, _client.Count("cli_extension valheim.world/player-support")); // one flying check per arrival, no polling
        Assert.True(Wrote("first-teleport-trace.json"));
        Assert.True(Wrote("after-restart-teleport-trace.json"));
        using var trace = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "first-teleport-trace.json")));
        Assert.Equal(2000, trace.RootElement.GetProperty("MovedMs").GetInt64());
        Assert.Equal(3400, trace.RootElement.GetProperty("AreaReadyMs").GetInt64());
        Assert.Equal(3450, trace.RootElement.GetProperty("FloorReadyMs").GetInt64());
        Assert.Equal(3500, trace.RootElement.GetProperty("DoneMs").GetInt64());
        Assert.False(report.Provenance.ContainsKey("arrivalWait")); // one procedure, nothing to record
    }

    // A client whose packs lack the arrival waits is refused at its menu, before any join or teleport.
    [Fact] public void AClientWithoutTheArrivalSignalsIsRefusedBeforeJoining()
    {
        var older = new ScriptedTransport().ClientAccess(() => false)
            .Extension("valheim.session", "state", _ => new { source = "session-state", complete = true, phase = "menu", worldPresent = false, worldReady = false, server = false, dedicated = false, localPlayer = false, playerReady = false, saving = false, loadError = false, connectionStatus = "None" })
            .Extension("valheim.world", "player-support", _ => new { });
        _client = older;
        var report = new ScenarioReport("rounds");
        Assert.ThrowsAny<Exception>(() => Rounds(report, Plan("attach")).Run(Server(), Open(Plan("attach")), Measure()));
        var failed = Assert.Single(report.Steps, s => !s.Passed);
        Assert.Contains(CliCapabilities.TeleportSignals, failed.Error);
        Assert.Equal(0, older.Count("cli_extension valheim.session/join"));
        Assert.Equal(0, _servers[0].Count("cli_teleport_peer"));
    }

    [Theory] [InlineData("owned", "arm64", "arm64")] [InlineData("owned", "x64", "x64")] [InlineData("attach", "", "attached")]
    public void TheReportRecordsTheClientsArchitecture(string mode, string architecture, string recorded)
    {
        var plan = Plan(mode); plan.Architecture = architecture;
        var report = new ScenarioReport("rounds");
        Rounds(report, plan, names: ["only"]).Run(Server(), Open(plan), Measure());
        Assert.True(report.Passed, string.Join("; ", Failed(report)));
        Assert.Equal(recorded, report.Provenance["clientArchitecture"]);
    }

    [Fact] public void AFailedSaveStopsBeforeTheLeaveAndRestartAndLeavesTheFirstRoundsEvidence()
    {
        _saves = false;
        var report = new ScenarioReport("rounds");
        Assert.Throws<InvalidOperationException>(() => Rounds(report, Plan()).Run(Server(), Open(Plan()), Measure()));
        Assert.Equal(new[] { "confirmed world save" }, Failed(report));
        Assert.Equal("stop only the owned client", report.Steps[^1].Name); Assert.True(report.Steps[^1].Passed);
        Assert.Equal(0, _restarts); Assert.Equal(0, _client.Count("cli_extension valheim.session/leave"));
        Assert.Equal(1, _process.Stops);
        Assert.True(Wrote("first-arrival.json")); Assert.True(Wrote("first-reading.json")); Assert.False(Wrote("after-restart-arrival.json"));
        Assert.False(report.Provenance.ContainsKey("clientRoundsCompleted"));
        // The runner writes the report: the failed save is in both result files, with its error.
        report.Write(Output);
        var junit = System.Xml.Linq.XDocument.Load(Path.Combine(Output, "junit.xml")).Root!;
        Assert.Equal("1", junit.Attribute("failures")!.Value);
        Assert.NotNull(junit.Descendants("testcase").Single(c => c.Attribute("name")!.Value == "confirmed world save").Element("failure"));
        Assert.False(JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement.GetProperty("Passed").GetBoolean());
    }

    [Fact] public void AFailedRestartStopsTheRoundsAndStillStopsTheClient()
    {
        var report = new ScenarioReport("rounds"); bool checkedAfterRestart = false;
        var error = Assert.Throws<InvalidOperationException>(() => Rounds(report, Plan(), restart: () => throw new InvalidOperationException("Owned process did not exit; restart refused."))
            .Run(Server(), Open(Plan()), Measure(), afterRestart: _ => checkedAfterRestart = true));
        Assert.Equal("Owned process did not exit; restart refused.", error.Message);
        Assert.Equal(new[] { "restart only the owned server" }, Failed(report));
        Assert.Contains(report.Steps, s => s.Name == "first: the client leaves to its menu" && s.Passed);
        Assert.False(checkedAfterRestart);
        Assert.Equal(1, _client.Count("cli_extension valheim.session/join")); // Never rejoined a server that did not come back.
        Assert.Equal(1, _process.Stops);
        Assert.Equal("first", report.Provenance["clientRoundsCompleted"]);
        Assert.True(Wrote("first-reading.json"));
    }

    [Theory][InlineData("first", 0, 0)][InlineData("after-restart", 1, 1)]
    public void AFailedMeasurementStopsItsRoundKeepsItsEvidenceAndStopsTheClient(string round, int saves, int restarts)
    {
        var report = new ScenarioReport("rounds");
        var error = Assert.Throws<InvalidOperationException>(() => Rounds(report, Plan()).Run(Server(), Open(Plan()), Measure(failIn: round)));
        Assert.StartsWith("3 of 10 samples differ", error.Message);
        Assert.Equal(new[] { round + ": measure" }, Failed(report));
        Assert.Equal(saves, _servers.Sum(s => s.Count("cli_save"))); Assert.Equal(restarts, _restarts);
        Assert.Equal(saves, _client.Count("cli_extension valheim.session/leave")); // The failed round never left.
        Assert.True(Wrote(round + "-reading.json"));
        Assert.Equal("stop only the owned client", report.Steps[^1].Name);
        Assert.Equal(1, _process.Stops);
    }

    [Fact] public void AnAttachedClientIsDetachedButNeverStopped()
    {
        var report = new ScenarioReport("rounds");
        Rounds(report, Plan("attach")).Run(Server(), Open(Plan("attach")), Measure());
        Assert.True(report.Passed);
        Assert.Equal("attach to the operator's client at its menu, plugins pinned", report.Steps[0].Name);
        Assert.Equal("detach from the operator's client", report.Steps[^1].Name);
        Assert.Equal(0, _process.Stops); Assert.True(_client.Disposed);
        // The operator's character keeps devcommands only: it is never marked as cheated.
        Assert.Equal(0, _client.Count("cli_acknowledge_local_cheats")); Assert.False(_client.Access.CheatsAcknowledged);
        Assert.True(_client.Access.Devcommands);
    }
    [Fact] public void AnOwnedClientTurnsDevcommandsOnAtItsMenuAndAcknowledgesCheatsOnceInTheWorld()
    {
        var report = new ScenarioReport("rounds");
        Rounds(report, Plan()).Run(Server(), Open(Plan()), Measure());
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        // Two joins, one toggle and one acknowledgement: the second round reads the state and changes nothing.
        Assert.Equal(1, _client.Count("devcommands")); Assert.Equal(1, _client.Count("cli_acknowledge_local_cheats"));
        Assert.True(_client.Access.Devcommands && _client.Access.CheatsAcknowledged);
        var commands = _client.Commands.ToList();
        Assert.True(commands.IndexOf("cli_acknowledge_local_cheats") > commands.FindIndex(x => x.StartsWith("cli_extension valheim.session/join", StringComparison.Ordinal)));
    }

    [Fact] public void AClientThatNeverOpensIsNotClosedAndNothingJoins()
    {
        var report = new ScenarioReport("rounds");
        Assert.Throws<IOException>(() => Rounds(report, Plan()).Run(Server(), () => throw new IOException("port in use"), Measure()));
        Assert.Equal(new[] { "launch the owned client to its menu, plugins pinned" }, report.Steps.Select(s => s.Name));
        Assert.Empty(_client.Commands);
    }

    [Fact] public void ThreeRoundsNameEachSaveAndRestartByTheRoundTheyLeadTo()
    {
        var report = new ScenarioReport("rounds");
        Rounds(report, Plan(), names: ["one", "two", "three"]).Run(Server(), Open(Plan()), Measure());
        Assert.True(report.Passed);
        Assert.Equal(2, _restarts);
        Assert.Equal(new[] { "confirmed world save before two", "restart only the owned server before two", "confirmed world save before three", "restart only the owned server before three" },
            report.Steps.Select(s => s.Name).Where(n => !n.Contains(": ", StringComparison.Ordinal) && n.Contains(" before ", StringComparison.Ordinal)));
        Assert.Equal(report.Steps.Count, report.Steps.Select(s => s.Name).Distinct().Count());
        Assert.Equal("one,two,three", report.Provenance["clientRoundsCompleted"]);
    }

    [Fact] public void OneRoundWithoutAnArrivalLeavesThePlayerWhereItJoinedAndNeverRestarts()
    {
        var report = new ScenarioReport("rounds");
        new ClientRounds
        {
            Client = Plan(), WorldUid = WorldUid, Report = report, Output = Output, OwnedServer = new TestOwnedServer(Server, _ => { }),
            OpenStep = "launch the stock client", Rounds = ["only"],
        }.Run(Server(), Open(Plan()), Measure());
        Assert.True(report.Passed);
        Assert.Equal("launch the stock client", report.Steps[0].Name);
        Assert.DoesNotContain(report.Steps, s => s.Name.Contains("arrive", StringComparison.Ordinal));
        Assert.Equal(0, _servers[0].Count("cli_teleport_peer")); Assert.False(Wrote("only-arrival.json"));
        Assert.Equal(0, _servers[0].Count("cli_save")); // One round: nothing to save for, and no restart.
        Assert.Single(_servers);
        Assert.Equal(1, _process.Stops);
    }

    [Fact] public void TheModNamesTheArrivalStep()
    {
        var report = new ScenarioReport("rounds");
        new ClientRounds
        {
            Client = Plan("attach"), WorldUid = WorldUid, Report = report, Output = Output, OwnedServer = new TestOwnedServer(Server, _ => { }),
            Arrival = Point, ArriveStep = "arrive on the dry support point", Rounds = ["only"],
        }.Run(Server(), Open(Plan("attach")), _ => { });
        Assert.True(report.Passed);
        Assert.Contains(report.Steps, s => s.Name == "only: arrive on the dry support point");
        Assert.True(Wrote("only-arrival.json"));
    }

    [Fact] public void EvidenceIsNeverOverwritten()
    {
        var report = new ScenarioReport("rounds");
        Assert.Throws<IOException>(() => Rounds(report, Plan(), names: ["only"]).Run(Server(), Open(Plan()), round => round.Step("measure twice", () =>
        {
            round.Write("reading", new { value = 1 });
            round.Write("reading", new { value = 2 });
        })));
        Assert.Equal(new[] { "only: measure twice" }, Failed(report));
        Assert.Contains("\"value\": 1", File.ReadAllText(Path.Combine(Output, "only-reading.json")));
        Assert.Equal(1, _process.Stops);
    }

    public static TheoryData<string[]> BadRoundNames => new() { Array.Empty<string>(), new[] { "first", "First" }, new[] { "first round" }, new[] { "../first" }, new[] { "" } };
    [Theory][MemberData(nameof(BadRoundNames))]
    public void RoundNamesAreCheckedBeforeTheClientOpens(string[] names)
    {
        var report = new ScenarioReport("rounds");
        var error = Assert.Throws<ArgumentException>(() => Rounds(report, Plan(), names: names).Run(Server(), Open(Plan()), Measure()));
        Assert.StartsWith("Rounds: ", error.Message);
        Assert.Equal(0, _opens); Assert.Empty(report.Steps);
    }

    // ---- One teardown for both kinds of client ----
    // After the rounds, or after a failure, a joining and a hosting client are closed the same way: the client is closed
    // (an owned one stopped), then a hosted world is released; a failed close fails a passing run and never hides an earlier
    // failure. The hosted world is moved into the evidence only once no client can still host it.

    public static TheoryData<bool> JoinedAndHosted => new() { false, true };
    private void RunEither(bool hosted, ScenarioReport report, Action<ClientRound> measure, Game? game = null)
    {
        if (hosted) RunHosted(report, HostPlan(), game ?? new Game(Worlds), measure);
        else Rounds(report, Plan()).Run(Server(), Open(Plan()), measure);
    }
    private static string[] After(ScenarioReport report, string step) => report.Steps.Select(s => s.Name).SkipWhile(n => n != step).Skip(1).ToArray();
    private static string[] Closed(bool hosted) => hosted
        ? ["stop only the owned client", "move the hosted world from the client's local worlds into the evidence"]
        : ["stop only the owned client"];

    [Theory] [MemberData(nameof(JoinedAndHosted))]
    public void APassingRunClosesTheClientOnceThenReleasesTheWorld(bool hosted)
    {
        var report = new ScenarioReport("teardown");
        RunEither(hosted, report, Measure());
        Assert.True(report.Passed, string.Join("; ", Failed(report)));
        Assert.Equal(Closed(hosted), After(report, hosted ? "after-restart: the host leaves to its menu" : "after-restart: the client leaves to its menu"));
        Assert.Equal(1, _process.Stops);
    }

    [Theory] [MemberData(nameof(JoinedAndHosted))]
    public void AFailedMeasurementClosesTheClientTheSameWay(bool hosted)
    {
        var report = new ScenarioReport("teardown");
        var error = Assert.Throws<InvalidOperationException>(() => RunEither(hosted, report, Measure(failIn: "first")));
        Assert.StartsWith("3 of 10 samples differ", error.Message);
        Assert.Equal(new[] { "first: measure" }, Failed(report));
        Assert.Equal(Closed(hosted), After(report, "first: measure")); // The owned client stopped, so its world may move.
        Assert.Equal(1, _process.Stops);
        if (hosted) Assert.Empty(OurWorldFiles());
    }

    [Theory] [MemberData(nameof(JoinedAndHosted))]
    public void AFailedSaveStopsBeforeTheLeaveAndTheRestart(bool hosted)
    {
        _saves = false;
        var game = new Game(Worlds) { SaveAdvances = false }; var report = new ScenarioReport("teardown");
        Assert.Throws<InvalidOperationException>(() => RunEither(hosted, report, Measure(), game));
        Assert.Equal(new[] { "confirmed world save" }, Failed(report));
        Assert.Equal(Closed(hosted), After(report, "confirmed world save"));
        Assert.Equal(0, hosted ? game.Transport.Count("cli_extension valheim.session/leave") : _client.Count("cli_extension valheim.session/leave"));
        Assert.Equal(0, _restarts);
        if (hosted) Assert.Equal(1, game.Transport.Count("cli_start_host_world"));
    }

    [Theory] [MemberData(nameof(JoinedAndHosted))]
    public void AFailedCloseFailsAPassingRun(bool hosted)
    {
        _process.StopFailure = () => new TimeoutException("client did not stop");
        var report = new ScenarioReport("teardown");
        var close = Assert.Throws<TimeoutException>(() => RunEither(hosted, report, Measure()));
        Assert.Equal("client did not stop", close.Message);
        Assert.Equal(new[] { "stop only the owned client" }, Failed(report));
        Assert.False(report.Passed);
        // Every round ended with the client at its menu, so the world is moved although the process did not stop.
        Assert.Equal(Closed(hosted), After(report, hosted ? "after-restart: the host leaves to its menu" : "after-restart: the client leaves to its menu"));
    }

    [Theory] [MemberData(nameof(JoinedAndHosted))]
    public void AFailedCloseNeverHidesTheFailureBeforeIt(bool hosted)
    {
        _process.StopFailure = () => new TimeoutException("client did not stop");
        var report = new ScenarioReport("teardown");
        var first = Assert.Throws<InvalidOperationException>(() => RunEither(hosted, report, Measure(failIn: "first")));
        Assert.StartsWith("3 of 10 samples differ", first.Message);
        Assert.Equal(new[] { "first: measure", "stop only the owned client" }, Failed(report));
        Assert.Equal(1, _process.Stops);
        // The client failed in the world and may still run it: its world stays where it is, and the report names it.
        Assert.Equal(new[] { "stop only the owned client" }, After(report, "first: measure"));
        if (hosted) { Assert.Contains(Name + ".fwl", OurWorldFiles()); Assert.Contains(Name, report.Provenance["hostWorldLeftInPlace"]); }
    }

    // ---- What each kind of client accepts ----

    [Fact] public void AJoiningClientNeedsItsServerAndWorldUidBeforeTheClientOpens()
    {
        var noServer = new ClientRounds { Client = Plan(), WorldUid = WorldUid, Report = new ScenarioReport("x"), Output = Output };
        Assert.StartsWith("OwnedServer: ", Assert.Throws<ArgumentException>(() => noServer.Run(Server(), Open(Plan()), Measure())).Message);
        var noWorld = new ClientRounds { Client = Plan(), OwnedServer = new TestOwnedServer(Server), Report = new ScenarioReport("x"), Output = Output };
        Assert.StartsWith("WorldUid: ", Assert.Throws<ArgumentException>(() => noWorld.Run(Server(), Open(Plan()), Measure())).Message);
        Assert.Equal(0, _opens);
    }

    public static TheoryData<string> JoinedOnly => new() { "OwnedServer", "Lobby", "Arrival", "ArriveStep", "WorldUid" };
    [Theory] [MemberData(nameof(JoinedOnly))]
    public void AHostingClientRefusesWhatOnlyAJoiningClientUses(string option)
    {
        var plan = HostPlan(); var report = new ScenarioReport("host");
        var rounds = option switch
        {
            "OwnedServer" => new ClientRounds { Client = plan, Report = report, Output = Output, OwnedServer = new TestOwnedServer(Server) },
            "Lobby" => new ClientRounds { Client = plan, Report = report, Output = Output, Lobby = _ => new CrossplayLobby("E", "L") },
            "Arrival" => new ClientRounds { Client = plan, Report = report, Output = Output, Arrival = Point },
            "ArriveStep" => new ClientRounds { Client = plan, Report = report, Output = Output, ArriveStep = "arrive at the marker" },
            _ => new ClientRounds { Client = plan, Report = report, Output = Output, WorldUid = "999" },
        };
        Assert.StartsWith(option + ": ", Assert.Throws<ArgumentException>(() => rounds.Run(Open(plan, new Game(Worlds)), Measure())).Message);
        Assert.Equal(0, _opens); Assert.Empty(report.Steps); Assert.Empty(OurWorldFiles());
        // Negative control: the fixture's own world UID is accepted.
        var same = new ScenarioReport("host");
        using (new FakeClientDataDirectory(Save))
            new ClientRounds { Client = plan, Report = same, Output = Output, WorldUid = WorldUid, Rounds = ["only"] }.Run(Open(plan, new Game(Worlds)), Measure());
        Assert.True(same.Passed, string.Join("; ", Failed(same)));
    }

    [Fact] public void AHostingClientCanLeaveProtectionOff()
    {
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        using (new FakeClientDataDirectory(Save))
            new ClientRounds { Client = HostPlan(), Report = report, Output = Output, ProtectPlayer = false }.Run(Open(HostPlan(), game), Measure());
        Assert.True(report.Passed, string.Join("; ", Failed(report)));
        Assert.Contains("first: host the fixture world with the disposable character", report.Steps.Select(s => s.Name));
        Assert.Contains("after-restart: restart the hosted world", report.Steps.Select(s => s.Name));
        Assert.Equal(0, game.Transport.Count("cli_set_player_safety"));
    }

    // ---- A client that hosts its own world ----

    [Fact] public void TwoRoundsHostSaveRestartAndMoveTheWorldIntoTheEvidence()
    {
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        var placedDuringRun = new List<bool>();
        RunHosted(report, HostPlan(), game, Measure(also: round =>
        {
            Assert.Same(round.Server, round.Client); // The host is both.
            placedDuringRun.Add(File.Exists(Path.Combine(Worlds, Name + ".fwl")));
        }));

        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Equal(new[]
        {
            "preflight the fixture world and the owned client's install, before anything is copied or started",
            "preflight the native client's hosted-world save directory",
            "place the disposable fixture world in the client's local worlds",
            "launch the owned client to its menu, plugins pinned",
            "the client's ValheimCLI offers the session commands the rounds use",
            "first: host the fixture world with the disposable character, protected", "first: establish test access on the owned host", "first: measure",
            "confirmed world save", "first: the host leaves to its menu",
            "after-restart: restart the hosted world, protected", "after-restart: establish test access on the owned host", "after-restart: measure", "after-restart: the host leaves to its menu",
            "stop only the owned client",
            "move the hosted world from the client's local worlds into the evidence",
        }, report.Steps.Select(s => s.Name));
        var t = game.Transport;
        Assert.Equal(2, t.Count("cli_start_host_world"));
        Assert.All(t.Commands.Where(c => c.StartsWith("cli_start_host_world", StringComparison.Ordinal)), c => Assert.Equal($"cli_start_host_world {Name} --public false --crossplay false", c));
        Assert.Equal(1, t.Count("cli_extension valheim.session/save"));
        Assert.Equal(2, t.Count("cli_extension valheim.session/leave"));
        Assert.Equal(2, t.Count("cli_set_player_safety")); // Once per start, only once the host's player exists.
        var commands = t.Commands.ToList();
        Assert.True(commands.IndexOf("cli_select_character Tester") < commands.FindIndex(c => c.StartsWith("cli_start_host_world", StringComparison.Ordinal)));
        Assert.Equal(new[] { true, true }, placedDuringRun);
        Assert.Equal(1, _process.Stops); Assert.True(t.Disposed);
        Assert.Equal("host", report.Provenance["role"]);
        Assert.Equal(Name, report.Provenance["hostWorld"]);
        Assert.Equal("false", report.Provenance["hostCrossplay"]);
        Assert.False(report.Provenance.ContainsKey("hostMode")); // #301: a host always opens a listen server.
        Assert.Equal("first,after-restart", report.Provenance["hostRoundsCompleted"]);
        Assert.Equal("x64", report.Provenance["clientArchitecture"]); // The plan asked for none.
        // The world, with what the game wrote for it, left the client's worlds for the evidence; the user's world stayed.
        Assert.Empty(OurWorldFiles());
        Assert.Equal("the user's world", File.ReadAllText(Path.Combine(Worlds, "MyWorld.fwl")));
        string evidence = report.Provenance["hostWorldEvidence"];
        Assert.Equal(Path.Combine(Output, "host-world"), evidence);
        foreach (string file in new[] { Name + ".fwl", Name + ".db", Name + ".db.old" }) Assert.True(File.Exists(Path.Combine(evidence, file)), file);
        // The verified input copy stays beside it, as the server runner keeps its fixture copies.
        Assert.Single(Directory.GetDirectories(Output, "valheim-test-*"));
        Assert.True(File.Exists(Path.Combine(Output, "first-reading.json"))); Assert.True(File.Exists(Path.Combine(Output, "after-restart-reading.json")));
    }

    [Theory] [InlineData(Name + ".fwl")] [InlineData(Name + "_backup_auto-20260929.db")] [InlineData("hostfixture.db")]
    public void AWorldAlreadyNamedLikeTheFixtureIsNeverOverwrittenOrUsed(string existing)
    {
        // Negative control: the same run passes without this file (the first test).
        File.WriteAllText(Path.Combine(Worlds, existing), "the user's own");
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        var error = Assert.Throws<InvalidOperationException>(() => RunHosted(report, HostPlan(), game, Measure()));
        Assert.Contains("never copied over a world", error.Message);
        Assert.Equal(new[] { "place the disposable fixture world in the client's local worlds" }, Failed(report));
        Assert.Equal(0, _opens); Assert.Empty(game.Transport.Commands);
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(Worlds, existing)));
        Assert.Equal(new[] { existing }, Directory.EnumerateFiles(Worlds).Select(Path.GetFileName).Where(f => !f!.StartsWith("MyWorld", StringComparison.Ordinal)));
    }

    [Fact] public void AHostStartTheGameRefusesStopsBeforeAnyWaitAndStillCleansUp()
    {
        var game = new Game(Worlds) { StartReply = "ERROR: Main menu is not available" }; var report = new ScenarioReport("host");
        var error = Assert.Throws<InvalidOperationException>(() => RunHosted(report, HostPlan(), game, Measure()));
        Assert.Contains("failed: ERROR: Main menu is not available", error.Message);
        Assert.Equal(new[] { "first: host the fixture world with the disposable character, protected" }, Failed(report));
        Assert.Equal(1, game.Transport.Count("cli_start_host_world"));
        Assert.Equal(1, game.Transport.Count("cli_extension valheim.session/state")); // Only the menu check before the start.
        Assert.Equal(0, game.Transport.Count("cli_set_player_safety"));
        Assert.Equal(1, _process.Stops);
        Assert.Empty(OurWorldFiles()); // The owned client stopped, so its world was moved out.
        Assert.False(report.Provenance.ContainsKey("hostRoundsCompleted"));
    }

    [Fact] public void AStartThatLoadsAnotherWorldFailsUnprotected()
    {
        // What the game does when the named world is missing: it creates a fresh one, with another UID.
        var game = new Game(Worlds) { LoadedUid = "999" }; var report = new ScenarioReport("host");
        var error = Assert.Throws<WaitFailedException>(() => RunHosted(report, HostPlan(), game, Measure()));
        Assert.Contains("different world", error.Message);
        Assert.Equal(0, game.Transport.Count("cli_set_player_safety"));
        Assert.Equal(1, _process.Stops);
    }

    [Fact] public void ACrossplayHostStartsWithCrossplayAndRequiresThePlayFabBackend()
    {
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        RunHosted(report, HostPlan(crossplay: true), game, Measure());
        Assert.True(report.Passed);
        Assert.All(game.Transport.Commands.Where(c => c.StartsWith("cli_start_host_world", StringComparison.Ordinal)), c => Assert.EndsWith("--crossplay true", c));
        Assert.Equal("true", report.Provenance["hostCrossplay"]);
    }

    [Fact] public void AStartReplyWithoutThePlannedCrossplayFails()
    {
        // Negative control for the reply check: the game started a Steam world although crossplay was asked for.
        var game = new Game(Worlds) { StartsSteamWorld = true };
        var plan = HostPlan(crossplay: true);
        using var actor = game.Transport.Actor("host", plan.MenuExpectations);
        var error = Assert.Throws<InvalidOperationException>(() => HostWorlds.Start(actor, plan, Name, TimeSpan.FromSeconds(10)));
        Assert.Contains("did not start as planned", error.Message);
        Assert.Contains("crossplay=False", error.Message);
        Assert.Equal(1, game.Transport.Count("cli_start_host_world"));
        Assert.Equal(0, game.Transport.Count("cli_set_player_safety"));
    }

    [Fact] public void AnAttachedHostThatFailsLeavesItsWorldInPlaceAndIsOnlyDetached()
    {
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        Assert.Throws<InvalidOperationException>(() => RunHosted(report, HostPlan("attach"), game, Measure(failIn: "first")));
        Assert.Equal(new[] { "first: measure" }, Failed(report));
        Assert.Equal("detach from the operator's client", report.Steps[^1].Name);
        Assert.True(game.Transport.Disposed); Assert.Equal(0, _process.Stops);
        // The operator's client may still host it, so the world stays and the report names it.
        Assert.Contains(Name + ".fwl", OurWorldFiles());
        Assert.Contains(Name, report.Provenance["hostWorldLeftInPlace"]);
        Assert.False(report.Provenance.ContainsKey("hostWorldEvidence"));
    }

    [Fact] public void AnAttachedHostThatPassesHasLeftItsWorldSoTheWorldIsMoved()
    {
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        RunHosted(report, HostPlan("attach"), game, Measure());
        Assert.True(report.Passed);
        Assert.Empty(OurWorldFiles());
        Assert.Equal(0, _process.Stops);
    }

    // The preflight (#117): a wrong fixture or install stops the run before the fixture is copied or the client opened.
    [Fact] public void AFixtureWithAnotherUidStopsBeforeItIsCopiedOrTheClientOpens()
    {
        var plan = HostPlan(); plan.HostWorld!.WorldUid = "450017353"; // Another snapshot's UID behind the same world name.
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        var error = Assert.Throws<InvalidOperationException>(() => RunHosted(report, plan, game, Measure()));
        Assert.Contains($"holds world {Name} with UID {WorldUid}", error.Message);
        Assert.Equal(new[] { "preflight the fixture world and the owned client's install, before anything is copied or started" }, report.Steps.Select(s => s.Name));
        Assert.Equal(0, _opens); Assert.Empty(game.Transport.Commands);
        Assert.Empty(OurWorldFiles()); Assert.Empty(Directory.GetDirectories(Output, "valheim-test-*"));
        // Negative control: the fixture's own UID passes (the first test).
    }

    // Negative control for FakeClientDataDirectory: on a Mac test host the client is a Mac client, and a saveDirectory that is
    // not its data directory is refused before placement, whether that is the signed-in user's own (no scope) or a simulated
    // one elsewhere. The helper test below covers the platform decision on every CI host.
    [Theory] [InlineData("attach", false)] [InlineData("owned", false)] [InlineData("attach", true)] [InlineData("owned", true)]
    public void AMacHostRefusesAWorldDirectoryThatIsNotItsDataDirectoryBeforePlacement(string mode, bool simulated)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var plan = HostPlan(mode);
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        using (simulated ? new FakeClientDataDirectory(Path.Combine(_root, "another-client-data")) : null)
        {
            var error = Assert.Throws<InvalidOperationException>(() => Hosted(report, plan).Run(Open(plan, game), Measure()));
            Assert.Contains("hostWorld.saveDirectory", error.Message);
        }
        Assert.Equal(0, _opens);
        Assert.Empty(OurWorldFiles());
        Assert.Empty(game.Transport.Commands);
        Assert.Equal("preflight the native client's hosted-world save directory", report.Steps[^1].Name);
    }

    [Fact] public void MacNativeSavePreflightRefusesCustomPathsAndSavedirArguments()
    {
        string defaultPath = Path.Combine(_root, "default-client-data");
        string customPath = Path.Combine(_root, "custom-client-data");
        Assert.Contains("hostWorld.saveDirectory", Assert.Throws<InvalidOperationException>(() =>
            HostedWorld.RequireNativeSaveDirectory(ClientPlatform.MacOS, customPath, [], defaultPath)).Message);
        Assert.Contains("-savedir", Assert.Throws<InvalidOperationException>(() =>
            HostedWorld.RequireNativeSaveDirectory(ClientPlatform.MacOS, null, ["-savedir", customPath], defaultPath)).Message);
        Assert.Contains("-savedir", Assert.Throws<InvalidOperationException>(() =>
            HostedWorld.RequireNativeSaveDirectory(ClientPlatform.MacOS, null, ["--savedir=" + customPath], defaultPath)).Message);
        HostedWorld.RequireNativeSaveDirectory(ClientPlatform.MacOS, null, [], defaultPath);
        HostedWorld.RequireNativeSaveDirectory(ClientPlatform.MacOS, defaultPath, [], defaultPath);
        HostedWorld.RequireNativeSaveDirectory(ClientPlatform.Windows, customPath, ["-savedir", customPath], defaultPath);
        HostedWorld.RequireNativeSaveDirectory(ClientPlatform.Linux, customPath, [], defaultPath);
    }

    [Fact] public void AnInstallThatFailsThePreflightStopsBeforeTheFixtureIsCopied()
    {
        _install.Standing("expect.txt", $"valheimCLI.valheimCLI={_install.CliMd5} my.mod=absent\n"); // Two pins on one line.
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        var error = Assert.Throws<InvalidOperationException>(() => RunHosted(report, HostPlan(), game, Measure()));
        Assert.Contains("line 1 holds 2 key=value pins", error.Message);
        Assert.Equal(0, _opens); Assert.Empty(OurWorldFiles()); Assert.Empty(Directory.GetDirectories(Output, "valheim-test-*"));
        // An attached client's install is its operator's: only the fixture is preflighted.
        var attached = new ScenarioReport("host");
        RunHosted(attached, HostPlan("attach"), new Game(Worlds), Measure());
        Assert.True(attached.Passed);
        Assert.Equal("preflight the fixture world, before it is copied", attached.Steps[0].Name);
    }

    [Fact] public void AHostWithoutTheSessionCommandsStopsBeforeTheWorldRound()
    {
        var game = new Game(Worlds, leaveOffered: false); var report = new ScenarioReport("host");
        var error = Assert.Throws<InvalidOperationException>(() => RunHosted(report, HostPlan(), game, Measure()));
        Assert.Contains("lacks valheim.session/leave", error.Message);
        Assert.Contains("the Standard pack", error.Message);
        Assert.Equal(new[] { "the client's ValheimCLI offers the session commands the rounds use" }, Failed(report));
        Assert.Equal(0, game.Transport.Count("cli_start_host_world"));
        Assert.Equal(1, _process.Stops);
    }

    [Fact] public void AJoiningClientPlanIsNotAHost()
    {
        var plan = HostPlan(); plan.HostWorld = null;
        Assert.Throws<ArgumentException>(() => RunHosted(new ScenarioReport("host"), plan, new Game(Worlds), Measure()));
        Assert.Equal(0, _opens);
    }

    [Theory]
    [InlineData(new[] { "HostFixture.fwl", "HostFixture.db", "HostFixture.fwl.old", "HostFixture_backup_auto-1.db" }, "HostFixture")]
    [InlineData(new[] { "Abc.fwl", "Abc.db/chunk-0" }, "Abc")]
    // Valheim 1.0 saves a world as a directory of chunks (the layout a 1.0.16 server wrote this fixture in).
    [InlineData(new[] { "LifecycleFixture/_main.1.chunks", "LifecycleFixture/_main.1.db2", "LifecycleFixture/_main.1.fwl2", "LifecycleFixture/_main.1.ok" }, "LifecycleFixture")]
    [InlineData(new[] { "Abc\\_main.12.fwl2", "Abc\\_main.12.db2" }, "Abc")]
    public void TheHostedWorldIsNamedByItsFixture(string[] files, string name) => Assert.Equal(name, HostedWorld.NameOf(files));

    public static TheoryData<string[]> NotOneNamedWorld => new()
    {
        new[] { "A.fwl", "B.fwl", "A.db" }, // Two worlds.
        new[] { "HostFixture.db" }, // No world metadata.
        new[] { "HostFixture.fwl" }, // No world data: the game would generate it afresh.
        new[] { "HostFixture.fwl", "HostFixture.db", "readme.txt" }, // A file that is not the world's.
        new[] { "Ab.fwl", "Ab.db" }, // Too short a name for the game.
        new[] { "My World.fwl", "My World.db" }, // Not one command token.
        new[] { "sub/HostFixture.fwl", "HostFixture.db" }, // The metadata is not at the root.
        new[] { "HostFixture/_main.1.fwl2", "HostFixture.fwl", "HostFixture.db" }, // Two worlds: a chunked save and the older pair.
        new[] { "A1c/_main.1.fwl2", "B2c/_main.1.fwl2" }, // Two chunked worlds.
        new[] { "HostFixture/_main.1.fwl2", "readme.txt" }, // A file that is not the world's.
        new[] { "HostFixture/deeper/_main.1.fwl2" }, // The chunked metadata is not directly in the world's directory.
    };
    [Theory] [MemberData(nameof(NotOneNamedWorld))]
    public void AFixtureThatIsNotOneNamedWorldIsRefused(string[] files) => Assert.Throws<ArgumentException>(() => HostedWorld.NameOf(files));

}

/// <summary>An owned server for rounds tests: waits on the scripted server's session capability (or as told) and restarts with the given function.</summary>
internal sealed class TestOwnedServer(Func<GameActor> restart, Action<GameActor>? wait = null) : IOwnedServer
{
    public void WaitUntilJoinable(GameActor server)
    {
        if (wait != null) wait(server);
        else OwnedServerSession.WaitUntilJoinable(server, "test.mod/session", TimeSpan.FromSeconds(5));
    }
    public GameActor Restart() => restart();
}
