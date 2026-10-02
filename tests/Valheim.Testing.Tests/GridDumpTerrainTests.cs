using Valheim.Testing;
using Xunit;

public class GridDumpTerrainTests
{
    // A 3x2 grid in ValheimCLI's cli_world_dump layout, rows shuffled.
    //   z=12: x=-4 20 BlackForest | x=0 22 Swamp   | x=4 26 Mountain
    //   z=8:  x=-4 10 Ocean       | x=0 14 Meadows | x=4 30 AshLands (the game's spelling)
    private const string Dump =
        "x,z,height,biome,river,river_width,base_height\n" +
        "0,12,22.0,Swamp,0.00,0.0,0.01\n" +
        "-4,8,10.0,Ocean,0.50,40.0,-0.10000\n" +
        "4,12,26.0,Mountain,0.00,0.0,0.3\n" +
        "0,8,14.0,Meadows,0.25,40.0,0.02\n" +
        "\n" +
        "-4,12,20.0,BlackForest,0.00,0.0,0.1\n" +
        "4,8,30.0,AshLands,0.00,0.0,0.2\n";

    private static GridDumpTerrain Read(string csv) => GridDumpTerrain.Read(new StringReader(csv), "test.csv");

    [Fact]
    public void DumpIsReadAsOneEvenGrid()
    {
        var grid = Read(Dump);
        Assert.Equal((-4f, 8f, 4f, 3, 2), (grid.OriginX, grid.OriginZ, grid.Spacing, grid.CountX, grid.CountZ));
        Assert.Equal((4f, 12f), (grid.MaxX, grid.MaxZ));
        Assert.True(grid.HasBiome); Assert.True(grid.HasRiver);
        Assert.True(grid.HasBaseHeight);
        Assert.Equal("test.csv", grid.Provenance);
    }

    [Theory]
    [InlineData(-4, 8, 10)] [InlineData(0, 8, 14)] [InlineData(4, 8, 30)]
    [InlineData(-4, 12, 20)] [InlineData(0, 12, 22)] [InlineData(4, 12, 26)]
    public void NodesReturnTheirSampleUnchanged(float x, float z, float height) => Assert.Equal(height, Read(Dump).GetHeight(x, z));

    [Theory]
    [InlineData(-2, 8, 12)]       // (10 + 14) / 2
    [InlineData(2, 8, 22)]        // (14 + 30) / 2
    [InlineData(-4, 10, 15)]      // (10 + 20) / 2
    [InlineData(4, 10, 28)]       // (30 + 26) / 2, on the far edge
    [InlineData(-2, 10, 16.5f)]   // (10 + 14 + 20 + 22) / 4
    [InlineData(2, 10, 23)]       // (14 + 30 + 22 + 26) / 4
    [InlineData(-3, 9, 13.375f)]  // rows 11 and 20.5, a quarter of the way: 11 + 9.5 / 4
    public void MidpointsAreBilinear(float x, float z, float height) => Assert.Equal(height, Read(Dump).GetHeight(x, z));

    [Theory]
    [InlineData(-2.01f, 8, TerrainBiome.Ocean)]
    [InlineData(-2, 8, TerrainBiome.Meadows)]     // exactly halfway: the larger x
    [InlineData(-4, 9.99f, TerrainBiome.Ocean)]
    [InlineData(-4, 10, TerrainBiome.BlackForest)] // exactly halfway: the larger z
    [InlineData(4, 8, TerrainBiome.Ashlands)]
    [InlineData(3, 11, TerrainBiome.Mountain)]
    public void BiomeIsTheNearestNodesLabel(float x, float z, TerrainBiome biome) => Assert.Equal(biome, Read(Dump).GetBiome(x, z));

