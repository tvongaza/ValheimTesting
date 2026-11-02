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
}
