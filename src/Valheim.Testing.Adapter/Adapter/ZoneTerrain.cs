// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Zone and terrain pieces for fixture commands on the server: the exact generated-zone check, waiting for the game's
    /// terrain builder, a zone's terrain with a saved terrain compiler outside the normal zone loading, height samples from
    /// the heightmap and from its collider, saving a compiler, destroying a saved object and a capped census of a zone's
    /// saved objects. Each piece checks the conditions under which the game would silently do nothing and throws instead.
    /// Behaviour matches Valheim 1.0.16. Gate fixture commands with <see cref="FixtureGate"/>.
    /// </summary>
    public static class ZoneTerrain
    {
        /// <summary>Zones the object census can address: the game files zones outside this range under one shared slot.</summary>
        public const int MaxZoneIndex = 255;

        /// <summary>
        /// Whether the game has generated <paramref name="zone"/> (placed its locations and vegetation), as it records it for
        /// saving. Not whether the zone is loaded now, and not whether terrain compilers or other saved objects exist there.
        /// </summary>
        public static bool IsZoneGenerated(Vector2s zone)
        {
            var system = ZoneSystem.instance ?? throw new InvalidOperationException("No ZoneSystem: load a world first.");
            return (bool)Members.Call(Members.Method(typeof(ZoneSystem), "IsZoneGenerated", typeof(Vector2s)), system, zone)!;
        }

        /// <summary>
        /// Waits, one frame at a time, until the game's background terrain builder has the base heights of every zone in
        /// <paramref name="zones"/>, as it requires before spawning a zone. Asking queues the build, as the game's own check
        /// does. Throws <see cref="TimeoutException"/> after <paramref name="timeoutSeconds"/> of real time and
        /// <see cref="OperationCanceledException"/> once <paramref name="cancelled"/> is true. Extension commands yield only
        /// null, so step through it: <c>foreach (var step in ZoneTerrain.WaitForTerrain(zones, 30, () =&gt; context.Cancelled)) yield return step;</c>
        /// </summary>
        public static IEnumerable<object?> WaitForTerrain(IEnumerable<Vector2s> zones, float timeoutSeconds, Func<bool> cancelled)
        {
            if (zones == null) throw new ArgumentNullException(nameof(zones));
            if (cancelled == null) throw new ArgumentNullException(nameof(cancelled));
            if (!(timeoutSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Give a positive timeout.");
            return Wait(zones.ToArray(), timeoutSeconds, cancelled);
        }

        private static IEnumerable<object?> Wait(Vector2s[] zones, float timeoutSeconds, Func<bool> cancelled)
        {
            var template = Template();
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            foreach (var zone in zones)
                while (!HeightmapBuilder.instance.IsTerrainReady(ZoneSystem.GetZonePos(zone), template.m_width, template.m_scale, template.IsDistantLod, WorldGenerator.instance))
                {
                    if (cancelled()) throw new OperationCanceledException();
                    if (Time.realtimeSinceStartup > deadline) throw new TimeoutException($"Terrain for zone {zone.x},{zone.y} was not ready within {timeoutSeconds} s.");
                    yield return null;
                }
        }

        /// <summary>
        /// Instantiates <paramref name="zone"/>'s terrain (the game's zone prefab, without locations or vegetation) and a
        /// terrain compiler attached to it whose saved object (ZDO) is created as the game creates ghost objects while
        /// generating a zone: saved, but not registered with the scene. Server only. Refuses a zone whose terrain or
        /// compiler is loaded, or that already has a saved compiler, because the new compiler would attach to the other
        /// terrain or duplicate the saved one. Wait for <see cref="WaitForTerrain"/> first, or the heights build
        /// synchronously on this frame. Dispose the result to release the Unity objects; the compiler's saved object stays
        /// (<see cref="DestroyZdo"/> removes it).
        /// </summary>
        public static SpawnedTerrain SpawnTerrain(Vector2s zone)
        {
            RequireServer();
            RequireAddressable(zone);
            var system = ZoneSystem.instance;
            var prefab = system.m_zonePrefab;
            if (prefab == null) throw new InvalidOperationException("ZoneSystem has no zone prefab.");
            var template = Template();
            Vector3 position = ZoneSystem.GetZonePos(zone);
            if (Heightmap.FindHeightmap(position) != null) throw new InvalidOperationException($"Zone {zone.x},{zone.y} has loaded terrain; use an unloaded zone.");
            if (TerrainComp.FindTerrainCompiler(position) != null) throw new InvalidOperationException($"Zone {zone.x},{zone.y} has a loaded terrain compiler.");
            var compilerPrefab = template.m_terrainCompilerPrefab;
            if (compilerPrefab == null) throw new InvalidOperationException("The zone's heightmap has no terrain compiler prefab.");
            int compilerHash = compilerPrefab.name.GetStableHashCode();
            int saved = ZoneObjects(zone, int.MaxValue, o => o.GetPrefab() == compilerHash).Count;
            if (saved != 0) throw new InvalidOperationException($"Zone {zone.x},{zone.y} already has {saved} saved terrain compiler(s).");

            GameObject? root = null, compilerObject = null;
            try
            {
                root = Object.Instantiate(prefab, position, Quaternion.identity);
                var heightmap = root.GetComponentInChildren<Heightmap>();
                if (heightmap == null) throw new InvalidOperationException("The zone prefab has no heightmap.");
                heightmap.Regenerate();
                ZNetView.StartGhostInit();
                try { compilerObject = Object.Instantiate(compilerPrefab, heightmap.transform.position, Quaternion.identity); }
                finally { ZNetView.FinishGhostInit(); }
                var compiler = compilerObject.GetComponent<TerrainComp>();
                var view = compilerObject.GetComponent<ZNetView>();
                var zdo = view == null ? null : view.GetZDO();
                if (compiler == null || zdo == null) throw new InvalidOperationException("The terrain compiler has no saved object.");
                if (Members.Field<Heightmap>(compiler, "m_hmap") != heightmap) throw new InvalidOperationException("The terrain compiler attached to other terrain.");
                heightmap.Regenerate();
                var spawned = new SpawnedTerrain(zone, root, heightmap, compilerObject, compiler, zdo);
                root = compilerObject = null;
                return spawned;
            }
            finally
            {
                // Only on failure: a returned SpawnedTerrain owns them.
                if (compilerObject != null) Object.DestroyImmediate(compilerObject);
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// The height the heightmap holds at world <paramref name="x"/>, <paramref name="z"/>: its nearest vertex, compiler
        /// changes included, as the game's own height lookup reads it. Throws outside the heightmap.
        /// </summary>
        public static float HeightmapHeight(Heightmap heightmap, float x, float z)
        {
            if (heightmap == null) throw new ArgumentNullException(nameof(heightmap));
            if (!heightmap.GetWorldHeight(new Vector3(x, 0f, z), out float height))
                throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {z}) is outside this heightmap.");
            return height;
        }

        /// <summary>
        /// The height at which a vertical ray hits this heightmap's own collider: what a player stands on, without any
        /// other collider in the way. Throws when the heightmap has no collider or the ray misses it.
        /// </summary>
        public static float ColliderHeight(Heightmap heightmap, float x, float z)
        {
            if (heightmap == null) throw new ArgumentNullException(nameof(heightmap));
            var collider = heightmap.GetComponent<MeshCollider>();
            if (collider == null) throw new InvalidOperationException("The heightmap has no collider.");
            if (!collider.Raycast(new Ray(new Vector3(x, 10000f, z), Vector3.down), out RaycastHit hit, 20000f))
                throw new InvalidOperationException($"The heightmap's collider has no surface at ({x}, {z}).");
            return hit.point.y;
        }

        /// <summary>
        /// Writes <paramref name="compiler"/>'s heights and paint to its saved object now. The game's save does nothing
        /// unless the compiler is initialized and this process owns its object; that throws here instead.
        /// </summary>
        public static void SaveTerrain(TerrainComp compiler)
        {
            if (compiler == null) throw new ArgumentNullException(nameof(compiler));
            var view = Members.Field<ZNetView>(compiler, "m_nview");
            if (!Members.Field<bool>(compiler, "m_initialized") || view == null || !view.IsValid() || !view.IsOwner())
                throw new InvalidOperationException("The terrain compiler is not initialized or not owned here; the game would not save it.");
            Members.Call(Members.Method(typeof(TerrainComp), "Save", typeof(bool)), compiler, false);
        }

        /// <summary>
        /// Destroys a saved object. Only its owner can: the game ignores anyone else's request, so that throws here. The
        /// game queues the destruction and sends it with its next update, so the object is gone a frame later, not at once.
        /// </summary>
        public static void DestroyZdo(ZDO zdo)
        {
            if (zdo == null) throw new ArgumentNullException(nameof(zdo));
            var objects = ZDOMan.instance ?? throw new InvalidOperationException("No ZDOMan: load a world first.");
            if (!zdo.IsOwner()) throw new InvalidOperationException("This process does not own the object; the game would not destroy it.");
            objects.DestroyZDO(zdo);
        }

        /// <summary>
        /// The saved objects (ZDOs) this process knows in <paramref name="zone"/>, loaded or not, that match
        /// <paramref name="include"/>. On the server that is every saved object there. More than <paramref name="limit"/>
        /// matches throws rather than returning a truncated list. Zones beyond ±<see cref="MaxZoneIndex"/> are refused: the
        /// game files them all under one slot, whose objects would be returned instead.
        /// </summary>
        public static List<ZDO> ZoneObjects(Vector2s zone, int limit, Func<ZDO, bool>? include = null)
        {
            if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
            RequireAddressable(zone);
            var objects = ZDOMan.instance ?? throw new InvalidOperationException("No ZDOMan: load a world first.");
            var found = new List<ZDO>();
            Members.Call(Members.Method(typeof(ZDOMan), "FindObjects", typeof(Vector2s), typeof(List<ZDO>), typeof(HashSet<ZoneSystem.SectorIndex>)),
                objects, zone, found, new HashSet<ZoneSystem.SectorIndex>());
            var matched = include == null ? found : found.Where(include).ToList();
            if (matched.Count > limit)
                throw new InvalidOperationException($"Zone {zone.x},{zone.y} has {matched.Count} matching objects, more than the census limit of {limit}; nothing is returned rather than a truncated list.");
            return matched;
        }

        private static Heightmap Template()
        {
            var system = ZoneSystem.instance ?? throw new InvalidOperationException("No ZoneSystem: load a world first.");
            if (HeightmapBuilder.instance == null || WorldGenerator.instance == null) throw new InvalidOperationException("The world's terrain builder is not running.");
            var prefab = system.m_zonePrefab;
            if (prefab == null) throw new InvalidOperationException("ZoneSystem has no zone prefab.");
            var template = prefab.GetComponentInChildren<Heightmap>();
            if (template == null) throw new InvalidOperationException("The zone prefab has no heightmap.");
            return template;
        }

        private static void RequireServer()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || ZoneSystem.instance == null)
                throw new InvalidOperationException("Terrain fixtures run on the server of a loaded world.");
        }

        private static void RequireAddressable(Vector2s zone)
        {
            if (zone.x < -MaxZoneIndex || zone.x > MaxZoneIndex || zone.y < -MaxZoneIndex || zone.y > MaxZoneIndex)
                throw new ArgumentOutOfRangeException(nameof(zone), $"Zone {zone.x},{zone.y} is outside ±{MaxZoneIndex}.");
        }
    }

    /// <summary>
    /// Terrain from <see cref="ZoneTerrain.SpawnTerrain"/>. Dispose releases the Unity objects (compiler, then terrain);
    /// the compiler's saved object stays unless the fixture destroys it.
    /// </summary>
    public sealed class SpawnedTerrain : IDisposable
    {
        public Vector2s Zone { get; }
        public GameObject Root { get; }
        public Heightmap Heightmap { get; }
        public TerrainComp Compiler { get; }
        /// <summary>The compiler's saved object.</summary>
        public ZDO CompilerZdo { get; }
        private GameObject? _compilerObject;
        private bool _disposed;

        internal SpawnedTerrain(Vector2s zone, GameObject root, Heightmap heightmap, GameObject compilerObject, TerrainComp compiler, ZDO compilerZdo)
        { Zone = zone; Root = root; Heightmap = heightmap; _compilerObject = compilerObject; Compiler = compiler; CompilerZdo = compilerZdo; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_compilerObject != null) Object.DestroyImmediate(_compilerObject); }
            finally { _compilerObject = null; Object.DestroyImmediate(Root); }
        }
    }
}