    [Fact]
    public void RiverWeightAndWidthAreSampledLikeHeight()
    {
        var grid = Read(Dump);
        grid.GetRiverWeight(-4, 8, out float weight, out float width);
        Assert.Equal((.5f, 40f), (weight, width));
        grid.GetRiverWeight(-2, 8, out weight, out width);
        Assert.Equal((.375f, 40f), (weight, width));   // (.5 + .25) / 2, (40 + 40) / 2
        grid.GetRiverWeight(-4, 10, out weight, out width);
        Assert.Equal((.25f, 20f), (weight, width));    // (.5 + 0) / 2, (40 + 0) / 2
    }

    [Fact]
    public void BaseHeightIsTheUnitlessGeneratorSampleNotGroundMetres()
    {
        // Four adjacent rows from a disposable Valheim 1.0.16 cli_world_dump, with no world identity or game files.
        var grid = Read("x,z,height,biome,river,river_width,base_height\n" +
            "-955,-1085,22.9,Meadows,0.80,90.9,0.06027\n" +
            "-950,-1085,24.1,Meadows,0.84,90.9,0.06609\n" +
            "-955,-1080,22.2,Meadows,0.77,91.1,0.06090\n" +
            "-950,-1080,23.2,Meadows,0.82,91.1,0.06655\n");
        Assert.Equal(22.9f, grid.GetHeight(-955, -1085));
        Assert.Equal(.06027f, grid.GetBaseHeight(-955, -1085));
        Assert.Equal(.06655f, grid.GetBaseHeight(-950, -1080));
        Assert.Equal((.06027f + .06609f + .06090f + .06655f) / 4, grid.GetBaseHeight(-952.5f, -1082.5f), 5);
    }

