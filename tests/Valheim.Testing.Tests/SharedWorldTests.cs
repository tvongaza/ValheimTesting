using Valheim.Testing;
using Xunit;

public class SharedWorldTests
{
    [Fact]
    public void RegionsComposeCliffTerraceAndBiomeWithoutChangingBackground()
    {
        var slope = new PlaneTerrain(40, .25f, -.5f);
        var regions = new[] {
            new TerrainRegion((x,z) => x >= 0, new PlaneTerrain(80, 0, 0, TerrainBiome.Mountain)),
            TerrainRegion.Rectangle(4,-2,8,2,new PlaneTerrain(60))
        };
        var world = new CompositeTerrain(slope, regions);
        regions[0] = new TerrainRegion((x,z) => true, new PlaneTerrain(999));
        Assert.Equal(36,world.GetHeight(-8,4));
        Assert.Equal(80,world.GetHeight(0,0));
        Assert.Equal(60,world.GetHeight(4,-2));
        Assert.Equal(80,world.GetHeight(8,0));
        Assert.Equal(80,world.GetHeight(4,2));
        Assert.Equal(TerrainBiome.Mountain,world.GetBiome(0,0));
        Assert.Equal(TerrainBiome.Meadows,world.GetBiome(4,0));
        Assert.Equal(42,slope.GetHeight(8,0));
    }

    [Fact]
    public void RegionReplacesRiverFactsTooRatherThanCombiningUnrelatedInputs()
    {
        var river = new SyntheticTerrain { RiverX=0, RiverHalfWidth=8, HasMountain=false };
        var world = new CompositeTerrain(river, TerrainRegion.Rectangle(-2,-2,2,2,new PlaneTerrain(50)));
        world.GetRiverWeight(0,0,out float w,out float width);
        Assert.Equal(0,w); Assert.Equal(0,width);
        world.GetRiverWeight(4,0,out w,out width);
        Assert.Equal(.5f,w); Assert.Equal(16,width);
    }

    [Theory]
    [InlineData(0,0,0,1)] [InlineData(0,0,1,0)] [InlineData(1,1,0,2)]
    public void EmptyOrReversedRegionIsRejected(float x,float z,float xx,float zz) =>
        Assert.Throws<ArgumentException>(()=>TerrainRegion.Rectangle(x,z,xx,zz,new PlaneTerrain()));

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    public void NonfiniteCoordinatesAreRejected(float bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(()=>TerrainRegion.Rectangle(0,0,bad,1,new PlaneTerrain()));
        Assert.Throws<ArgumentOutOfRangeException>(()=>new CompositeTerrain(new PlaneTerrain()).GetHeight(bad,0));
    }
}
