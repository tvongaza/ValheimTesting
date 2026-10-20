using System.Diagnostics;
using System.Text;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

// #257's interrupted-run faults for a hosted run: Ctrl+C while a client waits for its startup, and a standalone run's own
// server profile and world copy on the server host, journalled before their scripts run.
public sealed partial class HostedServerRunTests
{
    private static Dictionary<string, HostProfile> LinuxBox() => new() { ["linux-box"] = new() { Kind = "ssh", Lock = "/var/tmp/vt/lock" } };
    // The copies in the run's folder; the folder's own lines (RunFolder) are RunFolderCopies.
    private static IEnumerable<(string Kind, string Path)> Copies(IReadOnlyList<JournalRecord> journal) => journal
        .Where(record => record.Entry.Kind.StartsWith("copy-", StringComparison.Ordinal) && record.Entry.Fields["runtime"] != RunDirectory)
        .Select(record => (record.Entry.Kind, record.Entry.Fields["runtime"]));
    private static IEnumerable<string> RunFolderCopies(IReadOnlyList<JournalRecord> journal) => journal
        .Where(record => record.Entry.Kind.StartsWith("copy-", StringComparison.Ordinal) && record.Entry.Fields["runtime"] == RunDirectory)
        .Select(record => record.Entry.Kind);
    // Where in a fake host's script log the journal append of that kind for that copy ran.
    private static int CopyJournalIndex(FakeServerHost host, string kind, string path) => host.Runs.ToList().FindIndex(run => run.Script == "journal" &&
        Encoding.UTF8.GetString(Convert.FromBase64String(run.Variables["line"])) is var line &&
        line.Contains($"\"kind\":\"{kind}\"", StringComparison.Ordinal) && line.Contains($"\"runtime\":\"{path}\"", StringComparison.Ordinal));

