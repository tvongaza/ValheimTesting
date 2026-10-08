using Valheim.Testing.Doubles;
// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Valheim types. WorldGenerator is virtual so tests plug in synthetic worlds.

/// <summary>
/// Mirror of Valheim's string.GetStableHashCode extension (Utils), the same result as the game's for every string:
/// two djb2-style accumulators over alternate UTF-16 code units, stopping at the end or at a '\0'. ZDO values,
/// prefabs and ZDOVars are keyed by it.
/// </summary>
public static partial class StringExtensionMethods
{
    public static int GetStableHashCode(this string str)
    {
        unchecked
        {
            int hash1 = 5381;
            int hash2 = hash1;
            for (int i = 0; i < str.Length && str[i] != '\0'; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ str[i];
                if (i == str.Length - 1 || str[i + 1] == '\0')
                    break;
                hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
            }
            return hash1 + hash2 * 1566083941;
        }
    }
}

/// <summary>Mirror of Valheim's global Vector2i (integer grid coordinate).</summary>
public partial struct Vector2i : System.IEquatable<Vector2i>
{
    public int x;
    public int y;

    public Vector2i(int x, int y)
    {
        this.x = x;
        this.y = y;
    }

    public override bool Equals(object? other) =>
        other is Vector2i v && v.x == x && v.y == y;

    public bool Equals(Vector2i other) => x == other.x && y == other.y;

    public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode();

    public static bool operator ==(Vector2i a, Vector2i b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(Vector2i a, Vector2i b) => !(a == b);

    public override string ToString() => $"({x}, {y})";
}

/// <summary>
/// Mirror of Valheim 1.0's global Vector2s, which replaced Vector2i as the
/// type of a zone id. The fields really are short in the game: a zone id is
/// small and the game packs a lot of them, so the mod must not assume it can
/// put an arbitrary int in one. The int constructor narrows exactly as the
/// game's does, which is why the mod's own grid coordinates stay Vector2i.
/// </summary>
public partial struct Vector2s
{
    public short x;
    public short y;

    public Vector2s(short x, short y)
    {
        this.x = x;
        this.y = y;
    }

    public Vector2s(int x, int y)
    {
        this.x = (short)x;
        this.y = (short)y;
    }

    public Vector2s(Vector2i v)
    {
        x = (short)v.x;
        y = (short)v.y;
    }

    public override bool Equals(object? other) =>
        other is Vector2s v && v.x == x && v.y == y;

    public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 16);

    public static Vector2s operator +(Vector2s a, Vector2s b) =>
        new Vector2s((short)(a.x + b.x), (short)(a.y + b.y));
    public static Vector2s operator -(Vector2s a, Vector2s b) =>
        new Vector2s((short)(a.x - b.x), (short)(a.y - b.y));
    public static bool operator ==(Vector2s a, Vector2s b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(Vector2s a, Vector2s b) => !(a == b);

    public override string ToString() => $"({x}, {y})";
}

public partial class Heightmap
{
    [System.Flags]
    public enum Biome
    {
        None = 0,
        Meadows = 1,
        Swamp = 2,
        Mountain = 4,
        BlackForest = 8,
        Plains = 16,
        AshLands = 32,
        DeepNorth = 64,
        Ocean = 256,
        Mistlands = 512,
    }

    // --- terrain-modifier surface ---
    // A zone heightmap: m_width vertices per side plus one, m_scale metres
    // per vertex, centred on transform.position. Tests build one per zone
    // with a TerrainComp whose arrays start zeroed, exactly like a fresh
    // _TerrainCompiler in the game.
    public static UnityEngine.Color m_paintMaskDirt = new(1f, 0f, 0f, 1f);
    public static UnityEngine.Color m_paintMaskCultivated = new(0f, 1f, 0f, 1f);
    public static UnityEngine.Color m_paintMaskPaved = new(0f, 0f, 1f, 1f);
    public static UnityEngine.Color m_paintMaskNothing = new(0f, 0f, 0f, 1f);
    public static UnityEngine.Color m_paintMaskClearVegetation = new(0f, 0f, 0f, 0f);
    public static UnityEngine.Color m_paintMaskDeepSnow = new(1f, 1f, 1f, 1f);
    /// <summary>The generated build this heightmap was made from (the game's private field); null until a test sets one.</summary>
    public HeightmapBuilder.HMBuildData? m_buildData;
    /// <summary>The loaded zone heightmaps, as the game's own list. <c>ValheimWorldScope.RegisterHeightmap</c> loads one per zone.</summary>
    public static readonly System.Collections.Generic.List<Heightmap> s_heightmaps = new();
    /// <summary>
    /// A single-zone shorthand over <see cref="s_heightmaps"/>: the heightmap loaded last; setting it leaves that one
    /// heightmap loaded (or none).
    /// </summary>
    [TestOnly] public static Heightmap? Registered
    {
        get => s_heightmaps.Count == 0 ? null : s_heightmaps[s_heightmaps.Count - 1];
        set { s_heightmaps.Clear(); if (value != null) s_heightmaps.Add(value); }
    }

