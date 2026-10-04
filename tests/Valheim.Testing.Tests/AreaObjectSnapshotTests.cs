using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using System.Text.Json;
using Xunit;

public sealed class AreaObjectSnapshotTests
{
    private static ScriptedTransport Transport(bool server, Func<string>? world = null) => new ScriptedTransport()
        .Extension("valheim.session", "state", _ => new
        {
            source = "session-state", complete = true, phase = "world-present", worldUid = world?.Invoke() ?? "7", worldPresent = true,
            worldReady = true, server, dedicated = server, localPlayer = !server, playerReady = !server,
            saving = false, loadError = false, connectionStatus = "Connected"
        })
        .On("cli_area_ready 16 -8 0", _ => ScriptedTransport.Ok("OK: AREA_READY 16.0,-8.0 ready=True zone=0,0 loaded=True objects=2 without_instance=0"))
        .On("cli_zdos_at 16 -8 8", _ => ScriptedTransport.Ok(
            "ZDO chest_wood id=1:2 pos=16.000,42.000,-8.000 rot=0.00,0.00,0.00 quat=0,0,0,1 scale=- persistent=True owner=0",
            "OK: ZDOS_AT 16.0,-8.0 r=8.0 zones=1 objects=1"))
        .On("cli_containers_at 16 -8 8", _ => ScriptedTransport.Ok(
            "CONTAINER chest_wood pos=16.000,42.000,-8.000 defaultItemsRolled=True bytes=4 id=1:2",
            "  ITEM Wood x1 quality=1 variant=0 durability=100.0 slot=0,0",
            "OK: CONTAINERS_AT 16.0,-8.0 r=8.0 containers=1 unreadable=0"))
        .On("cli_ground_height 16 -8", _ => ScriptedTransport.Ok("GROUND 16.0,-8.0 h=42.125"))
        .On("cli_prefabs_at 16 42.125 -8 8", _ => ScriptedTransport.Ok(
            "PREFAB name=chest_wood distance=0.1 pos=16.000,42.000,-8.000 zdo=1:2 owner=0",
            "OK: NEARBY_PREFABS radius=8.0 count=1"))
        .On("cli_piece_support 16 -8 8", _ => ScriptedTransport.Ok(
            "SUPPORT wood_pole2 zdo=1:3 pos=16.000,42.000,-8.000 support=100.00 max=100.00 min=10.00 held=yes state=computed health=100.0",
            "OK: PIECE_SUPPORT 16.0,-8.0 r=8.0 pieces=1 held=1 unheld=0 pending=0"));

