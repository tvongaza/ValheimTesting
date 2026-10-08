using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// ZoneCycle against a scripted server and a scripted client whose zones load and unload as the game decides it from the
// player's zone and the client's simulation distance, a few readings late. A scripted mod object keeps one value in its
// saved data and one only in a component field, which the unload clears: the re-observation after the cycle must see the
// first and lose the second. No game.
public sealed class ZoneCycleTests : IDisposable
{
    // Zone (2,-1). The zones of interest round it: (1,-1) (2,-1) (1,0) (2,0).
    private static readonly HeightExpectation Home = new(100, -40, 42.5f);
    private static readonly IReadOnlyList<ZoneId> Site = ZoneId.Around(100, -40, 10);
    // Zone (7,-1): five rings from the site's nearest zones, the least that unloads them at near 2, far 2.
    private static readonly HeightExpectation FarAway = new(420, -40, 30f);
    private readonly string _output = Directory.CreateTempSubdirectory("zone-cycle-").FullName;
    public void Dispose() => Directory.Delete(_output, recursive: true);

    private sealed class World
    {
        public float X = Home.X, Z = Home.Z, Y = Home.Height;
        public int Near = 2, Far = 2; public bool Classic = true;
        /// <summary>Zone readings after a teleport that still show the zones as they were before it.</summary>
        public int Lag = 2;
        /// <summary>A zone that keeps one instance wherever the player goes (something else holds it).</summary>
        public ZoneId? Stuck;
        public string SavedMarker = "saved-value", FieldMarker = "field-value";
        public readonly List<string> Teleports = [];
        private int _sinceTeleport = int.MaxValue;
        private ZoneId _shown = ZoneId.Of(Home.X, Home.Z);
        public int ZoneReads;
        public void StartAt(HeightExpectation point) { X = point.X; Z = point.Z; Y = point.Height; _shown = ZoneId.Of(X, Z); }

        public ScriptedTransport Server() => new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", command =>
            {
                string[] w = command.Split(' ');
                X = float.Parse(w[2], CultureInfo.InvariantCulture); Y = float.Parse(w[3], CultureInfo.InvariantCulture) - .5f; Z = float.Parse(w[4], CultureInfo.InvariantCulture);
                Teleports.Add($"{X},{Z}"); _sinceTeleport = 0;
                return ScriptedTransport.Ok("OK: asked peer 1 to teleport");
            });

        public ScriptedTransport Client() => new ScriptedTransport()
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0,40,0 ms=3"))
            .Extension("valheim.world", "player-support", _ => Support())
            .ArrivalSignals(Support)
            .Extension("mymod.testing", "zones", Zones)
            .Extension("mymod.testing", "marker", _ => new { source = "marker", complete = true, saved = SavedMarker, field = FieldMarker });

        private object Support() => new
        {
            source = "local-player-support", complete = true, x = X, y = Y, z = Z, speed = 0f,
            grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
        };