    public readonly UnityEngine.Transform transform = new();
    public int m_width = 64;
    public float m_scale = 1f;
    [TestOnly] public TerrainComp? m_terrainComp;
    [TestOnly] public int PokeCount;

    /// <summary>Whether the point lies in this heightmap's zone (edges included), as in the game.</summary>
    public bool IsPointInside(UnityEngine.Vector3 point, float radius = 0f)
    {
        float half = m_width * m_scale * 0.5f; var centre = transform.position;
        return point.x + radius >= centre.x - half && point.x - radius <= centre.x + half
            && point.z + radius >= centre.z - half && point.z - radius <= centre.z + half;
    }
    /// <summary>The first loaded heightmap that contains the point, or null where no zone is loaded, as in the game.</summary>
    public static Heightmap? FindHeightmap(UnityEngine.Vector3 point) => s_heightmaps.Find(h => h.IsPointInside(point));
    public static System.Collections.Generic.List<Heightmap> GetAllHeightmaps() => s_heightmaps;
    /// <summary>Like the game: the zone's live compiler, or a new one (with a new ZDO) if it has none.</summary>
    public TerrainComp GetAndCreateTerrainCompiler() => m_terrainComp ??= new TerrainComp(this, m_width);
    /// <summary>Valheim 1.0: the argument selects which late pass rebuilds
    /// (1 = LateUpdate, 2 = CustomLateUpdate), it is not a frame count.</summary>
    // Most existing writer tests advance straight to the requested late pass.
    // Lifecycle regressions turn this off to exercise the intervening state.
    [TestOnly] public bool AutoRebuild = true;
    [TestOnly] public System.Func<float, float, float>? AuthoredHeight;
    [TestOnly] public System.Collections.Generic.List<float>? LastRenderedHeights;
    public void Poke(int delayed = 0, bool paintOnly = false)
    {
        PokeCount++;
        if (AutoRebuild) RebuildTerrain();
    }
    public void Regenerate() => RebuildTerrain();
    [TestOnly] public void RebuildTerrain()
    {
        int width = m_width;
        var compiler = m_terrainComp;
        if (compiler != null && compiler.m_width != width)
            throw new System.InvalidOperationException($"The zone's compiler is {compiler.m_width} wide and its heightmap {width}; the game sizes the compiler from the heightmap.");
        var heights = new System.Collections.Generic.List<float>();
        for (int z = 0; z <= width; z++)
            for (int x = 0; x <= width; x++)
            {
                float wx = transform.position.x + (x - width / 2f) * m_scale;
                float wz = transform.position.z + (z - width / 2f) * m_scale;
                float h = AuthoredHeight != null ? AuthoredHeight(wx, wz) : BaseHeight(wx, wz);
                heights.Add(h - transform.position.y);
            }
        // As the game's Heightmap.ApplyModifiers (1.0.16): without a compiler in the zone, ApplyToHeightmap is not called.
        if (compiler != null)
        {
            var baseline = heights.ToArray();
            // The seam a mod's Harmony prefix on ApplyToHeightmap uses: no compiler deltas are in these heights yet.
            ModTerrainPass(heights);
            // As ApplyToHeightmap: nothing from a compiler that is not initialized; otherwise every vertex with a non-zero
            // level or smooth delta, flagged as modified or not, gets both and only those vertices are clamped to 8 m
            // around the base height. The flag decides only what Save keeps.
            if (compiler.m_initialized)
            {
                float[] levels = compiler.m_levelDelta, smooths = compiler.m_smoothDelta;
                for (int i = 0; i < heights.Count; i++)
                    if (levels[i] != 0f || smooths[i] != 0f)
                        heights[i] = UnityEngine.Mathf.Clamp(heights[i] + levels[i] + smooths[i], baseline[i] - 8f, baseline[i] + 8f);
            }
        }
        LastRenderedHeights = heights;
    }

    private float BaseHeight(float wx, float wz)
    {
        float height = WorldGenerator.instance?.GetHeight(wx, wz) ?? 0f;
        ModBaseHeight(wx, wz, ref height);
        return height;
    }
    /// <summary>Implement in your partial Heightmap when your mod's own code decides the base height (for example a biome blend).</summary>
    partial void ModBaseHeight(float wx, float wz, ref float height);
    /// <summary>Implement in your partial Heightmap to run your mod's pass on the heights before compiler deltas are added.</summary>
    partial void ModTerrainPass(System.Collections.Generic.List<float> heights);

