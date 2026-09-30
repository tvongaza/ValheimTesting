using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Object = UnityEngine.Object;

// The gaps a second consumer (MoreWorldLocations) found (#83). Expected values come from the game's 1.0.16 code, written
// out here by hand, never from the doubles' own output.
public sealed class SecondConsumerTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithScene().WithZdos().WithTerrainModifiers();
    public void Dispose() { Object.EndOfFrame(); _scope.Dispose(); }

    // ---- TerrainComp.Save: the game's TCData bytes ----

    private TerrainComp Compiler(int width = 2) => _scope.RegisterHeightmap(new Vector2s(0, 0), width).m_terrainComp!;

    [Fact] public void TerrainCompSaveWritesTheGamesBytesAndLoadReadsThemBack()
    {
        var compiler = Compiler();
        compiler.m_operations = 3; compiler.m_lastOpPoint = new Vector3(1f, 2f, 3f); compiler.m_lastOpRadius = 4.5f;
        compiler.m_modifiedHeight[0] = true; compiler.m_levelDelta[0] = 0.25f; compiler.m_smoothDelta[0] = -0.5f;
        compiler.m_modifiedPaint[1] = true; compiler.m_paintMask[1] = new Color(0.1f, 0.2f, 0.3f, 0.4f);
        compiler.Save();

        // 1.0.16 TerrainComp.Save: version 1, operations, last point, last radius, 9 vertices each a flag (+ level, smooth),
        // 9 texels each a flag (+ r, g, b, a), little-endian as BinaryWriter writes, then gzip (Utils.Compress).
        var expected = new MemoryStream();
        using (var w = new BinaryWriter(expected))
        {
            w.Write(1); w.Write(3); w.Write(1f); w.Write(2f); w.Write(3f); w.Write(4.5f);
            w.Write(9); w.Write(true); w.Write(0.25f); w.Write(-0.5f); for (int i = 1; i < 9; i++) w.Write(false);
            w.Write(9); w.Write(false); w.Write(true); w.Write(0.1f); w.Write(0.2f); w.Write(0.3f); w.Write(0.4f); for (int i = 2; i < 9; i++) w.Write(false);
        }
        byte[] saved = compiler.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData)!;
        Assert.Equal(expected.ToArray(), Utils.Decompress(saved));
        Assert.Equal(1, compiler.SaveCount);

        var reread = new TerrainComp(compiler.m_hmap, 2) { m_nview = compiler.m_nview };
        Assert.True(reread.Load());
        Assert.Equal((3, new Vector3(1f, 2f, 3f).x, 4.5f), (reread.m_operations, reread.m_lastOpPoint.x, reread.m_lastOpRadius));
        Assert.Equal(compiler.m_modifiedHeight, reread.m_modifiedHeight);
        Assert.Equal(compiler.m_levelDelta, reread.m_levelDelta);
        Assert.Equal(compiler.m_smoothDelta, reread.m_smoothDelta);
        Assert.Equal(compiler.m_modifiedPaint, reread.m_modifiedPaint);
        Assert.True(reread.m_paintMask[1].Equals(new Color(0.1f, 0.2f, 0.3f, 0.4f)));
    }

    [Fact] public void OnlyTheOwnerSavesAndAMismatchedCountIsNotLoaded()
    {
        var compiler = Compiler();
        compiler.m_nview.GetZDO().SetOwner(99);
        compiler.Save();
        Assert.Null(compiler.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData));
        Assert.Equal(0, compiler.SaveCount);
        Assert.False(compiler.Load());

        compiler.m_nview.GetZDO().SetOwner(ZDOMan.instance!.m_sessionID);
        compiler.m_modifiedHeight[4] = true;
        compiler.Save();
        var wider = new TerrainComp(compiler.m_hmap, 4) { m_nview = compiler.m_nview };
        Assert.False(wider.Load()); // 9 vertices saved, 25 here: the game's "height array missmatch".
        Assert.False(wider.m_modifiedHeight.Any(flag => flag));
    }

    [Fact] public void APreOnePointZeroPaintGridIsSpreadWithTheGamesMapping()
    {
        var compiler = Compiler(width: 2);
        var package = new ZPackage();
        package.Write(1); package.Write(0); package.Write(Vector3.zero); package.Write(0f);
        package.Write(9); for (int i = 0; i < 9; i++) package.Write(false);
        package.Write(4); // width x width: the old layout
        for (int i = 0; i < 4; i++) { package.Write(true); package.Write(i / 10f); package.Write(0f); package.Write(0f); package.Write(1f); }
        compiler.m_nview.GetZDO().Set(ZDOVars.s_TCData, Utils.Compress(package.GetArray()));
        Assert.True(compiler.Load());
        // The game's index mapping for width 2, worked by hand: texel k of the 3 x 3 grid takes old texel [0,1,1,2,3,3,2,3,3][k].
        var from = new[] { 0, 1, 1, 2, 3, 3, 2, 3, 3 };
        Assert.Equal(from.Select(i => i / 10f), compiler.m_paintMask.Select(c => c.r));
    }

    // ---- TerrainModifier is a component ----

    [Fact] public void ATerrainModifierIsAComponentOnItsObjectAndWakesIntoTheLiveList()
    {
        var piece = new GameObject("piece");
        piece.transform.position = new Vector3(10f, 30f, -5f);
        var child = new GameObject("leveller");
        child.transform.SetParent(piece.transform, false);
        var modifier = child.AddComponent<TerrainModifier>();
        Assert.Same(modifier, child.GetComponent<TerrainModifier>());
        Assert.Equal(10f, modifier.transform.position.x);
        Assert.Contains(modifier, TerrainModifier.s_instances); // Awake on an active object, as a spawned piece's.
        Assert.Equal(new[] { modifier }, Utils.GetEnabledComponentsInChildren<TerrainModifier>(piece));
        modifier.enabled = false;
        Assert.False(modifier.enabled);
        // Kept under an inactive object (a template), it does not wake.
        var template = new GameObject("template"); template.SetActive(false);
        var asleep = template.AddComponent<TerrainModifier>();
        Assert.DoesNotContain(asleep, TerrainModifier.s_instances);
    }

    // ---- HeightmapBuilder ----

    [Fact] public void TheBuilderCanBeReplacedOrRemovedAndCountsItsRequests()
    {
        var world = new SyntheticWorldLike();
        var builder = new HeightmapBuilder();
        HeightmapBuilder.instance = builder;
        Assert.Same(builder, HeightmapBuilder.instance);
        HeightmapBuilder.instance = null;
        Assert.Null(HeightmapBuilder.instance); // As the game's after its builder is disposed.
        HeightmapBuilder.instance = builder;

        Assert.False(builder.IsTerrainReady(Vector3.zero, 4, 1f, false, world));
        builder.MakeReady(new Vector3(64, 0, 0), 4, 1f, false, world);
        Assert.True(builder.IsTerrainReady(new Vector3(64, 0, 0), 4, 1f, false, world));
        builder.RequestTerrainSync(new Vector3(64, 0, 0), 4, 1f, false, world);
        Assert.False(builder.IsTerrainReady(new Vector3(64, 0, 0), 4, 1f, false, world)); // Handed out: a new build is queued.
        Assert.Equal(1, builder.SyncRequests);
    }

    private sealed class SyntheticWorldLike : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
    }

    // ---- Unity keeps component settings in native properties ----

    [Theory]
    [InlineData(typeof(Light))] [InlineData(typeof(Animator))] [InlineData(typeof(Rigidbody))] [InlineData(typeof(Canvas))]
    [InlineData(typeof(BoxCollider))] [InlineData(typeof(SphereCollider))] [InlineData(typeof(CapsuleCollider))] [InlineData(typeof(MeshCollider))]
    [InlineData(typeof(Renderer))] [InlineData(typeof(MeshFilter))]
    public void UnityComponentsShowNoPublicFields(Type type) =>
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));

    [Fact] public void InstantiateStillCopiesTheirSettings()
    {
        var original = new GameObject("lamp");
        var light = original.AddComponent<Light>(); light.intensity = 3f; light.range = 7f;
        var box = original.AddComponent<BoxCollider>(); box.size = new Vector3(2f, 3f, 4f); box.isTrigger = true;
        var copy = Object.Instantiate(original);
        Assert.Equal((3f, 7f), (copy.GetComponent<Light>().intensity, copy.GetComponent<Light>().range));
        Assert.Equal((3f, true), (copy.GetComponent<BoxCollider>().size.y, copy.GetComponent<BoxCollider>().isTrigger));
    }

    // ---- ZNetView ----

    [Fact] public void AZNetViewCanBeAddedToAPrefabWithTheGamesDefaults()
    {
        var prefab = new GameObject("prefab"); prefab.SetActive(false);
        var view = prefab.AddComponent<ZNetView>();
        Assert.Same(view, prefab.GetComponent<ZNetView>());
        Assert.False(view.m_persistent);       // false in the game until a prefab sets it
        Assert.False(view.m_syncInitialScale);
        Assert.False(view.IsValid());          // no ZDO yet
    }

    // ---- Rotations and colours ----

    [Fact] public void RotationsComposeAndReadBackAsUnitysEulerAngles()
    {
        Assert.Equal(Quaternion.identity.w, new GameObject("new").transform.rotation.w);
        var euler = Quaternion.Euler(10f, 20f, 30f).eulerAngles;
        Assert.Equal(10f, euler.x, 3); Assert.Equal(20f, euler.y, 3); Assert.Equal(30f, euler.z, 3);
        Assert.Equal(350f, Quaternion.Euler(0f, -10f, 0f).eulerAngles.y, 3); // Unity reports [0, 360).
        // A yaw of 90 degrees turns +z (forward) to +x (right).
        var turned = Quaternion.Euler(0f, 90f, 0f) * new Vector3(0f, 0f, 1f);
        Assert.Equal(1f, turned.x, 5); Assert.Equal(0f, turned.z, 5);
        Assert.Equal(180f, (Quaternion.Euler(0f, 90f, 0f) * Quaternion.Euler(0f, 90f, 0f)).eulerAngles.y, 3);
        // Tilt then yaw: Unity's order (z, then x, then y) is yaw * pitch.
        var both = Quaternion.Euler(0f, 90f, 0f) * Quaternion.Euler(30f, 0f, 0f);
        Assert.Equal(30f, both.eulerAngles.x, 3); Assert.Equal(90f, both.eulerAngles.y, 3);

        var parent = new GameObject("parent"); parent.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        var child = new GameObject("child"); child.transform.SetParent(parent.transform, false);
        child.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
        Assert.Equal(135f, child.transform.eulerAngles.y, 3);
        Assert.Equal(45f, child.transform.localRotation.eulerAngles.y, 3);
    }

    [Fact] public void ColoursCompareAsUnitysDo()
    {
        Assert.True(new Color(0.5f, 0.5f, 0.5f, 1f) == new Color(0.5f, 0.5f, 0.500001f, 1f)); // within Unity's 1e-10 squared distance
        Assert.False(new Color(0.5f, 0.5f, 0.5f, 1f) == new Color(0.5f, 0.5f, 0.51f, 1f));
        Assert.False(new Color(0.5f, 0.5f, 0.5f, 1f).Equals(new Color(0.5f, 0.5f, 0.500001f, 1f))); // Equals is exact
        Assert.Equal(new Color(0f, 1f, 0f, 1f), Heightmap.m_paintMaskCultivated);
        Assert.Equal(new Color(0f, 0f, 0f, 0f), Heightmap.m_paintMaskClearVegetation);
    }

    // ---- Game members ----

    [Fact] public void AZdoKeepsItsRotationAsEulerAngles()
    {
        var zdo = new ZDO(Vector3.zero, 1);
        zdo.SetRotation(Quaternion.Euler(0f, 30f, 0f));
        Assert.Equal(30f, zdo.GetRotation().eulerAngles.y, 3);
        Assert.Equal("location".GetStableHashCode(), ZDOVars.s_location);
    }

    [Fact] public void ZNetAndZoneSystemAreMonoBehavioursWithTheGamesHookTargets()
    {
        Assert.True(typeof(MonoBehaviour).IsAssignableFrom(typeof(ZNet)));
        Assert.True(typeof(MonoBehaviour).IsAssignableFrom(typeof(ZoneSystem)));
        // The 1.0.16 signatures Harmony patches name.
        Assert.Equal(7, typeof(ZoneSystem).GetMethod(nameof(ZoneSystem.PlaceLocations))!.GetParameters().Length);
        Assert.Equal(7, typeof(ZoneSystem).GetMethod(nameof(ZoneSystem.SpawnLocation))!.GetParameters().Length);
        Assert.Equal(new[] { typeof(bool), typeof(bool), typeof(bool) }, typeof(ZNet).GetMethod(nameof(ZNet.Save))!.GetParameters().Select(p => p.ParameterType));
        var entry = new ZoneSystem.ZoneLocation.PrefabEntry { Name = "Runestone", Asset = new GameObject("Runestone") };
        entry.Load(); Assert.True(entry.IsLoaded);
        entry.Release(); Assert.False(entry.IsLoaded);
    }

    [Fact] public void LocationPiecesHaveTheGamesDefaults()
    {
        var piece = new GameObject("piece"); piece.SetActive(false);
        Assert.Equal(50f, piece.AddComponent<RandomSpawn>().m_chanceToSpawn);
        Assert.Equal(1f, new RandomObject.ObjectEntry().m_weight);
        var spawner = piece.AddComponent<CreatureSpawner>();
        Assert.Equal((1, 1, 10f, 20f, 60f), (spawner.m_minLevel, spawner.m_maxLevel, spawner.m_levelupChance, spawner.m_respawnTimeMinuts, spawner.m_triggerDistance));
        Assert.Equal((0.5f, 0.3f), (piece.AddComponent<DropOnDestroyed>().m_spawnYOffset, piece.GetComponent<DropOnDestroyed>().m_spawnYStep));
        Assert.Empty(piece.AddComponent<PickableItem>().m_randomItemPrefabs);
    }
}
