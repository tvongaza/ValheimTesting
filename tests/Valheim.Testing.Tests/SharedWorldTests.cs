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

    private static TerrainZoneState Zone(float originX) => new(
        new TerrainGrid<float>(65,65,originX,-32,1,(x,z)=>40+x*.25f-z*.5f),
        new TerrainGrid<PaintRgba>(32,32,originX+1,-31,2,(x,z)=>new PaintRgba(.2f,.4f,.6f,.3f)));

    [Fact]
    public void AdjacentZonesShareCoordinatesButNotMutableStorage()
    {
        var world=new TerrainWorldState(); world.Add(0,0,Zone(-32)); world.Add(1,0,Zone(32));
        Assert.Equal(32,world[0,0].Heights.WorldX(64));
        Assert.Equal(32,world[1,0].Heights.WorldX(0));
        Assert.Equal(48,world[0,0].Heights[64,32]);
        Assert.Equal(48,world[1,0].Heights[0,32]);
        world[0,0].Heights[64,32]=53;
        Assert.Equal(48,world[1,0].Heights[0,32]); // a missed neighbour write must remain visible
        Assert.Equal(33,world[1,0].Paint.WorldX(0));
        Assert.Equal(95,world[1,0].Paint.WorldX(31));
    }

    [Fact]
    public void SnapshotDoesNotShareHeightOrPaintBuffersInEitherDirection()
    {
        var world=new TerrainWorldState(); world.Add(-1,2,Zone(-96));
        var saved=world.Clone();
        world[-1,2].Heights[0,0]=999;
        world[-1,2].Paint[0,0]=new PaintRgba(1,0,0,1);
        Assert.Equal(32,saved[-1,2].Heights[0,0]);
        Assert.Equal(new PaintRgba(.2f,.4f,.6f,.3f),saved[-1,2].Paint[0,0]);
        saved[-1,2].Heights[1,0]=777;
        saved[-1,2].Paint[1,0]=new PaintRgba(0,0,1,1);
        Assert.Equal(32.25f,world[-1,2].Heights[1,0]);
        Assert.Equal(new PaintRgba(.2f,.4f,.6f,.3f),world[-1,2].Paint[1,0]);
        saved.Add(0,0,Zone(-32)); Assert.Equal(1,world.Count);
    }

    [Fact]
    public void MissingAndDuplicateZonesAreErrors()
    {
        var world=new TerrainWorldState(); world.Add(0,0,Zone(-32));
        Assert.Throws<KeyNotFoundException>(()=>world[1,0]);
        Assert.Throws<ArgumentException>(()=>world.Add(0,0,Zone(-32)));
    }

    [Theory]
    [InlineData(-1,0)] [InlineData(65,0)] [InlineData(0,-1)] [InlineData(0,65)]
    public void OutOfRangeSamplesCannotAliasAnotherRow(int x,int z)
    {
        var zone=Zone(-32);
        Assert.Throws<ArgumentOutOfRangeException>(()=>zone.Heights[x,z]);
        Assert.Throws<ArgumentOutOfRangeException>(()=>zone.Heights[x,z]=99);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)]
    public void InvalidSpacingIsRejected(float spacing) =>
        Assert.Throws<ArgumentOutOfRangeException>(()=>new TerrainGrid<float>(2,2,0,0,spacing,(x,z)=>0));

    [Theory]
    [InlineData(-.1f)] [InlineData(1.1f)] [InlineData(float.NaN)]
    public void InvalidPaintIsRejected(float channel) =>
        Assert.Throws<ArgumentOutOfRangeException>(()=>new PaintRgba(0,0,0,channel));
}
