using System.Collections.Generic;
using System.Linq;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;
using Vector3 = UnityEngine.Vector3;

// World state that mods read or add to, as Valheim 1.0.16 keeps it: global keys, the order overlapping terrain
// modifiers apply in, and the placement fields of a location. Expected values are worked out by hand from the rules.
public sealed class GlobalKeyTests
{
    private static List<(long Target, List<string> Keys)> Broadcasts() =>
        ZRoutedRpc.instance.Invoked.Where(c => c.Method == "GlobalKeys").Select(c => (c.Target, (List<string>)c.Args[0])).ToList();

    private static ZoneSystem StartedServer()
    {
        var zones = ZoneSystem.instance!;
        zones.Start();
        return zones;
    }

    [Fact] public void AKeySetBeforeStartIsDroppedAndTheSameCallAfterStartSticks()
    {
        using var world = new ValheimWorldScope().WithNetwork(server: true).WithZoneSystem();
        var zones = ZoneSystem.instance!;

        zones.SetGlobalKey(GlobalKeys.defeated_eikthyr);
        Assert.False(zones.GetGlobalKey(GlobalKeys.defeated_eikthyr));
        Assert.Empty(zones.GetGlobalKeys());
        Assert.Empty(Broadcasts());

        zones.Start();
        Assert.True(ZRoutedRpc.instance.IsRegistered("SetGlobalKey"));
        Assert.True(ZRoutedRpc.instance.IsRegistered("RemoveGlobalKey"));
        Assert.False(ZRoutedRpc.instance.IsRegistered("GlobalKeys"));
        zones.SetGlobalKey(GlobalKeys.defeated_eikthyr);
        Assert.True(zones.GetGlobalKey(GlobalKeys.defeated_eikthyr));
        Assert.True(zones.GetGlobalKey("Defeated_Eikthyr"));
        Assert.Equal(new[] { "defeated_eikthyr" }, zones.GetGlobalKeys());
        var broadcast = Assert.Single(Broadcasts());
        Assert.Equal(ZRoutedRpc.Everybody, broadcast.Target);
        Assert.Equal(new[] { "defeated_eikthyr" }, broadcast.Keys);
    }

    [Fact] public void AValueKeyIsFoundByItsNameAndANewValueReplacesTheOld()
    {
        using var world = new ValheimWorldScope().WithNetwork(server: true).WithZoneSystem();
        var zones = StartedServer();

        zones.SetGlobalKey(GlobalKeys.activeBosses, 1f);
        zones.SetGlobalKey("activeBosses 2");
        Assert.Equal(new[] { "activebosses 2" }, zones.GetGlobalKeys());
        Assert.True(zones.GetGlobalKey(GlobalKeys.activeBosses, out float count));
        Assert.Equal(2f, count);
        Assert.True(zones.GetGlobalKey("ACTIVEBOSSES", out string text));
        Assert.Equal("2", text);
        Assert.False(zones.GetGlobalKey("activebosses 2")); // a name, not a line
        Assert.True(zones.GetGlobalKeyExact("activebosses 2"));
        Assert.False(zones.GetGlobalKeyExact("activebosses 1"));
        Assert.Equal(new[] { "activebosses 2" }, Broadcasts().Last().Keys);
    }

    [Fact] public void AModsOwnStringIsAKeyWithoutAnEnumMember()
    {
        using var world = new ValheimWorldScope().WithNetwork(server: true).WithZoneSystem();
        var zones = StartedServer();

        zones.SetGlobalKey("MyMod_BridgeBuilt");
        Assert.True(zones.GetGlobalKey("mymod_bridgebuilt"));
        Assert.Equal(new[] { "mymod_bridgebuilt" }, zones.GetGlobalKeys());
        Assert.Empty(zones.m_globalKeysEnums);
    }

