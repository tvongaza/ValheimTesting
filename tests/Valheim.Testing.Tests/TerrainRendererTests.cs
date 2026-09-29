using System.IO.Compression;
using System.Security.Cryptography;
using Valheim.Testing;
using Xunit;

public class TerrainRendererTests
{
    private static readonly RenderColor Red = new(255, 0, 0), Black = new(0, 0, 0);

    // Heights 30 + x + z/2 (exact in float at pixel centres), a 70 m Mountain block over x 2..8, z -2..6.
    private static readonly ITerrain Ground = new CompositeTerrain(new PlaneTerrain(30, 1, .5f),
        TerrainRegion.Rectangle(2, -2, 8, 6, new PlaneTerrain(70, 0, 0, TerrainBiome.Mountain)));

    // 16x12 pixels of 1 m over x -8..8, z -6..6.
    private static TerrainRenderer Renderer(TerrainColoring coloring = TerrainColoring.Height) =>
        new TerrainRenderer(new TerrainArea(-8, -6, 8, 6), 1) { Coloring = coloring };

    [Theory]
    // Golden hashes computed by a separate encoder (Python, using zlib's crc32 and adler32) from this fixture's
    // description, not by this renderer. Each OS in CI must produce exactly these bytes.
    [InlineData(TerrainColoring.Height, "d76fb3f72fc3a86f058a94e20a855a9d057a6201eb2cff69450ae291efa9e305")]
    [InlineData(TerrainColoring.Biome, "e9cd953d2ffd99ddd0054ab9a8df8f0f0e3d41e90a09ae056e54a42f034f868d")]
    public void PngBytesAreStableForAFixedTerrain(TerrainColoring coloring, string sha256)
    {
        byte[] png = Renderer(coloring).Polyline(new[] { (-6f, -4f), (6f, 4f) }, Red).Point(0, 0, Black, 1).Render(Ground).ToPng();
        Assert.Equal(656, png.Length);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant());
    }

    [Fact]
    public void NorthIsUpAndEachPixelSamplesItsCentre()
    {
        var renderer = Renderer(TerrainColoring.Biome);
        Assert.Equal((16, 12), (renderer.Width, renderer.Height));
        Assert.Equal((-7.5f, 5.5f), (renderer.PixelX(0), renderer.PixelZ(0)));
        Assert.Equal((7.5f, -5.5f), (renderer.PixelX(15), renderer.PixelZ(11)));
        var image = renderer.Render(Ground);
        var mountain = TerrainRenderer.BiomeColor(TerrainBiome.Mountain);
        var meadows = TerrainRenderer.BiomeColor(TerrainBiome.Meadows);
        Assert.Equal(mountain, image[15, 0]);  // x 7.5, z 5.5
        Assert.Equal(mountain, image[10, 7]);  // x 2.5, z -1.5
        Assert.Equal(meadows, image[10, 8]);   // z -2.5, below the block
        Assert.Equal(meadows, image[9, 0]);    // x 1.5, west of the block
        Assert.Equal(meadows, image[0, 11]);
    }

    [Theory]
    [InlineData(30, 64, 120, 48)]    // land starts green at the water level
    [InlineData(47.5f, 112, 130, 72)] // a quarter of the land range: halfway from green to brown
    [InlineData(65, 160, 140, 96)]   // midpoint of 30..100: brown
    [InlineData(100, 245, 245, 245)] // top: white
    [InlineData(150, 245, 245, 245)] // beyond the range: the end colour
    [InlineData(15, 56, 100, 155)]   // halfway down to LowHeight 0: halfway from light to dark blue
    [InlineData(0, 16, 40, 100)]
    [InlineData(-10, 16, 40, 100)]
    public void HeightColoursFollowTheDocumentedRamp(float height, int r, int g, int b) =>
        Assert.Equal(new RenderColor((byte)r, (byte)g, (byte)b), Renderer().HeightColor(height));

    [Fact]
    public void WithoutWaterEveryHeightIsLand()
    {
        var renderer = Renderer(); renderer.WaterLevel = null;
        Assert.Equal(new RenderColor(64, 120, 48), renderer.HeightColor(0));
        Assert.Equal(new RenderColor(160, 140, 96), renderer.HeightColor(50));
    }

    [Fact]
    public void OverlaysDrawPolylinesThenPointsAndClipAtTheEdge()
    {
        var image = Renderer(TerrainColoring.Biome)
            .Point(.5f, .5f, Black, 1)
            .Polyline(new[] { (-7.5f, 5.5f), (-4.5f, 2.5f) }, Red)   // pixels (0,0) to (3,3)
            .Polyline(new[] { (.5f, .5f) }, Red)                      // under the point: the point wins
            .Polyline(new[] { (-100f, -.5f), (100f, -.5f) }, Red)     // row 6, far beyond both edges
            .Render(new PlaneTerrain(40));
        var meadows = TerrainRenderer.BiomeColor(TerrainBiome.Meadows);
        foreach (int i in new[] { 0, 1, 2, 3 }) Assert.Equal(Red, image[i, i]);
        Assert.Equal(meadows, image[1, 0]);
        Assert.Equal(meadows, image[4, 4]);
        foreach (var (x, y) in new[] { (8, 5), (7, 5), (9, 5), (8, 4) }) Assert.Equal(Black, image[x, y]);
        Assert.Equal(meadows, image[9, 4]);   // diagonal neighbour: 1 + 1 > 1
        for (int x = 0; x < 16; x++) Assert.Equal(x == 8 ? Black : Red, image[x, 6]);
    }

    [Fact]
    public void PngDecodesWithAnIndependentReader()
    {
        var image = Renderer().Polyline(new[] { (-6f, -4f), (6f, 4f) }, Red).Render(Ground);
        byte[] png = image.ToPng();
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var chunks = new List<(string type, byte[] data)>();
        for (int at = 8; at < png.Length;)
        {
            int length = BigEndian(png, at);
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            byte[] data = png[(at + 8)..(at + 8 + length)];
            Assert.Equal((uint)BigEndian(png, at + 8 + length), BitwiseCrc(png[(at + 4)..(at + 8 + length)]));
            chunks.Add((type, data));
            at += 12 + length;
        }
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, chunks.Select(c => c.type));
        Assert.Equal(new byte[] { 0, 0, 0, 16, 0, 0, 0, 12, 8, 2, 0, 0, 0 }, chunks[0].data);
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(chunks[1].data), CompressionMode.Decompress)) zlib.CopyTo(inflated);
        byte[] rows = inflated.ToArray();
        Assert.Equal(12 * (1 + 16 * 3), rows.Length);
        for (int y = 0; y < 12; y++)
        {
            Assert.Equal(0, rows[y * 49]); // filter type None
            for (int x = 0; x < 16; x++)
                Assert.Equal(image[x, y], new RenderColor(rows[y * 49 + 1 + x * 3], rows[y * 49 + 2 + x * 3], rows[y * 49 + 3 + x * 3]));
        }
    }

    [Fact]
    public void LargeImagesSpanSeveralStoredBlocks()
    {
        // 200x120 pixels is 72,120 bytes of rows: two stored blocks.
        var image = new TerrainRenderer(new TerrainArea(-100, -60, 100, 60), 1).Render(new PlaneTerrain(40, .1f));
        byte[] png = image.ToPng();
        int idat = BigEndian(png, 33);
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(png[41..(41 + idat)]), CompressionMode.Decompress)) zlib.CopyTo(inflated);
        Assert.Equal(120 * (1 + 200 * 3), inflated.Length);
        Assert.Equal(image[199, 119], new RenderColor(inflated.ToArray()[^3], inflated.ToArray()[^2], inflated.ToArray()[^1]));
    }

    [Fact]
    public void RefusesWhatItCannotDrawHonestly()
    {
        Assert.Throws<ArgumentException>(() => new TerrainRenderer(new TerrainArea(0, 0, 10, 10), 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainRenderer(new TerrainArea(0, 0, 10, 10), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainRenderer(new TerrainArea(0, 0, 10000, 10), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Renderer().Point(0, 0, Black, -1));
        Assert.Throws<ArgumentException>(() => Renderer().Polyline(Array.Empty<(float, float)>(), Red));

        var grid = new GridDumpTerrain("grid", 0, 0, 4, 2, 2, new float[] { 30, 40, 50, 60 });
        var inside = new TerrainRenderer(new TerrainArea(0, 0, 4, 4), 1).Render(grid);
        // Pixel (0,3) samples (0.5, 0.5): 30 + 10/8 + 20/8 = 33.75 m, t = 3.75/70 of the land range.
        // Green to brown at 2t: 64 + 96 * 0.1071 = 74.29, 120 + 20 * 0.1071 = 122.14, 48 + 48 * 0.1071 = 53.14.
        Assert.Equal(new RenderColor(74, 122, 53), inside[0, 3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainRenderer(new TerrainArea(0, 0, 8, 4), 1).Render(grid)); // nothing painted past the grid
        Assert.Throws<NotSupportedException>(() => new TerrainRenderer(new TerrainArea(0, 0, 4, 4), 1) { Coloring = TerrainColoring.Biome }.Render(grid));

        var inverted = Renderer(); inverted.HighHeight = -1;
        Assert.Throws<InvalidOperationException>(() => inverted.Render(Ground));
        var drowned = Renderer(); drowned.WaterLevel = 200;
        Assert.Throws<InvalidOperationException>(() => drowned.Render(Ground));
    }

    private static int BigEndian(byte[] data, int at) => (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];

    private static uint BitwiseCrc(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
