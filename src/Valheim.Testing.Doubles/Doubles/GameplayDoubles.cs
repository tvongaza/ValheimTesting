// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Valheim gameplay types, written from how the game (1.0.16) behaves: Utils, HeightmapBuilder, DropTable and the
// Inventory it fills, Container, Pickable and SpawnArea. Random rolls use UnityEngine.Random (seed it with
// Random.InitState). The world level is 0 and the resource rate 1, the game's defaults.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;

namespace UnityEngine
{
    /// <summary>An image; the doubles only pass it along.</summary>
    public partial class Sprite : Object { }
}

/// <summary>
/// The game's Utils, as far as mod logic uses it, with the game's own quirks. <see cref="RoundToInt"/> and
/// <see cref="FloorToInt"/> shift the value by 64000 in float arithmetic and truncate: halves round up
/// (<c>RoundToInt(2.5f)</c> is 3, <c>RoundToInt(-1.5f)</c> is -1), a value within float precision of an integer at that
/// magnitude lands on it (<c>FloorToInt(0.999f)</c> is 1), and below -64000 the result truncates toward zero.
/// <see cref="Lerp"/> clamps its factor only above 1.
/// </summary>
public static partial class Utils
{
    public enum IterativeSearchType { DepthFirst, BreadthFirst }
    public delegate void ChildHandler(GameObject go);
    private static readonly char[] s_nameEnds = { '(', ' ' };

    /// <summary>The name up to the first '(' or space: a copy's "wood_wall(Clone)" is "wood_wall".</summary>
    public static string GetPrefabName(GameObject gameObject) => GetPrefabName(gameObject.name);
    public static string GetPrefabName(string name)
    {
        int end = name.IndexOfAny(s_nameEnds);
        return end < 0 ? name : name.Substring(0, end);
    }

