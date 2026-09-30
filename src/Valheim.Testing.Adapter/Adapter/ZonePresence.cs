// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Which of a list of zones this client holds, for the runner's <c>ZoneCycle</c> in Valheim.Testing.Game ("leave the
    /// area and come back"). For each zone: whether its terrain is loaded (<c>ZoneSystem.IsZoneLoaded</c>), how many object
    /// instances stand in it (distant ones included, and those not marked distant, which keep its terrain loaded), and how
    /// many saved objects of known prefabs this client holds for it and how many of those have no instance. With it, the
    /// zone of the client's reference position and its synced simulation distance, from which the runner computes how far
    /// the player must go. Read-only, client only. Written against the Valheim 1.0.16 decompile; not yet run in game.
    /// </summary>
    public static class ZonePresence
    {
        public const string Source = "zone-presence";
        /// <summary>The most zones one reading lists.</summary>
        public const int MaxZones = 64;

        /// <summary>A read-only client extension command <paramref name="name"/>: <c>zones &lt;x,z&gt; [&lt;x,z&gt; ...]</c>, zone coordinates.</summary>
        public static ExtensionCommand Command(string name = "zones") =>
            new ExtensionCommand(name, "Report which zones this client holds: <x,z> [<x,z> ...] (zone coordinates)", Run,
                readOnly: true, role: ExtensionRole.Client, needsWorld: true);

        private static IEnumerator Run(ExtensionContext context)
        {
            var zones = new List<Vector2s>();
            foreach (string argument in context.Arguments)
            {
                if (!TryZone(argument, out int x, out int z)) { context.Fail("usage", "zones <x,z> [<x,z> ...]: zone coordinates within ±255, at most " + MaxZones); yield break; }
                zones.Add(new Vector2s(x, z));
            }
            if (zones.Count == 0 || zones.Count > MaxZones) { context.Fail("usage", "zones <x,z> [<x,z> ...]: name 1 to " + MaxZones + " zones"); yield break; }
            context.Succeed(Observe(zones));
        }

        /// <summary>Parses <c>x,z</c> zone coordinates within ±255, the range the game files zones under separately.</summary>
        public static bool TryZone(string text, out int x, out int z)
        {
            x = z = 0;
            string[] parts = (text ?? "").Split(',');
            return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out x) &&
                int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out z) && Math.Abs(x) <= 255 && Math.Abs(z) <= 255;
        }

        /// <summary>
        /// The reading as extension result data: <c>{source, complete, reference: {x, z}, simulation: {near, far, classic},
        /// zones: [{x, z, terrainLoaded, instances, nearInstances, saved, withoutInstance}]}</c>. Incomplete without a
        /// loaded world. Instances are counted where they stand, as the game does when it decides whether a zone's terrain
        /// can go (<c>ZNetScene.HaveInstanceInSector</c>).
        /// </summary>
        public static Dictionary<string, object?> Observe(IReadOnlyList<Vector2s> zones)
        {
            if (zones == null) throw new ArgumentNullException(nameof(zones));
            var net = ZNet.instance; var system = ZoneSystem.instance; var scene = ZNetScene.instance; var saved = ZDOMan.instance;
            if (net == null || system == null || scene == null || saved == null)
                return new Dictionary<string, object?> { ["source"] = Source, ["complete"] = false };
            // (x, z) -> [all instances, instances not marked distant]
            var counts = new Dictionary<long, int[]>();
            foreach (ZNetView view in Members.Field<Dictionary<ZDO, ZNetView>>(scene, "m_instances").Values)
            {
                if (view == null) continue; // Destroyed this frame.
                Vector2s at = ZoneSystem.GetZone(view.transform.position);
                if (!counts.TryGetValue(Key(at.x, at.y), out int[] count)) counts.Add(Key(at.x, at.y), count = new int[2]);
                count[0]++;
                if (!view.m_distant) count[1]++;
            }
            var rows = new List<object?>();
            var objects = new List<ZDO>();
            foreach (Vector2s zone in zones)
            {
                objects.Clear();
                // No rings round the zone: only its own saved objects, distant ones included.
                saved.FindSectorObjects(zone, new SimulationDistance(0, 0), objects);
                int known = 0, withoutInstance = 0;
                foreach (ZDO zdo in objects)
                {
                    int prefab = zdo.GetPrefab();
                    if (prefab == 0 || scene.GetPrefab(prefab) == null) continue;
                    known++;
                    if (scene.FindInstance(zdo) == null) withoutInstance++;
                }
                counts.TryGetValue(Key(zone.x, zone.y), out int[]? count);
                rows.Add(new Dictionary<string, object?>
                {
                    ["x"] = (int)zone.x, ["z"] = (int)zone.y, ["terrainLoaded"] = system.IsZoneLoaded(zone),
                    ["instances"] = count?[0] ?? 0, ["nearInstances"] = count?[1] ?? 0, ["saved"] = known, ["withoutInstance"] = withoutInstance,
                });
            }
            Vector2s reference = ZoneSystem.GetZone(net.GetReferencePosition());
            SimulationDistance range = net.GetSyncedSimulationDistance();
            return new Dictionary<string, object?>
            {
                ["source"] = Source, ["complete"] = true,
                ["reference"] = new Dictionary<string, object?> { ["x"] = (int)reference.x, ["z"] = (int)reference.y },
                ["simulation"] = new Dictionary<string, object?> { ["near"] = range.NearSimulationDistance, ["far"] = range.FarSimulationDistance, ["classic"] = range.IsClassic },
                ["zones"] = rows,
            };
        }

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;
    }
}