    [TestOnly] public static Heightmap CreateForZone(Vector2s zoneID, int width = 64, bool withCompiler = true)
    {
        var hm = new Heightmap { m_width = width, m_scale = ZoneSystem.ZoneSize / width };
        hm.transform.position = ZoneSystem.GetZonePos(zoneID);
        if (withCompiler)
            hm.m_terrainComp = new TerrainComp(hm, width);
        return hm;
    }
}

/// <summary>
/// Shim for ZNetView: one ZDO behind it, ours unless a test says otherwise. Its RPCs are in NetworkDoubles.cs. Its
/// <c>gameObject</c> is null for a view a test builds around a bare ZDO.
/// </summary>
public partial class ZNetView : UnityEngine.MonoBehaviour
{
    [TestOnly] public ZDO Zdo;
    /// <summary>True between StartGhostInit and FinishGhostInit: new objects get ZDOs but join no live scene.</summary>
    [TestOnly] public static bool GhostInit { get; private set; }
    public static void StartGhostInit() => GhostInit = true;
    public static void FinishGhostInit() => GhostInit = false;
    /// <summary>Whether the object's ZDO is saved with the world. The game's default is false; a prefab sets it.</summary>
    public bool m_persistent;
    /// <summary>Whether the object's authored scale is sent with its ZDO, and a loaded ZDO's scale applied when the view wakes (the game's default is false).</summary>
    public bool m_syncInitialScale;
    [TestOnly] public ZNetView(ZDO zdo) { Zdo = zdo; if (zdo != null) zdo.m_view = this; }
    /// <summary>A view with no ZDO yet, as <c>AddComponent&lt;ZNetView&gt;()</c> on a prefab makes one; a test gives it a ZDO with <see cref="Zdo"/>.</summary>
    public ZNetView() { Zdo = null!; }
    public bool IsValid() => Zdo != null;
    public bool IsOwner() => Zdo.IsOwner();
    public bool HasOwner() => Zdo.HasOwner();
    public void ClaimOwnership() { if (!IsOwner()) Zdo.SetOwner(ZDOMan.GetSessionID()); }
    public ZDO GetZDO() => Zdo;
    /// <summary>As the game's: the view lets go of its ZDO, so GetZDO() returns null and IsValid() false.</summary>
    public void ResetZDO() => Zdo = null!;

    /// <summary>Set by the game's ZNetScene while it instantiates the object for a loaded ZDO (<see cref="m_initZDO"/>).</summary>
    public static bool m_useInitZDO;
    /// <summary>The loaded ZDO the next view to wake takes instead of a new one, as ZNetScene sets it before <c>Instantiate</c>.</summary>
    public static ZDO? m_initZDO;
    /// <summary>While true, a view that wakes destroys itself, as in the game.</summary>
    public static bool m_forceDisableInit;
    private UnityEngine.Vector3 m_lastLocalScale = new(1f, 1f, 1f);

    // As the game's ZNetView.Awake (1.0.16) when an object with a view comes alive (Instantiate, or AddComponent on an
    // active scene object; a prefab asset never wakes): with no ZDOMan, or while init is disabled, the view destroys
    // itself. It takes m_initZDO when one is set (and its scale, with m_syncInitialScale); otherwise a new ZDO of its
    // object's prefab name at the object's position and rotation, owned by this session, persistent as m_persistent says
    // (with the authored scale, with m_syncInitialScale), which stays out of the live scene under ghost initialisation.
    // The object then joins the live scene. The ZDO's type and distant flags are not modelled.
    private void Awake()
    {
        if (m_forceDisableInit || ZDOMan.instance == null) { Destroy(this); return; }
        if (m_useInitZDO && m_initZDO == null) ZLog.LogWarning("Double ZNetview when initializing object " + gameObject.name);
        if (m_initZDO != null)
        {
            Zdo = m_initZDO; m_initZDO = null;
            if (m_syncInitialScale)
            {
                var scale = Zdo.GetVec3(ZDOVars.s_scaleHash, UnityEngine.Vector3.zero);
                if (!scale.Equals(UnityEngine.Vector3.zero)) transform.localScale = scale;
                else
                {
                    float scalar = Zdo.GetFloat(ZDOVars.s_scaleScalarHash, transform.localScale.x);
                    if (!transform.localScale.x.Equals(scalar)) transform.localScale = new UnityEngine.Vector3(scalar, scalar, scalar);
                }
            }
        }
        else
        {
            int prefab = Utils.GetPrefabName(gameObject).GetStableHashCode();
            Zdo = ZDOMan.instance.CreateNewZDO(transform.position, prefab);
            Zdo.SetOwner(ZDOMan.instance.m_sessionID); // the game's CreateNewZDO makes this session the owner
            Zdo.Persistent = m_persistent;
            Zdo.SetRotation(transform.rotation);
            if (m_syncInitialScale) SyncScale();
            if (GhostInit) return;
        }
        ZNetScene.instance?.Live.Add(gameObject);
    }
    private void SyncScale()
    {
        if (m_lastLocalScale.Equals(transform.localScale)) return;
        m_lastLocalScale = transform.localScale;
        Zdo.Set(ZDOVars.s_scaleHash, m_lastLocalScale);
    }
}

/// <summary>
/// Mirror of Valheim's ZDOID: the creating session's id and the object's number (a uint in the game, which is how a
/// package carries it). The doubles' ZDOs number themselves and leave the session id 0.
/// </summary>
public partial struct ZDOID : System.IEquatable<ZDOID>
{
    public readonly long UserID;
    public uint ID { get; private set; }
    public ZDOID(long userID, uint id) { UserID = userID; ID = id; }
    /// <summary>No object; what a peer's character id is before it spawns.</summary>
    public static ZDOID None => default;
    public bool IsNone() => UserID == 0 && ID == 0;
    /// <summary>The session id, a colon and the number, as the game writes a ZDOID ("0:12" for a doubles-made object).</summary>
    public override string ToString() => UserID + ":" + ID;
    public bool Equals(ZDOID other) => UserID == other.UserID && ID == other.ID;
    public override bool Equals(object? obj) => obj is ZDOID other && Equals(other);
    public override int GetHashCode() => ID.GetHashCode();
    public static bool operator ==(ZDOID a, ZDOID b) => a.Equals(b);
    public static bool operator !=(ZDOID a, ZDOID b) => !a.Equals(b);
}

/// <summary>
/// Shim for a Valheim ZDO: a networked object's identity, owner and position, and its typed values
/// (ZdoDoubles.cs), which are keyed by name hash as the game keys them.
/// </summary>
public partial class ZDO
{
    private static uint s_nextId = 1;

