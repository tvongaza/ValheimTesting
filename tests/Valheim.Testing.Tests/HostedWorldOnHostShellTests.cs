using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// The hosted world's scripts in a real shell (bash, or pwsh on Windows), on a folder standing for a user's worlds_local: a
// fake host cannot show what the scripts themselves select and refuse.
public sealed class HostedWorldOnHostShellTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-world-shell-").FullName;
    private string Worlds => Path.Combine(_root, "worlds_local");
    private string To => Path.Combine(_root, "runs", "run-1", "host-world");
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public void ServerWorldCopiesAreRetiredOnlyAfterVerifiedFetchUnderRealHostScripts(string shell)
    {
        var host = Host(shell);
        string fixture = FakeInstalls.World(Path.Combine(_root, "fixture"));
        var plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
        var journal = new List<JournalEntry>();
        var site = new HostedWorldOnHost.Site(host, host.Name, Worlds, Path.Combine(_root, "runs", "run-1", "world-stage"), To,
            (entry, _) => { journal.Add(entry); return Task.CompletedTask; }, _ => Task.CompletedTask) { RetireRunCopies = "run-1" };
        Directory.CreateDirectory(Worlds);
        File.WriteAllText(Path.Combine(Worlds, "Unrelated.fwl"), "the user's world");
        string output = Path.Combine(_root, "output");
        var placed = site.Place(plan, output, default);
        Assert.True(Directory.Exists(site.Stage));
        placed.Collect();
        Assert.False(Directory.Exists(site.Stage));
        Assert.False(Directory.Exists(To));
        Assert.True(File.Exists(Path.Combine(placed.CollectedTo!, "Campaign", "_main.1.fwl2")));
        Assert.Equal("the user's world", File.ReadAllText(Path.Combine(Worlds, "Unrelated.fwl")));
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.CopyRetired],
            journal.Where(entry => entry.Fields.GetValueOrDefault("runtime") == To).Select(entry => entry.Kind));


    }

    // A user's worlds_local: the world's own entries as the game writes them (its chunked folder and a backup), one in another case,
    // and the user's other worlds, one of them <name>_mine (a world of its own name, which the game never writes for Campaign).
    private static readonly string[] Named = ["Campaign", "Campaign_backup_auto-20261006.db", "campaign.fwl"];
    private static readonly string[] Others = ["Campaign2.fwl", "MyWorld.fwl", "Campaign_mine.fwl", "Campaig.fwl"];
    private void User()
    {
        Directory.CreateDirectory(Path.Combine(Worlds, "Campaign"));
        File.WriteAllText(Path.Combine(Worlds, "Campaign", "_main.0.fwl2"), "world");
        File.WriteAllText(Path.Combine(Worlds, "Campaign_backup_auto-20261006.db"), "backup");
        foreach (string other in Others.Append("campaign.fwl")) File.WriteAllText(Path.Combine(Worlds, other), "the user's");
    }
    private static IGameHost Host(string shell) => new LocalGameHost("local-" + shell, HostShell.Parse(shell));
    private static bool Windows(IGameHost host) => host.Shell.Kind == HostShellKind.PowerShell;

    // #829: one rule for what is named for a world. The refusal before a placement and the move-out after it take exactly the same
    // entries (<name>, <name>.*, <name>_backup*, any case), and they are the ones HostedWorld.IsNamedFor, the fixture's own rule,
    // names: so a move-out only ever takes what the refusal proved absent before the run placed it, and takes back all it placed.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheRefusalAndTheMoveOutTakeTheSameEntriesTheFixtureRuleNames(string shell)
    {
        User();
        var host = Host(shell);
        var result = (await host.RunAsync(Windows(host) ? HostedWorldOnHost.WindowsEntries : HostedWorldOnHost.BashEntries,
            new Dictionary<string, string> { ["worlds"] = Worlds, ["name"] = "Campaign" }, TimeSpan.FromSeconds(60))).EnsureSuccess("entries");
        var listed = result.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("VT-WORLD-ENTRY ", StringComparison.Ordinal))
            .Select(line => line["VT-WORLD-ENTRY ".Length..]).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Named.Order(StringComparer.Ordinal), listed);
        Assert.Equal(Named.Order(StringComparer.Ordinal), Directory.EnumerateFileSystemEntries(Worlds).Select(Path.GetFileName)
            .Where(leaf => HostedWorld.IsNamedFor(leaf!, "Campaign")).Order(StringComparer.Ordinal));

        var moved = await HostedWorldOnHost.MoveOut(host, Worlds, "Campaign", To, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal(listed, moved.Order(StringComparer.Ordinal));
        Assert.Equal("world", File.ReadAllText(Path.Combine(To, "Campaign", "_main.0.fwl2")));
        Assert.Equal(Others.Order(StringComparer.Ordinal), Directory.EnumerateFileSystemEntries(Worlds).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task AMoveNeverOverwrites(string shell)
    {
        User();
        var host = Host(shell);
        // A destination that exists already: refused before anything moves.
        Directory.CreateDirectory(Path.Combine(To, "Campaign"));
        await Assert.ThrowsAnyAsync<Exception>(() => HostedWorldOnHost.MoveOut(host, Worlds, "Campaign", To, TimeSpan.FromSeconds(60), CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(Worlds, "Campaign", "_main.0.fwl2")));
        Assert.True(File.Exists(Path.Combine(Worlds, "Campaign_backup_auto-20261006.db")));
        Assert.True(File.Exists(Path.Combine(Worlds, "campaign.fwl")));
    }

    // #807: what env recover reads before it moves a journalled world: the UID every metadata file of Valheim 1.0's world folder
    // and of the game's backups of it states. Another UID is refused; entries without readable metadata (a pre-1.0 <name>.db, which a
    // run never places) are refused; nothing there is reported as nothing. Only the world's own files are read: a <name>_mine of the
    // user's says nothing.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheIdentityCheckReadsTheWorldUidFromTheChunkedFolder(string shell)
    {
        var host = Host(shell);
        Directory.CreateDirectory(Path.Combine(Worlds, "Campaign"));
        File.WriteAllBytes(Path.Combine(Worlds, "Campaign", "_main.1.fwl2"), FakeInstalls.Metadata("Campaign", 4242));
        File.WriteAllBytes(Path.Combine(Worlds, "Campaign", "_main.12.fwl2"), FakeInstalls.Metadata("Campaign", 4242));
        File.WriteAllText(Path.Combine(Worlds, "Campaign", "_main.1.db2"), "data");
        File.WriteAllText(Path.Combine(Worlds, "Campaign", "0_0.chunk"), "chunk");
        Directory.CreateDirectory(Path.Combine(Worlds, "Campaign_mine"));
        File.WriteAllBytes(Path.Combine(Worlds, "Campaign_mine", "_main.1.fwl2"), FakeInstalls.Metadata("Campaign", 9999));
        Task<bool> Check(string name, string uid) => HostedWorldOnHost.RequireOwnWorldAsync(host, Worlds, name, uid, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(await Check("Campaign", "4242"));
        Assert.Contains("UID 4242, not the fixture's 7", (await Assert.ThrowsAsync<InvalidDataException>(() => Check("Campaign", "7"))).Message);
        Assert.False(await Check("Absent", "4242"));
        Directory.CreateDirectory(Path.Combine(Worlds, "Partial"));
        File.WriteAllText(Path.Combine(Worlds, "Partial", "_main.1.db2"), "data");
        Assert.Contains("holds no world metadata", (await Assert.ThrowsAsync<InvalidDataException>(() => Check("Partial", "4242"))).Message);
        // #869: a pre-1.0 world a user put back under the name is a file no run places; the move-out would take it, so it is refused.
        File.WriteAllText(Path.Combine(Worlds, "Campaign.fwl"), "the user's pre-1.0 world");
        Assert.Contains("Campaign.fwl in", (await Assert.ThrowsAsync<InvalidDataException>(() => Check("Campaign", "4242"))).Message);
        File.Delete(Path.Combine(Worlds, "Campaign.fwl"));

        // #829: every entry a move-out would take is read, in any case, not only <name>/: another world's metadata in a backup
        // folder (in another case) is refused, so the world stays where it is.
        Directory.CreateDirectory(Path.Combine(Worlds, "CAMPAIGN_backup_auto-20261006-120000"));
        File.WriteAllBytes(Path.Combine(Worlds, "CAMPAIGN_backup_auto-20261006-120000", "_main.0.fwl2"), FakeInstalls.Metadata("Campaign", 9999));
        Assert.Contains("UID 9999, not the fixture's 4242", (await Assert.ThrowsAsync<InvalidDataException>(() => Check("Campaign", "4242"))).Message);
    }
}
