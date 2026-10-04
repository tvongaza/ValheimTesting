using Valheim.Testing;
using Valheim.Testing.Game;
using Xunit;

// The checked-in fixtures are real: two bounded dumps a Valheim 1.0.16 dedicated server wrote on 2 October 2026 for a
// disposable world (see WorldDumpCaptureTests for their provenance), with the manifests the capture writes for them.
public class WorldDumpManifestTests
{
    private static WorldDumpManifest Real(string layer) => WorldDump.ReadManifest(PinnedDumpFixture.Real(layer));

    [Fact]
    public void TheRealNativeDumpsPassTheirManifestsAndKeepTheThreeHeightMeaningsApart()
    {
        var coarse = Real("coarse").Verify();
        var fine = Real("fine").Verify();
        Assert.Equal((8, 8, 128f, -400f, 496f), (coarse.CountX, coarse.CountZ, coarse.Spacing, coarse.OriginX, coarse.MaxX));
        Assert.Equal((17, 17, 8f, -64f, 64f), (fine.CountX, fine.CountZ, fine.Spacing, fine.OriginZ, fine.MaxZ));
        Assert.Equal("dumpcontract1-fine", fine.Provenance);
        // The first fine row: x,z = -64,-64; height 56.1 m (metres); base_height 0.29914 (unitless generator value).
        Assert.Equal(56.1f, fine.GetHeight(-64, -64));
        Assert.Equal(.29914f, fine.GetBaseHeight(-64, -64));
        Assert.Equal(TerrainBiome.Meadows, fine.GetBiome(-64, -64));
        Assert.Contains(Nodes(coarse), p => coarse.GetBiome(p.X, p.Z) == TerrainBiome.BlackForest);
        Assert.Contains(Nodes(fine).Concat(Nodes(coarse)), p => { var g = fine.Contains(p.X, p.Z) ? fine : coarse; g.GetRiverWeight(p.X, p.Z, out float w, out _); return w > 0; });
    }

    [Fact]
    public void TheRealLayersComposeNodeForNodeWithTheirOwnGrids()
    {
        var coarseGrid = Real("coarse").Verify();
        var fineGrid = Real("fine").Verify();
        var layered = LayeredDumpTerrain.Load(new[] { Real("fine"), Real("coarse") }, "dumpcontract1-coarse");
        int fineNodes = 0, coarseOnly = 0;
        foreach (var (x, z) in Nodes(fineGrid))
        {
            Assert.Equal(fineGrid.Provenance, layered.LayerAt(x, z).Provenance);
            Assert.Equal(fineGrid.GetHeight(x, z), layered.GetHeight(x, z));
            Assert.Equal(fineGrid.GetBiome(x, z), layered.GetBiome(x, z));
            fineGrid.GetRiverWeight(x, z, out float expected, out _); layered.GetRiverWeight(x, z, out float actual, out _);
            Assert.Equal(expected, actual);
            fineNodes++;
        }
        foreach (var (x, z) in Nodes(coarseGrid).Where(p => !fineGrid.Contains(p.X, p.Z)))
        {
            Assert.Equal("dumpcontract1-coarse", layered.LayerAt(x, z).Provenance);
            Assert.Equal(coarseGrid.GetHeight(x, z), layered.GetHeight(x, z));
            coarseOnly++;
        }
        Assert.Equal((289, 63), (fineNodes, coarseOnly)); // one coarse node, (-16,-16), lies inside the fine window
        // Both dumps sampled the same live world: where their lattices share a node, every native field agrees.
        coarseGrid.GetRiverWeight(-16, -16, out float coarseRiver, out _); fineGrid.GetRiverWeight(-16, -16, out float fineRiver, out _);
        Assert.Equal((68.1f, .31711f, TerrainBiome.Meadows, coarseRiver), (fineGrid.GetHeight(-16, -16), fineGrid.GetBaseHeight(-16, -16), fineGrid.GetBiome(-16, -16), fineRiver));
        Assert.Equal((68.1f, .31711f), (coarseGrid.GetHeight(-16, -16), coarseGrid.GetBaseHeight(-16, -16)));
        Assert.Equal("dumpcontract1-coarse", layered.BaseHeightLayer.Provenance);
        // (0,0) is a fine node but lies between coarse nodes: base height is still the coarse lattice's interpolation.
        Assert.True(fineGrid.IsSampleNode(0, 0));
        Assert.False(layered.BaseHeightLayer.IsSampleNode(0, 0));
        Assert.Equal(coarseGrid.GetBaseHeight(0, 0), layered.GetBaseHeight(0, 0));
    }

