using System.Security.Cryptography;
using System.Text;
using Valheim.Testing;
using Xunit;

public class WorldDumpContractTests
{
    // Four adjacent native ValheimCLI samples from a disposable 1.0.16 world. No save, account or full dump is included.
    // The four-sample reply below is constructed for this excerpt; a separate bounded run verified its full native reply.
    private const string NativeWindow =
        "x,z,height,biome,river,river_width,base_height\n" +
        "-955,-1085,22.9,Meadows,0.80,90.9,0.06027\n" +
        "-950,-1085,24.1,Meadows,0.84,90.9,0.06609\n" +
        "-955,-1080,22.2,Meadows,0.77,91.1,0.06090\n" +
        "-950,-1080,23.2,Meadows,0.82,91.1,0.06655\n";

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static WorldDumpManifest Manifest(string content = NativeWindow) => new()
    {
        WorldUid = "fixture-uid", Seed = "fixture-seed", GameBuild = "1.0.16",
        Command = "cli_world_dump 5 output --window -952.5,-1082.5,2.5",
        Reply = "OK: WORLD_DUMP samples=4 step=5 window=-953,-1083,3 extent=-955,-1085..-950,-1080 ms=1 world=output/world.csv",
        Sha256 = Hash(content), Step = 5, MinX = -955, MinZ = -1085, MaxX = -950, MaxZ = -1080
    };

    private static void WithFile(string content, Action<string> assertion)
    {
        string path = Path.Combine(Path.GetTempPath(), $"native-dump-{Guid.NewGuid():N}.csv");
        try { File.WriteAllText(path, content); assertion(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeWindowPassesAndKeepsTheThreeDifferentHeightMeaningsSeparate()
    {
        WithFile(NativeWindow, path =>
        {
            var grid = WorldDumpContract.Verify(path, Manifest());
            Assert.Equal(4, grid.CountX * grid.CountZ);
            Assert.Equal(22.9f, grid.GetHeight(-955, -1085));
            Assert.Equal(.06027f, grid.GetBaseHeight(-955, -1085));
            Assert.Equal(TerrainBiome.Meadows, grid.GetBiome(-955, -1085));
            grid.GetRiverWeight(-955, -1085, out float weight, out float width);
            Assert.Equal((.80f, 90.9f), (weight, width));
        });
    }

    [Fact]
    public void DamagedOrTruncatedFileIsNotSilentlyAccepted()
    {
        WithFile(NativeWindow.Replace("22.9", "22.8"), path =>
            Assert.Contains("SHA-256", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, Manifest())).Message));
        string truncated = NativeWindow.Replace("-950,-1080,23.2,Meadows,0.82,91.1,0.06655\n", "");
        WithFile(truncated, path =>
            Assert.Contains("ragged", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, Manifest(truncated))).Message));
    }

    [Fact]
    public void MissingNativeFieldIsAnExplicitCapabilityFailure()
    {
        string noBase = NativeWindow.Replace(",base_height", "")
            .Replace(",0.06027", "").Replace(",0.06609", "")
            .Replace(",0.06090", "").Replace(",0.06655", "");
        WithFile(noBase, path =>
            Assert.Contains("base_height", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, Manifest(noBase))).Message));
    }

    [Fact]
    public void CommandReplyBoundsAndLatticeAreIndependentlyChecked()
    {
        WithFile(NativeWindow, path =>
        {
            var m = Manifest(); m.Command = "cli_world_dump 8 output --window 0,0,5";
            Assert.Contains("step", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, m)).Message);
            m = Manifest(); m.Reply = m.Reply.Replace("samples=4", "samples=5");
            Assert.Contains("samples", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, m)).Message);
            m = Manifest(); m.Reply = m.Reply.Replace("extent=-955", "extent=-960");
            Assert.Contains("extent", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, m)).Message);
            m = Manifest(); m.Reply = m.Reply.Replace(" world=output/world.csv", "");
            Assert.Contains("world", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, m)).Message);
            m = Manifest(); m.Reply = m.Reply.Replace(" extent=-955,-1085..-950,-1080", "");
            Assert.Contains("extent", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, m)).Message);
            m = Manifest(); m.MinX = -950;
            Assert.Contains("bounds", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, m)).Message);
            m = Manifest(); m.Step = 6; m.Command = "cli_world_dump 6 output --window 0,0,5";
            m.Reply = m.Reply.Replace("step=5", "step=6");
            Assert.Contains("spacing", Assert.Throws<InvalidDataException>(() => WorldDumpContract.Verify(path, m)).Message);
        });
    }

    [Fact]
    public void MixedWorldOrBuildPairsAreRefused()
    {
        var first = Manifest(); var second = Manifest();
        WorldDumpContract.RequireSameWorld(first, second);
        second.WorldUid = "another";
        Assert.Contains("world", Assert.Throws<InvalidDataException>(() => WorldDumpContract.RequireSameWorld(first, second)).Message);
        second = Manifest(); second.GameBuild = "different";
        Assert.Contains("build", Assert.Throws<InvalidDataException>(() => WorldDumpContract.RequireSameWorld(first, second)).Message);
    }
}
