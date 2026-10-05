// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter (a BepInEx plugin that
// references ValheimCLI and the game). Not part of the mod itself; install the adapter only in test runtimes.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// A capped census of a zone's saved objects (ZDOs), loaded or not. Throws instead of returning a truncated list or the
    /// objects of another zone. Run in game by FullLifecycle's marker observation.
    /// </summary>
    public static class SavedObjects
    {
        /// <summary>Zones the census can address: the game files zones outside this range under one shared slot.</summary>
        public const int MaxZoneIndex = 255;

        /// <summary>
        /// The saved objects (ZDOs) this process knows in <paramref name="zone"/>, loaded or not, that match
        /// <paramref name="include"/>. On the server that is every saved object there. More than <paramref name="limit"/>
        /// matches throws rather than returning a truncated list. Zones beyond ±<see cref="MaxZoneIndex"/> are refused: the
        /// game files them all under one slot, whose objects would be returned instead.
        /// </summary>
        public static List<ZDO> InZone(Vector2s zone, int limit, Func<ZDO, bool>? include = null)
        {
            if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
            if (zone.x < -MaxZoneIndex || zone.x > MaxZoneIndex || zone.y < -MaxZoneIndex || zone.y > MaxZoneIndex)
                throw new ArgumentOutOfRangeException(nameof(zone), $"Zone {zone.x},{zone.y} is outside ±{MaxZoneIndex}.");
            var objects = ZDOMan.instance ?? throw new InvalidOperationException("No ZDOMan: load a world first.");
            var found = new List<ZDO>();
            Members.Call(Members.Method(typeof(ZDOMan), "FindObjects", typeof(Vector2s), typeof(List<ZDO>), typeof(HashSet<ZoneSystem.SectorIndex>)),
                objects, zone, found, new HashSet<ZoneSystem.SectorIndex>());
            var matched = include == null ? found : found.Where(include).ToList();
            if (matched.Count > limit)
                throw new InvalidOperationException($"Zone {zone.x},{zone.y} has {matched.Count} matching objects, more than the census limit of {limit}; nothing is returned rather than a truncated list.");
            return matched;
        }
    }
}
