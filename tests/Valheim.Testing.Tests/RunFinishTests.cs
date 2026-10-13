using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// #667: one run-finish owner. Every path that ends a run reports its outcome to RunFinish, which writes the run's one last entry
// in every journal the run's own flow wrote, and nowhere else; no other code composes that entry.
public sealed class RunFinishTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("run-finish-").FullName;
    private readonly IDisposable _local;
    // A runner on a machine that is not this one: how another machine reads a journal this machine wrote.
    private static readonly JournalRunner Elsewhere = new("another-machine-" + Guid.NewGuid().ToString("N")[..6], 4242, DateTime.UtcNow.AddHours(-1));

    public RunFinishTests() => _local = RunJournal.UseLocalDirectory(Path.Combine(_root, "local-journal"));
    public void Dispose() { _local.Dispose(); Directory.Delete(_root, recursive: true); }

    // Code that names the run's last entries: the kinds, or the entry's own word.
    private static readonly Regex NamesAnEnd = new(@"JournalEntry\.(RunEnded|CleanupAbandoned)\b|""run-ended""|""cleanup-abandoned""", RegexOptions.CultureInvariant);

    [Fact] public void OnlyTheOwnerComposesARunsLastEntry()
    {
        string source = Path.Combine(FixtureProjects.RepositoryRoot(), "src");
        // The owner composes it; RunJournal declares the kinds; RunJournalStatus only reads them.
        var allowed = new[] { "RunFinish.cs", "RunJournal.cs", "RunJournalStatus.cs" };
        var others = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !allowed.Contains(Path.GetFileName(path)))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(item => NamesAnEnd.IsMatch(item.line) && !item.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(source, item.path)}:{item.index + 1}").ToList();
        Assert.Empty(others);
        // RunJournalStatus reads the entry and never writes one.
        string status = File.ReadAllText(Path.Combine(source, "Valheim.Testing.GameSessions", "RunJournalStatus.cs"));
        Assert.DoesNotContain("JournalEntry.Of(", status);
        Assert.DoesNotContain("Append", status);
        // start reports every outcome to the owner, with no guard of its own (#581's ownershipStarted): the owner alone knows
        // whether the run journalled anything. Ending a real start after a journalled profile needs a launched client, so its
        // journal rule is tested below on the real TargetedRegression staging start uses.
        string start = File.ReadAllText(Path.Combine(source, "Valheim.Testing.NativeSmoke", "Program.cs"));
        Assert.Contains("if (!await RunFinish.EndAsync(runJournal, new RunEnd(exitCode == 0 ? RunOutcome.Passed : RunOutcome.Failed, journalClean)))", start);
        Assert.DoesNotContain("ownershipStarted", start);
        // Negative control: the pattern finds the per-path writers this issue removed.
        Assert.Matches(NamesAnEnd, "RunJournal.ThisProcess.AppendLocal(\"run\", JournalEntry.Of(JournalEntry.RunEnded,");
        Assert.Matches(NamesAnEnd, "var last = abandoned != null ? JournalEntry.Of(JournalEntry.CleanupAbandoned, (\"reason\", abandoned))");
    }

    // The #581 drift: start wrote its end only once a character was intended, so a start whose dependency check failed after
    // its profile was journalled and retired never wrote one. Here the real TargetedRegression journals and retires its profile,
    // the check refuses (the character is missing), and the run's flow reports the failure to the owner as start does (the
    // scan above holds start to that call). A client host only: a Linux machine runs no start client (#708).
    [ClientHostFact] public async Task AStartThatFailsAfterItsProfileWasJournalledRecordsAnEndAnotherMachineReadsAsEnded()
    {
        using var rig = new RegressionRig();
        string journal = Path.Combine(_root, "start-journal");
        using var localJournal = RunJournal.UseLocalDirectory(journal);
        var manifest = rig.Manifest();
        manifest.Client.Character = "nobody";
        string runId;
        using (var run = RunJournal.UseRun(RunJournal.NewRunId()))
        {
            runId = run.RunId;
            Assert.Contains("nobody.fch is not in", Assert.Throws<InvalidOperationException>(() => rig.Regression(manifest).Stage("parent")).Message);
            var kinds = Records(journal, runId).Select(record => record.Entry.Kind).ToList();
            Assert.Contains(JournalEntry.CopyIntended, kinds);
            Assert.DoesNotContain(JournalEntry.CharacterIntended, kinds);

            // Before its end: from another machine, which cannot check this runner, the run may still be going.
            Assert.Equal(JournalRunState.Unknown, (await ReadElsewhere(journal, runId)).State);

            Assert.True(await RunFinish.EndAsync(run, new RunEnd(RunOutcome.Failed, CleanupProven: true)));
        }
        var ended = Assert.Single(Records(journal, runId), record => record.Entry.Kind == JournalEntry.RunEnded);
        Assert.Equal(("failed", "true"), (ended.Entry.Fields["state"], ended.Entry.Fields["cleanupVerified"]));
        Assert.Equal(JournalEntry.RunEnded, Records(journal, runId).OrderBy(record => record.Utc).Last().Entry.Kind);
        var status = await ReadElsewhere(journal, runId);
        Assert.Equal(JournalRunState.Ended, status.State);
        Assert.True(status.EndRecorded);
        Assert.Contains("ended failed", status.Reason);
    }

    [Fact] public async Task ARunEndsOnceInEveryJournalItsFlowWroteAndNowhereElse()
    {
        var station = new FakeServerHost("station", Path.Combine(_root, "station"));
        var mac = new FakeServerHost("mac", Path.Combine(_root, "mac"));
        var untouched = new FakeServerHost("nas", Path.Combine(_root, "nas"));
        const string Journal = "/var/tmp/vt/journal";
        string local = Path.Combine(_root, "local-journal");
        var time = TimeSpan.FromSeconds(5);
        string runId = RunJournal.NewRunId(), other = RunJournal.NewRunId();
        using (var run = RunJournal.UseRun(runId))
        {
            var journal = new RunJournal(runId); // a host run's own instance: the flow, not the instance, knows the run
            await journal.AppendAsync(station, Journal, "server", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", "/srv/a")), time);
            await journal.AppendAsync(station, Journal, "server", JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", "/srv/a")), time);
            RunJournal.ThisProcess.AppendLocal(WorldFixture.Actor, JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", "/w")));
            // A second run inside this one (a hosted run under a test's fixed id) keeps its own journals.
            using (RunJournal.UseRun(other))
            {
                await new RunJournal(other).AppendAsync(untouched, Journal, "server", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", "/srv/b")), time);
                // An entry of the outer run written inside the inner scope still joins the outer run.
                await journal.AppendAsync(mac, Journal, "player", JournalEntry.Of(JournalEntry.LockHeld, ("lock", "/l"), ("claimant", "c")), time);
            }
            Assert.True(await RunFinish.EndAsync(run, new RunEnd(RunOutcome.Passed, CleanupProven: true)));
            // Exactly once: a second report writes nothing.
            Assert.True(await RunFinish.EndAsync(run, new RunEnd(RunOutcome.Failed, CleanupProven: false)));
        }
        foreach (var host in new[] { station, mac })
        {
            var end = Assert.Single(await RunJournalOnHost.ReadAsync(host, Journal, runId, time), record => record.Entry.Kind == JournalEntry.RunEnded);
            Assert.Equal(("passed", "true", "run"), (end.Entry.Fields["state"], end.Entry.Fields["cleanupVerified"], end.Actor));
        }
        Assert.Single(Records(local, runId), record => record.Entry.Kind == JournalEntry.RunEnded);
        // The other run's journal, and a host this run never wrote, get nothing.
        Assert.Empty(await RunJournalOnHost.ReadAsync(untouched, Journal, runId, time));
        Assert.DoesNotContain(await RunJournalOnHost.ReadAsync(untouched, Journal, other, time), record => record.Entry.Kind == JournalEntry.RunEnded);

        // A run refused before it journalled anything leaves no journal at all.
        string refused = RunJournal.NewRunId();
        using (var run = RunJournal.UseRun(refused))
            Assert.True(await RunFinish.EndAsync(run, new RunEnd(RunOutcome.Failed, CleanupProven: true)));
        Assert.False(Directory.Exists(Path.Combine(local, refused)));
        Assert.Empty(await RunJournalOnHost.ReadAsync(station, Journal, refused, time));
    }

    // This machine's journal written in process and through its own shell, spelled in another case where the file system
    // ignores case, is one journal: the run's end goes there once.
    [Fact] public async Task ThisMachinesJournalIsOnePlaceHoweverItIsReachedAndSpelled()
    {
        if (OperatingSystem.IsLinux()) return; // Case tells two directories apart there.
        string local = Path.Combine(_root, "local-journal");
        string runId = RunJournal.NewRunId();
        using (var run = RunJournal.UseRun(runId))
        {
            RunJournal.ThisProcess.AppendLocal(WorldFixture.Actor, JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", "/w")));
            await new RunJournal(runId).AppendAsync(new LocalGameHost("local", HostShell.ThisMachine), local.ToUpperInvariant(), "server",
                JournalEntry.Of(JournalEntry.LockHeld, ("lock", "/l"), ("claimant", "c")), TimeSpan.FromSeconds(30));
            Assert.True(await RunFinish.EndAsync(run, new RunEnd(RunOutcome.Passed, CleanupProven: true)));
        }
        Assert.Single(Records(local, runId), record => record.Entry.Kind == JournalEntry.RunEnded);
        Assert.Equal(2, Records(local, runId).Count(record => record.Entry.Kind != JournalEntry.RunEnded));
    }

    // A journal that never took the run's first entry (a write in process that threw, a host that did not confirm) holds
    // nothing of the run, so its end does not go there (a refusal stays a refusal).
    [Fact] public async Task AJournalThatNeverTookAnEntryGetsNoEnd()
    {
        string blocked = Path.Combine(_root, "blocked-journal");
        File.WriteAllText(blocked, "a file where the journal directory would go");
        var host = new FakeServerHost("station", Path.Combine(_root, "station"));
        host.Failures["journal"] = new HostResult(HostOutcome.Exited, 4, "", "No space left on device", TimeSpan.Zero, false);
        string runId = RunJournal.NewRunId();
        using (var run = RunJournal.UseRun(runId))
        {
            Assert.ThrowsAny<IOException>(() => RunJournal.ThisProcess.AppendLocal("run", JournalEntry.Of(JournalEntry.LockHeld, ("lock", "/l"), ("claimant", "c")), blocked));
            await Assert.ThrowsAsync<HostOperationException>(() => new RunJournal(runId).AppendAsync(host, "/var/tmp/vt/journal", "server",
                JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", "/srv/a")), TimeSpan.FromSeconds(5)));
            host.Failures.Remove("journal");
            Assert.Empty(run.Places);
            Assert.True(await RunFinish.EndAsync(run, new RunEnd(RunOutcome.Failed, CleanupProven: true)));
        }
        Assert.Empty(await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", runId, TimeSpan.FromSeconds(5)));
    }

    [Fact] public async Task AnAbandonedCleanupIsRecordedInsteadOfAnEndAndAJournalThatCannotTakeItIsReported()
    {
        var host = new FakeServerHost("station", Path.Combine(_root, "station"));
        const string Journal = "/var/tmp/vt/journal";
        var time = TimeSpan.FromSeconds(5);
        string runId = RunJournal.NewRunId();
        using (var run = RunJournal.UseRun(runId))
        {
            await new RunJournal(runId).AppendAsync(host, Journal, "server", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", "/srv/a")), time);
            RunJournal.ThisProcess.AppendLocal("run", JournalEntry.Of(JournalEntry.LockHeld, ("lock", "/l"), ("claimant", "c")));
            host.Failures["journal"] = new HostResult(HostOutcome.Exited, 4, "", "No space left on device", TimeSpan.Zero, false);
            // The host cannot take it: reported, and this machine's journal still gets it.
            Assert.False(await RunFinish.EndAsync(run, new RunEnd(RunOutcome.Failed, CleanupProven: true, Abandoned: "a second interrupt during cleanup")));
            host.Failures.Remove("journal");
        }
        var local = Records(Path.Combine(_root, "local-journal"), runId);
        Assert.Equal("a second interrupt during cleanup", Assert.Single(local, record => record.Entry.Kind == JournalEntry.CleanupAbandoned).Entry.Fields["reason"]);
        Assert.DoesNotContain(local, record => record.Entry.Kind == JournalEntry.RunEnded);
        Assert.DoesNotContain(await RunJournalOnHost.ReadAsync(host, Journal, runId, time), record => record.Entry.Kind is JournalEntry.RunEnded or JournalEntry.CleanupAbandoned);
    }

    [Fact] public async Task OnlyARecoveryOrAGoneMachineRecordsAnotherRunnersEnd()
    {
        var host = new FakeServerHost("station", Path.Combine(_root, "station"));
        var journal = new RunJournal(RunJournal.NewRunId());
        await Assert.ThrowsAsync<ArgumentException>(() => RunFinish.RecordForGoneRunnerAsync(journal, host, "/var/tmp/vt/journal",
            new RunEnd(RunOutcome.Passed, CleanupProven: true), TimeSpan.FromSeconds(5)));
        await RunFinish.RecordForGoneRunnerAsync(journal, host, "/var/tmp/vt/journal", new RunEnd(RunOutcome.Recovered, CleanupProven: true), TimeSpan.FromSeconds(5));
        var end = Assert.Single(await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", journal.RunId, TimeSpan.FromSeconds(5)));
        Assert.Equal((RunRecovery.Actor, "recovered: runner gone", "true"), (end.Actor, end.Entry.Fields["state"], end.Entry.Fields["cleanupVerified"]));
    }

    private static List<JournalRecord> Records(string journal, string runId)
    {
        string directory = Path.Combine(journal, runId);
        return !Directory.Exists(directory) ? [] : Directory.EnumerateFiles(directory, "*.jsonl")
            .SelectMany(File.ReadAllLines).Where(line => line.Trim().Length != 0).Select(RunJournal.ParseLine).ToList();
    }

    // The run's journal as another machine reads it: the same lines on a host of its inventory, its runner not this machine.
    private async Task<JournalRunStatus> ReadElsewhere(string journal, string runId)
    {
        var host = new FakeServerHost("pc", Path.Combine(_root, "elsewhere-" + Guid.NewGuid().ToString("N")[..6]));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(journal, runId), "*.jsonl"))
        {
            string target = host.Local(RunJournalStatusTests.Journal + "/" + runId + "/" + Path.GetFileName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllLines(target, File.ReadAllLines(file).Where(line => line.Trim().Length != 0).Select(line =>
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(line)!;
                node["runner"]!["machine"] = Elsewhere.Machine;
                return node.ToJsonString();
            }));
        }
        var hosts = new Dictionary<string, HostProfile> { ["pc"] = new() { Kind = "ssh", Lock = "/var/tmp/vt/lock" } };
        var report = await RunJournalStatus.InspectAsync(hosts, _ => host, TimeSpan.FromSeconds(5));
        return Assert.Single(report.Runs, run => run.Run == runId);
    }
}