    [Fact] public void RemovingByNameRemovesAnyValueAndRemovingNothingBroadcastsNothing()
    {
        using var world = new ValheimWorldScope().WithNetwork(server: true).WithZoneSystem();
        var zones = StartedServer();
        zones.SetGlobalKey("activebosses 3");
        zones.SetGlobalKey(GlobalKeys.defeated_gdking);
        Assert.Equal(2, Broadcasts().Count);

        zones.RemoveGlobalKey(GlobalKeys.activeBosses);
        Assert.False(zones.GetGlobalKey(GlobalKeys.activeBosses));
        Assert.Equal(new[] { "defeated_gdking" }, zones.GetGlobalKeys());
        Assert.Equal(new[] { "defeated_gdking" }, Broadcasts().Last().Keys);

        zones.RemoveGlobalKey("killed_surtling");
        Assert.Equal(3, Broadcasts().Count);
    }

    [Fact] public void OnlyAnExactRepeatIsSkipped()
    {
        using var world = new ValheimWorldScope().WithNetwork(server: true).WithZoneSystem();
        var zones = StartedServer();
        zones.SetGlobalKey("defeated_bonemass");
        zones.SetGlobalKey("defeated_bonemass");
        Assert.Single(Broadcasts());
        // The server skips a set only when the string as sent is already a line; a mixed-case name is applied again.
        zones.SetGlobalKey("Defeated_Bonemass");
        Assert.Equal(2, Broadcasts().Count);
        Assert.Equal(new[] { "defeated_bonemass" }, zones.GetGlobalKeys());
    }

    [Fact] public void ANewPeerGetsTheWholeList()
    {
        using var world = new ValheimWorldScope().WithNetwork(server: true).WithZoneSystem();
        var zones = StartedServer();
        zones.SetGlobalKey(GlobalKeys.defeated_eikthyr);
        zones.SetGlobalKey(GlobalKeys.defeated_gdking);

        zones.OnNewPeer(7);
        var sent = Broadcasts().Last();
        Assert.Equal(7L, sent.Target);
        Assert.Equal(new[] { "defeated_eikthyr", "defeated_gdking" }, sent.Keys.OrderBy(k => k, System.StringComparer.Ordinal));
    }

    [Fact] public void AClientsSetGoesToTheServerAndChangesNothingHere()
    {
        using var world = new ValheimWorldScope().WithNetwork(server: false).WithZoneSystem();
        var client = ZoneSystem.instance!;
        client.Start();
        Assert.True(ZRoutedRpc.instance.IsRegistered("GlobalKeys"));
        Assert.False(ZRoutedRpc.instance.IsRegistered("SetGlobalKey"));

        client.SetGlobalKey(GlobalKeys.defeated_dragon);
        Assert.False(client.GetGlobalKey(GlobalKeys.defeated_dragon));
        var call = Assert.Single(ZRoutedRpc.instance.Invoked, c => c.Method == "SetGlobalKey");
        Assert.Equal("defeated_dragon", call.Args[0]);

        ZRoutedRpc.instance.Deliver(42, "GlobalKeys", new List<string> { "defeated_dragon" });
        Assert.True(client.GetGlobalKey(GlobalKeys.defeated_dragon));
    }

    [Fact] public void TheServersBroadcastReplacesAClientsWholeList()
    {
        using var world = new ValheimWorldScope().WithNetwork(server: false).WithZoneSystem();
        var client = ZoneSystem.instance!;
        client.Start();
        client.GlobalKeyAdd("mymod_seen_intro"); // a key the client added locally (a publicized private method)
        client.GlobalKeyAdd("defeated_eikthyr");
        Assert.True(client.GetGlobalKey("mymod_seen_intro"));

        ZRoutedRpc.instance.Deliver(42, "GlobalKeys", new List<string> { "defeated_eikthyr", "activebosses 1" });

        Assert.False(client.GetGlobalKey("mymod_seen_intro"));
        Assert.Equal(new[] { "activebosses 1", "defeated_eikthyr" }, client.GetGlobalKeys().OrderBy(k => k, System.StringComparer.Ordinal));
        Assert.True(client.GetGlobalKey(GlobalKeys.activeBosses, out float bosses));
        Assert.Equal(1f, bosses);
        Assert.Equal(new[] { GlobalKeys.defeated_eikthyr, GlobalKeys.activeBosses }, client.m_globalKeysEnums.OrderBy(k => k)); // enum order
    }

