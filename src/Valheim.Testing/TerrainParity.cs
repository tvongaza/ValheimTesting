using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Valheim.Testing;

/// <summary>One compared coordinate; <see cref="Difference"/> is actual minus expected (NaN when either is not finite).</summary>
public sealed class TerrainParityPoint
{
    public float X { get; }
    public float Z { get; }
    public float Expected { get; }
    public float Actual { get; }
    public float Difference { get; }
    internal TerrainParityPoint(float x, float z, float expected, float actual)
    {
        X = x; Z = z; Expected = expected; Actual = actual;
        Difference = Finite(expected) && Finite(actual) ? actual - expected : float.NaN;
    }
    internal double Rank => float.IsNaN(Difference) ? double.PositiveInfinity : Math.Abs(Difference);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0},{1}) expected {2} actual {3} difference {4}", X, Z, Expected, Actual, Difference);
}

/// <summary>A biome that differs at one coordinate.</summary>
public sealed class TerrainBiomeMismatch
{
    public float X { get; }
    public float Z { get; }
    public TerrainBiome Expected { get; }
    public TerrainBiome Actual { get; }
    internal TerrainBiomeMismatch(float x, float z, TerrainBiome expected, TerrainBiome actual) { X = x; Z = z; Expected = expected; Actual = actual; }
    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0},{1}) expected {2} actual {3}", X, Z, Expected, Actual);
}

/// <summary>What <see cref="TerrainParity.Compare"/> found. Worst lists hold the largest differences whether or not they fail.</summary>
public sealed class TerrainParityReport
{
    public int SampleCount { get; }
    public float HeightTolerance { get; }
    /// <summary>Samples whose height differs by more than the tolerance, or where either height is not finite.</summary>
    public int HeightFailures { get; }
    /// <summary>Largest absolute height difference; +infinity when a height was not finite.</summary>
    public double MaxHeightDifference { get; }
    public IReadOnlyList<TerrainParityPoint> WorstHeights { get; }
    public bool BiomesCompared { get; }
    public int BiomeMismatches { get; }
    /// <summary>The first mismatches in sampling order (z rows, x fastest), up to the worst count.</summary>
    public IReadOnlyList<TerrainBiomeMismatch> FirstBiomeMismatches { get; }
    public float? RiverWeightTolerance { get; }
    public int RiverFailures { get; }
    public IReadOnlyList<TerrainParityPoint> WorstRiverWeights { get; }
    public bool Passed => HeightFailures == 0 && BiomeMismatches == 0 && RiverFailures == 0;

    internal TerrainParityReport(int samples, float heightTolerance, int heightFailures, double maxHeight, IReadOnlyList<TerrainParityPoint> worstHeights,
        bool biomesCompared, int biomeMismatches, IReadOnlyList<TerrainBiomeMismatch> firstBiomes,
        float? riverTolerance, int riverFailures, IReadOnlyList<TerrainParityPoint> worstRivers)
    {
        SampleCount = samples; HeightTolerance = heightTolerance; HeightFailures = heightFailures; MaxHeightDifference = maxHeight; WorstHeights = worstHeights;
        BiomesCompared = biomesCompared; BiomeMismatches = biomeMismatches; FirstBiomeMismatches = firstBiomes;
        RiverWeightTolerance = riverTolerance; RiverFailures = riverFailures; WorstRiverWeights = worstRivers;
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        text.Append(Passed ? "PASS" : "FAIL").Append(": ").Append(SampleCount.ToString(c)).Append(" samples; ")
            .Append(HeightFailures.ToString(c)).Append(" heights beyond ").Append(HeightTolerance.ToString(c)).Append(" m (max difference ")
            .Append(MaxHeightDifference.ToString(c)).Append(" m)");
        if (BiomesCompared) text.Append("; ").Append(BiomeMismatches.ToString(c)).Append(" biome mismatches");
        if (RiverWeightTolerance.HasValue) text.Append("; ").Append(RiverFailures.ToString(c)).Append(" river weights beyond ").Append(RiverWeightTolerance.Value.ToString(c));
        text.Append('.');
        foreach (var point in WorstHeights) text.AppendLine().Append("  height ").Append(point);
        foreach (var mismatch in FirstBiomeMismatches) text.AppendLine().Append("  biome ").Append(mismatch);
        foreach (var point in WorstRiverWeights) text.AppendLine().Append("  river weight ").Append(point);
        return text.ToString();
    }
}