    public static float DistanceSqr(Vector3 v0, Vector3 v1) { var d = v1 - v0; return d.x * d.x + d.y * d.y + d.z * d.z; }
    public static float DistanceXZ(Vector3 v0, Vector3 v1) => LengthXZ(v1 - v0);
    public static float LengthXZ(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);
    /// <summary>The direction flattened onto the ground and normalised (zero when too short to normalise, as Unity's Normalize).</summary>
    public static Vector3 DirectionXZ(Vector3 dir)
    {
        float length = LengthXZ(dir);
        return length > 1e-5f ? new Vector3(dir.x / length, 0f, dir.z / length) : Vector3.zero;
    }
    /// <summary>The compass heading of a direction in degrees, 0 along +z and 90 along +x.</summary>
    public static float YawFromDirection(Vector3 dir) => FixDegAngle(57.29578f * Mathf.Atan2(dir.x, dir.z));
    /// <summary>The angle brought into [0, 360) by whole turns, one turn at a time (so a huge angle keeps the game's float rounding).</summary>
    public static float FixDegAngle(float p_Angle)
    {
        for (; p_Angle >= 360f; p_Angle -= 360f) { }
        for (; p_Angle < 0f; p_Angle += 360f) { }
        return p_Angle;
    }
    /// <summary>The smaller angle between two headings, 0 to 180.</summary>
    public static float DegDistance(float p_a, float p_b)
    {
        if (p_a == p_b) return 0f;
        float gap = Math.Abs(FixDegAngle(p_a) - FixDegAngle(p_b));
        return Math.Min(gap, Math.Abs(gap - 360f));
    }
    public static int ChebyshevDistance(Vector2s a, Vector2s b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));

    public static float LerpStep(float l, float h, float v) => Clamp01((v - l) / (h - l));
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp1(t);
    public static float SmoothStep(float p_Min, float p_Max, float p_X) { float t = Mathf.Clamp01((p_X - p_Min) / (p_Max - p_Min)); return t * t * (3f - 2f * t); }
    public static float Remap(float value, float inLow, float inHigh, float outLow, float outHigh) =>
        Mathf.Lerp(outLow, outHigh, inLow != inHigh ? Mathf.Clamp01((value - inLow) / (inHigh - inLow)) : 0f);
    public static float Clamp01(float num) => Clamp(num, 0f, 1f);
    public static float Clamp0(float num) => num < 0f ? 0f : num;
    public static float Clamp1(float num) => num > 1f ? 1f : num;
    public static int Clamp(int num, int min, int max) => Math.Max(min, Math.Min(num, max));
    public static float Clamp(float num, float min, float max) => num > max ? max : num < min ? min : num;
    private const float Shift = 64000f;
    public static int RoundToInt(float f) => (int)(f + (Shift + 0.5f)) - (int)Shift;
    public static int FloorToInt(float f) => (int)(f + Shift) - (int)Shift;
    /// <summary>The remainder moved into [0, dividend).</summary>
    public static int Mod(int value, int dividend) { int r = value % dividend; return r < 0 ? r + dividend : r; }
    public static float Mod(float value, float dividend) { float r = value % dividend; return r < 0f ? r + dividend : r; }
    public static short ClampToShort(this int num) => (short)Clamp(num, short.MinValue, short.MaxValue);
    public static Vector2s ClampToShort(this Vector2i vec) => new(vec.x.ClampToShort(), vec.y.ClampToShort());

    /// <summary>Ordinal, character by character (no culture rules).</summary>
    public static bool CustomStartsWith(this string a, string b) => b.Length <= a.Length && string.CompareOrdinal(a, 0, b, 0, b.Length) == 0;
    public static bool CustomEndsWith(this string a, string b) => b.Length <= a.Length && string.CompareOrdinal(a, a.Length - b.Length, b, 0, b.Length) == 0;

    /// <summary>GZip at its fastest level, as the game compresses ZDO blobs.</summary>
    public static byte[] Compress(byte[] inputArray)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, System.IO.Compression.CompressionLevel.Fastest)) gzip.Write(inputArray, 0, inputArray.Length);
        return packed.ToArray();
    }
    public static byte[] Decompress(byte[] inputArray)
    {
        using var gzip = new GZipStream(new MemoryStream(inputArray), CompressionMode.Decompress);
        using var unpacked = new MemoryStream();
        gzip.CopyTo(unpacked);
        return unpacked.ToArray();
    }

    /// <summary>"/root/child/name": each name from the top down.</summary>
    public static string GetPath(this Transform obj)
    {
        var names = new List<string>();
        for (Transform? t = obj; t != null; t = t.parent) names.Insert(0, t.name);
        return "/" + string.Join("/", names.ToArray());
    }
    /// <summary>Whether <paramref name="parent"/> is <paramref name="go"/> or one of its ancestors.</summary>
    public static bool IsParent(Transform go, Transform parent) => Ancestry(go).Any(t => t == parent);
    private static IEnumerable<Transform> Ancestry(Transform from) { for (Transform? t = from; t != null; t = t.parent) yield return t; }

    /// <summary>
    /// The first descendant (never the parent itself) with that name: depth first visits a child and everything below it
    /// before the next child; breadth first visits all children before any grandchild.
    /// </summary>
    public static Transform? FindChild(Transform aParent, string aName, IterativeSearchType searchType = IterativeSearchType.DepthFirst)
    {
        if (searchType == IterativeSearchType.DepthFirst) return Below(aParent).FirstOrDefault(t => t.name == aName);
        for (var level = Children(aParent).ToList(); level.Count > 0; level = level.SelectMany(Children).ToList())
            foreach (var t in level) if (t.name == aName) return t;
        return null;
    }
    private static IEnumerable<Transform> Children(Transform t) { for (int i = 0; i < t.childCount; i++) yield return t.GetChild(i); }
    private static IEnumerable<Transform> Below(Transform t) => Children(t).SelectMany(child => new[] { child }.Concat(Below(child)));

    /// <summary>
    /// Whether <paramref name="go"/> and each parent up to <paramref name="root"/> is active itself. Root must be above
    /// go: going past the top of the hierarchy throws, as in the game.
    /// </summary>
    public static bool IsEnabledInheirarcy(GameObject go, GameObject root)
    {
        for (var at = go; ; at = at.transform.parent!.gameObject)
        {
            if (!at.activeSelf) return false;
            if (at == root) return true;
        }
    }
    /// <summary>The components below <paramref name="root"/> (not on it) on objects active up to it: a location's working pieces.</summary>
    public static T[] GetEnabledComponentsInChildren<T>(GameObject root) where T : Component =>
        root.GetComponentsInChildren<T>().Where(c => c.transform != root.transform && IsEnabledInheirarcy(c.gameObject, root)).ToArray();
    /// <summary>Calls the handler for every descendant, parents before children; <paramref name="deepestFirst"/> puts a child after its descendants, but for the first level only, as in the game.</summary>
    public static void IterateHierarchy(GameObject gameObject, ChildHandler childHandler, bool deepestFirst = false)
    {
        foreach (var child in Children(gameObject.transform).Select(t => t.gameObject).ToList())
        {
            if (deepestFirst) { IterateHierarchy(child, childHandler); childHandler(child); }
            else { childHandler(child); IterateHierarchy(child, childHandler); }
        }
    }
}