    [Fact] public void WorldModifiersAreKeptWithTheWorldAndProgressWithTheZoneSave()
    {
        List<string> saved, worldKeys = new();
        using (new ValheimWorldScope().WithNetwork(server: true).WithZoneSystem())
        {
            var zones = ZoneSystem.instance!;
            zones.WorldStartingGlobalKeys = worldKeys;
            zones.Start();
            zones.SetGlobalKey(GlobalKeys.defeated_eikthyr);
            zones.SetGlobalKey(GlobalKeys.PlayerDamage, 50f);
            zones.SetGlobalKey(GlobalKeys.PlayerDamage, 75f); // replaces the world's line too
            Assert.Equal(new[] { "playerdamage 75" }, worldKeys);
            saved = zones.SaveGlobalKeys();
            Assert.Equal(new[] { "defeated_eikthyr" }, saved);
        }

        using (new ValheimWorldScope().WithNetwork(server: true).WithZoneSystem())
        {
            var zones = ZoneSystem.instance!;
            zones.WorldStartingGlobalKeys = worldKeys;
            zones.LoadGlobalKeys(saved);
            Assert.False(zones.GetGlobalKey(GlobalKeys.PlayerDamage)); // not in the zone save
            zones.SetStartingGlobalKeys(send: false);
            Assert.True(zones.GetGlobalKey(GlobalKeys.defeated_eikthyr));
            Assert.True(zones.GetGlobalKey(GlobalKeys.PlayerDamage, out float damage));
            Assert.Equal(75f, damage);
            Assert.Equal(new[] { "playerdamage 75" }, worldKeys);
        }
    }

    [Theory]
    [InlineData("defeated_eikthyr", "defeated_eikthyr", "", GlobalKeys.defeated_eikthyr)]
    [InlineData("PlayerDamage 50", "playerdamage", "50", GlobalKeys.PlayerDamage)]
    [InlineData("mymod_key a b", "mymod_key", "a b", GlobalKeys.NonServerOption)]
    [InlineData(" leading", " leading", "", GlobalKeys.NonServerOption)] // a space at 0 does not split
    public void KeysSplitAtTheFirstSpace(string key, string name, string value, GlobalKeys parsed)
    {
        Assert.Equal(name, ZoneSystem.GetKeyValue(key, out string actualValue, out GlobalKeys actualKey));
        Assert.Equal(value, actualValue);
        Assert.Equal(parsed, actualKey);
    }
}

public sealed class TerrainModifierOrderTests
{
    private static TerrainModifier Modifier(float x, float y, float z, bool player = false, int sortOrder = 0, long created = 0)
    {
        var zdo = ZDOMan.instance!.CreateNewZDO(new Vector3(x, y, z), "modifier".GetStableHashCode());
        zdo.Persistent = true;
        if (created != 0) zdo.Set(ZDOVars.s_terrainModifierTimeCreated, created);
        return new TerrainModifier(new Vector3(x, y, z), new ZNetView(zdo)) { m_playerModifiction = player, m_sortOrder = sortOrder };
    }

    private static void Wake(params TerrainModifier[] modifiers) { foreach (var m in modifiers) m.Awake(); }

    // A distance-only sort, the mistake the game's order is checked against.
    private static List<TerrainModifier> NaiveByDistance(IEnumerable<TerrainModifier> modifiers) =>
        modifiers.OrderBy(m => m.transform.position.x * m.transform.position.x + m.transform.position.z * m.transform.position.z).ToList();