    // Gap 1: a Ctrl+C while an owned client waits for its startup (here its BepInEx log never gets a line) ends that wait at
    // once, not at the client's 120 s deadline: the client and the server are stopped by identity, the host copy is retired,
    // and the run's end is journalled with its cleanup verified.
    [Fact] public async Task ACtrlCWhileAClientWaitsForItsStartupEndsTheWaitAtOnceAndCleansUpTheRun()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("mac-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/Users/tester/valheim");
        FakeInstalls.MacClient(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        var client = PreparedClient(clientHost, startSeconds: 120, bepInExSeconds: 120, connect: false);
        using var interrupt = new RunCancellation();
        // The wait hangs until its token is cancelled; the Ctrl+C comes while it is in progress.
        clientHost.Hang.Add("follow");
        clientHost.BeforeScript = name =>
        {
            if (name == "follow") _ = Task.Run(async () => { await Task.Delay(200); interrupt.SignalCancel(); });
        };
        Exception? failure = null;
        var clock = Stopwatch.StartNew();
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            failure = Record.Exception(() => run.OpenClient(client));
            run.Cancellation.ThrowIfCancellationRequested(); // the scenario ends on the Ctrl+C, as a real one does
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport(), cancellation: interrupt)).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the run took {clock.Elapsed}");
        Assert.Equal(1, code);
        Assert.Null(interrupt.Abandoned);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        // The client this run started, and only it, stopped by its identity; its tunnel closed and its host's lock released.
        Assert.Equal(new[] { ("77", "555") }, clientHost.Stops);
        Assert.True(Assert.Single(clientHost.Tunnels).Stopped);
        Assert.Equal(clientHost.Claims, clientHost.Releases); Assert.Single(clientHost.Claims);
        // The server stopped by its identity, its profile retired, its lock released.
        Assert.Equal(new[] { ("1", "9001") }, host.Stops);
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.Equal(host.Claims, host.Releases);
        Assert.True(Result().GetProperty("CleanupVerified").GetBoolean());
        var ended = Assert.Single(await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5)), record => record.Entry.Kind == JournalEntry.RunEnded);
        Assert.Equal(("failed", "true"), (ended.Entry.Fields["state"], ended.Entry.Fields["cleanupVerified"]));
        // The client's process was journalled before its start and after it; nothing of the run is left for env recover.
        var clientJournal = await RunJournalOnHost.ReadAsync(clientHost, "/Users/tester/journal", RunId, TimeSpan.FromSeconds(5));
        var playerKinds = clientJournal.Where(record => record.Actor == "player").Select(record => record.Entry.Kind).ToList();
        Assert.Equal([JournalEntry.ProcessIntended, JournalEntry.ProcessStarted], playerKinds.Where(kind => kind is JournalEntry.ProcessIntended or JournalEntry.ProcessStarted));
        // The Mac GUI agent journalled before the start is retired again: no live agent is left for env recover.
        Assert.Equal(playerKinds.Count(kind => kind == JournalEntry.MacAgentIntended), playerKinds.Count(kind => kind == JournalEntry.MacAgentRetired));
        Assert.Contains(JournalEntry.MacAgentIntended, playerKinds);
    }

    // Gap 3: a standalone run's server profile (#730) and world copy on the server host are each journalled before their scripts
    // run, done once made and verified, then retired (the world and boot logs only after verified fetch); env status then finds nothing of the run left. A profile or copy the journal cannot name is never made.
    [Fact] public async Task AStandaloneRunJournalsItsProfileAndWorldBeforeTheirScriptsAndMakesNothingItCannotJournal()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        string runtime = RunDirectory + "/runtime", world = RunDirectory + "/world";
        var journal = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.Equal([(JournalEntry.CopyIntended, runtime), (JournalEntry.CopyDone, runtime), (JournalEntry.CopyIntended, world), (JournalEntry.CopyDone, world),
            (JournalEntry.CopyIntended, RunDirectory + "/boot-1"), (JournalEntry.CopyRetired, runtime),
            (JournalEntry.CopyDone, world), (JournalEntry.CopyRetired, world),
            (JournalEntry.CopyDone, RunDirectory + "/boot-1"), (JournalEntry.CopyRetired, RunDirectory + "/boot-1")], Copies(journal));
        // The run's folder is journalled before its first copy, and fetched (here nothing was left in it) and removed last (#791).
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.CopyRetired], RunFolderCopies(journal));
        Assert.True(CopyJournalIndex(host, JournalEntry.CopyIntended, RunDirectory) < CopyJournalIndex(host, JournalEntry.CopyIntended, runtime));
        Assert.True(CopyJournalIndex(host, JournalEntry.CopyRetired, RunDirectory) > CopyJournalIndex(host, JournalEntry.CopyRetired, RunDirectory + "/boot-1"));
        Assert.False(Directory.Exists(host.Local(RunDirectory)));
        Assert.DoesNotContain(journal, record => record.Entry.Fields.ContainsKey("handedOver"));
        Assert.All(journal.Where(record => record.Entry.Kind.StartsWith("copy-", StringComparison.Ordinal)), record => Assert.Equal("server", record.Actor));
        var scripts = host.Runs.ToList();
        // The profile's staging folder is its first write, the world's ship the world's.
        Assert.True(CopyJournalIndex(host, JournalEntry.CopyIntended, runtime) is >= 0 and var intended && intended < scripts.FindIndex(run => run.Script == "ship"));
        Assert.True(CopyJournalIndex(host, JournalEntry.CopyIntended, world) is >= 0 and var shipped && shipped < scripts.FindIndex(run => run.Script == "ship" && run.Variables["dest"] == world));
        Assert.False(Directory.Exists(host.Local(world))); // the verified fetch is the run's evidence
        var status = Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host, TimeSpan.FromSeconds(5))).Runs);
        Assert.Equal(JournalRunState.Ended, status.State);
        Assert.Empty(status.Items);

        // A copy-intended line that cannot be written: no profile, no ship, nothing started.
        var server2 = NewServer(); var host2 = new FakeServerHost("linux-box", Path.Combine(_root, "mirror-2"), server2) { JournalFailsFor = JournalEntry.CopyIntended };
        var (plan2, profile2) = Write(host2);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile2), ["run", plan2, Path.Combine(_root, "output-2")], Options(host2, server2)));
        Assert.DoesNotContain(host2.Runs, run => run.Script is "profile-seed" or "ship" or "start");
        Assert.False(Directory.Exists(host2.Local(runtime)));
        Assert.Equal(host2.Claims, host2.Releases);
    }

    // Gap 3: a profile whose staging failed part-way (here its apply failed on the host) is removed by the stage's own cleanup and
    // journalled retired. When that cleanup is not proven (its reply was lost: an unknown outcome), the run's teardown removes the
    // partial profile and its staging; when
    // that fails too, the profile stays open in the journal, env status names it, and env recover removes it. A world ship that
    // failed partway is removed by the teardown.
    private static readonly HostResult ApplyFailed = new(HostOutcome.Exited, 3, "", "cp: cannot create regular file: No space left on device", TimeSpan.Zero, false);

    [Fact] public async Task APartialProfileIsRemovedByItsStageOrTheRunOrElseByEnvRecover()
    {
        string runtime = RunDirectory + "/runtime", staging = RunDirectory + "/staging", world = RunDirectory + "/world";
        var server = NewServer(); var host = NewHost(server);
        host.Failures["apply-stage"] = ApplyFailed;
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.False(Directory.Exists(host.Local(runtime))); Assert.False(Directory.Exists(host.Local(staging)));
        var journal = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.Equal([(JournalEntry.CopyIntended, runtime), (JournalEntry.CopyRetired, runtime)], Copies(journal));
        Assert.Equal("true", journal.Single(record => record.Entry.Kind == JournalEntry.CopyRetired && record.Entry.Fields["runtime"] == runtime).Entry.Fields["failed"]);
        Assert.Equal(JournalRunState.Ended, Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host, TimeSpan.FromSeconds(5))).Runs).State);
        Assert.DoesNotContain("ship and verify the world copy on the server host", StepNames());

        // The stage's own cleanup is not proven (its reply is lost too): the teardown removes the partial profile, journalled.
        var server1 = NewServer(); var host1 = new FakeServerHost("linux-box", Path.Combine(_root, "mirror-1"), server1);
        host1.Failures["apply-stage"] = ApplyFailed;
        int cleanups = 0;
        host1.BeforeScript = name => { if (name == "cleanup-stage" && ++cleanups == 1) host1.Failures["cleanup-stage"] = FakeServerHost.TransportFailure; else host1.Failures.Remove("cleanup-stage"); };
        var (plan1, profile1) = Write(host1);
        Assert.Equal(3, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile1), ["run", plan1, Path.Combine(_root, "output-1")], Options(host1, server1)));
        Assert.Equal(3, cleanups); // the stage's own, the teardown's, then the run's folder (#791)
        Assert.False(Directory.Exists(host1.Local(runtime))); Assert.False(Directory.Exists(host1.Local(staging)));
        Assert.Equal([(JournalEntry.CopyIntended, runtime), (JournalEntry.CopyRetired, runtime)],
            Copies(await RunJournalOnHost.ReadAsync(host1, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5))));
        Assert.Empty(Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host1, TimeSpan.FromSeconds(5))).Runs).Items);

        // The run's own removal fails as well: the partial profile is left, and the journal says so.
        var server2 = NewServer(); var host2 = new FakeServerHost("linux-box", Path.Combine(_root, "mirror-2"), server2);
        host2.Failures["apply-stage"] = ApplyFailed;
        host2.Failures["cleanup-stage"] = new HostResult(HostOutcome.Exited, 3, "", "rm: cannot remove: Device or resource busy", TimeSpan.Zero, false);
        var (plan2, profile2) = Write(host2);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile2), ["run", plan2, Path.Combine(_root, "output-2")], Options(host2, server2)));
        host2.Failures.Remove("cleanup-stage");
        Assert.True(Directory.Exists(host2.Local(runtime))); Assert.True(Directory.Exists(host2.Local(staging)));
        var before = Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host2, TimeSpan.FromSeconds(5))).Runs);
        Assert.Equal(JournalRunState.Recoverable, before.State);
        var left = Assert.Single(before.Items, item => item.What != RunDirectory);
        Assert.Equal(("copy", runtime, "copy started, never finished"), (left.Kind, left.What, left.Status));
        // The run's folder stays open with it while the profile in it is not settled (#791).
        Assert.Equal("copy started, never finished", Assert.Single(before.Items, item => item.What == RunDirectory).Status);
        var recovery = await RunRecovery.RecoverAsync(LinuxBox(), _ => host2, RunId, teardown: false, TimeSpan.FromSeconds(5));
        Assert.True(recovery.Recovered, string.Join("\n", recovery.Steps));
        Assert.False(Directory.Exists(host2.Local(runtime)));
        Assert.Contains(host2.Runs, run => run.Script == "cleanup-stage" && run.Variables["runtime"] == runtime && run.Variables["stage"] == staging);
        Assert.False(Directory.Exists(host2.Local(staging)));
        // The run's folder, emptied by that removal, goes after it: nothing in it was left unfetched (#791).
        Assert.Contains("nothing", Assert.Single(recovery.Steps, step => step.What == "copy " + RunDirectory).Outcome);
        Assert.False(Directory.Exists(host2.Local(RunDirectory)));
        var after = Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host2, TimeSpan.FromSeconds(5))).Runs);
        Assert.Equal(JournalRunState.Ended, after.State);
        Assert.Empty(after.Items);

        // A world ship that fails partway: the partial world copy goes at teardown, journalled.
        var server3 = NewServer(); var host3 = new FakeServerHost("linux-box", Path.Combine(_root, "mirror-3"), server3);
        host3.AfterShip = dir => { if (dir == host3.Local(world)) throw new IOException("ship reply lost after copy"); };
        var (plan3, profile3) = Write(host3);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile3), ["run", plan3, Path.Combine(_root, "output-3")], Options(host3, server3)));
        Assert.False(Directory.Exists(host3.Local(world)));
        Assert.False(Directory.Exists(host3.Local(runtime)));
        Assert.Equal([(JournalEntry.CopyIntended, runtime), (JournalEntry.CopyDone, runtime), (JournalEntry.CopyIntended, world), (JournalEntry.CopyRetired, runtime),
            (JournalEntry.CopyRetired, world)], Copies(await RunJournalOnHost.ReadAsync(host3, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5))));
        Assert.Empty(Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host3, TimeSpan.FromSeconds(5))).Runs).Items);
    }
}