/// <summary>
/// The terrain builder, with its build thread made explicit. <see cref="IsTerrainReady"/> answers true only while a
/// matching build is ready, and otherwise queues one and answers false; <see cref="BuildQueued"/> does what the build
/// thread does between frames. <see cref="RequestTerrainSync"/> hands a ready build out and removes it (building it on the
/// spot when there is none), so asking again queues a new one. Heights come from the world generator: one biome's height
/// where the zone's four corners share a biome, otherwise the four corner biomes' heights blended by smoothstepped
/// position; a distant LOD takes each point's own biome and then evens out steps of more than 10 m (four passes, inside
/// the zone only). This matches the game's 1.0.16 builder.
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
        if (!m_toBuild.Exists(d => d.IsEqual(center, width, scale, distantLod, worldGen))) m_toBuild.Add(new HMBuildData(center, width, scale, distantLod, worldGen));
        return false;
    }
    public HMBuildData RequestTerrainSync(Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen)
    {
        var ready = m_ready.Find(d => d.IsEqual(center, width, scale, distantLod, worldGen));
        if (ready != null) { m_ready.Remove(ready); return ready; }
        m_toBuild.RemoveAll(d => d.IsEqual(center, width, scale, distantLod, worldGen));
        return Built(new HMBuildData(center, width, scale, distantLod, worldGen));
    }
    /// <summary>Builds everything queued and makes it ready, as the build thread does; returns how many were built.</summary>
    public int BuildQueued()
    {
        var queued = m_toBuild.ToList();
        m_toBuild.Clear();
        m_ready.AddRange(queued.Select(Built));
        return queued.Count;
    }

    private static HMBuildData Built(HMBuildData data)
    {
        int side = data.m_width + 1;
        var gen = data.m_worldGen;
        // Grid coordinates as the game computes them: the corner in float, each step added in double.
        float half = data.m_width * data.m_scale * -0.5f;
        float originX = data.m_center.x + half, originZ = data.m_center.z + half;
        float X(int i) => (float)(originX + (double)i * data.m_scale);
        float Z(int i) => (float)(originZ + (double)i * data.m_scale);
        float Weight(int i) => BlendSmoothStep((float)((double)i / data.m_width));
        var corners = new[] { gen.GetBiome(X(0), Z(0)), gen.GetBiome(X(data.m_width), Z(0)), gen.GetBiome(X(0), Z(data.m_width)), gen.GetBiome(X(data.m_width), Z(data.m_width)) };
        bool oneBiome = corners.All(b => b == corners[0]);

        var heights = new float[side * side];
        data.m_baseMask = new Color[side * side];
        for (int row = 0; row < side; row++)
            for (int col = 0; col < side; col++)
            {
                float x = X(col), z = Z(row);
                Color mask;
                float height;
                if (data.m_distantLod) height = gen.GetBiomeHeight(gen.GetBiome(x, z), x, z, out mask);
                else if (oneBiome) height = gen.GetBiomeHeight(corners[0], x, z, out mask);
                else
                {
                    var h = new float[4]; var m = new Color[4];
                    for (int c = 0; c < 4; c++) h[c] = gen.GetBiomeHeight(corners[c], x, z, out m[c]);
                    float across = Weight(col), up = Weight(row);
                    height = BlendLerp(BlendLerp(h[0], h[1], across), BlendLerp(h[2], h[3], across), up);
                    mask = Color.Lerp(Color.Lerp(m[0], m[1], across), Color.Lerp(m[2], m[3], across), up);
                }
                heights[row * side + col] = height;
                data.m_baseMask[row * side + col] = mask;
            }
        if (data.m_distantLod) for (int pass = 0; pass < 4; pass++) EvenOutSteps(heights, side);
        data.m_baseHeights = heights.ToList();
        return data;
    }

    // One pass over the inner points, reading the previous pass: a point more than 10 m from a neighbour moves halfway to
    // it, neighbours taken below, above, left, then right.
    private static void EvenOutSteps(float[] heights, int side)
    {
        var previous = (float[])heights.Clone();
        var offsets = new[] { -side, side, -1, 1 };
        for (int row = 1; row < side - 1; row++)
            for (int col = 1; col < side - 1; col++)
            {
                int at = row * side + col;
                float h = previous[at];
                foreach (int offset in offsets)
                    if (Math.Abs(h - previous[at + offset]) > 10f) h = (h + previous[at + offset]) * 0.5f;
                heights[at] = h;
            }
    }
    // The builder's own lerp and smoothstep: clamped, and computed in double.
    private static float BlendLerp(float a, float b, float t) => t <= 0f ? a : t >= 1f ? b : (float)(a * (1.0 - t) + b * (double)t);
    private static float BlendSmoothStep(float x)
    {
        double t = Math.Max(0.0, Math.Min(1.0, x));
        return (float)(t * t * (3.0 - 2.0 * t));
    }
}

