// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// The game's registries, keyed by a name's stable hash: ObjectDB (items, recipes, status effects) and ZNetScene's
// prefabs, with the lifecycle mods register in (ObjectDB wakes at the main menu and again with each world) and the
// items, recipes, status effects and pieces they hold. A test can hold entries back as not registered yet.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;

/// <summary>
/// Items, recipes and status effects, as the game's ObjectDB (1.0.16). <see cref="Awake"/> makes it the instance and
/// indexes its items by the stable hash of their names; <see cref="CopyOtherDB"/> takes the other database's lists
/// themselves (not copies), as the main menu does with the ObjectDB prefab, and indexes again. The index is built with
/// a dictionary's Add, as the game's, so two items with the same name make the next <see cref="UpdateRegisters"/>
/// throw. <see cref="AwakePostfix"/> and <see cref="CopyOtherDBPostfix"/> run where a mod's Harmony postfix on those
/// methods would. <see cref="DeclareVanilla"/> stands for the game's own content; <see cref="LoadMainMenu"/> and
/// <see cref="LoadWorld"/> run the game's two passes over it.
/// </summary>
public partial class ObjectDB : MonoBehaviour
{
    internal static ObjectDB? m_instance;
    public static ObjectDB? instance => m_instance;

    public List<StatusEffect> m_StatusEffects = new();
    public List<GameObject> m_items = new();
    public List<Recipe> m_recipes = new();
    private readonly Dictionary<int, GameObject> m_itemByHash = new();
    private readonly Dictionary<ItemDrop.ItemData.SharedData, GameObject> m_itemByData = new();

    /// <summary>Runs at the end of <see cref="Awake"/>, as a Harmony postfix would. <c>ValheimWorldScope</c> restores it.</summary>
    [TestOnly] public static Action<ObjectDB>? AwakePostfix;
    /// <summary>Runs at the end of <see cref="CopyOtherDB"/>, as a Harmony postfix would. <c>ValheimWorldScope</c> restores it.</summary>
    [TestOnly] public static Action<ObjectDB>? CopyOtherDBPostfix;

    /// <summary>
    /// Stable hashes of items a test holds back as not registered yet, standing for a registration another mod or a later
    /// pass has still to make: name and hash lookups return null for them and are recorded in <see cref="EarlyLookups"/>.
    /// The game's registries hold every registered entry and look it up directly.
    /// </summary>
    [TestOnly] public readonly HashSet<int> NotYetRegistered = new();
    /// <summary>Every lookup that returned null only because its item was held back as not registered yet.</summary>
    [TestOnly] public readonly List<string> EarlyLookups = new();
    [TestOnly] public void MarkNotYetRegistered(params string[] names) { foreach (var name in names) NotYetRegistered.Add(name.GetStableHashCode()); }
    [TestOnly] public void FinishRegistering() => NotYetRegistered.Clear();

    // The game's own content as it was built, which the world pass starts from (DeclareVanilla's database only).
    private (GameObject[] Items, Recipe[] Recipes, StatusEffect[] Effects)? m_vanilla;

    /// <summary>
    /// The game's own ObjectDB content: the prefab the main menu copies and the world scene's database both start from
    /// these items, recipes and status effects. Returns the prefab (its lists are the ones the main menu shares). No
    /// ObjectDB is the instance until a pass runs.
    /// </summary>
    [TestOnly] public static ObjectDB DeclareVanilla(IEnumerable<GameObject>? items = null, IEnumerable<Recipe>? recipes = null, IEnumerable<StatusEffect>? statusEffects = null)
    {
        var vanilla = (Items: items?.ToArray() ?? new GameObject[0], Recipes: recipes?.ToArray() ?? new Recipe[0], Effects: statusEffects?.ToArray() ?? new StatusEffect[0]);
        m_instance = null;
        return new ObjectDB { m_items = vanilla.Items.ToList(), m_recipes = vanilla.Recipes.ToList(), m_StatusEffects = vanilla.Effects.ToList(), m_vanilla = vanilla };
    }
    /// <summary>
    /// The main menu's pass, as the game runs it each time the menu loads: a new ObjectDB is added (its Awake and
    /// <see cref="AwakePostfix"/> run on empty lists), then <see cref="CopyOtherDB"/> takes the prefab's lists themselves
    /// (and <see cref="CopyOtherDBPostfix"/> runs). What a mod adds here lands in the prefab's lists, so it is still there
    /// the next time the menu loads.
    /// </summary>
    [TestOnly] public static ObjectDB LoadMainMenu(ObjectDB vanilla)
    {
        if (vanilla is null) throw new ArgumentNullException(nameof(vanilla), "Pass the database DeclareVanilla returned.");
        var db = new GameObject("FejdStartup").AddComponent<ObjectDB>();
        db.CopyOtherDB(vanilla);
        return db;
    }
    /// <summary>
    /// A world load's pass: the world scene's ObjectDB wakes with the game's own content as it was built (new lists, not
    /// the prefab's), and <see cref="AwakePostfix"/> runs.
    /// </summary>
    [TestOnly] public static ObjectDB LoadWorld(ObjectDB vanilla)
    {
        var content = vanilla.m_vanilla ?? throw new ArgumentException("Pass the database DeclareVanilla returned.", nameof(vanilla));
        var main = new GameObject("_GameMain");
        main.SetActive(false);
        var db = main.AddComponent<ObjectDB>();
        db.m_items = content.Items.ToList(); db.m_recipes = content.Recipes.ToList(); db.m_StatusEffects = content.Effects.ToList();
        main.SetActive(true);
        return db;
    }

