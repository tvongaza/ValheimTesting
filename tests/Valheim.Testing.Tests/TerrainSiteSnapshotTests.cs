using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed class TerrainSiteSnapshotTests
{
    private static readonly TerrainSitePoint Point = new(16, -8);
    private static ScriptedTransport Transport(string world = "7", bool server = false, Func<string>? worldSupplier = null) => new ScriptedTransport()
        .Extension("valheim.session", "state", _ => new
        {
            source = "session-state", complete = true, phase = "world-present", worldUid = worldSupplier?.Invoke() ?? world, worldPresent = true,
            worldReady = true, server, dedicated = server, localPlayer = !server, playerReady = !server,
            saving = false, loadError = false, connectionStatus = "Connected"
        })
        .On("cli_area_ready 16 -8 0", _ => ScriptedTransport.Ok("OK: AREA_READY 16.0,-8.0 ready=True zone=0,0 loaded=True objects=2 without_instance=0"))
        .On("cli_ground_height 16 -8", _ => ScriptedTransport.Ok("GROUND 16.0,-8.0 h=42.125"))
        .On("cli_surface_at 16 -8", _ => ScriptedTransport.Ok(
            "SURFACE 16.0,-8.0 hit=1 name=rock y=43.000 layer=Default zdo=local trigger=False",
            "SURFACE 16.0,-8.0 hit=2 name=terrain y=42.125 layer=terrain zdo=local trigger=False",
            "OK: SURFACE_AT 16.0,-8.0 hits=2"))
        .On("cli_paint_at 16 -8", _ => ScriptedTransport.Ok("PAINT 16.0,-8.0 dirt=0.250 cultivated=0.000 paved=0.500 clearveg=1.000 -> a blend"));

    [Fact] public void CaptureUsesClientForLoadedLayersAndVerifiesBothActors()
    {
        var clientTransport = Transport(); var serverTransport = Transport(server: true);
        using var client = clientTransport.Actor("client", "cli_expect --strict worlduid=7");
        using var server = serverTransport.Actor("server", "cli_expect --strict worlduid=7");
        var snapshot = TerrainSiteSnapshot.Capture(client, "cave entrance", "7", [Point], TimeSpan.FromSeconds(1), server);
        Assert.Equal(42.125, snapshot.Readings[0].GroundHeight);
        Assert.Equal(.5, snapshot.Readings[0].Paved);
        Assert.Equal(42.125, snapshot.Readings[0].TerrainSurfaceHeight);
        Assert.Equal(5, snapshot.Commands.Count);
        Assert.Single(snapshot.Commands.Where(x => x.Role == "server"));
        Assert.DoesNotContain(serverTransport.Commands, x => x.StartsWith("cli_paint_at") || x.StartsWith("cli_surface_at") || x.StartsWith("cli_ground_height"));
        Assert.All(snapshot.Commands, x => Assert.NotEmpty(x.Reply));
        Assert.All(clientTransport.Commands.Where(x => x.StartsWith("cli_", StringComparison.Ordinal) && x != "cli_extensions" && !x.StartsWith("cli_expect")),
            _ => Assert.True(clientTransport.Count("cli_expect") > 1));
    }

    [Fact] public void WrongWorldRefusesBeforeAnyTerrainCommand()
    {
        var transport = Transport("8"); using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain(transport.Commands, x => x.StartsWith("cli_area_ready"));
    }

    [Fact] public void WorldChangingDuringCaptureRefusesStaleEvidence()
    {
        int reads = 0;
        var transport = Transport(worldSupplier: () => ++reads == 1 ? "7" : "8");
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1)));
        Assert.Equal(1, transport.Count("cli_ground_height"));
    }

    [Fact] public void UnpinnedActorCannotCapture()
    {
        var transport = Transport(); using var actor = transport.Actor();
        actor.VerifyEnvironment(EnvironmentPinning.None);
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1)));
        Assert.Equal(0, transport.Count("cli_area_ready"));
    }

    [Theory]
    [InlineData("ground", "GROUND 16.0,-8.0 none (no terrain loaded there)")]
    [InlineData("ground", "GROUND 17.0,-8.0 h=42.125")]
    [InlineData("paint", "PAINT 16.0,-8.0 none (no heightmap loaded there)")]
    [InlineData("surface", "OK: SURFACE_AT 16.0,-8.0 hits=2")]
    public void IncompleteOrWrongCoordinateRepliesRefuse(string command, string line)
    {
        var transport = Transport();
        string actual = command switch { "ground" => "cli_ground_height 16 -8", "paint" => "cli_paint_at 16 -8", _ => "cli_surface_at 16 -8" };
        transport.On(actual, _ => ScriptedTransport.Ok(line));
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1)));
    }

    [Fact] public void ReadinessTimeoutIncludesLastReplyAndDoesNotSampleTerrain()
    {
        var transport = Transport().On("cli_area_ready 16 -8 0", _ => ScriptedTransport.Ok(
            "OK: AREA_READY 16.0,-8.0 ready=False zone=0,0 loaded=False objects=0 without_instance=0"));
        using var actor = transport.Actor();
        var error = Assert.Throws<WaitTimeoutException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromMilliseconds(1)));
        Assert.Contains("ready=False", error.Message);
        Assert.Equal(0, transport.Count("cli_ground_height"));
    }

    [Fact] public void ReportLinksExactRepliesAndComparisonIgnoresTimestamps()
    {
        var transport = Transport(); using var actor = transport.Actor();
        var before = TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1));
        var after = before with { StartedUtc = before.StartedUtc.AddDays(1), FinishedUtc = before.FinishedUtc.AddDays(1),
            Readings = [before.Readings[0] with { GroundHeight = 43.125 }] };
        var delta = Assert.Single(TerrainSiteSnapshot.Compare(before, after));
        Assert.Equal(1, delta.GroundHeight);
        Assert.Equal(0, delta.Paved);
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Compare(before, after with { WorldUid = "8" }));
        string directory = Path.Combine(Path.GetTempPath(), "terrain-site-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = new ScenarioReport("site test"); report.Step("read terrain", () => { }); report.AttachTerrainSnapshot(before);
            report.Write(directory);
            var link = Assert.Single(report.TerrainSnapshots);
            Assert.Equal("site", link.Site);
            using var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, link.File)));
            Assert.Equal("cli_ground_height 16 -8", snapshot.RootElement.GetProperty("Commands")[1].GetProperty("Command").GetString());
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "result.json")));
            Assert.Equal(link.Sha256, result.RootElement.GetProperty("TerrainSnapshots")[0].GetProperty("Sha256").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void FailureCaptureKeepsTheAssertionFailure()
    {
        var transport = Transport(); using var actor = transport.Actor();
        var report = new ScenarioReport("failure capture");
        var error = Assert.Throws<InvalidOperationException>(() => report.StepWithTerrainOnFailure("check ground",
            () => throw new InvalidOperationException("unexpected ground"),
            () => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1))));
        Assert.Equal("unexpected ground", error.Message);
        string directory = Path.Combine(Path.GetTempPath(), "terrain-site-" + Guid.NewGuid().ToString("N"));
        try { report.Write(directory); Assert.Single(report.TerrainSnapshots); }
        finally { Directory.Delete(directory, true); }
    }
}