internal static class WeightedChoice
{
    internal static float Sum(IEnumerable<float> weights) { float sum = 0f; foreach (float w in weights) sum += w; return sum; }
    /// <summary>
    /// Rolls a value from 0 to <paramref name="total"/> and returns the first entry whose running sum of weights reaches
    /// it, or -1 when none does (nothing to pick, float rounding, or negative weights).
    /// </summary>
    internal static int Roll(IReadOnlyList<float> weights, float total)
    {
        float roll = UnityEngine.Random.Range(0f, total), running = 0f;
        for (int i = 0; i < weights.Count; i++)
        {
            running += weights[i];
            if (roll <= running) return i;
        }
        return -1;
    }
}

/// <summary>
/// A weighted drop table, as the game uses it. A roll first checks <see cref="m_dropChance"/>, then draws
/// <see cref="m_dropMin"/> to <see cref="m_dropMax"/> (both included) entries by weight; with <see cref="m_oneOfEach"/>
/// a drawn entry leaves the pool, and a draw that finds nothing takes the first entry left. <see cref="GetDropList()"/>
/// gives objects: <c>m_dontScale</c> entries in a stack from <c>m_stackMin</c> up to but not including <c>m_stackMax</c>,
/// others from <c>m_stackMin</c> to <c>m_stackMax</c> rounded, at least 1. <see cref="GetDropListItems"/> gives item
/// stacks from <c>max(1, m_stackMin)</c> to <c>min(max stack, m_stackMax)</c>, both included; like the game's, it
/// returns one shared list that its next call clears, so copy it to keep it. <see cref="Clone"/> is shallow: the copy
/// shares <see cref="m_drops"/>.
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

    private static readonly List<ItemDrop.ItemData> s_sharedItems = new();
    public List<DropData> m_drops = new();
    public int m_dropMin = 1;
    public int m_dropMax = 1;
    public float m_dropChance = 1f;
    public bool m_oneOfEach;

    public DropTable Clone() => (DropTable)MemberwiseClone();
    public bool IsEmpty() => m_drops.Count == 0;

    public List<GameObject> GetDropList()
    {
        int draws = UnityEngine.Random.Range(m_dropMin, m_dropMax + 1);
        var objects = new List<GameObject>();
        if (!ChanceHolds()) return objects;
        foreach (var (entry, fallback) in Draws(draws))
        {
            int count = fallback ? 1 : entry.m_dontScale ? UnityEngine.Random.Range(entry.m_stackMin, entry.m_stackMax) : RoundedStack(entry);
            objects.AddRange(Enumerable.Repeat(entry.m_item, Math.Max(0, count)));
        }
        return objects;
    }

    public List<ItemDrop.ItemData> GetDropListItems()
    {
        s_sharedItems.Clear();
        if (!ChanceHolds()) return s_sharedItems;
        foreach (var (entry, _) in Draws(UnityEngine.Random.Range(m_dropMin, m_dropMax + 1))) s_sharedItems.Add(ItemFor(entry));
        return s_sharedItems;
    }

    private bool ChanceHolds() => m_drops.Count > 0 && !(UnityEngine.Random.value > m_dropChance);
    private static int RoundedStack(DropData entry) =>
        (int)Mathf.Max(1f, Mathf.Round(UnityEngine.Random.Range(Mathf.Round(entry.m_stackMin), Mathf.Round(entry.m_stackMax))));

    // Each draw, lazily: the caller rolls its stack before a one-of-each entry leaves the pool, as the game orders its rolls.
    private IEnumerable<(DropData Entry, bool Fallback)> Draws(int count)
    {
        var pool = new List<DropData>(m_drops);
        // The total shrinks by what leaves the pool rather than being summed again, as the game keeps it.
        float total = WeightedChoice.Sum(pool.Select(d => d.m_weight));
        for (int n = 0; n < count; n++)
        {
            int index = WeightedChoice.Roll(pool.Select(d => d.m_weight).ToList(), total);
            if (index < 0) { if (pool.Count > 0) yield return (pool[0], true); continue; }
            var entry = pool[index];
            yield return (entry, false);
            if (m_oneOfEach) { pool.RemoveAt(index); total -= entry.m_weight; }
        }
    }

    private static ItemDrop.ItemData ItemFor(DropData entry)
    {
        var template = entry.m_item.GetComponent<ItemDrop>().m_itemData;
        var item = template.Clone();
        item.m_dropPrefab = entry.m_item;
        item.m_stack = UnityEngine.Random.Range(Math.Max(1, entry.m_stackMin), Math.Min(template.m_shared.m_maxStackSize, entry.m_stackMax) + 1);
        item.m_worldLevel = 0;
        return item;
    }
}