    public ZDOID m_uid = new(0L, s_nextId++);
    public bool Persistent;
    private int m_prefab;
    private long m_owner;
    private UnityEngine.Vector3 m_position;

    [TestOnly] public ZDO(UnityEngine.Vector3 position, int prefab)
    {
        m_position = position;
        m_prefab = prefab;
    }

    public void SetPrefab(int prefab) => m_prefab = prefab;
    public int GetPrefab() => m_prefab;
    public void SetOwner(long owner) => m_owner = owner;
    public long GetOwner() => m_owner;
    public bool HasOwner() => m_owner != 0;
    /// <summary>Ours when it carries our session id; without a ZDOMan every ZDO counts as ours.</summary>
    public bool IsOwner() => ZDOMan.instance == null || m_owner == ZDOMan.instance.m_sessionID;
    public UnityEngine.Vector3 GetPosition() => m_position;
    public Vector2s GetSector() => ZoneSystem.GetZone(m_position);
    public void SetPosition(UnityEngine.Vector3 position) => m_position = position;
    // The game keeps the rotation as Euler angles (a Vector3), so a rotation comes back as the Euler of those angles.
    private UnityEngine.Vector3 m_rotation;
    public UnityEngine.Quaternion GetRotation() => UnityEngine.Quaternion.Euler(m_rotation);
    public void SetRotation(UnityEngine.Quaternion rotation) => m_rotation = rotation.eulerAngles;
    // The typed values, keyed by hash, and what a save and reload keep of them: ZdoDoubles.cs.
}

/// <summary>Shim for ZDOMan: the world's ZDOs as a list. Tests create one per world.</summary>
public partial class ZDOMan
{
    [TestOnly] public ZDOMan() { }
    public static ZDOMan? instance { get; private set; }

    public readonly long m_sessionID = 1;
    [TestOnly] public readonly System.Collections.Generic.List<ZDO> Zdos = new();
    /// <summary>ZDOs destroyed but not yet removed: the game queues destruction, so they stay visible until processed.</summary>
    [TestOnly] public readonly System.Collections.Generic.List<ZDO> DestroyQueue = new();
    public System.Collections.Generic.Dictionary<ZDOID, ZDO> m_objectsByID
    {
        get { var byId = new System.Collections.Generic.Dictionary<ZDOID, ZDO>(); foreach (var zdo in Zdos) byId[zdo.m_uid] = zdo; return byId; }
    }
    /// <summary>
    /// This session's id, the one source of "who am I" (<c>ZNet.GetUID()</c> and <c>ZRoutedRpc</c>'s peer id read it, as
    /// in the game). Without a ZDOMan it is 1, the default <see cref="m_sessionID"/>: never 0, which is
    /// <c>ZRoutedRpc.Everybody</c>.
    /// </summary>
    public static long GetSessionID() => instance?.m_sessionID ?? 1;
    public ZDO? GetZDO(ZDOID id) => id.IsNone() ? null : Zdos.Find(zdo => zdo.m_uid.Equals(id));
    public void DestroyZDO(ZDO zdo) { if (!DestroyQueue.Contains(zdo)) DestroyQueue.Add(zdo); }
    /// <summary>Removes the queued ZDOs, as the game's next update does; returns how many went.</summary>
    [TestOnly] public int ProcessDestroyed()
    {
        int removed = 0;
        foreach (var zdo in DestroyQueue) if (Zdos.Remove(zdo)) removed++;
        DestroyQueue.Clear();
        return removed;
    }

