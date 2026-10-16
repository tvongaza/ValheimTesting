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

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompletedRestoreWithLostJournalNeverReplaysOverNewerPersonalFiles(bool shell)
    {
        var remote = shell ? UserSaveFilesOnHost.Remote(new LocalGameHost("this machine", HostShell.ThisMachine), TimeSpan.FromSeconds(60)) : null;
        Write(Save, "serverlist_local/recent", "original personal servers\n");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("lost"), Markers, remote);
        await files.IsolateAsync();
        // No run version exists. Complete restoration, then lose only its journal reply.
        await files.RestoreAsync();
        Write(Save, "serverlist_local/recent", "newer personal servers\n");
        var recovered = UserSaveFiles.FromJournal(files.Captured().Fields, Run, Save, remote);
        await recovered.RestoreAsync();
        Assert.Equal("newer personal servers\n", File.ReadAllText(At(Save, "serverlist_local/recent")));
        Assert.False(File.Exists(At(Path.Combine(BackupFor("lost"), "after"), "serverlist_local/recent")));
    }

    private UserSaveFiles.Remote? Shell(bool shell) => shell
        ? UserSaveFilesOnHost.Remote(new LocalGameHost("this machine", HostShell.ThisMachine), TimeSpan.FromSeconds(60)) : null;

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task InterruptedRestoreKeepsOnePlanAndRefusesNewerBytesBeforeAnyMoreWrites(bool shell, bool mac)
    {
        var remote = Shell(shell);
        var kind = mac ? UserSaveFiles.Kind.MacServerLists : UserSaveFiles.Kind.ClientServerList;
        var names = UserSaveFiles.Names(kind);
        Write(Save, names[0], "original first"); Write(Save, names[1], "original second");
        var files = await UserSaveFiles.CaptureAsync(kind, Save, BackupFor("partial"), Markers, remote);
        await files.IsolateAsync();
        Write(Save, names[0], "run first"); Write(Save, names[1], "run second");
        await files.PrepareRestoreAsync();
        byte[] plan = File.ReadAllBytes(files.Marker);
        // Controlled stop after one complete file mutation, before completion is published.
        Write(Path.Combine(files.Backup, "after"), names[0], "run first");
        Write(Save, names[0], "original first");
        Write(Save, names[1], "newer personal second");
        var recovered = UserSaveFiles.FromJournal(files.Captured().Fields, Run, Save, remote);
        await Assert.ThrowsAnyAsync<Exception>(recovered.RestoreAsync);
        Assert.Equal(plan, File.ReadAllBytes(files.Marker));
        Assert.Equal("original first", File.ReadAllText(At(Save, names[0])));
        Assert.Equal("newer personal second", File.ReadAllText(At(Save, names[1])));
        Assert.False(File.Exists(At(Path.Combine(files.Backup, "after"), names[1])));
        Write(Save, names[1], "run second"); // Returning to the immutable planned state makes the interrupted restore resumable.
        await recovered.RestoreAsync();
        Assert.Equal("original second", File.ReadAllText(At(Save, names[1])));
        Assert.Equal("run second", File.ReadAllText(At(Path.Combine(files.Backup, "after"), names[1])));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task MissingChangedOrLegacyMarkerNeverAuthorizesUserFileWrites(bool shell, bool missing)
    {
        Write(Save, "serverlist_local/recent", "original");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("ownership"), Markers, Shell(shell));
        await files.IsolateAsync(); Write(Save, "serverlist_local/recent", "new personal");
        if (missing) File.Delete(files.Marker); else File.WriteAllText(files.Marker, files.Backup + "\n"); // Old plain-path format.
        await Assert.ThrowsAnyAsync<Exception>(files.RestoreAsync);
        Assert.Equal("new personal", File.ReadAllText(Recent));
        Assert.False(Directory.Exists(Path.Combine(files.Backup, "after")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompletedOwnerBeforeReceiptPublicationAndOldReceiptCannotTouchNewCapture(bool shell)
    {
        var remote = Shell(shell);
        Write(Save, "serverlist_local/recent", "original");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("completed"), Markers, remote);
        await files.IsolateAsync(); await files.RestoreAsync();
        File.Move(files.Receipt, files.Marker); // Controlled stop between completed phase and terminal rename.
        Write(Save, "serverlist_local/recent", "new personal");
        await files.RestoreAsync();
        Assert.Equal("new personal", File.ReadAllText(Recent));
        var next = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("next"), Markers, remote);
        byte[] marker = File.ReadAllBytes(next.Marker);
        await next.IsolateAsync();
        marker = File.ReadAllBytes(next.Marker);
        await files.RestoreAsync();
        Assert.Equal(marker, File.ReadAllBytes(next.Marker));
        Assert.False(File.Exists(Recent));
        await next.RestoreAsync();
        Assert.Equal("new personal", File.ReadAllText(Recent));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RestoreIntentJournalFailureDoesNotMutateAndCompletedJournalFailureCannotReplay(bool shell)
    {
        var remote = Shell(shell);
        Write(Save, "serverlist_local/recent", "original");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("journal"), Markers, remote);
        await files.IsolateAsync(); Write(Save, "serverlist_local/recent", "run");
        await Assert.ThrowsAsync<IOException>(() => files.RestoreAsync(_ => throw new IOException("no durable intent reply")));
        Assert.Equal("run", File.ReadAllText(Recent));
        await Assert.ThrowsAsync<IOException>(() => files.RestoreAsync(entry =>
            entry.Kind == JournalEntry.UserFilesRestored ? throw new IOException("lost completion reply") : Task.CompletedTask));
        Write(Save, "serverlist_local/recent", "later personal");
        await files.RestoreAsync();
        Assert.Equal("later personal", File.ReadAllText(Recent));
        Assert.Equal("run", File.ReadAllText(At(Path.Combine(files.Backup, "after"), "serverlist_local/recent")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CaptureIntentPrecedesOwnershipAndAnAmbiguousCapturedReplyCanOnlyUndo(bool shell)
    {
        var remote = Shell(shell);
        Write(Save, "serverlist_local/recent", "original");
        JournalEntry? intent = null;
        await Assert.ThrowsAsync<IOException>(() => UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("capture"), Markers, remote, entry =>
        {
            if (entry.Kind == JournalEntry.UserFilesCaptureIntended)
            {
                intent = entry;
                Assert.False(Directory.Exists(Markers));
                Assert.Equal("original", File.ReadAllText(Recent));
                return Task.CompletedTask;
            }
            throw new IOException("lost captured journal reply");
        }));
        Write(Save, "serverlist_local/recent", "later personal");
        var recovered = UserSaveFiles.FromJournal(intent!.Fields, Run, Save, remote);
        await recovered.RestoreAsync();
        Assert.Equal("later personal", File.ReadAllText(Recent));
        Assert.Empty(Directory.GetFiles(Markers, "*.owner"));
        Assert.True(File.Exists(recovered.Receipt));
        // Intent whose capture never acquired the marker settles without touching another capture.
        var next = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("next"), Markers, remote);
        JournalEntry? neverAcquired = null;
        await Assert.ThrowsAsync<IOException>(() => UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("never"), Markers, remote, entry =>
        { neverAcquired = entry; throw new IOException("failed before acquisition"); }));
        byte[] nextMarker = File.ReadAllBytes(next.Marker);
        await UserSaveFiles.FromJournal(neverAcquired!.Fields, Run, Save, remote).RestoreAsync();
        Assert.Equal(nextMarker, File.ReadAllBytes(next.Marker));
        await next.UndoIsolateAsync();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OriginallyAbsentFileThatAppearsAfterPlanningCannotBeDeletedByRetry(bool shell)
    {
        var remote = Shell(shell);
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("absent"), Markers, remote);
        await files.IsolateAsync(); await files.PrepareRestoreAsync();
        Write(Save, "serverlist_local/recent.old", "new personal old list");
        await Assert.ThrowsAnyAsync<Exception>(files.RestoreAsync);
        Assert.Equal("new personal old list", File.ReadAllText(At(Save, "serverlist_local/recent.old")));
        Assert.False(File.Exists(Recent));
        Assert.False(Directory.Exists(Path.Combine(files.Backup, "after")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ALinkedOwnerMarkerRefusesBeforeUserOrEvidenceWrites(bool shell)
    {
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("linked"), Markers, Shell(shell));
        await files.IsolateAsync(); Write(Save, "serverlist_local/recent", "new personal");
        string held = files.Marker + ".held"; File.Move(files.Marker, held);
        File.CreateSymbolicLink(files.Marker, held);
        byte[] record = File.ReadAllBytes(held);
        await Assert.ThrowsAnyAsync<Exception>(files.RestoreAsync);
        Assert.Equal("new personal", File.ReadAllText(Recent));
        Assert.Equal(record, File.ReadAllBytes(held));
        Assert.False(Directory.Exists(Path.Combine(files.Backup, "after")));
        File.Delete(files.Marker);
    }

    [Fact] public async Task SourceChangingBeforeShellMarkerAcquisitionCancelsWithoutReplay()
    {
        Write(Save, "serverlist_local/recent", "original");
        var runner = Shell(true)!;
        var interrupted = new UserSaveFiles.Remote(runner.PowerShell, (script, variables) => runner.Run(
            runner.PowerShell
                ? script.Replace("Write-VtOwner $owner $true", "[IO.File]::WriteAllText((Join-VtPath $saveRoot 'serverlist_local/recent'), 'new personal'); Write-VtOwner $owner $true")
                : script.Replace("phase=captured; write_owner acquire", "printf '%s' 'new personal' > \"$saveRoot/serverlist_local/recent\"; phase=captured; write_owner acquire"), variables));
        JournalEntry? intent = null;
        await Assert.ThrowsAnyAsync<Exception>(() => UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("capture-race"), Markers,
            interrupted, entry => { intent = entry; return Task.CompletedTask; }));
        Assert.Equal(JournalEntry.UserFilesCaptureIntended, intent!.Kind);
        Assert.Equal("original", File.ReadAllText(At(BackupFor("capture-race"), "serverlist_local/recent")));
        var recovered = UserSaveFiles.FromJournal(intent.Fields, Run, Save, runner);
        await recovered.RestoreAsync();
        Assert.Equal("new personal", File.ReadAllText(Recent));
        Assert.True(File.Exists(recovered.Receipt));
        Assert.Empty(Directory.GetFiles(Markers, "*.owner"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InterruptedIsolationRestoresMissingFilesAndKeepsPresentPersonalFiles(bool shell)
    {
        var remote = Shell(shell);
        Write(Save, "serverlist_local/recent", "original first"); Write(Save, "serverlist_local/recent.old", "original second");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("isolation"), Markers, remote);
        // The same durable record the owner publishes before its first deletion, with a stop after that deletion.
        string[] record = File.ReadAllLines(files.Marker); record[2] = "isolating";
        File.WriteAllText(files.Marker, string.Join('\n', record) + "\n");
        File.Delete(Recent); Write(Save, "serverlist_local/recent.old", "new personal second");
        await UserSaveFiles.FromJournal(files.Captured().Fields, Run, Save, remote).RestoreAsync();
        Assert.Equal("original first", File.ReadAllText(Recent));
        Assert.Equal("new personal second", File.ReadAllText(At(Save, "serverlist_local/recent.old")));
        Assert.False(Directory.Exists(Path.Combine(files.Backup, "after")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ACompletedCaptureIdentityCannotBeReusedAfterItsBackupWasArchived(bool shell)
    {
        var remote = Shell(shell);
        Write(Save, "serverlist_local/recent", "original");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("reuse"), Markers, remote);
        await files.IsolateAsync(); await files.RestoreAsync();
        Directory.Move(files.Backup, files.Backup + ".archived");
        Write(Save, "serverlist_local/recent", "new personal");
        await Assert.ThrowsAnyAsync<Exception>(() => UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, files.Backup, Markers, remote));
        Assert.False(Directory.Exists(files.Backup)); Assert.False(File.Exists(files.Marker));
        Assert.Equal("new personal", File.ReadAllText(Recent));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task AConcurrentEditBetweenFilesIsPreservedByRestoreAndUndo(bool shell, bool undo)
    {
        var runner = Shell(shell);
        var remote = runner == null ? null : new UserSaveFiles.Remote(runner.PowerShell, (script, variables) => runner.Run(
            script.Replace("# file-restored", runner.PowerShell
                ? "if ($name -eq 'adminlist.txt') { [IO.File]::WriteAllText((Join-VtPath $saveRoot 'permittedlist.txt'), 'new personal second') }"
                : "if [ \"$name\" = adminlist.txt ]; then printf '%s' 'new personal second' > \"$saveRoot/permittedlist.txt\"; fi"), variables));
        Write(Save, "adminlist.txt", "original first"); Write(Save, "permittedlist.txt", "original second");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.MacServerLists, Save, BackupFor("between"), Markers, remote);
        await files.IsolateAsync();
        if (!undo)
        {
            Write(Save, "adminlist.txt", "run first"); Write(Save, "permittedlist.txt", "run second");
            // An interrupted previous attempt already kept this version; the newer edit must not be lost behind it.
            Write(Path.Combine(files.Backup, "after"), "permittedlist.txt", "run second");
        }
        await files.PrepareRestoreAsync(undo);
        files.AfterRestoredFileForTest = name => { if (name == "adminlist.txt") Write(Save, "permittedlist.txt", "new personal second"); };
        await Assert.ThrowsAnyAsync<Exception>(undo ? files.UndoIsolateAsync : files.RestoreAsync);
        Assert.Equal("original first", File.ReadAllText(At(Save, "adminlist.txt")));
        Assert.Equal("new personal second", File.ReadAllText(At(Save, "permittedlist.txt")));
        Assert.True(File.Exists(files.Marker)); Assert.False(File.Exists(files.Receipt));
        if (!undo) Assert.Equal("run second", File.ReadAllText(At(Path.Combine(files.Backup, "after"), "permittedlist.txt")));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task AReplacedSaveRootOrNestedDirectoryCannotRedirectRestoration(bool shell, bool root)
    {
        Write(Save, "serverlist_local/recent", "original personal");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("escape"), Markers, Shell(shell));
        await files.IsolateAsync();
        string external = Path.Combine(_root, "external");
        Write(external, root ? "serverlist_local/recent" : "recent", "external personal file");
        string replaced = root ? Save : At(Save, "serverlist_local");
        if (Directory.Exists(replaced)) Directory.Move(replaced, replaced + ".held");
        else Directory.CreateDirectory(Path.GetDirectoryName(replaced)!);
        DirectoryLinks.Create(replaced, external); // A junction on Windows CI, a symlink on Unix.
        try
        {
            byte[] owner = File.ReadAllBytes(files.Marker);
            await Assert.ThrowsAnyAsync<Exception>(files.RestoreAsync);
            Assert.Equal("external personal file", File.ReadAllText(At(external, root ? "serverlist_local/recent" : "recent")));
            Assert.Equal(owner, File.ReadAllBytes(files.Marker));
            Assert.False(Directory.Exists(Path.Combine(files.Backup, "after")));
        }
        finally { DirectoryLinks.Remove(replaced); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AnUnisolatedCaptureCannotUndoTheUsersLaterDeletion(bool shell)
    {
        var remote = Shell(shell);
        Write(Save, "serverlist_local/recent", "personal before capture");
        JournalEntry? intent = null;
        await Assert.ThrowsAsync<IOException>(() => UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("unisolated"), Markers, remote, entry =>
        {
            if (entry.Kind == JournalEntry.UserFilesCaptureIntended) { intent = entry; return Task.CompletedTask; }
            throw new IOException("no captured acknowledgement; no isolation could start");
        }));
        File.Delete(Recent);
        var recovered = UserSaveFiles.FromJournal(intent!.Fields, Run, Save, remote);
        await recovered.RestoreAsync();
        Assert.False(File.Exists(Recent));
        Assert.True(File.Exists(recovered.Receipt)); Assert.False(File.Exists(recovered.Marker));
        Assert.Equal("personal before capture", File.ReadAllText(At(recovered.Backup, "serverlist_local/recent")));
    }

    [Theory]
    [InlineData(false, "backup")] [InlineData(true, "backup")]
    [InlineData(false, "backup-nested")] [InlineData(true, "backup-nested")]
    [InlineData(false, "after")] [InlineData(true, "after")]
    [InlineData(false, "markers")] [InlineData(true, "markers")]
    public async Task LinkedBackupEvidenceOrMarkerAncestorsCannotRedirectRecovery(bool shell, string target)
    {
        Write(Save, "serverlist_local/recent", "original personal");
        var files = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("linked-ancestors"), Markers, Shell(shell));
        await files.IsolateAsync(); Write(Save, "serverlist_local/recent", "run recent");
        byte[] owner = File.ReadAllBytes(files.Marker);
        string external = Path.Combine(_root, "external"); Write(external, "keep", "untouched");
        string replaced = target switch
        {
            "backup" => files.Backup,
            "backup-nested" => At(files.Backup, "serverlist_local"),
            "after" => Path.Combine(files.Backup, "after"),
            _ => Markers,
        };
        if (target == "backup") Write(external, "serverlist_local/recent", "original personal");
        if (target == "backup-nested") Write(external, "recent", "original personal");
        if (target == "markers") File.WriteAllBytes(Path.Combine(external, Path.GetFileName(files.Marker)), owner);
        string[] before = Directory.GetFiles(external, "*", SearchOption.AllDirectories).Order().ToArray();
        if (Directory.Exists(replaced)) Directory.Move(replaced, replaced + ".held");
        DirectoryLinks.Create(replaced, external);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(files.RestoreAsync);
            Assert.Equal("run recent", File.ReadAllText(Recent));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(external, "keep")));
            Assert.Equal(before, Directory.GetFiles(external, "*", SearchOption.AllDirectories).Order());
            Assert.Equal(owner, File.ReadAllBytes(files.Marker));
        }
        finally { DirectoryLinks.Remove(replaced); }
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
        files.Restore(); // A completion receipt settles only the journal, preserving later bytes.
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
        await recovered.RestoreAsync();
        Assert.Equal("another run version\n", File.ReadAllText(At(Save, "serverlist_local/recent.old")));
        File.Delete(At(Save, "serverlist_local/recent.old"));
        await recovered.RestoreAsync();
        Assert.Empty(Directory.GetFiles(Markers, "*.owner")); // Released with the restore.

        // A refused isolation is undone in place: what moved goes back, the user's newer file stays, the marker goes.
        Write(Save, "serverlist_local/recent.old", "the user's older servers\n");
        var undone = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("undo"), Markers, remote);
        Write(Save, "serverlist_local/recent", "the user's newer servers\n");
        await Assert.ThrowsAnyAsync<Exception>(undone.IsolateAsync);
        await undone.UndoIsolateAsync();
        Assert.Equal("the user's newer servers\n", File.ReadAllText(At(Save, "serverlist_local/recent")));
        Assert.Equal("the user's older servers\n", File.ReadAllText(At(Save, "serverlist_local/recent.old")));
        Assert.Empty(Directory.GetFiles(Markers, "*.owner"));
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
        Assert.Contains(a.Marker, refused.Message);
        Assert.False(Directory.Exists(BackupFor("b")));
        Write(Save, "serverlist_local/recent", "127.0.0.1:2456\n");
        a.Restore();
        Assert.Equal("the user's servers\n", File.ReadAllText(Recent));
        Assert.Empty(Directory.GetFiles(Markers, "*.owner"));
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
            var guard = new UserSaveFilesGuard(captureJournal =>
            {
                var files = UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, Save, BackupFor("x"), Markers, journal: captureJournal).GetAwaiter().GetResult();
                Write(Save, "serverlist_local/recent", "the user's newer servers\n"); // The user's own game, between the two steps.
                return Task.FromResult(files);
            }, entry => { RunJournal.ThisProcess.AppendLocal("client", entry); return Task.CompletedTask; });
            Assert.Throws<IOException>(guard.Protect);
        }
        Assert.Equal("the user's newer servers\n", File.ReadAllText(Recent));
        Assert.Equal("the user's older servers\n", File.ReadAllText(At(Save, "serverlist_local/recent.old")));
        Assert.False(Directory.Exists(Path.Combine(BackupFor("x"), "after")));
        Assert.Empty(Directory.GetFiles(Markers, "*.owner"));
        Assert.Equal(new[] { JournalEntry.UserFilesCaptureIntended, JournalEntry.UserFilesCaptured, JournalEntry.UserFilesRestoreIntended, JournalEntry.UserFilesRestored }, Kinds());
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
        Assert.Equal(new[] { JournalEntry.UserFilesCaptureIntended, JournalEntry.UserFilesCaptured }, Kinds());
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
        Assert.Equal(new[] { JournalEntry.UserFilesCaptureIntended, JournalEntry.UserFilesCaptured, JournalEntry.UserFilesRestoreIntended, JournalEntry.UserFilesRestored }, Kinds());
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
        Assert.Equal(new[] { JournalEntry.UserFilesCaptureIntended, JournalEntry.UserFilesCaptured, JournalEntry.UserFilesRestoreIntended, JournalEntry.UserFilesRestored, JournalEntry.UserFilesCaptureIntended, JournalEntry.UserFilesCaptured, JournalEntry.UserFilesRestoreIntended, JournalEntry.UserFilesRestored }, Kinds());
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
        Assert.Equal(new[] { JournalEntry.UserFilesCaptureIntended, JournalEntry.UserFilesCaptured }, Kinds());
    }
}
