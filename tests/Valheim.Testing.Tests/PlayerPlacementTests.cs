using valheim_cli.Testing;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public class PlayerPlacementTests
{
    private static readonly HeightExpectation Point = new(100, -40, 42.5f);
    private static object Standing(float y = 42.5f, bool grounded = true, float speed = 0, bool flying = false) =>
        new { source = "local-player-support", complete = true, x = 100f, y, z = -40f, speed, grounded, flying, attached = false, dead = false, teleporting = false, units = "metres" };
    private static object Loading() => new { source = "local-player-support", complete = false };
    private static ScriptedTransport NoIntro() =>
        new ScriptedTransport().OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0.6,33.7,2.8 ms=4"));

    private const string Peers = "PEER 1 character position=0.0,40.00,0.0 zone=0,0";
    private const string TraceOk = "OK: TELEPORT_TRACE id=7 distant=True requestedMs=0 movedMs=2000 areaReadyMs=3500 floorReadyMs=3600 doneMs=3620 floorAtDone=True final=100,42.5,-40";
    private const string TraceNoFloor = "OK: TELEPORT_TRACE id=7 distant=True requestedMs=0 movedMs=2000 areaReadyMs=3500 floorReadyMs=-1 doneMs=3620 floorAtDone=False final=100,42.5,-40";
    private static CommandResult SupportTimeout() => CommandResult.FromOutput("cli_extension valheim.world/player-support-wait",
        ["EXTENSION_RESULT {\"ok\":false,\"code\":\"support_timeout\",\"message\":\"still settling\"}",
            "ERROR: code=support_timeout message=Extension failed; see structured result."]);

    // A server that moves its one peer once, and a client that answers every arrival phase; the trace, the readiness wait
    // and the readings before and after the teleport are the parts a test varies.
    private static (ScriptedTransport Server, ScriptedTransport Client) SignalTransports(string trace = TraceOk,
        Func<string, CommandResult>? teleportable = null, Func<object>? before = null, Func<object>? landed = null, Func<string, CommandResult>? teleport = null,
        Func<string, CommandResult>? intro = null, Func<string, CommandResult>? traceWait = null,
        Func<string, CommandResult>? supportWait = null)
    {
        var server = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", Peers))
            .OnPrefix("cli_teleport_peer ", teleport ?? (_ => ScriptedTransport.Ok("OK: asked peer 1 to teleport")));
        var client = new ScriptedTransport()
            .OnPrefix("cli_skip_intro", intro ?? (_ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0.6,33.7,2.8 ms=4")))
            .OnPrefix("cli_wait_teleportable ", teleportable ?? (_ => ScriptedTransport.Ok("OK: TELEPORTABLE ms=500 stillMs=0 cooldownSeconds=2.00 grounded=True position=0,40,0")))
            .On("cli_teleport_trace_arm", _ => ScriptedTransport.Ok("OK: TELEPORT_TRACE_ARM id=7"))
            .OnPrefix("cli_teleport_trace_wait ", traceWait ?? (_ => ScriptedTransport.Ok(trace)))
            .Extension("valheim.session", "teleport-signals", _ => new { source = "teleport-signals", complete = true })
            .Extension("valheim.world", "player-support", _ => (before ?? (() => Standing(y: 40f)))())
            .Extension("valheim.world", "player-support-wait", _ => (landed ?? (() => Standing()))());
        if (supportWait != null) client.OnPrefix("cli_extension valheim.world/player-support-wait ", supportWait);
        return (server, client);
    }

    [Fact]
    public void HealthyArrivalMakesOneWaitPerPhaseAndOneTeleport()
    {
        var (serverTransport, clientTransport) = SignalTransports();
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var before = client.CommandTimeout;
        var result = PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30));
        Assert.True(result.Support.GetProperty("grounded").GetBoolean());
        Assert.Contains("floorAtDone=True", result.Trace);
        Assert.Equal(2000, result.Timing.MovedMs);
        Assert.Equal(3500, result.Timing.AreaReadyMs);
        Assert.Equal(3600, result.Timing.FloorReadyMs);
        Assert.Equal(3620, result.Timing.DoneMs);
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
        Assert.Contains("cli_teleport_peer 1 100 43 -40", serverTransport.Commands);
        Assert.Equal(1, clientTransport.Count("cli_wait_teleportable"));
        Assert.Equal(1, clientTransport.Count("cli_teleport_trace_wait"));
        Assert.Equal(1, clientTransport.Count("cli_extension valheim.world/player-support-wait"));
        Assert.Equal(1, clientTransport.Count("cli_extension valheim.world/player-support")); // the one flying check, never a polling loop
        Assert.Equal(0, clientTransport.Count("cli_teleport_test_mode")); // the game's ordinary timing, always
        Assert.Contains(clientTransport.Commands, command => command.StartsWith("cli_wait_teleportable 5 ", StringComparison.Ordinal));
        // With the intro skipped, a player need not be grounded: one that spawns swimming (29 Sep: fixture copies sharing a
        // world UID put the character in this copy's water) is teleported like a standing one.
        Assert.Contains(clientTransport.Commands, command => command.StartsWith("cli_wait_teleportable ", StringComparison.Ordinal) && command.EndsWith(" 0 False", StringComparison.Ordinal));
        Assert.Equal(1, clientTransport.Count("cli_extensions")); // one capability listing per arrival
        Assert.Equal(before, client.CommandTimeout);
        // In order: the intro, the flying check, readiness, the armed trace; the server's teleport; then the waits.
        var commands = clientTransport.Commands.ToList();
        int Index(string prefix) => commands.FindIndex(c => c == prefix || c.StartsWith(prefix + " ", StringComparison.Ordinal));
        Assert.True(Index("cli_skip_intro") < Index("cli_extension valheim.world/player-support"));
        Assert.True(Index("cli_extension valheim.world/player-support") < Index("cli_wait_teleportable"));
        Assert.True(Index("cli_wait_teleportable") < Index("cli_teleport_trace_arm"));
        Assert.True(Index("cli_teleport_trace_arm") < Index("cli_teleport_trace_wait"));
        Assert.True(Index("cli_teleport_trace_wait") < Index("cli_extension valheim.world/player-support-wait"));
    }

    [Fact]
    public void GameWaitExpiryCanBeFollowedByArrivalWithoutRepeatingTheTeleport()
    {
        int ready = 0, trace = 0, support = 0;
        var (serverTransport, clientTransport) = SignalTransports(
            teleportable: _ => ++ready == 1
                ? CommandResult.FromOutput("cli_wait_teleportable", ["ERROR: code=teleport_ready_timeout pending=loading ms=5000"])
                : ScriptedTransport.Ok("OK: TELEPORTABLE ms=0"),
            traceWait: _ => ++trace == 1
                ? CommandResult.FromOutput("cli_teleport_trace_wait", ["ERROR: code=teleport_trace_timeout id=7 ms=5000"])
                : ScriptedTransport.Ok(TraceOk),
            supportWait: _ => ++support == 1
                ? SupportTimeout()
                : ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("valheim.world", Standing())));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();

        var result = PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30));

        Assert.Equal(Point, result.Target);
        Assert.Equal(2, ready);
        Assert.Equal(2, trace);
        Assert.Equal(2, support);
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
        Assert.All(clientTransport.Commands.Where(c => c.StartsWith("cli_teleport_trace_wait ", StringComparison.Ordinal)),
            command => Assert.StartsWith("cli_teleport_trace_wait 7 ", command));
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("trace")]
    [InlineData("support")]
    public void CancellationAfterAGameWaitExpiryStopsBeforeAnotherInterval(string phase)
    {
        using var cancel = new CancellationTokenSource();
        var (serverTransport, clientTransport) = SignalTransports(
            teleportable: _ =>
            {
                if (phase == "ready") { cancel.Cancel(); return CommandResult.FromOutput("cli_wait_teleportable", ["ERROR: code=teleport_ready_timeout pending=loading ms=5000"]); }
                return ScriptedTransport.Ok("OK: TELEPORTABLE ms=0");
            },
            traceWait: _ =>
            {
                if (phase == "trace") { cancel.Cancel(); return CommandResult.FromOutput("cli_teleport_trace_wait", ["ERROR: code=teleport_trace_timeout id=7 ms=5000"]); }
                return ScriptedTransport.Ok(TraceOk);
            },
            supportWait: _ =>
            {
                if (phase == "support") { cancel.Cancel(); return SupportTimeout(); }
                return ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("valheim.world", Standing()));
            });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();

        Assert.Throws<OperationCanceledException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30), cancel.Token));
        Assert.Equal(1, clientTransport.Count(phase switch
        {
            "ready" => "cli_wait_teleportable", "trace" => "cli_teleport_trace_wait", _ => "cli_extension valheim.world/player-support-wait",
        }));
        Assert.Equal(phase == "ready" ? 0 : 1, serverTransport.Count("cli_teleport_peer"));
    }

    [Fact]
    public void CancellationBeforeIntroDoesNotSendTheOneShotCommand()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var (serverTransport, clientTransport) = SignalTransports();
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Throws<OperationCanceledException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30), cancel.Token));
        Assert.Equal(0, clientTransport.Count("cli_skip_intro"));
        Assert.Equal(0, serverTransport.Count("cli_teleport_peer"));
    }

    [Fact]
    public void UnrelatedTraceRefusalDoesNotWaitAgain()
    {
        var (serverTransport, clientTransport) = SignalTransports(traceWait: _ => ScriptedTransport.Ok("ERROR: code=teleport_trace_missing id=7"));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, clientTransport.Count("cli_teleport_trace_wait"));
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }

    [Fact]
    public void LostTraceReplyIsNotTreatedAsAnInGameTimeout()
    {
        var (serverTransport, clientTransport) = SignalTransports(traceWait: _ => new CommandResult
        {
            Ok = false, ErrorCode = "connection_lost", Message = "the connection closed",
            Output = ["ERROR: code=teleport_trace_timeout id=7 ms=5000"],
        });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, clientTransport.Count("cli_teleport_trace_wait"));
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }

    [Fact]
    public void LostSupportReplyIsNotTreatedAsAnInGameTimeout()
    {
        var (serverTransport, clientTransport) = SignalTransports(supportWait: _ => new CommandResult
        {
            Ok = false, ErrorCode = "connection_lost", Message = "the connection closed",
            Output = ["EXTENSION_RESULT {\"ok\":false,\"code\":\"support_timeout\",\"message\":\"still settling\"}"],
        });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Contains("support_timeout", Assert.Throws<InvalidOperationException>(() =>
            PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30))).Message);
        Assert.Equal(1, clientTransport.Count("cli_extension valheim.world/player-support-wait"));
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }

    [Theory]
    [InlineData("ready", "pending=teleport cooldown")]
    [InlineData("trace", "pending=floor loading")]
    [InlineData("support", "still settling")]
    public void RepeatedGameTimeoutsKeepTheLastReasonAndDoNotRepeatTeleport(string phase, string reason)
    {
        TimeSpan elapsed = TimeSpan.Zero;
        void Advance(string command, int secondsIndex) => elapsed += TimeSpan.FromSeconds(
            double.Parse(command.Split(' ')[secondsIndex], System.Globalization.CultureInfo.InvariantCulture));
        int ready = 0, trace = 0, support = 0;
        var (serverTransport, clientTransport) = SignalTransports(
            teleportable: command =>
            {
                if (phase != "ready") return ScriptedTransport.Ok("OK: TELEPORTABLE ms=0");
                ready++;
                Advance(command, 1);
                return CommandResult.FromOutput("cli_wait_teleportable", ["ERROR: code=teleport_ready_timeout pending=teleport cooldown"]);
            },
            traceWait: command =>
            {
                if (phase != "trace") return ScriptedTransport.Ok(TraceOk);
                trace++;
                Advance(command, 2);
                return CommandResult.FromOutput("cli_teleport_trace_wait", ["ERROR: code=teleport_trace_timeout id=7 pending=floor loading"]);
            },
            supportWait: command =>
            {
                if (phase != "support") return ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("valheim.world", Standing()));
                support++;
                Advance(command, 5);
                return SupportTimeout();
            });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var error = Assert.Throws<TimeoutException>(() => PlayerPlacement.ArriveCore(server, client, Point,
            TimeSpan.FromSeconds(12.5), default, skipIntro: false, loadedGround: false, () => elapsed));

        Assert.Contains(reason, error.Message);
        Assert.Contains(phase == "ready" ? "never became ready" : phase == "trace" ? "did not complete" : "did not settle", error.Message);
        Assert.Equal(phase == "ready" ? 0 : 1, serverTransport.Count("cli_teleport_peer"));
        int count = phase == "ready" ? ready : phase == "trace" ? trace : support;
        Assert.Equal(3, count);
        string prefix = phase switch
        {
            "ready" => "cli_wait_teleportable ",
            "trace" => "cli_teleport_trace_wait ",
            _ => "cli_extension valheim.world/player-support-wait ",
        };
        var seconds = clientTransport.Commands.Where(command => command.StartsWith(prefix, StringComparison.Ordinal))
            .Select(command => double.Parse(command.Split(' ')[phase == "ready" ? 1 : phase == "trace" ? 2 : 5], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(count, seconds.Length);
        Assert.Equal(new[] { 5d, 5d, 2.5d }, seconds);
        if (phase == "support") Assert.All(seconds, value => Assert.True(value >= .3, "A support slice must allow the 250 ms hold"));
    }

    [Fact]
    public void SupportDoesNotRestartAnImpossibleSubHoldTail()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        var (serverTransport, clientTransport) = SignalTransports(supportWait: command =>
        {
            elapsed += TimeSpan.FromSeconds(double.Parse(command.Split(' ')[5], System.Globalization.CultureInfo.InvariantCulture));
            return SupportTimeout();
        });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();

        var error = Assert.Throws<TimeoutException>(() => PlayerPlacement.ArriveCore(server, client, Point,
            TimeSpan.FromSeconds(10.2), default, skipIntro: false, loadedGround: false, () => elapsed));
        Assert.Contains("still settling", error.Message);
        Assert.Equal(2, clientTransport.Count("cli_extension valheim.world/player-support-wait"));
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }

    [Fact]
    public void OtherSupportFailureIsNotTreatedAsAWaitExpiry()
    {
        var (serverTransport, clientTransport) = SignalTransports(supportWait: _ => ScriptedTransport.Ok(
            "EXTENSION_RESULT {\"ok\":false,\"code\":\"no_local_player\",\"message\":\"player left\"}"));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Contains("no_local_player", Assert.Throws<InvalidOperationException>(() =>
            PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30))).Message);
        Assert.Equal(1, clientTransport.Count("cli_extension valheim.world/player-support-wait"));
    }

    [Fact]
    public void ArrivalRefusesACompletionWithoutFloorAndNeverRetries()
    {
        var (serverTransport, clientTransport) = SignalTransports(TraceNoFloor);
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Contains("without a ready floor", Assert.Throws<InvalidOperationException>(() =>
            PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30))).Message);
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
        Assert.Equal(0, clientTransport.Count("cli_extension valheim.world/player-support-wait"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void ArrivalTimeoutIsPositiveAndAtMostTenMinutes(int seconds)
    {
        var (serverTransport, clientTransport) = SignalTransports();
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(seconds)));
        Assert.All(clientTransport.Commands.Concat(serverTransport.Commands), c => Assert.StartsWith("cli_expect", c)); // the actors' own pin checks only
    }

    // Without a server the client teleports its own player (two players: the server cannot name one); no peer is read.
    [Fact] public void WithoutAServerTheClientTeleportsItselfOnce()
    {
        var (_, clientTransport) = SignalTransports();
        clientTransport.OnPrefix("cli_teleport ", _ => ScriptedTransport.Ok("OK: Teleported to (100.0, 43.0, -40.0)"));
        using var client = clientTransport.Actor();
        var result = PlayerPlacement.Arrive(null, client, Point, TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { "cli_teleport 100 43 -40" }, clientTransport.Commands.Where(c => c.StartsWith("cli_teleport ", StringComparison.Ordinal)));
        Assert.Equal(0, clientTransport.Count("cli_peers"));
        Assert.Equal(Point, result.Target);
        var commands = clientTransport.Commands.ToList();
        Assert.True(commands.IndexOf("cli_teleport_trace_arm") < commands.IndexOf("cli_teleport 100 43 -40"));
        Assert.True(commands.IndexOf("cli_teleport 100 43 -40") < commands.FindIndex(c => c.StartsWith("cli_teleport_trace_wait ", StringComparison.Ordinal)));
    }

    // A client without test access is refused by ValheimCLI: that fails the arrival, and nothing is retried or awaited.
    [Fact] public void ARefusedSelfTeleportFailsWithoutWaitingOrRetrying()
    {
        var (_, clientTransport) = SignalTransports();
        clientTransport.OnPrefix("cli_teleport ", _ => ScriptedTransport.Ok("REFUSED: cheats are not acknowledged on this client"));
        using var client = clientTransport.Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(null, client, Point, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, clientTransport.Count("cli_teleport"));
        Assert.Equal(0, clientTransport.Count("cli_teleport_trace_wait"));
    }

    // With loadedGround the landing is judged at the loaded ground measured after the floor is ready, 1.05 m below the
    // target here (a location's levelling); without it the same landing fails, so the measured ground is what passes it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoadedGroundJudgesTheLandingAtTheMeasuredHeight(bool loadedGround)
    {
        const float loaded = 41.45f;
        var (serverTransport, clientTransport) = SignalTransports(landed: () => Standing(y: loaded));
        clientTransport.Extension("valheim.world", "terrain", _ => new { source = "loaded-ground", complete = true, units = "metres", x = 100f, z = -40f, height = loaded });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        // The support wait's height argument: cli_extension valheim.world/player-support-wait <x> <height> <z> <seconds>.
        IEnumerable<string> supportHeights() => clientTransport.Commands
            .Where(c => c.StartsWith("cli_extension valheim.world/player-support-wait ", StringComparison.Ordinal)).Select(c => c.Split(' ')[3]);
        if (!loadedGround)
        {
            Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30)));
            Assert.Equal(new[] { "42.5" }, supportHeights());
            Assert.Equal(0, clientTransport.Count("cli_extension valheim.world/terrain"));
            return;
        }
        var result = PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30), loadedGround: true);
        Assert.Equal(new[] { "41.45" }, supportHeights());
        Assert.Equal(new HeightExpectation(100, -40, loaded), result.Target);
        Assert.Contains("cli_teleport_peer 1 100 43 -40", serverTransport.Commands); // the target is still the requested point
        var commands = clientTransport.Commands.ToList();
        Assert.True(commands.FindIndex(c => c.StartsWith("cli_teleport_trace_wait ", StringComparison.Ordinal)) < commands.FindIndex(c => c.StartsWith("cli_extension valheim.world/terrain ", StringComparison.Ordinal)));
    }

    [Fact] public void LoadedGroundNeedsTheTerrainReadingBeforeAnythingIsSent()
    {
        var (serverTransport, clientTransport) = SignalTransports();
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Contains("valheim.world/terrain", Assert.Throws<InvalidOperationException>(() =>
            PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30), loadedGround: true)).Message);
        Assert.All(clientTransport.Commands, c => Assert.True(c.StartsWith("cli_expect", StringComparison.Ordinal) || c == "cli_extensions", c));
        Assert.Equal(0, serverTransport.Count("cli_teleport_peer"));
    }

    [Fact] public void PeerCountReadsOneConsistentListing()
    {
        using var two = new ScriptedTransport().On("cli_peers", _ => ScriptedTransport.Ok("OK: 2 peer(s)",
            "PEER 1 character position=0.0,40.00,0.0 zone=0,0", "PEER 2 reference position=0.0,0.00,0.0 zone=0,0")).Actor();
        Assert.Equal(2, PlayerPlacement.PeerCount(two));
        using var none = new ScriptedTransport().On("cli_peers", _ => ScriptedTransport.Ok("OK: 0 peer(s)")).Actor();
        Assert.Equal(0, PlayerPlacement.PeerCount(none));
        // A count line that disagrees with the listed peers is refused, by PeerCount and OnlyPeer alike.
        using var torn = new ScriptedTransport().On("cli_peers", _ => ScriptedTransport.Ok("OK: 2 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0")).Actor();
        Assert.Contains("inconsistent", Assert.Throws<InvalidOperationException>(() => PlayerPlacement.PeerCount(torn)).Message);
        Assert.Contains("inconsistent", Assert.Throws<InvalidOperationException>(() => PlayerPlacement.OnlyPeer(torn)).Message);
        using var silent = new ScriptedTransport().On("cli_peers", _ => ScriptedTransport.Ok()).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.PeerCount(silent));
    }

    [Fact] public void ProtectionMustBeReadBack()
    {
        using var ok = new ScriptedTransport().On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True ghostReplicated=True")).Actor();
        PlayerPlacement.Protect(ok);
        using var refused = new ScriptedTransport().On("cli_set_player_safety true", _ => ScriptedTransport.Ok("ERROR: code=safety_not_applied playerSafety enabled=True god=True ghost=False debugMode=True cheats=True")).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Protect(refused));
    }

    // #261: ghost mode only the player's own game knows about is no protection from a creature another peer simulates. A
    // ValheimCLI that does not replicate it reads back an ordinary local OK line, and that is refused, naming why.
    [Fact] public void ProtectionThatHoldsOnlyLocallyIsRefused()
    {
        using var local = new ScriptedTransport().On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True")).Actor();
        Assert.Contains("ghostReplicated=True", Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Protect(local)).Message);
        using var unreplicated = new ScriptedTransport().On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True ghostReplicated=False")).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Protect(unreplicated));
    }

    // The deliberate opt-out (ClientRunPlan.Targetable): god and debug modes, ghost off, and the reply must say so.
    [Fact] public void TargetableProtectionLeavesGhostModeOffAndIsReadBack()
    {
        var transport = new ScriptedTransport().On("cli_set_player_safety true targetable", _ =>
            ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=False debugMode=True cheats=True ghostReplicated=True targetable=True"));
        using (var client = transport.Actor()) PlayerPlacement.Protect(client, targetable: true);
        Assert.Equal(["cli_set_player_safety true targetable"], transport.Commands.Where(c => c.StartsWith("cli_set_player_safety", StringComparison.Ordinal)));
        // A ValheimCLI without the mode answers with its usage, and a ghost reply is not targetable.
        using var old = new ScriptedTransport().On("cli_set_player_safety true targetable", _ => ScriptedTransport.Ok("Usage: cli_set_player_safety <true|false>")).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Protect(old, targetable: true));
        using var ghost = new ScriptedTransport().On("cli_set_player_safety true targetable", _ =>
            ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True ghostReplicated=True")).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Protect(ghost, targetable: true));
    }

    [Theory]
    [InlineData(new[] { "PEER 1 reference position=0.0,0.00,0.0 zone=0,0", "PEER 2 character position=5.0,40.00,5.0 zone=0,0" }, 2)]
    public void TheOnlyPlayerWithACharacterIsChosen(string[] peers, int expected)
    {
        using var server = new ScriptedTransport().On("cli_peers", _ => ScriptedTransport.Ok(["OK: 2 peer(s)", .. peers])).Actor();
        Assert.Equal(expected, PlayerPlacement.OnlyPeer(server));
    }
    [Fact] public void NoPlayerOrSeveralPlayersAreRefused()
    {
        using var none = new ScriptedTransport().On("cli_peers", _ => ScriptedTransport.Ok("OK: 0 peer(s)")).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.OnlyPeer(none));
        using var two = new ScriptedTransport().On("cli_peers", _ => ScriptedTransport.Ok("OK: 2 peer(s)",
            "PEER 1 character position=0.0,40.00,0.0 zone=0,0", "PEER 2 character position=9.0,40.00,9.0 zone=0,0")).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.OnlyPeer(two));
    }

    // Standing in water 12.5 m below the declared dry ground after the teleport: the client's wait ends unsupported.
    [Fact] public void AnUnsupportedLandingIsNotArrivalAndTheTeleportIsNotRepeated()
    {
        var (serverTransport, clientTransport) = SignalTransports(landed: () => Standing(y: 30f, grounded: false));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var error = Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30)));
        Assert.Contains("\"y\":30", error.Message);
        Assert.Contains("not repeated", error.Message);
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }
    // The game decides when a teleport would be taken (spawn and teleport cooldown, attached, dead, riding the intro); a
    // player that never gets there is not teleported.
    [Fact] public void APlayerThatNeverBecomesTeleportableIsNotTeleported()
    {
        var (serverTransport, clientTransport) = SignalTransports(teleportable: _ => ScriptedTransport.Ok("ERROR: code=teleportable_timeout attached=True ms=1000"));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Contains("teleportable_timeout", Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(5))).Message);
        Assert.Equal(0, serverTransport.Count("cli_teleport_peer"));
        Assert.Equal(0, clientTransport.Count("cli_teleport_trace_arm"));
    }
    // Without the intro skip, the Valkyrie ride may still be running, so the readiness wait also requires the player grounded.
    [Fact] public void ArrivalCanLeaveTheIntroAloneAndThenRequiresTheGround()
    {
        var (serverTransport, clientTransport) = SignalTransports();
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(5), skipIntro: false);
        Assert.Equal(0, clientTransport.Count("cli_skip_intro"));
        Assert.Contains(clientTransport.Commands, c => c.StartsWith("cli_wait_teleportable ", StringComparison.Ordinal) && c.EndsWith(" 0 True", StringComparison.Ordinal));
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }
    // An older Standard pack: refused by name before the intro skip or anything else is sent.
    [Fact] public void AClientWithoutTheTeleportSignalsIsRefusedBeforeAnythingIsSent()
    {
        var server = new ScriptedTransport();
        var client = new ScriptedTransport()
            .Extension("valheim.world", "player-support", _ => Standing())
            .Extension("valheim.world", "player-support-wait", _ => Standing());
        using var serverActor = server.Actor(); using var clientActor = client.Actor();
        Assert.Contains(CliCapabilities.TeleportSignals, Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(serverActor, clientActor, Point, TimeSpan.FromSeconds(30))).Message);
        Assert.All(client.Commands, c => Assert.True(c == "cli_extensions" || c.StartsWith("cli_expect", StringComparison.Ordinal), c));
        Assert.All(server.Commands, c => Assert.StartsWith("cli_expect", c));
    }
    [Fact] public void ARefusedTeleportIsNotArrival()
    {
        var (serverTransport, clientTransport) = SignalTransports(teleport: _ => ScriptedTransport.Ok("ERROR: peer 1 has no character yet"));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, clientTransport.Count("cli_teleport_trace_wait"));
    }

    [Theory]
    [InlineData("OK: skipped=True profileFirstSpawn=True position=0.6,33.7,2.8 ms=2100", true)]
    [InlineData("OK: skipped=False profileFirstSpawn=False position=0.6,33.7,2.8 ms=3", false)]
    public void SkippingTheIntroReportsWhetherOneWasRunning(string reply, bool expected)
    {
        var transport = new ScriptedTransport().OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok(reply));
        using var client = transport.Actor();
        Assert.Equal(expected, PlayerPlacement.SkipIntro(client, TimeSpan.FromSeconds(30)));
        Assert.Contains("cli_skip_intro 30", transport.Commands);
    }
    [Theory]
    [InlineData("ERROR: code=skip_intro_timeout pending=respawn skipped=True ms=30000")]
    [InlineData("ERROR: code=no_world message=no game or player profile; join or start a world first")]
    [InlineData("OK: queued")]
    public void AnUnconfirmedIntroSkipFails(string reply)
    {
        using var client = new ScriptedTransport().OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok(reply)).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.SkipIntro(client, TimeSpan.FromSeconds(30)));
    }
    [Fact] public void ArrivalSkipsTheIntroBeforeAnythingElseAndRefusesToTeleportIfItFails()
    {
        var (serverTransport, clientTransport) = SignalTransports(intro: _ => ScriptedTransport.Ok("ERROR: code=skip_intro_timeout pending=respawn skipped=True ms=30000"));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, serverTransport.Count("cli_teleport_peer"));
        Assert.Equal(new[] { "cli_skip_intro 5" }, clientTransport.Commands.Where(c => c != "cli_extensions" && !c.StartsWith("cli_expect", StringComparison.Ordinal)));
    }

    [Fact] public void EverySupportReadingMustShowThePlayerSettled()
    {
        int reads = 0;
        using var client = NoIntro().Extension("valheim.world", "player-support", _ => ++reads == 2 ? Standing(speed: 2) : Standing()).Actor();
        var error = Assert.Throws<SupportException>(() => PlayerPlacement.RequireSupported(client, Point, interval: TimeSpan.Zero));
        Assert.Equal(2, error.Readings.Count);
        reads = 10;
        Assert.Equal(3, PlayerPlacement.RequireSupported(client, Point, interval: TimeSpan.Zero).Count);
    }

    // #270: the pause between readings was an uncancellable Thread.Sleep. A minute's spacing ends with the token instead.
    [Fact] public void CancellationEndsThePauseBetweenSupportReadings()
    {
        using var cancel = new CancellationTokenSource();
        int reads = 0;
        using var client = NoIntro().Extension("valheim.world", "player-support", _ => { if (++reads == 1) cancel.CancelAfter(20); return Standing(); }).Actor();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => PlayerPlacement.RequireSupported(client, Point, interval: TimeSpan.FromMinutes(1), cancellation: cancel.Token));
        Assert.Equal(1, reads); Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
    }

    // The flying reading is the settled standing one with only "flying" changed.
    [Fact] public void SupportIsRefusedWhileFlyIsOnNotReportedAsUnsupported()
    {
        int reads = 0;
        using var client = NoIntro().Extension("valheim.world", "player-support", _ => ++reads == 2 ? Standing(flying: true) : Standing()).Actor();
        // Exactly InvalidOperationException: a SupportException would claim the ground was measured and failed.
        var error = Assert.Throws<InvalidOperationException>(() => PlayerPlacement.RequireSupported(client, Point, interval: TimeSpan.Zero));
        Assert.Contains("flying", error.Message);
        Assert.Equal(2, reads); // Refused at the flying reading; nothing more was measured.
        reads = 10;
        Assert.Equal(3, PlayerPlacement.RequireSupported(client, Point, interval: TimeSpan.Zero).Count); // Control: the same readings without fly pass.
    }
    [Fact] public void ArrivalRefusesAFlyingPlayerAndNeverTeleports()
    {
        var (serverTransport, clientTransport) = SignalTransports(before: () => Standing(flying: true));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30)));
        Assert.Contains("flying", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "refused at once, not after the arrival timeout");
        Assert.Equal(0, serverTransport.Count("cli_teleport_peer"));
        Assert.Equal(0, clientTransport.Count("cli_wait_teleportable"));
    }
    [Fact] public void ArrivalRefusesAPlayerWhoStartsFlyingAfterTheTeleport()
    {
        var (serverTransport, clientTransport) = SignalTransports(landed: () => Standing(flying: true));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Contains("flying", Assert.Throws<InvalidOperationException>(() =>
            PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30))).Message);
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }

    [Theory]
    [InlineData(true, "cli_fly on", "OK: fly=True changed=True")]
    [InlineData(true, "cli_fly on", "OK: fly=True changed=False")]
    [InlineData(false, "cli_fly off", "OK: fly=False changed=True")]
    public void FlyIsSetAndReadBack(bool on, string command, string reply)
    {
        var transport = new ScriptedTransport().On(command, _ => ScriptedTransport.Ok(reply));
        using var client = transport.Actor();
        PlayerPlacement.SetFly(client, on);
        Assert.Equal(1, transport.Count("cli_fly"));
    }
    [Theory]
    [InlineData(true, "cli_fly on", "OK: fly=False changed=False")]
    [InlineData(false, "cli_fly off", "OK: fly=True changed=False")]
    [InlineData(true, "cli_fly on", "ERROR: No local player found")]
    [InlineData(true, "cli_fly on", "Usage: cli_fly [on|off|toggle]")]
    public void AnUnconfirmedFlyChangeFails(bool on, string command, string reply)
    {
        using var client = new ScriptedTransport().On(command, _ => ScriptedTransport.Ok(reply)).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.SetFly(client, on));
    }
}