    public void Awake()
    {
        m_instance = this;
        UpdateRegisters();
        AwakePostfix?.Invoke(this);
    }

    public void CopyOtherDB(ObjectDB other)
    {
        m_items = other.m_items;
        m_recipes = other.m_recipes;
        m_StatusEffects = other.m_StatusEffects;
        UpdateRegisters();
        CopyOtherDBPostfix?.Invoke(this);
    }

    /// <summary>Indexes the items again (private in the game; public here, as with publicized assemblies). A second item with the same name hash throws.</summary>
    public void UpdateRegisters()
    {
        m_itemByHash.Clear();
        m_itemByData.Clear();
        foreach (var item in m_items)
        {
            int hash = item.name.GetStableHashCode();
            if (m_itemByHash.TryGetValue(hash, out var existing))
                throw new ArgumentException($"An item with the same key has already been added. Key: {hash} (ObjectDB item '{item.name}' and '{existing.name}')");
            m_itemByHash.Add(hash, item);
            var drop = item.GetComponent<ItemDrop>();
            if (drop != null) m_itemByData[drop.m_itemData.m_shared] = item;
        }
    }

    public StatusEffect? GetStatusEffect(int nameHash)
    {
        foreach (var effect in m_StatusEffects) if (effect.NameHash() == nameHash) return effect;
        return null;
    }
    public GameObject? GetItemPrefab(string name) => GetItemPrefab(name.GetStableHashCode());
    public GameObject? GetItemPrefab(int hash) => TryGetItemPrefab(hash, out var prefab) ? prefab : null;
    public GameObject? GetItemPrefab(ItemDrop.ItemData.SharedData sharedData) => TryGetItemPrefab(sharedData, out var prefab) ? prefab : null;
    public bool TryGetItemPrefab(string name, out GameObject prefab) => TryGetItemPrefab(name.GetStableHashCode(), out prefab);
    public bool TryGetItemPrefab(int hash, out GameObject prefab)
    {
        bool found = m_itemByHash.TryGetValue(hash, out var item);
        if (found && NotYetRegistered.Contains(hash)) { EarlyLookups.Add(item!.name); found = false; }
        prefab = found ? item! : null!;
        return found;
    }
    public bool TryGetItemPrefab(ItemDrop.ItemData.SharedData sharedData, out GameObject prefab)
    {
        bool found = m_itemByData.TryGetValue(sharedData, out var item);
        if (found && NotYetRegistered.Contains(item!.name.GetStableHashCode())) { EarlyLookups.Add(item.name); found = false; }
        prefab = found ? item! : null!;
        return found;
    }
    public int GetPrefabHash(GameObject prefab) => prefab.name.GetStableHashCode();

    /// <summary>The items of a type whose object names start with <paramref name="startWith"/>.</summary>
    public List<ItemDrop> GetAllItems(ItemDrop.ItemData.ItemType type, string startWith)
    {
        var found = new List<ItemDrop>();
        foreach (var item in m_items)
        {
            if (item == null) continue;
            var drop = item.GetComponent<ItemDrop>();
            if (drop != null && drop.m_itemData.m_shared.m_itemType == type && drop.gameObject.name.CustomStartsWith(startWith)) found.Add(drop);
        }
        return found;
    }

    /// <summary>The first recipe whose item has the same shared name as <paramref name="item"/>.</summary>
    public Recipe? GetRecipe(ItemDrop.ItemData item)
    {
        foreach (var recipe in m_recipes)
            if (recipe.m_item != null && recipe.m_item.m_itemData.m_shared.m_name == item.m_shared.m_name) return recipe;
        return null;
    }

