using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Object = UnityEngine.Object;

public sealed class GameplayTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithScene();
    public void Dispose() { Object.EndOfFrame(); _scope.Dispose(); }

    private static GameObject Item(string name, int maxStack, ItemDrop.ItemData.ItemType type = ItemDrop.ItemData.ItemType.Material)
    {
        var item = new GameObject(name);
        var shared = item.AddComponent<ItemDrop>().m_itemData.m_shared;
        shared.m_name = "$item_" + name; shared.m_maxStackSize = maxStack; shared.m_itemType = type;
        return item;
    }
    // Prefabs kept under an inactive object do not wake, as mods keep theirs; their copies wake.
    private static GameObject Asleep(GameObject prefab)
    {
        var holder = new GameObject("prefabs"); holder.SetActive(false);
        prefab.transform.SetParent(holder.transform);
        return prefab;
    }

    [Fact] public void UtilsKeepsTheGamesArithmeticAndNaming()
    {
        Assert.Equal("wood_wall", Utils.GetPrefabName("wood_wall(Clone)")); Assert.Equal("Troll", Utils.GetPrefabName("Troll (1)"));
        Assert.Equal(350f, Utils.FixDegAngle(-10f)); Assert.Equal(20f, Utils.DegDistance(350f, 10f));
        Assert.Equal(90f, Utils.YawFromDirection(new Vector3(1, 0, 0)), 3);
        Assert.Equal(5f, Utils.DistanceXZ(new Vector3(0, 100, 0), new Vector3(3, -7, 4)));
        Assert.Equal(-2, Utils.FloorToInt(-1.5f));
        Assert.Equal(3, Utils.RoundToInt(2.5f)); Assert.Equal(-1, Utils.RoundToInt(-1.5f)); // halves round up (Mathf gives 2 and -2)
        Assert.Equal(1, Utils.FloorToInt(0.999f)); // shifted by 64000 in float, 0.999 lands on 1 (Mathf gives 0)
        Assert.Equal(-70000, Utils.FloorToInt(-70000.5f)); // below -64000 it truncates toward zero (Mathf gives -70001)
        Assert.Equal(2f, Utils.Lerp(0f, 2f, 2f)); Assert.Equal(-2f, Utils.Lerp(0f, 2f, -1f)); // clamped above 1 only
        Assert.Equal(3, Utils.Mod(-1, 4)); Assert.Equal((short)32767, 40000.ClampToShort());
        Assert.True("Troll_Boss".CustomStartsWith("Troll")); Assert.False("troll".CustomStartsWith("Troll")); Assert.True("ruins_tower".CustomEndsWith("_tower"));
        var data = System.Text.Encoding.UTF8.GetBytes(new string('a', 1000));
        var packed = Utils.Compress(data);
        Assert.True(packed.Length < data.Length); Assert.Equal(0x1f, packed[0]); Assert.Equal(data, Utils.Decompress(packed)); // gzip
    }

    [Fact] public void UtilsWalksHierarchiesAsTheGameDoes()
    {
        var root = new GameObject("root"); var a = new GameObject("a"); var a1 = new GameObject("a1"); var deep = new GameObject("target");
        var b = new GameObject("b"); var shallow = new GameObject("target");
        a.transform.SetParent(root.transform); b.transform.SetParent(root.transform);
        a1.transform.SetParent(a.transform); deep.transform.SetParent(a1.transform); shallow.transform.SetParent(b.transform);
        Assert.Same(deep.transform, Utils.FindChild(root.transform, "target")); // depth first reaches a's grandchild first
        Assert.Same(shallow.transform, Utils.FindChild(root.transform, "target", Utils.IterativeSearchType.BreadthFirst));
        Assert.Null(Utils.FindChild(root.transform, "root")); // the parent itself is not a candidate
        root.AddComponent<BoxCollider>(); var onA = a.AddComponent<BoxCollider>(); b.AddComponent<BoxCollider>();
        b.SetActive(false);
        Assert.Equal(new[] { onA }, Utils.GetEnabledComponentsInChildren<BoxCollider>(root)); // not the root's own, nothing under an inactive object
        Assert.True(Utils.IsParent(deep.transform, root.transform)); Assert.False(Utils.IsParent(root.transform, deep.transform));
        var visited = new List<string>();
        Utils.IterateHierarchy(root, go => visited.Add(go.name), deepestFirst: true);
        Assert.Equal(new[] { "a1", "target", "a", "target", "b" }, visited); // deepest first applies to the first level only, as in the game
    }

    [Fact] public void TheHeightmapBuilderAnswersReadyOnlyForABuiltUnconsumedZone()
    {
        _scope.WithHeightmapBuilder();
        var world = new TerrainWorld(new Valheim.Testing.PlaneTerrain(33f));
        var builder = HeightmapBuilder.instance; var centre = new Vector3(64, 0, 0);
        Assert.False(builder.IsTerrainReady(centre, 4, 16f, false, world)); Assert.Equal(1, builder.QueuedCount);
        Assert.False(builder.IsTerrainReady(centre, 4, 16f, false, world)); Assert.Equal(1, builder.QueuedCount); // queued once
        Assert.Equal(1, builder.BuildQueued());
        Assert.True(builder.IsTerrainReady(centre, 4, 16f, false, world));
        var data = builder.RequestTerrainSync(centre, 4, 16f, false, world);
        Assert.Equal(25, data.m_baseHeights.Count); Assert.All(data.m_baseHeights, h => Assert.Equal(33f, h));
        Assert.False(builder.IsTerrainReady(centre, 4, 16f, false, world)); // handed out: asking again queues a new build
        Assert.False(builder.IsTerrainReady(centre, 4, 16f, false, new TerrainWorld(new Valheim.Testing.PlaneTerrain(33f)))); // another generator is another build
        Assert.Equal(2, builder.QueuedCount);
        var direct = builder.RequestTerrainSync(new Vector3(0, 0, 64), 2, 32f, false, world);
        Assert.Equal(9, direct.m_baseHeights.Count); // built on the spot when nothing is ready
    }

    private sealed class SplitWorld : WorldGenerator
    {
        public override Heightmap.Biome GetBiome(float wx, float wy) => wx < 0 ? Heightmap.Biome.Meadows : Heightmap.Biome.Mountain;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask) { mask = default; return biome == Heightmap.Biome.Meadows ? 10f : 50f; }
    }
    [Fact] public void HeightsBlendAcrossCornerBiomesAndADistantLodSmoothsSteps()
    {
        _scope.WithHeightmapBuilder();
        var world = new SplitWorld();
        // x from -32 to 32 in four steps: the west corners are Meadows (10 m), the east ones Mountain (50 m).
        var data = HeightmapBuilder.instance.RequestTerrainSync(Vector3.zero, 4, 16f, false, world);
        Assert.Equal(10f, data.m_baseHeights[0]); Assert.Equal(50f, data.m_baseHeights[4]); Assert.Equal(30f, data.m_baseHeights[2], 3);
        // A distant LOD takes each point's own biome, then evens out steps over 10 m inside the zone (not on its edge).
        var lod = HeightmapBuilder.instance.RequestTerrainSync(Vector3.zero, 4, 16f, true, world);
        Assert.Equal(new[] { 10f, 10f, 50f, 50f, 50f }, lod.m_baseHeights.Take(5)); // the edge row keeps the step
        Assert.Equal(new[] { 10f, 30f, 40f, 50f, 50f }, lod.m_baseHeights.Skip(5).Take(5));
    }

    [Fact] public void DropTablesRollChanceCountAndWeightAsTheGameDoes()
    {
        var coins = Item("Coins", 999); var ruby = Item("Ruby", 50);
        var table = new DropTable { m_dropMin = 3, m_dropMax = 3, m_oneOfEach = true };
        table.m_drops.Add(new DropTable.DropData { m_item = coins, m_stackMin = 2, m_stackMax = 2, m_weight = 1f });
        table.m_drops.Add(new DropTable.DropData { m_item = ruby, m_stackMin = 1, m_stackMax = 1, m_weight = 1f });
        UnityEngine.Random.InitState(3);
        var drops = table.GetDropList();
        // Three rolls, one of each: both entries once (coins in a stack of two), and the third roll finds nothing left.
        Assert.Equal(2, drops.Count(d => d == coins)); Assert.Equal(1, drops.Count(d => d == ruby));
        Assert.Empty(new DropTable { m_dropChance = 0f, m_drops = table.m_drops }.GetDropList());
        Assert.Empty(new DropTable().GetDropList()); Assert.True(new DropTable().IsEmpty());
        Assert.Same(table.m_drops, table.Clone().m_drops); // shallow, as the game's

        var items = new DropTable();
        items.m_drops.Add(new DropTable.DropData { m_item = coins, m_stackMin = 5, m_stackMax = 10, m_weight = 1f });
        var first = items.GetDropListItems();
        var stack = Assert.Single(first);
        Assert.InRange(stack.m_stack, 5, 10); Assert.Same(coins, stack.m_dropPrefab); Assert.NotSame(coins.GetComponent<ItemDrop>().m_itemData, stack);
        var next = new DropTable().GetDropListItems(); // the game's shared list: the next call clears it, whichever table makes it
        Assert.Same(first, next); Assert.Empty(first);
    }

    [Fact] public void DropCountsIncludeTheMaximumWeightsBiasThePickAndStackRangesFollowTheirKind()
    {
        var coins = Item("Coins", 50); var ruby = Item("Ruby", 50);
        var table = new DropTable { m_dropMin = 1, m_dropMax = 2 };
        table.m_drops.Add(new DropTable.DropData { m_item = coins, m_stackMin = 1, m_stackMax = 1, m_weight = 3f });
        table.m_drops.Add(new DropTable.DropData { m_item = ruby, m_stackMin = 1, m_stackMax = 1, m_weight = 1f });
        UnityEngine.Random.InitState(11);
        var rolls = Enumerable.Range(0, 2000).Select(_ => table.GetDropList()).ToList();
        Assert.Equal(new[] { 1, 2 }, rolls.Select(r => r.Count).Distinct().OrderBy(n => n)); // m_dropMax is included
        double coinShare = rolls.Sum(r => r.Count(d => d == coins)) / (double)rolls.Sum(r => r.Count);
        Assert.InRange(coinShare, 0.70, 0.80); // weight 3 of 4

        DropTable Single(int min, int max, bool dontScale) =>
            new() { m_drops = { new DropTable.DropData { m_item = coins, m_stackMin = min, m_stackMax = max, m_weight = 1f, m_dontScale = dontScale } } };
        var scaled = Single(1, 3, false); var unscaled = Single(1, 3, true);
        Assert.Equal(new[] { 1, 2, 3 }, Enumerable.Range(0, 400).Select(_ => scaled.GetDropList().Count).Distinct().OrderBy(n => n)); // rounded, both ends included
        Assert.Equal(new[] { 1, 2 }, Enumerable.Range(0, 400).Select(_ => unscaled.GetDropList().Count).Distinct().OrderBy(n => n)); // an int roll: m_stackMax excluded
        var itemStacks = Enumerable.Range(0, 400).Select(_ => Single(40, 60, false).GetDropListItems().Single().m_stack).ToList();
        Assert.Equal(40, itemStacks.Min()); Assert.Equal(50, itemStacks.Max()); // item stacks include the maximum, capped at the item's stack size
    }

    [Fact] public void AnInventoryTopsUpMatchingStacksBeforeUsingANewSlot()
    {
        var coins = Item("Coins", 50).GetComponent<ItemDrop>().m_itemData;
        var sword = Item("Sword", 1, ItemDrop.ItemData.ItemType.OneHandedWeapon).GetComponent<ItemDrop>().m_itemData;
        var inventory = new Inventory("chest", null, 2, 2);
        int changes = 0; inventory.m_onChanged = () => changes++;
        var forty = coins.Clone(); forty.m_stack = 40; inventory.AddItem(forty);
        var thirty = coins.Clone(); thirty.m_stack = 30; inventory.AddItem(thirty);
        Assert.Equal(new[] { 50, 20 }, inventory.GetAllItems().Select(i => i.m_stack)); Assert.Equal(70, inventory.CountItems("$item_Coins"));
        Assert.Equal(new Vector2i(0, 1), forty.m_gridPos); // materials fill from the bottom row
        var weapon = sword.Clone(); inventory.AddItem(weapon);
        Assert.Equal(new Vector2i(0, 0), weapon.m_gridPos); // weapons from the top
        Assert.Equal(3, changes); Assert.Equal(1, inventory.GetEmptySlots());
    }

    [Fact] public void AContainerAddsItsDefaultItemsOnceAndOnlyWhenOwned()
    {
        _scope.WithZdos();
        var coins = Item("Coins", 50);
        var prefab = Asleep(ZNetScene.instance!.AddPrefab("TreasureChest"));
        var container = prefab.AddComponent<Container>();
        container.m_defaultItems.m_drops.Add(new DropTable.DropData { m_item = coins, m_stackMin = 20, m_stackMax = 20, m_weight = 1f });
        Assert.Null(container.GetInventory()); // the prefab does not wake
        var chest = Object.Instantiate(prefab, new Vector3(1, 30, 1), default(Quaternion));
        var inventory = chest.GetComponent<Container>().GetInventory();
        Assert.Equal(20, inventory.CountItems("$item_Coins"));
        Assert.Equal(1, chest.GetComponent<ZNetView>().GetZDO().GetInt(ZDOVars.s_addedDefaultItems));
        Assert.NotSame(container.m_defaultItems, chest.GetComponent<Container>().m_defaultItems); // the copy has its own table

        // A chest another peer owns, and one whose ZDO already had its items: both wake with an empty inventory.
        var theirs = Object.Instantiate(prefab, prefab.transform.parent!); // still asleep under the inactive parent
        theirs.GetComponent<ZNetView>().GetZDO().SetOwner(99);
        theirs.transform.SetParent(null);
        Assert.Equal(0, theirs.GetComponent<Container>().GetInventory().NrOfItems());
        Assert.Equal(0, theirs.GetComponent<ZNetView>().GetZDO().GetInt(ZDOVars.s_addedDefaultItems));
        var loaded = Object.Instantiate(prefab, prefab.transform.parent!);
        loaded.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_addedDefaultItems, 1);
        loaded.transform.SetParent(null);
        Assert.Equal(0, loaded.GetComponent<Container>().GetInventory().NrOfItems());
    }

    [Fact] public void APickableReadsItsZdoAndPickingHidesItsPartOrDestroysIt()
    {
        _scope.WithZdos();
        var berries = Item("Raspberry", 50);
        var bushPrefab = Asleep(ZNetScene.instance!.AddPrefab("RaspberryBush"));
        var fruit = new GameObject("fruit"); fruit.transform.SetParent(bushPrefab.transform);
        var pickable = bushPrefab.AddComponent<Pickable>(); pickable.m_itemPrefab = berries; pickable.m_hideWhenPicked = fruit;
        var bush = Object.Instantiate(bushPrefab, new Vector3(1, 30, 1), default(Quaternion));
        var picked = bush.GetComponent<Pickable>();
        Assert.Same(bush.transform.GetChild(0).gameObject, picked.m_hideWhenPicked); // the copy hides its own part
        Assert.Equal("$item_Raspberry", picked.GetHoverName()); Assert.True(picked.CanBePicked());
        picked.SetPicked(true);
        Assert.False(picked.m_hideWhenPicked!.activeSelf); Assert.False(picked.CanBePicked()); Assert.Equal("", picked.GetHoverText());
        Assert.Equal(1, bush.GetComponent<ZNetView>().GetZDO().GetInt(ZDOVars.s_picked));
        Object.EndOfFrame(); // it hides a part, so it stays: not destroyed and its ZDO not queued
        Assert.False(bush == null); Assert.Contains(bush, ZNetScene.instance.Live); Assert.Empty(ZDOMan.instance!.DestroyQueue);

        var once = Object.Instantiate(bushPrefab, new Vector3(3, 30, 3), default(Quaternion));
        var single = once.GetComponent<Pickable>(); single.m_hideWhenPicked = null;
        single.SetPicked(true);
        Assert.DoesNotContain(once, ZNetScene.instance.Live); Object.EndOfFrame(); Assert.True(once == null); // no respawn, nothing to hide: destroyed

        var disabled = Object.Instantiate(bushPrefab, new Vector3(5, 30, 5), default(Quaternion)).GetComponent<Pickable>();
        disabled.m_hideWhenPicked = null; disabled.SetEnabled(false);
        Assert.False(disabled.CanBePicked()); Assert.Equal(0, disabled.GetEnabled);
        Assert.Equal(1, disabled.GetComponent<ZNetView>().GetZDO().GetInt(ZDOVars.s_enabled)); // as the game: the component's own flag is saved
    }

    [Fact] public void APickableWakingWithAPickedZdoHidesItsPartOrIsRemoved()
    {
        _scope.WithZdos();
        var berries = Item("Raspberry", 50);
        var withPart = Asleep(ZNetScene.instance!.AddPrefab("RaspberryBush"));
        var fruit = new GameObject("fruit"); fruit.transform.SetParent(withPart.transform);
        var bush = withPart.AddComponent<Pickable>(); bush.m_itemPrefab = berries; bush.m_hideWhenPicked = fruit;
        var plain = Asleep(ZNetScene.instance.AddPrefab("Mushroom"));
        plain.AddComponent<Pickable>().m_itemPrefab = berries;

        // Copies made asleep (under the inactive holder), their ZDOs marked picked as a load would, then woken.
        var loadedBush = Object.Instantiate(withPart, withPart.transform.parent!);
        loadedBush.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_picked, true);
        loadedBush.transform.SetParent(null);
        var picked = loadedBush.GetComponent<Pickable>();
        Assert.True(picked.GetPicked()); Assert.False(picked.m_hideWhenPicked!.activeSelf); Assert.False(picked.CanBePicked());
        var loadedMushroom = Object.Instantiate(plain, plain.transform.parent!);
        var mushroomZdo = loadedMushroom.GetComponent<ZNetView>().GetZDO();
        mushroomZdo.Set(ZDOVars.s_picked, true);
        loadedMushroom.transform.SetParent(null);
        Assert.DoesNotContain(loadedMushroom, ZNetScene.instance.Live); Assert.Contains(mushroomZdo, ZDOMan.instance!.DestroyQueue);
        Object.EndOfFrame();
        Assert.True(loadedMushroom == null); Assert.False(loadedBush == null); // neither respawning nor hiding a part: removed
    }

    [Fact] public void ASpawnAreaPicksByWeight()
    {
        var area = new GameObject("spawner").AddComponent<SpawnArea>();
        Assert.Null(area.SelectWeightedPrefab());
        var greyling = new SpawnArea.SpawnData { m_prefab = new GameObject("Greyling"), m_weight = 1f };
        var troll = new SpawnArea.SpawnData { m_prefab = new GameObject("Troll"), m_weight = 0f };
        area.m_prefabs.Add(greyling); area.m_prefabs.Add(troll); // the zero-weight entry last: a pick that fell back to the last entry would find it
        UnityEngine.Random.InitState(1);
        Assert.All(Enumerable.Range(0, 20).Select(_ => area.SelectWeightedPrefab()), pick => Assert.Same(greyling, pick));
        Assert.Equal(20, area.m_maxTotal); Assert.Equal(15f, area.m_levelupChance);
    }
}