/// <summary>
/// Golden parity between two terrain sources: samples both on one lattice over an area and reports where they
/// disagree. Samples sit at <c>MinX + i * step</c>, <c>MinZ + j * step</c> up to and including the area's
/// maximum, so a lattice matching a grid's spacing and origin lands on its nodes. Heights pass within an
/// absolute tolerance in metres. Biomes, when compared, must be equal. River weights, when a tolerance is given,
/// pass within it; river width is not compared. A source that refuses a coordinate fails the comparison with its
/// own exception: choose an area both sources cover. Neither source is treated as correct; the names only say
/// which value is which in the report.
/// </summary>
public static class TerrainParity
{
    /// <summary>Largest lattice compared in one call.</summary>
    public const int MaxSamples = 10000000;

    public static TerrainParityReport Compare(ITerrain expected, ITerrain actual, TerrainArea area, float step, float heightTolerance,
        bool compareBiomes = false, float? riverWeightTolerance = null, int worstCount = 5)
    {
        if (expected == null) throw new ArgumentNullException(nameof(expected));
        if (actual == null) throw new ArgumentNullException(nameof(actual));
        GridValues.Finite(step);
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step), "Step must be positive.");
        CheckTolerance(heightTolerance, nameof(heightTolerance));
        if (riverWeightTolerance.HasValue) CheckTolerance(riverWeightTolerance.Value, nameof(riverWeightTolerance));
        if (worstCount < 0) throw new ArgumentOutOfRangeException(nameof(worstCount));
        long countX = Count(area.MaxX - (double)area.MinX, step), countZ = Count(area.MaxZ - (double)area.MinZ, step);
        if (countX * countZ > MaxSamples) throw new ArgumentOutOfRangeException(nameof(step), $"{countX}x{countZ} samples exceeds {MaxSamples}; use a larger step or a smaller area.");

        var worstHeights = new List<TerrainParityPoint>();
        var worstRivers = new List<TerrainParityPoint>();
        var firstBiomes = new List<TerrainBiomeMismatch>();
        int heightFailures = 0, biomeMismatches = 0, riverFailures = 0;
        double maxHeight = 0;
        for (long j = 0; j < countZ; j++)
        {
            float z = (float)(area.MinZ + j * (double)step);
            for (long i = 0; i < countX; i++)
            {
                float x = (float)(area.MinX + i * (double)step);
                var height = new TerrainParityPoint(x, z, expected.GetHeight(x, z), actual.GetHeight(x, z));
                if (height.Rank > heightTolerance) heightFailures++;
                maxHeight = Math.Max(maxHeight, height.Rank);
                KeepWorst(worstHeights, height, worstCount);
                if (compareBiomes)
                {
                    TerrainBiome e = expected.GetBiome(x, z), a = actual.GetBiome(x, z);
                    if (e != a && biomeMismatches++ < worstCount) firstBiomes.Add(new TerrainBiomeMismatch(x, z, e, a));
                }
                if (riverWeightTolerance.HasValue)
                {
                    expected.GetRiverWeight(x, z, out float e, out _);
                    actual.GetRiverWeight(x, z, out float a, out _);
                    var river = new TerrainParityPoint(x, z, e, a);
                    if (river.Rank > riverWeightTolerance.Value) riverFailures++;
                    KeepWorst(worstRivers, river, worstCount);
                }
            }
        }
        return new TerrainParityReport(checked((int)(countX * countZ)), heightTolerance, heightFailures, maxHeight, worstHeights,
            compareBiomes, biomeMismatches, firstBiomes, riverWeightTolerance, riverFailures, worstRivers);
    }

    private static void CheckTolerance(float tolerance, string name)
    {
        if (float.IsNaN(tolerance) || float.IsInfinity(tolerance) || tolerance < 0) throw new ArgumentOutOfRangeException(name, "A tolerance must be finite and not negative.");
    }

    private static long Count(double size, float step)
    {
        double steps = Math.Floor(size / step + 1e-6);
        if (steps >= MaxSamples) throw new ArgumentOutOfRangeException(nameof(step), $"{steps + 1} samples along one side exceeds {MaxSamples}; use a larger step or a smaller area.");
        return (long)steps + 1;
    }

    // Largest first; among equal differences the earlier sample (lower z, then lower x) stays ahead.
    private static void KeepWorst(List<TerrainParityPoint> worst, TerrainParityPoint point, int count)
    {
        if (count == 0) return;
        int at = worst.Count;
        while (at > 0 && worst[at - 1].Rank < point.Rank) at--;
        if (at >= count) return;
        worst.Insert(at, point);
        if (worst.Count > count) worst.RemoveAt(count);
    }
}
