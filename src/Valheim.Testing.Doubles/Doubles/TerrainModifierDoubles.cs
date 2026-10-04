// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Terrain modifiers (the level/smooth/paint components on pieces and locations) and the order a heightmap applies
// them in, as Valheim 1.0.16 does: non-player before player, then lower m_sortOrder, then earlier creation time
// (read from the ZDO), then closer to the world origin (the full 3D position, height included). The list is sorted
// only when a modifier joins or leaves it. What a modifier does to the heights is not modelled. It is a MonoBehaviour, as in
// the game: AddComponent, GetComponent and Utils.GetEnabledComponentsInChildren find it on an object, and its position is
// its object's. Added to an active object it wakes (Awake) and joins the live list, as a spawned piece's does.
using System.Collections.Generic;
using System.Linq;
using Valheim.Testing.Doubles;

public partial class TerrainModifier : UnityEngine.MonoBehaviour
{
    public enum PaintType { Dirt, Cultivate, Paved, Reset, ClearVegetation, DeepSnow }

    public int m_sortOrder;
    public bool m_useTerrainCompiler;
    /// <summary>Set on the player's terrain tools' pieces (the game's own spelling).</summary>
    public bool m_playerModifiction;
    public float m_levelOffset;
    public bool m_level;
    public float m_levelRadius = 2f;
    public bool m_square = true;
    public bool m_smooth;
    public float m_smoothRadius = 2f;
    public float m_smoothPower = 3f;
    public bool m_paintCleared = true;
    public bool m_paintHeightCheck;
    public PaintType m_paintType;
    public float m_paintRadius = 2f;
    public float m_paintStrength = 1f;
    // Unity's Behaviour.enabled: a disabled modifier is sorted but not applied.
    /// <summary>The object's ZNetView, read in Awake as the game does when it is not set; null for a modifier without one (its creation time is then 0).</summary>
    public ZNetView? m_nview;

    private bool m_wasEnabled;
    private long m_creationTime;

    // The live modifiers, as the game's static list. ValheimWorldScope.WithTerrainModifiers installs a fresh one.
    internal static List<TerrainModifier> s_instances = new();
    internal static bool s_needsSorting;

    public TerrainModifier() { }
    /// <summary>
    /// A modifier on a new object of its own at <paramref name="position"/>, not yet awake: call <see cref="Awake"/> to spawn
    /// it into the live list. A test convenience; <c>AddComponent&lt;TerrainModifier&gt;()</c> is the game's way.
    /// </summary>
    [TestOnly] public TerrainModifier(UnityEngine.Vector3 position, ZNetView? view = null)
    {
        var owner = new UnityEngine.GameObject("TerrainModifier");
        owner.transform.position = position;
        owner.Attach(this);
        m_nview = view;
    }

    /// <summary>
    /// As the game's Awake when the object spawns: the modifier joins the live list (sorted again before the next
    /// read), pokes every loaded heightmap it overlaps when enabled, and reads its creation time from its ZDO.
    /// </summary>
    public void Awake()
    {
        m_unityAwoken = true; // Called by a test or by Unity's message: either way it woke once.
        if (m_nview == null && gameObject is { } owner) m_nview = owner.GetComponent<ZNetView>();
        s_instances.Add(this);
        s_needsSorting = true;
        m_wasEnabled = enabled;
        if (enabled) PokeHeightmaps();
        m_creationTime = GetCreationTime();
    }

    /// <summary>As the game's OnDestroy: the modifier leaves the list, and the heightmaps it overlapped are poked if it was enabled at Awake.</summary>
    public void OnDestroy()
    {
        s_instances.Remove(this);
        s_needsSorting = true;
        if (m_wasEnabled) PokeHeightmaps();
    }

    public static void RemoveAll() => s_instances.Clear();

    // The game delays the rebuild to the late pass (2) unless it is placing pieces with "trigger on placed".
    private void PokeHeightmaps()
    {
        foreach (var heightmap in Heightmap.GetAllHeightmaps())
            if (heightmap.TerrainVSModifier(this)) heightmap.Poke(2);
    }

    /// <summary>The largest radius among its enabled operations (level, smooth, paint), as the game's.</summary>
    public float GetRadius()
    {
        float radius = 0f;
        if (m_level && m_levelRadius > radius) radius = m_levelRadius;
        if (m_smooth && m_smoothRadius > radius) radius = m_smoothRadius;
        if (m_paintCleared && m_paintRadius > radius) radius = m_paintRadius;
        return radius;
    }