        private object Zones(IReadOnlyList<string> arguments)
        {
            ZoneReads++;
            if (_sinceTeleport != int.MaxValue && ++_sinceTeleport > Lag) _shown = ZoneId.Of(X, Z);
            var range = new SimulationRange(Near, Far, Classic);
            var zones = arguments.Select(a => a.Split(',')).Select(p => new ZoneId(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture))).Select(zone =>
            {
                bool loaded = range.KeepsLoaded(_shown, zone);
                int instances = zone.Rings(_shown) <= range.Total ? 3 : zone == Stuck ? 1 : 0;
                // The game recreates a zone's objects from its saved data: a component field starts empty.
                if (instances == 0 && zone == ZoneId.Of(Home.X, Home.Z)) FieldMarker = "";
                return new { x = zone.X, z = zone.Z, terrainLoaded = loaded, instances, nearInstances = loaded ? 2 : 0, saved = 3, withoutInstance = loaded ? 0 : 3 };
            }).ToArray();
            // The client's reference position follows its player at once; only the zones lag behind.
            var reference = ZoneId.Of(X, Z);
            return new { source = "zone-presence", complete = true, reference = new { x = reference.X, z = reference.Z }, simulation = new { near = Near, far = Far, classic = Classic }, zones };
        }
    }

    private static ZoneCycle Cycle(HeightExpectation? away = null, TimeSpan? timeout = null) => new()
    {
        Capability = "mymod.testing/zones", Zones = Site, Away = away ?? FarAway, Back = Home,
        StepTimeout = timeout ?? TimeSpan.FromSeconds(10), Interval = TimeSpan.FromMilliseconds(10),
    };

    private static JsonElement Marker(GameActor client) => client.ObserveComplete(client.RequireCapability("mymod.testing/marker"), "marker").Data;

    [Fact] public void TheCycleLeavesWaitsForTheUnloadReturnsAndWaitsForTheReload()
    {
        var world = new World();
        using var server = world.Server().Actor("server"); using var client = world.Client().Actor("client");
        var before = Marker(client);

        var result = Cycle().Run(server, client);

        Assert.Equal(new[] { "420,-40", "100,-40" }, world.Teleports);
        Assert.All(result.Before.Zones, z => Assert.True(z.Loaded));
        Assert.All(result.Unloaded.Zones, z => Assert.True(z.Unloaded, z.ToString()));
        Assert.Equal(new ZoneId(7, -1), result.Unloaded.Reference);
        Assert.All(result.Reloaded.Zones, z => Assert.True(z.Loaded, z.ToString()));
        Assert.Equal(new ZoneId(2, -1), result.Reloaded.Reference);
        Assert.True(world.ZoneReads > 2 * world.Lag, "the waits read the zones until they changed");
        // The caller's re-observation: saved state survives the unload, a component field does not (the negative control).
        var after = Marker(client);
        Assert.Equal(before.GetProperty("saved").GetString(), after.GetProperty("saved").GetString());
        Assert.NotEqual(before.GetProperty("field").GetString(), after.GetProperty("field").GetString());
    }

    [Theory]
    [InlineData(1, 2, true, 4)]
    [InlineData(2, 2, true, 5)]
    [InlineData(2, 2, false, 5)]
    [InlineData(5, 2, false, 8)]
    public void TheDistanceComesFromTheClientsSimulationDistance(int near, int far, bool classic, int rings) =>
        Assert.Equal(rings, ZoneCycle.RingsToLeave(new SimulationRange(near, far, classic)));

    // Four rings is inside near 2 + far 2: the distant objects would stay, nothing would be proven. Refused before any teleport,
    // so the component field is never cleared and a re-observation would have passed wrongly.
    [Fact] public void AnAwayPointInsideTheActiveAreaIsRefusedBeforeAnythingMoves()
    {
        var world = new World();
        using var server = world.Server().Actor("server"); using var client = world.Client().Actor("client");
        var error = Assert.Throws<InvalidOperationException>(() => Cycle(away: new HeightExpectation(356, -40, 30f)).Run(server, client));
        Assert.Contains("zone 6,-1, 4 ring(s)", error.Message);
        Assert.Contains("5 rings (320 m", error.Message);
        Assert.Empty(world.Teleports);
        Assert.Equal("field-value", world.FieldMarker);
    }

    [Fact] public void ALargerSimulationDistanceNeedsAFartherAwayPoint()
    {
        var world = new World { Near = 5, Classic = false };
        using var server = world.Server().Actor("server"); using var client = world.Client().Actor("client");
        var error = Assert.Throws<InvalidOperationException>(() => Cycle().Run(server, client));
        Assert.Contains("(near 5, far 2)", error.Message);
        Assert.Contains("8 rings (512 m", error.Message);
        Assert.Empty(world.Teleports);
    }

    [Fact] public void ZonesThatNeverUnloadTimeOutNamingThemWithoutReturning()
    {
        var world = new World { Stuck = new ZoneId(2, 0) };
        using var server = world.Server().Actor("server"); using var client = world.Client().Actor("client");
        var error = Assert.Throws<WaitTimeoutException>(() => Cycle(timeout: TimeSpan.FromSeconds(1)).Run(server, client));
        Assert.Contains("the client to unload zone(s)", error.Message);
        Assert.Contains("still loaded: zone 2,0: terrain not loaded, 1 instance(s)", error.Message);
        Assert.DoesNotContain("zone 1,-1:", error.Message);
        Assert.Equal(new[] { "420,-40" }, world.Teleports); // Never repeated, never returned.
    }

    [Fact] public void ZonesNotLoadedBeforeTheCycleAreRefused()
    {
        var world = new World();
        world.StartAt(FarAway); // The player already stands away: the site is not loaded.
        using var server = world.Server().Actor("server"); using var client = world.Client().Actor("client");
        var error = Assert.Throws<InvalidOperationException>(() => Cycle().Run(server, client));
        Assert.Contains("not loaded before leaving", error.Message);
        Assert.Empty(world.Teleports);
    }

    [Fact] public void InARoundEachPartIsAStepAndTheReadingsAreEvidence()
    {
        var world = new World();
        using var server = world.Server().Actor("server"); using var client = world.Client().Actor("client");
        var report = new ScenarioReport("zones");
        var round = new ClientRound("first", 0, true, server, client, report, _output, "1");
        Cycle().Run(round);
        Assert.Equal(new[]
        {
            "first: the zones of interest are loaded before leaving", "first: leave the area for (420, -40)", "first: the client unloads the zones",
            "first: return to (100, -40)", "first: the client loads the zones again",
        }, report.Steps.Select(s => s.Name));
        Assert.True(report.Passed);
        var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "first-zone-cycle.json"))).RootElement;
        Assert.Equal(4, evidence.GetProperty("unloaded").GetProperty("zones").GetArrayLength());
        Assert.True(evidence.GetProperty("reloadSeconds").GetDouble() >= 0);
        report.Write(_output);
        var link = Assert.Single(report.Evidence);
        Assert.Equal(new EvidenceReference("zone-cycle", "first", "1", "first-zone-cycle.json", FileHash.Sha256(Path.Combine(_output, "first-zone-cycle.json"))), link);
    }

    [Fact] public void ABadEvidenceNameIsRefusedBeforeTheClientMoves()
    {
        var world = new World();
        using var server = world.Server().Actor("server"); using var client = world.Client().Actor("client");
        var round = new ClientRound("first", 0, true, server, client, new ScenarioReport("zones"), _output, "1");
        Assert.Throws<ArgumentException>(() => Cycle().Run(round, evidence: "zone_cycle"));
        Assert.Empty(world.Teleports);
    }

    [Fact] public void RoundEvidenceIsNamedAsAnEvidenceKind()
    {
        using var server = new World().Server().Actor("server");
        var round = new ClientRound("first", 0, true, server, server, new ScenarioReport("names"), _output, "1");
        Assert.Throws<ArgumentException>(() => round.Write("Zone Cycle", new { }));
        Assert.False(File.Exists(Path.Combine(_output, "first-Zone Cycle.json")));
    }

    [Fact] public void AFailedCycleInARoundStillWritesWhatItSaw()
    {
        var world = new World { Stuck = new ZoneId(1, -1) };
        using var server = world.Server().Actor("server"); using var client = world.Client().Actor("client");
        var report = new ScenarioReport("zones");
        var round = new ClientRound("first", 0, true, server, client, report, _output, "1");
        // Leave enough time for arrival's 250 ms supported hold; this case tests the later, stuck zone unload.
        Assert.Throws<WaitTimeoutException>(() => Cycle(timeout: TimeSpan.FromSeconds(1)).Run(round));
        Assert.Equal(new[] { "first: the client unloads the zones" }, report.Steps.Where(s => !s.Passed).Select(s => s.Name));
        var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "first-zone-cycle.json"))).RootElement;
        Assert.Equal(JsonValueKind.Object, evidence.GetProperty("before").ValueKind);
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("unloaded").ValueKind);
    }

    [Theory]
    [InlineData(-32f, 31.99f, 0, 0)]
    [InlineData(32f, -32.01f, 1, -1)]
    [InlineData(-32.01f, 95.99f, -1, 1)]
    public void ZonesAreNumberedAsTheGameNumbersThem(float x, float z, int zx, int zz) => Assert.Equal(new ZoneId(zx, zz), ZoneId.Of(x, z));

    [Fact] public void ANonClassicAreaLeavesOutItsCorners()
    {
        var range = new SimulationRange(2, 2, Classic: false);
        Assert.True(range.KeepsLoaded(new ZoneId(0, 0), new ZoneId(2, 1)));  // 143 m between centres, within 160
        Assert.False(range.KeepsLoaded(new ZoneId(0, 0), new ZoneId(2, 2))); // 181 m
        Assert.True(new SimulationRange(2, 2, Classic: true).KeepsLoaded(new ZoneId(0, 0), new ZoneId(2, 2)));
        Assert.False(range.KeepsLoaded(new ZoneId(0, 0), new ZoneId(3, 0)));
    }

    [Fact] public void AReadingWithoutARequestedZoneIsRefused()
    {
        var data = JsonDocument.Parse("""{"source":"zone-presence","complete":true,"reference":{"x":0,"z":0},"simulation":{"near":2,"far":2,"classic":true},"zones":[{"x":0,"z":0,"terrainLoaded":true,"instances":1,"nearInstances":1,"saved":1,"withoutInstance":0}]}""").RootElement;
        Assert.Single(ZoneReading.Parse(data, [new ZoneId(0, 0)]).Zones);
        Assert.Contains("not the zones asked for", Assert.Throws<InvalidOperationException>(() => ZoneReading.Parse(data, [new ZoneId(0, 0), new ZoneId(1, 0)])).Message);
        var incomplete = JsonDocument.Parse("""{"source":"zone-presence","complete":false}""").RootElement;
        Assert.Throws<InvalidOperationException>(() => ZoneReading.Parse(incomplete, [new ZoneId(0, 0)]));
    }

    [Fact] public void ThePlanIsCheckedBeforeAnythingRuns()
    {
        var world = new World();
        using var server = world.Server().Actor("server"); using var client = world.Client().Actor("client");
        ZoneCycle With(IReadOnlyList<ZoneId> zones, TimeSpan timeout) => new()
        { Capability = "mymod.testing/zones", Zones = zones, Away = FarAway, Back = Home, StepTimeout = timeout };
        Assert.Throws<ArgumentException>(() => With([], TimeSpan.FromSeconds(1)).Run(server, client));
        Assert.Throws<ArgumentException>(() => With([new ZoneId(2, -1), new ZoneId(2, -1)], TimeSpan.FromSeconds(1)).Run(server, client));
        Assert.Throws<ArgumentException>(() => With(Site, TimeSpan.Zero).Run(server, client));
        Assert.Empty(world.Teleports); Assert.Equal(0, world.ZoneReads);
    }
}
