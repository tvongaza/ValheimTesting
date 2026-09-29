using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Object = UnityEngine.Object;

public sealed class RegistryTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithScene();
    public void Dispose() { Object.EndOfFrame(); _scope.Dispose(); }

    private static GameObject Item(string name, ItemDrop.ItemData.ItemType type = ItemDrop.ItemData.ItemType.Material, int stack = 50)
    {
        var item = new GameObject(name);
        var drop = item.AddComponent<ItemDrop>();
        drop.m_itemData.m_shared.m_name = "$item_" + name.ToLowerInvariant(); drop.m_itemData.m_shared.m_itemType = type; drop.m_itemData.m_shared.m_maxStackSize = stack;
        return item;
    }

    // A mod's registration, as mods write it in a postfix on ObjectDB.Awake and ObjectDB.CopyOtherDB.
    private static readonly GameObject s_modItem = Item("ModSword", ItemDrop.ItemData.ItemType.OneHandedWeapon, 1);
    private static void RegisterGuarded(ObjectDB db)
    {
        if (db.GetItemPrefab(s_modItem.name) != null) return; // already there: the lists survive from an earlier pass
        db.m_items.Add(s_modItem);
        db.UpdateRegisters();
    }
    private static void RegisterUnguarded(ObjectDB db) { db.m_items.Add(s_modItem); db.UpdateRegisters(); }

    [Fact] public void AGuardedRegistrationLeavesOneEntryThroughMenuWorldAndMenuAgain()
    {
        _scope.WithObjectDB(new[] { Item("Wood"), Item("Stone") });
        ObjectDB.AwakePostfix = RegisterGuarded; ObjectDB.CopyOtherDBPostfix = RegisterGuarded;
        var menu = _scope.LoadMainMenuObjectDB();
        Assert.Same(menu, ObjectDB.instance); Assert.Same(s_modItem, menu.GetItemPrefab("ModSword"));
        var world = _scope.LoadWorldObjectDB();
        Assert.Same(world, ObjectDB.instance);
        Assert.Equal(1, world.m_items.Count(i => i.name == "ModSword"));
        var menuAgain = _scope.LoadMainMenuObjectDB();
        Assert.Equal(1, menuAgain.m_items.Count(i => i.name == "ModSword")); Assert.Empty(menuAgain.FindDuplicateItems());
        Assert.Equal(new[] { "Wood", "Stone", "ModSword" }, menuAgain.m_items.Select(i => i.name));
    }

    [Fact] public void AnUnguardedRegistrationBreaksTheSecondMenuLoad()
    {
        var prefab = _scope.WithObjectDB(new[] { Item("Wood") });
        ObjectDB.AwakePostfix = RegisterUnguarded; ObjectDB.CopyOtherDBPostfix = RegisterUnguarded;
        // The menu's Awake runs on an empty database, then CopyOtherDB shares the prefab's lists: the item lands in them.
        var menu = _scope.LoadMainMenuObjectDB();
        Assert.Same(prefab.m_items, menu.m_items); Assert.Equal(1, prefab.m_items.Count(i => i.name == "ModSword"));
        // Back at the menu, the prefab's lists still hold the item; adding it again makes the index throw.
        Assert.Throws<ArgumentException>(() => _scope.LoadMainMenuObjectDB());
        Assert.Equal(new[] { "ModSword" }, prefab.FindDuplicateItems().Single().Names.Distinct());
        // The world's database starts from the game's own content, not the prefab's lists.
        ObjectDB.AwakePostfix = null;
        Assert.Equal(new[] { "Wood" }, _scope.LoadWorldObjectDB().m_items.Select(i => i.name));
    }

    [Fact] public void ZNetSceneAwakeIndexesPrefabsByHashAndReportsADuplicate()
    {
        var scene = new ZNetScene();
        scene.m_prefabs.AddRange(new[] { new GameObject("piece_a"), new GameObject("piece_b") });
        scene.m_nonNetViewPrefabs.Add(new GameObject("fx_a"));
        ZNetScene? awoken = null; ZNetScene.AwakePostfix = s => awoken = s;
        scene.Awake();
        Assert.Same(scene, ZNetScene.instance); Assert.Same(scene, awoken);
        Assert.Same(scene.m_prefabs[1], scene.GetPrefab("piece_b")); Assert.Same(scene.m_nonNetViewPrefabs[0], scene.GetPrefab("fx_a".GetStableHashCode()));
        Assert.True(scene.HasPrefab("piece_a".GetStableHashCode())); Assert.Null(scene.GetPrefab("missing"));
        scene.m_prefabs.Add(new GameObject("piece_a"));
        var duplicate = scene.FindDuplicateHashes().Single();
        Assert.Equal("piece_a".GetStableHashCode(), duplicate.Hash); Assert.Equal(new[] { "piece_a", "piece_a" }, duplicate.Names);
        var error = Assert.Throws<ArgumentException>(() => scene.Awake());
        Assert.Contains("'piece_a'", error.Message);
        Assert.Throws<ArgumentException>(() => scene.m_namedPrefabs.Add("piece_b".GetStableHashCode(), new GameObject("piece_b"))); // as a mod adding directly
    }

    // A registration another mod or a later pass has still to make, held back by the test: lookups miss and are recorded.
    [Fact] public void ALookupBeforeARegistrationIsMadeMissesAndIsRecorded()
    {
        var scene = new ZNetScene(); scene.m_prefabs.Add(new GameObject("Troll")); scene.Awake();
        scene.MarkNotYetRegistered("Troll");
        Assert.Null(scene.GetPrefab("Troll")); Assert.Equal(new[] { "Troll" }, scene.EarlyLookups);
        Assert.True(scene.HasPrefab("Troll".GetStableHashCode())); // the index itself is untouched
        scene.FinishRegistering();
        Assert.NotNull(scene.GetPrefab("Troll")); Assert.Single(scene.EarlyLookups);

        _scope.WithObjectDB(new[] { Item("Coins") });
        var db = _scope.LoadWorldObjectDB();
        db.MarkNotYetRegistered("Coins");
        Assert.Null(db.GetItemPrefab("Coins")); Assert.False(db.TryGetItemPrefab("Coins", out _));
        Assert.Equal(new[] { "Coins", "Coins" }, db.EarlyLookups);
        db.FinishRegistering(); Assert.NotNull(db.GetItemPrefab("Coins"));
        // The existing AddPrefab registers at once.
        using var scoped = new ValheimWorldScope().WithScene();
        var added = ZNetScene.instance!.AddPrefab("wood_pole2");
        Assert.Same(added, ZNetScene.instance.GetPrefab("wood_pole2")); Assert.Same(added, ZNetScene.instance.m_prefabs.Single());
    }

    [Fact] public void RecipesStatusEffectsAndItemQueriesFollowTheGame()
    {
        var sword = Item("SwordIron", ItemDrop.ItemData.ItemType.OneHandedWeapon, 1); var wood = Item("Wood");
        var recipe = ScriptableObject.CreateInstance<Recipe>(); recipe.m_item = sword.GetComponent<ItemDrop>();
        recipe.m_resources = new[] { new Piece.Requirement { m_resItem = wood.GetComponent<ItemDrop>(), m_amount = 10, m_amountPerLevel = 5 } };
        var rested = ScriptableObject.CreateInstance<StatusEffect>(); rested.name = "Rested";
        _scope.WithObjectDB(new[] { sword, wood }, new[] { recipe }, new[] { rested });
        var db = _scope.LoadWorldObjectDB();
        var copy = sword.GetComponent<ItemDrop>().m_itemData.Clone();
        Assert.Same(recipe, db.GetRecipe(copy)); Assert.Same(sword, db.GetItemPrefab(copy.m_shared));
        Assert.Equal(new[] { sword.GetComponent<ItemDrop>() }, db.GetAllItems(ItemDrop.ItemData.ItemType.OneHandedWeapon, "Sword"));
        Assert.Empty(db.GetAllItems(ItemDrop.ItemData.ItemType.OneHandedWeapon, "sword")); // ordinal, case-sensitive
        Assert.Same(rested, db.GetStatusEffect("Rested".GetStableHashCode()));
        rested.name = "Renamed"; // the hash was computed once and is kept, as in the game
        Assert.Same(rested, db.GetStatusEffect("Rested".GetStableHashCode())); Assert.Null(db.GetStatusEffect("Renamed".GetStableHashCode()));
        var requirement = recipe.m_resources[0];
        Assert.Equal(10, requirement.GetAmount(1)); Assert.Equal(5, requirement.GetAmount(2)); Assert.Equal(20, requirement.GetAmount(4)); Assert.Equal(22, requirement.GetAmount(5));
        Assert.True(copy.IsWeapon()); Assert.Same(copy.m_shared, sword.GetComponent<ItemDrop>().m_itemData.m_shared);
    }
}
