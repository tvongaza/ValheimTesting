using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

// env status (#257 step 4): what each journalled run left on each host, judged against the host, changing nothing.
public sealed class RunJournalStatusTests : IDisposable
{
    internal const string Journal = "/var/tmp/vt/journal";
    private readonly string _root = Directory.CreateTempSubdirectory("journal-status-").FullName;
    private readonly FakeServerHost _host;
    // A day back, so the lines a test writes come before any a recovery writes now.
    private static readonly DateTime T0 = DateTime.UtcNow.AddDays(-1);
    // A runner on this machine whose process is gone (no process has this ID), one still running (this test), one elsewhere.
    internal static readonly JournalRunner Gone = new(Environment.MachineName, 999_999_999, T0.AddHours(-1));
    private static readonly JournalRunner Elsewhere = new("another-machine-" + Guid.NewGuid().ToString("N")[..6], 4242, T0.AddHours(-1));

    public RunJournalStatusTests() => _host = new FakeServerHost("pc", Path.Combine(_root, "pc"));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Dictionary<string, HostProfile> Hosts(params string[] names) =>
        names.ToDictionary(name => name, _ => new HostProfile { Kind = "ssh", Lock = "/var/tmp/vt/lock" });

    private Task<JournalStatusReport> InspectAsync(params (string Name, FakeServerHost Host)[] hosts)
    {
        if (hosts.Length == 0) hosts = [("pc", _host)];
        return RunJournalStatus.InspectAsync(Hosts([.. hosts.Select(host => host.Name)]), name => hosts.Single(host => host.Name == name).Host, TimeSpan.FromSeconds(5));
    }