    [Fact] public void NonPlayerModifiersComeBeforePlayerOnesWhateverElse()
    {
        using var world = new ValheimWorldScope().WithZdos().WithTerrainModifiers();
        // The player's hoe mark is nearer the origin, lower in sort order and older; the location's flattener still goes first.
        var hoe = Modifier(1, 30, 1, player: true, sortOrder: -5, created: 100);
        var location = Modifier(500, 60, 500, sortOrder: 10, created: 900);
        Wake(hoe, location);

        Assert.Equal(new[] { location, hoe }, TerrainModifier.GetAllInstances());
        Assert.Equal(new[] { hoe, location }, NaiveByDistance(new[] { hoe, location }));
    }

    [Fact] public void LowerSortOrderComesFirst()
    {
        using var world = new ValheimWorldScope().WithZdos().WithTerrainModifiers();
        var near = Modifier(2, 30, 2, sortOrder: 1, created: 100);
        var far = Modifier(300, 30, 300, sortOrder: 0, created: 900);
        Wake(near, far);
        Assert.Equal(new[] { far, near }, TerrainModifier.GetAllInstances());
    }

    [Fact] public void EarlierCreationComesFirstAndSurvivesARestart()
    {
        using var world = new ValheimWorldScope().WithZdos().WithTerrainModifiers();
        var younger = Modifier(2, 30, 2, created: 200);
        var older = Modifier(300, 30, 300, created: 100);
        Wake(younger, older);
        Assert.Equal(new[] { older, younger }, TerrainModifier.GetAllInstances());

        // Restart: the ZDOs come back from the save and the objects wake again, in the other order.
        var zdoYounger = younger.m_nview!.GetZDO(); var zdoOlder = older.m_nview!.GetZDO();
        Assert.Equal(2, ZDOMan.instance!.RoundTripThroughSave());
        TerrainModifier.RemoveAll();
        var olderAgain = new TerrainModifier(zdoOlder.GetPosition(), new ZNetView(zdoOlder));
        var youngerAgain = new TerrainModifier(zdoYounger.GetPosition(), new ZNetView(zdoYounger));
        Wake(youngerAgain, olderAgain);
        Assert.Equal(100L, olderAgain.CreationTime);
        Assert.Equal(new[] { olderAgain, youngerAgain }, TerrainModifier.GetAllInstances());
    }

    [Fact] public void AModifierWithoutAStoredTimeReadsZeroAndGoesBeforeTimedOnes()
    {
        using var world = new ValheimWorldScope().WithZdos().WithTerrainModifiers();
        var converted = Modifier(1, 30, 1, created: 100);
        var placedSince = Modifier(400, 30, 400);
        var noView = new TerrainModifier(new Vector3(500, 30, 500));
        Wake(converted, placedSince, noView);
        Assert.Equal(0L, placedSince.CreationTime);
        Assert.Equal(0L, noView.CreationTime);
        Assert.Equal(new[] { placedSince, noView, converted }, TerrainModifier.GetAllInstances());
    }

    [Fact] public void DistanceCountsTheHeightToo()
    {
        using var world = new ValheimWorldScope().WithZdos().WithTerrainModifiers();
        var low = Modifier(5, 0, 0);   // 5² = 25
        var high = Modifier(4, 40, 0); // 4² + 40² = 1616, though nearer the origin across the ground
        Wake(high, low);
        Assert.Equal(new[] { low, high }, TerrainModifier.GetAllInstances());
        Assert.Equal(new[] { high, low }, NaiveByDistance(new[] { high, low }));
    }

