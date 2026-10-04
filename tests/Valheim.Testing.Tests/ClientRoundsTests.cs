using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// ClientRounds against scripted servers and a scripted client with a fake owned process: the order of the round steps,
// what stops the rounds (a failed save, restart or measurement), the evidence each failure leaves, and that the client is
// closed exactly once in every outcome. No game, Steam or network connection.
public sealed class ClientRoundsTests : IDisposable
{
    private const string WorldUid = "4242";
    private static readonly HeightExpectation Point = new(100, -40, 42.5f);
    private readonly string _output = Directory.CreateTempSubdirectory("client-rounds-").FullName;
    private readonly List<ScriptedTransport> _servers = [];
    private readonly ScriptedTransport _client;
    private readonly RoundProcess _process = new();
    private bool _saves = true;
    private bool _atPoint = true;
    private int _restarts, _opens;
    public void Dispose() => Directory.Delete(_output, recursive: true);

    public ClientRoundsTests()
    {
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
                source = "local-player-support", complete = true, x = _atPoint ? Point.X : 0f, y = Point.Height, z = _atPoint ? Point.Z : 0f, speed = 0f,
                grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            });
    }

    private static ClientRunPlan Plan(string mode = "owned") => new()
    {
        Mode = mode, Install = mode == "owned" ? Path.GetFullPath("client-install") : "", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
    };

    private ClientRunPlan PreparedPlan()
    {
        var plan = Plan();
        plan.Character = "tester-copy";
        plan.StartAtCharacterSave = true;
        string local = Path.Combine(_output, "characters_local"), evidence = Path.Combine(_output, "evidence");
        string steam = Path.Combine(_output, "userdata");
        Directory.CreateDirectory(local); Directory.CreateDirectory(evidence); Directory.CreateDirectory(steam);
        string source = Path.Combine(local, "seed.fch"), prepared = Path.Combine(evidence, "tester-copy.fch");
        File.WriteAllBytes(source, CharacterSavePositionTests.Profile(secondUid: 4242).File);
        string hash = CharacterStartCopy.Prepare(CharacterSavePositionTests.Register(_output, source), prepared, 4242, Point.X, Point.Height, Point.Z);
        plan.CharacterStart = new CharacterStartPlan
        {
            PreparedFile = prepared, Sha256 = hash, CharactersLocalDirectory = local, SteamUserDataDirectory = steam,
            CharacterStore = Path.Combine(_output, "store"),
        };
        return plan;
    }

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
        Client = plan, WorldUid = WorldUid, Report = report, Output = _output,
        WaitUntilJoinable = server => OwnedServerSession.WaitUntilJoinable(server, "test.mod/session", TimeSpan.FromSeconds(5)),
        RestartServer = restart ?? (() => { _restarts++; return Server(); }),
        Arrival = arrival ?? Point, SettleFor = TimeSpan.Zero, Rounds = names ?? ["first", "after-restart"],
    };

    private Func<ClientSession> Open(ClientRunPlan plan) => () =>
    {
        _opens++;
        return plan.Owned
            ? ClientSession.Launch(plan, _output, () => _process, () => _client, (_, _) => Task.CompletedTask)
            : ClientSession.Attach(plan, _output, _client);
    };

    // The mod's measurement: one step and one evidence file per round, failing where asked after writing its evidence.
    private static Action<ClientRound> Measure(string? failIn = null) => round => round.Step("measure", () =>
    {
        round.Write("reading", new { round = round.Name, server = round.Server.Name });
        if (round.Name == failIn) throw new InvalidOperationException("3 of 10 samples differ; see " + round.Name + "-reading.json.");
    });

    private static string[] Failed(ScenarioReport report) => report.Steps.Where(s => !s.Passed).Select(s => s.Name).ToArray();
    private bool Wrote(string name) => File.Exists(Path.Combine(_output, name));

    [Fact]
    public void LoadOnlyJoinCanLeaveProtectionOffWithoutSkippingWorldAndPluginChecks()
    {
        var plan = Plan();
        var report = new ScenarioReport("load-only");
        new ClientRounds
        {
            Client = plan, WorldUid = WorldUid, Report = report, Output = _output,
            WaitUntilJoinable = server => OwnedServerSession.WaitUntilJoinable(server, "test.mod/session", TimeSpan.FromSeconds(5)),
            RestartServer = Server, Rounds = ["first"], ProtectPlayer = false,
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
            "first: establish test access on the owned client",
            "first: arrive at the measurement point", "first: measure",
            "confirmed world save", "first: the client leaves to its menu", "restart only the owned server",
            "after-restart: the server kept the change",
            "after-restart: the server accepts game connections", "after-restart: join the owned server with the disposable character, protected",
            "after-restart: establish test access on the owned client",
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
        Assert.True(JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "first-arrival.json"))).RootElement.GetProperty("grounded").GetBoolean());
        Assert.Equal("first,after-restart", report.Provenance["clientRoundsCompleted"]);
        Assert.Equal("x64", report.Provenance["clientArchitecture"]);
    }

    [Fact] public void JoinedEventDrivenRoundsKeepOneWaitAndTracePerHopWithoutFastMode()
    {
        var plan = Plan();
        plan.EventDrivenArrival = true;
        _client.On("cli_teleport_test_mode on", _ => ScriptedTransport.Ok("OK: testFastTeleport enabled=True"))
            .On("cli_teleport_test_mode off", _ => ScriptedTransport.Ok("OK: testFastTeleport enabled=False"))
            .OnPrefix("cli_wait_teleportable ", _ => ScriptedTransport.Ok("OK: TELEPORTABLE ms=500"))
            .On("cli_teleport_trace_arm", _ => ScriptedTransport.Ok("OK: TELEPORT_TRACE_ARM id=7"))
            .OnPrefix("cli_teleport_trace_wait ", _ => ScriptedTransport.Ok("OK: TELEPORT_TRACE id=7 distant=True requestedMs=0 movedMs=2000 areaReadyMs=3400 floorReadyMs=3450 doneMs=3500 floorAtDone=True final=100,42.5,-40"))
            .Extension("valheim.world", "player-support-wait", _ => new
            {
                source = "local-player-support", complete = true, x = Point.X, y = Point.Height, z = Point.Z, speed = 0f,
                grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            });
        var report = new ScenarioReport("signal-rounds");
        Rounds(report, plan).Run(Server(), Open(plan), Measure());
        Assert.True(report.Passed);
        Assert.Equal(2, _client.Count("cli_wait_teleportable"));
        Assert.Equal(2, _client.Count("cli_teleport_trace_wait"));
        Assert.Equal(2, _client.Count("cli_extension valheim.world/player-support-wait"));
        Assert.Equal(0, _client.Count("cli_teleport_test_mode on"));
        Assert.Equal(0, _client.Count("cli_teleport_test_mode off"));
        Assert.Equal(0, _client.Count("cli_extension valheim.world/player-support"));
        Assert.True(Wrote("first-teleport-trace.json"));
        Assert.True(Wrote("after-restart-teleport-trace.json"));
        using var trace = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "first-teleport-trace.json")));
        Assert.Equal(2000, trace.RootElement.GetProperty("MovedMs").GetInt64());
        Assert.Equal(3400, trace.RootElement.GetProperty("AreaReadyMs").GetInt64());
        Assert.Equal(3450, trace.RootElement.GetProperty("FloorReadyMs").GetInt64());
        Assert.Equal(3500, trace.RootElement.GetProperty("DoneMs").GetInt64());
        Assert.Equal("game-side signal", report.Provenance["arrivalWait"]);
        Assert.Equal("False", report.Provenance["testFastTeleport"]);
    }

    [Fact] public void FastTeleportRequiresAnOwnedPinnedSignalRun()
    {
        var plan = Plan();
        plan.FastTestTeleports = true;
        Assert.Contains("eventDrivenArrival", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
        plan.EventDrivenArrival = true;
        plan.Mode = "attach";
        plan.Install = "";
        Assert.Contains("owned", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
        plan.Mode = "owned";
        plan.Install = Path.GetFullPath("client-install");
        plan.Pinning = "none";
        Assert.Contains("strictly pinned", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
    }

    [Fact] public void AJoinedFastTeleportIsRefusedBeforeOpeningTheClientOrMovingThePlayer()
    {
        var plan = Plan();
        plan.EventDrivenArrival = true;
        plan.FastTestTeleports = true;
        Assert.Contains("hostWorld.local", Assert.Throws<ArgumentException>(() =>
            Rounds(new ScenarioReport("guard"), plan).Run(Server(), Open(plan), Measure())).Message);
        Assert.Equal(0, _opens);
        Assert.Equal(0, _servers.Sum(server => server.Count("cli_teleport_peer")));
    }

    [Fact] public void DirectRoundCallCannotBypassFastTeleportPlanGuard()
    {
        var plan = Plan();
        plan.FastTestTeleports = true;
        Assert.Contains("eventDrivenArrival", Assert.Throws<ArgumentException>(() =>
            Rounds(new ScenarioReport("guard"), plan).Run(Server(), Open(plan), Measure())).Message);
        Assert.Equal(0, _opens);
        Assert.Equal(0, _servers.Sum(server => server.Count("cli_teleport_peer")));
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

    [Fact] public void StagedCharacterStartIsObservedWithoutAFirstTeleport()
    {
        var plan = PreparedPlan();
        var report = new ScenarioReport("rounds");
        Rounds(report, plan).Run(Server(), Open(plan), Measure());
        Assert.True(report.Passed);
        Assert.Equal(new[] { 0, 1 }, _servers.Select(s => s.Count("cli_teleport_peer")));
        Assert.Equal("characterSave", report.Provenance["clientStart"]);
        Assert.Contains(report.Steps, step => step.Name == "stage the pinned disposable local character" && step.Passed);
        var stepNames = report.Steps.Select(step => step.Name).ToArray();
        int ready = Array.IndexOf(stepNames, "the server accepts connections before client launch");
        int opened = Array.IndexOf(stepNames, "launch the owned client to its menu, plugins pinned");
        Assert.True(ready >= 0 && opened > ready);
        Assert.DoesNotContain(report.Steps, step => step.Name == "first: the server accepts game connections");
        Assert.Contains(report.Steps, step => step.Name == "remove only the staged character and its game-made backups" && step.Passed);
        Assert.False(File.Exists(Path.Combine(plan.CharacterStart!.CharactersLocalDirectory, "tester-copy.fch")));
        Assert.Equal(1, _client.Count("cli_select_character tester-copy"));
        Assert.True(Wrote("first-arrival.json"));
        Assert.True(Wrote("after-restart-arrival.json"));
    }

    [Fact] public void AFailedClientLaunchRemovesTheStagedCharacter()
    {
        var plan = PreparedPlan();
        var report = new ScenarioReport("rounds");
        Assert.Throws<IOException>(() => Rounds(report, plan).Run(Server(), () => throw new IOException("client did not start"), Measure()));
        Assert.False(File.Exists(Path.Combine(plan.CharacterStart!.CharactersLocalDirectory, "tester-copy.fch")));
        Assert.True(File.Exists(plan.CharacterStart.PreparedFile));
        Assert.Contains(report.Steps, step => step.Name == "remove only the staged character and its game-made backups" && step.Passed);
    }

    [Fact] public void AnUnregisteredPreparedCharacterIsRefusedBeforeTheClientOpens()
    {
        var plan = PreparedPlan();
        plan.CharacterStart!.CharacterStore = DisposableCharacterStore.Create(Path.Combine(_output, "other-store")).Root;
        var report = new ScenarioReport("rounds");
        bool opened = false;
        Assert.Throws<InvalidDataException>(() => Rounds(report, plan).Run(Server(), () => { opened = true; throw new IOException("must not open"); }, Measure()));
        Assert.False(opened);
        Assert.False(File.Exists(Path.Combine(plan.CharacterStart.CharactersLocalDirectory, "tester-copy.fch")));
    }

    [Fact] public void AMissingRegisteredCopyIsRefusedBeforeTheClientOpens()
    {
        var plan = PreparedPlan();
        File.Delete(Path.Combine(plan.CharacterStart!.CharacterStore, "tester.fch"));
        AssertRefusedBeforeOpening<FileNotFoundException>(plan);
    }

    [Fact] public void ASwappedRegisteredCopyIsRefusedBeforeTheClientOpens()
    {
        var plan = PreparedPlan();
        File.WriteAllBytes(Path.Combine(plan.CharacterStart!.CharacterStore, "tester.fch"), CharacterSavePositionTests.Profile(playerId: 99).File);
        AssertRefusedBeforeOpening<InvalidDataException>(plan);
    }

    private void AssertRefusedBeforeOpening<T>(ClientRunPlan plan) where T : Exception
    {
        var report = new ScenarioReport("rounds");
        bool opened = false;
        Assert.Throws<T>(() => Rounds(report, plan).Run(Server(), () => { opened = true; throw new IOException("must not open"); }, Measure()));
        Assert.False(opened);
        Assert.Equal(new[] { "stage the pinned disposable local character" }, Failed(report));
        Assert.False(File.Exists(Path.Combine(plan.CharacterStart!.CharactersLocalDirectory, "tester-copy.fch")));
    }

    [Fact] public void WrongStagedStartFailsRatherThanTeleportingOrMeasuring()
    {
        _atPoint = false;
        var plan = PreparedPlan();
        plan.ArrivalSeconds = 10;
        var report = new ScenarioReport("rounds");
        var error = Assert.Throws<InvalidOperationException>(() => Rounds(report, plan).Run(Server(), Open(plan), Measure()));
        Assert.Contains("No teleport was sent", error.Message);
        Assert.Contains("settled away", error.Message);
        Assert.Equal(new[] { "first: verify prepared character start at the measurement point" }, Failed(report));
        Assert.Equal(0, _servers.Sum(s => s.Count("cli_teleport_peer")));
        Assert.False(File.Exists(Path.Combine(plan.CharacterStart!.CharactersLocalDirectory, "tester-copy.fch")));
        Assert.False(Wrote("first-reading.json"));
    }

    [Fact] public void StagedStartWithoutAnArrivalPointIsRefusedBeforeOpeningTheClient()
    {
        var plan = Plan();
        plan.StartAtCharacterSave = true;
        var report = new ScenarioReport("rounds");
        var error = Assert.Throws<ArgumentException>(() => new ClientRounds
        {
            Client = plan, WorldUid = WorldUid, Report = report, Output = _output,
            WaitUntilJoinable = _ => { }, RestartServer = Server,
        }.Run(Server(), Open(plan), Measure()));
        Assert.Contains("needs an arrival point", error.Message);
        Assert.Equal(0, _opens);
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
        report.Write(_output);
        var junit = System.Xml.Linq.XDocument.Load(Path.Combine(_output, "junit.xml")).Root!;
        Assert.Equal("1", junit.Attribute("failures")!.Value);
        Assert.NotNull(junit.Elements("testcase").Single(c => c.Attribute("name")!.Value == "confirmed world save").Element("failure"));
        Assert.False(JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "result.json"))).RootElement.GetProperty("Passed").GetBoolean());
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

    [Fact] public void AFailedCloseFailsAPassingRun()
    {
        _process.RefuseStop = true;
        var report = new ScenarioReport("rounds");
        var close = Assert.Throws<TimeoutException>(() => Rounds(report, Plan()).Run(Server(), Open(Plan()), Measure()));
        Assert.Equal("client did not stop", close.Message);
        Assert.Equal(new[] { "stop only the owned client" }, Failed(report));
        Assert.False(report.Passed);
    }

    [Fact] public void AFailedCloseNeverHidesTheFailureBeforeIt()
    {
        _process.RefuseStop = true;
        var report = new ScenarioReport("rounds");
        var first = Assert.Throws<InvalidOperationException>(() => Rounds(report, Plan()).Run(Server(), Open(Plan()), Measure(failIn: "first")));
        Assert.StartsWith("3 of 10 samples differ", first.Message);
        Assert.Equal(new[] { "first: measure", "stop only the owned client" }, Failed(report));
        Assert.Equal(1, _process.Stops);
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
            Client = Plan(), WorldUid = WorldUid, Report = report, Output = _output, WaitUntilJoinable = _ => { }, RestartServer = Server,
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
            Client = Plan("attach"), WorldUid = WorldUid, Report = report, Output = _output, WaitUntilJoinable = _ => { }, RestartServer = Server,
            Arrival = Point, ArriveStep = "arrive on the dry support point", SettleFor = TimeSpan.Zero, Rounds = ["only"],
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
        Assert.Contains("\"value\": 1", File.ReadAllText(Path.Combine(_output, "only-reading.json")));
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

    private sealed class RoundProcess : IServerProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public bool RefuseStop;
        public int Id => 77;
        public bool HasExited => _exit.Task.IsCompleted;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);
        public void Stop(TimeSpan timeout)
        {
            Stops++;
            if (RefuseStop) throw new TimeoutException("client did not stop");
            _exit.TrySetResult(-1);
        }
        public void Dispose() { }
    }
}