/// <summary>
/// A grid of item stacks. Adding an item first tops up non-full stacks of the same name, quality and world level one unit
/// at a time; what is left takes the first empty slot, scanning rows from the top for weapons, tools, shields, utility,
/// misc and trinkets and from the bottom for everything else, as the game does. An item that fills no slot is refused.
/// </summary>
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
        int merged = 0;
        if (item.m_shared.m_maxStackSize > 1)
            for (; merged < item.m_stack && OpenStackFor(item) is { } open; merged++) { open.m_stack++; if (item.m_cheated) open.m_cheated = true; }
        bool everythingMerged = item.m_shared.m_maxStackSize > 1 && merged >= item.m_stack;
        if (!everythingMerged)
        {
            item.m_stack -= merged;
            added = TakeSlot(item);
        }
        m_onChanged?.Invoke();
        return added;
    }
    private ItemDrop.ItemData? OpenStackFor(ItemDrop.ItemData item) =>
        m_inventory.Find(s => s.m_shared.m_name == item.m_shared.m_name && s.m_quality == item.m_quality && s.m_worldLevel == item.m_worldLevel && s.m_stack < s.m_shared.m_maxStackSize);
    private bool TakeSlot(ItemDrop.ItemData item)
    {
        var free = SlotsInOrder(FromTop(item)).Where(p => GetItemAt(p.x, p.y) == null).Select(p => (Vector2i?)p).FirstOrDefault();
        if (free is not { } slot) return false;
        item.m_gridPos = slot;
        m_inventory.Add(item);
        return true;
    }
    private static bool FromTop(ItemDrop.ItemData item)
    {
        var type = item.m_shared.m_itemType;
        return item.IsWeapon() || type is ItemDrop.ItemData.ItemType.Tool or ItemDrop.ItemData.ItemType.Shield or ItemDrop.ItemData.ItemType.Utility
            or ItemDrop.ItemData.ItemType.Misc or ItemDrop.ItemData.ItemType.Trinket;
    }
    private IEnumerable<Vector2i> SlotsInOrder(bool fromTop)
    {
        var rows = Enumerable.Range(0, m_height);
        foreach (int y in fromTop ? rows : rows.Reverse())
            for (int x = 0; x < m_width; x++) yield return new Vector2i(x, y);
    }
    public ItemDrop.ItemData? GetItemAt(int x, int y) => m_inventory.Find(i => i.m_gridPos.x == x && i.m_gridPos.y == y);
    public List<ItemDrop.ItemData> GetAllItems() => m_inventory;
    public int NrOfItems() => m_inventory.Count;
    public int NrOfItemsIncludingStacks() => m_inventory.Sum(i => i.m_stack);
    /// <summary>Units of items with this shared name (any name when null), of this quality (any when negative); the world level always matches here (it is 0).</summary>
    public int CountItems(string name, int quality = -1, bool matchWorldLevel = true) =>
        m_inventory.Where(i => (name == null || i.m_shared.m_name == name) && (quality < 0 || i.m_quality == quality) && (!matchWorldLevel || i.m_worldLevel >= 0)).Sum(i => i.m_stack);
    public bool HaveEmptySlot() => m_inventory.Count < m_width * m_height;
    public int GetEmptySlots() => m_width * m_height - m_inventory.Count;
    public bool RemoveItem(ItemDrop.ItemData item) { bool removed = m_inventory.Remove(item); if (removed) m_onChanged?.Invoke(); return removed; }
}