    public ZDO CreateNewZDO(UnityEngine.Vector3 position, int prefabHash)
    {
        var zdo = new ZDO(position, prefabHash);
        Zdos.Add(zdo);
        return zdo;
    }

    /// <summary>Adds every ZDO of the prefab to the list; the real one pages, this one finishes in a single call.</summary>
    public bool GetAllZDOsWithPrefabIterative(string prefab, System.Collections.Generic.List<ZDO> zdos, ref int index)
    {
        int hash = prefab.GetStableHashCode();
        foreach (var zdo in Zdos)
            if (zdo.GetPrefab() == hash)
                zdos.Add(zdo);
        index = Zdos.Count;
        return true;
    }

    /// <summary>
    /// The ZDOs whose position lies in the sector (zone), as the game's (private) FindObjects: a sector already in
    /// <paramref name="visitedSectorIndices"/> adds nothing, and the sector is added to it, so pass a new set per lookup.
    /// </summary>
    public void FindObjects(Vector2s sector, System.Collections.Generic.List<ZDO> objects,
        System.Collections.Generic.HashSet<ZoneSystem.SectorIndex> visitedSectorIndices)
    {
        if (!visitedSectorIndices.Add(ZoneSystem.SectorToIndex(sector))) return;
        foreach (var zdo in Zdos)
            if (zdo.GetSector() == sector)
                objects.Add(zdo);
    }

    [TestOnly] public int CountWithPrefab(string prefab)
    {
        var found = new System.Collections.Generic.List<ZDO>();
        int index = 0;
        GetAllZDOsWithPrefabIterative(prefab, found, ref index);
        return found.Count;
    }
}

/// <summary>
/// Shim for Valheim's TerrainComp (_TerrainCompiler): the per-vertex arrays
/// a terrain modifier writes. (m_width + 1)^2 vertices, row-major, y outer.
/// </summary>
public partial class TerrainComp
{
    [TestOnly] public const string PrefabName = "_TerrainCompiler";

    public int m_width;
    public Heightmap m_hmap;
    public ZNetView m_nview;
    public float[] m_levelDelta;
    public float[] m_smoothDelta;
    public bool[] m_modifiedHeight;
    public UnityEngine.Color[] m_paintMask;
    public bool[] m_modifiedPaint;
    /// <summary>How many terrain operations were applied (the game counts each; saved with the terrain).</summary>
    public int m_operations;
    /// <summary>The last operation's point and radius, saved with the terrain.</summary>
    public UnityEngine.Vector3 m_lastOpPoint;
    public float m_lastOpRadius;
    /// <summary>The game's (private) flag, set once the compiler has its heightmap; until then Save writes nothing and a rebuild adds none of its deltas. True here from construction; a test sets it false to make a save silently not happen.</summary>
    public bool m_initialized = true;
    [TestOnly] public int SaveCount;

    /// <summary>
    /// The first live compiler whose zone holds the position, edges included (a seam point belongs to the first zone),
    /// as in the game; null where none is. It never creates one.
    /// </summary>
    public static TerrainComp? FindTerrainCompiler(UnityEngine.Vector3 pos) =>
        Heightmap.s_heightmaps.Find(hm => hm.m_terrainComp != null && hm.IsPointInside(pos))?.m_terrainComp;

    /// <summary>A new compiler for the heightmap's zone with its own ZDO, registered with the ZDOMan when there is one and owned by us.</summary>
    [TestOnly] public TerrainComp(Heightmap hmap, int width)
    {
        m_hmap = hmap;
        m_width = width;
        int prefab = PrefabName.GetStableHashCode();
        ZDO zdo = ZDOMan.instance != null
            ? ZDOMan.instance.CreateNewZDO(hmap.transform.position, prefab)
            : new ZDO(hmap.transform.position, prefab);
        zdo.Persistent = true;
        zdo.SetOwner(ZDOMan.GetSessionID());
        m_nview = new ZNetView(zdo);
        int n = (width + 1) * (width + 1);
        m_levelDelta = new float[n];
        m_smoothDelta = new float[n];
        m_modifiedHeight = new bool[n];
        m_paintMask = new UnityEngine.Color[n];
        m_modifiedPaint = new bool[n];
    }

