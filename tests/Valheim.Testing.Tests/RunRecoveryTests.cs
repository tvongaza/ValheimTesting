using Valheim.Testing.Game;
using Xunit;
using static RunJournalStatusTests;

// env recover|teardown (#257 step 4): clears exactly what one run provably left, refuses anything it cannot prove.
public sealed class RunRecoveryTests : IDisposable
{
    private const string Prep = "/srv/runs/server/vt-prep-cut-server";
    private const string Characters = "/home/p/.config/unity3d/IronGate/Valheim/characters_local", UserData = "/home/p/.local/share/Steam/userdata";
    private readonly string _root = Directory.CreateTempSubdirectory("journal-recover-").FullName;
    private readonly FakeServerHost _host;

    public RunRecoveryTests() => _host = new FakeServerHost("pc", Path.Combine(_root, "pc"));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Dictionary<string, HostProfile> Hosts() => new() { ["pc"] = new HostProfile { Kind = "ssh", Lock = "/var/tmp/vt/lock" } };
    private Task<RecoveryReport> RecoverAsync(string run, bool teardown = false) =>
        RunRecovery.RecoverAsync(Hosts(), _ => _host, run, teardown, TimeSpan.FromSeconds(5));
    private Task<JournalStatusReport> StatusAsync() => RunJournalStatus.InspectAsync(Hosts(), _ => _host, TimeSpan.FromSeconds(5));

    // A run cut short after preparing its server copy, staging a character, leasing an account, taking the lock and starting its server.
    private void InterruptedRun(string run = "run-cut")
    {
        Directory.CreateDirectory(Path.Combine(_host.Local(Prep + "/runtime"), "BepInEx"));
        File.WriteAllText(Path.Combine(_host.Local(Prep + "/runtime"), "valheim_server.x86_64"), "game");
        Directory.CreateDirectory(_host.Local(Prep + "/staging"));
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", Prep + "/runtime"), ("stage", Prep + "/staging"), ("parent", Prep));
        Line(_host, run, "server", Gone, JournalEntry.CopyDone, ("runtime", Prep + "/runtime"), ("files", "1"));
        Line(_host, run, "player", Gone, JournalEntry.CharacterIntended, ("characters", Characters), ("userData", UserData), ("fileName", "vt01"));
        Line(_host, run, "player", Gone, JournalEntry.CharacterDone, ("fileName", "vt01"));
        _host.Leases["alt1"] = (run + " [b]", "lease-b");
        Line(_host, run, "player", Gone, JournalEntry.LeaseHeld, ("account", "alt1"), ("pool", "steam"), ("owner", run + " [b]"), ("expiresUtc", ""),
            ("leaseId", "lease-b"), ("number", "1"), ("directory", "/var/tmp/vt/leases"));
        _host.Claims.Add(run + " [c]");
        Line(_host, run, "run", Gone, JournalEntry.LockHeld, ("lock", "/var/tmp/vt/lock"), ("claimant", run + " [c]"));
        _host.Running(41, "9041");
        Line(_host, run, "server", Gone, JournalEntry.ProcessStarted, ("pid", "41"), ("startIdentity", "9041"),
            ("commandLineSha256", FakeServerHost.CommandLineSha256("41")), ("bootDirectory", "/srv/runs/cut/boot-1"));
    }

    [Fact] public async Task RecoverStopsTheRunsOwnServerRetiresItsCharacterAndCopyAndReleasesItsLeaseAndLock()
    {
        InterruptedRun();

        var report = await RecoverAsync("run-cut");

        Assert.Null(report.Refused);
        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Equal(JournalRunState.Recoverable, report.Before);
        Assert.Equal([("41", "9041")], _host.Stops);
        Assert.False(Directory.Exists(_host.Local(Prep)));
        Assert.Empty(_host.Leases);
        Assert.Equal(["run-cut [c]"], _host.Releases);
        // Under the run's own lock: no lock of its own was taken.
        Assert.Equal(["run-cut [c]"], _host.Claims);
        // Processes first, then the character before the copy, then the lease, then the lock.
        var runs = _host.Runs.ToList();
        int At(string script) => runs.FindIndex(run => run.Script == script && (script != "lease" || run.Variables["action"] == "release"));
        Assert.True(At("stop") < At("character-retire") && At("character-retire") < At("cleanup-stage") && At("cleanup-stage") < At("lease"));
        var after = Assert.Single((await StatusAsync()).Runs);
        Assert.Equal(JournalRunState.Ended, after.State);
        Assert.Contains("recovered by env recover/teardown", after.Reason);
        // A second recovery finds nothing left.
        Assert.True((await RecoverAsync("run-cut")).Recovered);
        Assert.Single(_host.Stops);
    }