    [Fact] public void CapturesSavedAndLoadedLayersOnCorrectRoles()
    {
        var st = Transport(true); var ct = Transport(false);
        using var server = st.Actor("server", "cli_expect --strict worlduid=7");
        using var client = ct.Actor("client", "cli_expect --strict worlduid=7");
        var snapshot = AreaObjectSnapshot.Capture(server, client, "test chest", "7", 16, -8, 8, TimeSpan.FromSeconds(1), true, true);
        Assert.Equal(1, snapshot.SavedObjects);
        Assert.Equal(1, snapshot.Containers);
        Assert.Equal(1, snapshot.LoadedPrefabs);
        Assert.Equal(1, snapshot.Pieces);
        Assert.Equal(6, snapshot.Commands.Count);
        Assert.Equal(2, snapshot.Commands.Count(c => c.Role == "server"));
        Assert.Contains(snapshot.Commands, c => c.Reply.Any(l => l.StartsWith("  ITEM Wood", StringComparison.Ordinal)));
        Assert.DoesNotContain(st.Commands, c => c.StartsWith("cli_prefabs_at", StringComparison.Ordinal));
        Assert.DoesNotContain(ct.Commands, c => c.StartsWith("cli_zdos_at", StringComparison.Ordinal));
        string directory = Path.Combine(Path.GetTempPath(), "area-object-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = new ScenarioReport("object evidence");
            report.Step("capture site", () => { });
            report.AttachAreaObjectSnapshot(snapshot);
            report.Write(directory);
            var link = Assert.Single(report.AreaObjectSnapshots);
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "result.json")));
            Assert.Equal(link.Sha256, result.RootElement.GetProperty("AreaObjectSnapshots")[0].GetProperty("Sha256").GetString());
            using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, link.File)));
            Assert.Equal("cli_zdos_at 16 -8 8", capture.RootElement.GetProperty("Commands")[1].GetProperty("Command").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact] public void OptionalSectionsAreNotQueried()
    {
        var st = Transport(true); var ct = Transport(false);
        using var server = st.Actor(); using var client = ct.Actor();
        var snapshot = AreaObjectSnapshot.Capture(server, client, "site", "7", 16, -8, 8, TimeSpan.FromSeconds(1));
        Assert.Equal(-1, snapshot.Containers);
        Assert.Equal(-1, snapshot.Pieces);
        Assert.DoesNotContain(st.Commands, c => c.StartsWith("cli_containers_at", StringComparison.Ordinal));
        Assert.DoesNotContain(ct.Commands, c => c.StartsWith("cli_piece_support", StringComparison.Ordinal));
    }

    [Fact] public void WrongWorldOrWrongRoleRefusesBeforeObjectCommands()
    {
        var st = Transport(true); var ct = Transport(false, () => "8");
        using var server = st.Actor(); using var client = ct.Actor();
        Assert.Throws<InvalidOperationException>(() => AreaObjectSnapshot.Capture(server, client, "site", "7", 16, -8, 8, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, st.Count("cli_zdos_at"));
        using var wrongServer = Transport(false).Actor();
        Assert.Throws<InvalidOperationException>(() => AreaObjectSnapshot.Capture(wrongServer, server, "site", "7", 16, -8, 8, TimeSpan.FromSeconds(1)));
    }

    [Fact] public void StaleWorldAfterObservationRefusesCapture()
    {
        int checks = 0;
        var st = Transport(true, () => ++checks == 1 ? "7" : "8"); var ct = Transport(false);
        using var server = st.Actor(); using var client = ct.Actor();
        Assert.Throws<InvalidOperationException>(() => AreaObjectSnapshot.Capture(server, client, "site", "7", 16, -8, 8, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, st.Count("cli_zdos_at"));
    }

    [Theory]
    [InlineData("cli_zdos_at 16 -8 8", "OK: ZDOS_AT 16.0,-8.0 r=8.0 zones=1 objects=2")]
    [InlineData("cli_zdos_at 16 -8 8", "OK: ZDOS_AT 17.0,-8.0 r=8.0 zones=1 objects=1")]
    [InlineData("cli_prefabs_at 16 42.125 -8 8", "OK: NEARBY_PREFABS radius=8.0 count=2")]
    [InlineData("cli_piece_support 16 -8 8", "OK: PIECE_SUPPORT 16.0,-8.0 r=8.0 pieces=1 held=0 unheld=0 pending=0")]
    public void IncompleteOrContradictoryReplyFails(string command, string badEnd)
    {
        var st = Transport(true); var ct = Transport(false);
        (command.StartsWith("cli_zdos_at", StringComparison.Ordinal) ? st : ct).On(command, _ => ScriptedTransport.Ok(badEnd));
        using var server = st.Actor(); using var client = ct.Actor();
        Assert.Throws<InvalidOperationException>(() => AreaObjectSnapshot.Capture(server, client, "site", "7", 16, -8, 8, TimeSpan.FromSeconds(1), includeSupport: true));
    }

    [Fact] public void UnreadableContainerCannotLookLikeEmptyContent()
    {
        var st = Transport(true).On("cli_containers_at 16 -8 8", _ => ScriptedTransport.Ok(
            "CONTAINER chest_wood pos=16.000,42.000,-8.000 defaultItemsRolled=True bytes=4 id=1:2",
            "ERROR: inventory incomplete: saved=2 decoded=1 container=1:2",
            "ERROR: CONTAINERS_AT 16.0,-8.0 r=8.0 containers=1 unreadable=1"));
        var ct = Transport(false); using var server = st.Actor(); using var client = ct.Actor();
        Assert.Throws<InvalidOperationException>(() => AreaObjectSnapshot.Capture(server, client, "site", "7", 16, -8, 8, TimeSpan.FromSeconds(1), includeContainers: true));
    }

    [Fact] public void AreaMustLoadBeforeAnyObjectCensus()
    {
        var st = Transport(true);
        var ct = Transport(false).On("cli_area_ready 16 -8 0", _ => ScriptedTransport.Ok(
            "OK: AREA_READY 16.0,-8.0 ready=False zone=0,0 loaded=False objects=1 without_instance=1"));
        using var server = st.Actor(); using var client = ct.Actor();
        var error = Assert.Throws<WaitTimeoutException>(() => AreaObjectSnapshot.Capture(server, client, "site", "7", 16, -8, 8, TimeSpan.FromMilliseconds(1)));
        Assert.Contains("ready=False", error.Message);
        Assert.Equal(0, st.Count("cli_zdos_at"));
    }

    [Fact] public void UnpinnedActorCannotCaptureEvenWhenRepliesLookCorrect()
    {
        var st = Transport(true); var ct = Transport(false);
        using var server = st.Actor(); using var client = ct.Actor();
        client.VerifyEnvironment(EnvironmentPinning.None);
        Assert.Throws<InvalidOperationException>(() => AreaObjectSnapshot.Capture(server, client, "site", "7", 16, -8, 8, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, st.Count("cli_zdos_at"));
    }
}
