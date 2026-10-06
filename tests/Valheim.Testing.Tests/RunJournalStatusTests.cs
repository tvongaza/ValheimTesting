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
        Assert.Equal("held by this run until released", cut.Items[2].Status);
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

    // Run A (#258): a run whose runner ran on the host itself (the station PC, its journal read from this Mac) is judged through
    // that host: ended when its runner is gone, live while it runs. A runner on a third machine, or on a host that cannot be
    // asked, stays unknown; so does one on a bash host, whose start times do not compare with the journalled one.
    [Fact] public async Task ARunnerOnTheHostItselfIsJudgedThroughThatHost()
    {
        var pc = new FakeServerHost("pc", Path.Combine(_root, "winpc"), windows: true) { MachineName = Elsewhere.Machine.ToUpperInvariant() };
        var gone = Elsewhere with { Pid = 5001 };
        var alive = Elsewhere with { Pid = 5002 };
        var third = new JournalRunner("a-third-machine", 5003, T0.AddHours(-1));
        pc.Running(5002, alive.StartedUtc.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture));
        pc.Running(5004, DateTime.UtcNow.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture)); // ID 5004 reused since
        var reused = Elsewhere with { Pid = 5004 };
        foreach (var (run, runner) in new[] { ("run-gone", gone), ("run-alive", alive), ("run-third", third), ("run-reused", reused) })
            Line(pc, run, "server", runner, JournalEntry.CopyIntended, ("runtime", "/srv/" + run));
        Line(pc, "run-gone", "server", gone, JournalEntry.CopyRetired, ("runtime", "/srv/run-gone"));
        Line(pc, "run-reused", "server", reused, JournalEntry.CopyRetired, ("runtime", "/srv/run-reused"));

        var report = await InspectAsync(("pc", pc));

        Assert.Equal(JournalRunState.Ended, Run(report, "run-gone").State);
        Assert.Contains("its runner is gone, as host pc reports", Run(report, "run-gone").Reason);
        Assert.Equal(JournalRunState.Ended, Run(report, "run-reused").State); // Same ID, another start time: not the runner.
        // Negative control: the runner still runs there, so the run is live, not ended.
        Assert.Equal(JournalRunState.Live, Run(report, "run-alive").State);
        Assert.Contains("as host pc reports", Run(report, "run-alive").Reason);
        Assert.Equal(JournalRunState.Unknown, Run(report, "run-third").State);
        Assert.Contains("its runner ran on a-third-machine: check it there", Run(report, "run-third").Reason);
        Assert.Single(pc.Runs, script => script.Script == "machine-name");
        Assert.Equal("5001: 5002: 5004:", string.Join(' ', pc.Runs.Single(script => script.Script == "process-probe").Variables["processes"].Split(' ').Order()));

        // A host that cannot say its name leaves its runs unknown.
        var mute = new FakeServerHost("pc", Path.Combine(_root, "mute"), windows: true);
        mute.Failures["machine-name"] = new HostResult(HostOutcome.Unknown, null, "", "", TimeSpan.FromSeconds(5), true);
        Line(mute, "run-gone", "server", gone, JournalEntry.CopyIntended, ("runtime", "/srv/run-gone"));
        Assert.Equal(JournalRunState.Unknown, Run(await InspectAsync(("pc", mute)), "run-gone").State);
        // Two hosts reporting the runner's machine name: neither is taken for it, so the run stays unknown.
        var twin = new FakeServerHost("twin", Path.Combine(_root, "twin"), windows: true) { MachineName = pc.MachineName };
        var twins = await RunJournalStatus.InspectAsync(Hosts("pc", "twin"), name => name == "pc" ? pc : twin, TimeSpan.FromSeconds(5));
        Assert.Equal(JournalRunState.Unknown, Run(twins, "run-gone").State);
        // The runner's machine is asked even when the run journalled only on another host.
        var other = new FakeServerHost("nas", Path.Combine(_root, "nas"));
        Line(other, "run-on-nas", "server", gone, JournalEntry.CopyIntended, ("runtime", "/srv/run-on-nas"));
        var viaPc = await RunJournalStatus.InspectAsync(Hosts("pc", "nas"), name => name == "pc" ? pc : other, TimeSpan.FromSeconds(5));
        Assert.Contains("its runner is gone, as host pc reports", Run(viaPc, "run-on-nas").Reason);
        // A bash host is not asked: its runs stay unknown.
        Line(_host, "run-bash", "server", gone, JournalEntry.CopyIntended, ("runtime", "/srv/run-bash"));
        Assert.Equal(JournalRunState.Unknown, Run(await InspectAsync(), "run-bash").State);
        Assert.DoesNotContain(_host.Runs, script => script.Script == "machine-name");
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
        Assert.Equal(["kept: kept on request", "kept: its client may still run; held by this run until released"], kept.Items.Select(item => item.Status));
        Assert.Equal(JournalRunState.Ended, Run(report, "run-start-failed").State);
        Assert.Equal(JournalRunState.Unrecoverable, Run(report, "run-unverified").State);
        Assert.Contains("cleanup not verified", Run(report, "run-unverified").Reason);
        var cut = Run(report, "run-launch-cut");
        Assert.Equal(JournalRunState.Unrecoverable, cut.State);
        Assert.Equal("/l/run-launch-cut", Assert.Single(cut.Items).What);
        Assert.Equal(JournalRunState.Ended, Run(report, "run-lock-gone").State);
    }

    // A launcher's pid file, as the Windows launchers write it (PID and start identity) or the Linux recorders do (PID only).
    internal static void PidFile(FakeServerHost host, string directory, string text)
    {
        Directory.CreateDirectory(host.Local(directory));
        File.WriteAllText(Path.Combine(host.Local(directory), "pid"), text);
    }

    // The native recovery check of 5 Oct 2026 (#257): the runner was killed after the client's process-intended and before its
    // process-started, with the client's pid file written and its process running. Its pid file names a process whose ID, start
    // identity and command line match the launch: the run is recoverable, and the client is named as the run's process.
    [Fact] public async Task ALaunchKilledBeforeItsProcessWasJournalledAdoptsTheProcessItsPidFileNames()
    {
        const string launch = "/srv/runs/run-native/client-1";
        _host.Running(91152, "134357104986630869");
        Line(_host, "run-native", "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", launch),
            ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("91152")));
        PidFile(_host, launch, "91152 134357104986630869");

        var report = await InspectAsync();

        var run = Run(report, "run-native");
        Assert.Equal(JournalRunState.Recoverable, run.State);
        var adopted = Assert.Single(run.Items);
        Assert.Equal("process", adopted.Kind);
        Assert.Equal("91152 (started 134357104986630869)", adopted.What);
        Assert.Equal($"still runs; adopted from its pid file in {launch} (ID, start time and command line match)", adopted.Status);
        Assert.False(adopted.Unrecoverable);
        Assert.Equal(("91152", "134357104986630869", FakeServerHost.CommandLineSha256("91152"), launch),
            (adopted.Fields["pid"], adopted.Fields["startIdentity"], adopted.Fields["commandLineSha256"], adopted.Fields["launchDirectory"]));
        var text = new StringWriter();
        RunJournalStatus.Write(report, text, json: false);
        Assert.Contains("RECOVERABLE run-native on pc, ", text.ToString());
        Assert.Contains("  process 91152 (started 134357104986630869) on pc (client, since ", text.ToString());
        // One pid-file read and one process check, nothing else.
        Assert.Equal(["journal-read-all", "pid-file", "process-probe"], _host.Scripts);
        Assert.Equal("91152:134357104986630869", _host.Runs.Single(run => run.Script == "process-probe").Variables["processes"]);
        Assert.Empty(_host.Stops);
    }

    // What a pid file proves, and what it does not: a process gone (or an ID reused by another start) proves the launch gone; a
    // missing or unreadable pid file, a live process whose start identity is not recorded, a launch that journalled no command
    // line, and another command line leave it unrecoverable, never adopted.
    [Fact] public async Task ALaunchsPidFileProvesItsProcessGoneOrItsOwnAndNothingLess()
    {
        string Expected(string pid) => FakeServerHost.CommandLineSha256(pid);
        void Launch(string run, string? pidFile, string pid, bool commandLine = true)
        {
            string directory = "/srv/runs/" + run + "/client-1";
            Line(_host, run, "client", Gone, JournalEntry.ProcessIntended, commandLine
                ? [("launchDirectory", directory), ("expectedCommandLineSha256", Expected(pid))] : [("launchDirectory", directory)]);
            if (pidFile != null) PidFile(_host, directory, pidFile);
        }
        Launch("run-exited", "70 9070", "70");                      // its process has exited: no process 70
        _host.Running(71, "8000");                                   // ID 71 now belongs to a process started at another time
        Launch("run-reused", "71 9071", "71");
        Launch("run-no-pid-file", null, "72");                        // killed before the launcher wrote its pid file
        Launch("run-garbled", "not a pid", "73");
        _host.Running(74, "9074");                                    // a Linux recorder's pid file: the PID only
        Launch("run-pid-only", "74\n", "74");
        Launch("run-pid-only-gone", "75\n", "75");
        _host.Running(76, "9076");                                    // journalled before launches journalled their command line
        Launch("run-no-command-line", "76 9076", "76", commandLine: false);
        _host.Running(77, "9077");                                    // another program with the pid file's ID and start identity
        _host.CommandLines[77] = new string('b', 64);
        Launch("run-other-command-line", "77 9077", "77");

        var report = await InspectAsync();

        foreach (string run in new[] { "run-exited", "run-reused", "run-pid-only-gone" })
        {
            Assert.Equal(JournalRunState.Ended, Run(report, run).State);
            Assert.Empty(Run(report, run).Items);
        }
        string Status(string run)
        {
            var status = Run(report, run);
            Assert.Equal(JournalRunState.Unrecoverable, status.State);
            var item = Assert.Single(status.Items);
            Assert.Equal("launch", item.Kind);
            Assert.True(item.Unrecoverable);
            return item.Status;
        }
        Assert.Contains("/srv/runs/run-no-pid-file/client-1 does not exist", Status("run-no-pid-file"));
        Assert.Contains("names no process ID and start identity (it holds 'not a pid')", Status("run-garbled"));
        Assert.Contains("names process 74, which runs, but no start identity", Status("run-pid-only"));
        Assert.Contains("names process 76 (started 9076), which still runs; the launch journalled no command line", Status("run-no-command-line"));
        Assert.Contains("names process 77 (started 9077), which runs another command line than the launch's", Status("run-other-command-line"));
        Assert.Empty(_host.Stops);
    }

    // The pid-file read and the server copy's top-level skip through this machine's own shell (bash, or Windows PowerShell);
    // on Linux, also the command line a game started the Linux launchers' way (env executing it) has, as the probe hashes it.
    [Fact] public async Task ThePidFileReadAndTheServerCopysSkipRunThroughThisMachinesShell()
    {
        var local = OperatingSystem.IsWindows() ? new LocalGameHost("local", HostShell.WindowsPowerShell) : new LocalGameHost("local", HostShell.Bash);
        string Dir(string name) { string path = Path.Combine(_root, "launches", name); Directory.CreateDirectory(path); return path; }
        string windows = Dir("windows client"), linux = Dir("linux client"), empty = Dir("empty"), missing = Path.Combine(_root, "launches", "never made");
        File.WriteAllText(Path.Combine(windows, "pid"), "91152 134357104986630869");
        File.WriteAllText(Path.Combine(linux, "pid"), "4242\n");
        File.WriteAllText(Path.Combine(empty, "pid"), "");
        var files = await RunJournalStatus.ReadPidFilesAsync(local, [windows, linux, empty, missing], TimeSpan.FromSeconds(60), default);
        Assert.Equal([(RunJournalStatus.PidFileState.Found, 91152, "134357104986630869"), (RunJournalStatus.PidFileState.Found, 4242, null),
            (RunJournalStatus.PidFileState.Malformed, 0, null), (RunJournalStatus.PidFileState.Missing, 0, null)],
            files.Select(file => (file.State, file.Pid, file.StartIdentity)));

        // The install's top-level logs/ stays behind; a nested one, the rest and the install itself are untouched.
        string install = Path.Combine(_root, "server install"), runtime = Path.Combine(_root, "runs", "run 1", "runtime");
        foreach (var (relative, text) in new[] { ("valheim_server.x86_64", "game"), ("logs/connection_log_2456.txt", "steam"), ("logs/stats_log.txt", "steam"),
                     ("BepInEx/logs/kept.txt", "own"), (".hidden", "dot") })
        {
            string file = Path.Combine(install, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        await HostInstall.CopyAsync(local, install, runtime, TimeSpan.FromSeconds(60), HostInstall.ServerRuntimeSkips, default);
        Assert.Equal(new[] { "valheim_server.x86_64", ".hidden", "BepInEx/logs/kept.txt" }.Order(StringComparer.Ordinal),
            WorldFixture.Manifest(runtime).Keys.Select(key => key.Replace('\\', '/')).Order(StringComparer.Ordinal));
        Assert.Equal(5, WorldFixture.Manifest(install).Count);
        string whole = Path.Combine(_root, "runs", "run 2", "runtime");
        await HostInstall.CopyAsync(local, install, whole, TimeSpan.FromSeconds(60));
        Assert.Equal(WorldFixture.Manifest(install), WorldFixture.Manifest(whole));

        if (!OperatingSystem.IsLinux()) return;
        string sleep = File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep";
        // The ID runs a shell for 2 s before env executes the game in place, as a launch's recorder fork does (#463): the probe
        // must wait through every launch stage, not only env. 2 s outlasts the probe's own start on a slow runner, inside its 5 s wait.
        var started = (await local.RunAsync("setsid sh -c 'sleep 2; exec env \"$0\" 60' \"$exe\" > /dev/null 2>&1 < /dev/null & echo \"VT-PID $!\"",
            new Dictionary<string, string> { ["exe"] = sleep }, TimeSpan.FromSeconds(30))).EnsureSuccess("starting sleep");
        int pid = int.Parse(InteractiveClient.Line(started.Stdout, "VT-PID ")!, System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            // Until the game is executed in place, the command line is a launch stage's, so wait for the exec as the start's probe does.
            var probed = (await HostProcessProbe.ProbeAsync(local, [(pid, "")], TimeSpan.FromSeconds(30), settle: true))[(pid, "")];
            Assert.Equal(ProbedState.Same, probed.State);
            Assert.Equal(HostProcessProbe.ExpectedCommandLineSha256(false, sleep, ["60"]), probed.CommandLineSha256);
        }
        finally { try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch (ArgumentException) { } }
    }

    // A host that cannot answer for a pid file, or for the process it names, leaves the launch unrecoverable.
    [Fact] public async Task APidFileOrItsProcessThatCannotBeCheckedLeavesTheLaunchUnrecoverable()
    {
        _host.Running(80, "9080");
        Line(_host, "run-unread", "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", "/l/80"), ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("80")));
        PidFile(_host, "/l/80", "80 9080");
        _host.Failures["pid-file"] = FakeServerHost.TransportFailure;
        Assert.Contains("cannot be read: ", Assert.Single(Run(await InspectAsync(), "run-unread").Items).Status);
        _host.Failures.Remove("pid-file");
        _host.Failures["process-probe"] = FakeServerHost.TransportFailure;
        var item = Assert.Single(Run(await InspectAsync(), "run-unread").Items);
        Assert.True(item.Unrecoverable);
        Assert.Contains("names process 80 (started 9080), which cannot be checked: ", item.Status);
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
        // still holds cannot be released by env recover without its id (it never lapses): the escape hatch names the way out.
        _host.Leases["alt4"] = ("o", "unknown");
        var withDirectory = await RunJournalStatus.InspectAsync(Hosts("pc"), _ => _host, TimeSpan.FromSeconds(5), leaseHost: "pc", leaseDirectory: "/var/tmp/vt/leases");
        Assert.Equal(JournalRunState.Unrecoverable, Run(withDirectory, "run-lease-old").State);
        Assert.Contains("env recover cannot release it; once its client is gone, valheim-test env teardown --run run-lease-old --machine-gone releases it", Assert.Single(Run(withDirectory, "run-lease-old").Items).Status);
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

    // End to end through the real local shell (#257): a lease never lapses, so a run whose machine is gone for good holds its
    // account until the maintainer's escape hatch, env teardown --run ID --machine-gone, releases it on the lease host. Only that
    // run's leases go; the release and the run's end are journalled there; a run still going is refused.
    [Fact] public async Task TheMachineGoneEscapeHatchReleasesOnlyThatRunsLeasesAndJournalsIt()
    {
        string data = Path.Combine(_root, "data dir"), leases = Path.Combine(data, "leases"), file = Path.Combine(_root, "inventory.json");
        using var machine = EnvironmentInventory.UseMachine(new FakeMachine(HostProfile.CurrentPlatform) { DataRoot = data });
        using var localJournal = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));
        File.WriteAllText(file, JsonSerializer.Serialize(new { hosts = new { local = new { kind = "local", @lock = Path.Combine(data, "lock") } }, leaseHost = "local", leaseDirectory = leases }));
        var local = OperatingSystem.IsWindows() ? new LocalGameHost("local", HostShell.WindowsPowerShell) : new LocalGameHost("local", HostShell.Bash);
        SteamAccountPool Pool(string account) => new() { Pool = "steam-clients", LeaseDirectory = leases, Accounts = [new SteamPoolAccount { Name = account }] };
        // The gone run's lease names its run; another run's lease stays; a still-going run (this test is its runner) is refused.
        var gone = await Pool("steam_gone").AcquireAsync(local, "runner run-gone client player", TimeSpan.FromSeconds(30), run: "run-gone");
        await Pool("steam_other").AcquireAsync(local, "runner run-other client player", TimeSpan.FromSeconds(30), run: "run-other");
        await new RunJournal("run-live").AppendAsync(local, Path.Combine(data, "journal"), "player", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", Path.Combine(data, "x"))), TimeSpan.FromSeconds(30));

        foreach (string[] args in new[] { new[] { "teardown", "--machine-gone" }, ["teardown", "--run", "run-gone", "--machine-gone", "--json"], ["recover", "--run", "run-gone", "--machine-gone"] })
            Assert.Equal(2, await EnvCommand.RunAsync([.. args, "--inventory", file], new StringWriter(), new StringWriter()));
        var output = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["teardown", "--run", "run-live", "--machine-gone", "--inventory", file], output, new StringWriter()));
        Assert.Contains("REFUSED run-live: it is still going", output.ToString());

        output = new StringWriter();
        Assert.Equal(0, await EnvCommand.RunAsync(["teardown", "--run", "run-gone", "--machine-gone", "--inventory", file], output, new StringWriter()));
        Assert.Contains("RELEASED lease on Steam account steam_gone (steam-clients), held by runner run-gone client player: its machine was declared gone", output.ToString());
        Assert.Equal(SteamAccountState.Free, Assert.Single(await Pool("steam_gone").ListAsync(local, TimeSpan.FromSeconds(30))).State);
        Assert.Equal((SteamAccountState.Held, "run-other"), Assert.Single(await Pool("steam_other").ListAsync(local, TimeSpan.FromSeconds(30))) is var other ? (other.State, other.Run) : default);
        Assert.Equal(SteamAccountLeaseState.Released, (await gone.ReleaseAsync()).State); // Its own release, were it ever to come, finds it released.
        var (records, _) = await RunJournalOnHost.ReadAllAsync(local, Path.Combine(data, "journal"), TimeSpan.FromSeconds(30));
        var released = Assert.Single(records, record => record.Run == "run-gone" && record.Entry.Kind == JournalEntry.LeaseReleased);
        Assert.Equal(("recovery", "true", "steam_gone"), (released.Actor, released.Entry.Fields["machineGone"], released.Entry.Fields["account"]));
        // Its end is journalled for it: env status shows it ended, not a run that may still be going.
        output = new StringWriter();
        await EnvCommand.RunAsync(["status", "--inventory", file], output, new StringWriter());
        Assert.DoesNotContain("run-gone", output.ToString());
        // Nothing of it is left to release a second time.
        output = new StringWriter();
        Assert.Equal(0, await EnvCommand.RunAsync(["teardown", "--run", "run-gone", "--machine-gone", "--inventory", file], output, new StringWriter()));
        Assert.Contains("Run run-gone holds no lease", output.ToString());
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
