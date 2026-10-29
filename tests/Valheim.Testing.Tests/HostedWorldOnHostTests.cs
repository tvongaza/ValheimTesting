using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// #258 step 8b: a hosted fixture placed in a campaign client's own worlds_local on its host. Never over a world already named for
// it; journalled before anything is shipped; shipped to the run's stage and verified there before it reaches the user's worlds.
public sealed class HostedWorldOnHostTests : IDisposable
{
    private const string Worlds = "/home/t/.config/unity3d/IronGate/Valheim/worlds_local";
    private readonly string _root = Directory.CreateTempSubdirectory("hosted-world-on-host-").FullName;
    private readonly FakeServerHost _host;
    private readonly List<JournalEntry> _journal = [];
    public HostedWorldOnHostTests() => _host = new FakeServerHost("pc", Path.Combine(_root, "pc"));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HostWorldPlan Plan()
    {
        string fixture = Path.Combine(_root, "fixture");
        // A world never saved: its lone _main.0.fwl2.
        Directory.CreateDirectory(Path.Combine(fixture, "Campaign"));
        if (!File.Exists(Path.Combine(fixture, "Campaign", "_main.0.fwl2")))
            File.WriteAllBytes(Path.Combine(fixture, "Campaign", "_main.0.fwl2"), FakeInstalls.Metadata("Campaign", 4242, needsDb: false));
        return new() { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
    }
    private HostedWorldOnHost.Site Site() => new(_host, "pc", Worlds, "/runs/run-1/host-world-stage", "/runs/run-1/host-world",
        (entry, _) => { lock (_journal) _journal.Add(entry); return Task.CompletedTask; }, _ => { _held++; return Task.CompletedTask; });
    private int _held;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServerCollectionKeepsItsOnlyHostCopyWhenFetchOrReceiptFails(bool journalFails)
    {
        string output = Path.Combine(_root, "out");
        var site = Site() with { Stage = "/runs/run-1/world-stage", RetireRunCopies = "run-1",
            Journal = (entry, _) =>
            {
                if (journalFails && entry.Fields.GetValueOrDefault("fetched") == "true") throw new IOException("journal full");
                _journal.Add(entry); return Task.CompletedTask;
            } };
        var placed = site.Place(Plan(), output, default);
        if (!journalFails) _host.AfterFetch = _ => throw new IOException("fetch interrupted");
        Assert.Throws<IOException>(() => placed.Collect());
        Assert.True(File.Exists(_host.Local(site.KeepIn + "/Campaign/_main.0.fwl2")));
        Assert.True(Directory.Exists(_host.Local(site.Stage)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_host.Local(Worlds)));
        Assert.DoesNotContain(_journal, entry => entry.Kind == JournalEntry.CopyRetired && entry.Fields["runtime"] == site.KeepIn);
    }

    [Theory] [InlineData("Campaign")] [InlineData("campaign.fwl")] [InlineData("Campaign_backup_auto-1.db")]
    public void AWorldAlreadyNamedLikeTheFixtureIsNeverOverwrittenAndNothingIsShipped(string existing)
    {
        Directory.CreateDirectory(_host.Local(Worlds));
        string path = _host.Local(Worlds + "/" + existing);
        if (existing == "Campaign") Directory.CreateDirectory(path); else File.WriteAllText(path, "the user's own");
        string output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var error = Assert.Throws<InvalidOperationException>(() => HostedWorldOnHost.Place(Site(), Plan(), output, CancellationToken.None));
        Assert.Contains("never copied over a world", error.Message);
        Assert.Empty(_journal);
        Assert.DoesNotContain(_host.Runs, run => run.Script is "ship" or "world-move");
        Assert.True(Path.Exists(path));
    }

    [Fact] public void ThePlacementIsJournalledBeforeTheShipAndCollectingMovesItIntoTheRunsFolder()
    {
        string output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var placed = HostedWorldOnHost.Place(Site(), Plan(), output, CancellationToken.None);
        Assert.Equal("pc", placed.Host);
        Assert.True(File.Exists(_host.Local(Worlds + "/Campaign/_main.0.fwl2")));
        Assert.False(File.Exists(_host.Local(Worlds + "/SOURCE.txt"))); // the ship's record stays in the stage
        Assert.Equal(new[] { JournalEntry.CopyIntended, JournalEntry.CopyIntended, JournalEntry.CopyDone }, _journal.Select(entry => entry.Kind));
        Assert.Equal(Worlds + "/Campaign", _journal[0].Fields["runtime"]);
        Assert.Equal("4242", _journal[0].Fields["worldUid"]); // what env recover proves the world there by (#807)
        placed.Collect();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_host.Local(Worlds)));
        Assert.True(File.Exists(_host.Local("/runs/run-1/host-world/Campaign/_main.0.fwl2")));
        Assert.True(File.Exists(Path.Combine(placed.CollectedTo!, "Campaign", "_main.0.fwl2")));
        Assert.Equal(JournalEntry.CopyRetired, _journal[^1].Kind);
    }
    [Theory]
    [InlineData("sibling", "Campaign")]
    [InlineData("sibling", "campaign.fwl")]
    [InlineData("account", "CAMPAIGN_backup_auto-1.db")]
    [InlineData("account", "Campaign")]
    public void ACloudWorldRefusesBeforeReservationJournalOrShipEvenWithIdenticalFixtureBytes(string location, string leaf)
    {
        var plan = Plan();
        const string userdata = "/steam/userdata";
        string cloud = location == "sibling" ? HostPath.Join(HostPath.Parent(Worlds), "worlds") : userdata + "/222/892970/remote/worlds";
        Directory.CreateDirectory(_host.Local(userdata + "/111/892970/remote/worlds"));
        string target = _host.Local(cloud + "/" + leaf);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        byte[] bytes = File.ReadAllBytes(Path.Combine(plan.World.Source, "Campaign", "_main.0.fwl2"));
        if (leaf == "Campaign") { Directory.CreateDirectory(target); File.WriteAllBytes(Path.Combine(target, "_main.0.fwl2"), bytes); }
        else File.WriteAllBytes(target, bytes);
        var before = WorldFixture.Manifest(_root);
        var site = Site() with { SteamUserDataDirectory = userdata };
        string output = Path.Combine(_root, "not-created");
        var error = Assert.Throws<InvalidOperationException>(() => site.Place(plan, output, default));
        Assert.Contains(cloud, error.Message); Assert.Contains(plan.World.Source, error.Message);
        Assert.Empty(_journal); Assert.False(Directory.Exists(output)); Assert.False(Directory.Exists(_host.Local(site.KeepIn)));
        Assert.False(Directory.Exists(_host.Local(Worlds)));
        Assert.DoesNotContain(_host.Runs, run => run.Script is "ship" or "world-move" or "world-reserve");
        Assert.Equal(before, WorldFixture.Manifest(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnconfirmedCloudEnumerationNeverAllowsPlacement(bool malformed)
    {
        var plan = Plan();
        _host.Failures["world-collisions"] = new HostResult(HostOutcome.Exited, malformed ? 0 : 5,
            malformed ? "VT-WORLD-COLLISION /cloud/Campaign\n" : "", "cannot enumerate cloud roots", TimeSpan.Zero, false);
        Assert.Throws<HostOperationException>(() => Site().Place(plan, Path.Combine(_root, "not-created"), default));
        Assert.Empty(_journal);
        Assert.DoesNotContain(_host.Runs, run => run.Script is "ship" or "world-reserve" or "world-move");
    }

    [Theory]
    [InlineData("/steam/not-userdata", Worlds)]
    [InlineData("/steam/userdata/../userdata", Worlds)]
    [InlineData("/steam/userdata", "/save/not-worlds-local")]
    public void WrongSaveRootShapesRefuseBeforeHostInspectionOrEffects(string userdata, string worlds)
    {
        var plan = Plan(); var before = WorldFixture.Manifest(_root);
        var site = Site() with { SteamUserDataDirectory = userdata, WorldsDirectory = worlds };
        Assert.Throws<ArgumentException>(() => site.Place(plan, Path.Combine(_root, "not-created"), default));
        Assert.Empty(_host.Runs); Assert.Empty(_journal);
        Assert.Equal(before, WorldFixture.Manifest(_root));
    }

}
