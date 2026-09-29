// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Valheim gameplay types as the game (1.0.16) behaves: Utils, HeightmapBuilder, DropTable and the Inventory it fills,
// Container, Pickable and SpawnArea. Random rolls use UnityEngine.Random (seed it with Random.InitState). The world
// level is 0 and the resource rate 1, the game's defaults.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace UnityEngine
{
    /// <summary>An image; the doubles only pass it along.</summary>
    public partial class Sprite : Object { }
}

/// <summary>
/// The game's Utils, as far as mod logic uses it. Its own rounding helpers are kept with their quirks:
/// <see cref="RoundToInt"/> and <see cref="FloorToInt"/> are exact only between -64000 and 64000, and <see cref="Lerp"/>
/// clamps the factor only above 1.
/// </summary>
public static partial class Utils
{
    public enum IterativeSearchType { DepthFirst, BreadthFirst }
    public delegate void ChildHandler(GameObject go);
    private static readonly char[] s_extraCharacters = { '(', ' ' };

    /// <summary>The name up to the first '(' or space: a copy's "wood_wall(Clone)" is "wood_wall".</summary>
    public static string GetPrefabName(GameObject gameObject) => GetPrefabName(gameObject.name);
    public static string GetPrefabName(string name) { int cut = name.IndexOfAny(s_extraCharacters); return cut != -1 ? name.Remove(cut) : name; }

