using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public class PlayerPlacementTests
{
    private static readonly HeightExpectation Point = new(100, -40, 42.5f);
    private static object Standing(float y = 42.5f, bool grounded = true, float speed = 0) =>
        new { source = "local-player-support", complete = true, x = 100f, y, z = -40f, speed, grounded, flying = false, attached = false, dead = false, teleporting = false, units = "metres" };
    private static object Loading() => new { source = "local-player-support", complete = false };
    private static ScriptedTransport NoIntro() =>
        new ScriptedTransport().OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0.6,33.7,2.8 ms=4"));

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

    private int teleportedAfterReads = -1;
    [Fact] public void ArrivalTeleportsOnceAndWaitsForTheClientToStandThere()
    {
        int reads = 0;
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => { teleportedAfterReads = reads; return ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"); });
        // Before the teleport: still loading, then riding the first-join Valkyrie, then standing at the spawn. After it:
        // falling onto the point, then settled there. Only the last is arrival.
        var clientTransport = NoIntro().Extension("valheim.world", "player-support", _ => ++reads switch
        {
            1 => Loading(),
            2 => new { source = "local-player-support", complete = true, x = 0f, y = 80f, z = 0f, speed = 12f, grounded = false, flying = false, attached = true, dead = false, teleporting = false, units = "metres" },
            3 => new { source = "local-player-support", complete = true, x = 0.6f, y = 33.7f, z = 2.8f, speed = 0f, grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres" },
            4 => Standing(y: 44f, grounded: false, speed: 3),
            _ => Standing(),
        });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var arrived = PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30), settleFor: TimeSpan.Zero);
        Assert.True(arrived.GetProperty("grounded").GetBoolean());
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
        Assert.Contains("cli_teleport_peer 1 100 43 -40", serverTransport.Commands);
        Assert.Equal(5, reads);
        // The teleport went out only after the player stood still at the spawn (reading 3).
        Assert.Equal(3, teleportedAfterReads);
        // The intro was skipped before the first player reading.
        var commands = clientTransport.Commands.ToList();
        int skip = commands.FindIndex(c => c.StartsWith("cli_skip_intro ", StringComparison.Ordinal));
        Assert.InRange(skip, 0, commands.FindIndex(c => c.StartsWith("cli_extension valheim.world/player-support", StringComparison.Ordinal)) - 1);
    }
    [Fact] public void ArrivalThatNeverSettlesTimesOutWithoutASecondTeleport()
    {
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"));
        // Settled at the spawn, so the teleport goes out; then standing in water, 12.5 m below the declared dry ground.
        int reads = 0;
        var clientTransport = NoIntro().Extension("valheim.world", "player-support", _ => ++reads == 1
            ? new { source = "local-player-support", complete = true, x = 0.6f, y = 33.7f, z = 2.8f, speed = 0f, grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres" }
            : Standing(y: 30f, grounded: false));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var error = Assert.Throws<TimeoutException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(1), settleFor: TimeSpan.Zero));
        Assert.Contains("\"y\":30", error.Message);
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }
    // 29 Sep native run: the first reading after the join already showed the player standing at the spawn, the teleport
    // went out within the game's 2 s post-spawn cooldown and was dropped. Standing still must last before the teleport.
    [Fact] public void ThePlayerMustStandStillForAWhileBeforeTheTeleport()
    {
        // Timed, not counted: polls can take longer than their 250 ms on a loaded machine, but the teleport can never
        // come sooner than settleFor after the first still reading.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? firstStill = null, teleported = null;
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => { teleported = clock.Elapsed; return ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"); });
        var clientTransport = NoIntro().Extension("valheim.world", "player-support", _ =>
        {
            if (teleported != null) return Standing();
            firstStill ??= clock.Elapsed;
            return new { source = "local-player-support", complete = true, x = 0.6f, y = 33.7f, z = 2.8f, speed = 0f, grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres" };
        });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30), settleFor: TimeSpan.FromSeconds(1));
        Assert.True(teleported - firstStill >= TimeSpan.FromSeconds(1), $"teleported {(teleported - firstStill)?.TotalMilliseconds:F0} ms after the player first stood still");
    }
    // 29 Sep native run: two copies of one world pinned the same world UID, and the character had logged out on the other
    // copy's raised road, which this copy does not have. It spawned swimming (grounded false, y 28, bobbing at 6 cm/s),
    // never "stood still" on the ground, and the arrival timed out before the teleport it needed.
    [Fact] public void APlayerSwimmingAtTheSpawnIsTeleportedOnce()
    {
        bool teleported = false;
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=280.0,28.07,-384.0 zone=4,-6"))
            .OnPrefix("cli_teleport_peer ", _ => { teleported = true; return ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"); });
        var clientTransport = NoIntro().Extension("valheim.world", "player-support", _ => teleported
            ? Standing()
            : new { source = "local-player-support", complete = true, x = 280f, y = 28.07f, z = -384f, speed = .06f, grounded = false, flying = false, attached = false, dead = false, teleporting = false, units = "metres" });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var arrived = PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(10), settleFor: TimeSpan.Zero);
        Assert.True(arrived.GetProperty("grounded").GetBoolean());
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }
    // Without the intro skip, the Valkyrie ride may still be running: it is not reported as attached and its speed can read
    // low, so a player that is not grounded is not teleported (the swimming case above needs the skip).
    [Fact] public void WithoutTheIntroSkipAPlayerThatIsNotGroundedIsNotTeleported()
    {
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,80.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"));
        var clientTransport = new ScriptedTransport().Extension("valheim.world", "player-support", _ =>
            new { source = "local-player-support", complete = true, x = 0f, y = 80f, z = 0f, speed = .1f, grounded = false, flying = false, attached = false, dead = false, teleporting = false, units = "metres" });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Contains("Nothing was teleported", Assert.Throws<TimeoutException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(1), settleFor: TimeSpan.Zero, skipIntro: false)).Message);
        Assert.Equal(0, serverTransport.Count("cli_teleport_peer"));
        Assert.Equal(0, clientTransport.Count("cli_skip_intro"));
    }
    [Fact] public void AFallingPlayerIsNotTeleported()
    {
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"));
        var clientTransport = NoIntro().Extension("valheim.world", "player-support", _ => Standing(y: 60f, grounded: false, speed: 3));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Contains("Nothing was teleported", Assert.Throws<TimeoutException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(1), settleFor: TimeSpan.Zero)).Message);
        Assert.Equal(0, serverTransport.Count("cli_teleport_peer"));
    }
    [Fact] public void APlayerThatNeverStandsStillIsNotTeleported()
    {
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"));
        var clientTransport = NoIntro().Extension("valheim.world", "player-support", _ =>
            new { source = "local-player-support", complete = true, x = 0f, y = 80f, z = 0f, speed = 12f, grounded = false, flying = false, attached = true, dead = false, teleporting = false, units = "metres" });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Contains("Nothing was teleported", Assert.Throws<TimeoutException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(1), settleFor: TimeSpan.Zero)).Message);
        Assert.Equal(0, serverTransport.Count("cli_teleport_peer"));
    }
    [Fact] public void ARefusedTeleportIsNotArrival()
    {
        using var server = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("ERROR: peer 1 has no character yet")).Actor();
        using var client = NoIntro().Extension("valheim.world", "player-support", _ => Standing()).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(5), settleFor: TimeSpan.Zero));
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
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"));
        var clientTransport = new ScriptedTransport()
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("ERROR: code=skip_intro_timeout pending=respawn skipped=True ms=30000"))
            .Extension("valheim.world", "player-support", _ => Standing());
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(5), settleFor: TimeSpan.Zero));
        Assert.Equal(0, serverTransport.Count("cli_teleport_peer"));
        Assert.DoesNotContain(clientTransport.Commands, c => c.StartsWith("cli_extension valheim.world/player-support", StringComparison.Ordinal));
    }
    [Fact] public void ArrivalCanLeaveTheIntroAlone()
    {
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"));
        // Unscripted, so a skip would throw.
        var clientTransport = new ScriptedTransport().Extension("valheim.world", "player-support", _ => Standing());
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(5), settleFor: TimeSpan.Zero, skipIntro: false);
        Assert.Equal(0, clientTransport.Count("cli_skip_intro"));
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
}
