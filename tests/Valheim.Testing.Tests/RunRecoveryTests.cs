using Valheim.Testing.Game.Fakes;
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
    private Task<RecoveryReport> RecoverAsync(string run, bool teardown = false, bool launchGone = false) =>
        RunRecovery.RecoverAsync(Hosts(), _ => _host, run, teardown, TimeSpan.FromSeconds(5), launchGone: launchGone);
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

    // #800: a server launch whose controller died before its pid file existed is named by the launcher's ownership record, written
    // before the game was spawned. Recovery retires that exact launcher and stops the game it recorded, each proven by ID and start
    // time (the game by its command line too). A record whose launcher ID now belongs to another start is never acted on, and one
    // whose game ID was reused proves the launch gone.
    [Fact]
    public async Task ALauncherOwnershipRecordRecoversALaunchWithoutAPidFileAndNeverAReusedIdentity()
    {
        string owner = HostServerLaunchOwnership.Create(ClientPlatform.Linux).FileName;
        void Launch(string run, string record)
        {
            string directory = "/srv/runs/" + run + "/boot-1";
            Line(_host, run, "server", Gone, JournalEntry.ProcessIntended, ("bootDirectory", directory),
                ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("82")), ("quitSeconds", "0"),
                ("launcherOwnershipFile", owner), ("launcherTask", ""));
            Directory.CreateDirectory(_host.Local(directory));
            File.WriteAllText(Path.Combine(_host.Local(directory), owner), record);
        }
        _host.Running(81, "9081");
        _host.Running(82, "9082");
        Launch("run-owned", "unix 81 9081 81 81 82 9082\n");
        _host.Running(83, "8000");                                      // the recorded launcher's ID, started at another time
        Launch("run-launcher-reused", "unix 83 9083 83 83\n");
        _host.Running(86, "7000");                                      // launcher gone; the recorded game's ID reused
        Launch("run-game-reused", "unix 85 9085 85 85 86 9086\n");

        var status = await StatusAsync();
        var owned = Assert.Single(Assert.Single(status.Runs, run => run.Run == "run-owned").Items);
        Assert.Equal(("launcher", "81 (started 9081)", false), (owned.Kind, owned.What, owned.Unrecoverable));
        var reused = Assert.Single(status.Runs, run => run.Run == "run-launcher-reused");
        Assert.Equal(JournalRunState.Unrecoverable, reused.State);
        Assert.Contains("gone without recording whether it started the game", Assert.Single(reused.Items).Status);
        Assert.Equal(JournalRunState.Ended, Assert.Single(status.Runs, run => run.Run == "run-game-reused").State);

        var report = await RecoverAsync("run-owned");
        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Contains(report.Steps, step => step.What == "launcher 81 (started 9081)" && !step.Failed &&
            step.Outcome == "stopped; its recorded game was stopped");
        Assert.False((await RecoverAsync("run-launcher-reused")).Recovered);
        Assert.Equal([("82", "9082")], _host.Stops);
        Assert.Equal(["81"], _host.Runs.Where(call => call.Script == "launcher-retire").Select(call => call.Variables["launcherPid"]));
        Assert.True(_host.IsRunning(83) && _host.IsRunning(86), "a reused ID's process was stopped");
    }

    // #814: a Windows server launch whose controller died after the pid file appeared is recovered through that pid file. Its
    // launcher still holds the start task (it waits for the controller's acknowledgement), so once the game is stopped recovery
    // retires the launcher and the exact task its ownership record names, through the launcher's one owner. A record that names
    // another game than the pid file is not taken as the launch's launcher: its game is stopped, its launcher never touched.
    [Fact]
    public async Task APidFileRecoveryAlsoRetiresTheWindowsLauncherAndItsStartTask()
    {
        var win = new FakeServerHost("pc", Path.Combine(_root, "win"), windows: true);
        var ownership = HostServerLaunchOwnership.Create(ClientPlatform.Windows);
        string Launch(string run, string game, string record)
        {
            string boot = @"C:\vt\runs\" + run + @"\boot-1";
            Line(win, run, "server", Gone, JournalEntry.ProcessIntended, ("bootDirectory", boot),
                ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256(game)), ("quitSeconds", "0"),
                ("launcherOwnershipFile", ownership.FileName), ("launcherTask", ownership.TaskName));
            PidFile(win, boot, $"{game} 90{game}");
            File.WriteAllText(Path.Combine(win.Local(boot), ownership.FileName), record);
            return boot;
        }
        win.Running(81, "9081");
        win.Running(82, "9082");
        string boot = Launch("run-pid-file", "82", $"windows {ownership.TaskName} 81 9081 1 82 9082");
        win.Running(83, "9083");
        win.Running(84, "9084");
        Launch("run-other-game", "84", $"windows {ownership.TaskName} 83 9083 1 85 9085");
        Task<RecoveryReport> Recover(string run) => RunRecovery.RecoverAsync(Hosts(), _ => win, run, false, TimeSpan.FromSeconds(5));

        var report = await Recover("run-pid-file");

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Contains(report.Steps, step => step.What == "process 82 (started 9082)" && step.Outcome == "stopped");
        Assert.Contains(report.Steps, step => step.What == "launcher 81 (started 9081)" && !step.Failed &&
            step.Outcome == $"stopped; its start task {ownership.TaskName} removed if it was left");
        var retire = Assert.Single(win.Runs, call => call.Script == "launcher-retire");
        Assert.Equal(("81", "9081", "1", ownership.TaskName, boot), (retire.Variables["launcherPid"], retire.Variables["launcherStart"],
            retire.Variables["launcherSession"], retire.Variables["task"], retire.Variables["dir"]));
        Assert.False(win.IsRunning(81) || win.IsRunning(82), "the game or its launcher was left running");
        Assert.Equal(JournalRunState.Ended, Assert.Single((await RunJournalStatus.InspectAsync(Hosts(), _ => win, TimeSpan.FromSeconds(5))).Runs,
            run => run.Run == "run-pid-file").State);

        Assert.True((await Recover("run-other-game")).Recovered);
        Assert.Single(win.Runs, call => call.Script == "launcher-retire");
        Assert.True(win.IsRunning(83), "a launcher whose record names another game was stopped");
        Assert.False(win.IsRunning(84));
    }

    [Fact]
    public async Task RegressionCharacterIntentWithoutCompletionCannotDeleteASameNamedSave()
    {
        const string run = "run-character-intent";
        string personal = _host.Local(Characters + "/vt01.fch");
        Directory.CreateDirectory(Path.GetDirectoryName(personal)!);
        File.WriteAllText(personal, "personal save");
        Line(_host, run, "client", Gone, JournalEntry.CharacterIntended,
            ("characters", Characters), ("userData", UserData), ("fileName", "vt01"),
            ("characterKind", "regression"), ("local", "true"),
            ("expectedSha256", FileHash.Sha256(System.Text.Encoding.UTF8.GetBytes("registered save"))));

        var report = await RecoverAsync(run);

        Assert.False(report.Recovered);
        Assert.Contains(report.Steps, step => step.What.StartsWith("character vt01", StringComparison.Ordinal) &&
            step.Failed && step.Outcome.Contains("differs from its journalled SHA-256", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Runs, call => call.Script == "character-retire");
        Assert.Equal("personal save", File.ReadAllText(personal));
        Assert.Equal(JournalRunState.Recoverable, Assert.Single((await StatusAsync()).Runs).State);
    }

    [Fact]
    public async Task RegressionIntentIgnoresUnrelatedLinkedPersonalCharacters()
    {
        if (OperatingSystem.IsWindows()) return; // local Bash host and symlink creation need no Windows privilege here
        const string run = "run-character-unrelated-link";
        string data = Path.Combine(_root, "local-data");
        string characters = Path.Combine(data, "characters_local");
        string userData = Path.Combine(data, "userdata");
        Directory.CreateDirectory(characters);
        Directory.CreateDirectory(userData);
        string personal = Path.Combine(data, "personal.fch");
        File.WriteAllText(personal, "personal save");
        File.CreateSymbolicLink(Path.Combine(characters, "personal-link.fch"), personal);
        string journal = Path.Combine(data, "journal", run, "client.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["utc"] = DateTime.UtcNow.AddMinutes(-1).ToString("O"), ["run"] = run, ["actor"] = "client",
            ["kind"] = JournalEntry.CharacterIntended,
            ["fields"] = new Dictionary<string, string>
            {
                ["characters"] = characters, ["userData"] = userData, ["fileName"] = "vt01",
                ["characterKind"] = "regression", ["local"] = "true",
                ["expectedSha256"] = FileHash.Sha256(System.Text.Encoding.UTF8.GetBytes("registered save")),
            },
            ["runner"] = new { machine = Gone.Machine, pid = Gone.Pid, startedUtc = Gone.StartedUtc.ToString("O") },
        }) + "\n");
        var hosts = new Dictionary<string, HostProfile> { ["local"] = new() { Kind = "local", Lock = Path.Combine(data, "lock") } };
        var host = new LocalGameHost("local", HostShell.Bash);

        var recovered = await RunRecovery.RecoverAsync(hosts, _ => host, run, false, TimeSpan.FromSeconds(10));

        Assert.True(recovered.Recovered, string.Join("\n", recovered.Steps));
        Assert.Equal("personal save", File.ReadAllText(personal));
        Assert.True(File.Exists(Path.Combine(characters, "personal-link.fch")));
    }

    [Fact]
    public async Task HostedCharacterIntentCannotDeleteAPersonalSaveWithTheSameName()
    {
        const string run = "run-hosted-character-intent";
        string personal = _host.Local(Characters + "/vt01.fch");
        Directory.CreateDirectory(Path.GetDirectoryName(personal)!);
        File.WriteAllText(personal, "personal save");
        Line(_host, run, "client", Gone, JournalEntry.CharacterIntended,
            ("characters", Characters), ("userData", UserData), ("fileName", "vt01"),
            ("characterKind", "hosted"));

        var report = await RecoverAsync(run);

        Assert.False(report.Recovered);
        Assert.DoesNotContain(_host.Runs, call => call.Script == "character-retire");
        Assert.Equal("personal save", File.ReadAllText(personal));
    }

    [Theory]
    [InlineData("regression", false)]
    [InlineData("regression", true)]
    [InlineData("hosted", false)]
    public async Task CharacterIntentCanRecoverWhenFileIsMissingOrMatchesItsSource(string kind, bool copyFinished)
    {
        const string run = "run-character-intent-recoverable";
        byte[] source = System.Text.Encoding.UTF8.GetBytes("registered save");
        string local = _host.Local(Characters);
        Directory.CreateDirectory(local);
        if (copyFinished) File.WriteAllBytes(Path.Combine(local, "vt01.fch"), source);
        var fields = new List<(string, string)>
        {
            ("characters", Characters), ("userData", UserData), ("fileName", "vt01"),
            ("characterKind", kind), ("local", "true"),
        };
        if (kind == "regression") fields.Add(("expectedSha256", FileHash.Sha256(source)));
        Line(_host, run, "client", Gone, JournalEntry.CharacterIntended, [.. fields]);

        var report = await RecoverAsync(run);

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.False(File.Exists(Path.Combine(local, "vt01.fch")));
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
        Assert.Equal(copyFinished, _host.Runs.Any(call => call.Script == "character-retire"));
    }

    [Fact]
    public async Task IncompleteHostedIntentNeverDeletesEvenAnIdenticalSave()
    {
        const string run = "run-hosted-identical";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("registered save");
        string local = _host.Local(Characters);
        Directory.CreateDirectory(local);
        File.WriteAllBytes(Path.Combine(local, "vt01.fch"), bytes);
        Line(_host, run, "client", Gone, JournalEntry.CharacterIntended,
            ("characters", Characters), ("userData", UserData), ("fileName", "vt01"),
            ("characterKind", "hosted"));

        var report = await RecoverAsync(run);

        Assert.False(report.Recovered);
        Assert.DoesNotContain(_host.Runs, call => call.Script == "character-retire");
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(local, "vt01.fch")));
    }

    [Fact]
    public async Task OlderHostedIntentWithoutAHashClearsWhenNoSaveExists()
    {
        const string run = "run-hosted-old-intent";
        Line(_host, run, "client", Gone, JournalEntry.CharacterIntended,
            ("characters", Characters), ("userData", UserData), ("fileName", "vt01"));

        var report = await RecoverAsync(run);

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.DoesNotContain(_host.Runs, call => call.Script == "character-retire");
    }

    [Theory]
    [InlineData("vt01.fch.old")]
    [InlineData("VT01.FCH.OLD")]
    [InlineData("vt01_backup_auto-2026")]
    public async Task RegressionCharacterIntentRefusesAnUnexpectedBackupEvenWhenTheMainFileMatches(string backupName)
    {
        const string run = "run-character-intent-extra";
        byte[] source = System.Text.Encoding.UTF8.GetBytes("registered save");
        string local = _host.Local(Characters);
        Directory.CreateDirectory(local);
        File.WriteAllBytes(Path.Combine(local, "vt01.fch"), source);
        File.WriteAllText(Path.Combine(local, backupName), "personal backup");
        Line(_host, run, "client", Gone, JournalEntry.CharacterIntended,
            ("characters", Characters), ("userData", UserData), ("fileName", "vt01"),
            ("characterKind", "regression"), ("local", "true"), ("expectedSha256", FileHash.Sha256(source)));

        var report = await RecoverAsync(run);

        Assert.False(report.Recovered);
        Assert.DoesNotContain(_host.Runs, call => call.Script == "character-retire");
        Assert.True(File.Exists(Path.Combine(local, "vt01.fch")));
        Assert.Equal("personal backup", File.ReadAllText(Path.Combine(local, backupName)));
    }

    [Theory]
    [InlineData("VT01.FCH.OLD")]
    [InlineData("VT01_backup_auto-2026")]
    public async Task RealHostListingFindsCaseVariantBackupsWithoutReadingUnrelatedCharacters(string backupName)
    {
        string local = Path.Combine(_root, "listing", "characters_local");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "vt01.fch"), "registered");
        File.WriteAllText(Path.Combine(local, backupName), "unexpected backup");
        if (!OperatingSystem.IsWindows())
            File.CreateSymbolicLink(Path.Combine(local, "personal.fch"), Path.Combine(local, "missing-personal.fch"));
        else File.WriteAllText(Path.Combine(local, "personal.fch"), "personal");
        var host = new LocalGameHost("this machine", HostShell.ThisMachine);

        var listing = await HostInstall.ListCaseInsensitiveAsync(host, local, TimeSpan.FromSeconds(10),
            DisposableCharacterStore.OwnedFilePatterns("vt01", HostInstall.LiteralPattern));

        Assert.Equal(2, listing.Files.Count);
        Assert.Contains(listing.Files.Keys, name => name.Equals("vt01.fch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(listing.Files.Keys, name => name.Equals(backupName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompletedRegressionCharacterCanBeRetiredByRecovery()
    {
        const string run = "run-character-done";
        Line(_host, run, "client", Gone, JournalEntry.CharacterIntended,
            ("characters", Characters), ("userData", UserData), ("fileName", "vt01"),
            ("characterKind", "regression"), ("local", "true"));
        Line(_host, run, "client", Gone, JournalEntry.CharacterDone,
            ("fileName", "vt01"), ("staged", "true"));

        var report = await RecoverAsync(run);

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Contains(_host.Runs, call => call.Script == "character-retire");
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
    }

    [Fact]
    public async Task InterruptedPlainStartListsAndRecoversItsHostedRuntimeCopy()
    {
        const string run = "run-plain-start";
        const string parent = "/srv/runs/client/vt-prep-regression-plain";
        string runtime = parent + "/runtime";
        Directory.CreateDirectory(Path.Combine(_host.Local(runtime), "BepInEx"));
        File.WriteAllText(Path.Combine(_host.Local(runtime), "BepInEx", "LogOutput.log"), "game log");
        Line(_host, run, "regression-plain", Gone, JournalEntry.CopyIntended,
            ("runtime", runtime), ("stage", parent + "/staging"), ("parent", parent));
        Line(_host, run, "regression-plain", Gone, JournalEntry.CopyDone, ("runtime", runtime));
        Line(_host, run, "client", Gone, JournalEntry.CharacterIntended,
            ("characters", Characters), ("userData", UserData), ("fileName", "vt01"),
            ("characterKind", "regression"), ("local", "true"));
        Line(_host, run, "client", Gone, JournalEntry.CharacterDone, ("fileName", "vt01"), ("staged", "true"));

        var before = await StatusAsync();
        Assert.Equal(JournalRunState.Recoverable, Assert.Single(before.Runs).State);
        Assert.Contains(Assert.Single(before.Runs).Items, item => item.Kind == "copy" && item.What == runtime);
        var recovered = await RecoverAsync(run);

        Assert.True(recovered.Recovered, string.Join("\n", recovered.Steps));
        Assert.False(Directory.Exists(_host.Local(parent)));
        Assert.Contains(_host.Runs, call => call.Script == "character-retire");
        Assert.Empty(Assert.Single((await StatusAsync()).Runs).Items);
    }

    // #612: a run killed after its server launched leaves its profile and the journalled source baseline. Recovery settles the
    // install from that baseline before it retires the copy: what the game added is moved into the run's recovered-<actor> folder
    // with its source-changes.json, the install lists as before, and the step names it. A baseline whose hash differs from the
    // journal's is refused, and nothing is moved or removed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverySettlesTheSourceInstallFromItsJournalledBaseline(bool tampered)
    {
        const string run = "run-src", source = "/srv/game", runtime = Prep + "/runtime";
        Directory.CreateDirectory(_host.Local(source));
        File.WriteAllText(Path.Combine(_host.Local(source), "valheim_server.x86_64"), "game");
        Directory.CreateDirectory(Path.Combine(_host.Local(runtime), "BepInEx"));
        Directory.CreateDirectory(_host.Local(Prep + "/staging"));
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", runtime), ("stage", Prep + "/staging"), ("parent", Prep),
            ("runtimeKind", "server"), ("launchRoot", source));
        Line(_host, run, "server", Gone, JournalEntry.CopyDone, ("runtime", runtime), ("files", "1"));
        var check = ProfileSourceCheck.Prepare(await ProfileSourceCheck.InspectAsync(_host, source, TimeSpan.FromSeconds(5)), [], ProfileLayout.SourceExecutable);
        await check.PersistAsync(_host, runtime, "server", entry =>
        {
            Line(_host, run, "server", Gone, entry.Kind, [.. entry.Fields.Select(field => (field.Key, field.Value))]);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(5));
        // The killed game wrote beside its executable and made a folder.
        File.WriteAllText(Path.Combine(_host.Local(source), "preloader_20261020.log"), "the game's preloader log");
        Directory.CreateDirectory(Path.Combine(_host.Local(source), "logs"));
        File.WriteAllText(Path.Combine(_host.Local(source), "logs", "stats.txt"), "written by the game");
        if (tampered) File.AppendAllText(_host.Local(Prep + "/" + ProfileSourceCheck.BaselineFile), " ");

        var recovered = await RecoverAsync(run);

        string recoveredIn = "/srv/runs/server/" + run + "/recovered-server";
        if (tampered)
        {
            Assert.False(recovered.Recovered);
            Assert.Contains("differs from its journalled SHA-256", Assert.Single(recovered.Steps, step => step.What == "copy " + runtime).Outcome);
            Assert.True(File.Exists(Path.Combine(_host.Local(source), "preloader_20261020.log")));
            Assert.True(Directory.Exists(_host.Local(runtime)));
            Assert.False(Directory.Exists(_host.Local(recoveredIn)));
            Assert.DoesNotContain(_host.Runs, call => call.Script is "source-settle" or "cleanup-stage");
            return;
        }
        Assert.True(recovered.Recovered, string.Join("\n", recovered.Steps));
        var settled = Assert.Single(recovered.Steps, step => step.What.StartsWith("source install of server", StringComparison.Ordinal));
        Assert.Contains("preloader_20261020.log", settled.Outcome);
        Assert.Contains(recoveredIn, settled.Outcome);
        Assert.Equal("the game's preloader log", File.ReadAllText(Path.Combine(Assert.Single(Directory.GetDirectories(_host.Local(recoveredIn), "source-changes-*")), "added", "preloader_20261020.log")));
        Assert.Equal("written by the game", File.ReadAllText(Path.Combine(Assert.Single(Directory.GetDirectories(_host.Local(recoveredIn), "source-changes-*")), "added", "logs", "stats.txt")));
        Assert.Contains("\"verdict\": \"restored\"", File.ReadAllText(Path.Combine(Assert.Single(Directory.GetDirectories(_host.Local(recoveredIn), "source-changes-*")), "source-changes.json")));
        Assert.Equal(["valheim_server.x86_64"], Directory.EnumerateFileSystemEntries(_host.Local(source)).Select(Path.GetFileName));
        Assert.False(Directory.Exists(_host.Local(Prep))); // the copy, its baseline and the moved files' folder are gone
        Assert.Empty(Assert.Single((await StatusAsync()).Runs).Items);
    }

    [Fact]
    public async Task InterruptedPlainStartRecoversItsPartialHostedCopy()
    {
        const string run = "run-partial-plain-start";
        const string parent = "/srv/runs/client/vt-prep-regression-partial";
        string runtime = parent + "/runtime";
        Directory.CreateDirectory(_host.Local(runtime));
        File.WriteAllText(Path.Combine(_host.Local(runtime), "unfinished-copy"), "partial");
        Line(_host, run, "regression-partial", Gone, JournalEntry.CopyIntended,
            ("runtime", runtime), ("stage", parent + "/staging"), ("parent", parent));

        var recovered = await RecoverAsync(run);

        Assert.True(recovered.Recovered, string.Join("\n", recovered.Steps));
        Assert.False(Directory.Exists(_host.Local(parent)));
    }

    [Fact]
    public async Task AJournalledCopyOutsideTheHostedRuntimeShapeIsNeverRemoved()
    {
        const string run = "run-foreign-copy";
        const string parent = "/srv/runs/client/personal";
        string runtime = parent + "/runtime";
        Directory.CreateDirectory(_host.Local(runtime));
        File.WriteAllText(Path.Combine(_host.Local(runtime), "keep-me"), "personal");
        Line(_host, run, "regression-plain", Gone, JournalEntry.CopyIntended,
            ("runtime", runtime), ("stage", parent + "/staging"), ("parent", parent));

        var recovered = await RecoverAsync(run);

        Assert.False(recovered.Recovered);
        Assert.True(File.Exists(Path.Combine(_host.Local(runtime), "keep-me")));
    }

    [Fact]
    public async Task CompletedPlainStartLeavesNoRecoverableCopyCharacterOrProcess()
    {
        const string run = "run-plain-complete";
        const string copy = "/srv/runs/client/vt-prep-regression-complete/runtime";
        Line(_host, run, "regression-complete", Gone, JournalEntry.CopyIntended,
            ("runtime", copy), ("stage", "/srv/runs/client/vt-prep-regression-complete/staging"));
        Line(_host, run, "client", Gone, JournalEntry.CharacterIntended,
            ("characters", Characters), ("userData", UserData), ("fileName", "vt01"),
            ("characterKind", "regression"), ("local", "true"));
        Line(_host, run, "client", Gone, JournalEntry.CharacterDone, ("fileName", "vt01"), ("staged", "true"));
        Line(_host, run, "client", Gone, JournalEntry.CharacterRetired, ("fileName", "vt01"));
        Line(_host, run, "regression-complete", Gone, JournalEntry.CopyRetired, ("runtime", copy));
        Line(_host, run, "run", Gone, JournalEntry.RunEnded, ("state", "passed"), ("cleanupVerified", "true"));

        var status = await StatusAsync();

        Assert.True(status.Clean);
        Assert.Equal(JournalRunState.Ended, Assert.Single(status.Runs).State);
        Assert.Empty(Assert.Single(status.Runs).Items);
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
        Assert.Contains(report.Steps, step => step.What == "process 91152 (started 134357104986630869)" && step.Outcome == "quit when asked");
        var after = Assert.Single((await StatusAsync()).Runs);
        Assert.Equal(JournalRunState.Ended, after.State);
        Assert.Empty(after.Items);
        // The launch is settled in the journal: with its evidence folder gone later, the run stays over, its pid file unread.
        Directory.Delete(_host.Local(launch), recursive: true);
        int before = _host.Scripts.Count;
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
        Assert.DoesNotContain("pid-file", _host.Scripts.Skip(before));
    }

    // #665: recovery stops each process as its own run would have: asked to quit first, given the quit budget its boot journalled
    // (the plan's QuitSeconds, here more than the default) or the server plan's default, and killed only after that; a server
    // as the server's stop asks it, a client as its session asks it, and on macOS both by the one Mac stop, which checks the
    // journalled command line again itself.
    [Theory]
    [InlineData("linux")]
    [InlineData("macos")]
    [InlineData("windows")]
    public async Task RecoveryAsksEachProcessToQuitWithTheBudgetItsBootJournalled(string platform)
    {
        const string run = "run-quit-budget";
        var host = new FakeServerHost("pc", Path.Combine(_root, platform), windows: platform == "windows");
        host.Running(42, "9042");
        host.Running(43, "9043");
        Line(host, run, "server", Gone, JournalEntry.ProcessStarted, ("pid", "42"), ("startIdentity", "9042"),
            ("commandLineSha256", FakeServerHost.CommandLineSha256("42")), ("bootDirectory", "/srv/runs/q/boot-1"), ("quitSeconds", "300"));
        Line(host, run, "client", Gone, JournalEntry.ProcessStarted, ("pid", "43"), ("startIdentity", "9043"),
            ("commandLineSha256", FakeServerHost.CommandLineSha256("43")), ("launchDirectory", "/srv/runs/q/client-1"));
        var hosts = new Dictionary<string, HostProfile> { ["pc"] = new() { Kind = "ssh", Platform = platform, Lock = "/var/tmp/vt/lock" } };

        var report = await RunRecovery.RecoverAsync(hosts, _ => host, run, false, TimeSpan.FromSeconds(5));

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        var stops = host.Runs.Where(call => call.Script == "stop").ToDictionary(call => call.Variables["game"], call => call.Variables);
        Assert.Equal("300", stops["42"]["quit"]);
        Assert.Equal(((int)ClientSession.DefaultQuitTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), stops["43"]["quit"]);
        if (platform == "macos")
            Assert.All(stops, stop => Assert.Equal(FakeServerHost.CommandLineSha256(stop.Key), stop.Value["commandLine"]));
        else
        {
            Assert.Equal("INT", stops["42"]["signal"]);
            Assert.Equal(ProcessQuit.WindowsConsoleControlScript, stops["42"]["quitHelper"]);
            Assert.Equal("TERM", stops["43"]["signal"]);
        }
        Assert.Contains(report.Steps, step => step.What == "process 42 (started 9042)" && step.Outcome == "quit when asked");
    }

    [Fact] public async Task RemoteMacLostLaunchReplyStopsItsExactClientAndRetiresItsGuiAgent()
    {
        const string run = "run-mac-lost-launch", launch = "/srv/runs/run-mac-lost-launch/client-1";
        var hosts = new Dictionary<string, HostProfile> { ["pc"] = new() { Kind = "ssh", Platform = "macos", Lock = "/var/tmp/vt/lock" } };
        _host.Running(91153, "391852");
        Line(_host, run, "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", launch),
            ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("91153")));
        Line(_host, run, "client", Gone, JournalEntry.MacAgentIntended, ("launchDirectory", launch));
        PidFile(_host, launch, "91153 391852");

        var report = await RunRecovery.RecoverAsync(hosts, _ => _host, run, false, TimeSpan.FromSeconds(5));

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Contains(("91153", "391852"), _host.Stops);
        Assert.Contains(_host.Runs, call => call.Script == "mac-agent-retire" && call.Variables["dir"] == launch);
        Assert.Equal(JournalRunState.Ended,
            Assert.Single((await RunJournalStatus.InspectAsync(hosts, _ => _host, TimeSpan.FromSeconds(5))).Runs).State);
    }

    [Fact] public async Task ProvenNeverStartedMacLaunchDoesNotBlockRecovery()
    {
        const string run = "run-mac-never-started", launch = "/srv/runs/run-mac-never-started/client-1";
        var hosts = new Dictionary<string, HostProfile> { ["pc"] = new() { Kind = "ssh", Platform = "macos", Lock = "/var/tmp/vt/lock" } };
        Line(_host, run, "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", launch),
            ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("never")));
        Line(_host, run, "client", Gone, JournalEntry.MacAgentIntended, ("launchDirectory", launch));
        Line(_host, run, "client", Gone, JournalEntry.LaunchSettled, ("actor", "client"), ("directory", launch));
        Line(_host, run, "client", Gone, JournalEntry.MacAgentRetired, ("launchDirectory", launch));

        var report = await RunRecovery.RecoverAsync(hosts, _ => _host, run, false, TimeSpan.FromSeconds(5));

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Equal(JournalRunState.Ended,
            Assert.Single((await RunJournalStatus.InspectAsync(hosts, _ => _host, TimeSpan.FromSeconds(5))).Runs).State);
        Assert.DoesNotContain(_host.Runs, call => call.Script is "stop" or "mac-agent-retire" or "pid-file");
    }

    [Fact] public async Task RemoteMacLostStopReplyKeepsItsProfileUntilTheGuiAgentIsProvenRemoved()
    {
        const string run = "run-mac-lost-stop", launch = "/srv/runs/run-mac-lost-stop/client-1";
        var hosts = new Dictionary<string, HostProfile> { ["pc"] = new() { Kind = "ssh", Platform = "macos", Lock = "/var/tmp/vt/lock" } };
        _host.Running(91154, "391853");
        Line(_host, run, "client", Gone, JournalEntry.ProcessStarted, ("pid", "91154"),
            ("startIdentity", "391853"), ("commandLineSha256", FakeServerHost.CommandLineSha256("91154")),
            ("launchDirectory", launch));
        Line(_host, run, "client", Gone, JournalEntry.MacAgentIntended, ("launchDirectory", launch));
        _host.MacAgentReply = "VT-AGENT kept\n";

        var first = await RunRecovery.RecoverAsync(hosts, _ => _host, run, false, TimeSpan.FromSeconds(5));

        Assert.False(first.Recovered);
        Assert.Contains(first.Steps, step => step.Failed && step.What.Contains("process", StringComparison.Ordinal));
        Assert.Equal(JournalRunState.Recoverable,
            Assert.Single((await RunJournalStatus.InspectAsync(hosts, _ => _host, TimeSpan.FromSeconds(5))).Runs).State);

        _host.MacAgentReply = "VT-AGENT removed\n";
        var second = await RunRecovery.RecoverAsync(hosts, _ => _host, run, false, TimeSpan.FromSeconds(5));
        Assert.True(second.Recovered, string.Join("\n", second.Steps));
        Assert.Single(_host.Stops);
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

    [Fact] public async Task LaunchGoneSettlesAMissingPidLaunchThenRecoversTheRunsOwnedLock()
    {
        const string run = "run-launch-gone", launch = "/l/run-launch-gone", claimant = "run-launch-gone [claim]";
        Line(_host, run, "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", launch),
            ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("missing")));
        _host.Claims.Add(claimant);
        Line(_host, run, "run", Gone, JournalEntry.LockHeld, ("lock", "/var/tmp/vt/lock"), ("claimant", claimant));

        var report = await RecoverAsync(run, launchGone: true);

        Assert.True(report.Clean, string.Join("\n", report.Steps));
        Assert.Contains(report.Steps, step => step.What == "launch " + launch && step.Outcome.Contains("operator confirmed --launch-gone", StringComparison.Ordinal));
        Assert.Contains(report.Steps, step => step.What == "lock /var/tmp/vt/lock" && step.Outcome == "released");
        Assert.Contains(claimant, _host.Releases);
        var records = (await RunJournalOnHost.ReadAllAsync(_host, Journal, TimeSpan.FromSeconds(5))).Records;
        var abandoned = Assert.Single(records, record => record.Run == run && record.Entry.Kind == JournalEntry.LaunchAbandoned);
        Assert.Equal(("client", launch, "--launch-gone"), (abandoned.Entry.Fields["actor"], abandoned.Entry.Fields["directory"], abandoned.Entry.Fields["operatorConfirmed"]));
        Assert.Contains("no Valheim image or journalled launch command line", abandoned.Entry.Fields["reason"]);
        var recoveryHeld = Assert.Single(records, record => record.Run == run && record.Actor == RunRecovery.Actor &&
            record.Entry.Kind == JournalEntry.LockHeld && record.Entry.Fields["claimant"] != claimant);
        string recoveryClaimant = recoveryHeld.Entry.Fields["claimant"];
        var recoveryReleased = Assert.Single(records, record => record.Run == run && record.Actor == RunRecovery.Actor &&
            record.Entry.Kind == JournalEntry.LockReleased && record.Entry.Fields["claimant"] == recoveryClaimant);
        var staleReleased = Assert.Single(records, record => record.Run == run && record.Actor == RunRecovery.Actor &&
            record.Entry.Kind == JournalEntry.LockReleased && record.Entry.Fields["claimant"] == claimant);
        var ordered = records.ToList();
        Assert.True(ordered.IndexOf(staleReleased) < ordered.IndexOf(recoveryHeld));
        Assert.True(ordered.IndexOf(recoveryHeld) < ordered.IndexOf(abandoned));
        Assert.True(ordered.IndexOf(abandoned) < ordered.IndexOf(recoveryReleased));
        Assert.Contains(recoveryClaimant, _host.Claims);
        Assert.Contains(recoveryClaimant, _host.Releases);
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
    }

    [Fact] public async Task LaunchGoneRefusesWhileAValheimProcessRuns()
    {
        const string run = "run-launch-busy", claimant = "run-launch-busy [claim]";
        Line(_host, run, "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", "/l/busy"),
            ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("81")));
        _host.Claims.Add(claimant);
        Line(_host, run, "run", Gone, JournalEntry.LockHeld, ("lock", "/var/tmp/vt/lock"), ("claimant", claimant));
        _host.Running(81, "9081");

        var report = await RecoverAsync(run, launchGone: true);

        Assert.NotNull(report.Refused);
        Assert.Contains("process 4242 still runs", report.Refused);
        Assert.Empty(report.Steps);
        Assert.DoesNotContain(claimant, _host.Releases);
        Assert.DoesNotContain("lock", _host.Scripts);
        Assert.Contains("launch-gone", _host.Scripts);
        Assert.DoesNotContain((await RunJournalOnHost.ReadAllAsync(_host, Journal, TimeSpan.FromSeconds(5))).Records,
            record => record.Entry.Kind == JournalEntry.LaunchAbandoned);
    }

    [Fact] public async Task LaunchGoneKeepsItsRecoveryClaimWhenSomethingOnTheHostFailsToRecover()
    {
        const string run = "run-launch-failed-copy", claimant = "run-launch-failed-copy [claim]", runtime = "/srv/runs/run-launch-failed-copy/runtime";
        Line(_host, run, "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", "/l/failed-copy"),
            ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("missing")));
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", runtime), ("stage", runtime + ".stage"));
        Line(_host, run, "server", Gone, JournalEntry.CopyDone, ("runtime", runtime), ("stage", runtime + ".stage"));
        _host.Claims.Add(claimant);
        Line(_host, run, "run", Gone, JournalEntry.LockHeld, ("lock", "/var/tmp/vt/lock"), ("claimant", claimant));

        var report = await RecoverAsync(run, launchGone: true);

        Assert.False(report.Recovered);
        Assert.Contains(report.Steps, step => step.What == "copy " + runtime && step.Failed);
        Assert.Contains(report.Steps, step => step.What == "lock /var/tmp/vt/lock" && step.Failed &&
            step.Outcome.Contains("kept as the recovery claim", StringComparison.Ordinal));
        var records = (await RunJournalOnHost.ReadAllAsync(_host, Journal, TimeSpan.FromSeconds(5))).Records;
        string recoveryClaimant = Assert.Single(records, record => record.Run == run && record.Actor == RunRecovery.Actor &&
            record.Entry.Kind == JournalEntry.LockHeld && record.Entry.Fields["claimant"] != claimant).Entry.Fields["claimant"];
        Assert.Contains(recoveryClaimant, _host.Claims);
        Assert.DoesNotContain(recoveryClaimant, _host.Releases);
        Assert.DoesNotContain(records, record => record.Run == run && record.Actor == RunRecovery.Actor &&
            record.Entry.Kind == JournalEntry.LockReleased && record.Entry.Fields["claimant"] == recoveryClaimant);
    }

    [Fact] public async Task LaunchGoneRefusesWhenAnotherOpenItemIsUnprovable()
    {
        const string run = "run-launch-and-lease";
        Line(_host, run, "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", "/l/lease"));
        Line(_host, run, "client", Gone, JournalEntry.LeaseHeld, ("account", "alt-old"), ("pool", "steam"), ("owner", "old owner"));

        var report = await RecoverAsync(run, launchGone: true);

        Assert.NotNull(report.Refused);
        Assert.Contains("not only interrupted launches", report.Refused);
        Assert.Contains("lease", report.Refused);
        Assert.DoesNotContain("launch-gone", _host.Scripts);
        Assert.DoesNotContain((await RunJournalOnHost.ReadAllAsync(_host, Journal, TimeSpan.FromSeconds(5))).Records,
            record => record.Entry.Kind == JournalEntry.LaunchAbandoned);
    }

    [Fact] public async Task LaunchGoneRefusesWhenAnotherRunHoldsTheHostLock()
    {
        const string run = "run-launch-locked";
        Line(_host, run, "client", Gone, JournalEntry.ProcessIntended, ("launchDirectory", "/l/locked"));
        // The other run wins the atomic boundary after this recovery journals its intent but before its claim.
        _host.BeforeScript = script => { if (script == "lock") _host.HeldBy = "another live run"; };

        var report = await RecoverAsync(run, launchGone: true);

        Assert.NotNull(report.Refused);
        Assert.Contains("lock was not granted to this recovery", report.Refused);
        Assert.Contains("another live run", report.Refused);
        Assert.Empty(report.Steps);
        Assert.Contains("lock", _host.Scripts);
        Assert.DoesNotContain("launch-gone", _host.Scripts);
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

    [Fact] public async Task AFailedStopThenRecoveryReportsItsRetainedRuntimeUntilTeardown()
    {
        InterruptedRun("run-retained-after-stop");
        _host.Failures["stop"] = FakeServerHost.TransportFailure;
        var failedStop = await RecoverAsync("run-retained-after-stop");
        Assert.False(failedStop.Recovered);
        Assert.True(Directory.Exists(_host.Local(Prep + "/runtime")));

        // The run keeps its runtime as evidence of the failed stop. The next recovery
        // succeeds at stopping the game, but must not call that retained copy "gone".
        Line(_host, "run-retained-after-stop", "server", Gone, JournalEntry.CopyKept,
            ("runtime", Prep + "/runtime"), ("why", "inspect failed stop"));
        _host.Failures.Remove("stop");
        var recovered = await RecoverAsync("run-retained-after-stop");
        Assert.True(recovered.Recovered, string.Join("\n", recovered.Steps));
        Assert.False(recovered.Clean);
        Assert.Equal(JournalRunState.Kept, recovered.After);
        Assert.Equal("copy", Assert.Single(recovered.Remaining).Kind);
        using var text = new StringWriter();
        RunRecovery.Write(recovered, text, json: false);
        Assert.Contains("WITH RETAINED EVIDENCE: 1 item(s) remain: copy on pc:", text.ToString());
        Assert.Contains("valheim-test env teardown --run run-retained-after-stop", text.ToString());
        Assert.DoesNotContain("nothing of it is left", text.ToString());
        using var json = new StringWriter();
        RunRecovery.Write(recovered, json, json: true);
        using var value = System.Text.Json.JsonDocument.Parse(json.ToString());
        Assert.True(value.RootElement.GetProperty("Recovered").GetBoolean());
        Assert.False(value.RootElement.GetProperty("Clean").GetBoolean());
        Assert.Single(value.RootElement.GetProperty("Remaining").EnumerateArray());
        Assert.Equal(JournalRunState.Kept, Assert.Single((await StatusAsync()).Runs).State);
        Assert.True(Directory.Exists(_host.Local(Prep + "/runtime")));

        var torn = await RecoverAsync("run-retained-after-stop", teardown: true);
        Assert.True(torn.Clean, string.Join("\n", torn.Steps));
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
        Assert.False(Directory.Exists(_host.Local(Prep)));
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

    [Fact] public async Task InterruptedClientProfileKeepsItsFilesWhileTheSourceClientStillRuns()
    {
        const string run = "run-profile-client", source = "/opt/valheim/client";
        Directory.CreateDirectory(_host.Local(Prep + "/runtime"));
        Line(_host, run, "client-a", Gone, JournalEntry.CopyIntended,
            ("runtime", Prep + "/runtime"), ("stage", Prep + "/staging"), ("parent", Prep),
            ("runtimeKind", "client"), ("launchRoot", source));
        _host.GameActive = true;

        var report = await RecoverAsync(run);

        Assert.False(report.Recovered);
        Assert.True(Directory.Exists(_host.Local(Prep + "/runtime")));
        var check = Assert.Single(_host.Runs.Where(item => item.Script == "game-process"));
        Assert.Equal("true", check.Variables["clientSession"]);
        Assert.Equal(source, check.Variables["runtime"]);
    }

    [Fact] public async Task AClientNamedServerStillKeepsItsWindowsProfileWhileItRuns()
    {
        const string run = "run-profile-misnamed-client";
        string runtime = Prep + "/runtime";
        Directory.CreateDirectory(_host.Local(runtime));
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended,
            ("runtime", runtime), ("stage", Prep + "/staging"), ("parent", Prep),
            ("runtimeKind", "client"), ("launchRoot", "/source/game"));
        Line(_host, run, "server", Gone, JournalEntry.CopyDone,
            ("runtime", runtime), ("runtimeKind", "client"), ("launchRoot", runtime));
        _host.GameActive = true;

        var report = await RecoverAsync(run);

        Assert.False(report.Recovered);
        Assert.True(Directory.Exists(_host.Local(runtime)));
        var check = Assert.Single(_host.Runs.Where(item => item.Script == "game-process"));
        Assert.Equal("true", check.Variables["clientSession"]);
        Assert.Equal(runtime, check.Variables["runtime"]);
    }

    [Fact] public async Task AStoppedServerProfileDoesNotRequireAnUnrelatedDesktopClientToStop()
    {
        const string run = "run-server-profile";
        string runtime = Prep + "/runtime";
        Directory.CreateDirectory(_host.Local(runtime));
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended,
            ("runtime", runtime), ("stage", Prep + "/staging"), ("parent", Prep),
            ("runtimeKind", "server"), ("launchRoot", "/source/server"));
        Line(_host, run, "server", Gone, JournalEntry.CopyDone,
            ("runtime", runtime), ("runtimeKind", "server"), ("launchRoot", "/source/server"));

        var report = await RecoverAsync(run);

        Assert.True(report.Recovered);
        var check = Assert.Single(_host.Runs.Where(item => item.Script == "game-process"));
        Assert.Equal("false", check.Variables["clientSession"]);
        Assert.Equal("/source/server", check.Variables["runtime"]);
    }

    [Fact] public async Task AHostThatCannotBeReadRefusesTheRecovery()
    {
        InterruptedRun();
        _host.Failures["journal-read-all"] = FakeServerHost.TransportFailure;
        var report = await RecoverAsync("run-cut");
        Assert.Contains("could not be read", report.Refused);
        Assert.Empty(_host.Stops);
    }

    // The post-recovery check reads the run's own hosts. A run that left only a copy on pc, with the NAS powered off: every step was
    // done and nothing of the run is left, so the recovery succeeds. A host the run did journal on that can only be partly read, or
    // the lease host, still fails the check.
    private static Dictionary<string, HostProfile> TwoHosts() => new()
    {
        ["pc"] = new HostProfile { Kind = "ssh", Lock = "/var/tmp/vt/lock" }, ["nas"] = new HostProfile { Kind = "ssh", Lock = "/var/tmp/vt/lock" },
    };

    private void RunThatLeftACopy(string run)
    {
        Directory.CreateDirectory(Path.Combine(_host.Local(Prep + "/runtime"), "BepInEx"));
        File.WriteAllText(Path.Combine(_host.Local(Prep + "/runtime"), "valheim_server.x86_64"), "game");
        Directory.CreateDirectory(_host.Local(Prep + "/staging"));
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", Prep + "/runtime"), ("stage", Prep + "/staging"), ("parent", Prep));
        Line(_host, run, "server", Gone, JournalEntry.CopyDone, ("runtime", Prep + "/runtime"), ("files", "1"));
    }

    private Task<RecoveryReport> RecoverWithAsync(FakeServerHost nas, string run, string? leaseHost = null) =>
        RunRecovery.RecoverAsync(TwoHosts(), name => name == "nas" ? nas : _host, run, false, TimeSpan.FromSeconds(5),
            leaseHost: leaseHost, leaseDirectory: leaseHost == null ? null : "/var/tmp/vt/leases");

    private FakeServerHost PoweredOffNas()
    {
        var nas = new FakeServerHost("nas", Path.Combine(_root, "nas"));
        nas.Failures["journal-read-all"] = FakeServerHost.TransportFailure;
        return nas;
    }

    [Fact] public async Task AHostTheRunNeverJournalledOnDoesNotFailARecoveryThatDidEveryStep()
    {
        RunThatLeftACopy("run-cut");
        var nas = PoweredOffNas();

        var report = await RecoverWithAsync(nas, "run-cut");

        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Equal(JournalRunState.Ended, report.After);
        Assert.False(Directory.Exists(_host.Local(Prep + "/runtime")));
        // What the NAS may hold of the run is not part of that result, and the output says so.
        Assert.Equal("nas", Assert.Single(report.NotRead));
        var written = new StringWriter();
        RunRecovery.Write(report, written, json: false);
        Assert.Contains("NOTE host nas could not be read", written.ToString(), StringComparison.Ordinal);
    }

    // A host that answered with a cut line: the line may be the run's, so it counts for the run even though no readable line is.
    [Fact] public async Task AHostWithOnlyUnreadableLinesStillFailsThePostRecoveryCheck()
    {
        RunThatLeftACopy("run-cut");
        var nas = new FakeServerHost("nas", Path.Combine(_root, "nas"));
        Directory.CreateDirectory(nas.Local(Journal + "/run-cut"));
        File.AppendAllText(nas.Local(Journal + "/run-cut/client.jsonl"), "{\"utc\":");

        var report = await RecoverWithAsync(nas, "run-cut");

        Assert.False(report.Recovered);
        Assert.Contains("host nas", Assert.Single(report.Steps, step => step.What == "post-recovery status").Outcome, StringComparison.Ordinal);
    }

    [Fact] public async Task AHostTheRunJournalledOnStillFailsThePostRecoveryCheck()
    {
        RunThatLeftACopy("run-cut");
        // The NAS holds a line of the run and a line cut short: what that line recorded is unknown.
        var nas = new FakeServerHost("nas", Path.Combine(_root, "nas"));
        Line(nas, "run-cut", "client", Gone, JournalEntry.CopyRetired, ("runtime", "/srv/over-client"));
        File.AppendAllText(nas.Local(Journal + "/run-cut/client.jsonl"), "{\"utc\":");

        var report = await RecoverWithAsync(nas, "run-cut");

        Assert.False(report.Recovered);
        Assert.Contains("host nas", Assert.Single(report.Steps, step => step.What == "post-recovery status").Outcome, StringComparison.Ordinal);
    }

    // The first read found the run's closed entries on the NAS; the NAS is gone by the post-recovery read. The run's host counts.
    [Fact] public async Task AHostTheFirstReadFoundTheRunOnStillCountsAfterItIsGone()
    {
        RunThatLeftACopy("run-cut");
        var nas = new FakeServerHost("nas", Path.Combine(_root, "nas"));
        Line(nas, "run-cut", "client", Gone, JournalEntry.CopyIntended, ("runtime", "/srv/over-client"));
        Line(nas, "run-cut", "client", Gone, JournalEntry.CopyRetired, ("runtime", "/srv/over-client"));
        int asked = 0;
        IGameHost Of(string name)
        {
            if (name != "nas") return _host;
            if (++asked == 2) nas.Failures["journal-read-all"] = FakeServerHost.TransportFailure;
            return nas;
        }

        var report = await RunRecovery.RecoverAsync(TwoHosts(), Of, "run-cut", false, TimeSpan.FromSeconds(5));

        Assert.False(report.Recovered);
        Assert.Contains("host nas", Assert.Single(report.Steps, step => step.What == "post-recovery status").Outcome, StringComparison.Ordinal);
    }

    [Fact] public async Task TheLeaseHostIsAlwaysAmongTheHostsThePostRecoveryCheckReads()
    {
        RunThatLeftACopy("run-cut");
        var nas = PoweredOffNas();

        var report = await RecoverWithAsync(nas, "run-cut", leaseHost: "nas");

        Assert.False(report.Recovered);
        Assert.Contains("host nas", Assert.Single(report.Steps, step => step.What == "post-recovery status").Outcome, StringComparison.Ordinal);
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
        var local = new LocalGameHost("this-machine", HostShell.ThisMachine);

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

    // #822: a run cut short while its client ran leaves the host user's recent-servers list moved aside, and the client's own
    // version in its place. Recover puts the user's back and keeps the client's in the backup; a backup another run names is refused.
    [Theory] [InlineData("run-recent", true)] [InlineData("run-other", false)]
    public async Task RecoverPutsBackTheHostUsersRecentServersList(string backupRun, bool restored)
    {
        const string run = "run-recent";
        string root = "/Users/tester/" + HostedWorld.MacSaveRelative, backup = "/srv/runs/" + run + "/" + UserSaveFiles.BackupName(backupRun, "client-1");
        string recent = _host.Local(root + "/serverlist_local/recent");
        Directory.CreateDirectory(Path.GetDirectoryName(recent)!); File.WriteAllText(recent, "the user's servers\n");
        string markers = "/var/tmp/vt/user-files";
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, root, backup, markers,
            UserSaveFilesOnHost.Remote(_host, TimeSpan.FromSeconds(60)));
        await files.IsolateAsync(); File.WriteAllText(recent, "127.0.0.1:2456\n");
        string marker = files.Marker;
        Line(_host, run, "player", Gone, JournalEntry.UserFilesCaptured, files.Captured().Fields.Select(pair => (pair.Key, pair.Value)).ToArray());

        var report = await RecoverAsync(run);

        Assert.Equal(restored, report.Recovered);
        Assert.Equal(restored ? "the user's servers\n" : "127.0.0.1:2456\n", File.ReadAllText(recent));
        Assert.Equal(restored, File.Exists(_host.Local(backup + "/after/serverlist_local/recent")));
        Assert.Equal(!restored, File.Exists(_host.Local(marker))); // Released with the restore, so the next run may capture.
        var step = Assert.Single(report.Steps, step => step.What == "client recent-servers list");
        Assert.Equal(restored, step.Outcome.StartsWith("restored", StringComparison.Ordinal));
        Assert.Equal(restored ? JournalRunState.Ended : JournalRunState.Recoverable, Assert.Single((await StatusAsync()).Runs).State);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task CaptureIntentWithNoCapturedReplyIsVisibleAndRecoverableWithoutPersonalFileMutations(bool acquired, bool deleted)
    {
        const string run = "run-capture-intent";
        string root = "/Users/tester/" + HostedWorld.MacSaveRelative;
        string backup = "/srv/runs/" + run + "/" + UserSaveFiles.BackupName(run, "client-1");
        string recent = _host.Local(root + "/serverlist_local/recent");
        Directory.CreateDirectory(Path.GetDirectoryName(recent)!); File.WriteAllText(recent, "personal before");
        await Assert.ThrowsAsync<IOException>(() => UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, root, backup, "/var/tmp/vt/user-files",
            UserSaveFilesOnHost.Remote(_host, TimeSpan.FromSeconds(60)), entry =>
            {
                if (entry.Kind == JournalEntry.UserFilesCaptureIntended)
                    Line(_host, run, "player", Gone, entry.Kind, entry.Fields.Select(pair => (pair.Key, pair.Value)).ToArray());
                if (!acquired || entry.Kind == JournalEntry.UserFilesCaptured) throw new IOException("lost journal reply");
                return Task.CompletedTask;
            }));
        if (deleted) File.Delete(recent); else File.WriteAllText(recent, "newer personal file");
        var pending = Assert.Single((await StatusAsync()).Runs);
        Assert.Equal(JournalRunState.Recoverable, pending.State);
        Assert.Contains("capture intended", Assert.Single(pending.Items).Status);
        var report = await RecoverAsync(run);
        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        if (deleted) Assert.False(File.Exists(recent)); else Assert.Equal("newer personal file", File.ReadAllText(recent));
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
    }

    [Fact] public async Task RecoveryAfterCompletedRestoreOnlySettlesJournalAndLeavesAnotherCapturesMarker()
    {
        const string run = "run-completed-recent";
        string root = "/Users/tester/" + HostedWorld.MacSaveRelative;
        string recent = _host.Local(root + "/serverlist_local/recent");
        Directory.CreateDirectory(Path.GetDirectoryName(recent)!); File.WriteAllText(recent, "personal before");
        var remote = UserSaveFilesOnHost.Remote(_host, TimeSpan.FromSeconds(60));
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, root,
            "/srv/runs/" + run + "/" + UserSaveFiles.BackupName(run, "client-1"), "/var/tmp/vt/user-files", remote);
        Line(_host, run, "player", Gone, JournalEntry.UserFilesCaptured, files.Captured().Fields.Select(pair => (pair.Key, pair.Value)).ToArray());
        await files.IsolateAsync(); await files.RestoreAsync();
        File.WriteAllText(recent, "newer personal file");
        var next = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, root,
            "/srv/runs/run-next/" + UserSaveFiles.BackupName("run-next", "client-1"), "/var/tmp/vt/user-files", remote);
        byte[] marker = File.ReadAllBytes(_host.Local(next.Marker));
        var report = await RecoverAsync(run);
        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Equal("newer personal file", File.ReadAllText(recent));
        Assert.Equal(marker, File.ReadAllBytes(_host.Local(next.Marker)));
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
        await next.UndoIsolateAsync();
    }

    [Fact] public async Task RecoveryIntentAppendFailureNeverStartsUserFileMutation()
    {
        const string run = "run-restore-intent-failed";
        string root = "/Users/tester/" + HostedWorld.MacSaveRelative;
        string recent = _host.Local(root + "/serverlist_local/recent");
        Directory.CreateDirectory(Path.GetDirectoryName(recent)!); File.WriteAllText(recent, "personal before");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, root,
            "/srv/runs/" + run + "/" + UserSaveFiles.BackupName(run, "client-1"), "/var/tmp/vt/user-files",
            UserSaveFilesOnHost.Remote(_host, TimeSpan.FromSeconds(60)));
        Line(_host, run, "player", Gone, JournalEntry.UserFilesCaptured, files.Captured().Fields.Select(pair => (pair.Key, pair.Value)).ToArray());
        await files.IsolateAsync(); File.WriteAllText(recent, "run file");
        _host.JournalFailsFor = JournalEntry.UserFilesRestoreIntended;
        var report = await RecoverAsync(run);
        Assert.False(report.Recovered);
        Assert.Equal("run file", File.ReadAllText(recent));
        Assert.False(Directory.Exists(_host.Local(files.Backup + "/after")));
        Assert.DoesNotContain(_host.Runs, step => step.Script == "user-files" && step.Variables["mode"] == "restore");
        Assert.Equal(JournalRunState.Recoverable, Assert.Single((await StatusAsync()).Runs).State);
        _host.JournalFailsFor = null;
        Assert.True((await RecoverAsync(run)).Recovered);
        Assert.Equal("personal before", File.ReadAllText(recent));
    }

    [Fact] public async Task ADuplicateRestoreIntentAfterDurableCompletionWithLostReplyRemainsRecoverable()
    {
        const string run = "run-duplicate-intent";
        string root = "/Users/tester/" + HostedWorld.MacSaveRelative;
        string recent = _host.Local(root + "/serverlist_local/recent");
        Directory.CreateDirectory(Path.GetDirectoryName(recent)!); File.WriteAllText(recent, "personal before");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, root,
            "/srv/runs/" + run + "/" + UserSaveFiles.BackupName(run, "client-1"), "/var/tmp/vt/user-files",
            UserSaveFilesOnHost.Remote(_host, TimeSpan.FromSeconds(60)));
        void Append(JournalEntry entry) => Line(_host, run, "player", Gone, entry.Kind, entry.Fields.Select(pair => (pair.Key, pair.Value)).ToArray());
        Append(files.Captured()); await files.IsolateAsync();
        await Assert.ThrowsAsync<IOException>(() => files.RestoreAsync(entry =>
        {
            Append(entry); // Completion is durable, but the caller receives no acknowledgement.
            if (entry.Kind == JournalEntry.UserFilesRestored) throw new IOException("lost completion acknowledgement");
            return Task.CompletedTask;
        }));
        Assert.Empty(Assert.Single((await StatusAsync()).Runs).Items);
        File.WriteAllText(recent, "new personal after completion");
        await Assert.ThrowsAsync<IOException>(() => files.RestoreAsync(entry =>
        {
            Assert.Equal(JournalEntry.UserFilesRestoreIntended, entry.Kind);
            Append(entry); throw new IOException("stop after duplicate intent");
        }));
        var pending = Assert.Single(Assert.Single((await StatusAsync()).Runs).Items);
        Assert.Equal(2, pending.Fields["hashes"].Split(',').Length);
        var report = await RecoverAsync(run);
        Assert.True(report.Recovered, string.Join("\n", report.Steps));
        Assert.Equal("new personal after completion", File.ReadAllText(recent));
        Assert.False(Directory.Exists(_host.Local(files.Backup + "/after")));
        Assert.Empty(Assert.Single((await StatusAsync()).Runs).Items);
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
        // A client's copy: its logs too. Its Unity log is in its launch folder, already the run's (#822).
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
        // Since the copy was made (its journalled start); only the copy's own logs, the Unity log by its one name, never the host
        // user's Player.log (#822).
        var keeps = _host.Runs.Where(run => run.Script == "keep-logs").ToList();
        Assert.All(keeps, run => Assert.Equal(new[] { "keep", "runtime", "since", "unitylog" }, run.Variables.Keys.Order(StringComparer.Ordinal)));
        Assert.All(keeps, run => Assert.Equal(GameLaunch.UnityLogName, run.Variables["unitylog"]));
        var copiedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(keeps[0].Variables["since"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(copiedAt, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddHours(-12)); // T0, the journal's time, not now.
        // Each log is kept before its copy is removed.
        var scripts = _host.Scripts.ToList();
        Assert.True(scripts.IndexOf("keep-logs") < scripts.IndexOf("cleanup-stage"));
        Assert.Equal(JournalRunState.Ended, Assert.Single((await StatusAsync()).Runs).State);
    }

    [Fact]
    public async Task MacServerRecoveryMovesTheUserWorldBeforeRetiringItsCollectedDirectory()
    {
        const string run = "run-mac-evidence", worlds = "/home/p/worlds_local", collected = "/srv/runs/run-mac-evidence/host-world";
        string fixture = FakeInstalls.World(Path.Combine(_root, "fixture"));
        Directory.CreateDirectory(_host.Local(worlds));
        File.Copy(Path.Combine(fixture, "Campaign.fwl"), _host.Local(worlds + "/Campaign.fwl"));
        // Deliberately put the collected copy first in the journal: recovery must still move the user's placement first.
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", collected), ("stage", ""), ("parent", "/srv/runs/" + run), ("retention", "after-fetch"));
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", worlds + "/Campaign"), ("stage", ""),
            ("parent", worlds), ("holds", HostedWorldOnHost.Holds), ("world", "Campaign"), ("worldUid", "4242"), ("keepIn", collected));
        Line(_host, run, "server", Gone, JournalEntry.CopyDone, ("runtime", worlds + "/Campaign"), ("verified", "true"));
        var result = await RecoverAsync(run, teardown: true);
        Assert.True(result.Clean, string.Join("\n", result.Steps));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_host.Local(worlds)));
        Assert.False(Directory.Exists(_host.Local(collected)));
        Assert.True(_host.Runs.FindIndex(item => item.Script == "world-move") < _host.Runs.FindIndex(item => item.Script == "cleanup-stage"));
    }

    [Fact]
    public async Task TeardownKeepsBootOwnershipFilesWhenLaunchSettlementCannotBeJournalled()
    {
        const string run = "run-unsettled", boot = "/srv/runs/run-unsettled/boot-1";
        Line(_host, run, "server", Gone, JournalEntry.ProcessIntended, ("bootDirectory", boot), ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("41")));
        PidFile(_host, boot, "41 9041"); // the exact process is already gone
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", boot), ("stage", ""), ("parent", "/srv/runs/" + run), ("retention", "after-fetch"));
        _host.JournalFailsFor = JournalEntry.LaunchSettled;
        var failed = await RecoverAsync(run, teardown: true);
        Assert.False(failed.Recovered);
        Assert.True(File.Exists(_host.Local(boot + "/pid")));
        Assert.DoesNotContain(_host.Runs, item => item.Script == "cleanup-stage");
        _host.JournalFailsFor = null;
        var recovered = await RecoverAsync(run, teardown: true);
        Assert.True(recovered.Clean, string.Join("\n", recovered.Steps));
        Assert.False(Directory.Exists(_host.Local(boot)));
    }

    [Fact]
    public async Task ASettledLaunchWithDeletedBootFilesNeedsNeitherPidNorRunEnded()
    {
        const string run = "run-settled", boot = "/srv/runs/run-settled/boot-1";
        Line(_host, run, "server", Gone, JournalEntry.ProcessIntended, ("bootDirectory", boot), ("expectedCommandLineSha256", FakeServerHost.CommandLineSha256("41")));
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", boot), ("stage", ""), ("parent", "/srv/runs/" + run), ("retention", "after-fetch"));
        Line(_host, run, "server", Gone, JournalEntry.LaunchSettled, ("actor", "server"), ("directory", boot));
        Line(_host, run, "server", Gone, JournalEntry.CopyDone, ("runtime", boot), ("fetched", "true"));
        // The remover completed, then the runner died before CopyRetired and RunEnded; no ownership file remains.
        Assert.False(Directory.Exists(_host.Local(boot)));
        var result = await RecoverAsync(run);
        Assert.True(result.Clean, string.Join("\n", result.Steps));
    }

    [Theory]
    [InlineData("world", false)]
    [InlineData("world", true)]
    [InlineData("boot-1", false)]
    [InlineData("boot-1", true)]
    [InlineData("host-world", false)]
    [InlineData("host-world", true)]
    public async Task InterruptedServerEvidenceStaysUntilFetchIsProvenOrTeardownRequested(string name, bool fetched)
    {
        const string run = "run-evidence";
        string directory = "/srv/runs/" + run + "/" + name;
        Directory.CreateDirectory(_host.Local(directory));
        File.WriteAllText(Path.Combine(_host.Local(directory), "evidence.txt"), "the only copy unless fetched");
        Line(_host, run, "server", Gone, JournalEntry.CopyIntended, ("runtime", directory), ("stage", ""),
            ("parent", "/srv/runs/" + run), ("holds", name == "world" ? "world" : "server-boot"), ("retention", "after-fetch"));
        if (fetched) Line(_host, run, "server", Gone, JournalEntry.CopyDone, ("runtime", directory), ("fetched", "true"));
        var recovered = await RecoverAsync(run);
        Assert.True(recovered.Recovered, string.Join("\n", recovered.Steps));
        Assert.Equal(!fetched, Directory.Exists(_host.Local(directory)));
        if (!fetched)
        {
            Assert.Equal(JournalRunState.Kept, recovered.After);
            Assert.True(Assert.Single(recovered.Remaining).Kept);
            var torn = await RecoverAsync(run, teardown: true);
            Assert.True(torn.Clean, string.Join("\n", torn.Steps));
            Assert.False(Directory.Exists(_host.Local(directory)));
        }
        else Assert.True(recovered.Clean);
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
    // host-world folder (never deleted), with the game's backup beside it, once its metadata states the journalled world UID; the
    // user's own world stays. A journal line that does not name a worlds_local or a world UID, or a keep folder outside this run's
    // directory, is refused and nothing moves.
    [Fact] public async Task RecoverMovesAHostedWorldOutOfTheClientsWorldsIntoTheRunsFolder()
    {
        const string Worlds = "/home/p/.config/unity3d/IronGate/Valheim/worlds_local", KeepIn = "/srv/runs/run-host/host-world";
        ChunkedWorld(Worlds, "Campaign", 4242);
        File.WriteAllText(_host.Local(Worlds + "/Campaign_backup_auto-1.db"), "backup");
        File.WriteAllText(_host.Local(Worlds + "/MyWorld.fwl"), "the user's world");
        Line(_host, "run-host", "host", Gone, JournalEntry.CopyIntended, ("runtime", Worlds + "/Campaign"), ("stage", ""), ("parent", Worlds),
            ("holds", "hosted-world"), ("world", "Campaign"), ("worldUid", "4242"), ("keepIn", KeepIn));
        Line(_host, "run-host", "host", Gone, JournalEntry.CopyDone, ("runtime", Worlds + "/Campaign"), ("files", "1"), ("verified", "true"));

        var recovered = await RecoverAsync("run-host");

        Assert.True(recovered.Recovered, string.Join("\n", recovered.Steps));
        Assert.StartsWith("moved out of the client's worlds into " + KeepIn, Assert.Single(recovered.Steps, step => step.What == "copy " + Worlds + "/Campaign").Outcome);
        Assert.Equal(new[] { "MyWorld.fwl" }, Directory.EnumerateFileSystemEntries(_host.Local(Worlds)).Select(Path.GetFileName));
        Assert.Equal("chunk", File.ReadAllText(_host.Local(KeepIn + "/Campaign/0_0.chunk")));
        Assert.Equal("backup", File.ReadAllText(_host.Local(KeepIn + "/Campaign_backup_auto-1.db")));
        Assert.All((await StatusAsync()).Runs, run => Assert.Equal(JournalRunState.Ended, run.State));

        // Refused: a folder that is not a worlds_local, a line without the world UID, and a keep folder outside the run's directory.
        // Nothing moves.
        foreach (var (run, parent, keepIn, uid) in new[]
        {
            ("run-odd", "/home/p/Documents", "/srv/runs/run-odd/host-world", "7"), ("run-nouid", Worlds, "/srv/runs/run-nouid/host-world", ""),
            ("run-far", Worlds, "/srv/elsewhere/host-world", "7"),
        })
        {
            ChunkedWorld(parent, "Other", 7);
            Line(_host, run, "host", Gone, JournalEntry.CopyIntended, ("runtime", parent + "/Other"), ("stage", ""), ("parent", parent),
                ("holds", "hosted-world"), ("world", "Other"), ("worldUid", uid), ("keepIn", keepIn));
            Assert.False((await RecoverAsync(run)).Recovered);
            Assert.True(Directory.Exists(_host.Local(parent + "/Other")));
            Assert.DoesNotContain(_host.Runs, call => call.Script == "world-move" && call.Variables["name"] == "Other");
        }
    }

    [Fact]
    public async Task RecoveryOfAnIntentOnlySameUidWorldPreservesTheForeignWorldAndStaysPending()
    {
        const string run = "run-intent-only", worlds = "/home/p/worlds_local", keep = "/srv/runs/run-intent-only/host-world";
        ChunkedWorld(worlds, "Campaign", 4242);
        Line(_host, run, "host", Gone, JournalEntry.CopyIntended, ("runtime", worlds + "/Campaign"), ("stage", ""),
            ("parent", worlds), ("holds", HostedWorldOnHost.Holds), ("world", "Campaign"), ("worldUid", "4242"), ("keepIn", keep));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var report = await RecoverAsync(run);
            Assert.False(report.Recovered);
            Assert.Contains("never confirmed", Assert.Single(report.Steps, step => step.What == "copy " + worlds + "/Campaign").Outcome);
            Assert.Equal("chunk", File.ReadAllText(_host.Local(worlds + "/Campaign/0_0.chunk")));
            Assert.Equal(JournalRunState.Recoverable, Assert.Single((await StatusAsync()).Runs).State);
        }
        Assert.DoesNotContain(_host.Runs, call => call.Script == "world-move");
    }

    // #807: the name is not the proof. A world in the client's worlds under the journalled name that states another world UID (one
    // the user made after the run's was moved away by hand), or whose identity cannot be read, is refused: it stays where it is and
    // the run stays recoverable. With nothing named for the world left, the copy is retired as gone.
    [Fact] public async Task RecoverNeverMovesASameNamedWorldThatIsNotTheRunsOwn()
    {
        const string Worlds = "/home/p/.config/unity3d/IronGate/Valheim/worlds_local";
        void Journalled(string run, string world) => Line(_host, run, "host", Gone, JournalEntry.CopyIntended, ("runtime", Worlds + "/" + world), ("stage", ""),
            ("parent", Worlds), ("holds", "hosted-world"), ("world", world), ("worldUid", "4242"), ("keepIn", "/srv/runs/" + run + "/host-world"));
        ChunkedWorld(Worlds, "Campaign", 9999);
        Journalled("run-mine", "Campaign");
        var refused = await RecoverAsync("run-mine");
        Assert.False(refused.Recovered);
        Assert.Contains("UID 9999, not the fixture's 4242", Assert.Single(refused.Steps, step => step.What == "copy " + Worlds + "/Campaign").Outcome);
        Assert.Equal("chunk", File.ReadAllText(_host.Local(Worlds + "/Campaign/0_0.chunk")));
        Assert.DoesNotContain(_host.Runs, call => call.Script == "world-move");

        Directory.CreateDirectory(_host.Local(Worlds + "/Unread"));
        File.WriteAllText(_host.Local(Worlds + "/Unread/0_0.chunk"), "chunk");
        Journalled("run-unread", "Unread");
        Assert.Contains("holds no world metadata", Assert.Single((await RecoverAsync("run-unread")).Steps, step => step.What == "copy " + Worlds + "/Unread").Outcome);
        Assert.True(File.Exists(_host.Local(Worlds + "/Unread/0_0.chunk")));

        Journalled("run-gone", "Vanished");
        var gone = await RecoverAsync("run-gone");
        Assert.True(gone.Recovered, string.Join("\n", gone.Steps));
        Assert.Equal("nothing left in the client's worlds", Assert.Single(gone.Steps, step => step.What == "copy " + Worlds + "/Vanished").Outcome);
    }

    // #807, with fakes: a killed `start` whose hosting client runs on this machine. The campaign places its fixture world in this
    // machine's worlds_local through the same journalled site as a remote client's, so env status lists the staged world, env
    // recover moves it (a chunked save folder the game rewrote, proven by its world UID) out into the run's folder, the user's own
    // world stays, nothing is left, and the next run's placement of the same fixture is not refused.
    [Fact] public async Task RecoverRetiresTheWorldAKilledLocalHostingClientLeftSoTheNextStartPlacesIt()
    {
        const string Data = "/Users/t/Library/Application Support/IronGate/Valheim", Worlds = Data + "/worlds_local", Run = "run-killed-hold";
        var here = new FakeServerHost("here", Path.Combine(_root, "here"), kind: GameHostKind.Local);
        var hosts = new Dictionary<string, HostProfile> { ["here"] = new HostProfile { Kind = "local", Lock = "/var/tmp/vt/lock" } };
        var environment = new ResolvedEnvironment
        {
            Hosts = hosts, Clients = { ["host"] = new GameRole { Host = "here", Install = "/game/client", Runtime = "/runs", CliPort = 5578 } },
        };
        var character = new HostedCampaignCharacter { FileName = "vt-host", CharactersLocalDirectory = Data + "/characters_local" };
        Directory.CreateDirectory(here.Local(Worlds));
        File.WriteAllText(here.Local(Worlds + "/MyWorld.fwl"), "the user's world");
        string fixture = Path.Combine(_root, "fixture");
        Directory.CreateDirectory(Path.Combine(fixture, "Campaign"));
        File.Copy(Path.Combine(FakeInstalls.World(Path.Combine(_root, "flat")), "Campaign.fwl"), Path.Combine(fixture, "Campaign", "_main.0.fwl2"));
        File.WriteAllText(Path.Combine(fixture, "Campaign", "_main.0.db2"), "fixture");
        var plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
        // The simulated client's data directory is the same one, so a world placed without the campaign's site lands there too.
        using var data = new FakeClientDataDirectory(here.Local(Data));
        // The hosting client's placement as the campaign makes it, on the site it chooses; the runner's journal lines as a runner
        // that was then killed writes them.
        HostedWorld Place(string run)
        {
            var site = HostedWorldOnHost.ForCampaignClient(environment, "host", run, character, here,
                (entry, _) => { Line(here, run, "host", Gone, entry.Kind, [.. entry.Fields.Select(field => (field.Key, field.Value))]); return Task.CompletedTask; },
                _ => Task.CompletedTask);
            var section = new ClientRunPlan { Mode = "attach", Port = 5578, Character = "vt-host", HostWorld = plan };
            var lifecycle = new HostedWorldLifecycle(section, Directory.CreateDirectory(Path.Combine(_root, "out-" + run)).FullName, CancellationToken.None, site);
            lifecycle.CheckSaveDirectory();
            lifecycle.PlaceWorld();
            return lifecycle.World!;
        }

        Place(Run);
        Assert.True(File.Exists(here.Local(Worlds + "/Campaign/_main.0.fwl2")));
        // The game hosted it and saved: a newer save beside the fixture's, in Valheim 1.0's chunked folder. Then the runner was killed.
        File.Copy(here.Local(Worlds + "/Campaign/_main.0.fwl2"), here.Local(Worlds + "/Campaign/_main.1.fwl2"));
        File.WriteAllText(here.Local(Worlds + "/Campaign/0_0.chunk"), "saved by the game");

        var status = await RunJournalStatus.InspectAsync(hosts, _ => here, TimeSpan.FromSeconds(5));
        var left = Assert.Single(status.Runs);
        Assert.Equal(JournalRunState.Recoverable, left.State);
        // The world in the user's worlds, and the run folder's stage and collection the placement reserved for it.
        Assert.Equal(new[] { Worlds + "/Campaign", "/runs/" + Run + "/host-world", "/runs/" + Run + "/host-world-stage" },
            left.Items.Where(item => item.Kind == "copy").Select(item => item.What).Order(StringComparer.Ordinal));

        var recovered = await RunRecovery.RecoverAsync(hosts, _ => here, Run, teardown: false, TimeSpan.FromSeconds(5));

        Assert.Contains("world UID 4242 as journalled", Assert.Single(recovered.Steps, step => step.What == "copy " + Worlds + "/Campaign").Outcome);
        Assert.Equal(new[] { "MyWorld.fwl" }, Directory.EnumerateFileSystemEntries(here.Local(Worlds)).Select(Path.GetFileName));
        // The world moved out into the run's folder is the run's only copy of its save: never fetched, it is kept and listed (#791).
        Assert.Equal("saved by the game", File.ReadAllText(here.Local("/runs/" + Run + "/host-world/Campaign/0_0.chunk")));
        Assert.False(recovered.Clean, string.Join("\n", recovered.Steps));
        Assert.Contains("fetch was not proven", Assert.Single(recovered.Steps, step => step.What == "copy /runs/" + Run + "/host-world").Outcome);
        Assert.False(Directory.Exists(here.Local("/runs/" + Run + "/host-world-stage")));
        Assert.Equal(JournalRunState.Kept, Assert.Single((await RunJournalStatus.InspectAsync(hosts, _ => here, TimeSpan.FromSeconds(5))).Runs).State);
        // env teardown removes it.
        Assert.True((await RunRecovery.RecoverAsync(hosts, _ => here, Run, teardown: true, TimeSpan.FromSeconds(5))).Clean);
        Assert.False(Directory.Exists(here.Local("/runs/" + Run + "/host-world")));
        Assert.All((await RunJournalStatus.InspectAsync(hosts, _ => here, TimeSpan.FromSeconds(5))).Runs, run => Assert.Equal(JournalRunState.Ended, run.State));

        // The next start places the same fixture: nothing named for it is left to refuse it.
        var next = Place("run-next");
        Assert.True(File.Exists(here.Local(Worlds + "/Campaign/_main.0.fwl2")));
        next.Collect();
    }

    // A chunked world folder as Valheim 1.0 writes one: <name>/_main.<n>.fwl2 stating the world, its data and a chunk.
    private void ChunkedWorld(string worlds, string name, long uid)
    {
        string folder = _host.Local(worlds + "/" + name);
        Directory.CreateDirectory(folder);
        string flat = FakeInstalls.World(Path.Combine(_root, "flat-" + name + "-" + uid), name, uid);
        File.Copy(Path.Combine(flat, name + ".fwl"), Path.Combine(folder, "_main.1.fwl2"), overwrite: true);
        File.WriteAllText(Path.Combine(folder, "_main.1.db2"), "data");
        File.WriteAllText(Path.Combine(folder, "0_0.chunk"), "chunk");
    }

    // #738: the staging path is read from the journal. One that is not the copy's own <parent>/staging (a corrupt or hand-edited
    // line) is refused before any host script runs: the copy and the named folder both stay, and the recovery reports it.
    [Theory]
    [InlineData("/srv/runs/server/someone-else")]
    [InlineData("/srv/runs/server/vt-prep-cut-server/../someone-else")]
    [InlineData("/srv/runs/server/vt-prep-cut-server/runtime")]
    public async Task RecoverRefusesAJournalledStagingPathThatIsNotTheCopysOwn(string stage)
    {
        Directory.CreateDirectory(Path.Combine(_host.Local(Prep + "/runtime"), "BepInEx"));
        Directory.CreateDirectory(_host.Local("/srv/runs/server/someone-else"));
        File.WriteAllText(_host.Local("/srv/runs/server/someone-else/precious.txt"), "not ours");
        Line(_host, "run-stray-stage", "server", Gone, JournalEntry.CopyIntended, ("runtime", Prep + "/runtime"), ("stage", stage), ("parent", Prep));
        Line(_host, "run-stray-stage", "server", Gone, JournalEntry.CopyDone, ("runtime", Prep + "/runtime"), ("files", "1"));

        var report = await RecoverAsync("run-stray-stage");

        Assert.False(report.Recovered);
        Assert.Contains("not removed: The staging directory must be", Assert.Single(report.Steps, step => step.What == "copy " + Prep + "/runtime").Outcome);
        Assert.DoesNotContain(_host.Scripts, script => script is "keep-logs" or "cleanup-stage"); // nothing ran on the host
        Assert.True(Directory.Exists(_host.Local(Prep + "/runtime")));
        Assert.Equal("not ours", File.ReadAllText(_host.Local("/srv/runs/server/someone-else/precious.txt")));
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
    // in the run's folder with their names, older ones are only named, and a copy that holds no log of the run makes no folder.
    // Only a prepared copy's logs are kept.
    [Fact] public async Task TheRealKeepScriptKeepsOnlyLogsWrittenSinceTheCopyWasMade()
    {
        var local = new LocalGameHost("pc", HostShell.ThisMachine);
        string runs = Path.Combine(_root, "runs", "env"), runtime = Path.Combine(runs, "vt-prep-run-real-client", "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "BepInEx"));
        File.WriteAllText(Path.Combine(runtime, "BepInEx", "LogOutput.log"), "bepinex");
        File.WriteAllText(Path.Combine(runtime, GameLaunch.UnityLogName), "unity");
        File.WriteAllText(Path.Combine(runtime, "preloader_1.log"), "old preloader");
        var copied = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(Path.Combine(runtime, "preloader_1.log"), copied.AddDays(-3)); // Came with the install.

        var logs = await RunRecovery.KeepLogsAsync(local, runtime, "run-real", "client", copied, TimeSpan.FromSeconds(60));
        Assert.Equal(Path.Combine(runs, "run-real", "recovered-client"), logs.Folder);
        Assert.Equal(["BepInEx/LogOutput.log", "toolkit-unity.log"], logs.Kept);
        Assert.Equal(["preloader_1.log"], logs.Older);
        Assert.Equal("bepinex", File.ReadAllText(Path.Combine(logs.Folder, "BepInEx", "LogOutput.log")));
        Assert.Equal("unity", File.ReadAllText(Path.Combine(logs.Folder, "toolkit-unity.log")));
        Assert.False(File.Exists(Path.Combine(logs.Folder, "preloader_1.log")));

        string server = Path.Combine(runs, "vt-prep-run-real2-server", "runtime");
        Directory.CreateDirectory(server); File.WriteAllText(Path.Combine(server, "toolkit-unity.log"), "unity");
        File.SetLastWriteTimeUtc(Path.Combine(server, "toolkit-unity.log"), copied.AddHours(-1)); // An earlier run's.
        logs = await RunRecovery.KeepLogsAsync(local, server, "run-real2", "server", copied, TimeSpan.FromSeconds(60));
        Assert.Empty(logs.Kept);
        Assert.Equal(["toolkit-unity.log"], logs.Older);

        string empty = Directory.CreateDirectory(Path.Combine(runs, "vt-prep-run-empty-server", "runtime")).FullName;
        logs = await RunRecovery.KeepLogsAsync(local, empty, "run-empty", "server", copied, TimeSpan.FromSeconds(60));
        Assert.Empty(logs.Kept);
        Assert.False(Directory.Exists(logs.Folder));
        await Assert.ThrowsAsync<ArgumentException>(() => RunRecovery.KeepLogsAsync(local, Path.Combine(runs, "elsewhere", "runtime"), "run-x", "server", copied, TimeSpan.FromSeconds(60)));
    }

    [Fact] public async Task TheCommandLineRequiresARunAndSaysWhatItRefused()
    {
        foreach (string[] args in new[] { new[] { "recover" }, ["teardown", "--run"], ["recover", "--run", "x", "extra"], ["recover", "--hosts", "--run", "x"],
                     ["teardown", "--run", "x", "--launch-gone"] })
            Assert.Equal(2, await EnvCommand.RunAsync(args, new StringWriter(), new StringWriter()));
        var error = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["recover", "--run", "../escape", "--inventory", Path.Combine(_root, "missing.json")], new StringWriter(), error));
        Assert.StartsWith("REFUSED: ", error.ToString());
        error = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["recover", "--run", "../escape", "--launch-gone", "--inventory", Path.Combine(_root, "missing.json")], new StringWriter(), error));
        Assert.StartsWith("REFUSED: ", error.ToString());
        Assert.Contains("recover --run ID [--launch-gone]", EnvCommand.Usage);
    }
}