    // One journal line, as RunJournal.AppendAsync writes it, with the runner chosen by the test.
    private static int s_minute;
    internal static void Line(FakeServerHost host, string run, string actor, JournalRunner? runner, string kind, params (string Key, string Value)[] fields)
    {
        string file = host.Local(Journal + "/" + run + "/" + actor + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var line = new Dictionary<string, object?>
        {
            ["utc"] = T0.AddMinutes(Interlocked.Increment(ref s_minute)).ToString("O"), ["run"] = run, ["actor"] = actor, ["kind"] = kind,
            ["fields"] = fields.ToDictionary(field => field.Key, field => field.Value),
        };
        if (runner != null) line["runner"] = new Dictionary<string, object> { ["machine"] = runner.Machine, ["pid"] = runner.Pid, ["startedUtc"] = runner.StartedUtc.ToString("O") };
        File.AppendAllText(file, JsonSerializer.Serialize(line) + "\n");
    }

    private static JournalRunStatus Run(JournalStatusReport report, string run) => Assert.Single(report.Runs, status => status.Run == run);

    [Fact] public async Task ARunThatEndedAndRetiredEverythingLeftNothingAndAnInterruptedOneNamesEachThingItLeft()
    {
        // Over: copied, retired, its lock released, its end journalled.
        Line(_host, "run-over", "server", Gone, JournalEntry.CopyIntended, ("runtime", "/srv/runs/over/server/runtime"));
        Line(_host, "run-over", "server", Gone, JournalEntry.CopyDone, ("runtime", "/srv/runs/over/server/runtime"));
        Line(_host, "run-over", "run", Gone, JournalEntry.LockHeld, ("lock", "/var/tmp/vt/lock"), ("claimant", "run-over [a]"));
        Line(_host, "run-over", "server", Gone, JournalEntry.CopyRetired, ("runtime", "/srv/runs/over/server/runtime"));
        Line(_host, "run-over", "run", Gone, JournalEntry.LockReleased, ("lock", "/var/tmp/vt/lock"), ("claimant", "run-over [a]"));
        Line(_host, "run-over", "run", Gone, JournalEntry.RunEnded, ("state", "passed"), ("cleanupVerified", "true"));
        // Interrupted: a partial copy, a staged character, a held lease and lock, and a server still running as journalled.
        Line(_host, "run-cut", "server", Gone, JournalEntry.CopyIntended, ("runtime", "/srv/runs/cut/server/runtime"));
        Line(_host, "run-cut", "player", Gone, JournalEntry.CharacterIntended, ("characters", "/home/p/characters_local"), ("userData", "/home/p/userdata"), ("fileName", "vt01"));
        Line(_host, "run-cut", "player", Gone, JournalEntry.CharacterDone, ("fileName", "vt01"));
        _host.Leases["alt1"] = ("run-cut [b]", "lease-b");
        Line(_host, "run-cut", "player", Gone, JournalEntry.LeaseHeld, ("account", "alt1"), ("pool", "steam"), ("owner", "run-cut [b]"), ("expiresUtc", T0.ToString("O")),
            ("leaseId", "lease-b"), ("number", "1"), ("directory", "/var/tmp/vt/leases"));
        _host.Claims.Add("run-cut [c]");
        Line(_host, "run-cut", "run", Gone, JournalEntry.LockHeld, ("lock", "/var/tmp/vt/lock"), ("claimant", "run-cut [c]"));
        _host.Running(41, "9041");
        Line(_host, "run-cut", "server", Gone, JournalEntry.ProcessIntended, ("bootDirectory", "/srv/runs/cut/boot-1"));
        Line(_host, "run-cut", "server", Gone, JournalEntry.ProcessStarted, ("pid", "41"), ("startIdentity", "9041"),
            ("commandLineSha256", FakeServerHost.CommandLineSha256("41")), ("bootDirectory", "/srv/runs/cut/boot-1"));

        var report = await InspectAsync();

        Assert.Equal(new JournalHostStatus("pc", Journal, 2, null), Assert.Single(report.Hosts));
        var over = Run(report, "run-over");
        Assert.Equal(JournalRunState.Ended, over.State);
        Assert.Empty(over.Items);
        var cut = Run(report, "run-cut");
        Assert.Equal(JournalRunState.Recoverable, cut.State);
        Assert.Contains("never journalled its end; its runner is gone", cut.Reason);
        Assert.Equal(["copy", "character", "lease", "lock", "process"], cut.Items.Select(item => item.Kind));
        Assert.Equal("copy started, never finished", cut.Items[0].Status);
        Assert.Equal("staged, not retired", cut.Items[1].Status);
        Assert.Equal("held by this run until 2030-01-01 00:00Z unless released", cut.Items[2].Status);
        Assert.Equal("held by this run (run-cut [c])", cut.Items[3].Status);
        Assert.Equal("still runs (ID, start time and command line match)", cut.Items[4].Status);
        Assert.DoesNotContain(cut.Items, item => item.Unrecoverable);
        Assert.False(report.Clean);
        // Nothing was changed: the only scripts were the journal read and one process check.
        Assert.Equal(["journal-read-all", "process-probe", "lease"], _host.Scripts);
        Assert.Empty(_host.Stops);
    }

    // #257 Q2: a process counts as the run's own only when its ID, start time and command line all match the journal. Two of
    // three is unrecoverable and named; a reused ID or an exited process is gone.
    [Fact] public async Task AProcessIsTheRunsOwnOnlyWhenIdStartAndCommandLineAllMatch()
    {
        _host.Running(51, "7");           // the journal says start 6: another process reused ID 51
        _host.Running(53, "9053");        // its command line is not the journalled one
        _host.CommandLines[53] = new string('a', 64);
        _host.Running(54, "9054");        // journalled before command lines were
        void Started(string run, int pid, string start, string commandLine) => Line(_host, run, "server", Gone, JournalEntry.ProcessStarted,
            ("pid", pid.ToString()), ("startIdentity", start), ("commandLineSha256", commandLine), ("bootDirectory", "/b/" + pid));
        Started("run-gone", 50, "9050", FakeServerHost.CommandLineSha256("50"));
        Started("run-gone", 51, "6", FakeServerHost.CommandLineSha256("51"));
        Started("run-other-command", 53, "9053", FakeServerHost.CommandLineSha256("53"));
        Started("run-no-command", 54, "9054", "");

        var report = await InspectAsync();

        Assert.Equal(JournalRunState.Ended, Run(report, "run-gone").State);
        Assert.Empty(Run(report, "run-gone").Items);
        var other = Run(report, "run-other-command");
        Assert.Equal(JournalRunState.Unrecoverable, other.State);
        Assert.Equal("a process with its ID and start time runs another command line", Assert.Single(other.Items).Status);
        var unrecorded = Run(report, "run-no-command");
        Assert.Equal(JournalRunState.Unrecoverable, unrecorded.State);
        Assert.Contains("its command line was not journalled", Assert.Single(unrecorded.Items).Status);
        // One check for all four processes.
        Assert.Equal("50:9050 51:6 53:9053 54:9054", string.Join(' ', _host.Runs.Single(run => run.Script == "process-probe").Variables["processes"].Split(' ').Order()));
    }

    [Fact] public async Task ARunWithoutItsEndIsLiveWhileItsRunnerRunsAndUnknownWhenItsRunnerCannotBeChecked()
    {
        foreach (var (run, runner) in new[] { ("run-live", JournalRunner.Current), ("run-elsewhere", Elsewhere), ("run-unrecorded", (JournalRunner?)null) })
            Line(_host, run, "server", runner, JournalEntry.CopyIntended, ("runtime", "/srv/" + run));

        var report = await InspectAsync();

        Assert.Equal(JournalRunState.Live, Run(report, "run-live").State);
        Assert.Equal(JournalRunner.Current.ToString(), Run(report, "run-live").Runner);
        Assert.Equal(JournalRunState.Unknown, Run(report, "run-elsewhere").State);
        Assert.Contains($"its runner ran on {Elsewhere.Machine}", Run(report, "run-elsewhere").Reason);
        Assert.Equal(JournalRunState.Unknown, Run(report, "run-unrecorded").State);
        Assert.Contains("its runner was not journalled", Run(report, "run-unrecorded").Reason);
    }

    // #257 review: two runs that journalled the same process ID with different start times get their own verdicts.
    [Fact] public async Task AProcessIdReusedByALaterRunIsJudgedPerStartIdentity()
    {
        _host.Running(41, "200");
        Line(_host, "run-early", "server", Gone, JournalEntry.ProcessStarted, ("pid", "41"), ("startIdentity", "100"), ("commandLineSha256", FakeServerHost.CommandLineSha256("41")));
        Line(_host, "run-late", "server", Gone, JournalEntry.ProcessStarted, ("pid", "41"), ("startIdentity", "200"), ("commandLineSha256", FakeServerHost.CommandLineSha256("41")));

        var report = await InspectAsync();

        Assert.Equal(JournalRunState.Ended, Run(report, "run-early").State);
        Assert.Equal(JournalRunState.Recoverable, Run(report, "run-late").State);
        Assert.Equal("41 (started 200)", Assert.Single(Run(report, "run-late").Items).What);
    }

    [Fact] public async Task AnUnreadableJournalLineLeavesTheHostUnclean()
    {
        Line(_host, "run-over", "run", Gone, JournalEntry.RunEnded, ("state", "passed"), ("cleanupVerified", "true"));
        Directory.CreateDirectory(_host.Local(Journal + "/run-cut"));
        File.AppendAllText(_host.Local(Journal + "/run-cut/server.jsonl"), "{\"utc\":");

        var report = await InspectAsync();

        Assert.False(report.Clean);
        Assert.Equal("1 journal line could not be read, so what they record is unknown.", Assert.Single(report.Hosts).Error);
        Assert.Equal(JournalRunState.Ended, Assert.Single(report.Runs).State);
    }

    [Fact] public async Task KeptCopiesAndLeasesLeaveARunKeptAndAnInterruptedLaunchIsUnrecoverableUnlessTheRunCleanedUp()
    {
        Line(_host, "run-kept", "server", Gone, JournalEntry.CopyDone, ("runtime", "/srv/kept"));
        Line(_host, "run-kept", "server", Gone, JournalEntry.CopyKept, ("runtime", "/srv/kept"), ("why", "kept on request"));
        _host.Leases["alt2"] = ("o", "lease-o");
        Line(_host, "run-kept", "player", Gone, JournalEntry.LeaseHeld, ("account", "alt2"), ("pool", "steam"), ("owner", "o"), ("expiresUtc", ""),
            ("leaseId", "lease-o"), ("number", "1"), ("directory", "/var/tmp/vt/leases"));
        Line(_host, "run-kept", "player", Gone, JournalEntry.LeaseKept, ("account", "alt2"), ("pool", "steam"), ("owner", "o"), ("expiresUtc", ""));
        Line(_host, "run-kept", "run", Gone, JournalEntry.RunEnded, ("state", "passed"), ("cleanupVerified", "true"));
        // A start the run saw fail and cleaned up after; the same left by a run whose cleanup was not verified; and by one interrupted.
        foreach (var (run, cleaned) in new[] { ("run-start-failed", "true"), ("run-unverified", "false"), ("run-launch-cut", "") })
        {
            Line(_host, run, "player", Gone, JournalEntry.ProcessIntended, ("launchDirectory", "/l/" + run));
            if (cleaned.Length != 0) Line(_host, run, "run", Gone, JournalEntry.RunEnded, ("state", "failed"), ("cleanupVerified", cleaned));
        }
        // A lock journalled as held that nobody holds any more (removed by a person) is not left.
        Line(_host, "run-lock-gone", "run", Gone, JournalEntry.LockHeld, ("lock", "/var/tmp/vt/lock"), ("claimant", "removed [x]"));

        var report = await InspectAsync();

        var kept = Run(report, "run-kept");
        Assert.Equal(JournalRunState.Kept, kept.State);
        Assert.All(kept.Items, item => Assert.True(item.Kept));
        Assert.Equal(["kept: kept on request", "kept: its client may still run; held by this run until 2030-01-01 00:00Z unless released"], kept.Items.Select(item => item.Status));
        Assert.Equal(JournalRunState.Ended, Run(report, "run-start-failed").State);
        Assert.Equal(JournalRunState.Unrecoverable, Run(report, "run-unverified").State);
        Assert.Contains("cleanup not verified", Run(report, "run-unverified").Reason);
        var cut = Run(report, "run-launch-cut");
        Assert.Equal(JournalRunState.Unrecoverable, cut.State);
        Assert.Equal("/l/run-launch-cut", Assert.Single(cut.Items).What);
        Assert.Equal(JournalRunState.Ended, Run(report, "run-lock-gone").State);
    }

    // A lease counts only while its journalled owner still holds it on the lease host; one journalled without its lease id cannot be checked.
    [Fact] public async Task ALeaseIsLeftOnlyWhileItsOwnerHoldsItAndAnUncheckableOneIsUnrecoverable()
    {
        _host.Leases["alt3"] = ("someone else", "lease-x");
        Line(_host, "run-lease-gone", "player", Gone, JournalEntry.LeaseHeld, ("account", "alt3"), ("pool", "steam"), ("owner", "run-lease-gone [a]"),
            ("expiresUtc", ""), ("leaseId", "lease-a"), ("number", "1"), ("directory", "/var/tmp/vt/leases"));
        Line(_host, "run-lease-old", "player", Gone, JournalEntry.LeaseHeld, ("account", "alt4"), ("pool", "steam"), ("owner", "o"), ("expiresUtc", ""));

        var report = await InspectAsync();

        Assert.Equal(JournalRunState.Ended, Run(report, "run-lease-gone").State);
        Assert.Equal(JournalRunState.Unrecoverable, Run(report, "run-lease-old").State);
        Assert.StartsWith("journalled without its lease directory", Assert.Single(Run(report, "run-lease-old").Items).Status);

        // With the inventory's lease directory, an older lease is checked there: one its owner no longer holds is gone, one it
        // still holds cannot be released without its id and only lapses.
        _host.Leases["alt4"] = ("o", "unknown");
        var withDirectory = await RunJournalStatus.InspectAsync(Hosts("pc"), _ => _host, TimeSpan.FromSeconds(5), leaseHost: "pc", leaseDirectory: "/var/tmp/vt/leases");
        Assert.Equal(JournalRunState.Unrecoverable, Run(withDirectory, "run-lease-old").State);
        Assert.Contains("only its lapse ends it", Assert.Single(Run(withDirectory, "run-lease-old").Items).Status);
        _host.Leases.Remove("alt4");
        withDirectory = await RunJournalStatus.InspectAsync(Hosts("pc"), _ => _host, TimeSpan.FromSeconds(5), leaseHost: "pc", leaseDirectory: "/var/tmp/vt/leases");
        Assert.Equal(JournalRunState.Ended, Run(withDirectory, "run-lease-old").State);
    }

    // SSH loss: a host whose journal cannot be read is named, and nothing is reported as over or free.
    [Fact] public async Task AnUnreachableHostIsNamedAndAProcessThatCannotBeCheckedIsUnrecoverable()
    {
        var lost = new FakeServerHost("lost", Path.Combine(_root, "lost"));
        lost.Failures["journal-read-all"] = FakeServerHost.TransportFailure;
        Line(_host, "run-probe-lost", "server", Gone, JournalEntry.ProcessStarted, ("pid", "60"), ("startIdentity", "1"), ("commandLineSha256", "x"));
        _host.Failures["process-probe"] = FakeServerHost.TransportFailure;

        var report = await InspectAsync(("pc", _host), ("lost", lost));

        Assert.False(report.Clean);
        var unreachable = Assert.Single(report.Hosts, host => host.Name == "lost");
        Assert.Contains("Connection refused", unreachable.Error);
        var run = Assert.Single(report.Runs);
        Assert.Equal(JournalRunState.Unrecoverable, run.State);
        Assert.StartsWith("cannot be checked: ", Assert.Single(run.Items).Status);

        var text = new StringWriter();
        RunJournalStatus.Write(report, text, json: false);
        Assert.Contains("UNREADABLE host lost: ", text.ToString());
        Assert.Contains("UNRECOVERABLE run-probe-lost on pc, ", text.ToString());
        Assert.Contains("  process 60 (started 1) on pc (server, since ", text.ToString());
        Assert.EndsWith("0 runs ended and left nothing. Nothing was changed." + Environment.NewLine, text.ToString());
    }

    // End to end through the real local shell: valheim-test env status reads this machine's journal and says what a run left.
    [Fact] public async Task EnvStatusReadsTheLocalHostsJournalThroughItsShell()
    {
        string data = Path.Combine(_root, "data dir");
        string file = Path.Combine(_root, "inventory.json");
        // This machine's own journal is the inventory host's (its lock is in the data folder), so no other test's copies show.
        using var machine = EnvironmentInventory.UseMachine(new FakeMachine(HostProfile.CurrentPlatform) { DataRoot = data });
        using var localJournal = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));
        // Only the host: a journal needs no install or environment.
        File.WriteAllText(file, JsonSerializer.Serialize(new { hosts = new { local = new { kind = "local", @lock = Path.Combine(data, "lock") } } }));
        var local = OperatingSystem.IsWindows() ? new LocalGameHost("local", HostShell.WindowsPowerShell) : new LocalGameHost("local", HostShell.Bash);
        var journal = new RunJournal("run-20261005T180000Z-0badc0de");
        await journal.AppendAsync(local, Path.Combine(data, "journal"), "player", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", Path.Combine(data, "runs", "x"))), TimeSpan.FromSeconds(30));

        var output = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["status", "--inventory", file], output, new StringWriter()));
        // This test is the runner, and it still runs.
        Assert.Contains("LIVE run-20261005T180000Z-0badc0de on local", output.ToString());
        Assert.Contains("host local: journal " + Path.Combine(data, "journal") + ", 1 run", output.ToString());

        await journal.AppendAsync(local, Path.Combine(data, "journal"), "player", JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", Path.Combine(data, "runs", "x"))), TimeSpan.FromSeconds(30));
        await journal.AppendAsync(local, Path.Combine(data, "journal"), "run", JournalEntry.Of(JournalEntry.RunEnded, ("state", "passed"), ("cleanupVerified", "true")), TimeSpan.FromSeconds(30));
        output = new StringWriter();
        Assert.Equal(0, await EnvCommand.RunAsync(["status", "--inventory", file, "--json"], output, new StringWriter()));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("Clean").GetBoolean());
        Assert.Equal("Ended", json.RootElement.GetProperty("Runs")[0].GetProperty("State").GetString());

        foreach (string[] args in new[] { new[] { "status", "--inventory" }, ["status", "extra"], ["status", "--hosts"] })
            Assert.Equal(2, await EnvCommand.RunAsync(args, new StringWriter(), new StringWriter()));
    }
}
