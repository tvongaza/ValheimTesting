using Valheim.Testing;
using Xunit;

public class LayeredDumpTerrainTests
{
    private const string Header = WorldDumpManifest.Header + "\n";
    // The first coarse node sits at -10000 + 78*128 = -16; the fine nodes use the same global origin.
    private const string Coarse = Header +
        "-16,-16,10,Meadows,0.1,10,0.1,11\n" +
        "112,-16,20,Meadows,0.2,20,0.2,21\n" +
        "-16,112,30,Meadows,0.3,30,0.3,31\n" +
        "112,112,40,Meadows,0.4,40,0.4,41\n";
    private const string Fine = Header +
        "-16,-16,100,Plains,0.5,50,9.1,101\n" +
        "-8,-16,110,Plains,0.6,60,9.2,111\n" +
        "-16,-8,120,Plains,0.7,70,9.3,121\n" +
        "-8,-8,130,Plains,0.8,80,9.4,131\n";

    [Fact]
    public void FinestLayerWinsForOrdinaryFactsButNeverForBaseHeight()
    {
        using var files = new PinnedDumpFixture();
        var coarse = files.Capture("coarse", Coarse, 128);
        var fine = files.Capture("fine", Fine, 8);
        var layered = LayeredDumpTerrain.Load(new[] { coarse, fine }, "coarse");
        Assert.Equal(100f, layered.GetHeight(-16, -16));
        Assert.Equal(115f, layered.GetHeight(-12, -12));
        Assert.Equal(TerrainBiome.Plains, layered.GetBiome(-12, -12));
        layered.GetRiverWeight(-16, -16, out float weight, out float width);
        Assert.Equal((.5f, 50f), (weight, width));
        Assert.Equal(.1f, layered.GetBaseHeight(-16, -16)); // fine CSV says 9.1
        Assert.Equal(GridDumpTerrain.Load(coarse.Path).GetBaseHeight(-12, -12), layered.GetBaseHeight(-12, -12));
        Assert.Equal(40f, layered.GetHeight(112, 112)); // outside fine: coarse fallback
        Assert.Equal(TerrainBiome.Meadows, layered.GetBiome(112, 112));
        Assert.Equal(("fine", 8f, true), (layered.LayerAt(-16, -16).Provenance, layered.LayerAt(-16, -16).Spacing, layered.LayerAt(-16, -16).IsSampleNode(-16, -16)));
        Assert.Equal(("fine", false), (layered.LayerAt(-12, -12).Provenance, layered.LayerAt(-12, -12).IsSampleNode(-12, -12)));
        Assert.Equal(("coarse", true), (layered.BaseHeightLayer.Provenance, layered.BaseHeightLayer.IsSampleNode(-16, -16)));
        Assert.False(layered.BaseHeightLayer.IsSampleNode(-12, -12));
    }

    [Fact]
    public void EveryFineNodeAndCoarseOnlyNodeMatchesItsSource()
    {
        using var files = new PinnedDumpFixture();
        var coarse = files.Capture("coarse", Coarse, 128);
        var fine = files.Capture("fine", Fine, 8);
        var layered = LayeredDumpTerrain.Load(new[] { fine, coarse }, "coarse"); // input order must not decide precedence
        var fineGrid = GridDumpTerrain.Load(fine.Path);
        foreach (float z in new[] { -16f, -8f }) foreach (float x in new[] { -16f, -8f })
        {
            Assert.Equal(fineGrid.GetHeight(x, z), layered.GetHeight(x, z));
            Assert.Equal(fineGrid.GetBiome(x, z), layered.GetBiome(x, z));
            fineGrid.GetRiverWeight(x, z, out float expected, out _);
            layered.GetRiverWeight(x, z, out float actual, out _);
            Assert.Equal(expected, actual);
        }
        var coarseGrid = GridDumpTerrain.Load(coarse.Path);
        foreach (var point in new[] { (112f, -16f), (-16f, 112f), (112f, 112f) })
            Assert.Equal(coarseGrid.GetHeight(point.Item1, point.Item2), layered.GetHeight(point.Item1, point.Item2));
        Assert.Equal(coarseGrid.GetHeight(0, 0), layered.GetHeight(0, 0)); // coarse interpolation outside fine
    }

