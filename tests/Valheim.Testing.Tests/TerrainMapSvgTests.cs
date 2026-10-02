using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Xml.Linq;
using Valheim.Testing;
using Xunit;

public sealed class TerrainMapSvgTests
{
    private const string Header = "x,z,height,biome,river,river_width,base_height\n";
    private const string Coarse = Header +
        "-16,-16,10,Ocean,0,0,0.1\n" +
        "112,-16,20,Meadows,0,0,0.2\n" +
        "-16,112,30,Meadows,0,0,0.3\n" +
        "112,112,50,Mountain,0,0,0.4\n";
    private const string Fine = Header +
        "-16,-16,34,Meadows,0,0,0.1\n" +
        "-8,-16,35,Meadows,0,0,0.1\n" +
        "-16,-8,36,Meadows,0,0,0.1\n" +
        "-8,-8,37,Meadows,0,0,0.1\n";

    private sealed class Files : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("terrain-map-").FullName;
        public WorldDumpLayerSpec Add(string name, string csv, int step, int min, int max, string uid = "fixture-uid")
        {
            string path = Path.Combine(_root, name + ".csv");
            File.WriteAllText(path, csv);
            return new(name, path, new WorldDumpManifest {
                WorldUid = uid, Seed = "fixture-seed", GameBuild = "1.0.16",
                Command = $"cli_world_dump {step} fixture",
                Reply = $"OK: WORLD_DUMP samples=4 step={step} world=fixture",
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv))).ToLowerInvariant(),
                Step = step, MinX = min, MinZ = min, MaxX = max, MaxZ = max
            });
        }
        public void Dispose() => Directory.Delete(_root, true);
    }

    private static string Render(params WorldDumpLayerSpec[] specs) => TerrainMapSvg.Render(specs, "coarse",
        new TerrainArea(-16, -16, 112, 112), 8, new TerrainArea(-16, -16, -8, -8), 2,
        contourInterval: 5, worldRadius: 80);

    [Fact]
    public void PinnedSlopeHasSeaAndLandContoursDiscMaskAndMarkedFineWindow()
    {
        using var files = new Files();
        var coarse = files.Add("coarse", Coarse, 128, -16, 112);
        var fine = files.Add("fine", Fine, 8, -16, -8);
        string svg = Render(coarse, fine);
        Assert.Equal(svg, Render(fine, coarse)); // input ordering cannot change precedence or output
        Assert.NotNull(XDocument.Parse(svg).Root);
        string caption = string.Join(" ", XDocument.Parse(svg).Descendants(XName.Get("text", "http://www.w3.org/2000/svg")).Select(x => x.Value));
        Assert.Contains("World UID fixture-uid", caption);
        Assert.Contains("Valheim 1.0.16", caption);
        Assert.Contains("zoom fine 8 m", caption);
        Assert.Contains("between-node terrain is approximate", caption);
        Assert.Contains("id=\"overview-disc\"", svg);
        var image = XDocument.Parse(svg).Descendants(XName.Get("image", "http://www.w3.org/2000/svg")).First();
        byte[] png = Convert.FromBase64String(image.Attribute("href")!.Value["data:image/png;base64,".Length..]);
        int length = (png[33] << 24) | (png[34] << 16) | (png[35] << 8) | png[36];
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(png, 41, length), CompressionMode.Decompress)) zlib.CopyTo(inflated);
        byte[] pixels = inflated.ToArray();
        int outside = 15 * (1 + 16 * 3) + 1 + 15 * 3; // top-right is beyond the declared world disc
        Assert.Equal(new byte[] { 226, 226, 226 }, pixels[outside..(outside + 3)]);
        Assert.Contains("stroke=\"#1f3a5f\"", svg); // heavy sea contour
        Assert.Contains("stroke=\"#5a4a30\"", svg); // land contours
        Assert.Contains("stroke-dasharray=\"5,3\"", svg); // fine-window bounds on overview
        Assert.Equal(2, svg.Split("data:image/png;base64,").Length - 1);
    }

    [Fact]
    public void MissingOrForeignFineWindowAndCorruptGridFailBeforeRendering()
    {
        using var files = new Files();
        var coarse = files.Add("coarse", Coarse, 128, -16, 112);
        Assert.Throws<ArgumentException>(() => Render(coarse));
        var foreign = files.Add("foreign", Fine, 8, -16, -8, "other-world");
        Assert.Contains("different world", Assert.Throws<InvalidDataException>(() => Render(coarse, foreign)).Message);
        var fine = files.Add("fine", Fine, 8, -16, -8);
        File.AppendAllText(fine.Path, "damaged");
        Assert.Contains("SHA-256", Assert.Throws<InvalidDataException>(() => Render(coarse, fine)).Message);
    }

    [Fact]
    public void OverviewCannotClaimAWindowOutsideItsBoundsOrWithoutFineCoverage()
    {
        using var files = new Files();
        var coarse = files.Add("coarse", Coarse, 128, -16, 112);
        var fine = files.Add("fine", Fine, 8, -16, -8);
        Assert.Throws<ArgumentException>(() => TerrainMapSvg.Render([coarse, fine], "coarse",
            new TerrainArea(0, 0, 112, 112), 8, new TerrainArea(-16, -16, -8, -8), 2));
        Assert.Contains("finer", Assert.Throws<InvalidOperationException>(() => TerrainMapSvg.Render([coarse, fine], "coarse",
            new TerrainArea(-16, -16, 112, 112), 8, new TerrainArea(64, 64, 72, 72), 2)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainMapSvg.Render([coarse, fine], "coarse",
            new TerrainArea(-16, -16, 112, 112), 8, new TerrainArea(-16, -16, -8, -8), 2, contourInterval: .01f));
        Assert.Contains("four million", Assert.Throws<ArgumentOutOfRangeException>(() => TerrainMapSvg.Render([coarse, fine], "coarse",
            new TerrainArea(-16, -16, 112, 112), .05f, new TerrainArea(-16, -16, -8, -8), 2)).Message);
    }
}