    /// <summary>
    /// The creation time the order uses, read at <see cref="Awake"/> from the ZDO's <c>terrainModifierTimeCreated</c>
    /// long. Valheim 1.0.16 writes that value only when it converts a world saved before its ZDO rework; a modifier
    /// placed since reads 0, so it sorts before every converted one of the same kind and order.
    /// </summary>
    [TestOnly] public long CreationTime => m_creationTime;

    private long GetCreationTime()
    {
        var zdo = m_nview?.GetZDO();
        return zdo == null ? 0L : zdo.GetLong(ZDOVars.s_terrainModifierTimeCreated, 0L);
    }

    public ZDOID GetZDOID() => m_nview?.GetZDO()?.m_uid ?? ZDOID.None;

    /// <summary>
    /// The game's order (private there): non-player first (false before true), then lower <see cref="m_sortOrder"/>,
    /// then earlier <see cref="CreationTime"/>, then smaller squared distance of the whole position from the origin.
    /// </summary>
    public static int SortByModifiers(TerrainModifier a, TerrainModifier b)
    {
        if (a.m_playerModifiction != b.m_playerModifiction) return a.m_playerModifiction.CompareTo(b.m_playerModifiction);
        if (a.m_sortOrder != b.m_sortOrder) return a.m_sortOrder.CompareTo(b.m_sortOrder);
        if (a.m_creationTime != b.m_creationTime) return a.m_creationTime.CompareTo(b.m_creationTime);
        return UnityEngine.Vector3.SqrMagnitude(a.transform.position).CompareTo(UnityEngine.Vector3.SqrMagnitude(b.transform.position));
    }

    /// <summary>
    /// The live modifiers in application order: the list is sorted when a modifier has joined or left since the last
    /// read, and not otherwise, so moving a modifier or changing its sort order takes effect at the next join or leave.
    /// The game's sort is unstable; modifiers equal on every key keep their joining order here, in no defined order there.
    /// </summary>
    public static List<TerrainModifier> GetAllInstances()
    {
        if (s_needsSorting)
        {
            var sorted = s_instances.OrderBy(m => m, Comparer<TerrainModifier>.Create(SortByModifiers)).ToList();
            s_instances.Clear();
            s_instances.AddRange(sorted);
            s_needsSorting = false;
        }
        return s_instances;
    }
}

public static partial class ZDOVars
{
    /// <summary>A terrain modifier's creation time, which its place in the order uses.</summary>
    public static readonly int s_terrainModifierTimeCreated = "terrainModifierTimeCreated".GetStableHashCode();
}

public partial class Heightmap
{
    /// <summary>Whether the modifier's reach (radius + 4 m) touches this heightmap's square, edges included, as the game's.</summary>
    public bool TerrainVSModifier(TerrainModifier modifier)
    {
        var position = modifier.transform.position;
        float reach = modifier.GetRadius() + 4f;
        var centre = transform.position;
        float half = m_width * m_scale * 0.5f;
        return !(position.x + reach < centre.x - half || position.x - reach > centre.x + half
            || position.z + reach < centre.z - half || position.z - reach > centre.z + half);
    }

    /// <summary>Whether the modifier's radius + 0.1 m lies wholly inside this heightmap's square, as the game's.</summary>
    public bool CheckTerrainModIsContained(TerrainModifier modifier)
    {
        var position = modifier.transform.position;
        float reach = modifier.GetRadius() + 0.1f;
        var centre = transform.position;
        float half = m_width * m_scale * 0.5f;
        return !(position.x + reach > centre.x + half || position.x - reach < centre.x - half
            || position.z + reach > centre.z + half || position.z - reach < centre.z - half);
    }

    /// <summary>
    /// The modifiers this heightmap's rebuild applies, in the order it applies them: every enabled live modifier that
    /// overlaps it (<see cref="TerrainVSModifier"/>), in <see cref="TerrainModifier.GetAllInstances"/> order. The game
    /// applies the zone's terrain compiler (<see cref="TerrainComp"/>) after all of them.
    /// </summary>
    [TestOnly] public List<TerrainModifier> ModifiersInApplyOrder()
    {
        var applied = new List<TerrainModifier>();
        foreach (var modifier in TerrainModifier.GetAllInstances())
            if (modifier.enabled && TerrainVSModifier(modifier)) applied.Add(modifier);
        return applied;
    }
}

namespace Valheim.Testing.Doubles
{
    public sealed partial class ValheimWorldScope
    {
        /// <summary>No live terrain modifiers yet: the modifiers this test wakes join a fresh list.</summary>
        public ValheimWorldScope WithTerrainModifiers()
        {
            global::TerrainModifier.s_instances = new List<global::TerrainModifier>();
            global::TerrainModifier.s_needsSorting = false;
            return this;
        }
    }
}