    [Fact]
    public void BoundsAndEqualResolutionOverlapAreNotGuessed()
    {
        using var files = new PinnedDumpFixture();
        var coarse = files.Capture("coarse", Coarse, 128);
        var fine = files.Capture("fine", Fine, 8);
        var layered = LayeredDumpTerrain.Load(new[] { coarse, fine }, "coarse");
        Assert.Equal("fine", layered.LayerAt(-8, -8).Provenance); // closed fine boundary
        Assert.Equal("coarse", layered.LayerAt(-7.9f, -8).Provenance);
        Assert.Throws<ArgumentOutOfRangeException>(() => layered.GetHeight(113, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => layered.GetBaseHeight(113, 0));
        var duplicate = files.Capture("duplicate", Fine, 8);
        Assert.Contains("overlap", Assert.Throws<InvalidDataException>(() => LayeredDumpTerrain.Load(new[] { coarse, fine, duplicate }, "coarse")).Message);
    }

    [Fact]
    public void MixedWorldMissingSourceAndCorruptFileAreRefusedBeforeUse()
    {
        using var files = new PinnedDumpFixture();
        var coarse = files.Capture("coarse", Coarse, 128);
        var foreign = files.Capture("foreign", Fine, 8, uid: "999");
        Assert.Contains("different world", Assert.Throws<InvalidDataException>(() => LayeredDumpTerrain.Load(new[] { coarse, foreign }, "coarse")).Message);
        var fine = files.Capture("fine", Fine, 8);
        Assert.Contains("was not supplied", Assert.Throws<InvalidDataException>(() => LayeredDumpTerrain.Load(new[] { coarse, fine }, "missing")).Message);
        File.AppendAllText(fine.Path, "damaged");
        Assert.Contains("SHA-256", Assert.Throws<InvalidDataException>(() => LayeredDumpTerrain.Load(new[] { coarse, fine }, "coarse")).Message);
    }

    [Fact]
    public void AOneMetreWindowComposesOverThe128MetreLayerNodeForNode()
    {
        // cli_world_dump 1 and 8 --window -16,-16,8 over the 128 m layer: the 1 m window holds the 8 m nodes and the
        // coarse node (-16,-16), all at full precision, and answers every query inside it from its own nodes.
        using var files = new PinnedDumpFixture();
        var coarse = files.Capture("coarse", Coarse, 128);
        var eight = files.Capture("eight", Window(-24, -8, 8), 8, window: (-16, -16, 8));
        var one = files.Capture("one", Window(-24, -8, 1), 1, window: (-16, -16, 8));
        var layered = LayeredDumpTerrain.Load(new[] { coarse, one, eight }, "coarse");
        var oneGrid = GridDumpTerrain.Load(one.Path);
        Assert.Equal((17, 17, 1f), (oneGrid.CountX, oneGrid.CountZ, oneGrid.Spacing));
        for (int z = -24; z <= -8; z++)
            for (int x = -24; x <= -8; x++)
            {
                Assert.Equal("one", layered.LayerAt(x, z).Provenance);
                Assert.True(layered.LayerAt(x, z).IsSampleNode(x, z));
                Assert.Equal(Ground(x, z), layered.GetHeight(x, z)); // the exact float the CSV wrote
                Assert.Equal(Blended(x, z), layered.GetBlendedHeight(x, z));
            }
        var eightGrid = GridDumpTerrain.Load(eight.Path);
        foreach (float z in new[] { -24f, -16f, -8f }) foreach (float x in new[] { -24f, -16f, -8f })
            Assert.Equal((eightGrid.GetHeight(x, z), eightGrid.GetBlendedHeight(x, z)), (oneGrid.GetHeight(x, z), oneGrid.GetBlendedHeight(x, z)));
        Assert.Equal("coarse", layered.LayerAt(-7, -8).Provenance); // just outside the window
        Assert.Equal(41f, layered.GetBlendedHeight(112, 112));
        Assert.Equal(.1f, layered.GetBaseHeight(-16, -16)); // base height still comes from the designated layer
    }

    [Fact]
    public void ADumpWithoutBlendedHeightSaysSo()
    {
        var grid = GridDumpTerrain.Read(new StringReader("x,z,height\n0,0,1\n8,0,1\n0,8,1\n8,8,1\n"), "plain.csv");
        Assert.False(grid.HasBlendedHeight);
        Assert.Contains("blended_height", Assert.Throws<NotSupportedException>(() => grid.GetBlendedHeight(0, 0)).Message);
    }

    // Swamp-like ground with a few metres of period, at values no short decimal holds.
    private static float Ground(int x, int z) => 30.000001f + 2.9f * MathF.Sin(x * 1.37f) * MathF.Cos(z * .91f);
    private static float Blended(int x, int z) => Ground(x, z) + .0123457f * x;

    private static string Window(int from, int to, int step)
    {
        var csv = new System.Text.StringBuilder(Header);
        for (int z = from; z <= to; z += step)
            for (int x = from; x <= to; x += step)
                csv.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2:R},Swamp,0,0,0.25,{3:R}\n", x, z, Ground(x, z), Blended(x, z)));
        return csv.ToString();
    }
}