    [Fact]
    public void ADamagedTruncatedOrRelabelledRealDumpIsRefused()
    {
        using var copy = new PinnedDumpFixture();
        var manifest = Copy(Real("fine"), copy.Directory);
        byte[] bytes = File.ReadAllBytes(manifest.Path);
        bytes[^5] ^= 1;
        File.WriteAllBytes(manifest.Path, bytes);
        Assert.Contains("SHA-256", Assert.Throws<InvalidDataException>(() => manifest.Verify()).Message);

        manifest = Copy(Real("fine"), copy.Directory);
        var cases = new (Action<WorldDumpManifest> Change, string Reason)[]
        {
            (m => m.MinX = -56, "bounds"), (m => m.Step = 16, "spacing"), (m => m.WorldUid = "", "world UID"),
            (m => m.Reply = "", "recorded command and reply"), (m => m.Name = "", "layer name"), (m => m.Step = 4, "5..1000"),
            (m => m.Sha256 = "abc", "64-digit"), (m => m.Sha256 = null!, "64-digit"),
        };
        foreach (var (change, reason) in cases)
        {
            var m = Copy(manifest, copy.Directory); change(m);
            Assert.Contains(reason, Assert.Throws<InvalidDataException>(() => m.Verify()).Message);
        }
    }

    [Fact]
    public void AByteOrderMarkDoesNotHideTheNativeHeader()
    {
        using var copy = new PinnedDumpFixture();
        var manifest = Copy(Real("fine"), copy.Directory);
        byte[] marked = [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(manifest.Path)];
        File.WriteAllBytes(manifest.Path, marked);
        manifest.Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(marked)).ToLowerInvariant();
        Assert.Equal(56.1f, manifest.Verify().GetHeight(-64, -64));
    }

    [Fact]
    public void TheCoreNoLongerReadsTheReplyGrammarTheCaptureDoes()
    {
        // A reply that disagrees with the file is refused when the dump is captured (WorldDumpCaptureTests); once a
        // capture has written the manifest, the core verifies the file against the manifest's own facts only.
        var manifest = Real("fine");
        manifest.Reply = manifest.Reply.Replace("samples=289", "samples=1");
        manifest.Command = "anything the CLI accepted";
        var grid = manifest.Verify();
        Assert.Equal(289, grid.CountX * grid.CountZ);
    }

    [Fact]
    public void MissingNativeLayersAreAnExplicitFailure()
    {
        using var files = new PinnedDumpFixture();
        string noBase = "x,z,height,biome,river,river_width\n-16,-16,1,Meadows,0,0\n-8,-16,1,Meadows,0,0\n-16,-8,1,Meadows,0,0\n-8,-8,1,Meadows,0,0\n";
        var error = Assert.Throws<InvalidDataException>(() => files.Capture("nobase", noBase, 8));
        Assert.Contains("native header", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(files.Directory)); // the capture left nothing behind
    }

    [Fact]
    public void LayersFromDifferentWorldsAreRefused()
    {
        using var copy = new PinnedDumpFixture();
        var other = Copy(Real("fine"), copy.Directory); other.WorldUid = "1";
        Assert.Contains("different world", Assert.Throws<InvalidDataException>(() => LayeredDumpTerrain.Load(new[] { Real("coarse"), other }, "dumpcontract1-coarse")).Message);
        other = Copy(Real("fine"), copy.Directory); other.GameBuild = "1.0.17";
        Assert.Contains("game builds", Assert.Throws<InvalidDataException>(() => LayeredDumpTerrain.Load(new[] { Real("coarse"), other }, "dumpcontract1-coarse")).Message);
    }

    private static WorldDumpManifest Copy(WorldDumpManifest manifest, string directory)
    {
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".csv");
        File.Copy(manifest.Path, path);
        return new WorldDumpManifest
        {
            Name = manifest.Name, Path = path, WorldUid = manifest.WorldUid, Seed = manifest.Seed, GameBuild = manifest.GameBuild,
            Command = manifest.Command, Reply = manifest.Reply, Sha256 = manifest.Sha256, Step = manifest.Step,
            MinX = manifest.MinX, MinZ = manifest.MinZ, MaxX = manifest.MaxX, MaxZ = manifest.MaxZ,
        };
    }

    private static IEnumerable<(float X, float Z)> Nodes(GridDumpTerrain grid)
    {
        for (int j = 0; j < grid.CountZ; j++)
            for (int i = 0; i < grid.CountX; i++)
                yield return (grid.OriginX + i * grid.Spacing, grid.OriginZ + j * grid.Spacing);
    }
}
