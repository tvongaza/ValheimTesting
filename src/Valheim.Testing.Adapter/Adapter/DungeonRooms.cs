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
    /// The rooms a dungeon saved, read from its generator's ZDO on the server, with the location it belongs to. In Valheim
    /// 1.0.16 a dungeon generator saves its placed rooms as one byte array, <c>roomData</c>: the room count, then for each
    /// room its prefab-name hash, position and rotation (as Euler angles). Worlds saved before that format keep an int
    /// <c>rooms</c> and <c>room&lt;i&gt;</c> (hash), <c>room&lt;i&gt;_pos</c> and <c>room&lt;i&gt;_rot</c> fields, which
    /// the game reads when <c>roomData</c> is absent and removes at its next save. The data is returned raw (the byte
    /// array as base64); the runner's <c>DungeonRooms</c> in Valheim.Testing.Game decodes it and checks that every room lies
    /// in the location's zone, where the game puts the interior environment. Reading changes nothing. Written against the
    /// Valheim 1.0.16 decompile and read a SunkenCrypt4's saved rooms on a 1.0.16 dedicated server.
    /// </summary>
    public static class DungeonRooms
    {
        public const string Source = "dungeon-rooms";
        /// <summary>The largest search radius, in metres.</summary>
        public const float MaxRadius = 256f;
        /// <summary>The most objects one search reads before refusing, rather than returning a partial answer.</summary>
        public const int MaxObjects = 50000;

        /// <summary>
        /// A read-only server extension command <paramref name="name"/>: <c>&lt;x&gt; &lt;z&gt; [radius]</c>, the dungeon
        /// generators whose saved position is within <c>radius</c> metres (default 64, at most <see cref="MaxRadius"/>)
        /// horizontally of (x, z), with the location nearest (x, z). A generator stands 5000 m or so above its location,
        /// so give the location's ground position. It replies <see cref="Observe"/>'s data. A dungeon has no saved
        /// generator until its zone has been generated (a player came near), so an unvisited dungeon replies an empty list.
        /// </summary>
        public static ExtensionCommand Command(string name = "dungeon-rooms") =>
            new ExtensionCommand(name, "Read saved dungeon rooms near a position: <x> <z> [radius]", Run, readOnly: true, role: ExtensionRole.Server, needsWorld: true);

        private static IEnumerator Run(ExtensionContext context)
        {
            var arguments = context.Arguments;
            float x = 0f, z = 0f, radius = 64f;
            if (arguments.Count < 2 || arguments.Count > 3 || !Number(arguments[0], out x) || !Number(arguments[1], out z) ||
                (arguments.Count == 3 && (!Number(arguments[2], out radius) || !(radius > 0f && radius <= MaxRadius))))
            { context.Fail("usage", "dungeon-rooms <x> <z> [radius], 0 < radius <= " + MaxRadius.ToString(CultureInfo.InvariantCulture)); yield break; }
            if (ZNetScene.instance == null || ZDOMan.instance == null || ZoneSystem.instance == null) { context.Fail("no_world", "No loaded world."); yield break; }
            context.Succeed(Observe(x, z, radius));
        }

        private static bool Number(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// <c>{source: "dungeon-rooms", complete: true, x, z, radius, zoneSize, dungeons: [{prefab, uid, x, y, z, zoneX,
        /// zoneZ, customInterior, format, roomData, rooms, legacyRooms: [{hash, x, y, z, rx, ry, rz}], location}]}</c>.
        /// <c>format</c> is <c>roomData</c> (1.0's byte array, in <c>roomData</c> as base64), <c>legacy</c> (the older fields,
        /// in <c>legacyRooms</c>, with <c>rooms</c> their count) or <c>none</c>. <c>location</c> is the location instance
        /// nearest (x, z) among those the server keeps for the 3 x 3 zones around (x, z) (<c>{prefab, x, y, z, zoneX,
        /// zoneZ}</c>), or null when it keeps none there; it is the same for every generator in one reply. The game keys a
        /// location instance by the zone its position stands in, so the location is found from the queried ground position
        /// and not from the generator's zone: a generator displaced into another zone then still pairs with its location,
        /// and the runner's zone comparison can see the displacement. <c>customInterior</c> is the generator prefab's
        /// <c>m_useCustomInteriorTransform</c>.
        /// </summary>
        public static Dictionary<string, object?> Observe(float x, float z, float radius)
        {
            if (!(radius > 0f && radius <= MaxRadius)) throw new ArgumentOutOfRangeException(nameof(radius));
            var scene = ZNetScene.instance ?? throw new InvalidOperationException("No ZNetScene: load a world first.");
            var system = ZoneSystem.instance ?? throw new InvalidOperationException("No ZoneSystem: load a world first.");
            Vector2s min = ZoneSystem.GetZone(new Vector3(x - radius, 0f, z - radius));
            Vector2s max = ZoneSystem.GetZone(new Vector3(x + radius, 0f, z + radius));
            Dictionary<string, object?>? location = NearestLocation(system, x, z);
            var dungeons = new List<Dictionary<string, object?>>();
            int scanned = 0;
            for (int zx = min.x; zx <= max.x; zx++)
                for (int zz = min.y; zz <= max.y; zz++)
                    foreach (ZDO zdo in SavedObjects.InZone(new Vector2s(zx, zz), MaxObjects))
                    {
                        if (++scanned > MaxObjects) throw new InvalidOperationException($"More than {MaxObjects} objects to search; nothing is returned rather than a partial answer.");
                        Vector3 position = zdo.GetPosition();
                        float dx = position.x - x, dz = position.z - z;
                        if (dx * dx + dz * dz > radius * radius) continue;
                        int hash = zdo.GetPrefab();
                        if (hash == 0) continue;
                        GameObject prefab = scene.GetPrefab(hash);
                        if (prefab == null) continue;
                        DungeonGenerator generator = prefab.GetComponent<DungeonGenerator>();
                        if (generator == null) continue;
                        dungeons.Add(Describe(zdo, prefab.name, generator, position, location));
                    }
            return new Dictionary<string, object?>
            {
                ["source"] = Source, ["complete"] = true, ["x"] = x, ["z"] = z, ["radius"] = radius, ["zoneSize"] = system.m_zoneSize,
                ["dungeons"] = dungeons.OrderBy(d => (string)d["uid"]!, StringComparer.Ordinal).ToArray(),
            };
        }

        private static Dictionary<string, object?> Describe(ZDO zdo, string prefab, DungeonGenerator generator, Vector3 position, Dictionary<string, object?>? location)
        {
            Vector2s zone = ZoneSystem.GetZone(position);
            string format = "none";
            string? roomData = null;
            var legacy = new List<Dictionary<string, object?>>();
            int rooms = 0;
            if (zdo.GetByteArray("roomData", out byte[] bytes) && bytes != null)
            {
                format = "roomData";
                roomData = Convert.ToBase64String(bytes);
            }
            else if (zdo.GetInt("rooms", out rooms))
            {
                format = "legacy";
                for (int i = 0; i < rooms; i++)
                {
                    string field = "room" + i.ToString(CultureInfo.InvariantCulture);
                    Vector3 at = zdo.GetVec3(field + "_pos", new Vector3(0f, 0f, 0f));
                    Vector3 euler = zdo.GetQuaternion(field + "_rot", Quaternion.identity).eulerAngles;
                    legacy.Add(new Dictionary<string, object?>
                    {
                        ["hash"] = zdo.GetInt(field, 0), ["x"] = at.x, ["y"] = at.y, ["z"] = at.z, ["rx"] = euler.x, ["ry"] = euler.y, ["rz"] = euler.z,
                    });
                }
            }
            return new Dictionary<string, object?>
            {
                ["prefab"] = prefab, ["uid"] = zdo.m_uid.ToString(), ["x"] = position.x, ["y"] = position.y, ["z"] = position.z,
                ["zoneX"] = (int)zone.x, ["zoneZ"] = (int)zone.y, ["customInterior"] = generator.m_useCustomInteriorTransform,
                ["format"] = format, ["roomData"] = roomData, ["rooms"] = format == "legacy" ? rooms : (int?)null, ["legacyRooms"] = legacy.ToArray(),
                ["location"] = location,
            };
        }

        // The location instance nearest (x, z) horizontally in the 3 x 3 zones around it, or null. The game stores each
        // instance under the zone its own position stands in (one per zone), so a caller's point near the location finds it
        // in its own zone or a neighbour.
        private static Dictionary<string, object?>? NearestLocation(ZoneSystem system, float x, float z)
        {
            Vector2s centre = ZoneSystem.GetZone(new Vector3(x, 0f, z));
            ZoneSystem.LocationInstance nearest = default;
            float best = float.MaxValue;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!system.m_locationInstances.TryGetValue(new Vector2s(centre.x + dx, centre.y + dz), out var instance)) continue;
                    float ox = instance.m_position.x - x, oz = instance.m_position.z - z, distance = ox * ox + oz * oz;
                    if (distance < best) { best = distance; nearest = instance; }
                }
            if (best == float.MaxValue) return null;
            Vector2s zone = ZoneSystem.GetZone(nearest.m_position);
            return new Dictionary<string, object?>
            {
                ["prefab"] = nearest.m_location?.m_prefabName, ["x"] = nearest.m_position.x, ["y"] = nearest.m_position.y, ["z"] = nearest.m_position.z,
                ["zoneX"] = (int)zone.x, ["zoneZ"] = (int)zone.y,
            };
        }
    }
}
