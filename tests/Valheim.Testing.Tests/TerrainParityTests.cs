using Valheim.Testing;
using Xunit;

public class TerrainParityTests
{
    // h = 30 + x^2/64, independent of z. Every value used below is exact in float.
    private sealed class Parabola : ITerrain
    {
        public float GetHeight(float x, float z) => 30 + x * x / 64;
        public TerrainBiome GetBiome(float x, float z) => TerrainBiome.Meadows;
        public void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }

    // The parabola sampled every 8 m over x -32..32, z -8..8, placed with its first node at originX.
    private static GridDumpTerrain Sampled(float originX)
    {
        var heights = new float[9 * 3];
        for (int j = 0; j < 3; j++) for (int i = 0; i < 9; i++) heights[j * 9 + i] = 30 + (-32 + 8 * i) * (-32 + 8 * i) / 64f;
        return new GridDumpTerrain("parabola", originX, -8, 8, 9, 3, heights);
    }

    [Fact]
    public void GridMatchesItsSourceWithinTheBilinearError()
    {
        // Between nodes 8 m apart, a chord of x^2/64 sits (8/2)^2/64 = 0.25 m above the curve at the midpoint.
        var report = TerrainParity.Compare(new Parabola(), Sampled(-32), new TerrainArea(-32, -8, 32, 8), 2, .25f);
        Assert.True(report.Passed, report.ToString());
        Assert.Equal(33 * 9, report.SampleCount);
        Assert.Equal(.25, report.MaxHeightDifference);
        Assert.Equal((-28f, -8f, 42.25f, 42.5f), (report.WorstHeights[0].X, report.WorstHeights[0].Z, report.WorstHeights[0].Expected, report.WorstHeights[0].Actual));
        Assert.Equal(5, report.WorstHeights.Count);

        var nodesOnly = TerrainParity.Compare(new Parabola(), Sampled(-32), new TerrainArea(-32, -8, 32, 8), 8, 0);
        Assert.True(nodesOnly.Passed, nodesOnly.ToString());
        Assert.Equal(0, nodesOnly.MaxHeightDifference);
    }

    [Fact]
    public void GridShiftedByOneCellFails()
    {
        // Negative control: the same samples placed one cell east. At a node x the shifted grid holds
        // f(x - 8), so the difference is ((x - 8)^2 - x^2)/64 = 1 - x/4: +7 at x = -24 and -7 at x = 32.
        var shifted = Sampled(-24);
        var area = new TerrainArea(-24, -8, 32, 8);
        var report = TerrainParity.Compare(Sampled(-32), shifted, area, 8, .5f);
        Assert.False(report.Passed);
        Assert.Equal(24, report.SampleCount);
        Assert.Equal(24, report.HeightFailures); // |1 - x/4| is at least 1 at every node
        Assert.Equal(7, report.MaxHeightDifference);
        var worst = report.WorstHeights;
        Assert.Equal((-24f, -8f, 39f, 46f, 7f), (worst[0].X, worst[0].Z, worst[0].Expected, worst[0].Actual, worst[0].Difference));
        Assert.Equal((32f, -8f, -7f), (worst[1].X, worst[1].Z, worst[1].Difference));
        Assert.Equal((-24f, 0f), (worst[2].X, worst[2].Z));
        Assert.StartsWith("FAIL: 24 samples; 24 heights beyond 0.5 m (max difference 7 m).", report.ToString());
        Assert.Contains("height (-24,-8) expected 39 actual 46 difference 7", report.ToString());

        // The same shift also fails against the analytic source, which the unshifted grid passes at this tolerance.
        Assert.False(TerrainParity.Compare(new Parabola(), shifted, area, 2, .5f).Passed);
        Assert.True(TerrainParity.Compare(new Parabola(), Sampled(-32), area, 2, .5f).Passed);
    }

    [Fact]
    public void BiomeAndRiverDifferencesAreReportedWhenAskedFor()
    {
        var meadow = new PlaneTerrain(40);
        var withHill = new CompositeTerrain(meadow, TerrainRegion.Rectangle(0, 0, 4, 4, new PlaneTerrain(40, 0, 0, TerrainBiome.Mountain)));
        var area = new TerrainArea(-4, -4, 4, 4);
        Assert.True(TerrainParity.Compare(meadow, withHill, area, 2, 0).Passed); // heights alone agree

        var report = TerrainParity.Compare(meadow, withHill, area, 2, 0, compareBiomes: true, worstCount: 3);
        Assert.False(report.Passed);
        Assert.Equal(4, report.BiomeMismatches); // x and z in {0, 2}: the rectangle excludes its maximum
        Assert.Equal(3, report.FirstBiomeMismatches.Count);
        Assert.Equal((0f, 0f, TerrainBiome.Meadows, TerrainBiome.Mountain),
            (report.FirstBiomeMismatches[0].X, report.FirstBiomeMismatches[0].Z, report.FirstBiomeMismatches[0].Expected, report.FirstBiomeMismatches[0].Actual));

        var river = new SyntheticTerrain { RiverX = 0, RiverHalfWidth = 8, HasMountain = false };
        var rivers = TerrainParity.Compare(river, river, area, 2, 0, riverWeightTolerance: 0);
        Assert.True(rivers.Passed);
        var noRiver = new CompositeTerrain(river, TerrainRegion.Rectangle(-2, -2, 2, 2, new PlaneTerrain(0)));
        rivers = TerrainParity.Compare(river, noRiver, new TerrainArea(-2, -2, 2, 2), 2, 100, riverWeightTolerance: .1f);
        Assert.Equal(4, rivers.RiverFailures); // x and z in {-2, 0} lose their weight; x or z = 2 lies outside the rectangle
        Assert.Equal((0f, 0f, -1f), (rivers.WorstRiverWeights[0].X, rivers.WorstRiverWeights[0].Z, rivers.WorstRiverWeights[0].Difference)); // full weight on the channel
    }

    [Fact]
    public void AreaBeyondASourceIsRefusedNotPadded() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainParity.Compare(new Parabola(), Sampled(-32), new TerrainArea(-32, -8, 40, 8), 8, 1));

    [Fact]
    public void NonFiniteHeightsFail()
    {
        var report = TerrainParity.Compare(new PlaneTerrain(40), new PlaneTerrain(float.NaN), new TerrainArea(0, 0, 1, 1), 1, 1000);
        Assert.False(report.Passed);
        Assert.Equal(4, report.HeightFailures);
        Assert.True(double.IsPositiveInfinity(report.MaxHeightDifference));
    }

    [Fact]
    public void InvalidArgumentsAreRefused()
    {
        var plane = new PlaneTerrain();
        var area = new TerrainArea(0, 0, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainParity.Compare(plane, plane, area, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainParity.Compare(plane, plane, area, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainParity.Compare(plane, plane, area, 1, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainParity.Compare(plane, plane, area, 1, 1, riverWeightTolerance: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainParity.Compare(plane, plane, new TerrainArea(0, 0, 100000, 100000), .001f, 1));
        Assert.Throws<ArgumentException>(() => new TerrainArea(0, 0, 0, 1));
    }
}
