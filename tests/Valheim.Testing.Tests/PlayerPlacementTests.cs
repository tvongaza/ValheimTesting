using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public class PlayerPlacementTests
{
    private static readonly HeightExpectation Point = new(100, -40, 42.5f);
    private static object Standing(float y = 42.5f, bool grounded = true, float speed = 0) =>
        new { source = "local-player-support", complete = true, x = 100f, y, z = -40f, speed, grounded, flying = false, attached = false, dead = false, teleporting = false, units = "metres" };
    private static object Loading() => new { source = "local-player-support", complete = false };

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

    [Fact] public void ArrivalTeleportsOnceAndWaitsForTheClientToStandThere()
    {
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"));
        int reads = 0;
        // Loading, then falling, then settled: only the settled reading is arrival.
        var clientTransport = new ScriptedTransport().Extension("valheim.world", "player-support",
            _ => ++reads switch { 1 => Loading(), 2 => Standing(y: 44f, grounded: false, speed: 3), _ => Standing() });
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var arrived = PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(30));
        Assert.True(arrived.GetProperty("grounded").GetBoolean());
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
        Assert.Contains("cli_teleport_peer 1 100 43 -40", serverTransport.Commands);
        Assert.Equal(3, reads);
    }
    [Fact] public void ArrivalThatNeverSettlesTimesOutWithoutASecondTeleport()
    {
        var serverTransport = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"));
        // Standing in water: 12.5 m below the declared dry ground.
        var clientTransport = new ScriptedTransport().Extension("valheim.world", "player-support", _ => Standing(y: 30f, grounded: false));
        using var server = serverTransport.Actor(); using var client = clientTransport.Actor();
        var error = Assert.Throws<TimeoutException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(1)));
        Assert.Contains("\"y\":30", error.Message);
        Assert.Equal(1, serverTransport.Count("cli_teleport_peer"));
    }
    [Fact] public void ARefusedTeleportIsNotArrival()
    {
        using var server = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("ERROR: peer 1 has no character yet")).Actor();
        using var client = new ScriptedTransport().Extension("valheim.world", "player-support", _ => Standing()).Actor();
        Assert.Throws<InvalidOperationException>(() => PlayerPlacement.Arrive(server, client, Point, TimeSpan.FromSeconds(5)));
    }

    [Fact] public void EverySupportReadingMustShowThePlayerSettled()
    {
        int reads = 0;
        using var client = new ScriptedTransport().Extension("valheim.world", "player-support", _ => ++reads == 2 ? Standing(speed: 2) : Standing()).Actor();
        var error = Assert.Throws<SupportException>(() => PlayerPlacement.RequireSupported(client, Point, interval: TimeSpan.Zero));
        Assert.Equal(2, error.Readings.Count);
        reads = 10;
        Assert.Equal(3, PlayerPlacement.RequireSupported(client, Point, interval: TimeSpan.Zero).Count);
    }
}
