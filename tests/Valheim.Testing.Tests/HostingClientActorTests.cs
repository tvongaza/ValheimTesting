using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// HostingClientActor (#258 step 8): one client that hosts its own world is the session's IOwnedServer. Its world is joinable
// before a peer joins it, a restart re-hosts it, teardown closes the peers first and then the host, and the placed world is
// collected only once no client can still host it. No game: the host and its peer are scripted transports.
public sealed class HostingClientActorTests : IDisposable
{
    private const string WorldUid = "4242", Name = "HostFixture", HostSteamId = "76561198000000001";
    private readonly string _root = Directory.CreateTempSubdirectory("hosting-client-").FullName;
    private readonly PreflightInstall _install = PreflightInstall.Create(); // An owned host's install that passes the preflight.
    private readonly FakeOwnedProcess _hostProcess = new(77);
    private readonly List<string> _commands = []; // Host and peer commands in the order they arrived.
    private string Output => Path.Combine(_root, "out");
    private string Fixture => Path.Combine(_root, "fixture");
    private string Save => Path.Combine(_root, "client-data");
    private string Worlds => Path.Combine(Save, "worlds_local");
    private bool _hosting, _openServer = true, _peerJoined;
    private string _hostSteamId = HostSteamId;
    private int _readings, _peerOpens;

    public HostingClientActorTests()
    {
        Directory.CreateDirectory(Output); Directory.CreateDirectory(Fixture); Directory.CreateDirectory(Worlds);
        File.WriteAllBytes(Path.Combine(Fixture, Name + ".fwl"), OwnedRunPreflightTests.Metadata(Name, long.Parse(WorldUid)));
        File.WriteAllText(Path.Combine(Fixture, Name + ".db"), "fixture world");
    }
    public void Dispose() { _install.Dispose(); Directory.Delete(_root, recursive: true); }