    // The native case (#257, 5 Oct 2026): a server journalled as started and a client killed between process-intended and
    // process-started, with its pid file written. Recover stops both by identity, retires the rest, and the run is over.
    [Fact] public async Task RecoverStopsTheProcessALaunchsPidFileNamesAndTheRunEnds()
    {
        InterruptedRun();
        const string launch = "/srv/runs/run-cut/client-1";
        _host.Running(91152, "134357104986630869");
        Line(_host, "run-cut", "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", launch),
            ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("91152")));
        PidFile(_host, launch, "91152 134357104986630869");

        var report = await RecoverAsync("run-cut");

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Equal(JournalRunState.Recoverable, report.Before);
        Assert.Equal([("41", "9041"), ("91152", "134357104986630869")], _host.Stops.Order());
        Assert.Contains(report.Steps, step => step.What == "process 91152 (started 134357104986630869)" && step.Outcome == "stopped");
        var after = Assert.Single((await StatusAsync()).Runs);
        Assert.Equal(JournalRunState.Ended, after.State);
        Assert.Empty(after.Items);
        // The launch is settled in the journal: with its evidence folder gone later, the run stays over, its pid file unread.
        Directory.Delete(_host.Local(launch), recursive: true);
        int before = _host.Scripts.Count;
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
        Assert.DoesNotContain("pid-file", _host.Scripts.Skip(before));
    }

    // A pid file proves no more than it says: a process whose command line is not the launch's is never stopped and the run is
    // refused whole; an ID another process reused since is not the launch's and is left alone, the launch counting as gone.
    [Fact] public async Task RecoverNeverStopsAProcessAPidFileDoesNotProveTheLaunchs()
    {
        _host.Running(77, "9077");
        _host.CommandLines[77] = new string('b', 64);
        Line(_host, "run-other", "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", "/l/77"), ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("77")));
        PidFile(_host, "/l/77", "77 9077");
        _host.Running(71, "8000");
        Line(_host, "run-reused", "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", "/l/71"), ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("71")));
        PidFile(_host, "/l/71", "71 9071");
        Line(_host, "run-missing", "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", "/l/72"), ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("72")));

        var other = await RecoverAsync("run-other", teardown: true);
        Assert.Equal(JournalRunState.Unrecoverable, other.Before);
        Assert.Contains("runs another command line than the launch's", other.Refused);
        var missing = await RecoverAsync("run-missing", teardown: true);
        Assert.Equal(JournalRunState.Unrecoverable, missing.Before);
        Assert.Contains("does not exist", missing.Refused);
        var reused = await RecoverAsync("run-reused", teardown: true);
        Assert.True(reused.Recovered);
        // Nothing to stop: the launch is only settled, so no later status depends on its pid file.
        Assert.Equal("settled: its pid file's process is gone", Assert.Single(reused.Steps).Outcome);
        Assert.Empty(_host.Stops);
        Assert.DoesNotContain(_host.Scripts, script => script == "stop");
        Directory.Delete(_host.Local("/l/71"), recursive: true);
        Assert.Equal(JournalRunState.Ended, (await StatusAsync()).Runs.Single(run => run.Run == "run-reused").State);
    }

    [Fact] public async Task ALiveUnknownOrUnrecoverableRunIsRefusedAndNothingChanges()
    {
        Line(_host, "run-live", "server", JournalRunner.Current, JournalEntry.CopyIntended, ("runtime", Prep + "/runtime"), ("stage", Prep + "/staging"));
        Line(_host, "run-elsewhere", "server", new JournalRunner("another-machine-x", 1, DateTime.UtcNow), JournalEntry.CopyIntended, ("runtime", "/x/vt-prep-e-s/runtime"));
        _host.Running(53, "9053");
        _host.CommandLines[53] = new string('a', 64);
        Line(_host, "run-other-command", "server", Gone, JournalEntry.ProcessStarted, ("pid", "53"), ("startIdentity", "9053"),
            ("commandLineSha256", FakeServerHost.CommandLineSha256("53")));

        foreach (var (run, state) in new[] { ("run-live", JournalRunState.Live), ("run-elsewhere", JournalRunState.Unknown), ("run-other-command", JournalRunState.Unrecoverable),
                     ("run-nowhere", JournalRunState.Unknown) })
        {
            var report = await RecoverAsync(run, teardown: true);
            Assert.False(report.Recovered);
            Assert.Equal(state, report.Before);
            Assert.NotNull(report.Refused);
            Assert.Empty(report.Steps);
        }
        Assert.Contains("runs another command line", (await RecoverAsync("run-other-command")).Refused);
        Assert.Empty(_host.Stops);
        Assert.DoesNotContain(_host.Scripts, script => script is "stop" or "cleanup-stage" or "character-retire" or "journal");
    }

    [Fact] public async Task RecoverLeavesWhatARunKeptOnPurposeAndTeardownRemovesIt()
    {
        Directory.CreateDirectory(_host.Local(Prep + "/runtime"));
        Line(_host, "run-kept", "server", Gone, JournalEntry.CopyIntended, ("runtime", Prep + "/runtime"), ("stage", Prep + "/staging"), ("parent", Prep));
        Line(_host, "run-kept", "server", Gone, JournalEntry.CopyDone, ("runtime", Prep + "/runtime"));
        Line(_host, "run-kept", "server", Gone, JournalEntry.CopyKept, ("runtime", Prep + "/runtime"), ("why", "kept on request"));
        Line(_host, "run-kept", "run", Gone, JournalEntry.RunEnded, ("state", "passed"), ("cleanupVerified", "true"));

        var refused = await RecoverAsync("run-kept");
        Assert.Equal(JournalRunState.Kept, refused.Before);
        Assert.Contains("env teardown --run run-kept", refused.Refused);
        Assert.True(Directory.Exists(_host.Local(Prep + "/runtime")));

        var torn = await RecoverAsync("run-kept", teardown: true);
        Assert.True(torn.Recovered, string.Join("\n", torn.Steps));
        Assert.False(Directory.Exists(_host.Local(Prep)));
        // No lock of the run's was left, so the recovery took the host's lock for the retire and gave it back.
        Assert.Equal(["recover run-kept [fake]"], _host.Claims);
        Assert.Equal(["recover run-kept [fake]"], _host.Releases);
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
    }

    // Q2 at the moment of the stop: a process whose command line changed after env status read it is not stopped, and what it may use stays.
    [Fact] public async Task AProcessIsCheckedAgainRightBeforeItsStop()
    {
        InterruptedRun();
        _host.AfterProbe = () => { _host.CommandLines[41] = new string('b', 64); _host.AfterProbe = null; };

        var report = await RecoverAsync("run-cut");

        Assert.False(report.Recovered);
        Assert.Equal("not stopped: its ID, start time and command line no longer all match the journal",
            Assert.Single(report.Steps, step => step.What == "process 41 (started 9041)").Outcome);
        Assert.Empty(_host.Stops);
        Assert.True(Directory.Exists(_host.Local(Prep + "/runtime")));
        Assert.Empty(_host.Releases);
    }

    // Cleanup failure, then retry: a server that cannot be stopped keeps its copy, character and lock; the next recovery finishes.
    [Fact] public async Task AFailedStopKeepsWhatTheProcessMayUseAndASecondRecoveryFinishes()
    {
        InterruptedRun();
        _host.Failures["stop"] = FakeServerHost.TransportFailure;

        var first = await RecoverAsync("run-cut");

        Assert.False(first.Recovered);
        Assert.Contains(first.Steps, step => step.What == "process 41 (started 9041)" && step.Failed && step.Outcome.StartsWith("not stopped: "));
        Assert.Contains(first.Steps, step => step.What.StartsWith("copy ") && step.Outcome == "not attempted: a process on this host was not stopped");
        Assert.Contains(first.Steps, step => step.What.StartsWith("lock ") && step.Outcome.StartsWith("kept: "));
        Assert.True(Directory.Exists(_host.Local(Prep + "/runtime")));
        Assert.Empty(_host.Releases);
        var between = Assert.Single((await StatusAsync()).Runs);
        Assert.Equal(JournalRunState.Recoverable, between.State);
        Assert.Contains("recovered by env recover/teardown, but not all of it", between.Reason);

        _host.Failures.Remove("stop");
        var second = await RecoverAsync("run-cut");
        Assert.True(second.Recovered, string.Join("\n", second.Steps));
        Assert.False(Directory.Exists(_host.Local(Prep)));
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
    }

    // A lease outlives every process of its run: a client that could not be stopped on its host keeps the lease on the lease host.
    [Fact] public async Task ALeaseIsKeptWhileAClientOnAnotherHostWasNotStopped()
    {
        var leases = new FakeServerHost("leases", Path.Combine(_root, "leases"));
        leases.Leases["alt1"] = ("run-two [b]", "lease-b");
        // The lease host's entries are first in host order, so a host-by-host recovery would release it before the client stops.
        Line(leases, "run-two", "player", Gone, JournalEntry.LeaseHeld, ("account", "alt1"), ("pool", "steam"), ("owner", "run-two [b]"), ("expiresUtc", ""),
            ("leaseId", "lease-b"), ("number", "1"), ("directory", "/var/tmp/vt/leases"));
        _host.Running(77, "555");
        Line(_host, "run-two", "player", Gone, JournalEntry.ProcessStarted, ("pid", "77"), ("startIdentity", "555"),
            ("commandLineSha256", FakeServerHost.CommandLineSha256("77")), ("launchDirectory", "/l/1"));
        _host.Failures["stop"] = FakeServerHost.TransportFailure;
        var hosts = new Dictionary<string, HostProfile>
        {
            ["leases"] = new() { Kind = "ssh", Lock = "/var/tmp/vt/lock" }, ["pc"] = new() { Kind = "ssh", Lock = "/var/tmp/vt/lock" },
        };
        FakeServerHost Of(string name) => name == "pc" ? _host : leases;

        var first = await RunRecovery.RecoverAsync(hosts, Of, "run-two", false, TimeSpan.FromSeconds(5));
        Assert.False(first.Recovered);
        Assert.Equal("kept: a process of the run was not stopped", Assert.Single(first.Steps, step => step.What.StartsWith("lease ")).Outcome);
        Assert.True(leases.Leases.ContainsKey("alt1"));

        _host.Failures.Remove("stop");
        Assert.True((await RunRecovery.RecoverAsync(hosts, Of, "run-two", false, TimeSpan.FromSeconds(5))).Recovered);
        Assert.Empty(leases.Leases);
    }

    [Fact] public async Task AnotherRunsLockOrAGameRunningFromTheCopyKeepsTheCopy()
    {
        Directory.CreateDirectory(_host.Local(Prep + "/runtime"));
        Line(_host, "run-copy", "server", Gone, JournalEntry.CopyIntended, ("runtime", Prep + "/runtime"), ("stage", Prep + "/staging"), ("parent", Prep));
        _host.HeldBy = "run-going [z]";

        var locked = await RecoverAsync("run-copy");
        Assert.False(locked.Recovered);
        Assert.StartsWith("not attempted: ", Assert.Single(locked.Steps, step => step.What.StartsWith("copy ")).Outcome);
        Assert.True(Directory.Exists(_host.Local(Prep + "/runtime")));

        _host.HeldBy = null;
        _host.GameActive = true;
        var busy = await RecoverAsync("run-copy");
        Assert.False(busy.Recovered);
        Assert.True(Directory.Exists(_host.Local(Prep + "/runtime")));
    }

    [Fact] public async Task AHostThatCannotBeReadRefusesTheRecovery()
    {
        InterruptedRun();
        _host.Failures["journal-read-all"] = FakeServerHost.TransportFailure;
        var report = await RecoverAsync("run-cut");
        Assert.Contains("could not be read", report.Refused);
        Assert.Empty(_host.Stops);
    }

    // A process that made copies on this machine and died: recover removes its unfinished and runtime copies (keeping what
    // changed) and hands its world, a run's save, over to its output.
    [Fact] public async Task RecoverRemovesADeadProcesssLocalCopiesAndHandsOverItsSave()
    {
        string data = Path.Combine(_root, "data"), output = Path.Combine(_root, "output");
        string server = Path.Combine(_root, "source-server"), world = Path.Combine(_root, "source-world");
        Directory.CreateDirectory(server); File.WriteAllText(Path.Combine(server, GameLaunch.ServerWindowsExecutable), "game");
        Directory.CreateDirectory(Path.Combine(world, "worlds_local")); File.WriteAllText(Path.Combine(world, "worlds_local", "W.db"), "save");
        string runtime, save;
        using (RunJournal.UseLocalDirectory(Path.Combine(_root, "elsewhere", "journal")))
        {
            var a = WorldFixture.Copy(server, output, WorldFixture.Manifest(server)); a.Preserve = true; a.Dispose(); runtime = a.DirectoryPath;
            var b = WorldFixture.Copy(world, output, WorldFixture.Manifest(world)); b.Preserve = true; b.Dispose(); save = b.DirectoryPath;
        }
        File.WriteAllText(Path.Combine(runtime, "toolkit.log"), "written by the run");
        string unfinished = Path.Combine(output, "valheim-test-" + new string('b', 32));
        Directory.CreateDirectory(unfinished); File.WriteAllText(Path.Combine(unfinished, "half"), "x");
        // The dead process's journal on this machine: three copies started, two finished, none let go.
        string file = Path.Combine(data, "journal", "run-dead", WorldFixture.Actor + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        int minute = 0;
        foreach (var (kind, path) in new[] { (JournalEntry.CopyIntended, runtime), (JournalEntry.CopyDone, runtime), (JournalEntry.CopyIntended, save),
                     (JournalEntry.CopyDone, save), (JournalEntry.CopyIntended, unfinished) })
            File.AppendAllText(file, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["utc"] = DateTime.UtcNow.AddHours(-1).AddMinutes(minute++).ToString("O"), ["run"] = "run-dead", ["actor"] = WorldFixture.Actor, ["kind"] = kind,
                ["fields"] = new Dictionary<string, string> { ["runtime"] = path, ["local"] = "true" },
                ["runner"] = new Dictionary<string, object> { ["machine"] = Gone.Machine, ["pid"] = Gone.Pid, ["startedUtc"] = Gone.StartedUtc.ToString("O") },
            }) + "\n");
        var hosts = new Dictionary<string, HostProfile> { ["this-machine"] = new() { Kind = "local", Lock = Path.Combine(data, "lock") } };
        var local = OperatingSystem.IsWindows() ? new LocalGameHost("this-machine", HostShell.WindowsPowerShell) : new LocalGameHost("this-machine", HostShell.Bash);

        var report = await RunRecovery.RecoverAsync(hosts, _ => local, "run-dead", false, TimeSpan.FromSeconds(60));

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.False(Directory.Exists(runtime));
        Assert.Equal("written by the run", File.ReadAllText(Path.Combine(runtime + "-changes", "toolkit.log")));
        Assert.False(Directory.Exists(unfinished));
        Assert.True(File.Exists(Path.Combine(save, "worlds_local", "W.db")));
        Assert.Equal("kept as the run's save (handed over to its output)", Assert.Single(report.Steps, step => step.What == "copy " + save).Outcome);
        var after = Assert.Single((await RunJournalStatus.InspectAsync(hosts, _ => local, TimeSpan.FromSeconds(60))).Runs);
        Assert.Equal(JournalRunState.Ended, after.State);
    }

    [Fact] public async Task TheCommandLineRequiresARunAndSaysWhatItRefused()
    {
        foreach (string[] args in new[] { new[] { "recover" }, ["teardown", "--run"], ["recover", "--run", "x", "extra"], ["recover", "--hosts", "--run", "x"] })
            Assert.Equal(2, await EnvCommand.RunAsync(args, new StringWriter(), new StringWriter()));
        var error = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["recover", "--run", "../escape", "--inventory", Path.Combine(_root, "missing.json")], new StringWriter(), error));
        Assert.StartsWith("REFUSED: ", error.ToString());
    }
}