    /// <summary>Items whose names share a stable hash (the same name twice, or a collision), which the next index would throw on.</summary>
    [TestOnly] public List<(int Hash, string[] Names)> FindDuplicateItems() => Registry.Duplicates(m_items);
}

/// <summary>
/// ZNetScene's prefab registry, as the game's (1.0.16): <see cref="Awake"/> indexes <see cref="m_prefabs"/> and
/// <see cref="m_nonNetViewPrefabs"/> by the stable hash of their names with a dictionary's Add, so a second prefab with
/// the same hash throws, naming both; <see cref="GetPrefab(int)"/> and <see cref="GetPrefab(string)"/> look them up.
/// <see cref="AddPrefab"/> registers at once, as a mod adding a prefab after Awake does. <see cref="AwakePostfix"/>
/// runs where a Harmony postfix on Awake would. The SpawnObject RPC is not registered.
/// </summary>
public partial class ZNetScene : MonoBehaviour
{
    public List<GameObject> m_prefabs = new();
    public List<GameObject> m_nonNetViewPrefabs = new();
    /// <summary>The index by name hash (private in the game; mods reach it through publicized assemblies).</summary>
    public readonly Dictionary<int, GameObject> m_namedPrefabs = new();
    /// <summary>Runs at the end of <see cref="Awake"/>, as a Harmony postfix would. <c>ValheimWorldScope</c> restores it.</summary>
    [TestOnly] public static Action<ZNetScene>? AwakePostfix;

    /// <summary>
    /// Stable hashes of prefabs a test holds back as not registered yet, standing for a registration another mod or a later
    /// pass has still to make: name and hash lookups return null for them and are recorded in <see cref="EarlyLookups"/>.
    /// The game's registries hold every registered entry and look it up directly.
    /// </summary>
    [TestOnly] public readonly HashSet<int> NotYetRegistered = new();
    /// <summary>Every lookup that returned null only because its prefab was held back as not registered yet.</summary>
    [TestOnly] public readonly List<string> EarlyLookups = new();
    [TestOnly] public void MarkNotYetRegistered(params string[] names) { foreach (var name in names) NotYetRegistered.Add(name.GetStableHashCode()); }
    [TestOnly] public void FinishRegistering() => NotYetRegistered.Clear();

    public void Awake()
    {
        instance = this;
        m_namedPrefabs.Clear();
        foreach (var prefab in m_prefabs) Register(prefab);
        foreach (var prefab in m_nonNetViewPrefabs) Register(prefab);
        AwakePostfix?.Invoke(this);
    }
    private void Register(GameObject prefab)
    {
        int hash = prefab.name.GetStableHashCode();
        if (m_namedPrefabs.TryGetValue(hash, out var existing))
            throw new ArgumentException($"An item with the same key has already been added. Key: {hash} (ZNetScene prefab '{prefab.name}' and '{existing.name}')");
        m_namedPrefabs.Add(hash, prefab);
    }

    public bool HasPrefab(int hash) => m_namedPrefabs.ContainsKey(hash);
    public GameObject? GetPrefab(int hash)
    {
        if (!m_namedPrefabs.TryGetValue(hash, out var prefab)) return null;
        if (NotYetRegistered.Contains(hash)) { EarlyLookups.Add(prefab.name); return null; }
        return prefab;
    }
    public GameObject? GetPrefab(string name) => GetPrefab(name.GetStableHashCode());
    public int GetPrefabHash(GameObject go) => go.name.GetStableHashCode();
    public List<string> GetPrefabNames() => m_namedPrefabs.Values.Select(p => p.name).ToList();
    /// <summary>Prefabs whose names share a stable hash (the same name twice, or a collision), which Awake would throw on.</summary>
    [TestOnly] public List<(int Hash, string[] Names)> FindDuplicateHashes() => Registry.Duplicates(m_prefabs.Concat(m_nonNetViewPrefabs));
}

internal static class Registry
{
    internal static List<(int Hash, string[] Names)> Duplicates(IEnumerable<GameObject> prefabs) =>
        prefabs.Where(p => p != null).GroupBy(p => p.name.GetStableHashCode()).Where(g => g.Count() > 1)
            .Select(g => (g.Key, g.Select(p => p.name).ToArray())).ToList();
}

/// <summary>An item on the ground or in an inventory: its <see cref="ItemData"/>, with the fields mods read and write.</summary>
public partial class ItemDrop : MonoBehaviour
{
    public ItemData m_itemData = new();

    [Serializable]
    public partial class ItemData
    {
        public int m_stack = 1;
        public float m_durability = 100f;
        public int m_quality = 1;
        public int m_variant;
        public int m_worldLevel;
        public bool m_equipped;
        public bool m_pickedUp;
        public bool m_cheated;
        public long m_crafterID;
        public string m_crafterName = "";
        public Dictionary<string, string> m_customData = new();
        public Vector2i m_gridPos;
        [NonSerialized] public GameObject? m_dropPrefab;
        public SharedData m_shared = new();