    private int m_lastHash;
    // The game's ComputePaintMaskHash: a double summing each modified texel's flag and colour channels, hashed.
    private int PaintMaskHash()
    {
        double sum = 0.0;
        for (int i = 0; i < m_modifiedPaint.Length; i++)
        {
            sum += m_modifiedPaint[i] ? 1 : 0;
            if (m_modifiedPaint[i]) { var c = m_paintMask[i]; sum += c.r; sum += c.g; sum += c.b; sum += c.a; }
        }
        return sum.GetHashCode();
    }

    /// <summary>
    /// Like the game (1.0.16): only the owner's compiler saves, into the ZDO's TCData, the bytes the game writes:
    /// <c>Utils.Compress</c> of a ZPackage holding version 1, <see cref="m_operations"/>, <see cref="m_lastOpPoint"/>,
    /// <see cref="m_lastOpRadius"/>, the vertex count and per vertex a modified flag (then its level and smooth deltas),
    /// the texel count and per texel a modified flag (then its paint r, g, b, a). A peer that does not own the ZDO writes
    /// nothing, and so does a compiler that is not <see cref="m_initialized"/>: the game's Save returns silently in both cases.
    /// With <paramref name="paintOnly"/> it also skips the write when the paint has not changed since the last paint-only
    /// save (the game's hash of the modified texels). Private in the game; mods reach it through a publicized assembly.
    /// </summary>
    public void Save(bool paintOnly = false)
    {
        if (!m_initialized || m_nview == null || !m_nview.IsValid() || !m_nview.IsOwner())
            return;
        int hash = paintOnly ? PaintMaskHash() : 0;
        if (hash == m_lastHash && paintOnly) return;
        m_lastHash = hash;
        SaveCount++;
        var package = new ZPackage();
        package.Write(1);
        package.Write(m_operations);
        package.Write(m_lastOpPoint);
        package.Write(m_lastOpRadius);
        package.Write(m_modifiedHeight.Length);
        for (int i = 0; i < m_modifiedHeight.Length; i++)
        {
            package.Write(m_modifiedHeight[i]);
            if (m_modifiedHeight[i]) { package.Write(m_levelDelta[i]); package.Write(m_smoothDelta[i]); }
        }
        package.Write(m_modifiedPaint.Length);
        for (int i = 0; i < m_modifiedPaint.Length; i++)
        {
            package.Write(m_modifiedPaint[i]);
            if (m_modifiedPaint[i]) { package.Write(m_paintMask[i].r); package.Write(m_paintMask[i].g); package.Write(m_paintMask[i].b); package.Write(m_paintMask[i].a); }
        }
        m_nview.GetZDO().Set(ZDOVars.s_TCData, Utils.Compress(package.GetArray()));
    }

    /// <summary>
    /// As the game's Load (1.0.16): reads what <see cref="Save"/> wrote back into the arrays. False without TCData, or when
    /// its vertex count is not this compiler's (the operation counter, point and radius are read before that check, as the
    /// game reads them). An unmodified vertex's deltas become 0; an unmodified texel keeps its paint. A pre-1.0 paint grid of
    /// width x width texels is spread onto the (width + 1)^2 grid with the game's own index mapping.
    /// </summary>
    public bool Load()
    {
        byte[]? data = m_nview?.GetZDO()?.GetByteArray(ZDOVars.s_TCData);
        if (data == null) return false;
        var package = new ZPackage(Utils.Decompress(data));
        package.ReadInt();
        m_operations = package.ReadInt();
        m_lastOpPoint = package.ReadVector3();
        m_lastOpRadius = package.ReadSingle();
        int vertices = package.ReadInt();
        if (vertices != m_modifiedHeight.Length) return false;
        for (int i = 0; i < vertices; i++)
        {
            m_modifiedHeight[i] = package.ReadBool();
            if (m_modifiedHeight[i]) { m_levelDelta[i] = package.ReadSingle(); m_smoothDelta[i] = package.ReadSingle(); }
            else { m_levelDelta[i] = 0f; m_smoothDelta[i] = 0f; }
        }
        int texels = package.ReadInt();
        for (int i = 0; i < texels; i++)
        {
            m_modifiedPaint[i] = package.ReadBool();
            if (m_modifiedPaint[i]) m_paintMask[i] = new UnityEngine.Color(package.ReadSingle(), package.ReadSingle(), package.ReadSingle(), package.ReadSingle());
        }
        if (texels == m_width * m_width)
        {
            var paint = (UnityEngine.Color[])m_paintMask.Clone();
            var modified = (bool[])m_modifiedPaint.Clone();
            int side = m_width + 1;
            for (int k = 0; k < m_paintMask.Length; k++)
            {
                int row = k / side, next = (k + 1) / side, from = k - row;
                if (row == m_width) from -= m_width;
                if (k > 0 && (k - row) % m_width == 0 && (k + 1 - next) % m_width == 0) from--;
                m_paintMask[k] = paint[from];
                m_modifiedPaint[k] = modified[from];
            }
        }
        return true;
    }
}

/// <summary>Shim for ZDOVars: the ZDO keys the doubles and mod logic read.</summary>
public static partial class ZDOVars
{
    public static readonly int s_TCData = "TCData".GetStableHashCode();
    /// <summary>The player who made an object; vanilla sets it on what a player builds.</summary>
    public static readonly int s_creator = "creator".GetStableHashCode();
    /// <summary>The location a LocationProxy stands for, by prefab hash.</summary>
    public static readonly int s_location = "location".GetStableHashCode();
    /// <summary>A synced object's scale (a ZNetView with <c>m_syncInitialScale</c>), and its one-number form.</summary>
    public static readonly int s_scaleHash = "scale".GetStableHashCode();
    public static readonly int s_scaleScalarHash = "scaleScalar".GetStableHashCode();
}

/// <summary>
/// Shim base for Valheim's WorldGenerator with the members mod logic has needed so
/// far. Tests subclass this with synthetic terrain.
/// </summary>
public partial class WorldGenerator
{
    [TestOnly] public WorldGenerator() { }
    public static WorldGenerator? instance { get; private set; }

