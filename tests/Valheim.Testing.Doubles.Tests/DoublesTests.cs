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
        UnityEngine.Object.EndOfFrame();
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
        var ourZdo = ours.View.GetZDO();
        ZNetScene.instance.Destroy(ours); ZNetScene.instance.Destroy(theirs);
        Assert.Null(ZNetScene.instance.FindInstance(ourZdo)); Assert.Empty(ZNetScene.instance.Live);
        Assert.Null(ours.View.GetZDO()); Assert.False(ours.View.IsValid()); // the view let go of its ZDO, as the game's ResetZDO does
        Assert.False(ours == null); Assert.False(ours.Destroyed); // the object itself goes at the end of the frame
        Assert.Equal(2, UnityEngine.Object.EndOfFrame());
        Assert.True(ours.Destroyed); Assert.True(theirs.Destroyed);
        Assert.Equal(new[] { ourZdo }, ZDOMan.instance!.DestroyQueue);
        Assert.Null(ZNetScene.instance.GetPrefab("missing"));
        Assert.Null(prefab.GetComponent<ZNetView>());
    }
    [Fact] public void ADestroyedObjectEqualsNullOnlyThroughUnitysOperators()
    {
        var live = new UnityEngine.GameObject("stone");
        var destroyed = new UnityEngine.GameObject("stone");
        UnityEngine.Object.DestroyImmediate(destroyed);
        Assert.True(destroyed == null); Assert.False(destroyed != null); Assert.True(destroyed!.Equals(null)); // the reference itself is not null
        Assert.False(destroyed is null); Assert.Same(destroyed, destroyed ?? live); // is null and ?? see the reference
        // The game's player throws a NullReferenceException here; the double's message names the destroyed object.
        Assert.Contains("GameObject 'stone' was destroyed", Assert.Throws<NullReferenceException>(() => destroyed?.name).Message);
        Assert.Contains("'stone' was destroyed", Assert.Throws<NullReferenceException>(() => destroyed!.GetComponent<ZNetView>()).Message);
        bool entered = false;
        if (destroyed) entered = true;
        Assert.False(entered);
        // A live object and a real null reference behave as before.
        bool exists = live; Assert.True(exists); Assert.False(live == null); Assert.True(live != null); Assert.False(live.Equals(null));
        Assert.Equal("stone", live.name); Assert.False(live == destroyed);
        UnityEngine.GameObject? none = null; bool noneExists = none;
        Assert.True(none == null); Assert.False(noneExists);
    }
    [Fact] public void DestroyWaitsForTheEndOfTheFrameAndDestroyImmediateDoesNot()
    {
        var beam = new UnityEngine.GameObject("beam");
        UnityEngine.Object.Destroy(beam); UnityEngine.Object.Destroy(beam);
        Assert.False(beam == null); Assert.Equal("beam", beam.name); // still alive this frame, as in Unity
        Assert.Equal(1, UnityEngine.Object.EndOfFrame());
        Assert.True(beam == null); Assert.True(beam!.Destroyed);
        Assert.Equal(0, UnityEngine.Object.EndOfFrame());
        var pole = new UnityEngine.GameObject("pole");
        UnityEngine.Object.Destroy(pole); UnityEngine.Object.DestroyImmediate(pole);
        Assert.True(pole == null); Assert.Equal(0, UnityEngine.Object.EndOfFrame());
        UnityEngine.Object.Destroy(null); UnityEngine.Object.DestroyImmediate(null);
    }
    [Fact] public void AScopeEndsTheFrameSoNoDestroyOutlivesTheTest()
    {
        var beam = new UnityEngine.GameObject("beam");
        using (new Valheim.Testing.Doubles.ValheimWorldScope()) UnityEngine.Object.Destroy(beam);
        Assert.True(beam.Destroyed); Assert.Equal(0, UnityEngine.Object.EndOfFrame());
    }
    [Fact] public void ComponentsAreDestroyedWithTheirObjectAndKeepOnlyTheirOwnFields()
    {
        var prefab = ZNetScene.instance!.AddPrefab("wood_wall", health: 400f);
        var wall = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(3, 30, 3), UnityEngine.Quaternion.Euler(0, 0, 0));
        var view = wall.GetComponent<ZNetView>(); var wear = wall.GetComponent<WearNTear>();
        Assert.Same(wall, view.gameObject); Assert.Equal("wood_wall", wear.name); Assert.Same(view, wear.GetComponent<ZNetView>());
        UnityEngine.Object.Destroy(wall);
        UnityEngine.Object.EndOfFrame();
        Assert.True(view == null); Assert.True(wear == null);
        Assert.Equal(400f, wear!.m_health); Assert.NotNull(view!.GetZDO()); // the component's own C# members still answer, as in Unity
        Assert.Contains("ZNetView 'wood_wall' was destroyed", Assert.Throws<NullReferenceException>(() => view.gameObject).Message);
        Assert.Contains("WearNTear 'wood_wall' was destroyed", Assert.Throws<NullReferenceException>(() => wear?.GetComponent<ZNetView>()).Message);
        // A plain Destroy does not touch the scene, as in the game: the destroyed view is still found, and only == null sees it.
        Assert.Contains(wall, ZNetScene.instance.Live);
        var found = ZNetScene.instance.FindInstance(view.GetZDO());
        Assert.Same(view, found); Assert.True(found == null);
        // Destroying one component leaves the object; GetComponent then finds none.
        var door = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(4, 30, 4), UnityEngine.Quaternion.Euler(0, 0, 0));
        UnityEngine.Object.DestroyImmediate(door.GetComponent<WearNTear>());
        Assert.Null(door.GetComponent<WearNTear>()); Assert.False(door == null); Assert.NotNull(door.GetComponent<ZNetView>());
    }
    [Fact] public void DestroyingKeepsObjectsAsDistinctKeysAndListEntries()
    {
        var a = new UnityEngine.GameObject("same"); var b = new UnityEngine.GameObject("same");
        var byObject = new Dictionary<UnityEngine.GameObject, int> { [a] = 1, [b] = 2 };
        var list = new List<UnityEngine.GameObject> { a, b };
        Assert.False(a == b); Assert.NotEqual(a, b);
        UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b);
        Assert.False(a == b); Assert.NotEqual(a, b); // two destroyed objects are still two objects, as in Unity
        Assert.Equal(1, byObject[a]); Assert.Equal(2, byObject[b]);
        Assert.Equal(1, list.IndexOf(b)); Assert.True(list.Remove(a)); Assert.Same(b, Assert.Single(list));
    }
    // Mod code in the shape the modding wiki warns about: ?. does not see a destroyed object, == null does.
    private static class PieceHealth
    {
        public static float WithConditionalAccess(IEnumerable<UnityEngine.GameObject> pieces) =>
            pieces.Sum(piece => piece?.GetComponent<WearNTear>()?.m_health ?? 0f);
        public static float WithUnityNullCheck(IEnumerable<UnityEngine.GameObject> pieces)
        {
            float total = 0f;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;
                var wear = piece.GetComponent<WearNTear>();
                if (wear != null) total += wear.m_health;
            }
            return total;
        }
    }
    [Fact] public void AConditionalAccessOnADestroyedObjectThrowsWhereAnExplicitNullCheckSkipsIt()
    {
        var prefab = ZNetScene.instance!.AddPrefab("stone_wall", health: 1500f);
        var kept = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(1, 30, 1), UnityEngine.Quaternion.Euler(0, 0, 0));
        var removed = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(2, 30, 2), UnityEngine.Quaternion.Euler(0, 0, 0));
        var pieces = new[] { kept, removed };
        Assert.Equal(3000f, PieceHealth.WithConditionalAccess(pieces)); Assert.Equal(3000f, PieceHealth.WithUnityNullCheck(pieces));
        UnityEngine.Object.Destroy(removed); UnityEngine.Object.EndOfFrame();
        Assert.Throws<NullReferenceException>(() => PieceHealth.WithConditionalAccess(pieces));
        Assert.Equal(1500f, PieceHealth.WithUnityNullCheck(pieces));
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
    [Fact] public void AScopeRestoresEveryWorldSingleton()
    {
        var before = WorldGenerator.instance; var zdos = ZDOMan.instance;
        using (var scope = new Valheim.Testing.Doubles.ValheimWorldScope())
        {
            scope.WithTerrain(new Valheim.Testing.PlaneTerrain(42f)).WithZdos().AsServer();
            var hm = scope.RegisterHeightmap(new Vector2s(0, 0), width: 4);
            var log = scope.CaptureLog();
            Assert.Equal(42f, WorldGenerator.instance!.GetHeight(10, 10)); Assert.NotSame(zdos, ZDOMan.instance);
            Assert.Same(hm, Heightmap.Registered); Assert.True(ZNet.instance.Server); Assert.Same(log, BepInEx.Logging.ManualLogSource.Captured);
        }
        Assert.Same(before, WorldGenerator.instance); Assert.Same(zdos, ZDOMan.instance);
        Assert.Null(Heightmap.Registered); Assert.False(ZNet.instance.Server); Assert.Null(BepInEx.Logging.ManualLogSource.Captured);
    }
    [Fact] public void AScopeRestoresTheOriginalNetworkAfterAFailingTest()
    {
        var net = new ZNet { Server = true }; net.Peers.Add(3, new ZNetPeer()); var rpc = new ZRoutedRpc();
        var (priorNet, priorRpc) = (ZNet.instance, ZRoutedRpc.instance);
        ZNet.instance = net; ZRoutedRpc.instance = rpc;
        try
        {
            Assert.Throws<InvalidOperationException>(FailInsideTheScope);
            Assert.Same(net, ZNet.instance); Assert.Same(rpc, ZRoutedRpc.instance);
            Assert.True(net.IsServer()); Assert.Equal(new[] { 3L }, net.Peers.Keys);
        }
        finally { ZNet.instance = priorNet; ZRoutedRpc.instance = priorRpc; }

        void FailInsideTheScope()
        {
            using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithNetwork(server: false);
            Assert.NotSame(net, ZNet.instance); Assert.NotSame(rpc, ZRoutedRpc.instance);
            Assert.Empty(ZNet.instance.Peers); Assert.False(ZNet.instance.IsServer());
            ZNet.instance.Peers.Add(7, new ZNetPeer());
            throw new InvalidOperationException("test failed");
        }
    }
    // The scope restores references, not contents: a peer added to the network it did not install stays.
    [Fact] public void AScopeDoesNotUndoChangesInsideObjectsItDidNotInstall()
    {
        var peers = ZNet.instance.Peers.Count;
        try
        {
            using (new Valheim.Testing.Doubles.ValheimWorldScope()) ZNet.instance.Peers.Add(99, new ZNetPeer());
            Assert.Equal(peers + 1, ZNet.instance.Peers.Count);
        }
        finally { ZNet.instance.Peers.Remove(99); }
    }
    [Fact] public void ACommandRegistersByLowerCaseNameAndRunsWithItsArguments()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithCommands();
        Terminal.ConsoleEventArgs? seen = null;
        new Terminal.ConsoleCommand("Mod_Mark", "Adds a mark.", args => { seen = args; args.Context.AddString("marked " + args.ArgsAll); }, isCheat: true);
        var console = new Terminal();
        console.TryRunCommand("mod_mark add  10 -2.5");
        Assert.Equal(new[] { "mod_mark", "add", "10", "-2.5" }, seen!.Args);
        Assert.Equal("add  10 -2.5", seen.ArgsAll); Assert.Equal(10, seen.TryParameterInt(2)); Assert.Equal(-2.5f, seen.TryParameterFloat(3));
        Assert.Same(console, seen.Context); Assert.Equal(new[] { "marked add  10 -2.5" }, console.Output);
        Assert.True(Terminal.commands["mod_mark"].IsCheat);
    }
    [Fact] public void AnUnknownCommandOrAFailedFailableIsPrinted()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithCommands();
        new Terminal.ConsoleCommand("fails", "", (Terminal.ConsoleEventFailable)(_ => "no world"));
        new Terminal.ConsoleCommand("works", "", (Terminal.ConsoleEventFailable)(_ => true));
        var console = new Terminal();
        console.TryRunCommand("nothing"); console.TryRunCommand("nothing", silentFail: true); console.TryRunCommand("fails"); console.TryRunCommand("works");
        Assert.Equal(new[] { "Unknown command: nothing", "Error executing command: no world" }, console.Output);
    }
    [Fact] public void AScopeGivesItsOwnCommandTableAndLocalPlayerAndRestoresThem()
    {
        var commands = Terminal.commands; var player = Player.m_localPlayer;
        using (var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithCommands())
        {
            var local = scope.WithLocalPlayer(new UnityEngine.Vector3(1, 2, 3));
            new Terminal.ConsoleCommand("scoped", "", _ => { });
            Assert.Same(local, Player.m_localPlayer); Assert.Equal(3f, Player.m_localPlayer!.transform.position.z);
            Assert.True(Terminal.commands.ContainsKey("scoped")); Assert.NotSame(commands, Terminal.commands);
        }
        Assert.Same(commands, Terminal.commands); Assert.Same(player, Player.m_localPlayer); Assert.False(Terminal.commands.ContainsKey("scoped"));
    }
    [Fact] public void APeersCharacterIsFoundByItsZdoId()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithNetwork().WithZdos();
        var character = ZDOMan.instance!.CreateNewZDO(new UnityEngine.Vector3(5, 0, 7), 1);
        ZNet.instance.Peers.Add(12, new ZNetPeer { m_characterID = character.m_uid });
        ZNet.instance.Peers.Add(13, new ZNetPeer());
        var peers = ZNet.instance.GetPeers();
        Assert.Equal(new[] { 12L, 13L }, peers.Select(p => p.m_uid));
        Assert.Same(character, ZDOMan.instance.GetZDO(peers[0].m_characterID));
        Assert.True(peers[1].m_characterID.IsNone()); Assert.Null(ZDOMan.instance.GetZDO(ZDOID.None));
    }
    [Fact] public void RoutedRpcsAreRecordedAndDeliveredToTheirHandlers()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithNetwork();
        var got = new List<(long, int, string)>();
        ZRoutedRpc.instance.Register<int, string>("Mod_Ping", (sender, n, text) => got.Add((sender, n, text)));
        ZRoutedRpc.instance.Register("Mod_Fail", _ => throw new InvalidOperationException("handler failed"));
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Ping", 3, "hi");
        ZRoutedRpc.instance.InvokeRoutedRPC("Mod_Ping", 4, "server");
        Assert.Equal(new[] { (0L, "Mod_Ping"), (42L, "Mod_Ping") }, ZRoutedRpc.instance.Invoked.Select(x => (x.Target, x.Method)));
        ZRoutedRpc.instance.Deliver(7, "Mod_Ping", 5, "back");
        Assert.Equal(new[] { (7L, 5, "back") }, got);
        Assert.Equal("handler failed", Assert.Throws<InvalidOperationException>(() => ZRoutedRpc.instance.Deliver(7, "Mod_Fail")).Message);
        Assert.Throws<InvalidOperationException>(() => ZRoutedRpc.instance.Deliver(7, "Mod_Unknown"));
    }
    [Fact] public void JotunnRpcsAreKeptByNameAndTheScopeGivesAFreshManager()
    {
        var manager = Jotunn.Managers.NetworkManager.Instance;
        using (new Valheim.Testing.Doubles.ValheimWorldScope().WithNetwork())
        {
            var a = Jotunn.Managers.NetworkManager.Instance.AddRPC("A", (_, _) => null!, (_, _) => null!);
            var b = Jotunn.Managers.NetworkManager.Instance.AddRPC("B", (_, _) => null!, (_, _) => null!);
            Assert.Same(a, Jotunn.Managers.NetworkManager.Instance.Rpcs["A"]); Assert.Same(b, Jotunn.Managers.NetworkManager.Instance.Rpc);
            a.SendPackage(new List<ZNetPeer> { new() { m_uid = 5 }, new() { m_uid = 6 } }, new ZPackage());
            Assert.Equal(new[] { 5L, 6L }, a.Sent.Select(s => s.Peer)); Assert.Empty(b.Sent);
            Assert.NotSame(manager, Jotunn.Managers.NetworkManager.Instance);
        }
        Assert.Same(manager, Jotunn.Managers.NetworkManager.Instance);
    }
    [Fact] public void LoadedZonesAreFoundByPositionAndAReloadReplacesTheZone()
    {
        var before = Heightmap.s_heightmaps;
        using (var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithTerrain(new Valheim.Testing.PlaneTerrain(30f)).WithZdos())
        {
            var west = scope.RegisterHeightmap(new Vector2s(0, 0)); var east = scope.RegisterHeightmap(new Vector2s(1, 0));
            Assert.Same(west, Heightmap.FindHeightmap(new UnityEngine.Vector3(10, 0, 0)));
            Assert.Same(east, Heightmap.FindHeightmap(new UnityEngine.Vector3(70, 0, 0)));
            Assert.Null(Heightmap.FindHeightmap(new UnityEngine.Vector3(200, 0, 0)));
            Assert.Same(east.m_terrainComp, TerrainComp.FindTerrainCompiler(new UnityEngine.Vector3(64, 0, 0)));
            Assert.Equal(2, Heightmap.GetAllHeightmaps().Count); Assert.Same(east, Heightmap.Registered);
            var reloaded = scope.RegisterHeightmap(new Vector2s(0, 0));
            Assert.Equal(new[] { east, reloaded }, Heightmap.GetAllHeightmaps());
            scope.UnloadHeightmap(new Vector2s(1, 0));
            Assert.Null(TerrainComp.FindTerrainCompiler(new UnityEngine.Vector3(64, 0, 0)));
        }
        Assert.Same(before, Heightmap.s_heightmaps);
    }
    [Fact] public void AFootprintCheckNamesChangesOutsideItIncludingFlagOnlyOnes()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithTerrain(new Valheim.Testing.PlaneTerrain(30f)).WithZdos();
        var tc = scope.RegisterHeightmap(new Vector2s(0, 0)).m_terrainComp!;
        for (int i = 0; i < tc.m_levelDelta.Length; i++) { tc.m_levelDelta[i] = 0.5f; tc.m_paintMask[i] = new UnityEngine.Color(0.2f, 0.4f, 0.6f, 0.3f); }
        var before = Valheim.Testing.Doubles.TerrainSnapshot.Of(tc);
        Assert.Throws<Valheim.Testing.Doubles.TerrainAssertException>(() => Valheim.Testing.Doubles.TerrainAssert.OnlyChangedWithin(before, tc, (x, z) => true));
        Valheim.Testing.Doubles.TerrainAssert.OnlyChangedWithin(before, tc, (x, z) => false, requireChange: false);
        int centre = 32 * 65 + 32, corner = 0;
        tc.m_levelDelta[centre] = 2f;
        Valheim.Testing.Doubles.TerrainAssert.OnlyChangedWithin(before, tc, (x, z) => Math.Abs(x) < 2 && Math.Abs(z) < 2);
        tc.m_modifiedPaint[corner] = true; // A flag alone is a change.
        var error = Assert.Throws<Valheim.Testing.Doubles.TerrainAssertException>(() => Valheim.Testing.Doubles.TerrainAssert.OnlyChangedWithin(before, tc, (x, z) => Math.Abs(x) < 2 && Math.Abs(z) < 2));
        Assert.Contains("at (-32.00, -32.00): paint flag False->True", error.Message);
        Assert.Throws<Valheim.Testing.Doubles.TerrainAssertException>(() => Valheim.Testing.Doubles.TerrainAssert.Unchanged(before, tc));
    }
    [Fact] public void ASeamCheckFailsWhenOnlyOneZoneWasWrittenAndPassesWhenBothAgree()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithTerrain(new Valheim.Testing.PlaneTerrain(30f)).WithZdos();
        var west = scope.RegisterHeightmap(new Vector2s(0, 0)); var east = scope.RegisterHeightmap(new Vector2s(1, 0));
        // The shared column is x = 32: the west zone's last vertex column and the east zone's first.
        for (int z = 0; z <= 64; z++) west.m_terrainComp!.m_levelDelta[z * 65 + 64] = 1.5f;
        west.RebuildTerrain(); east.RebuildTerrain();
        var error = Assert.Throws<Valheim.Testing.Doubles.TerrainAssertException>(() => Valheim.Testing.Doubles.TerrainAssert.SeamAgrees(west, east));
        Assert.Contains("65 of 65 shared vertices", error.Message);
        for (int z = 0; z <= 64; z++) east.m_terrainComp!.m_levelDelta[z * 65] = 1.5f;
        east.RebuildTerrain();
        Valheim.Testing.Doubles.TerrainAssert.SeamAgrees(west, east);
    }
    [Fact] public void TerrainChecksRefuseComparisonsThatCouldPassByAccident()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithTerrain(new Valheim.Testing.PlaneTerrain(30f)).WithZdos();
        var west = scope.RegisterHeightmap(new Vector2s(0, 0)); var east = scope.RegisterHeightmap(new Vector2s(1, 0));
        var diagonal = scope.RegisterHeightmap(new Vector2s(1, 1)); var small = scope.RegisterHeightmap(new Vector2s(0, 1), width: 32);
        foreach (var hm in new[] { west, east, diagonal, small }) hm.RebuildTerrain();
        var snapshot = Valheim.Testing.Doubles.TerrainSnapshot.Of(west.m_terrainComp!);
        Assert.Throws<ArgumentException>(() => snapshot.ChangesIn(east.m_terrainComp!));         // another zone
        Assert.Throws<ArgumentException>(() => Valheim.Testing.Doubles.TerrainAssert.SeamAgrees(west, west));
        Assert.Throws<ArgumentException>(() => Valheim.Testing.Doubles.TerrainAssert.SeamAgrees(west, diagonal)); // a corner only
        Assert.Throws<ArgumentException>(() => Valheim.Testing.Doubles.TerrainAssert.SeamAgrees(west, small));    // different grids
        Assert.Throws<ArgumentOutOfRangeException>(() => Valheim.Testing.Doubles.TerrainAssert.SeamAgrees(west, east, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Valheim.Testing.Doubles.TerrainAssert.SeamAgrees(west, east, -1f));
        west.LastRenderedHeights![64] = float.NaN;                                                 // a shared vertex
        Assert.Contains("not finite", Assert.Throws<Valheim.Testing.Doubles.TerrainAssertException>(() => Valheim.Testing.Doubles.TerrainAssert.SeamAgrees(west, east)).Message);
    }
    [Fact] public void TerrainWorldMapsTheToolkitsBiomes()
    {
        var world = new Valheim.Testing.Doubles.TerrainWorld(new Valheim.Testing.PlaneTerrain(35f));
        Assert.Equal(35f, world.GetHeight(0, 0)); Assert.Equal(Heightmap.Biome.Meadows, world.GetBiome(0, 0));
    }
    [Fact] public void LogLinesCanBeCaptured()
    {
        BepInEx.Logging.ManualLogSource.Captured = new List<string>();
        try { new BepInEx.Logging.ManualLogSource().LogWarning("bridge pending"); Assert.Equal(new[] { "bridge pending" }, BepInEx.Logging.ManualLogSource.Captured); }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
    }
}
