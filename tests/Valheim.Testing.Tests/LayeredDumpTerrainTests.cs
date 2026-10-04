using Valheim.Testing;
using Xunit;

public class LayeredDumpTerrainTests
{
    private const string Header = "x,z,height,biome,river,river_width,base_height\n";
    // The first coarse node sits at -10000 + 78*128 = -16; the fine nodes use the same global origin.
    private const string Coarse = Header +
        "-16,-16,10,Meadows,0.1,10,0.1\n" +
        "112,-16,20,Meadows,0.2,20,0.2\n" +
        "-16,112,30,Meadows,0.3,30,0.3\n" +
        "112,112,40,Meadows,0.4,40,0.4\n";
    private const string Fine = Header +
        "-16,-16,100,Plains,0.5,50,9.1\n" +
        "-8,-16,110,Plains,0.6,60,9.2\n" +
        "-16,-8,120,Plains,0.7,70,9.3\n" +
        "-8,-8,130,Plains,0.8,80,9.4\n";

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
}
