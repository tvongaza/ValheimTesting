using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// A hosted (listen-server) client against a scripted game and a fake owned process: the fixture world is placed in a
// temporary client data directory (never over a world already there), hosted, saved, restarted and moved into the
// evidence. No game, Steam or network connection.
public sealed class HostRoundsTests : IDisposable
{
    private const string WorldUid = "4242", Name = "HostFixture";
    private readonly string _root = Directory.CreateTempSubdirectory("host-rounds-").FullName;
    private string Fixture => Path.Combine(_root, "fixture");
    private string Save => Path.Combine(_root, "client-data");
    private string Worlds => Path.Combine(Save, "worlds_local");
    private string Output => Path.Combine(_root, "out");
    private readonly RoundProcess _process = new();
    private int _opens;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    public HostRoundsTests()
    {
        Directory.CreateDirectory(Fixture); Directory.CreateDirectory(Worlds); Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Fixture, Name + ".fwl"), "fixture metadata");
        File.WriteAllText(Path.Combine(Fixture, Name + ".db"), "fixture world");
        File.WriteAllText(Path.Combine(Worlds, "MyWorld.fwl"), "the user's world"); // Never touched.
        File.WriteAllText(Path.Combine(Worlds, "MyWorld.db"), "the user's world");
    }

    private ClientRunPlan Plan(string mode = "owned", bool crossplay = false) => new()
    {
        Mode = mode, Install = mode == "owned" ? Path.GetFullPath("client-install") : "", Port = 5556, Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
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
        public Game(string worlds)
        {
            bool devcommands = false;
            Transport = new ScriptedTransport()
                .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
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
                .Extension("valheim.session", "leave", _ =>
                {
                    Hosting = false;
                    File.WriteAllText(Path.Combine(worlds, Name + ".db.old"), "backup");
                    return new { source = "session-leave", complete = true, action = "leave" };
                }, readOnly: false)
                .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"));
        }
    }

    private HostRounds Rounds(ScenarioReport report, ClientRunPlan plan) => new() { Client = plan, Report = report, Output = Output };
    private Func<ClientSession> Open(ClientRunPlan plan, Game game) => () =>
    {
        _opens++;
        return plan.Owned
            ? ClientSession.Launch(plan, Output, () => _process, () => game.Transport, (_, _) => Task.CompletedTask)
            : ClientSession.Attach(plan, Output, game.Transport);
    };
    private static Action<ClientRound> Measure(string? failIn = null, Action<ClientRound>? also = null) => round => round.Step("measure", () =>
    {
        also?.Invoke(round);
        round.Write("reading", new { round = round.Name });
        if (round.Name == failIn) throw new InvalidOperationException("the marker is missing");
    });
    private static string[] Failed(ScenarioReport report) => report.Steps.Where(s => !s.Passed).Select(s => s.Name).ToArray();
    private string[] OurWorldFiles() => Directory.EnumerateFileSystemEntries(Worlds).Select(Path.GetFileName).Where(f => f!.StartsWith(Name, StringComparison.Ordinal)).Select(f => f!).ToArray();

    [Fact] public void TwoRoundsHostSaveRestartAndMoveTheWorldIntoTheEvidence()
    {
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        var placedDuringRun = new List<bool>();
        Rounds(report, Plan()).Run(Open(Plan(), game), Measure(also: round =>
        {
            Assert.Same(round.Server, round.Client); // The host is both.
            placedDuringRun.Add(File.Exists(Path.Combine(Worlds, Name + ".fwl")));
        }));

        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Equal(new[]
        {
            "place the disposable fixture world in the client's local worlds",
            "launch the owned client to its menu, plugins pinned",
            "first: host the fixture world with the disposable character, protected", "first: measure",
            "confirmed world save", "first: the host leaves to its menu",
            "after-restart: restart the hosted world, protected", "after-restart: measure", "after-restart: the host leaves to its menu",
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
        var error = Assert.Throws<InvalidOperationException>(() => Rounds(report, Plan()).Run(Open(Plan(), game), Measure()));
        Assert.Contains("never copied over a world", error.Message);
        Assert.Equal(new[] { "place the disposable fixture world in the client's local worlds" }, Failed(report));
        Assert.Equal(0, _opens); Assert.Empty(game.Transport.Commands);
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(Worlds, existing)));
        Assert.Equal(new[] { existing }, Directory.EnumerateFiles(Worlds).Select(Path.GetFileName).Where(f => !f!.StartsWith("MyWorld", StringComparison.Ordinal)));
    }

    [Fact] public void AHostStartTheGameRefusesStopsBeforeAnyWaitAndStillCleansUp()
    {
        var game = new Game(Worlds) { StartReply = "ERROR: Main menu is not available" }; var report = new ScenarioReport("host");
        var error = Assert.Throws<InvalidOperationException>(() => Rounds(report, Plan()).Run(Open(Plan(), game), Measure()));
        Assert.Contains("did not start as planned", error.Message);
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
        var error = Assert.Throws<InvalidOperationException>(() => Rounds(report, Plan()).Run(Open(Plan(), game), Measure()));
        Assert.Contains("different world", error.Message);
        Assert.Equal(0, game.Transport.Count("cli_set_player_safety"));
        Assert.Equal(1, _process.Stops);
    }

    [Fact] public void ACrossplayHostStartsWithCrossplayAndRequiresThePlayFabBackend()
    {
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        Rounds(report, Plan(crossplay: true)).Run(Open(Plan(crossplay: true), game), Measure());
        Assert.True(report.Passed);
        Assert.All(game.Transport.Commands.Where(c => c.StartsWith("cli_start_host_world", StringComparison.Ordinal)), c => Assert.EndsWith("--crossplay true", c));
        Assert.Equal("true", report.Provenance["hostCrossplay"]);
    }

    [Fact] public void AStartReplyWithoutThePlannedCrossplayFails()
    {
        // Negative control for the reply check: the game started a Steam world although crossplay was asked for.
        var game = new Game(Worlds) { StartsSteamWorld = true };
        var plan = Plan(crossplay: true);
        using var actor = game.Transport.Actor("host", plan.MenuExpectations);
        var error = Assert.Throws<InvalidOperationException>(() => HostWorlds.Start(actor, plan, Name, TimeSpan.FromSeconds(10)));
        Assert.Contains("did not start as planned", error.Message);
        Assert.Contains("crossplay=False", error.Message);
        Assert.Equal(1, game.Transport.Count("cli_start_host_world"));
        Assert.Equal(0, game.Transport.Count("cli_set_player_safety"));
    }

    [Fact] public void AFailedSaveStopsBeforeTheLeaveAndRestart()
    {
        var game = new Game(Worlds) { SaveAdvances = false }; var report = new ScenarioReport("host");
        Assert.Throws<InvalidOperationException>(() => Rounds(report, Plan()).Run(Open(Plan(), game), Measure()));
        Assert.Equal(new[] { "confirmed world save" }, Failed(report));
        Assert.Equal(0, game.Transport.Count("cli_extension valheim.session/leave"));
        Assert.Equal(1, game.Transport.Count("cli_start_host_world"));
        Assert.Equal(1, _process.Stops);
    }

    [Fact] public void AnAttachedHostThatFailsLeavesItsWorldInPlaceAndIsOnlyDetached()
    {
        var game = new Game(Worlds); var report = new ScenarioReport("host");
        Assert.Throws<InvalidOperationException>(() => Rounds(report, Plan("attach")).Run(Open(Plan("attach"), game), Measure(failIn: "first")));
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
        Rounds(report, Plan("attach")).Run(Open(Plan("attach"), game), Measure());
        Assert.True(report.Passed);
        Assert.Empty(OurWorldFiles());
        Assert.Equal(0, _process.Stops);
    }

    [Fact] public void AJoiningClientPlanIsNotAHost()
    {
        var plan = Plan(); plan.HostWorld = null;
        Assert.Throws<ArgumentException>(() => Rounds(new ScenarioReport("host"), plan).Run(Open(plan, new Game(Worlds)), Measure()));
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

    private sealed class RoundProcess : IServerProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public int Id => 78;
        public bool HasExited => _exit.Task.IsCompleted;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);
        public void Stop(TimeSpan timeout) { Stops++; _exit.TrySetResult(-1); }
        public void Dispose() { }
    }
}