        /// <summary>A copy sharing <see cref="m_shared"/> with its own custom data, as the game's.</summary>
        public ItemData Clone()
        {
            var copy = (ItemData)MemberwiseClone();
            copy.m_customData = new Dictionary<string, string>(m_customData);
            return copy;
        }
        /// <summary>One- and two-handed weapons, bows and torches, as the game counts them.</summary>
        public bool IsWeapon() =>
            m_shared.m_itemType is ItemType.OneHandedWeapon or ItemType.Bow or ItemType.TwoHandedWeapon or ItemType.TwoHandedWeaponLeft or ItemType.Torch;

        public enum ItemType
        {
            None = 0, Material = 1, Consumable = 2, OneHandedWeapon = 3, Bow = 4, Shield = 5, Helmet = 6, Chest = 7, Ammo = 9,
            Customization = 10, Legs = 11, Hands = 12, Trophy = 13, TwoHandedWeapon = 14, Torch = 15, Misc = 16, Shoulder = 17,
            Utility = 18, Tool = 19, Attach_Atgeir = 20, Fish = 21, TwoHandedWeaponLeft = 22, AmmoNonEquipable = 23, Trinket = 24,
        }

        /// <summary>What every item of a kind shares. Set <see cref="m_maxStackSize"/> as the prefab does: the C# default is 0.</summary>
        [Serializable]
        public partial class SharedData
        {
            public string m_name = "";
            public PieceTable? m_buildPieces;
            public string m_description = "";
            public ItemType m_itemType;
            public int m_maxStackSize;
            public int m_maxQuality;
            public float m_weight;
            public int m_value;
            public bool m_teleportable;
            public bool m_questItem;
            public float m_food;
        }
    }
}

/// <summary>A crafting recipe, with the game's defaults.</summary>
public partial class Recipe : ScriptableObject
{
    public ItemDrop? m_item;
    public int m_amount = 1;
    public bool m_enabled = true;
    public float m_qualityResultAmountMultiplier = 1f;
    public int m_listSortWeight = 100;
    public CraftingStation? m_craftingStation;
    public CraftingStation? m_repairStation;
    public int m_minStationLevel = 1;
    public bool m_requireOnlyOneIngredient;
    public Piece.Requirement[] m_resources = new Piece.Requirement[0];
}

/// <summary>A status effect. <see cref="NameHash"/> is the stable hash of its object name, computed once and then kept, as the game's: renaming it later does not change the hash.</summary>
public partial class StatusEffect : ScriptableObject
{
    public string m_name = "";
    public string m_category = "";
    public string m_tooltip = "";
    public float m_ttl;
    public float m_cooldown;
    private int m_nameHash;
    public int NameHash()
    {
        if (m_nameHash == 0) m_nameHash = name.GetStableHashCode();
        return m_nameHash;
    }
}

/// <summary>A buildable piece and the resources it costs.</summary>
public partial class Piece : MonoBehaviour
{
    public string m_name = "";
    public string m_description = "";
    public bool m_enabled = true;
    public CraftingStation? m_craftingStation;
    public Requirement[] m_resources = new Requirement[0];

    [Serializable]
    public partial class Requirement
    {
        public ItemDrop? m_resItem;
        public int m_amount = 1;
        public int m_extraAmountOnlyOneIngredient;
        public int m_amountPerLevel = 1;
        public bool m_upgraderResource;
        public bool m_recover = true;
        /// <summary>The amount for a quality level, as the game computes it: the base amount at level 1, per-level amounts above (half steps from level 4).</summary>
        public int GetAmount(int qualityLevel)
        {
            if (qualityLevel <= 1) return m_amount;
            // Levels 2 and 3 add a step each, level 4 two more (4 steps), and each level after that half a step. An
            // upgrader resource adds the base amount.
            float steps = qualityLevel < 4 ? qualityLevel - 1 : 4f + (qualityLevel - 4) * 0.5f;
            int baseAmount = m_upgraderResource ? m_amount : 0;
            return (int)Math.Floor(steps * m_amountPerLevel + (float)baseAmount);
        }
    }
}

/// <summary>A tool's buildable prefabs, kept separately from scene registration as in the game.</summary>
public partial class PieceTable : MonoBehaviour
{
    public List<GameObject> m_pieces = new();
}

/// <summary>A crafting station, known by name.</summary>
public partial class CraftingStation : MonoBehaviour
{
    public string m_name = "";
}
