using System;

namespace Valheim.Testing;

internal static class GridValues
{
    internal static void Finite(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), "A finite value is required.");
    }
}
public enum TerrainBiome { Unknown, Meadows, BlackForest, Swamp, Mountain, Plains, Mistlands, Ocean, AshLands, DeepNorth }
public interface ITerrain
{
    // Horizontal x/z, returned height y; all metres.
    float GetHeight(float x, float z);
    TerrainBiome GetBiome(float x, float z);
    void GetRiverWeight(float x, float z, out float weight, out float width);
}
public sealed class PlaneTerrain : ITerrain
{
    public float OriginHeight { get; }
    public float GradeX { get; }
    public float GradeZ { get; }
    public TerrainBiome Biome { get; }
    public PlaneTerrain(float originHeight = 40, float gradeX = 0, float gradeZ = 0)
        : this(originHeight, gradeX, gradeZ, TerrainBiome.Meadows) { }
    public PlaneTerrain(float originHeight, float gradeX, float gradeZ, TerrainBiome biome)
    { OriginHeight = originHeight; GradeX = gradeX; GradeZ = gradeZ; Biome = biome; }
    public float GetHeight(float x, float z) => OriginHeight + x * GradeX + z * GradeZ;
    public TerrainBiome GetBiome(float x, float z) => Biome;
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
