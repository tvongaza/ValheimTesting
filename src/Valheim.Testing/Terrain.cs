using System;

namespace Valheim.Testing;
public enum TerrainBiome { Unknown, Meadows, BlackForest, Swamp, Mountain, Plains, Mistlands, Ocean, Ashlands, DeepNorth }
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
    public PlaneTerrain(float originHeight = 40, float gradeX = 0, float gradeZ = 0)
    { OriginHeight = originHeight; GradeX = gradeX; GradeZ = gradeZ; }
    public float GetHeight(float x, float z) => OriginHeight + x * GradeX + z * GradeZ;
    public TerrainBiome GetBiome(float x, float z) => TerrainBiome.Meadows;
    public void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
}
internal static class TerrainMath
{
    internal static float Sqrt(float value) => (float)Math.Sqrt(value);
    internal static float Sin(float value) => (float)Math.Sin(value);
    internal static float Abs(float value) => Math.Abs(value);
    internal static float Clamp01(float value) => Math.Max(0, Math.Min(1, value));
    internal static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    internal static float SmoothStep(float a, float b, float t) { t = Clamp01(t); t = -2f * t * t * t + 3f * t * t; return b * t + a * (1f - t); }
}