    public static float DistanceSqr(Vector3 v0, Vector3 v1) { float x = v1.x - v0.x, y = v1.y - v0.y, z = v1.z - v0.z; return x * x + y * y + z * z; }
    public static float DistanceXZ(Vector3 v0, Vector3 v1) { float x = v1.x - v0.x, z = v1.z - v0.z; return Mathf.Sqrt(x * x + z * z); }
    public static float LengthXZ(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);
    /// <summary>The direction flattened onto the ground and normalised (zero when too short to normalise, as Unity's Normalize).</summary>
    public static Vector3 DirectionXZ(Vector3 dir)
    {
        float length = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
        return length > 1e-5f ? new Vector3(dir.x / length, 0f, dir.z / length) : Vector3.zero;
    }
    public static float YawFromDirection(Vector3 dir) => FixDegAngle(57.29578f * Mathf.Atan2(dir.x, dir.z));
    public static float FixDegAngle(float p_Angle)
    {
        while (p_Angle >= 360f) p_Angle -= 360f;
        while (p_Angle < 0f) p_Angle += 360f;
        return p_Angle;
    }
    public static float DegDistance(float p_a, float p_b)
    {
        if (p_a == p_b) return 0f;
        float d = Mathf.Abs(FixDegAngle(p_b) - FixDegAngle(p_a));
        return d > 180f ? Mathf.Abs(d - 360f) : d;
    }
    public static int ChebyshevDistance(Vector2s a, Vector2s b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));

    public static float LerpStep(float l, float h, float v) => Clamp01((v - l) / (h - l));
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp1(t);
    public static float SmoothStep(float p_Min, float p_Max, float p_X) { float t = Mathf.Clamp01((p_X - p_Min) / (p_Max - p_Min)); return t * t * (3f - 2f * t); }
    public static float Remap(float value, float inLow, float inHigh, float outLow, float outHigh) =>
        Mathf.Lerp(outLow, outHigh, inLow != inHigh ? Mathf.Clamp01((value - inLow) / (inHigh - inLow)) : 0f);
    public static float Clamp01(float num) => num > 1f ? 1f : num < 0f ? 0f : num;
    public static float Clamp0(float num) => num < 0f ? 0f : num;
    public static float Clamp1(float num) => num > 1f ? 1f : num;
    public static int Clamp(int num, int min, int max) => Math.Max(min, Math.Min(num, max));
    public static float Clamp(float num, float min, float max) => num > max ? max : num < min ? min : num;
    public static int RoundToInt(float f) => (int)(f + 64000.5f) - 64000;
    public static int FloorToInt(float f) => (int)(f + 64000f) - 64000;
    public static int Mod(int value, int dividend) { value %= dividend; return value < 0 ? value + dividend : value; }
    public static float Mod(float value, float dividend) { value %= dividend; return value < 0f ? value + dividend : value; }
    public static short ClampToShort(this int num) => (short)Math.Max(-32768, Math.Min(num, 32767));
    public static Vector2s ClampToShort(this Vector2i vec) => new(vec.x.ClampToShort(), vec.y.ClampToShort());

    /// <summary>Ordinal, character by character, as the game's (no culture rules).</summary>
    public static bool CustomStartsWith(this string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i == b.Length;
    }
    public static bool CustomEndsWith(this string a, string b)
    {
        int i = a.Length - 1, j = b.Length - 1;
        while (i >= 0 && j >= 0 && a[i] == b[j]) { i--; j--; }
        return j < 0;
    }

    /// <summary>GZip, fastest level, as the game compresses ZDO blobs.</summary>
    public static byte[] Compress(byte[] inputArray)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Fastest)) gzip.Write(inputArray, 0, inputArray.Length);
        return output.ToArray();
    }
    public static byte[] Decompress(byte[] inputArray)
    {
        using var input = new MemoryStream(inputArray);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>"/root/child/name", as the game writes a transform's path.</summary>
    public static string GetPath(this Transform obj) => obj.parent == null ? "/" + obj.name : obj.parent.GetPath() + "/" + obj.name;
    public static bool IsParent(Transform go, Transform parent)
    {
        for (Transform? t = go; t != null; t = t.parent) if (t == parent) return true;
        return false;
    }
    /// <summary>A descendant (not the parent itself) with that name, depth or breadth first, as the game searches.</summary>
    public static Transform? FindChild(Transform aParent, string aName, IterativeSearchType searchType = IterativeSearchType.DepthFirst)
    {
        if (searchType == IterativeSearchType.DepthFirst)
        {
            var stack = new Stack<Transform>();
            var at = aParent;
            while (true)
            {
                for (int i = at.childCount - 1; i >= 0; i--) stack.Push(at.GetChild(i));
                if (stack.Count == 0) return null;
                at = stack.Pop();
                if (at.name == aName) return at;
            }
        }
        var queue = new Queue<Transform>();
        var current = aParent;
        while (true)
        {
            for (int i = 0; i < current.childCount; i++) queue.Enqueue(current.GetChild(i));
            if (queue.Count == 0) return null;
            current = queue.Dequeue();
            if (current.name == aName) return current;
        }
    }
    /// <summary>Whether every object from <paramref name="go"/> up to <paramref name="root"/> is active itself. Like the game's, it runs off the top (and throws) when root is not above go.</summary>
    public static bool IsEnabledInheirarcy(GameObject go, GameObject root)
    {
        do
        {
            if (!go.activeSelf) return false;
            if (go == root) return true;
            go = go.transform.parent!.gameObject;
        }
        while (go != null);
        return true;
    }
    /// <summary>The components below <paramref name="root"/> (not on it) on objects active up to it, as the game collects a location's pieces.</summary>
    public static T[] GetEnabledComponentsInChildren<T>(GameObject root) where T : Component
    {
        var all = root.GetComponentsInChildren<T>();
        var found = new List<T>(all.Length);
        foreach (var component in all)
            if (component.transform != root.transform && IsEnabledInheirarcy(component.gameObject, root)) found.Add(component);
        return found.ToArray();
    }
    /// <summary>Calls the handler for every descendant. As in the game, <paramref name="deepestFirst"/> applies to the first level only.</summary>
    public static void IterateHierarchy(GameObject gameObject, ChildHandler childHandler, bool deepestFirst = false)
    {
        foreach (Transform child in gameObject.transform)
        {
            if (!deepestFirst) childHandler(child.gameObject);
            IterateHierarchy(child.gameObject, childHandler);
            if (deepestFirst) childHandler(child.gameObject);
        }
    }
}

