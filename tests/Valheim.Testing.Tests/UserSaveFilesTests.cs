using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;
using Xunit;

// #822: the files the game writes into the user's own Valheim data folder whatever the launch asks (a client's recent-servers
// list, the macOS server's access lists) are left as the run found them: in process, through this machine's real shell (bash on
// macOS and Linux, PowerShell on Windows), and around every owned client process a ClientSession starts.
public sealed class UserSaveFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("user-save-files-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Run = "run-user-files";
    private string Save => Path.Combine(_root, "save");
    private string Markers => Path.Combine(_root, "markers");
    private string BackupFor(string label) => Path.Combine(_root, "evidence", UserSaveFiles.BackupName(Run, label));
    private string At(string folder, string name) => Path.Combine([folder, .. name.Split('/')]);
    private void Write(string folder, string name, string text)
    {
        string path = At(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void TheUsersFilesAreMovedAsideAndPutBackWhileRunVersionsAreKept(bool client)
    {
        var kind = client ? UserSaveFiles.Kind.ClientServerList : UserSaveFiles.Kind.MacServerLists;
        var names = UserSaveFiles.Names(kind);
        Write(Save, names[0], "the user's first\n"); // The second is absent, as a user's often is.
        var files = UserSaveFiles.Capture(kind, Save, BackupFor("x"), Markers);
        Assert.Equal(JournalEntry.UserFilesCaptured, files.Captured().Kind);
        files.Isolate();
        Assert.DoesNotContain(names, name => File.Exists(At(Save, name)));
        Write(Save, names[0], "the run's first\n");
        Write(Save, names[1], "the run's second\n");
        files.Restore();
        Assert.Equal("the user's first\n", File.ReadAllText(At(Save, names[0])));
        Assert.False(File.Exists(At(Save, names[1])));
        Assert.Equal("the run's first\n", File.ReadAllText(At(Path.Combine(BackupFor("x"), "after"), names[0])));
        Assert.Equal("the run's second\n", File.ReadAllText(At(Path.Combine(BackupFor("x"), "after"), names[1])));
        files.Restore(); // A retry after a lost "restored" line changes nothing.
        Assert.Equal("the user's first\n", File.ReadAllText(At(Save, names[0])));
    }

    [Fact] public void AChangedFileRefusesIsolationAndAChangedBackupRefusesRestore()
    {
        string recent = At(Save, "serverlist_local/recent");
        Write(Save, "serverlist_local/recent", "user\n");
        var files = UserSaveFiles.Capture(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("x"), Markers);
        File.WriteAllText(recent, "changed\n");
        Assert.Throws<IOException>(files.Isolate);
        Assert.Equal("changed\n", File.ReadAllText(recent));
        File.WriteAllText(At(BackupFor("x"), "serverlist_local/recent"), "bad backup\n");
        Assert.Throws<IOException>(files.Restore);
        Assert.Equal("changed\n", File.ReadAllText(recent));
    }

    [Fact] public void ACorruptLaterBackupDoesNotPartlyRestoreEarlierFiles()
    {
        Write(Save, "serverlist_local/recent", "user recent\n");
        Write(Save, "serverlist_local/recent.old", "user old\n");
        var files = UserSaveFiles.Capture(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("x"), Markers);
        files.Isolate();
        Write(Save, "serverlist_local/recent", "run recent\n");
        File.WriteAllText(At(BackupFor("x"), "serverlist_local/recent.old"), "corrupt\n");
        Assert.Throws<IOException>(files.Restore);
        Assert.Equal("run recent\n", File.ReadAllText(At(Save, "serverlist_local/recent")));
        Assert.False(File.Exists(At(Save, "serverlist_local/recent.old")));
    }

    [Fact] public void ASecondRunVersionCannotBeLostOnARetry()
    {
        var files = UserSaveFiles.Capture(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("x"), Markers);
        files.Isolate();
        Write(Save, "serverlist_local/recent", "first run version\n");
        files.Restore();
        Write(Save, "serverlist_local/recent", "second run version\n");
        Assert.Throws<IOException>(files.Restore);
        Assert.Equal("second run version\n", File.ReadAllText(At(Save, "serverlist_local/recent")));
        Assert.Equal("first run version\n", File.ReadAllText(At(Path.Combine(BackupFor("x"), "after"), "serverlist_local/recent")));
    }

    [Fact] public void RecoveryRestoresOnlyFromThisRunsBackupInThisUsersFolder()
    {
        Write(Save, "serverlist_local/recent", "user\n");
        var fields = UserSaveFiles.Capture(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("x"), Markers).Captured().Fields;
        Assert.Equal(BackupFor("x"), UserSaveFiles.FromJournal(fields, Run, Save).Backup);
        Assert.Throws<InvalidDataException>(() => UserSaveFiles.FromJournal(fields, "run-other", Save));
        Assert.Throws<InvalidDataException>(() => UserSaveFiles.FromJournal(fields, Run, Path.Combine(_root, "another-user")));
        var unknown = new Dictionary<string, string>(fields) { ["files"] = "worlds_local" };
        Assert.Throws<InvalidDataException>(() => UserSaveFiles.FromJournal(unknown, Run, Save));
        var hashes = new Dictionary<string, string>(fields) { ["hashes"] = "-" };
        Assert.Throws<InvalidDataException>(() => UserSaveFiles.FromJournal(hashes, Run, Save));
    }

    // The host scripts, run by this machine's own shell, do what the in-process steps do: the same capture, refusals and restore.
    [Fact] public async Task TheHostScriptOnThisMachinesShellKeepsTheFilesAsTheInProcessStepsDo()
    {
        var remote = UserSaveFilesOnHost.Remote(new LocalGameHost("this machine", HostShell.ThisMachine), TimeSpan.FromSeconds(60));
        Write(Save, "serverlist_local/recent", "the user's servers\n");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("host"), Markers, remote);
        await Assert.ThrowsAnyAsync<Exception>(() => UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("host"), Markers, remote));
        // Another run's capture of the same folder is refused while this one holds it, and changes nothing.
        await Assert.ThrowsAnyAsync<Exception>(() => UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("other"), Markers, remote));
        Assert.False(Directory.Exists(BackupFor("other")));
        Assert.Equal("the user's servers\n", File.ReadAllText(At(BackupFor("host"), "serverlist_local/recent")));
        Assert.Equal(new[] { FileHash.Sha256(At(Save, "serverlist_local/recent")), "-" }, files.Captured().Fields["hashes"].Split(','));
        await files.IsolateAsync();
        Assert.False(File.Exists(At(Save, "serverlist_local/recent")));
        Write(Save, "serverlist_local/recent", "127.0.0.1:2456\n");
        Write(Save, "serverlist_local/recent.old", "127.0.0.1:2455\n");
        var recovered = UserSaveFiles.FromJournal(files.Captured().Fields, Run, Save, remote);
        await recovered.RestoreAsync();
        Assert.Equal("the user's servers\n", File.ReadAllText(At(Save, "serverlist_local/recent")));
        Assert.False(File.Exists(At(Save, "serverlist_local/recent.old")));
        Assert.Equal("127.0.0.1:2456\n", File.ReadAllText(At(Path.Combine(BackupFor("host"), "after"), "serverlist_local/recent")));
        Assert.Equal("127.0.0.1:2455\n", File.ReadAllText(At(Path.Combine(BackupFor("host"), "after"), "serverlist_local/recent.old")));
        await recovered.RestoreAsync();
        // A run version written again after a restore is never lost: the script refuses, as the in-process step does.
        Write(Save, "serverlist_local/recent.old", "another run version\n");
        await Assert.ThrowsAnyAsync<Exception>(recovered.RestoreAsync);
        Assert.Equal("another run version\n", File.ReadAllText(At(Save, "serverlist_local/recent.old")));
        File.Delete(At(Save, "serverlist_local/recent.old"));
        await recovered.RestoreAsync();
        Assert.Empty(Directory.GetFiles(Markers)); // Released with the restore.

        // A refused isolation is undone in place: what moved goes back, the user's newer file stays, the marker goes.
        Write(Save, "serverlist_local/recent.old", "the user's older servers\n");
        var undone = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("undo"), Markers, remote);
        Write(Save, "serverlist_local/recent", "the user's newer servers\n");
        await Assert.ThrowsAnyAsync<Exception>(undone.IsolateAsync);
        await undone.UndoIsolateAsync();
        Assert.Equal("the user's newer servers\n", File.ReadAllText(At(Save, "serverlist_local/recent")));
        Assert.Equal("the user's older servers\n", File.ReadAllText(At(Save, "serverlist_local/recent.old")));
        Assert.Empty(Directory.GetFiles(Markers));
    }

    // #822 review: two runs at once on one folder. Without one owner at a time, A captures and moves the list aside, B captures
    // "absent", A restores, B restores and deletes the user's list. B's capture is refused instead, changing nothing; once A has
    // restored, the folder is free again.
    [Fact] public void ASecondRunCannotCaptureAFolderAnotherRunHasMovedAside()
    {
        Write(Save, "serverlist_local/recent", "the user's servers\n");
        var a = UserSaveFiles.Capture(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("a"), Markers);
        a.Isolate();
        var refused = Assert.Throws<IOException>(() => UserSaveFiles.Capture(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("b"), Markers));
        Assert.Contains(BackupFor("a"), refused.Message);
        Assert.False(Directory.Exists(BackupFor("b")));
        Write(Save, "serverlist_local/recent", "127.0.0.1:2456\n");
        a.Restore();
        Assert.Equal("the user's servers\n", File.ReadAllText(Recent));
        Assert.Empty(Directory.GetFiles(Markers));
        var c = UserSaveFiles.Capture(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("c"), Markers);
        c.Restore();
        Assert.Equal("the user's servers\n", File.ReadAllText(Recent));
        // The Mac server's lists in the same folder are other files: their capture is not held by the client's.
        var lists = UserSaveFiles.Capture(UserSaveFiles.Kind.MacServerLists, Save, BackupFor("server"), Markers);
        lists.Restore();
    }

    // #822 review: an isolation that refuses (a file changed between capture and isolate) starts nothing, so a present file is the
    // user's newer one: it stays, and only what was moved goes back.
    [Fact] public void ARefusedIsolationPutsBackOnlyWhatMovedAndKeepsTheUsersNewerFile()
    {
        Write(Save, "serverlist_local/recent", "the user's servers\n");
        Write(Save, "serverlist_local/recent.old", "the user's older servers\n");
        using (new Scope(Save, Journal))
        {
            var guard = new UserSaveFilesGuard(() =>
            {
                var files = UserSaveFiles.Capture(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("x"), Markers);
                Write(Save, "serverlist_local/recent", "the user's newer servers\n"); // The user's own game, between the two steps.
                return Task.FromResult(files);
            }, entry => { RunJournal.ThisProcess.AppendLocal("client", entry); return Task.CompletedTask; });
            Assert.Throws<IOException>(guard.Protect);
        }
        Assert.Equal("the user's newer servers\n", File.ReadAllText(Recent));
        Assert.Equal("the user's older servers\n", File.ReadAllText(At(Save, "serverlist_local/recent.old")));
        Assert.False(Directory.Exists(Path.Combine(BackupFor("x"), "after")));
        Assert.Empty(Directory.GetFiles(Markers));
        Assert.Equal(new[] { JournalEntry.UserFilesCaptured, JournalEntry.UserFilesRestored }, Kinds());
    }

    // #822 review: a start that failed without proving no process exists (an ambiguous host reply, an unproven stop) leaves the
    // list aside and journalled, so a client that may still run never writes into the user's restored list.
    [Fact] public void AStartThatMayHaveLeftAProcessKeepsTheCaptureForRecovery()
    {
        Write(Save, "serverlist_local/recent", "the user's servers\n");
        using (new Scope(Save, Journal))
            Assert.Throws<TimeoutException>(() => ClientSession.Launch(Plan(), Output, () => throw new TimeoutException("no reply from the host"),
                () => new ScriptedTransport(), (_, _) => Task.CompletedTask, default, null, null, saveFiles: UserSaveFilesGuard.ForLocalClient(Output)));
        Assert.False(File.Exists(Recent));
        Assert.Equal(new[] { JournalEntry.UserFilesCaptured }, Kinds());
        Assert.Single(Directory.GetFiles(UserSaveFilesMarkers)); // Still held: env recover releases it with the restore.
    }

    private string UserSaveFilesMarkers => Path.Combine(_root, "user-files");

    private static ClientRunPlan Plan() => new()
    {
        Mode = "owned", Install = Path.GetFullPath("client-install"), Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
        InstallPins = new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) },
    };

    private sealed class Scope : IDisposable
    {
        private readonly IDisposable[] _scopes;
        public Scope(string save, string journal) =>
            _scopes = [new FakeClientDataDirectory(save), RunJournal.UseLocalDirectory(journal), RunJournal.UseRun(Run)];
        public void Dispose() { foreach (var scope in _scopes.Reverse()) scope.Dispose(); }
    }

    private string Journal => Path.Combine(_root, "journal");
    private string Output => Directory.CreateDirectory(Path.Combine(_root, "evidence")).FullName;
    private IEnumerable<string> Kinds() => File.ReadAllLines(Path.Combine(Journal, Run, "client.jsonl")).Select(line => RunJournal.ParseLine(line).Entry.Kind);
    private string Recent => At(Save, "serverlist_local/recent");

    // The client runs with the user's list moved aside, writes its own (as the game adds the server it joined), and once it is
    // stopped the user's list is back and the client's is kept with the evidence.
    [Fact] public void AnOwnedClientRunsWithTheUsersRecentServersListAsideAndLeavesItAsFound()
    {
        Write(Save, "serverlist_local/recent", "the user's servers\n");
        using (new Scope(Save, Journal))
        {
            var process = new FakeOwnedProcess(99);
            using (var session = ClientSession.Launch(Plan(), Output, () =>
            {
                Assert.False(File.Exists(Recent));
                Write(Save, "serverlist_local/recent", "127.0.0.1:2456\n");
                return process;
            }, () => new ScriptedTransport(), (_, _) => Task.CompletedTask, default, null, null, saveFiles: UserSaveFilesGuard.ForLocalClient(Output)))
                Assert.Equal("127.0.0.1:2456\n", File.ReadAllText(Recent));
            Assert.Equal(1, process.Stops);
        }
        Assert.Equal("the user's servers\n", File.ReadAllText(Recent));
        string backup = Assert.Single(Directory.GetDirectories(Output, "user-files-" + Run + "-client-*"));
        Assert.Equal("127.0.0.1:2456\n", File.ReadAllText(At(Path.Combine(backup, "after"), "serverlist_local/recent")));
        Assert.Equal(new[] { JournalEntry.UserFilesCaptured, JournalEntry.UserFilesRestored }, Kinds());
    }

    [Fact] public void AClientWhoseStartupFailsOrThatProvablyNeverStartedLeavesTheListAsFound()
    {
        Write(Save, "serverlist_local/recent", "the user's servers\n");
        using (new Scope(Save, Journal))
        {
            Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), Output, () =>
            {
                Write(Save, "serverlist_local/recent", "127.0.0.1:2456\n");
                return FakeOwnedProcess.Exited(1, 98);
            }, () => new ScriptedTransport(), (_, _) => Task.CompletedTask, default, null, null, saveFiles: UserSaveFilesGuard.ForLocalClient(Output)));
            Assert.Equal("the user's servers\n", File.ReadAllText(Recent));
            Assert.Throws<IOException>(() => ClientSession.Launch(Plan(), Output, () => throw UserSaveFilesGuard.NoProcess(new IOException("refused before any process")),
                () => new ScriptedTransport(), (_, _) => Task.CompletedTask, default, null, null, saveFiles: UserSaveFilesGuard.ForLocalClient(Output)));
        }
        Assert.Equal("the user's servers\n", File.ReadAllText(Recent));
        Assert.Equal(new[] { JournalEntry.UserFilesCaptured, JournalEntry.UserFilesRestored, JournalEntry.UserFilesCaptured, JournalEntry.UserFilesRestored }, Kinds());
    }

    // A client whose stop is unproven may still write the list: it stays aside, journalled for env recover.
    [Fact] public void AClientWhoseStopIsUnprovenLeavesTheCaptureForRecovery()
    {
        Write(Save, "serverlist_local/recent", "the user's servers\n");
        using (new Scope(Save, Journal))
        {
            var process = new FakeOwnedProcess(97) { StopFailure = () => new IOException("unproven") };
            var session = ClientSession.Launch(Plan(), Output, () => process, () => new ScriptedTransport(), (_, _) => Task.CompletedTask,
                default, null, null, saveFiles: UserSaveFilesGuard.ForLocalClient(Output));
            Assert.Throws<IOException>(session.Dispose);
        }
        Assert.False(File.Exists(Recent));
        Assert.Equal(new[] { JournalEntry.UserFilesCaptured }, Kinds());
    }
}