    [Theory]
    [InlineData(-4.01f, 8)] [InlineData(4.01f, 12)] [InlineData(0, 7.99f)] [InlineData(0, 12.5f)] [InlineData(-100, -100)]
    public void QueriesOutsideTheGridAreRefused(float x, float z)
    {
        var grid = Read(Dump);
        Assert.False(grid.Contains(x, z));
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetHeight(x, z));
        Assert.Contains("x -4..4, z 8..12", error.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetBiome(x, z));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetRiverWeight(x, z, out _, out _));
    }

    [Fact]
    public void BoundaryNodesAreInside()
    {
        var grid = Read(Dump);
        Assert.True(grid.Contains(-4, 8)); Assert.True(grid.Contains(4, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetHeight(float.NaN, 8));
    }

    [Fact]
    public void MissingLayersAreRefusedRatherThanInvented()
    {
        var grid = Read("x,z,height\n-8,-8,1\n0,-8,2\n-8,0,3\n0,0,4\n");
        Assert.False(grid.HasBiome); Assert.False(grid.HasRiver);
        Assert.False(grid.HasBaseHeight);
        Assert.Equal(2.5f, grid.GetHeight(-4, -4));
        Assert.Throws<NotSupportedException>(() => grid.GetBiome(0, 0));
        Assert.Throws<NotSupportedException>(() => grid.GetRiverWeight(0, 0, out _, out _));
        Assert.Throws<NotSupportedException>(() => grid.GetBaseHeight(0, 0));
    }

    [Fact]
    public void NegativeZeroIsTheSameCoordinateAsZero()
    {
        var grid = Read("z,x,height\n-0,-0,1\n0,4,2\n4,0,3\n4,4,4\n");
        Assert.Equal(1, grid.GetHeight(0, 0));
        Assert.Equal(3, grid.GetHeight(0, 4));
    }

    [Theory]
    [InlineData("x,z,height\n0,0,1\n4,0\n0,4,1\n4,4,1\n", "line 3 has 2 cells; the header has 3")]
    [InlineData("x,z,height\n0,0,1\n4,0,1,9\n0,4,1\n4,4,1\n", "line 3 has 4 cells")]
    [InlineData("x,z,height\n0,0,1\n4,0,1\n8,0,1\n0,4,1\n4,4,1\n", "the row at z=4 has 2 of 3 samples (first missing x=8); the grid is ragged")]
    [InlineData("x,z,height\n0,0,1\n4,0,1\n0,4,1\n4,4,1\n4,4,2\n", "line 6 repeats the node (4,4) from line 5")]
    [InlineData("x,z,height\n0,0,1\n4,0,1\n10,0,1\n0,4,1\n4,4,1\n10,4,1\n", "x values are not evenly spaced")]
    [InlineData("x,z,height\n0,0,1\n4,0,1\n0,8,1\n4,8,1\n", "x spacing 4 and z spacing 8 differ")]
    [InlineData("x,z,height\n0,0,1\n0,4,1\n", "at least two distinct x values")]
    [InlineData("x,z,elevation\n0,0,1\n", "required column 'height' is missing")]
    [InlineData("x,z,height,x\n0,0,1,0\n", "names column 'x' twice")]
    [InlineData("x,z,height,river\n0,0,1,0\n", "'river' and 'river_width' must be present together")]
    [InlineData("x,z,height,biome\n0,0,1,None\n", "unknown biome 'None'")]
    [InlineData("x,z,height,biome\n0,0,1,3\n", "unknown biome '3'")]
    [InlineData("x,z,height,biome\n0,0,1,Unknown\n", "unknown biome 'Unknown'")]
    [InlineData("x,z,height\n0,0,abc\n", "line 2 column 'height' is not a finite number")]
    [InlineData("x,z,height,base_height\n0,0,1,NaN\n", "line 2 column 'base_height' is not a finite number")]
    [InlineData("x,z,height\n0,0,NaN\n", "is not a finite number")]
    [InlineData("x,z,height\n", "no samples after the header")]
    [InlineData("", "empty file")]
    public void MalformedDumpsAreRefused(string csv, string reason)
    {
        var error = Assert.Throws<InvalidDataException>(() => Read(csv));
        Assert.Contains(reason, error.Message);
        Assert.StartsWith("test.csv: ", error.Message);
    }

    [Fact]
    public void LoadNamesTheFileWithoutItsDirectory()
    {
        string directory = Directory.CreateTempSubdirectory("griddump-").FullName;
        try
        {
            string path = Path.Combine(directory, "world.csv");
            File.WriteAllText(path, Dump);
            var grid = GridDumpTerrain.Load(path);
            Assert.Equal("world.csv", grid.Provenance);
            Assert.Equal(13.375f, grid.GetHeight(-3, 9));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DeclaredGridCopiesItsInputs()
    {
        var heights = new float[] { 1, 2, 3, 4 };
        var grid = new GridDumpTerrain("declared", 0, 0, 2, 2, 2, heights, new[] { TerrainBiome.Plains, TerrainBiome.Plains, TerrainBiome.Swamp, TerrainBiome.Swamp });
        heights[0] = 99;
        Assert.Equal(1, grid.GetHeight(0, 0));
        Assert.Equal(TerrainBiome.Swamp, grid.GetBiome(0, 1));
        Assert.Equal(2.5f, grid.GetHeight(1, 1));
    }

    [Fact]
    public void InvalidDeclaredGridsAreRefused()
    {
        var four = new float[] { 1, 2, 3, 4 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridDumpTerrain("g", 0, 0, 1, 1, 4, four));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridDumpTerrain("g", 0, 0, 0, 2, 2, four));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridDumpTerrain("g", float.NaN, 0, 1, 2, 2, four));
        Assert.Throws<ArgumentException>(() => new GridDumpTerrain("g", 0, 0, 1, 2, 3, four));
        Assert.Throws<ArgumentException>(() => new GridDumpTerrain("g", 0, 0, 1, 2, 2, new float[] { 1, 2, float.NaN, 4 }));
        Assert.Throws<ArgumentException>(() => new GridDumpTerrain("g", 0, 0, 1, 2, 2, four, riverWeights: four));
        Assert.Throws<ArgumentException>(() => new GridDumpTerrain("g", 0, 0, 1, 2, 2, four, new TerrainBiome[4]));
        Assert.Throws<ArgumentException>(() => new GridDumpTerrain(" ", 0, 0, 1, 2, 2, four));
    }
}