/// <summary>
/// The terrain builder, as the game's (1.0.16), with its build thread made explicit. <see cref="IsTerrainReady"/> answers
/// true only while a matching build is in the ready list, and otherwise queues one and answers false;
/// <see cref="BuildQueued"/> does what the build thread does between frames. <see cref="RequestTerrainSync"/> hands the
/// ready build out and removes it (building it on the spot when there is none), so asking again queues a new one. The
/// heights are the game's: the world generator's biome heights, blended across the zone's four corner biomes, and
/// smoothed for a distant LOD.
/// </summary>
public partial class HeightmapBuilder
{
    public partial class HMBuildData
    {
        public Vector3 m_center;
        public int m_width;
        public float m_scale;
        public bool m_distantLod;
        public bool m_menu;
        public WorldGenerator m_worldGen;
        public List<float> m_baseHeights = new();
        public Color[] m_baseMask = new Color[0];
        public HMBuildData(Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen)
        {
            m_center = center; m_width = width; m_scale = scale; m_distantLod = distantLod; m_worldGen = worldGen;
        }
        public bool IsEqual(Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen) =>
            m_center.x == center.x && m_center.y == center.y && m_center.z == center.z && m_width == width && m_scale == scale &&
            m_distantLod == distantLod && ReferenceEquals(m_worldGen, worldGen);
    }

    internal static HeightmapBuilder? m_instance;
    public static HeightmapBuilder instance => m_instance ??= new HeightmapBuilder();
    private readonly List<HMBuildData> m_toBuild = new();
    private readonly List<HMBuildData> m_ready = new();
    /// <summary>Builds waiting for the build thread.</summary>
    public int QueuedCount => m_toBuild.Count;
    /// <summary>Finished builds not yet handed out.</summary>
    public int ReadyCount => m_ready.Count;

    public bool IsTerrainReady(Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen)
    {
        if (m_ready.Exists(d => d.IsEqual(center, width, scale, distantLod, worldGen))) return true;
        if (m_toBuild.Exists(d => d.IsEqual(center, width, scale, distantLod, worldGen))) return false;
        m_toBuild.Add(new HMBuildData(center, width, scale, distantLod, worldGen));
        return false;
    }
    public HMBuildData RequestTerrainSync(Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen)
    {
        int ready = m_ready.FindIndex(d => d.IsEqual(center, width, scale, distantLod, worldGen));
        if (ready >= 0) { var data = m_ready[ready]; m_ready.RemoveAt(ready); return data; }
        m_toBuild.RemoveAll(d => d.IsEqual(center, width, scale, distantLod, worldGen));
        var built = new HMBuildData(center, width, scale, distantLod, worldGen);
        Build(built);
        return built;
    }
    /// <summary>Builds everything queued and moves it to the ready list, as the build thread does; returns how many were built.</summary>
    public int BuildQueued()
    {
        var queued = m_toBuild.ToArray();
        m_toBuild.Clear();
        foreach (var data in queued) { Build(data); m_ready.Add(data); }
        return queued.Length;
    }

    private static float SmoothStep(float min, float max, float x)
    {
        double t = Math.Max(0.0, Math.Min(1.0, ((double)x - min) / ((double)max - min)));
        return (float)(t * t * (3.0 - 2.0 * t));
    }
    private static float Lerp(float a, float b, float t) => t <= 0f ? a : t >= 1f ? b : (float)(a * (1.0 - t) + b * (double)t);

