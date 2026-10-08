using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;
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

    [Fact] public async Task ADeadOneShotRunRetiresOnlyItsMarkedRegressionInstall()
    {
        string data = Path.Combine(_root, "data"), journal = Path.Combine(data, "journal");
        string install = Path.Combine(_root, "regression-native-smoke-recovery"), evidence = Path.Combine(_root, "evidence");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(install, "BepInEx"));
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(install, TargetedRegression.MarkerFile), "{\"tool\":\"TargetedRegression\"}");
        File.WriteAllText(Path.Combine(install, "BepInEx", "LogOutput.log"), "game log from the interrupted run");
        string file = Path.Combine(journal, "run-regression", WorldFixture.Actor + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        foreach (string kind in new[] { JournalEntry.CopyIntended, JournalEntry.CopyDone })
            File.AppendAllText(file, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["utc"] = DateTime.UtcNow.AddHours(-1).ToString("O"), ["run"] = "run-regression", ["actor"] = WorldFixture.Actor, ["kind"] = kind,
                ["fields"] = new Dictionary<string, string> { ["runtime"] = install, ["local"] = "true", ["copyKind"] = "regression", ["evidenceRoot"] = evidence },
                ["runner"] = new Dictionary<string, object> { ["machine"] = Gone.Machine, ["pid"] = Gone.Pid, ["startedUtc"] = Gone.StartedUtc.ToString("O") },
            }) + "\n");
        using var localJournal = RunJournal.UseLocalDirectory(journal);
        var hosts = new Dictionary<string, HostProfile> { ["this-machine"] = new() { Kind = "local", Lock = Path.Combine(data, "lock") } };
        var local = OperatingSystem.IsWindows() ? new LocalGameHost("this-machine", HostShell.WindowsPowerShell) : new LocalGameHost("this-machine", HostShell.Bash);

        Assert.True((await RunRecovery.RecoverAsync(hosts, _ => local, "run-regression", false, TimeSpan.FromSeconds(60))).Recovered);
        Assert.False(Directory.Exists(install));
        Assert.Equal("game log from the interrupted run", File.ReadAllText(Path.Combine(evidence, "recovered-game-logs", "BepInEx", "LogOutput.log")));

        // A journal entry alone never authorizes deletion of an unmarked directory.
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "personal.txt"), "keep");
        string other = File.ReadAllText(file).Replace("run-regression", "run-unmarked", StringComparison.Ordinal);
        string otherFile = Path.Combine(journal, "run-unmarked", WorldFixture.Actor + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(otherFile)!);
        File.WriteAllText(otherFile, other);
        var refused = await RunRecovery.RecoverAsync(hosts, _ => local, "run-unmarked", false, TimeSpan.FromSeconds(60));
        Assert.False(refused.Recovered);
        Assert.True(File.Exists(Path.Combine(install, "personal.txt")));
    }

    // #412: the game's logs inside a copy are what explains why the run was interrupted. Recover keeps them in the run's
    // evidence folder on the host (beside its boot-N and client launch folders) before the copy goes, and names them. A log
    // older than the copy came with the install: it is named, not kept.
    [Fact] public async Task RecoverKeepsACopysGameLogsInTheRunsEvidenceFolderBeforeRemovingIt()
    {
        InterruptedRun();
        string runtime = _host.Local(Prep + "/runtime");
        File.WriteAllText(Path.Combine(runtime, "BepInEx", "LogOutput.log"), "[Info   :   BepInEx] Chainloader startup complete\n");
        File.WriteAllText(Path.Combine(runtime, "toolkit-unity.log"), "unity says why");
        File.WriteAllText(Path.Combine(runtime, "preloader_20261005_221500.log"), "[Error  : Preloader] it broke");
        File.WriteAllText(Path.Combine(runtime, "preloader_20260901_101500.log"), "an old crash of the install");
        File.SetLastWriteTimeUtc(Path.Combine(runtime, "preloader_20260901_101500.log"), DateTime.UtcNow.AddDays(-30));
        // A client's copy: its logs too, the host user's Player.log among them.
        const string ClientPrep = "/srv/runs/server/vt-prep-cut-client";
        Directory.CreateDirectory(Path.Combine(_host.Local(ClientPrep + "/runtime"), "BepInEx"));
        File.WriteAllText(Path.Combine(_host.Local(ClientPrep + "/runtime"), "BepInEx", "LogOutput.log"), "client log");
        Line(_host, "run-cut", "client", Gone, JournalEntry.CopyIntended, ("runtime", ClientPrep + "/runtime"), ("stage", ClientPrep + "/staging"), ("parent", ClientPrep));
        Line(_host, "run-cut", "client", Gone, JournalEntry.CopyDone, ("runtime", ClientPrep + "/runtime"));

        var report = await RecoverAsync("run-cut");

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.False(Directory.Exists(_host.Local(Prep)));
        Assert.False(Directory.Exists(_host.Local(ClientPrep)));
        string server = _host.Local("/srv/runs/server/run-cut/recovered-server"), client = _host.Local("/srv/runs/server/run-cut/recovered-client");
        Assert.Equal("[Info   :   BepInEx] Chainloader startup complete\n", File.ReadAllText(Path.Combine(server, "BepInEx", "LogOutput.log")));
        Assert.Equal("unity says why", File.ReadAllText(Path.Combine(server, "toolkit-unity.log")));
        Assert.True(File.Exists(Path.Combine(server, "preloader_20261005_221500.log")));
        Assert.False(File.Exists(Path.Combine(server, "preloader_20260901_101500.log")));
        Assert.Equal("client log", File.ReadAllText(Path.Combine(client, "BepInEx", "LogOutput.log")));
        Assert.Equal("removed; its game logs are in /srv/runs/server/run-cut/recovered-server (BepInEx/LogOutput.log, toolkit-unity.log, preloader_20261005_221500.log)" +
            "; left out as older than the copy: preloader_20260901_101500.log", Assert.Single(report.Steps, step => step.What == "copy " + Prep + "/runtime").Outcome);
        Assert.Equal("removed; its game logs are in /srv/runs/server/run-cut/recovered-client (BepInEx/LogOutput.log)",
            Assert.Single(report.Steps, step => step.What == "copy " + ClientPrep + "/runtime").Outcome);
        // Since the copy was made (its journalled start); the host user's Player.log only for a client.
        var keeps = _host.Runs.Where(run => run.Script == "keep-logs").ToList();
        Assert.Equal("false", Assert.Single(keeps, run => run.Variables["runtime"] == Prep + "/runtime").Variables["client"]);
        Assert.Equal("true", Assert.Single(keeps, run => run.Variables["runtime"] == ClientPrep + "/runtime").Variables["client"]);
        var copiedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(keeps[0].Variables["since"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(copiedAt, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddHours(-12)); // T0, the journal's time, not now.
        // Each log is kept before its copy is removed.
        var scripts = _host.Scripts.ToList();
        Assert.True(scripts.IndexOf("keep-logs") < scripts.IndexOf("cleanup-stage"));
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
    }

    // #257: a standalone hosted run's own copies, in its run directory: the runtime copy goes (its logs kept first), a world copy
    // verified and journalled done is the run's save and is handed over where it is, and a world ship that never finished goes.
    [Fact] public async Task RecoverRemovesAHostedRunsOwnRuntimeCopyAndPartialWorldAndHandsOverItsVerifiedWorld()
    {
        const string Run = "/srv/runs/run-hosted", Other = "/srv/runs/run-partial";
        Directory.CreateDirectory(Path.Combine(_host.Local(Run + "/runtime"), "BepInEx"));
        File.WriteAllText(Path.Combine(_host.Local(Run + "/runtime"), "BepInEx", "LogOutput.log"), "server log");
        Directory.CreateDirectory(_host.Local(Run + "/world/worlds_local"));
        Directory.CreateDirectory(_host.Local(Other + "/world/worlds_local"));
        foreach (var (run, directory, holds, done) in new[] { ("run-hosted", Run + "/runtime", "runtime", true), ("run-hosted", Run + "/world", "world", true), ("run-partial", Other + "/world", "world", false) })
        {
            string parent = directory[..directory.LastIndexOf('/')];
            Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", directory), ("stage", ""), ("parent", parent), ("holds", holds));
            if (done) Line(_host, run, "server", Gone, JournalEntry.CopyDone, ("runtime", directory), ("files", "1"), ("verified", "true"));
        }

        var hosted = await RecoverAsync("run-hosted");
        var partial = await RecoverAsync("run-partial");

        Assert.True(hosted.Recovered, string.Join("\n", hosted.Steps));
        Assert.True(partial.Recovered, string.Join("\n", partial.Steps));
        Assert.False(Directory.Exists(_host.Local(Run + "/runtime")));
        Assert.Equal("server log", File.ReadAllText(Path.Combine(_host.Local(Run + "/recovered-server"), "BepInEx", "LogOutput.log")));
        Assert.True(Directory.Exists(_host.Local(Run + "/world")));
        Assert.Equal("kept as the run's save (handed over, in its run directory)", Assert.Single(hosted.Steps, step => step.What == "copy " + Run + "/world").Outcome);
        Assert.False(Directory.Exists(_host.Local(Other)));
        Assert.Equal("removed (the ship never finished)", Assert.Single(partial.Steps, step => step.What == "copy " + Other + "/world").Outcome);
        Assert.All((await StatusAsync()).Runs, run => Assert.Equal(JournalRunState.Ended, run.State));
        // The run directory guards the removal: a copy outside its own run's directory is refused, and stays.
        Directory.CreateDirectory(_host.Local("/srv/runs/someone-else/runtime"));
        Line(_host, "run-stray", "server", Gone, JournalEntry.CopyIntended, ("runtime", "/srv/runs/someone-else/runtime"), ("stage", ""), ("parent", "/srv/runs/someone-else"), ("holds", "runtime"));
        Assert.False((await RecoverAsync("run-stray")).Recovered);
        Assert.True(Directory.Exists(_host.Local("/srv/runs/someone-else/runtime")));
    }

    // #258 step 8b: a hosted world a killed run left in a campaign client's own worlds_local is moved out into that run's
    // host-world folder (never deleted), with the game's backup beside it; the user's own world stays. A journal line that does
    // not name a worlds_local, or a keep folder outside this run's directory, is refused and nothing moves.
    [Fact] public async Task RecoverMovesAHostedWorldOutOfTheClientsWorldsIntoTheRunsFolder()
    {
        const string Worlds = "/home/p/.config/unity3d/IronGate/Valheim/worlds_local", KeepIn = "/srv/runs/run-host/host-world";
        Directory.CreateDirectory(_host.Local(Worlds + "/Campaign"));
        File.WriteAllText(_host.Local(Worlds + "/Campaign/_main.0.fwl2"), "world");
        File.WriteAllText(_host.Local(Worlds + "/Campaign_backup_auto-1.db"), "backup");
        File.WriteAllText(_host.Local(Worlds + "/MyWorld.fwl"), "the user's world");
        Line(_host, "run-host", "host", Gone, JournalEntry.CopyIntended, ("runtime", Worlds + "/Campaign"), ("stage", ""), ("parent", Worlds),
            ("holds", "hosted-world"), ("world", "Campaign"), ("keepIn", KeepIn));
        Line(_host, "run-host", "host", Gone, JournalEntry.CopyDone, ("runtime", Worlds + "/Campaign"), ("files", "1"), ("verified", "true"));

        var recovered = await RecoverAsync("run-host");

        Assert.True(recovered.Recovered, string.Join("\n", recovered.Steps));
        Assert.StartsWith("moved out of the client's worlds into " + KeepIn, Assert.Single(recovered.Steps, step => step.What == "copy " + Worlds + "/Campaign").Outcome);
        Assert.Equal(new[] { "MyWorld.fwl" }, Directory.EnumerateFileSystemEntries(_host.Local(Worlds)).Select(Path.GetFileName));
        Assert.Equal("world", File.ReadAllText(_host.Local(KeepIn + "/Campaign/_main.0.fwl2")));
        Assert.Equal("backup", File.ReadAllText(_host.Local(KeepIn + "/Campaign_backup_auto-1.db")));
        Assert.All((await StatusAsync()).Runs, run => Assert.Equal(JournalRunState.Ended, run.State));

        // Refused: a folder that is not a worlds_local, and a keep folder outside the run's directory. Nothing moves.
        foreach (var (run, parent, keepIn) in new[] { ("run-odd", "/home/p/Documents", "/srv/runs/run-odd/host-world"), ("run-far", Worlds, "/srv/elsewhere/host-world") })
        {
            Directory.CreateDirectory(_host.Local(parent + "/Other"));
            Line(_host, run, "host", Gone, JournalEntry.CopyIntended, ("runtime", parent + "/Other"), ("stage", ""), ("parent", parent),
                ("holds", "hosted-world"), ("world", "Other"), ("keepIn", keepIn));
            Assert.False((await RecoverAsync(run)).Recovered);
            Assert.True(Directory.Exists(_host.Local(parent + "/Other")));
        }
    }

    // A copy whose logs could not be kept stays, for a later recovery; teardown keeps a kept copy's logs as recover does.
    [Fact] public async Task ACopyWhoseLogsCannotBeKeptStaysAndTeardownKeepsAKeptCopysLogs()
    {
        InterruptedRun();
        File.WriteAllText(Path.Combine(_host.Local(Prep + "/runtime"), "BepInEx", "LogOutput.log"), "why");
        _host.Failures["keep-logs"] = FakeServerHost.TransportFailure;
        var failed = await RecoverAsync("run-cut");
        Assert.False(failed.Recovered);
        Assert.StartsWith("not removed: its game logs could not be kept, so it stays", Assert.Single(failed.Steps, step => step.What == "copy " + Prep + "/runtime").Outcome);
        Assert.True(File.Exists(Path.Combine(_host.Local(Prep + "/runtime"), "BepInEx", "LogOutput.log")));
        Assert.DoesNotContain("cleanup-stage", _host.Scripts);
        _host.Failures.Remove("keep-logs");
        Assert.True((await RecoverAsync("run-cut")).Recovered);
        Assert.Equal("why", File.ReadAllText(_host.Local("/srv/runs/server/run-cut/recovered-server/BepInEx/LogOutput.log")));

        const string KeptPrep = "/srv/runs/server/vt-prep-kept-server";
        Directory.CreateDirectory(_host.Local(KeptPrep + "/runtime/BepInEx"));
        File.WriteAllText(_host.Local(KeptPrep + "/runtime/BepInEx/LogOutput.log"), "kept run");
        Line(_host, "run-kept", "server", Gone, JournalEntry.CopyIntended, ("runtime", KeptPrep + "/runtime"), ("stage", KeptPrep + "/staging"), ("parent", KeptPrep));
        Line(_host, "run-kept", "server", Gone, JournalEntry.CopyKept, ("runtime", KeptPrep + "/runtime"), ("why", "kept on request"));
        Line(_host, "run-kept", "run", Gone, JournalEntry.RunEnded, ("state", "passed"), ("cleanupVerified", "true"));
        Assert.NotNull((await RecoverAsync("run-kept")).Refused);
        Assert.False(Directory.Exists(_host.Local("/srv/runs/server/run-kept"))); // Recover left the kept copy alone.
        Assert.True((await RecoverAsync("run-kept", teardown: true)).Recovered);
        Assert.Equal("kept run", File.ReadAllText(_host.Local("/srv/runs/server/run-kept/recovered-server/BepInEx/LogOutput.log")));
    }

    // The real scripts on this machine (PowerShell on Windows, bash elsewhere): a copy's logs written since it was made are kept
    // in the run's folder with their names, older ones are only named, a client's Player.log is kept the same way, and a copy
    // that holds no log of the run makes no folder. Only a prepared copy's logs are kept.
    [Fact] public async Task TheRealKeepScriptKeepsOnlyLogsWrittenSinceTheCopyWasMade()
    {
        var local = OperatingSystem.IsWindows() ? new LocalGameHost("pc", HostShell.WindowsPowerShell) : new LocalGameHost("pc", HostShell.Bash);
        string runs = Path.Combine(_root, "runs", "env"), runtime = Path.Combine(runs, "vt-prep-run-real-client", "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "BepInEx"));
        File.WriteAllText(Path.Combine(runtime, "BepInEx", "LogOutput.log"), "bepinex");
        File.WriteAllText(Path.Combine(runtime, "preloader_1.log"), "old preloader");
        string player = Path.Combine(_root, "profile", "Player.log");
        Directory.CreateDirectory(Path.GetDirectoryName(player)!); File.WriteAllText(player, "player");
        var copied = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(Path.Combine(runtime, "preloader_1.log"), copied.AddDays(-3)); // Came with the install.

        var logs = await RunRecovery.KeepLogsAsync(local, runtime, "run-real", "client", copied, TimeSpan.FromSeconds(60), playerLog: player);
        Assert.Equal(Path.Combine(runs, "run-real", "recovered-client"), logs.Folder);
        Assert.Equal(["BepInEx/LogOutput.log", "Player.log"], logs.Kept);
        Assert.Equal(["preloader_1.log"], logs.Older);
        Assert.Equal("bepinex", File.ReadAllText(Path.Combine(logs.Folder, "BepInEx", "LogOutput.log")));
        Assert.Equal("player", File.ReadAllText(Path.Combine(logs.Folder, "Player.log")));
        Assert.False(File.Exists(Path.Combine(logs.Folder, "preloader_1.log")));

        File.SetLastWriteTimeUtc(player, copied.AddHours(-1)); // An earlier client's.
        string server = Path.Combine(runs, "vt-prep-run-real2-server", "runtime");
        Directory.CreateDirectory(server); File.WriteAllText(Path.Combine(server, "toolkit-unity.log"), "unity");
        logs = await RunRecovery.KeepLogsAsync(local, server, "run-real2", "client", copied, TimeSpan.FromSeconds(60), playerLog: player);
        Assert.Equal(["toolkit-unity.log"], logs.Kept);
        Assert.Equal(["Player.log"], logs.Older);

        string empty = Directory.CreateDirectory(Path.Combine(runs, "vt-prep-run-empty-server", "runtime")).FullName;
        logs = await RunRecovery.KeepLogsAsync(local, empty, "run-empty", "server", copied, TimeSpan.FromSeconds(60));
        Assert.Empty(logs.Kept);
        Assert.False(Directory.Exists(logs.Folder));
        await Assert.ThrowsAsync<ArgumentException>(() => RunRecovery.KeepLogsAsync(local, Path.Combine(runs, "elsewhere", "runtime"), "run-x", "server", copied, TimeSpan.FromSeconds(60)));
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
