using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// The doubles share process-wide singletons (ZDOMan.instance, ZNetScene.instance, ...), as the game does.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// A consumer's partial Heightmap: this assembly's implementation of the rebuild hooks, switched on per test.
public partial class Heightmap
{
    public static Func<float, float, float>? TestBaseHeight;
    public static Action<List<float>>? TestTerrainPass;
    partial void ModBaseHeight(float wx, float wz, ref float height) { if (TestBaseHeight != null) height = TestBaseHeight(wx, wz); }
    partial void ModTerrainPass(List<float> heights) => TestTerrainPass?.Invoke(heights);
}

public sealed class DoublesTests : IDisposable
{
    private sealed class Slope : WorldGenerator { public override float GetHeight(float wx, float wy) => 40f + wx * 0.1f; }
    public DoublesTests() { ZDOMan.instance = new ZDOMan(); ZNetScene.instance = new ZNetScene(); ZoneSystem.instance = new ZoneSystem(); }
    public void Dispose()
    {
        ZDOMan.instance = null; ZNetScene.instance = null; ZoneSystem.instance = null; WorldGenerator.instance = null;
        Heightmap.Registered = null; Heightmap.TestBaseHeight = null; Heightmap.TestTerrainPass = null;
    }
    private static List<ZDO> InZone(Vector2s zone) { var found = new List<ZDO>(); ZDOMan.instance!.FindObjects(zone, found, new HashSet<ZoneSystem.SectorIndex>()); return found; }

    [Fact] public void DestroyedZdosStayVisibleUntilTheQueueIsProcessed()
    {
        var zdo = ZDOMan.instance!.CreateNewZDO(new UnityEngine.Vector3(10, 30, 10), 7);
        ZDOMan.instance.DestroyZDO(zdo); ZDOMan.instance.DestroyZDO(zdo);
        Assert.Contains(zdo, InZone(ZoneSystem.GetZone(zdo.GetPosition())));
        Assert.Equal(1, ZDOMan.instance.ProcessDestroyed());
        Assert.DoesNotContain(zdo, InZone(ZoneSystem.GetZone(zdo.GetPosition())));
        Assert.Equal(0, ZDOMan.instance.ProcessDestroyed());
    }
    [Fact] public void AGhostInitialisedObjectLeavesItsZdoButJoinsNoScene()
    {
        var prefab = ZNetScene.instance!.AddPrefab("wood_beam", health: 250f);
        ZNetView.StartGhostInit();
        var ghost = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(5, 31, 5), UnityEngine.Quaternion.Euler(0, 90, 0));
        UnityEngine.Object.Destroy(ghost);
        ZNetView.FinishGhostInit();
        var zdo = Assert.Single(ZDOMan.instance!.Zdos);
        Assert.Equal("wood_beam".GetStableHashCode(), zdo.GetPrefab());
        Assert.True(zdo.Persistent); Assert.True(zdo.IsOwner());
        Assert.Empty(ZNetScene.instance.Live); Assert.Null(ZNetScene.instance.FindInstance(zdo));
        Assert.Equal(250f, ghost.GetComponent<WearNTear>().m_health);
    }
    [Fact] public void ALiveObjectIsFoundAndSceneDestructionQueuesOnlyOwnedZdos()
    {
        var prefab = ZNetScene.instance!.AddPrefab("stone_floor");
        var ours = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(1, 30, 1), UnityEngine.Quaternion.Euler(0, 0, 0));
        var theirs = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(2, 30, 2), UnityEngine.Quaternion.Euler(0, 0, 0));
        theirs.GetComponent<ZNetView>().GetZDO().SetOwner(99);
        Assert.Same(ours.View, ZNetScene.instance.FindInstance(ours.View!.GetZDO()));
        ZNetScene.instance.Destroy(ours); ZNetScene.instance.Destroy(theirs);
        Assert.True(ours.Destroyed); Assert.True(theirs.Destroyed);
        Assert.Equal(new[] { ours.View.GetZDO() }, ZDOMan.instance!.DestroyQueue);
        Assert.Null(ZNetScene.instance.GetPrefab("missing"));
        Assert.Null(prefab.GetComponent<ZNetView>());
    }
    [Fact] public void RebuildUsesTheGeneratorThenTheModsHooksAndClampsCompilerDeltas()
    {
        WorldGenerator.instance = new Slope();
        var hm = Heightmap.CreateForZone(new Vector2s(1, 0), width: 4);
        hm.RebuildTerrain();
        Assert.Equal(40f + (64f - 32f) * 0.1f, hm.LastRenderedHeights![0], 3);
        Heightmap.TestBaseHeight = (_, _) => 50f;
        Heightmap.TestTerrainPass = heights => heights[0] += 1f;
        hm.m_terrainComp!.m_levelDelta[1] = 20f;
        hm.RebuildTerrain();
        Assert.Equal(51f, hm.LastRenderedHeights![0], 3);
        Assert.Equal(58f, hm.LastRenderedHeights[1], 3); // +20 clamped to the game's +8 m
    }
    [Fact] public void ACompilerSavesOnlyWhenOwned()
    {
        var comp = Heightmap.CreateForZone(new Vector2s(0, 0)).m_terrainComp!;
        comp.Save(); Assert.Equal(1, comp.SaveCount);
        comp.m_nview.GetZDO().SetOwner(99); comp.Save(); Assert.Equal(1, comp.SaveCount);
    }
    [Fact] public void ZoneIdsNarrowToShortAsTheGamesDo()
    {
        Assert.Equal(new Vector2s(1, -1), ZoneSystem.GetZone(new UnityEngine.Vector3(40, 0, -40)));
        Assert.Equal((short)-32768, new Vector2s(32768, 0).x);
    }
    [Fact] public void LocationsLoadedFromASaveRaiseNothingButTheSetterRaisesOnce()
    {
        var zones = ZoneSystem.instance!; int raised = 0;
        zones.GenerateLocationsCompleted += () => raised++;
        zones.LoadLocationsGeneratedFromSave(true);
        Assert.Equal(0, raised); Assert.True(zones.LocationsGenerated);
        var fresh = new ZoneSystem(); fresh.GenerateLocationsCompleted += () => raised++;
        fresh.LocationsGenerated = true; fresh.LocationsGenerated = true;
        Assert.Equal(1, raised);
        fresh.GenerateLocationsCompleted += () => raised++; // subscribing afterwards fires at once
        Assert.Equal(2, raised);
    }
    [Fact] public void RoutedRpcsRefusePackagesOverSteamsLimit()
    {
        var small = new ZPackage(); small.Write(new byte[1024]);
        ZRoutedRpc.instance.InvokeRoutedRPC(1, "ok", small);
        var large = new ZPackage(); large.Write(new byte[600 * 1024]);
        Assert.Throws<InvalidOperationException>(() => ZRoutedRpc.instance.InvokeRoutedRPC(1, "big", large));
    }
    [Fact] public void LogLinesCanBeCaptured()
    {
        BepInEx.Logging.ManualLogSource.Captured = new List<string>();
        try { new BepInEx.Logging.ManualLogSource().LogWarning("bridge pending"); Assert.Equal(new[] { "bridge pending" }, BepInEx.Logging.ManualLogSource.Captured); }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
    }
}
