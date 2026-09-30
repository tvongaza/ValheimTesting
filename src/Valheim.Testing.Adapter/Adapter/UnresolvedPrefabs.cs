// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// The saved objects (ZDOs) near this process's reference position (the local player on a client) whose prefab hash
    /// the local <c>ZNetScene</c> cannot resolve. In Valheim 1.0.16 the scene creates an object for each ZDO near the
    /// player from the prefab registered under its hash; for an unknown hash it logs "Missing prefab hash: &lt;hash&gt;" and
    /// creates nothing, and tries again on every pass, so a client without a server-side mod's prefabs silently lacks those
    /// objects. The runner's <c>UnresolvedPrefabs</c> and <c>VanillaClientCheck</c> in Valheim.Testing.Game read this on
    /// a client that does not have the mod. Reading changes nothing. Written against the Valheim 1.0.16 decompile; not yet
    /// run in game.
    /// </summary>
    public static class UnresolvedPrefabs
    {
        public const string Source = "unresolved-prefabs";
        /// <summary>The largest radius the census accepts, in metres: a client knows only the objects around its player.</summary>
        public const float MaxRadius = 256f;
        /// <summary>The most objects one census reads before refusing, rather than returning a truncated list.</summary>
        public const int MaxObjects = 50000;

        /// <summary>
        /// A read-only extension command <paramref name="name"/>: <c>[radius]</c> in metres (default 64, at most
        /// <see cref="MaxRadius"/>) around the reference position. It replies <see cref="Observe"/>'s data.
        /// </summary>
        public static ExtensionCommand Command(string name = "unresolved-prefabs") =>
            new ExtensionCommand(name, "List prefab hashes near the player that this process cannot resolve: [radius]", Run, readOnly: true, needsWorld: true);

        private static IEnumerator Run(ExtensionContext context)
        {
            float radius = 64f;
            if (context.Arguments.Count > 1 || (context.Arguments.Count == 1 &&
                (!float.TryParse(context.Arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out radius) || !(radius > 0f && radius <= MaxRadius))))
            { context.Fail("usage", "unresolved-prefabs [radius], 0 < radius <= " + MaxRadius.ToString(CultureInfo.InvariantCulture)); yield break; }
            if (ZNet.instance == null || ZNetScene.instance == null || ZDOMan.instance == null || ZoneSystem.instance == null)
            { context.Fail("no_world", "No loaded world."); yield break; }
            context.Succeed(Observe(ZNet.instance.GetReferencePosition(), radius));
        }

        /// <summary>
        /// <c>{source: "unresolved-prefabs", complete, x, z, radius, zones, zonesLoaded, scanned, withoutPrefab,
        /// unresolved: [{hash, count, x, y, z}]}</c> for the ZDOs within <paramref name="radius"/> metres (horizontally) of
        /// <paramref name="centre"/>. <c>unresolved</c> has one entry per unknown hash, ordered by hash, with how many ZDOs
        /// carry it and where the first one is. A ZDO without a prefab (hash 0) is counted in <c>withoutPrefab</c>, since the
        /// game creates nothing for it on purpose. <c>complete</c> is true once every zone the circle touches is loaded here
        /// (its objects created or tried), so a census taken while the area still loads says so.
        /// </summary>
        public static Dictionary<string, object?> Observe(Vector3 centre, float radius)
        {
            if (!(radius > 0f && radius <= MaxRadius)) throw new ArgumentOutOfRangeException(nameof(radius));
            var scene = ZNetScene.instance ?? throw new InvalidOperationException("No ZNetScene: load a world first.");
            var system = ZoneSystem.instance ?? throw new InvalidOperationException("No ZoneSystem: load a world first.");
            Vector2s min = ZoneSystem.GetZone(new Vector3(centre.x - radius, 0f, centre.z - radius));
            Vector2s max = ZoneSystem.GetZone(new Vector3(centre.x + radius, 0f, centre.z + radius));
            int zones = 0, loaded = 0, scanned = 0, withoutPrefab = 0;
            var unresolved = new SortedDictionary<int, (int Count, Vector3 First)>();
            for (int zx = min.x; zx <= max.x; zx++)
                for (int zz = min.y; zz <= max.y; zz++)
                {
                    var zone = new Vector2s(zx, zz);
                    zones++;
                    if (system.IsZoneLoaded(zone)) loaded++;
                    foreach (ZDO zdo in ZoneTerrain.ZoneObjects(zone, MaxObjects))
                    {
                        Vector3 position = zdo.GetPosition();
                        float dx = position.x - centre.x, dz = position.z - centre.z;
                        if (dx * dx + dz * dz > radius * radius) continue;
                        if (++scanned > MaxObjects) throw new InvalidOperationException($"More than {MaxObjects} objects within {radius} m; nothing is returned rather than a partial census.");
                        int hash = zdo.GetPrefab();
                        if (hash == 0) { withoutPrefab++; continue; }
                        if (scene.HasPrefab(hash)) continue;
                        unresolved[hash] = unresolved.TryGetValue(hash, out var seen) ? (seen.Count + 1, seen.First) : (1, position);
                    }
                }
            return new Dictionary<string, object?>
            {
                ["source"] = Source, ["complete"] = loaded == zones, ["x"] = centre.x, ["z"] = centre.z, ["radius"] = radius,
                ["zones"] = zones, ["zonesLoaded"] = loaded, ["scanned"] = scanned, ["withoutPrefab"] = withoutPrefab,
                ["unresolved"] = unresolved.Select(entry => new Dictionary<string, object?>
                {
                    ["hash"] = entry.Key, ["count"] = entry.Value.Count,
                    ["x"] = entry.Value.First.x, ["y"] = entry.Value.First.y, ["z"] = entry.Value.First.z,
                }).ToArray(),
            };
        }
    }
}