    private static void Build(HMBuildData data)
    {
        int side = data.m_width + 1;
        float half = data.m_width * data.m_scale * -0.5f;
        var corner = new Vector3(data.m_center.x + half, data.m_center.y, data.m_center.z + half);
        var gen = data.m_worldGen;
        float far = (float)((double)data.m_width * data.m_scale);
        var b0 = gen.GetBiome(corner.x, corner.z);
        var b1 = gen.GetBiome((float)(corner.x + (double)far), corner.z);
        var b2 = gen.GetBiome(corner.x, (float)(corner.z + (double)far));
        var b3 = gen.GetBiome((float)(corner.x + (double)far), (float)(corner.z + (double)far));
        data.m_baseHeights = new List<float>(new float[side * side]);
        data.m_baseMask = new Color[side * side];
        for (int k = 0; k < side; k++)
        {
            float wy = (float)(corner.z + (double)k * data.m_scale);
            float ty = SmoothStep(0f, 1f, (float)((double)k / data.m_width));
            for (int l = 0; l < side; l++)
            {
                float wx = (float)(corner.x + (double)l * data.m_scale);
                float tx = SmoothStep(0f, 1f, (float)((double)l / data.m_width));
                float height;
                Color mask;
                if (data.m_distantLod) height = gen.GetBiomeHeight(gen.GetBiome(wx, wy), wx, wy, out mask);
                else if (b2 == b0 && b1 == b0 && b3 == b0) height = gen.GetBiomeHeight(b0, wx, wy, out mask);
                else
                {
                    float h0 = gen.GetBiomeHeight(b0, wx, wy, out var m0), h1 = gen.GetBiomeHeight(b1, wx, wy, out var m1);
                    float h2 = gen.GetBiomeHeight(b2, wx, wy, out var m2), h3 = gen.GetBiomeHeight(b3, wx, wy, out var m3);
                    height = Lerp(Lerp(h0, h1, tx), Lerp(h2, h3, tx), ty);
                    mask = Color.Lerp(Color.Lerp(m0, m1, tx), Color.Lerp(m2, m3, tx), ty);
                }
                data.m_baseHeights[k * side + l] = height;
                data.m_baseMask[k * side + l] = mask;
            }
        }
        if (!data.m_distantLod) return;
        // A distant LOD smooths steps over 10 m, four passes, as the game does.
        for (int pass = 0; pass < 4; pass++)
        {
            var before = new List<float>(data.m_baseHeights);
            for (int n = 1; n < side - 1; n++)
                for (int m = 1; m < side - 1; m++)
                {
                    float h = before[n * side + m];
                    foreach (float neighbour in new[] { before[(n - 1) * side + m], before[(n + 1) * side + m], before[n * side + m - 1], before[n * side + m + 1] })
                        if (Mathf.Abs(h - neighbour) > 10f) h = (h + neighbour) * 0.5f;
                    data.m_baseHeights[n * side + m] = h;
                }
        }
    }
}

/// <summary>
/// A weighted drop table, as the game's. <see cref="GetDropList()"/> rolls the chance, then the number of drops, then
/// each drop by weight (with <see cref="m_oneOfEach"/> removing what was picked, and the first entry when a roll finds
/// none). <see cref="GetDropListItems"/> rolls item stacks; like the game's, it returns one shared list that the next call
/// clears, so copy it to keep it. <see cref="Clone"/> is shallow: the copy shares <see cref="m_drops"/>.
/// </summary>
[Serializable]
public partial class DropTable
{
    [Serializable]
    public partial struct DropData
    {
        public GameObject m_item;
        public int m_stackMin;
        public int m_stackMax;
        public float m_weight;
        public bool m_dontScale;
    }

    private static readonly List<ItemDrop.ItemData> s_toDrop = new();
    public List<DropData> m_drops = new();
    public int m_dropMin = 1;
    public int m_dropMax = 1;
    public float m_dropChance = 1f;
    public bool m_oneOfEach;

    public DropTable Clone() => (DropTable)MemberwiseClone();
    public bool IsEmpty() => m_drops.Count == 0;

    public List<GameObject> GetDropList() => GetDropList(UnityEngine.Random.Range(m_dropMin, m_dropMax + 1));
    private List<GameObject> GetDropList(int amount)
    {
        var list = new List<GameObject>();
        if (m_drops.Count == 0 || UnityEngine.Random.value > m_dropChance) return list;
        var drops = new List<DropData>(m_drops);
        float total = 0f;
        foreach (var drop in drops) total += drop.m_weight;
        for (int j = 0; j < amount; j++)
        {
            float roll = UnityEngine.Random.Range(0f, total), sum = 0f;
            bool picked = false;
            foreach (var drop in drops)
            {
                sum += drop.m_weight;
                if (roll > sum) continue;
                picked = true;
                int count = drop.m_dontScale
                    ? UnityEngine.Random.Range(drop.m_stackMin, drop.m_stackMax)
                    : (int)Mathf.Max(1f, Mathf.Round(UnityEngine.Random.Range(Mathf.Round(drop.m_stackMin), Mathf.Round(drop.m_stackMax))));
                for (int k = 0; k < count; k++) list.Add(drop.m_item);
                if (m_oneOfEach) { drops.Remove(drop); total -= drop.m_weight; }
                break;
            }
            if (!picked && drops.Count > 0) list.Add(drops[0].m_item);
        }
        return list;
    }

