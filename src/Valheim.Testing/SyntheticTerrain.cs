using System;

namespace Valheim.Testing;

/// <summary>
/// Deterministic pseudo-Valheim world: an island with a gaussian hill profile,
/// a mountain ridge, and an optional river channel crossing it. Heights use
/// the same conventions as the game (sea level 30, deep water below 28).
/// </summary>
public class SyntheticTerrain : ITerrain
{
    public const int FixtureVersion = 1;
    public bool HasRiver = true;

    /// <summary>River channel runs roughly north-south near x = RiverX.</summary>
    public float RiverX = 100f;
    public float RiverHalfWidth = 24f;

    public bool HasMountain = true;
    public float MountainX = -250f;
    public float MountainHalfWidth = 140f;
    public float MountainHeight = 42f;

    public float IslandRadius = 600f;
    public float IslandPeakHeight = 18f; // above sea level at the island center

    public float GetHeight(float wx, float wy)
    {
        float r = TerrainMath.Sqrt(wx * wx + wy * wy);

        // Island: smooth dome from sea floor (20) to a low inland plateau.
        float t = TerrainMath.Clamp01(1f - r / IslandRadius);
        float height = 20f + (10f + IslandPeakHeight) * TerrainMath.SmoothStep(0f, 1f, t * 1.6f);

        // Gentle deterministic roughness so the terrain is not perfectly flat.
        height += Noise(wx * 0.02f, wy * 0.02f) * 1.5f;

        // Mountain ridge running north-south.
        if (HasMountain)
        {
            float md = TerrainMath.Abs(wx - MountainX - TerrainMath.Sin(wy * 0.004f) * 40f);
            float mt = TerrainMath.Clamp01(1f - md / MountainHalfWidth);
            height += MountainHeight * mt * mt;
        }

        // River carves the terrain down below sea level in its channel.
        if (HasRiver)
        {
            float weight = RiverWeightAt(wx, wy);
            if (weight > 0f)
                height = TerrainMath.Lerp(height, 26f, weight);
        }

        return height;
    }

    public TerrainBiome GetBiome(float wx, float wy)
    {
        if (GetHeight(wx, wy) < 30f - 2f)
            return TerrainBiome.Ocean;
        if (HasMountain && TerrainMath.Abs(wx - MountainX) < MountainHalfWidth * 0.6f)
            return TerrainBiome.Mountain;
        return TerrainBiome.Meadows;
    }

    public void GetRiverWeight(float wx, float wy, out float weight, out float width)
    {
        weight = HasRiver ? RiverWeightAt(wx, wy) : 0f;
        width = weight > 0f ? RiverHalfWidth * 2f : 0f;
    }

    private float RiverWeightAt(float wx, float wy)
    {
        // Meandering channel, only inside the island footprint.
        float channelX = RiverX + TerrainMath.Sin(wy * 0.006f) * 60f;
        float d = TerrainMath.Abs(wx - channelX);
        if (d >= RiverHalfWidth)
            return 0f;
        return TerrainMath.Clamp01(1f - d / RiverHalfWidth);
    }

    /// <summary>Cheap deterministic value noise (no Unity PerlinNoise available headless).</summary>
    private static float Noise(float x, float y)
    {
        int xi = (int)System.Math.Floor(x);
        int yi = (int)System.Math.Floor(y);
        float xf = x - xi, yf = y - yi;

        float a = Hash(xi, yi);
        float b = Hash(xi + 1, yi);
        float c = Hash(xi, yi + 1);
        float d = Hash(xi + 1, yi + 1);

        float u = xf * xf * (3f - 2f * xf);
        float v = yf * yf * (3f - 2f * yf);

        return TerrainMath.Lerp(TerrainMath.Lerp(a, b, u), TerrainMath.Lerp(c, d, u), v);
    }

    private static float Hash(int x, int y)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0x7fffffff) / (float)int.MaxValue;
        }
    }
}
