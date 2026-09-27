using System.Text.Json;
using Valheim.Testing;
using Valheim.Testing.Game;
using valheim_cli.Testing;
using Xunit;

public class ToolkitTests
{
    [Fact] public void ReplayRefusesUncapturedSamplesAndWrongLayer()
    {
        var terrain = new ReplayTerrain("fixture-sha256", "generator", [new(1, 2, 40, TerrainBiome.Meadows)]);
        Assert.Equal(40, terrain.GetHeight(1, 2)); Assert.Throws<InvalidOperationException>(() => terrain.GetHeight(2, 1));
        Assert.Throws<InvalidOperationException>(() => new Observation("generator", true, default).RequireComplete("client-ground"));
    }
    [Fact] public void HeightOnlyCaptureCannotInventBiomeOrRiver()
    {
        var replay = new ReplayTerrain("fixture", "generator", [new(0, 0, 42, TerrainBiome.Unknown)]);
        Assert.Equal(42, replay.GetHeight(0, 0));
        Assert.Throws<NotSupportedException>(() => replay.GetBiome(0, 0));
        Assert.Throws<NotSupportedException>(() => replay.GetRiverWeight(0, 0, out _, out _));
    }
    [Fact] public void ReplayRejectsDuplicatesAndNonfiniteSamples()
    {
        Assert.Throws<ArgumentException>(() => new ReplayTerrain("f", "g", [new(0, 0, 1, TerrainBiome.Meadows), new(0, 0, 2, TerrainBiome.Meadows)]));
        Assert.Throws<ArgumentException>(() => new ReplayTerrain("f", "g", [new(0, 0, float.NaN, TerrainBiome.Meadows)]));
    }
    [Fact] public void PlaneUsesXZNotXY()
    {
        ITerrain terrain = new PlaneTerrain(40, .25f, -.5f); Check.Near(terrain.GetHeight(8, 10), 37, .0001);
    }
    [Fact] public void SyntheticRiverHasDeclaredChannelAndWidth()
    {
        var terrain = new SyntheticTerrain(); terrain.GetRiverWeight(100, 0, out float weight, out float width);
        Assert.Equal(1, weight); Assert.Equal(48, width); Assert.Equal(26, terrain.GetHeight(100, 0));
        terrain.HasRiver = false; terrain.GetRiverWeight(100, 0, out weight, out width); Assert.Equal(0, weight); Assert.Equal(0, width);
    }
    [Fact] public void IdentityCheckDoesNotHideDuplicates()
    {
        Assert.Throws<InvalidOperationException>(() => Check.SameIdentities(["a", "a"], ["a", "b"]));
        Check.SameIdentities(["b", "a"], ["a", "b"]);
    }
    [Theory] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void NonfiniteIsNotNear(double value) => Assert.Throws<InvalidOperationException>(() => Check.Near(value, 1, 1));
    [Fact] public void IncompleteCannotMeanEmpty() => Assert.Throws<InvalidOperationException>(() => new Observation("zdo-store", false, default).RequireComplete("zdo-store"));
    [Fact] public async Task EventualWaitObservesUntilMatch()
    {
        int reads = 0; Assert.Equal(3, await Check.Eventually(() => ++reads, n => n == 3, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1)));
    }
    [Fact] public async Task EventualWaitTimesOut()
    { await Assert.ThrowsAsync<TimeoutException>(() => Check.Eventually(() => false, x => x, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(2))); }
    [Fact] public void FixtureHashMismatchCannotCreateACopy()
    {
        using var dirs = new Directories(); File.WriteAllText(Path.Combine(dirs.Source, "world.db"), "original");
        Assert.Throws<InvalidOperationException>(() => WorldFixture.Copy(dirs.Source, dirs.Output, new Dictionary<string, string> { ["world.db"] = "wrong" }));
        Assert.Empty(Directory.GetDirectories(dirs.Output));
    }
    [Fact] public void FixtureOwnsOnlyItsCopyAndCanPreserveFailure()
    {
        using var dirs = new Directories(); string file = Path.Combine(dirs.Source, "world.db"); File.WriteAllText(file, "original");
        var hashes = new Dictionary<string, string> { ["world.db"] = WorldFixture.Hash(file) };
        var fixture = WorldFixture.Copy(dirs.Source, dirs.Output, hashes); string copy = fixture.DirectoryPath;
        File.WriteAllText(Path.Combine(copy, "world.db"), "edited"); fixture.Dispose(); Assert.False(Directory.Exists(copy)); Assert.Equal("original", File.ReadAllText(file));
        using var kept = WorldFixture.Copy(dirs.Source, dirs.Output, hashes); kept.Preserve = true; kept.Dispose(); Assert.True(Directory.Exists(kept.DirectoryPath));
    }
    [Fact] public void RuntimeCopyPreservesEmptyDirectories()
    {
        using var dirs = new Directories();
        Directory.CreateDirectory(Path.Combine(dirs.Source, "BepInEx", "scripts"));
        string file = Path.Combine(dirs.Source, "server.exe"); File.WriteAllText(file, "fixture");
        using var copy = WorldFixture.Copy(dirs.Source, dirs.Output, new Dictionary<string, string> { ["server.exe"] = WorldFixture.Hash(file) });
        Assert.True(Directory.Exists(Path.Combine(copy.DirectoryPath, "BepInEx", "scripts")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(copy.DirectoryPath, "BepInEx", "scripts")));
    }
    [Fact] public void UnpinnedActorCannotIssueCommands()
    {
        using var actor = new GameActor("server", new Fake()); Assert.Throws<InvalidOperationException>(() => actor.Execute("road_generate"));
    }
    [Fact] public void FailedPinClearsPreviousVerification()
    {
        var fake = new Fake(); using var actor = new GameActor("server", fake); actor.VerifyEnvironment("cli_expect plugin=sha");
        fake.Ok = false; Assert.Throws<InvalidOperationException>(() => actor.VerifyEnvironment("cli_expect plugin=other"));
        Assert.Throws<InvalidOperationException>(() => actor.Execute("road_generate"));
    }
    [Fact] public void DuplicateStructuredRepliesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => GameActor.ParseLine(new CommandResult { Output = ["EXTENSION_RESULT {}", "EXTENSION_RESULT {}"] }, "EXTENSION_RESULT "));
    }
    [Fact] public void ReloadedInstanceRequiresExplicitRediscovery()
    {
        var fake = new Fake { Output = ["EXTENSION_RESULT {\"schemaVersion\":1,\"ok\":true,\"instance\":\"new\",\"extension\":\"roads\",\"data\":{}}"] };
        using var actor = new GameActor("server", fake); actor.VerifyEnvironment("cli_expect plugin=sha");
        Assert.Throws<InvalidOperationException>(() => actor.Invoke(new("roads/query", "old", true, 1)));
    }
    [Fact] public void MutatingCapabilityCannotBePolled()
    {
        using var actor = new GameActor("server", new Fake()); Assert.Throws<InvalidOperationException>(() => actor.Observe(new("roads/change", "a", false, 1)));
    }
    [Fact] public void ReportIncludesTeardownFailure()
    {
        using var dirs = new Directories(); var report = new ScenarioReport("scenario"); report.Step("action", () => {});
        Assert.Throws<IOException>(() => report.Step("cleanup", () => throw new IOException("failed"))); report.Write(dirs.Output);
        Assert.False(report.Passed); Assert.Contains("failures=\"1\"", File.ReadAllText(Path.Combine(dirs.Output, "junit.xml")));
    }
    private sealed class Fake : IGameTransport
    {
        public bool Ok = true; public List<string> Output = [];
        public CommandResult Execute(string command, TimeSpan timeout) => new() { Ok = Ok, Output = command.StartsWith("cli_expect ") ? ["OK: EXPECT"] : Output, ErrorCode = "test_error" };
        public void Dispose() { }
    }
    private sealed class Directories : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "toolkit-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source"); public string Output => Path.Combine(Root, "output");
        public Directories() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Output); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