    private ClientRunPlan HostPlan(string mode = "attach") => new()
    {
        Mode = mode, Install = mode == "owned" ? _install.Root : "", Port = 5556, Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = mode == "owned" ? _install.CliMd5 : new string('a', 32), ["my.mod"] = "absent" },
        InstallPins = mode == "owned" ? InstallPins.Of(_install.Root) : null,
        HostWorld = new() { World = new() { Source = Fixture, Sha256 = new(WorldFixture.Manifest(Fixture)) }, WorldUid = WorldUid, SaveDirectory = Save },
    };
    private static ClientRunPlan PeerPlan() => new()
    {
        Mode = "attach", Port = 5557, Character = "Peer", JoinsHost = true,
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('b', 32), ["my.mod"] = "absent" },
    };

    private void Note(string who, string command) { lock (_commands) _commands.Add(who + ": " + command); }

    // The host at its menu until it starts the world; then one loading reading and the world with its player. It reports its
    // Steam identity, an open server while it hosts.
    private ScriptedTransport HostTransport()
    {
        var transport = new ScriptedTransport()
            .ClientAccess(() => _hosting, () => _hosting)
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Tester' (tester, Local)"))
            .OnPrefix("cli_start_host_world ", command =>
            {
                Note("host", "start"); _hosting = true; _readings = 0;
                return ScriptedTransport.Ok($"OK: Starting hosted world '{command.Split(' ')[1]}' using character 'Tester' (tester, Local); open=true, public=False, crossplay=False, backend=Steamworks, passwordSet=False");
            })
            .On("cli_multiplayer_identity", _ =>
            {
                Note("host", "identity");
                return ScriptedTransport.Ok($"OK: steamId={_hostSteamId}, playFabLoginState=LoggedIn, playFabId=ABCDEF, backend=Steamworks, gameState=InGame, connectionStatus=Connected, isServer={(_hosting ? "True" : "False")}, isOpenServer={(_hosting && _openServer ? "True" : "False")}, server=");
            })
            .Extension("valheim.session", "state", _ =>
            {
                bool present = _hosting && ++_readings > 1;
                return new
                {
                    source = "session-state", complete = true, phase = present ? "world-present" : _hosting ? "loading" : "menu", worldUid = present ? WorldUid : null,
                    worldPresent = present, worldReady = present, server = present, dedicated = false, localPlayer = present, playerReady = present,
                    saving = false, loadError = false, connectionStatus = present ? "Connected" : "None",
                };
            })
            .Extension("valheim.session", "save", _ => new { source = "session-save", complete = true, worldUid = WorldUid, saved = true, before = 1, after = 2, milliseconds = 10 }, readOnly: false)
            .Extension("valheim.session", "leave", _ =>
            {
                Note("host", "leave"); _hosting = false; _peerJoined = false; // A host that leaves disconnects its peers.
                File.WriteAllText(Path.Combine(Worlds, Name + ".db.old"), "backup");
                return new { source = "session-leave", complete = true, action = "leave" };
            }, readOnly: false)
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"));
        return transport;
    }

    // A peer at its menu that joins a Steam host by its Steam ID.
    private ScriptedTransport PeerTransport(int open = 0) => new ScriptedTransport()
        .ClientAccess(() => _peerJoined)
        .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Peer' (peer, Local)"))
        .OnPrefix("cli_connect_steam_user ", command =>
        {
            Note("peer", command);
            string id = command.Split(' ')[1];
            _peerJoined = _hosting && id == HostSteamId;
            return ScriptedTransport.Ok($"OK: Steam user join started for {id} using character 'Peer' (peer, Local)");
        })
        .Extension("valheim.session", "state", _ => new
        {
            source = "session-state", complete = true, phase = _peerJoined ? "world-present" : "menu", worldUid = _peerJoined ? WorldUid : null, worldPresent = _peerJoined,
            worldReady = _peerJoined, server = false, dedicated = false, localPlayer = _peerJoined, playerReady = _peerJoined, saving = false, loadError = false,
            connectionStatus = _peerJoined ? "Connected" : "None",
        })
        .Extension("valheim.session", "leave", _ => { _peerJoined = false; return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
        .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"));

    private GameSession Session(ScenarioReport report, ClientRunPlan host, bool withPeer = true) =>
        FakeGameSession.Hosted(report, Output, host, (plan, name, output) =>
            plan.HostWorld != null
                ? plan.Owned ? ClientSession.Launch(plan, output, () => _hostProcess, HostTransport, (_, _) => Task.CompletedTask) : ClientSession.Attach(plan, output, HostTransport())
                : ClientSession.Attach(plan, Directory.CreateDirectory(Path.Combine(output, name)).FullName, PeerTransport(Interlocked.Increment(ref _peerOpens))),
            peers: withPeer ? new Dictionary<string, ClientRunPlan> { ["peer"] = PeerPlan() } : null);

    private static List<string> Steps(ScenarioReport report, StepPhase phase) => report.Steps.Where(step => step.Phase == phase).Select(step => step.Name).ToList();
    private string[] OurWorldFiles() => Directory.EnumerateFileSystemEntries(Worlds).Select(Path.GetFileName).Where(f => f!.StartsWith(Name, StringComparison.Ordinal)).Select(f => f!).ToArray();

    [Fact] public async Task TheHostsWorldIsJoinableBeforeItsPeerJoinsItBySteamId()
    {
        var report = new ScenarioReport("hosted");
        using var data = new FakeClientDataDirectory(Save);
        await using (var session = Session(report, HostPlan()))
        {
            await session.StartAsync();
            Assert.NotNull(session.Host); Assert.Null(session.Server);
            await session.Join("peer");
            Assert.True(_peerJoined);
        }
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        var setup = Steps(report, StepPhase.Setup);
        int joinable = setup.IndexOf("the host's world accepts game connections for client peer"), joined = setup.IndexOf($"client peer in host host's world {WorldUid}, protected");
        Assert.True(setup.IndexOf("host the fixture world with the disposable character, protected") < joinable);
        Assert.True(joinable >= 0 && joinable < joined, string.Join(" | ", setup));
        // The host's identity is read (it is an open server) before the peer's one join, which names the host's Steam ID.
        List<string> commands; lock (_commands) commands = [.. _commands];
        Assert.Equal(new[] { "host: start", "host: identity", "host: identity", $"peer: cli_connect_steam_user {HostSteamId}", "host: leave" }, commands);
    }

    [Fact] public async Task APeerNeverJoinsAHostWhoseWorldIsNotOpen()
    {
        _openServer = false; // Negative control of the test above: the same session, the host not an open server.
        var report = new ScenarioReport("hosted");
        using var data = new FakeClientDataDirectory(Save);
        await using var session = Session(report, HostPlan());
        await session.StartAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.Join("peer"));
        Assert.Contains("not an open server", error.Message);
        Assert.Equal(new[] { "the host's world accepts game connections for client peer" }, report.Steps.Where(s => !s.Passed).Select(s => s.Name));
        lock (_commands) Assert.DoesNotContain(_commands, c => c.StartsWith("peer: ", StringComparison.Ordinal));
    }

    [Theory] [InlineData("0")] [InlineData("unavailable [InvalidOperationException]")]
    public async Task APeerNeverJoinsAHostWithoutASignedInSteamUser(string steamId)
    {
        _hostSteamId = steamId;
        var report = new ScenarioReport("hosted");
        using var data = new FakeClientDataDirectory(Save);
        await using var session = Session(report, HostPlan());
        await session.StartAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.Join("peer"));
        Assert.Contains("not a signed-in Steam user's", error.Message);
        lock (_commands) Assert.DoesNotContain(_commands, c => c.StartsWith("peer: ", StringComparison.Ordinal));
    }

    [Fact] public async Task AHostWhoseFixtureFailsItsPreflightStopsBeforeAnyPeerStarts()
    {
        var plan = HostPlan();
        File.WriteAllText(Path.Combine(Fixture, Name + ".db"), "another world"); // No longer the pinned fixture.
        var report = new ScenarioReport("hosted");
        using var data = new FakeClientDataDirectory(Save);
        await using var session = Session(report, plan);
        await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync());
        Assert.Equal(new[] { "preflight the fixture world, before it is copied" }, report.Steps.Where(s => !s.Passed).Select(s => s.Name));
        Assert.Equal(0, Volatile.Read(ref _peerOpens));
        Assert.Empty(OurWorldFiles());
    }

    [Fact] public async Task ARestartReHostsTheSameWorldInTheSameProcess()
    {
        var report = new ScenarioReport("hosted");
        using var data = new FakeClientDataDirectory(Save);
        await using var session = Session(report, HostPlan("owned"), withPeer: false);
        await session.StartAsync();
        var host = session.Host!;
        var before = host.Game;
        Assert.Same(before, host.Restart());
        host.WaitUntilJoinable(host.Game);
        List<string> commands; lock (_commands) commands = [.. _commands];
        Assert.Equal(new[] { "host: start", "host: leave", "host: start", "host: identity" }, commands);
        Assert.Contains("establish test access on the owned host", Steps(report, StepPhase.Setup));
        Assert.Equal(0, _hostProcess.Stops); // A host's restart is the same process.
    }

    [Fact] public async Task TeardownClosesThePeerThenTheHostThenCollectsTheWorld()
    {
        var report = new ScenarioReport("hosted");
        using var data = new FakeClientDataDirectory(Save);
        File.WriteAllText(Path.Combine(Worlds, "MyWorld.fwl"), "the user's world");
        await using (var session = Session(report, HostPlan("owned")))
        {
            await session.StartAsync();
            await session.Join("peer");
            Assert.Equal(new[] { Name + ".db", Name + ".fwl" }, OurWorldFiles().Order(StringComparer.Ordinal)); // Placed while the host runs.
        }
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Equal(new[] { "detach from the operator's client peer", "the host leaves its world to its menu", "stop only the owned hosting client host",
            "move the hosted world from the client's local worlds into the evidence" }, Steps(report, StepPhase.Cleanup));
        Assert.Equal(1, _hostProcess.Stops);
        Assert.Empty(OurWorldFiles());
        Assert.Equal("the user's world", File.ReadAllText(Path.Combine(Worlds, "MyWorld.fwl")));
        foreach (string file in new[] { Name + ".fwl", Name + ".db", Name + ".db.old" }) Assert.True(File.Exists(Path.Combine(report.Provenance["hostWorldEvidence"], file)), file);
    }

    [Fact] public async Task AnOwnedHostThatDoesNotStopLeavesItsWorldInPlace()
    {
        // The world is collected only after the host stopped: a failed stop after a failure (no leave) means it may still host it.
        _hostProcess.StopFailure = () => new IOException("the stop could not be proven");
        var report = new ScenarioReport("hosted");
        using var data = new FakeClientDataDirectory(Save);
        await using (var session = Session(report, HostPlan("owned"), withPeer: false))
        {
            await session.StartAsync();
            report.RecordFailure(StepPhase.Scenario, "the scenario failed", new InvalidOperationException("a measurement differs"));
        }
        Assert.Equal(new[] { "the scenario failed", "stop only the owned hosting client host" }, report.Steps.Where(s => !s.Passed).Select(s => s.Name));
        Assert.DoesNotContain(report.Steps, s => s.Name.StartsWith("move the hosted world", StringComparison.Ordinal) || s.Name == "the host leaves its world to its menu");
        Assert.Equal(new[] { Name + ".db", Name + ".fwl" }, OurWorldFiles().Order(StringComparer.Ordinal));
        Assert.Equal(Worlds + " (" + Name + ")", report.Provenance["hostWorldLeftInPlace"]);
    }

    [Fact] public async Task AJoinRefusesAPeerOfTheWrongKind()
    {
        var report = new ScenarioReport("hosted");
        using var data = new FakeClientDataDirectory(Save);
        var notAPeer = PeerPlan(); notAPeer.JoinsHost = false; notAPeer.Join = "127.0.0.1:2456";
        await using var session = FakeGameSession.Hosted(report, Output, HostPlan(), (plan, name, output) =>
            // Each client's evidence in its own folder: they open at once.
            plan.HostWorld != null ? ClientSession.Attach(plan, output, HostTransport()) : ClientSession.Attach(plan, Directory.CreateDirectory(Path.Combine(output, name)).FullName, PeerTransport()),
            peers: new Dictionary<string, ClientRunPlan> { ["other"] = notAPeer });
        await session.StartAsync();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => session.Join("other"));
        Assert.Contains("set joinsHost", error.Message);
    }

    [Fact] public void APeerPlanJoinsTheHostOnly()
    {
        PeerPlan().Validate();
        foreach (var change in new Action<ClientRunPlan>[] { p => p.Join = "127.0.0.1:2456", p => p.PasswordVariable = "PW", p => p.Crossplay = true })
        {
            var plan = PeerPlan(); change(plan);
            Assert.Contains("joinsHost", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
        }
        var joiner = PeerPlan();
        Assert.Throws<ArgumentException>(() => new SessionControl(new ScriptedTransport().Actor()).JoinWorld(joiner, WorldUid));
    }

    [Fact] public void ASessionHasADedicatedServerOrAHostNotBoth()
    {
        var plan = HostPlan();
        var error = Assert.Throws<ArgumentException>(() => new GameSession(new ScenarioReport("both"), Output, WorldUid,
            _ => throw new InvalidOperationException("not built"), [], CancellationToken.None,
            token => HostingClientActor.OnThisMachine("host", plan, Output, token)));
        Assert.Contains("not both", error.Message);
    }
}