/// <summary>
/// A container. On waking with a ZDO it makes its inventory, and the ZDO's owner fills it from
/// <see cref="m_defaultItems"/> once, marking the ZDO so a later load (or another peer) does not fill it again. It needs a
/// ZNetView on its object, as in the game.
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
        if (m_nview!.GetZDO() is not { } zdo) return;
        m_inventory = new Inventory(m_name, null, m_width, m_height);
        bool filled = zdo.GetBool(ZDOVars.s_addedDefaultItems);
        if (filled || !m_nview.IsOwner()) return;
        m_defaultItems.GetDropListItems().ForEach(item => m_inventory.AddItem(item));
        zdo.Set(ZDOVars.s_addedDefaultItems, true);
    }
    public Inventory GetInventory() => m_inventory!;
}

/// <summary>
/// Something to pick. Waking with a ZDO, it reads whether it was picked and whether it is enabled (writing its own
/// enabled state when that was set before it woke and it owns the ZDO) and shows <see cref="m_hideWhenPicked"/> only
/// while unpicked and enabled; one that neither respawns nor hides a part and was already picked is claimed and removed.
/// Picking shows or hides the part, and then, when owned, saves the picked state if the pickable respawns or hides a
/// part, and otherwise removes it. Respawn timing is not modelled.
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
    private int m_enabled = 2; // 2: not known yet, taken from the ZDO on waking

    public int GetEnabled => m_enabled;
    private bool KeepsObjectWhenPicked => m_respawnTimeMinutes > 0f || m_hideWhenPicked != null;

    private void Awake()
    {
        m_nview = GetComponent<ZNetView>();
        if (m_nview!.GetZDO() is not { } zdo) return;
        m_picked = zdo.GetBool(ZDOVars.s_picked, m_defaultPicked);
        if (m_enabled != 2) { if (m_nview.IsOwner()) zdo.Set(ZDOVars.s_enabled, m_enabled == 1); }
        else m_enabled = zdo.GetBool(ZDOVars.s_enabled, true) ? 1 : 0;
        ShowPart(!m_picked && m_enabled == 1);
        bool leftOverFromAPick = !KeepsObjectWhenPicked && zdo.GetBool(ZDOVars.s_picked);
        if (leftOverFromAPick) { m_nview.ClaimOwnership(); ZNetScene.instance!.Destroy(gameObject); }
    }
    private void ShowPart(bool show) { if (m_hideWhenPicked) m_hideWhenPicked!.SetActive(show); }

    public string GetHoverName() => string.IsNullOrEmpty(m_overrideName) ? m_itemPrefab!.GetComponent<ItemDrop>().m_itemData.m_shared.m_name : m_overrideName;
    /// <summary>The hover text, localized: empty once picked or disabled.</summary>
    public string GetHoverText() => m_picked || m_enabled == 0 ? "" : Localization.instance.Localize(GetHoverName() + "\n[<color=yellow><b>$KEY_Use</b></color>] $inventory_pickup");
    public void SetPicked(bool picked)
    {
        m_picked = picked;
        ShowPart(!picked);
        if (!m_nview || !m_nview!.IsOwner()) return;
        if (KeepsObjectWhenPicked) m_nview.GetZDO().Set(ZDOVars.s_picked, picked);
        else if (picked) ZNetScene.instance!.Destroy(gameObject);
    }
    public bool GetPicked() => m_picked;
    public void SetEnabled(bool value) => SetEnabled(value ? 1 : 0);
    /// <summary>As the game's, the ZDO gets this component's own enabled flag, not <paramref name="value"/>. (The game then shows the hidden part by respawn time, which is not modelled.)</summary>
    public void SetEnabled(int value)
    {
        m_enabled = value;
        if (m_nview && m_nview!.IsOwner() && m_nview.GetZDO() != null) m_nview.GetZDO().Set(ZDOVars.s_enabled, enabled);
    }
    /// <summary>True while the hidden part is showing, otherwise when unpicked and enabled.</summary>
    public bool CanBePicked() => (m_hideWhenPicked && m_hideWhenPicked!.activeInHierarchy) || (!m_picked && m_enabled == 1);
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

    /// <summary>A pick by weight (private in the game): null when there is nothing to pick, the last entry when a roll finds none.</summary>
    public SpawnData? SelectWeightedPrefab()
    {
        if (m_prefabs.Count == 0) return null;
        var weights = m_prefabs.Select(p => p.m_weight).ToList();
        int index = WeightedChoice.Roll(weights, WeightedChoice.Sum(weights));
        return m_prefabs[index >= 0 ? index : m_prefabs.Count - 1];
    }
}

public static partial class ZDOVars
{
    public static readonly int s_addedDefaultItems = "addedDefaultItems".GetStableHashCode();
    public static readonly int s_picked = "picked".GetStableHashCode();
    public static readonly int s_enabled = "enabled".GetStableHashCode();
}
