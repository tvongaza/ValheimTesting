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

    // A server that moves its one peer once, and a client that answers every arrival phase; the trace, the readiness wait
    // and the readings before and after the teleport are the parts a test varies.
    private static (ScriptedTransport Server, ScriptedTransport Client) SignalTransports(string trace = TraceOk,
        Func<string, CommandResult>? teleportable = null, Func<object>? before = null, Func<object>? landed = null, Func<string, CommandResult>? teleport = null,
        Func<string, CommandResult>? intro = null)
    {
        var server = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", Peers))
            .OnPrefix("cli_teleport_peer ", teleport ?? (_ => ScriptedTransport.Ok("OK: asked peer 1 to teleport")));
        var client = new ScriptedTransport()
            .OnPrefix("cli_skip_intro", intro ?? (_ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0.6,33.7,2.8 ms=4")))
            .OnPrefix("cli_wait_teleportable ", teleportable ?? (_ => ScriptedTransport.Ok("OK: TELEPORTABLE ms=500 stillMs=0 cooldownSeconds=2.00 grounded=True position=0,40,0")))
            .On("cli_teleport_trace_arm", _ => ScriptedTransport.Ok("OK: TELEPORT_TRACE_ARM id=7"))
            .OnPrefix("cli_teleport_trace_wait ", _ => ScriptedTransport.Ok(trace))
            .Extension("valheim.session", "teleport-signals", _ => new { source = "teleport-signals", complete = true })
            .Extension("valheim.world", "player-support", _ => (before ?? (() => Standing(y: 40f)))())
            .Extension("valheim.world", "player-support-wait", _ => (landed ?? (() => Standing()))());
        return (server, client);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(180)]
    public void ArrivalMakesOneWaitPerPhaseAndOneTeleport(int seconds)
    {
        var (serverTransport, clientTransport) = SignalTransports();
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var before = client.CommandTimeout;
        var result = PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(seconds));
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
        if (seconds > 120)
            Assert.Contains(clientTransport.Commands, command => command.StartsWith("cli_wait_teleportable 120 ", StringComparison.Ordinal));
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

    [Fact] public void ProtectionMustBeReadBack()
    {
        using var ok = new ScriptedTransport().On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True")).Actor();
        PlayerPlacement.Protect(ok);
        using var refused = new ScriptedTransport().On("cli_set_player_safety true", _ => ScriptedTransport.Ok("ERROR: code=safety_not_applied playerSafety enabled=True god=True ghost=False debugMode=True cheats=True")).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Protect(refused));
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