    public List<ItemDrop.ItemData> GetDropListItems()
    {
        s_toDrop.Clear();
        if (m_drops.Count == 0 || UnityEngine.Random.value > m_dropChance) return s_toDrop;
        var drops = new List<DropData>(m_drops);
        float total = 0f;
        foreach (var drop in drops) total += drop.m_weight;
        int count = UnityEngine.Random.Range(m_dropMin, m_dropMax + 1);
        for (int i = 0; i < count; i++)
        {
            float roll = UnityEngine.Random.Range(0f, total), sum = 0f;
            bool picked = false;
            foreach (var drop in drops)
            {
                sum += drop.m_weight;
                if (roll > sum) continue;
                picked = true;
                AddItemToList(s_toDrop, drop);
                if (m_oneOfEach) { drops.Remove(drop); total -= drop.m_weight; }
                break;
            }
            if (!picked && drops.Count > 0) AddItemToList(s_toDrop, drops[0]);
        }
        return s_toDrop;
    }
    private static void AddItemToList(List<ItemDrop.ItemData> toDrop, DropData data)
    {
        var itemData = data.m_item.GetComponent<ItemDrop>().m_itemData;
        var copy = itemData.Clone();
        copy.m_dropPrefab = data.m_item;
        int min = Math.Max(1, data.m_stackMin), max = Math.Min(itemData.m_shared.m_maxStackSize, data.m_stackMax);
        copy.m_stack = UnityEngine.Random.Range(min, max + 1);
        copy.m_worldLevel = 0;
        toDrop.Add(copy);
    }
}

/// <summary>A grid of item stacks, as the game's: adding stacks onto a matching non-full stack first, then fills the first empty slot (from the top for weapons, tools, shields, utility, misc and trinkets, from the bottom otherwise).</summary>
public partial class Inventory
{
    private readonly List<ItemDrop.ItemData> m_inventory = new();
    public string m_name;
    public int m_width, m_height;
    public Action? m_onChanged;
    public Inventory(string name, Sprite? bkg, int w, int h) { m_name = name; m_width = w; m_height = h; }

