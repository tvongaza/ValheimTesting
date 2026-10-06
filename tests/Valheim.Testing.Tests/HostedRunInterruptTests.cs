using System.Diagnostics;
using System.Text;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// #257's interrupted-run faults for a hosted run: Ctrl+C while a client waits for its startup, and a standalone run's own
// runtime and world copies on the server host, journalled before their scripts run.
public sealed partial class HostedServerRunTests
{
    private static Dictionary<string, HostProfile> LinuxBox() => new() { ["linux-box"] = new() { Kind = "ssh", Lock = "/var/tmp/vt/lock" } };
    private static IEnumerable<(string Kind, string Path)> Copies(IReadOnlyList<JournalRecord> journal) => journal
        .Where(record => record.Entry.Kind.StartsWith("copy-", StringComparison.Ordinal))
        .Select(record => (record.Entry.Kind, record.Entry.Fields["runtime"]));
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
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, GameLaunch.ClientLinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 120, BepInExSeconds = 120 };
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
        // The server stopped by its identity, its runtime copy retired, its lock released.
        Assert.Equal(new[] { ("1", "9001") }, host.Stops);
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.Equal(host.Claims, host.Releases);
        Assert.True(Result().GetProperty("CleanupVerified").GetBoolean());
        var ended = Assert.Single(await RunJournal.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5)), record => record.Entry.Kind == JournalEntry.RunEnded);
        Assert.Equal(("failed", "true"), (ended.Entry.Fields["state"], ended.Entry.Fields["cleanupVerified"]));
        // The client's process was journalled before its start and after it; nothing of the run is left for env recover.
        var clientJournal = await RunJournal.ReadAsync(clientHost, "/home/tester/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.Equal([JournalEntry.ProcessIntended, JournalEntry.ProcessStarted], clientJournal.Where(record => record.Actor == "player").Select(record => record.Entry.Kind));
    }

    // Gap 3: a standalone run's runtime copy and world copy on the server host are each journalled before their script runs,
    // done once verified, then retired (the runtime) or handed over as the run's evidence (the world, which stays beside the
    // boot logs); env status then finds nothing of the run left. A copy the journal cannot name is never made.
    [Fact] public async Task AStandaloneRunJournalsItsHostCopiesBeforeTheirScriptsAndMakesNoCopyItCannotJournal()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        string runtime = RunDirectory + "/runtime", world = RunDirectory + "/world";
        var journal = await RunJournal.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.Equal([(JournalEntry.CopyIntended, runtime), (JournalEntry.CopyDone, runtime), (JournalEntry.CopyIntended, world), (JournalEntry.CopyDone, world),
            (JournalEntry.CopyRetired, runtime), (JournalEntry.CopyRetired, world)], Copies(journal));
        Assert.Equal("true", journal.Last(record => record.Entry.Kind == JournalEntry.CopyRetired).Entry.Fields["handedOver"]);
        Assert.All(journal.Where(record => record.Entry.Kind.StartsWith("copy-", StringComparison.Ordinal)), record => Assert.Equal("server", record.Actor));
        var scripts = host.Runs.ToList();
        Assert.True(CopyJournalIndex(host, JournalEntry.CopyIntended, runtime) is >= 0 and var intended && intended < scripts.FindIndex(run => run.Script == "copy"));
        Assert.True(CopyJournalIndex(host, JournalEntry.CopyIntended, world) is >= 0 and var shipped && shipped < scripts.FindIndex(run => run.Script == "ship"));
        Assert.True(Directory.Exists(host.Local(world))); // the run's evidence, as before
        var status = Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host, TimeSpan.FromSeconds(5))).Runs);
        Assert.Equal(JournalRunState.Ended, status.State);
        Assert.Empty(status.Items);

        // A copy-intended line that cannot be written: no copy, no ship, nothing started.
        var server2 = NewServer(); var host2 = new FakeServerHost("linux-box", Path.Combine(_root, "mirror-2"), server2) { JournalFailsFor = JournalEntry.CopyIntended };
        var (plan2, profile2) = Write(host2);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile2), ["run", plan2, Path.Combine(_root, "output-2")], Options(host2, server2)));
        Assert.DoesNotContain(host2.Runs, run => run.Script is "copy" or "ship" or "start");
        Assert.False(Directory.Exists(host2.Local(runtime)));
        Assert.Equal(host2.Claims, host2.Releases);
    }

    // Gap 3: a copy whose reply was lost (it was made, but the run never heard so) is still the run's: its teardown removes it.
    // When that removal fails too, the copy stays open in the journal, env status names it, and env recover removes it. A
    // world ship that failed partway is removed by the teardown.
    [Fact] public async Task ACopyWhoseReplyWasLostIsRemovedByTheRunOrElseByEnvRecover()
    {
        string runtime = RunDirectory + "/runtime", world = RunDirectory + "/world";
        var server = NewServer(); var host = NewHost(server);
        host.AfterCopy = _ => throw new IOException("copy reply lost after copy");
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.False(Directory.Exists(host.Local(runtime)));
        Assert.Equal([(JournalEntry.CopyIntended, runtime), (JournalEntry.CopyRetired, runtime)],
            Copies(await RunJournal.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5))));
        Assert.Equal(JournalRunState.Ended, Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host, TimeSpan.FromSeconds(5))).Runs).State);

        // The run's own removal fails as well: the partial copy is left, and the journal says so.
        var server2 = NewServer(); var host2 = new FakeServerHost("linux-box", Path.Combine(_root, "mirror-2"), server2);
        host2.AfterCopy = _ => throw new IOException("copy reply lost after copy");
        host2.Failures["retire"] = new HostResult(HostOutcome.Exited, 3, "", "rm: cannot remove: Device or resource busy", TimeSpan.Zero, false);
        var (plan2, profile2) = Write(host2);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile2), ["run", plan2, Path.Combine(_root, "output-2")], Options(host2, server2)));
        host2.Failures.Remove("retire");
        Assert.True(Directory.Exists(host2.Local(runtime)));
        var before = Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host2, TimeSpan.FromSeconds(5))).Runs);
        Assert.Equal(JournalRunState.Recoverable, before.State);
        var left = Assert.Single(before.Items);
        Assert.Equal(("copy", runtime, "copy started, never finished"), (left.Kind, left.What, left.Status));
        var recovery = await RunRecovery.RecoverAsync(LinuxBox(), _ => host2, RunId, teardown: false, TimeSpan.FromSeconds(5));
        Assert.True(recovery.Recovered, string.Join("\n", recovery.Steps));
        Assert.False(Directory.Exists(host2.Local(runtime)));
        Assert.Contains(host2.Runs, run => run.Script == "cleanup-stage" && run.Variables["runtime"] == runtime);
        var after = Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host2, TimeSpan.FromSeconds(5))).Runs);
        Assert.Equal(JournalRunState.Ended, after.State);
        Assert.Empty(after.Items);

        // A world ship that fails partway: the partial world copy goes at teardown, journalled.
        var server3 = NewServer(); var host3 = new FakeServerHost("linux-box", Path.Combine(_root, "mirror-3"), server3);
        host3.AfterShip = _ => throw new IOException("ship reply lost after copy");
        var (plan3, profile3) = Write(host3);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile3), ["run", plan3, Path.Combine(_root, "output-3")], Options(host3, server3)));
        Assert.False(Directory.Exists(host3.Local(world)));
        Assert.False(Directory.Exists(host3.Local(runtime)));
        Assert.Equal([(JournalEntry.CopyIntended, runtime), (JournalEntry.CopyDone, runtime), (JournalEntry.CopyIntended, world), (JournalEntry.CopyRetired, runtime),
            (JournalEntry.CopyRetired, world)], Copies(await RunJournal.ReadAsync(host3, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5))));
        Assert.Empty(Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host3, TimeSpan.FromSeconds(5))).Runs).Items);
    }
}
