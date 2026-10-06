using Valheim.Testing.Game;
using Xunit;

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
        string flat = FakeInstalls.World(Path.Combine(_root, "flat"));
        Directory.CreateDirectory(Path.Combine(fixture, "Campaign"));
        if (!File.Exists(Path.Combine(fixture, "Campaign", "_main.0.fwl2"))) File.Copy(Path.Combine(flat, "Campaign.fwl"), Path.Combine(fixture, "Campaign", "_main.0.fwl2"));
        return new() { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
    }
    private HostedWorldOnHost.Site Site() => new(_host, "pc", Worlds, "/runs/run-1/host-world-stage", "/runs/run-1/host-world",
        (entry, _) => { lock (_journal) _journal.Add(entry); return Task.CompletedTask; }, _ => { _held++; return Task.CompletedTask; });
    private int _held;

    [Theory] [InlineData("Campaign")] [InlineData("campaign.fwl")] [InlineData("Campaign_backup_auto-1.db")]
    public void AWorldAlreadyNamedLikeTheFixtureIsNeverOverwrittenAndNothingIsShipped(string existing)
    {
        Directory.CreateDirectory(_host.Local(Worlds));
        string path = _host.Local(Worlds + "/" + existing);
        if (existing == "Campaign") Directory.CreateDirectory(path); else File.WriteAllText(path, "the user's own");
        string output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var error = Assert.Throws<InvalidOperationException>(() => HostedWorldOnHost.Place(Site(), Plan(), output, pinned: true, CancellationToken.None));
        Assert.Contains("never copied over a world", error.Message);
        Assert.Empty(_journal);
        Assert.DoesNotContain(_host.Runs, run => run.Script is "ship" or "world-move");
        Assert.True(Path.Exists(path));
    }

    [Fact] public void ThePlacementIsJournalledBeforeTheShipAndCollectingMovesItIntoTheRunsFolder()
    {
        string output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var placed = HostedWorldOnHost.Place(Site(), Plan(), output, pinned: true, CancellationToken.None);
        Assert.Equal("pc", placed.Host);
        Assert.True(File.Exists(_host.Local(Worlds + "/Campaign/_main.0.fwl2")));
        Assert.False(File.Exists(_host.Local(Worlds + "/SOURCE.txt"))); // the ship's record stays in the stage
        Assert.Equal(new[] { JournalEntry.CopyIntended, JournalEntry.CopyDone }, _journal.Select(entry => entry.Kind));
        Assert.Equal(Worlds + "/Campaign", _journal[0].Fields["runtime"]);
        placed.Collect();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_host.Local(Worlds)));
        Assert.True(File.Exists(_host.Local("/runs/run-1/host-world/Campaign/_main.0.fwl2")));
        Assert.True(File.Exists(Path.Combine(placed.CollectedTo!, "Campaign", "_main.0.fwl2")));
        Assert.Equal(JournalEntry.CopyRetired, _journal[^1].Kind);
    }
}