    /// <summary>The game's own formula (1.0): the 20-lobed wobble on the biome rings.</summary>
    public static float WorldAngle(float wx, float wy) =>
        (float)System.Math.Sin((float)((double)(float)System.Math.Atan2(wx, wy) * 20.0));

    public virtual float GetHeight(float wx, float wy) => 0f;

    /// <summary>
    /// The biome at a point, with the game's signature and defaults. A test world overrides it; the double passes the
    /// game's ocean parameters through and does not model them.
    /// </summary>
    public virtual Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;

    public virtual void GetRiverWeight(float wx, float wy, out float weight, out float width)
    {
        weight = 0f;
        width = 0f;
    }

    public virtual int GetSeed() => 0;

    /// <summary>
    /// Valheim's base height is a normalised value where water lies below 0.05
    /// (IslandDetector.WaterThreshold) and the terrain height is roughly
    /// 200 × base; map the shim's metres onto that scale so the island detector
    /// sees ocean where the synthetic world puts it (sea level 30 m → 0.05).
    /// </summary>
    public virtual float GetBaseHeight(float wx, float wy, bool menuTerrain) =>
        0.05f + (GetHeight(wx, wy) - WaterLevel) / 200f;

    /// <summary>The game's water level in metres (ZoneSystem.m_waterLevel).</summary>
    [TestOnly] public const float WaterLevel = 30f;

    /// <summary>One biome's height at a point, with the game's signature and defaults; the generation flags are not modelled.</summary>
    public virtual float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out UnityEngine.Color mask, bool preGeneration = false, bool riverPreDN = true)
    {
        mask = default;
        return GetHeight(wx, wy);
    }
}

/// <summary>
/// Shim for Valheim's ZoneSystem with the members mod logic has needed so far. A test places locations by adding them to
/// <see cref="m_locationInstances"/>, which GetLocationList lists.
/// </summary>
public partial class ZoneSystem : UnityEngine.MonoBehaviour
{
    [TestOnly] public const float ZoneSize = 64f;

    /// <summary>Valheim 1.0's index of a zone within the 512 x 512 sector tables (<see cref="SectorToIndex(int, int)"/>).</summary>
    public partial struct SectorIndex
    {
        public uint Sector;
        public SectorIndex(uint sector) => Sector = sector;
        public override bool Equals(object? o) => o is SectorIndex s && s.Sector == Sector;
        public override int GetHashCode() => (int)Sector;
        public static bool operator ==(SectorIndex s1, SectorIndex s2) => s1.Sector == s2.Sector;
        public static bool operator !=(SectorIndex s1, SectorIndex s2) => s1.Sector != s2.Sector;
    }

    /// <summary>The sector's index, as the game's: sectors -256..255 on each axis, row by row; any other sector is index 0.</summary>
    public static SectorIndex SectorToIndex(Vector2s sector) => SectorToIndex(sector.x, sector.y);
    public static SectorIndex SectorToIndex(int sectorX, int sectorY)
    {
        uint x = (uint)(sectorX + 256), y = (uint)(sectorY + 256);
        return new SectorIndex(x >= 512 || y >= 512 ? 0u : y * 512 + x);
    }
    /// <summary>The sector an index stands for, as the game's.</summary>
    public static Vector2s IndexToSector(uint index) => new((int)(index % 512 - 256), (int)(index / 512 - 256));

    public static ZoneSystem? instance { get; private set; }

    public enum SpawnMode { Full, Client, Ghost }
    public partial class ClearArea
    {
        public readonly UnityEngine.Vector3 m_center;
        public readonly float m_radius;
        public ClearArea(UnityEngine.Vector3 center, float radius) { m_center = center; m_radius = radius; }
    }
    /// <summary>Zones the world has generated; a test adds the ones it needs.</summary>
    [TestOnly] public readonly System.Collections.Generic.HashSet<Vector2s> Generated = new();
    public bool IsZoneGenerated(Vector2s zone) => Generated.Contains(zone);

