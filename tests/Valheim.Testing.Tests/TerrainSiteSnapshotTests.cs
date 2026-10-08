using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed class TerrainSiteSnapshotTests
{
    private static readonly TerrainSitePoint Point = new(16, -8);
    // Replies shaped as the World Tools pack's valheim.world capabilities answer them.
    private static object Ground(IReadOnlyList<string> a) => new { source = a[2], complete = true, x = float.Parse(a[0]), z = float.Parse(a[1]), height = 42.125f, units = "metres" };
    private static object Surface(IReadOnlyList<string> a) => new { source = "loaded-terrain-surface", complete = true, x = float.Parse(a[0]), z = float.Parse(a[1]), height = 42.125f, colliderHeight = 42.0f, units = "metres" };
    private static object Paint(IReadOnlyList<string> a) => new { source = "loaded-terrain-paint", complete = true, x = float.Parse(a[0]), z = float.Parse(a[1]), units = "rgba01", r = .25f, g = 0f, b = .5f, a = 1f };
    private static object Area(bool ready = true) => new { source = "zone-presence", complete = true,
        reference = new { x = 0, z = 0 }, simulation = new { near = 2, far = 2, classic = true },
        zones = new[] { new { x = 0, z = 0, terrainLoaded = ready, areaReady = ready, instances = ready ? 2 : 0,
            nearInstances = ready ? 2 : 0, saved = 2, withoutInstance = ready ? 0 : 2 } } };
    private static ScriptedTransport Transport(string world = "7", bool server = false, Func<string>? worldSupplier = null,
        Func<IReadOnlyList<string>, object>? ground = null, Func<IReadOnlyList<string>, object>? surface = null, Func<IReadOnlyList<string>, object>? paint = null,
        bool areaReady = true, Func<object>? area = null) => new ScriptedTransport()
        .Extension("valheim.session", "state", _ => new
        {
            source = "session-state", complete = true, phase = "world-present", worldUid = worldSupplier?.Invoke() ?? world, worldPresent = true,
            worldReady = true, server, dedicated = server, localPlayer = !server, playerReady = !server,
            saving = false, loadError = false, connectionStatus = "Connected"
        })
        .Extension("valheim.world", "terrain", ground ?? Ground)
        .Extension("valheim.world", "terrain-surface", surface ?? Surface)
        .Extension("valheim.world", "terrain-paint", paint ?? Paint)
        .Extension("valheim.observe", "zones", _ => area?.Invoke() ?? Area(areaReady));
    private const string GroundCommand = "cli_extension valheim.world/terrain 16 -8 loaded-ground";

    [Fact] public void CaptureUsesClientForLoadedLayersAndVerifiesBothActors()
    {
        var clientTransport = Transport(); var serverTransport = Transport(server: true, areaReady: false);
        using var client = clientTransport.Actor("client", "cli_expect --strict worlduid=7");
        using var server = serverTransport.Actor("server", "cli_expect --strict worlduid=7");
        var snapshot = TerrainSiteSnapshot.Capture(client, "cave entrance", "7", [Point], TimeSpan.FromSeconds(1), server);
        Assert.Equal(new TerrainSiteReading(Point, 42.125f, 42.0f, .25f, 0f, .5f, 1f), snapshot.Readings[0]);
        Assert.Equal(4, snapshot.Commands.Count);
        Assert.DoesNotContain(snapshot.Commands, x => x.Role == "server");
        Assert.Equal(new[] { "cli_extension valheim.observe/zones 0,0", GroundCommand, "cli_extension valheim.world/terrain-surface 16 -8", "cli_extension valheim.world/terrain-paint 16 -8" },
            snapshot.Commands.Where(x => x.Role == "client").Select(x => x.Command).ToArray());
        Assert.Contains("\"colliderHeight\":42", snapshot.Commands[2].Reply[0]);
        Assert.DoesNotContain(serverTransport.Commands, x => x.StartsWith("cli_extension valheim.world/", StringComparison.Ordinal));
        Assert.All(snapshot.Commands, x => Assert.NotEmpty(x.Reply));
        Assert.All(clientTransport.Commands.Where(x => x.StartsWith("cli_", StringComparison.Ordinal) && x != "cli_extensions" && !x.StartsWith("cli_expect")),
            _ => Assert.True(clientTransport.Count("cli_expect") > 1));
    }

    [Fact] public void WrongWorldRefusesBeforeAnyTerrainCommand()
    {
        var transport = Transport("8"); using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain(transport.Commands, x => x.StartsWith("cli_extension valheim.observe/zones"));
    }

    [Fact] public void WorldChangingDuringCaptureRefusesStaleEvidence()
    {
        int reads = 0;
        var transport = Transport(worldSupplier: () => ++reads == 1 ? "7" : "8");
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1)));
        Assert.Equal(1, transport.Count(GroundCommand));
    }

    [Fact] public void UnpinnedActorCannotCapture()
    {
        var transport = Transport(); using var actor = transport.Actor();
        actor.VerifyEnvironment(EnvironmentPinning.None);
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1)));
        Assert.Equal(0, transport.Count("cli_extension valheim.observe/zones"));
    }

    public static TheoryData<string> BadReplies => new() { "ground incomplete", "ground other point", "ground other layer", "surface incomplete", "surface units",
        "paint incomplete", "paint units", "paint out of range", "paint other point" };
    [Theory, MemberData(nameof(BadReplies))]
    public void IncompleteWrongLayerOrWrongCoordinateRepliesRefuse(string bad)
    {
        Func<IReadOnlyList<string>, object>? ground = null, surface = null, paint = null;
        switch (bad)
        {
            case "ground incomplete": ground = a => new { source = a[2], complete = false, x = 16f, z = -8f, height = (float?)null, units = "metres" }; break;
            case "ground other point": ground = a => new { source = a[2], complete = true, x = 17f, z = -8f, height = 42f, units = "metres" }; break;
            case "ground other layer": ground = a => new { source = "generator", complete = true, x = 16f, z = -8f, height = 42f, units = "metres" }; break;
            case "surface incomplete": surface = _ => new { source = "loaded-terrain-surface", complete = false, x = 16f, z = -8f, units = "metres" }; break;
            case "surface units": surface = _ => new { source = "loaded-terrain-surface", complete = true, x = 16f, z = -8f, height = 42f, colliderHeight = 42f, units = "feet" }; break;
            case "paint incomplete": paint = _ => new { source = "loaded-terrain-paint", complete = false, x = 16f, z = -8f, units = "rgba01" }; break;
            case "paint units": paint = _ => new { source = "loaded-terrain-paint", complete = true, x = 16f, z = -8f, units = "rgb255", r = 0f, g = 0f, b = 0f, a = 0f }; break;
            case "paint out of range": paint = _ => new { source = "loaded-terrain-paint", complete = true, x = 16f, z = -8f, units = "rgba01", r = 2f, g = 0f, b = 0f, a = 0f }; break;
            default: paint = _ => new { source = "loaded-terrain-paint", complete = true, x = 16f, z = -7f, units = "rgba01", r = 0f, g = 0f, b = 0f, a = 0f }; break;
        }
        var transport = Transport(ground: ground, surface: surface, paint: paint);
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1)));
    }

    [Fact] public void ARefusedCapabilityIsRecordedBeforeTheCaptureFails()
    {
        var transport = Transport().On("cli_extension valheim.world/terrain-paint 16 -8", _ => new() { Ok = false,
            Output = ["EXTENSION_RESULT {\"ok\":false,\"code\":\"no_world\",\"message\":\"world unloading\"}"] });
        using var actor = transport.Actor();
        var commands = new List<ObservedCommand>();
        var paint = actor.RequireCapability("valheim.world/terrain-paint");
        Assert.Throws<InvalidOperationException>(() => SiteObservation.Observe(actor, "client", paint, commands, "16", "-8"));
        var recorded = Assert.Single(commands);
        Assert.Equal("cli_extension valheim.world/terrain-paint 16 -8", recorded.Command);
        Assert.StartsWith("EXTENSION_RESULT {", Assert.Single(recorded.Reply)); // The exact reply line, not a message about it.
        Assert.Contains("no_world", recorded.Reply[0]);
    }

    [Fact] public void ReadinessTimeoutIncludesLastReplyAndDoesNotSampleTerrain()
    {
        var transport = Transport(areaReady: false);
        using var actor = transport.Actor();
        var error = Assert.Throws<WaitTimeoutException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromMilliseconds(1)));
        Assert.Contains("not ready", error.Message);
        Assert.Equal(0, transport.Count(GroundCommand));
    }

    [Fact] public void ReadinessFromAnotherZoneCannotApproveTheSite()
    {
        var transport = Transport(area: () => new { source = "zone-presence", complete = true,
            reference = new { x = 0, z = 0 }, simulation = new { near = 2, far = 2, classic = true },
            zones = new[] { new { x = 1, z = 0, terrainLoaded = true, areaReady = true,
                instances = 2, nearInstances = 2, saved = 2, withoutInstance = 0 } } });
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1)));
        Assert.Equal(0, transport.Count(GroundCommand));
    }

    [Fact] public void ReportLinksExactRepliesAndComparisonIgnoresTimestamps()
    {
        var transport = Transport(); using var actor = transport.Actor();
        var before = TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1));
        var after = before with { StartedUtc = before.StartedUtc.AddDays(1), FinishedUtc = before.FinishedUtc.AddDays(1),
            Readings = [before.Readings[0] with { GroundHeight = 43.125f }] };
        var delta = Assert.Single(TerrainSiteSnapshot.Compare(before, after));
        Assert.Equal(1, delta.GroundHeight);
        Assert.Equal(0, delta.B);
        Assert.Throws<InvalidOperationException>(() => TerrainSiteSnapshot.Compare(before, after with { WorldUid = "8" }));
        string directory = Path.Combine(Path.GetTempPath(), "terrain-site-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = new ScenarioReport("site test"); report.Step("read terrain", () => { }); report.Attach(before);
            report.Write(directory);
            var link = Assert.Single(report.Evidence);
            Assert.Equal("site", link.Site);
            Assert.Equal("terrain-site", link.Kind);
            Assert.Equal("evidence/terrain-site-001.json", link.File);
            using var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, link.File)));
            Assert.Equal(GroundCommand, snapshot.RootElement.GetProperty("Commands")[1].GetProperty("Command").GetString());
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "result.json")));
            Assert.Equal(link.Sha256, result.RootElement.GetProperty("Evidence")[0].GetProperty("Sha256").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void FailureCaptureKeepsTheAssertionFailure()
    {
        var transport = Transport(); using var actor = transport.Actor();
        var report = new ScenarioReport("failure capture");
        var error = Assert.Throws<InvalidOperationException>(() => report.StepWithEvidenceOnFailure("check ground",
            () => throw new InvalidOperationException("unexpected ground"),
            () => TerrainSiteSnapshot.Capture(actor, "site", "7", [Point], TimeSpan.FromSeconds(1))));
        Assert.Equal("unexpected ground", error.Message);
        string directory = Path.Combine(Path.GetTempPath(), "terrain-site-" + Guid.NewGuid().ToString("N"));
        try { report.Write(directory); Assert.Equal("terrain-site", Assert.Single(report.Evidence).Kind); }
        finally { Directory.Delete(directory, true); }
    }
}