    // Two modifiers that differ only in one tie-breaker; swapping that value between them swaps the order.
    [Theory]
    [InlineData("player")]
    [InlineData("sortOrder")]
    [InlineData("created")]
    [InlineData("distance")]
    public void SwappingTheDecidingValueSwapsTheOrder(string key)
    {
        using var world = new ValheimWorldScope().WithZdos().WithTerrainModifiers();
        TerrainModifier Make(bool first) => key switch
        {
            "player" => Modifier(10, 30, 10, player: !first, created: 5),
            "sortOrder" => Modifier(10, 30, 10, sortOrder: first ? 1 : 2, created: 5),
            "created" => Modifier(10, 30, 10, created: first ? 5 : 6),
            _ => Modifier(first ? 10 : 11, 30, 10, created: 5),
        };
        var a = Make(first: true); var b = Make(first: false);
        Wake(b, a);
        Assert.Equal(new[] { a, b }, TerrainModifier.GetAllInstances());

        TerrainModifier.RemoveAll();
        var swappedA = Make(first: false); var swappedB = Make(first: true);
        Wake(swappedA, swappedB);
        Assert.Equal(new[] { swappedB, swappedA }, TerrainModifier.GetAllInstances());
    }

    [Fact] public void TheListIsSortedOnlyWhenAModifierJoinsOrLeaves()
    {
        using var world = new ValheimWorldScope().WithZdos().WithTerrainModifiers();
        var a = Modifier(1, 30, 1, sortOrder: 0);
        var b = Modifier(2, 30, 2, sortOrder: 1);
        Wake(b, a);
        Assert.Equal(new[] { a, b }, TerrainModifier.GetAllInstances());

        a.m_sortOrder = 2;
        Assert.Equal(new[] { a, b }, TerrainModifier.GetAllInstances());
        var c = Modifier(3, 30, 3, sortOrder: 5);
        c.Awake();
        Assert.Equal(new[] { b, a, c }, TerrainModifier.GetAllInstances());

        b.OnDestroy();
        Assert.Equal(new[] { a, c }, TerrainModifier.GetAllInstances());
    }

    [Fact] public void AHeightmapAppliesTheEnabledModifiersThatReachIt()
    {
        using var world = new ValheimWorldScope().WithZdos().WithTerrainModifiers();
        var zone = world.RegisterHeightmap(new Vector2s(0, 0));
        // Zone 0,0 spans x and z from -32 to 32. The default modifier's radius is its paint radius, 2 m, and its
        // reach is 2 + 4 = 6 m, so one centred at x = 38 touches the edge and one at 38.5 does not.
        var edge = Modifier(38, 30, 0, player: true);
        var beyond = Modifier(38.5f, 30, 0);
        var off = Modifier(0, 30, 0); off.enabled = false;
        var inside = Modifier(5, 30, 5);
        Wake(edge, beyond, off, inside);

        Assert.Equal(2f, inside.GetRadius());
        Assert.Equal(new[] { inside, edge }, zone.ModifiersInApplyOrder());
        Assert.True(zone.CheckTerrainModIsContained(inside));
        Assert.False(zone.CheckTerrainModIsContained(edge));
        Assert.Equal(2, zone.PokeCount); // edge and inside; the disabled one does not poke
    }
}

public sealed class LocationPlacementTests
{
    [Fact] public void DefaultsAreTheGames()
    {
        var location = new ZoneSystem.ZoneLocation();
        Assert.Equal(-1000f, location.m_minAltitude);
        Assert.Equal(1000f, location.m_maxAltitude);
        Assert.Equal(2f, location.m_maxTerrainDelta);
        Assert.True(location.m_randomRotation);
        Assert.False(location.m_slopeRotation);
        Assert.Equal(Heightmap.BiomeArea.Everything, location.m_biomeArea);
        Assert.Equal(Heightmap.Biome.None, location.m_biome);
        Assert.Equal(30f, new ZoneSystem().m_waterLevel);
    }

    [Theory]
    [InlineData(30f, 0f)]
    [InlineData(80f, 50f)]
    [InlineData(230f, 200f)]
    [InlineData(10f, -20f)]
    public void AltitudeIsMetresAboveTheWater(float ground, float altitude) =>
        Assert.Equal(altitude, ZoneSystem.ZoneLocation.AltitudeAboveWater(ground));

