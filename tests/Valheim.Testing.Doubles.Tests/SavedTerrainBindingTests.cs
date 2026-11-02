using System;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

public sealed class SavedTerrainBindingTests
{
    [Fact]
    public void ExplicitSavedCompilerReloadKeepsItsIdentityAndDecodedState()
    {
        using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(40f)).WithZdos().WithZoneSystem();
        var zone = new Vector2s(-1, 2);
        var first = world.RegisterHeightmap(zone, width: 4); TerrainComp source = first.m_terrainComp!;
        source.m_modifiedHeight[7] = true; source.m_levelDelta[7] = 1.25f; source.m_smoothDelta[7] = -.5f;
        source.m_modifiedPaint[6] = true; source.m_paintMask[6] = new Color(.1f, .2f, .3f, .4f);
        source.m_operations = 3; source.m_lastOpPoint = new Vector3(-63, 40, 129); source.m_lastOpRadius = 2.5f;
        source.Save(); ZDO saved = source.m_nview.GetZDO(); byte[] payload = (byte[])saved.GetByteArray(ZDOVars.s_TCData)!.Clone();
        saved.SetOwner(77); saved.Persistent = false; var id = saved.m_uid;
        world.UnloadHeightmap(zone);
        var loaded = world.RegisterHeightmap(zone, saved, width: 4);
        Assert.Single(ZDOMan.instance!.Zdos);
        Assert.Same(saved, loaded.m_terrainComp!.m_nview.GetZDO()); Assert.Equal(id, saved.m_uid);
        Assert.Equal(77, saved.GetOwner()); Assert.False(saved.Persistent); Assert.Equal(payload, saved.GetByteArray(ZDOVars.s_TCData));
        Assert.True(loaded.m_terrainComp.m_modifiedHeight[7]); Assert.Equal(1.25f, loaded.m_terrainComp.m_levelDelta[7]);
        Assert.Equal(-.5f, loaded.m_terrainComp.m_smoothDelta[7]); Assert.True(loaded.m_terrainComp.m_modifiedPaint[6]);
        Assert.Equal(.3f, loaded.m_terrainComp.m_paintMask[6].b); Assert.Equal(3, loaded.m_terrainComp.m_operations);
        Assert.Equal(source.m_lastOpPoint, loaded.m_terrainComp.m_lastOpPoint); Assert.Equal(2.5f, loaded.m_terrainComp.m_lastOpRadius);
        Assert.False(ZoneSystem.instance!.IsZoneGenerated(zone));
    }
    private static ZDO Saved(ValheimWorldScope world, Vector2s zone)
    {
        var map = world.RegisterHeightmap(zone, width: 4); TerrainComp compiler = map.m_terrainComp!;
        compiler.m_modifiedHeight[7] = true; compiler.m_levelDelta[7] = 1.25f; compiler.m_smoothDelta[7] = -.5f;
        compiler.m_levelDelta[1] = 3f; // Unflagged values do not survive the existing Save/Load contract.
        compiler.m_modifiedPaint[6] = true; compiler.m_paintMask[6] = new Color(.1f, .2f, .3f, .4f);
        compiler.m_operations = 5; compiler.m_lastOpPoint = new Vector3(1, 2, 3); compiler.m_lastOpRadius = 4.5f;
        compiler.Save(); var saved = compiler.m_nview.GetZDO(); saved.SetOwner(77); saved.Persistent = false;
        saved.Set("sentinel-int", 17); saved.Set("sentinel-long", 1L << 40); saved.Set("sentinel-float", 1.5f);
        saved.Set("sentinel-vector", new Vector3(2, 3, 4)); saved.Set("sentinel-rotation", Quaternion.Euler(0, 45, 0));
        saved.Set("sentinel-string", "unchanged"); saved.Set("sentinel-bytes", new byte[] { 3, 1, 4 });
        return saved;
    }
    private static void UnchangedRecord(ZDO saved, ZDOID id, Vector3 position, byte[]? payload)
    {
        Assert.Equal(id, saved.m_uid); Assert.Equal(position, saved.GetPosition()); Assert.Equal(77, saved.GetOwner()); Assert.False(saved.Persistent);
        Assert.Equal(payload, saved.GetByteArray(ZDOVars.s_TCData)); Assert.Equal(17, saved.GetInt("sentinel-int"));
        Assert.Equal(1L << 40, saved.GetLong("sentinel-long")); Assert.Equal(1.5f, saved.GetFloat("sentinel-float"));
        Assert.Equal(new Vector3(2, 3, 4), saved.GetVec3("sentinel-vector", default));
        Assert.Equal(45f, saved.GetQuaternion("sentinel-rotation", default).EulerY); Assert.Equal("unchanged", saved.GetString("sentinel-string"));
        Assert.Equal(new byte[] { 3, 1, 4 }, saved.GetByteArray("sentinel-bytes"));
    }

    [Fact]
    public void RepeatedExplicitReloadPreservesRecordAndRendersOnlyThroughTheExistingBaselinePath()
    {
        using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(40f)).WithZdos().WithZoneSystem();
        var zone = new Vector2s(-1, 2); ZDO saved = Saved(world, zone); var id = saved.m_uid; Vector3 position = saved.GetPosition();
        byte[] payload = (byte[])saved.GetByteArray(ZDOVars.s_TCData)!.Clone();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            world.UnloadHeightmap(zone); var loaded = world.RegisterHeightmap(zone, saved, width: 4); var compiler = loaded.m_terrainComp!;
            Assert.Single(ZDOMan.instance!.Zdos); Assert.Empty(ZDOMan.instance.DestroyQueue); Assert.Same(saved, compiler.m_nview.GetZDO());
            Assert.Same(compiler.m_nview, saved.m_view); Assert.Equal(0, compiler.SaveCount); UnchangedRecord(saved, id, position, payload);
            Assert.Equal(25, compiler.m_levelDelta.Length); Assert.Equal(0f, compiler.m_levelDelta[1]); Assert.False(compiler.m_modifiedHeight[1]);
            Assert.True(compiler.m_modifiedHeight[7]); Assert.Equal(1.25f, compiler.m_levelDelta[7]); Assert.Equal(-.5f, compiler.m_smoothDelta[7]);
            Assert.Equal((.1f, .2f, .3f, .4f), (compiler.m_paintMask[6].r, compiler.m_paintMask[6].g, compiler.m_paintMask[6].b, compiler.m_paintMask[6].a));
            Assert.True(compiler.m_modifiedPaint[6]); Assert.Equal(5, compiler.m_operations); Assert.Equal(new Vector3(1, 2, 3), compiler.m_lastOpPoint); Assert.Equal(4.5f, compiler.m_lastOpRadius);
            Assert.Null(loaded.LastRenderedHeights); Assert.False(ZoneSystem.instance!.IsZoneGenerated(zone));
            loaded.AuthoredHeight = (_, _) => 50f; loaded.RebuildTerrain();
            Assert.Equal(50.75f, loaded.LastRenderedHeights![7]); Assert.Equal(50f, loaded.LastRenderedHeights[1]);
            UnchangedRecord(saved, id, position, payload); Assert.Equal(0, compiler.SaveCount);
        }
        ZDO next = ZDOMan.instance!.CreateNewZDO(default, 1); Assert.Equal(id.ID + 1u, next.m_uid.ID); // No hidden throwaway allocations.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APristineSavedRecordBeforeAnyLiveMapBindsWithoutChangingGeneration(bool generated)
    {
        using var world = new ValheimWorldScope().WithZdos().WithZoneSystem(); var zone = new Vector2s(1, -1);
        // The payload's writer is an unregistered compiler; this world has never had a live map in its map list.
        var source = Heightmap.CreateForZone(zone, width: 4).m_terrainComp!; source.Save();
        ZDO saved = source.m_nview.GetZDO(); saved.SetOwner(77);
        if (generated) ZoneSystem.instance!.Generated.Add(zone);
        byte[] payload = (byte[])saved.GetByteArray(ZDOVars.s_TCData)!.Clone();
        Assert.Empty(Heightmap.GetAllHeightmaps());
        var loaded = Heightmap.CreateForZone(zone, saved, width: 4);
        Assert.Empty(Heightmap.GetAllHeightmaps()); Assert.Single(ZDOMan.instance!.Zdos); Assert.Same(saved, loaded.m_terrainComp!.m_nview.GetZDO());
        Assert.All(loaded.m_terrainComp.m_modifiedHeight, flag => Assert.False(flag));
        Assert.Equal(generated, ZoneSystem.instance!.IsZoneGenerated(zone)); Assert.Null(loaded.LastRenderedHeights);
        Assert.Equal(payload, saved.GetByteArray(ZDOVars.s_TCData)); Assert.Equal(77, saved.GetOwner());
    }

    [Fact]
    public void ExplicitBindingDoesNotDiscoverOrRemoveAnotherCompilerInTheSameZone()
    {
        using var world = new ValheimWorldScope().WithZdos().WithZoneSystem(); var zone = new Vector2s(0, 0);
        ZDO saved = Saved(world, zone); ZDO other = ZDOMan.instance!.CreateNewZDO(ZoneSystem.GetZonePos(zone), TerrainComp.PrefabName.GetStableHashCode());
        other.Set(ZDOVars.s_TCData, new byte[] { 1, 2, 3 }); var oldView = other.m_view;
        var loaded = world.RegisterHeightmap(zone, saved, width: 4);
        Assert.Same(saved, loaded.m_terrainComp!.m_nview.GetZDO()); Assert.Equal(2, ZDOMan.instance.Zdos.Count);
        Assert.Same(oldView, other.m_view); Assert.Equal(new byte[] { 1, 2, 3 }, other.GetByteArray(ZDOVars.s_TCData));
        Assert.Empty(ZDOMan.instance.DestroyQueue);
    }

    [Fact]
    public void OriginalSignaturesStillCompileAsMethodGroupsAndKeepFreshAndNoCompilerBehavior()
    {
        using var world = new ValheimWorldScope().WithZdos().WithZoneSystem();
        Func<Vector2s, int, bool, Heightmap> create = Heightmap.CreateForZone;
        Func<Vector2s, int, bool, Heightmap> register = world.RegisterHeightmap;
        var zone = new Vector2s(0, 0); var noCompiler = create(zone, 4, false);
        Assert.Null(noCompiler.m_terrainComp); Assert.Empty(ZDOMan.instance!.Zdos);
        TerrainComp direct = new(noCompiler, 4); Assert.True(direct.m_nview.IsOwner()); Assert.True(direct.m_nview.GetZDO().Persistent);
        var loaded = register(zone, 4, true); Assert.Equal(2, ZDOMan.instance.Zdos.Count); Assert.NotSame(direct.m_nview.GetZDO(), loaded.m_terrainComp!.m_nview.GetZDO());
        var none = register(zone, 4, false); Assert.Null(none.m_terrainComp); Assert.Equal(2, ZDOMan.instance.Zdos.Count);
        Assert.False(ZoneSystem.instance!.IsZoneGenerated(zone));
    }

    [Fact]
    public void MissingPayloadFailureKeepsTheOldLiveMapAndSavedView() => FailureKeepsOld("missing");

    [Fact]
    public void AFailedNeverLiveRecordRestoresNullViewWithoutReplacingAnotherLiveMap()
    {
        using var world = new ValheimWorldScope().WithZdos().WithZoneSystem(); var zone = new Vector2s(0, 0);
        Heightmap old = world.RegisterHeightmap(zone, width: 4); ZDO oldRecord = old.m_terrainComp!.m_nview.GetZDO();
        ZNetView? oldView = oldRecord.m_view;
        ZDO saved = ZDOMan.instance!.CreateNewZDO(ZoneSystem.GetZonePos(zone), TerrainComp.PrefabName.GetStableHashCode());
        saved.SetOwner(77); Assert.Null(saved.m_view);
        Assert.Throws<System.IO.InvalidDataException>(() => world.RegisterHeightmap(zone, saved, width: 4));
        Assert.Null(saved.m_view); Assert.Same(old, Assert.Single(Heightmap.GetAllHeightmaps()));
        Assert.Same(oldView, oldRecord.m_view); Assert.Equal(2, ZDOMan.instance.Zdos.Count); Assert.Empty(ZDOMan.instance.DestroyQueue);
        Assert.Equal(77, saved.GetOwner()); Assert.Null(saved.GetByteArray(ZDOVars.s_TCData));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("null-map")]
    [InlineData("prefab")]
    [InlineData("zone")]
    [InlineData("width-zero")]
    [InlineData("width-mismatch")]
    [InlineData("overflow")]
    [InlineData("height-count")]
    [InlineData("compressed-truncated")]
    [InlineData("decoded-truncated")]
    public void InvalidSavedInputsKeepTheOldMapViewAndRecord(string failure) => FailureKeepsOld(failure);

    private static void FailureKeepsOld(string failure)
    {
        using var world = new ValheimWorldScope().WithZdos().WithZoneSystem(); var zone = new Vector2s(0, 0);
        ZDO saved = Saved(world, zone); Heightmap old = Assert.Single(Heightmap.GetAllHeightmaps()); ZNetView? view = saved.m_view;
        switch (failure)
        {
            case "prefab": saved.SetPrefab(1); break;
            case "zone": saved.SetPosition(new Vector3(64, 0, 0)); break;
            case "missing": saved.RemoveByteArray(ZDOVars.s_TCData); break;
            case "height-count":
                var wrong = new ZPackage(); wrong.Write(1); wrong.Write(0); wrong.Write(Vector3.zero); wrong.Write(0f); wrong.Write(24);
                saved.Set(ZDOVars.s_TCData, Utils.Compress(wrong.GetArray())); break;
            case "compressed-truncated": saved.Set(ZDOVars.s_TCData, saved.GetByteArray(ZDOVars.s_TCData)!.Take(5).ToArray()); break;
            case "decoded-truncated": saved.Set(ZDOVars.s_TCData, Utils.Compress(Utils.Decompress(saved.GetByteArray(ZDOVars.s_TCData)!).Take(12).ToArray())); break;
        }
        byte[]? payload = saved.GetByteArray(ZDOVars.s_TCData)?.ToArray(); var id = saved.m_uid; Vector3 position = saved.GetPosition(); int prefab = saved.GetPrefab();
        Exception? error = Record.Exception(() =>
        {
            switch (failure)
            {
                case "null": world.RegisterHeightmap(zone, null!, width: 4); break;
                case "null-map": _ = new TerrainComp(null!, 4, saved); break;
                case "width-zero": world.RegisterHeightmap(zone, saved, width: 0); break;
                case "width-mismatch": _ = new TerrainComp(old, 2, saved); break;
                case "overflow": world.RegisterHeightmap(zone, saved, width: int.MaxValue); break;
                default: world.RegisterHeightmap(zone, saved, width: 4); break;
            }
        });
        Assert.NotNull(error); Assert.Same(old, Assert.Single(Heightmap.GetAllHeightmaps())); Assert.Same(view, saved.m_view);
        Assert.Single(ZDOMan.instance!.Zdos); Assert.Empty(ZDOMan.instance.DestroyQueue); UnchangedRecord(saved, id, position, payload);
        Assert.Equal(prefab, saved.GetPrefab()); Assert.False(ZoneSystem.instance!.IsZoneGenerated(zone));
    }

}