    public bool AddItem(ItemDrop.ItemData item)
    {
        bool added = true;
        if (item.m_shared.m_maxStackSize > 1)
        {
            for (int i = 0; i < item.m_stack; i++)
            {
                var stack = m_inventory.Find(s => s.m_shared.m_name == item.m_shared.m_name && s.m_quality == item.m_quality && s.m_stack < s.m_shared.m_maxStackSize && s.m_worldLevel == item.m_worldLevel);
                if (stack != null) { stack.m_stack++; if (item.m_cheated) stack.m_cheated = true; continue; }
                item.m_stack -= i;
                added = Place(item);
                break;
            }
        }
        else added = Place(item);
        m_onChanged?.Invoke();
        return added;
    }
    private bool Place(ItemDrop.ItemData item)
    {
        var slot = FindEmptySlot(TopFirst(item));
        if (slot.x < 0) return false;
        item.m_gridPos = slot;
        m_inventory.Add(item);
        return true;
    }
    private static bool TopFirst(ItemDrop.ItemData item) =>
        item.IsWeapon() || item.m_shared.m_itemType is ItemDrop.ItemData.ItemType.Tool or ItemDrop.ItemData.ItemType.Shield or
            ItemDrop.ItemData.ItemType.Utility or ItemDrop.ItemData.ItemType.Misc or ItemDrop.ItemData.ItemType.Trinket;
    private Vector2i FindEmptySlot(bool topFirst)
    {
        for (int row = 0; row < m_height; row++)
        {
            int y = topFirst ? row : m_height - 1 - row;
            for (int x = 0; x < m_width; x++) if (GetItemAt(x, y) == null) return new Vector2i(x, y);
        }
        return new Vector2i(-1, -1);
    }
    public ItemDrop.ItemData? GetItemAt(int x, int y) => m_inventory.Find(i => i.m_gridPos.x == x && i.m_gridPos.y == y);
    public List<ItemDrop.ItemData> GetAllItems() => m_inventory;
    public int NrOfItems() => m_inventory.Count;
    public int NrOfItemsIncludingStacks() { int n = 0; foreach (var item in m_inventory) n += item.m_stack; return n; }
    public int CountItems(string name, int quality = -1, bool matchWorldLevel = true)
    {
        int n = 0;
        foreach (var item in m_inventory)
            if ((name == null || item.m_shared.m_name == name) && (quality < 0 || quality == item.m_quality) && (!matchWorldLevel || item.m_worldLevel >= 0)) n += item.m_stack;
        return n;
    }
    public bool HaveEmptySlot() => m_inventory.Count < m_width * m_height;
    public int GetEmptySlots() => m_width * m_height - m_inventory.Count;
    public bool RemoveItem(ItemDrop.ItemData item) { bool removed = m_inventory.Remove(item); if (removed) m_onChanged?.Invoke(); return removed; }
}

/// <summary>
/// A container, as the game's: on waking with a ZDO it makes its inventory, and the owner adds the default items once,
/// marking the ZDO so later loads (or other peers) do not add them again. It needs a ZNetView on its object, as in the game.
/// </summary>
public partial class Container : MonoBehaviour
{
    public enum PrivacySetting { Private, Group, Public }
    public string m_name = "Container";
    public int m_width = 3;
    public int m_height = 2;
    public PrivacySetting m_privacy = PrivacySetting.Public;
    public bool m_checkGuardStone;
    public bool m_autoDestroyEmpty;
    public DropTable m_defaultItems = new();
    private Inventory? m_inventory;
    private ZNetView? m_nview;

    private void Awake()
    {
        m_nview = GetComponent<ZNetView>();
        var zdo = m_nview!.GetZDO();
        if (zdo == null) return;
        m_inventory = new Inventory(m_name, null, m_width, m_height);
        if (m_nview.IsOwner() && zdo.GetInt(ZDOVars.s_addedDefaultItems) == 0)
        {
            foreach (var item in m_defaultItems.GetDropListItems()) m_inventory.AddItem(item);
            zdo.Set(ZDOVars.s_addedDefaultItems, 1);
        }
    }
    public Inventory GetInventory() => m_inventory!;
}

/// <summary>
/// Something to pick, as the game's: its picked and enabled state comes from its ZDO when it wakes; picking a pickable
/// that neither respawns nor hides a part destroys it (when owned), otherwise the picked state is saved to the ZDO and
/// <see cref="m_hideWhenPicked"/> is switched. Respawn timing is not modelled.
/// </summary>
public partial class Pickable : MonoBehaviour
{
    public GameObject? m_hideWhenPicked;
    public GameObject? m_itemPrefab;
    public int m_amount = 1;
    public int m_minAmountScaled = 1;
    public bool m_dontScale;
    public DropTable m_extraDrops = new();
    public string m_overrideName = "";
    public float m_respawnTimeMinutes;
    public float m_spawnOffset = 0.5f;
    public bool m_defaultPicked;
    public bool m_defaultEnabled = true;
    public bool m_harvestable;
    private ZNetView? m_nview;
    private bool m_picked;
    private int m_enabled = 2;

    public int GetEnabled => m_enabled;