    public partial class ZoneLocation
    {
        /// <summary>
        /// The location's template, as the game's soft reference: a test gives it one with
        /// <c>new SoftReference&lt;GameObject&gt;(name, template)</c>.
        /// </summary>
        public SoftReferenceableAssets.SoftReference<UnityEngine.GameObject> m_prefab;
        public float m_exteriorRadius;
    }

    public partial struct LocationInstance
    {
        public ZoneLocation m_location;
        public UnityEngine.Vector3 m_position;
        public bool m_placed; // spawned in its zone; generation registers a location unplaced
    }

    /// <summary>The placements, as the game's: the values of <see cref="m_locationInstances"/>.</summary>
    public System.Collections.Generic.Dictionary<Vector2s, LocationInstance>.ValueCollection GetLocationList() => m_locationInstances.Values;

    /// <summary>The placements the game has decided on, one per zone.</summary>
    public System.Collections.Generic.Dictionary<Vector2s, LocationInstance> m_locationInstances = new();
    /// <summary>The world's location list, which is what places buildings.</summary>
    public System.Collections.Generic.List<ZoneLocation> m_locations = new();
    /// <summary>The same list by prefab hash (private in the game), which is how a proxy finds its template.</summary>
    public readonly System.Collections.Generic.Dictionary<int, ZoneLocation> m_locationsByHash = new();

    // The generation methods mods hook, with the game's 1.0.16 signatures (several are private there). Their bodies do
    // nothing: what these doubles cannot establish is when Harmony runs a hook, which only an in-game check shows.
    public void Awake() { }
    public void Update() { }
    public void GenerateLocationsIfNeeded() { }
    public void SetupLocations() { }
    public UnityEngine.GameObject? SpawnLocation(ZoneLocation location, int seed, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot,
        SpawnMode mode, System.Collections.Generic.List<UnityEngine.GameObject> spawnedGhostObjects, bool cheated = false) => null;
    public bool SpawnZone(Vector2s zoneID, SpawnMode mode, out UnityEngine.GameObject? root) { root = null; return true; }
    public void PlaceLocations(Vector2s zoneID, UnityEngine.Vector3 zoneCenterPos, UnityEngine.Transform parent, Heightmap hmap,
        System.Collections.Generic.List<ClearArea> clearAreas, SpawnMode mode, System.Collections.Generic.List<UnityEngine.GameObject> spawnedObjects) { }

    // ---- locations-generated, as Valheim 1.0 actually behaves ----
    //
    // 1.0 kept the LocationsGenerated property and the GenerateLocationsCompleted
    // event, but changed who writes the backing field. The setter still raises the
    // event (once, then drops the handlers), and subscribing after the fact fires
    // immediately -- but ZoneSystem.Load now writes m_locationsGenerated DIRECTLY
    // from the save package, bypassing the setter. So a world read from disk never
    // raises the event, however early a handler subscribed. Before 1.0 the setter
    // was the only writer and every path raised it.
    //
    // The ordering that matters is the game's own: loading a world sets this from
    // the save DURING the load, before the world's ZDOs are read.
    //
    // The shim models all three doors so a test can tell them apart.

    private bool m_locationsGenerated;
    private System.Action? m_generateLocationsCompleted;

    public bool LocationsGenerated
    {
        get => m_locationsGenerated;
        set
        {
            m_locationsGenerated = value;
            if (!m_locationsGenerated) return;
            m_generateLocationsCompleted?.Invoke();
            m_generateLocationsCompleted = null;
        }
    }

    public event System.Action GenerateLocationsCompleted
    {
        add
        {
            if (m_locationsGenerated) { value?.Invoke(); return; }
            m_generateLocationsCompleted += value;
        }
        remove => m_generateLocationsCompleted -= value;
    }

    /// <summary>
    /// What ZoneSystem.Load does in 1.0: set the flag straight from the save and
    /// raise nothing. This is the door the mod used to be told about and is not.
    /// </summary>
    [TestOnly] public void LoadLocationsGeneratedFromSave(bool generated) => m_locationsGenerated = generated;

    // Valheim 1.0 types a zone id as Vector2s, not Vector2i.
    public static Vector2s GetZone(UnityEngine.Vector3 point) =>
        new(UnityEngine.Mathf.FloorToInt((point.x + ZoneSize / 2f) / ZoneSize),
            UnityEngine.Mathf.FloorToInt((point.z + ZoneSize / 2f) / ZoneSize));

    public static UnityEngine.Vector3 GetZonePos(Vector2s id) =>
        new(id.x * ZoneSize, 0f, id.y * ZoneSize);
}
