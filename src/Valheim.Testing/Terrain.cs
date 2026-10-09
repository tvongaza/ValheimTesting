using System;

namespace Valheim.Testing;

internal static class GridValues
{
    internal static void Finite(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), "A finite value is required.");
    }
}
/// <summary>Biome labels used by declared terrain inputs; <see cref="Unknown"/> means no biome was supplied.</summary>
public enum TerrainBiome { Unknown, Meadows, BlackForest, Swamp, Mountain, Plains, Mistlands, Ocean, AshLands, DeepNorth }
/// <summary>A declared terrain input. Horizontal x/z and returned height y are in metres; it does not run Valheim's generator.</summary>
public interface ITerrain
{
    /// <summary>Returns ground height y in metres at horizontal <paramref name="x"/>/<paramref name="z"/>.</summary>
    float GetHeight(float x, float z);
    /// <summary>Returns the declared biome at horizontal <paramref name="x"/>/<paramref name="z"/>.</summary>
    TerrainBiome GetBiome(float x, float z);
    /// <summary>Returns the declared river weight and width; their interpretation belongs to the consuming mod.</summary>
    void GetRiverWeight(float x, float z, out float weight, out float width);
}
/// <summary>A deterministic plane with constant biome and no river: height is origin height plus x and z grades.</summary>
public sealed class PlaneTerrain : ITerrain
{
    /// <summary>The height at x=0, z=0, in metres.</summary>
    public float OriginHeight { get; }
    /// <summary>The change in height for one metre along x.</summary>
    public float GradeX { get; }
    /// <summary>The change in height for one metre along z.</summary>
    public float GradeZ { get; }
    /// <summary>The biome returned at every coordinate.</summary>
    public TerrainBiome Biome { get; }
    /// <summary>Creates a Meadows plane; defaults to 40 m high with zero grade.</summary>
    public PlaneTerrain(float originHeight = 40, float gradeX = 0, float gradeZ = 0)
        : this(originHeight, gradeX, gradeZ, TerrainBiome.Meadows) { }
    /// <summary>Creates a plane with the given origin height, x/z grades and constant biome.</summary>
    public PlaneTerrain(float originHeight, float gradeX, float gradeZ, TerrainBiome biome)
    { OriginHeight = originHeight; GradeX = gradeX; GradeZ = gradeZ; Biome = biome; }
    /// <summary>Returns <see cref="OriginHeight"/> + x * <see cref="GradeX"/> + z * <see cref="GradeZ"/> in metres.</summary>
    public float GetHeight(float x, float z) => OriginHeight + x * GradeX + z * GradeZ;
    /// <summary>Returns the constant <see cref="Biome"/>.</summary>
    public TerrainBiome GetBiome(float x, float z) => Biome;
    /// <summary>Returns zero river weight and width at every coordinate.</summary>
    public void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
}
internal static class TerrainMath
{
    /// <summary>The game's sea level in metres (ZoneSystem.m_waterLevel); the one copy every default here uses.</summary>
    internal const float SeaLevel = 30f;
    internal static float Sqrt(float value) => (float)Math.Sqrt(value);
    internal static float Sin(float value) => (float)Math.Sin(value);
    internal static float Abs(float value) => Math.Abs(value);
    internal static float Clamp01(float value) => Math.Max(0, Math.Min(1, value));
    internal static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    internal static float SmoothStep(float a, float b, float t) { t = Clamp01(t); t = -2f * t * t * t + 3f * t * t; return b * t + a * (1f - t); }
}