    private void Awake()
    {
        m_nview = GetComponent<ZNetView>();
        var zdo = m_nview!.GetZDO();
        if (zdo == null) return;
        m_picked = zdo.GetInt(ZDOVars.s_picked, m_defaultPicked ? 1 : 0) != 0;
        if (m_enabled == 2) m_enabled = zdo.GetInt(ZDOVars.s_enabled, 1) != 0 ? 1 : 0;
        else if (m_nview.IsOwner()) zdo.Set(ZDOVars.s_enabled, m_enabled == 1 ? 1 : 0);
        if (m_hideWhenPicked) m_hideWhenPicked!.SetActive(!m_picked && m_enabled == 1);
        if (m_respawnTimeMinutes <= 0f && m_hideWhenPicked == null && zdo.GetInt(ZDOVars.s_picked) != 0)
        {
            m_nview.ClaimOwnership();
            ZNetScene.instance!.Destroy(gameObject);
        }
    }

    public string GetHoverName() => !string.IsNullOrEmpty(m_overrideName) ? m_overrideName : m_itemPrefab!.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
    /// <summary>The hover text, localized: empty once picked or disabled.</summary>
    public string GetHoverText() => m_picked || m_enabled == 0 ? "" : Localization.instance.Localize(GetHoverName() + "\n[<color=yellow><b>$KEY_Use</b></color>] $inventory_pickup");
    public void SetPicked(bool picked)
    {
        m_picked = picked;
        if (m_hideWhenPicked) m_hideWhenPicked!.SetActive(!picked);
        if (!m_nview || !m_nview!.IsOwner()) return;
        if (m_respawnTimeMinutes > 0f || m_hideWhenPicked != null) m_nview.GetZDO().Set(ZDOVars.s_picked, picked ? 1 : 0);
        else if (picked) ZNetScene.instance!.Destroy(gameObject);
    }
    public bool GetPicked() => m_picked;
    public void SetEnabled(bool value) => SetEnabled(value ? 1 : 0);
    /// <summary>As the game's, the ZDO gets this component's own enabled flag, not <paramref name="value"/>. (The game then shows the hidden part by respawn time, which is not modelled.)</summary>
    public void SetEnabled(int value)
    {
        m_enabled = value;
        if (m_nview && m_nview!.IsOwner() && m_nview.GetZDO() != null) m_nview.GetZDO().Set(ZDOVars.s_enabled, enabled ? 1 : 0);
    }
    public bool CanBePicked() => m_hideWhenPicked && m_hideWhenPicked!.activeInHierarchy || !m_picked && m_enabled == 1;
}

/// <summary>A spawner's settings and its weighted choice of what to spawn. Spawning itself is not modelled.</summary>
public partial class SpawnArea : MonoBehaviour
{
    [Serializable]
    public partial class SpawnData
    {
        public GameObject? m_prefab;
        public float m_weight;
        public int m_maxLevel = 1;
        public int m_minLevel = 1;
    }
    public List<SpawnData> m_prefabs = new();
    public float m_levelupChance = 15f;
    public float m_spawnIntervalSec = 30f;
    public float m_triggerDistance = 256f;
    public bool m_setPatrolSpawnPoint = true;
    public float m_spawnRadius = 2f;
    public float m_nearRadius = 10f;
    public float m_farRadius = 1000f;
    public int m_maxNear = 3;
    public int m_maxTotal = 20;
    public bool m_onGroundOnly;

    /// <summary>Picks by weight as the game does (private there): the last entry when the roll finds none, null when empty.</summary>
    public SpawnData? SelectWeightedPrefab()
    {
        if (m_prefabs.Count == 0) return null;
        float total = 0f;
        foreach (var prefab in m_prefabs) total += prefab.m_weight;
        float roll = UnityEngine.Random.Range(0f, total), sum = 0f;
        foreach (var prefab in m_prefabs) { sum += prefab.m_weight; if (roll <= sum) return prefab; }
        return m_prefabs[m_prefabs.Count - 1];
    }
}

public static partial class ZDOVars
{
    public static readonly int s_addedDefaultItems = "addedDefaultItems".GetStableHashCode();
    public static readonly int s_picked = "picked".GetStableHashCode();
    public static readonly int s_enabled = "enabled".GetStableHashCode();
}