    [Theory]
    [InlineData(30f, 0f, 1000f, true)]      // at the water line; both ends are included
    [InlineData(30f, 0.5f, 1000f, false)]
    [InlineData(80f, -1000f, 50f, true)]    // altitude 50
    [InlineData(80.5f, -1000f, 50f, false)]
    [InlineData(230f, 180f, 210f, true)]    // altitude 200, far from the water
    [InlineData(10f, 0f, 1000f, false)]     // below the water
    [InlineData(10f, -1000f, 1000f, true)]
    public void TheAltitudeRuleReadsHeightsFromTheWaterLevel(float ground, float min, float max, bool allowed) =>
        Assert.Equal(allowed, new ZoneSystem.ZoneLocation { m_minAltitude = min, m_maxAltitude = max }.IsAltitudeAllowed(ground));

    [Fact] public void AbsoluteHeightsGiveTheOppositeVerdictAwayFromTheWater()
    {
        // A definition written as if the limits were absolute heights: "between 20 and 60 m".
        var location = new ZoneSystem.ZoneLocation { m_minAltitude = 20f, m_maxAltitude = 60f };
        bool Absolute(float ground) => ground >= location.m_minAltitude && ground <= location.m_maxAltitude;

        Assert.True(Absolute(40f));
        Assert.False(location.IsAltitudeAllowed(40f)); // altitude 10: the game refuses it
        Assert.False(Absolute(80f));
        Assert.True(location.IsAltitudeAllowed(80f));  // altitude 50: the game accepts it
    }

    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 0f, 90f)]
    [InlineData(0f, -1f, 180f)]
    [InlineData(-1f, 0f, 270f)]
    [InlineData(1f, 1f, 45f)]
    [InlineData(0.17632698f, 1f, 0f)]   // 10 degrees snaps down to 0
    [InlineData(0.21255656f, 1f, 22.5f)] // 12 degrees snaps up to 22.5
    [InlineData(-0.17632698f, 1f, 0f)]  // 350 degrees snaps to 360, which is 0
    [InlineData(0f, 0f, 0f)]
    public void ASlopeRotatedLocationFacesDownhillIn22AndAHalfDegreeSteps(float x, float z, float yaw) =>
        Assert.Equal(yaw, ZoneSystem.ZoneLocation.SlopeRotationYaw(new Vector3(x, -3f, z)));

    [Fact] public void APointIsCheckedInTheGamesOrder()
    {
        using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(80f)); // Meadows, altitude 50 everywhere
        var generator = WorldGenerator.instance!;
        var location = new ZoneSystem.ZoneLocation { m_biome = Heightmap.Biome.Meadows, m_maxAltitude = 60f };

        Assert.Equal(ZoneSystem.ZoneLocation.Placement.Accepted, location.CheckPlacement(300, 400, generator));
        Assert.Equal(ZoneSystem.ZoneLocation.Placement.Biome, new ZoneSystem.ZoneLocation().CheckPlacement(300, 400, generator));
        Assert.Equal(ZoneSystem.ZoneLocation.Placement.Altitude, new ZoneSystem.ZoneLocation { m_biome = Heightmap.Biome.Meadows, m_maxAltitude = 40f }.CheckPlacement(300, 400, generator));
        // 500 m from the origin: refused for distance before the biome is looked at.
        Assert.Equal(ZoneSystem.ZoneLocation.Placement.Distance, new ZoneSystem.ZoneLocation { m_minDistance = 600f }.CheckPlacement(300, 400, generator));
        location.m_maxDistanceFromCenter = 499f;
        Assert.Equal(ZoneSystem.ZoneLocation.Placement.CenterDistance, location.CheckPlacement(300, 400, generator));
        location.m_maxDistanceFromCenter = 0f;
        Assert.Equal(ZoneSystem.ZoneLocation.Placement.TerrainDelta, location.CheckPlacement(300, 400, generator, terrainDelta: 2.5f));
        Assert.Equal(ZoneSystem.ZoneLocation.Placement.Accepted, location.CheckPlacement(300, 400, generator, terrainDelta: 2f));
    }
}
