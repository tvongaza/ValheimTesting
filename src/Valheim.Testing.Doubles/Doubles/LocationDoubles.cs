// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Location placement fields, with the game's defaults (Valheim 1.0.16), and the placement rules a location
// definition is easiest to get wrong: altitude is measured from the water level (y = 30), not from 0, and a
// slope-rotated location faces local +Z downhill, snapped to 22.5 degrees. Generation itself is not modelled.

public partial class Heightmap
{
    /// <summary>Where a zone lies within its biome: near a biome edge, or in its middle.</summary>
    public enum BiomeArea
    {
        Edge = 1,
        Median = 2,
        Everything = Edge | Median,
    }
}

public partial class ZoneSystem
{
    /// <summary>The water level, as the game's field. Location placement measures altitude from 30 m itself, not from this field.</summary>
    public float m_waterLevel = 30f;

    public partial class ZoneLocation
    {
        public string m_name = "";
        public bool m_enable = true;
        public string m_prefabName = "";
        /// <summary>The biomes it may be placed in. None (the default) matches no biome, so nothing is placed.</summary>
        public Heightmap.Biome m_biome;
        public Heightmap.BiomeArea m_biomeArea = Heightmap.BiomeArea.Everything;
        public int m_quantity;
        public bool m_prioritized;
        public bool m_centerFirst;
        public bool m_unique;
        public string m_group = "";
        public float m_minDistanceFromSimilar;
        public string m_groupMax = "";
        public float m_maxDistanceFromSimilar;
        public bool m_iconAlways;
        public bool m_iconPlaced;
        /// <summary>A random yaw in 22.5-degree steps, unless <see cref="m_slopeRotation"/> is set.</summary>
        public bool m_randomRotation = true;
        /// <summary>Faces local +Z downhill, snapped to 22.5 degrees (<see cref="SlopeRotationYaw"/>).</summary>
        public bool m_slopeRotation;
        /// <summary>Placed at y = 30 (the water level) instead of on the ground.</summary>
        public bool m_snapToWater;
        public float m_interiorRadius;
        public bool m_clearArea;
        public float m_minTerrainDelta;
        public float m_maxTerrainDelta = 2f;
        public float m_minimumVegetation;
        public float m_maximumVegetation = 1f;
        public bool m_surroundCheckVegetation;
        public float m_surroundCheckDistance = 20f;
        public int m_surroundCheckLayers = 2;
        public float m_surroundBetterThanAverage;
        public bool m_inForest;
        public float m_forestTresholdMin;
        public float m_forestTresholdMax = 1f;
        public float m_minDistanceFromCenter;
        public float m_maxDistanceFromCenter;
        /// <summary>Minimum distance from the world origin; 0 means no limit.</summary>
        public float m_minDistance;
        /// <summary>Maximum distance from the world origin; 0 means no limit.</summary>
        public float m_maxDistance;
        /// <summary>Lowest ground height allowed, in metres above the water level (y = 30), not absolute.</summary>
        public float m_minAltitude = -1000f;
        /// <summary>Highest ground height allowed, in metres above the water level (y = 30), not absolute.</summary>
        public float m_maxAltitude = 1000f;

        public int Hash => m_prefab.Name.GetStableHashCode();

        /// <summary>The height the placement measures altitude from: 30 m, the default water level.</summary>
        public const float AltitudeZero = 30f;

        /// <summary>A ground height as the placement measures altitude: metres above the water level (y = 30), negative below it.</summary>
        public static float AltitudeAboveWater(float groundHeight) => (float)((double)groundHeight - AltitudeZero);

        /// <summary>
        /// Whether the placement's altitude rule accepts ground at this height: <see cref="m_minAltitude"/> ≤ height − 30 ≤
        /// <see cref="m_maxAltitude"/>, both ends included.
        /// </summary>
        public bool IsAltitudeAllowed(float groundHeight)
        {
            float altitude = AltitudeAboveWater(groundHeight);
            return !(altitude < m_minAltitude || altitude > m_maxAltitude);
        }

        /// <summary>
        /// The yaw in degrees (0 up to 360) the game gives a slope-rotated location: local +Z faces
        /// <paramref name="downhill"/> (from the highest ground sample towards the lowest; its height is ignored), rounded
        /// to the nearest 22.5 degrees with ties to even, as Unity's Mathf.Round. A level direction gives 0.
        /// </summary>
        public static float SlopeRotationYaw(UnityEngine.Vector3 downhill)
        {
            if (downhill.x == 0f && downhill.z == 0f) return 0f;
            float yaw = (float)(System.Math.Atan2(downhill.x, downhill.z) * 180.0 / System.Math.PI);
            if (yaw < 0f) yaw += 360f;
            float snapped = UnityEngine.Mathf.Round(yaw / 22.5f) * 22.5f;
            return snapped >= 360f ? snapped - 360f : snapped;
        }

        /// <summary>The first placement rule a point fails, in the game's order, or Accepted.</summary>
        public enum Placement { Accepted, Distance, Biome, Altitude, CenterDistance, TerrainDelta }

        /// <summary>
        /// Checks a candidate point against the rules the game applies to each point in turn: distance from the origin
        /// (<see cref="m_minDistance"/>/<see cref="m_maxDistance"/>, 0 = no limit), biome, altitude above the water level,
        /// distance from the centre (<see cref="m_minDistanceFromCenter"/>/<see cref="m_maxDistanceFromCenter"/>) and the
        /// terrain delta, which the game samples at random within <c>m_exteriorRadius</c> and the caller passes in.
        /// Heights and biomes come from <paramref name="world"/>. Not checked: the biome area, forest, distance to similar
        /// locations, vegetation, alternative biomes and whether the zone is free and ungenerated. Not a game method.
        /// </summary>
        public Placement CheckPlacement(float x, float z, WorldGenerator world, float terrainDelta = 0f)
        {
            // The candidate point has y = 0 when its distances are measured.
            float distance = (float)System.Math.Sqrt(x * x + z * z);
            if (m_minDistance != 0f && distance < m_minDistance || m_maxDistance != 0f && distance > m_maxDistance) return Placement.Distance;
            if ((m_biome & world.GetBiome(x, z)) == 0) return Placement.Biome;
            if (!IsAltitudeAllowed(world.GetHeight(x, z))) return Placement.Altitude;
            if (m_minDistanceFromCenter > 0f && distance < m_minDistanceFromCenter || m_maxDistanceFromCenter > 0f && distance > m_maxDistanceFromCenter)
                return Placement.CenterDistance;
            if (terrainDelta > m_maxTerrainDelta || terrainDelta < m_minTerrainDelta) return Placement.TerrainDelta;
            return Placement.Accepted;
        }
    }

    public partial struct LocationInstance
    {
        /// <summary>Whether the location has been spawned in its zone; generation registers it unplaced.</summary>
        public bool m_placed;
    }
}
