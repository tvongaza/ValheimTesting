using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// #653: one source-check owner decides the four profile rows and keeps the whole-source post-run verdict; #612: it settles the
// install after the run, moving what the game added into the run's evidence, and a recovery settles it from the journalled baseline.
public sealed class ProfileSourceCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("profile-source-check-").FullName;
    private readonly LocalGameHost _host = new("source-host", HostShell.ThisMachine);
    // This machine answered in process, for the tests of what the owner decides: its settlement, listing and read answers are the
    // real scripts' (SourceScriptShellTests runs both and compares them), so no step starts a shell. Its own name, so a step sent to
    // the real host fails.
    private readonly FakeServerHost _fake = FakeServerHost.ThisMachine("source-host-in-process");

    public void Dispose()
    {
        ProfileSourceCheck.HashAfterRunOverride.Value = null;
        foreach (string folder in new[] { _root, _root + "-runs", Output, Output + "-content", Output + "-again", Output + "-resumed" })
            if (Directory.Exists(folder)) DirectoryLinks.DeleteTree(folder);
    }

    private Task<ProfileSourceCheck> PrepareAsync(params string[] links) => PrepareOnAsync(_host, links);
    private async Task<ProfileSourceCheck> PrepareOnAsync(IGameHost host, params string[] links) =>
        ProfileSourceCheck.Prepare(await ProfileSourceCheck.InspectAsync(host, _root, TimeSpan.FromSeconds(30)), links, links.Length == 0 ? ProfileLayout.SourceExecutable : ProfileLayout.Linked);

    // The profile beside the install, as every owned launch path lays it out: <runs>/vt-prep-*/runtime, its parent holding the baseline.
    private string Runtime => Path.GetFullPath(Path.Combine(_root, "..", Path.GetFileName(_root) + "-runs", "vt-prep-test-client", "runtime"));
    private string Parent => Path.GetDirectoryName(Path.GetFullPath(Runtime))!;
    private readonly List<JournalEntry> _journal = [];
    private string Output => Path.Combine(Path.GetFullPath(Path.Combine(_root, "..")), Path.GetFileName(_root) + "-output");

    private async Task<SourceVerdict> SettleAsync(ProfileSourceCheck check, string? output = null, IGameHost? host = null) => await check.SettleAsync(host ?? _host,
        SourceSettlement.Run("client", Path.GetFullPath(Runtime), output ?? Output), entry => { _journal.Add(entry); return Task.CompletedTask; },
        TimeSpan.FromSeconds(60));

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // #612: what the game adds is moved out into the run's evidence, its new folders removed, and the install lists as before.
    [Fact]
    public async Task AnAddedNestedFileIsMovedIntoTheEvidenceItsNewFoldersRemovedAndTheInstallEqualsTheBaseline()
    {
        Write("game.bin", "game");
        Write("BepInEx/core/BepInEx.dll", "loader");
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();
        Write("BepInEx/core/preloader_20261020.log", "beside the loader");
        Write("mods/written/deep/state.json", "a mod wrote this");
        Directory.CreateDirectory(Path.Combine(_root, "empty", "inner"));

        var verdict = await SettleAsync(check);

        Assert.Equal(SourceVerdictKind.Restored, verdict.Verdict);
        Assert.Equal(["BepInEx/core/preloader_20261020.log", "mods/written/deep/state.json"], verdict.Added.Select(file => file.Path));
        Assert.Equal("a mod wrote this", File.ReadAllText(Path.Combine(Output, "source-changes", "client", "added", "mods", "written", "deep", "state.json")));
        Assert.Equal("added/mods/written/deep/state.json", verdict.Added[1].KeptAs);
        Assert.Equal(FileHash.Sha256(Path.Combine(Output, "source-changes", "client", "added", "mods", "written", "deep", "state.json")), verdict.Added[1].Sha256);
        Assert.Contains("empty/inner/", verdict.RemovedDirectories);
        Assert.False(Directory.Exists(Path.Combine(_root, "mods")));
        Assert.False(Directory.Exists(Path.Combine(_root, "empty")));
        Assert.True(File.Exists(Path.Combine(_root, "BepInEx", "core", "BepInEx.dll")));
        // The install lists as the baseline did, and the host keeps nothing beside the profile: baseline and moved files are gone.
        var after = await ProfileSourceCheck.InspectAsync(_host, _root, TimeSpan.FromSeconds(30));
        Assert.Equal(check.Source.Files.Keys.Order(), after.Files.Keys.Order());
        Assert.Equal(check.Source.Directories.Order(), after.Directories.Order());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Parent));
        string json = File.ReadAllText(Path.Combine(Output, "source-changes", "client", "source-changes.json"));
        Assert.Contains("\"verdict\": \"restored\"", json);
        Assert.Contains("mods/written/deep/state.json", json);
        Assert.Equal([JournalEntry.SourceMoveIntended, JournalEntry.SourceSettled], _journal.Select(entry => entry.Kind));
        Assert.Contains("mods/written/deep/state.json", _journal[0].Fields["moving"]);

        // It passes, with a warning that names every path, as sourceIntegrity.client.
        var report = new ScenarioReport("settled");
        verdict.Record(report);
        var step = Assert.Single(report.Steps);
        Assert.True(step.Passed);
        Assert.Equal("verify client's source install after the run (size and modified time)", step.Name);
        Assert.StartsWith("restored with a warning: ", report.Provenance["sourceIntegrity.client"]);
        Assert.Contains("mods/written/deep/state.json", report.Provenance["sourceIntegrity.client"]);
        Assert.Contains("BepInEx/core/preloader_20261020.log", report.Provenance["sourceIntegrity.client"]);
    }

    [Theory]
    [InlineData("game.bin", true)]
    [InlineData("logs/steam.txt", true)]
    [InlineData("game.bin", false)]
    public async Task AChangedOrMissingFileFailsTheVerdictNamesItAndIsLeftAsItIs(string relative, bool changed)
    {
        Write(relative, "before");
        Write("other.bin", "other");
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();
        string path = Path.Combine(_root, relative);
        if (changed) File.WriteAllText(path, "changed during the fake run");
        else File.Delete(path);

        var verdict = await SettleAsync(check);

        Assert.Equal(SourceVerdictKind.Failed, verdict.Verdict);
        Assert.Equal(changed ? new[] { relative } : [], verdict.Changed.Select(file => file.Path));
        Assert.Equal(changed ? [] : new[] { relative }, verdict.Missing);
        var report = new ScenarioReport("failed");
        var error = Assert.Throws<IOException>(() => verdict.Record(report));
        Assert.Contains(relative, error.Message);
        Assert.Contains("not repaired", error.Message);
        Assert.False(Assert.Single(report.Steps).Passed);
        Assert.StartsWith("failed: ", report.Provenance["sourceIntegrity.client"]);
        if (changed) Assert.Equal("changed during the fake run", File.ReadAllText(path));
        else Assert.False(File.Exists(path));
        Assert.Contains(relative, File.ReadAllText(Path.Combine(Output, "source-changes", "client", "source-changes.json")));
    }

    // An added file the host cannot move (a read-only folder; on Windows a file another process holds) stays named, and fails.
    [Fact]
    public async Task AnAddedFileThatCannotBeMovedFailsTheVerdictAsNotMoved()
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root") return; // root moves out of a read-only folder
        Write("game.bin", "game");
        Directory.CreateDirectory(Path.Combine(_root, "locked"));
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();
        Write("locked/held.log", "the game holds this");
        string folder = Path.Combine(_root, "locked"), held = Path.Combine(folder, "held.log");
        FileStream? holder = null;
        if (OperatingSystem.IsWindows()) holder = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        else File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        SourceVerdict verdict;
        try { verdict = await SettleAsync(check); }
        finally
        {
            holder?.Dispose();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(SourceVerdictKind.Failed, verdict.Verdict);
        var notMoved = Assert.Single(verdict.NotMoved);
        Assert.Equal("locked/held.log", notMoved.Path);
        Assert.Equal("the move failed", notMoved.Reason);
        Assert.True(File.Exists(held));
        Assert.Contains("added and not moved: locked/held.log", Assert.Throws<IOException>(() => verdict.Record(null)).Message);
    }

    // A write through a hard link (a linked profile's file is the install's file) that keeps the size and modified time: metadata
    // cannot see it, a gate run's content hashes can.
    [Fact]
    public async Task AWriteThroughAHardLinkWithItsTimeRestoredIsCaughtByContentHashes()
    {
        Write("same.bin", "aaaa");
        string path = Path.Combine(_root, "same.bin");
        var modified = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);
        Directory.CreateDirectory(Parent);
        string linked = Path.Combine(Parent, "same.bin");
        string script = _host.Shell.Kind == HostShellKind.Bash ? "ln \"$source\" \"$target\""
            : "New-Item -ItemType HardLink -Path $target -Target $source -ErrorAction Stop | Out-Null";
        (await _host.RunAsync(script, new Dictionary<string, string> { ["source"] = path, ["target"] = linked }, TimeSpan.FromSeconds(30)))
            .EnsureSuccess("create hard link");
        var check = await PrepareAsync();
        File.WriteAllText(linked, "bbbb");
        File.SetLastWriteTimeUtc(linked, modified);
        File.Delete(linked);

        Assert.Equal(SourceVerdictKind.Unchanged, (await SettleAsync(check)).Verdict); // ordinary metadata verdict
        ProfileSourceCheck.HashAfterRunOverride.Value = true;
        var verdict = await SettleAsync(check, Output + "-content");
        Assert.Equal(SourceVerdictKind.Failed, verdict.Verdict);
        Assert.Equal("verify client's source install after the run (content hashes)", verdict.StepName);
        Assert.Contains("different content: same.bin", Assert.Throws<IOException>(() => verdict.Record(null)).Message);
    }

    // Settle is idempotent: a second settlement finds nothing to move, and one interrupted after its move keeps what it moved.
    [Fact]
    public async Task RepeatingTheSettlementIsANoOpAndAnInterruptedOneKeepsWhatItMoved()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareOnAsync(_fake);
        Write("preloader_1.log", "first");
        Assert.Equal(SourceVerdictKind.Restored, (await SettleAsync(check, host: _fake)).Verdict);
        var again = await SettleAsync(check, Output + "-again", _fake);
        Assert.Equal(SourceVerdictKind.Unchanged, again.Verdict);
        Assert.Empty(again.Added);
        Assert.False(Directory.Exists(Output + "-again"));

        // As if a settlement moved this and stopped before keeping it.
        Directory.CreateDirectory(check.MovedFilesFolder(Runtime));
        File.WriteAllText(Path.Combine(check.MovedFilesFolder(Runtime), "preloader_2.log"), "moved, not kept");
        var resumed = await SettleAsync(check, Output + "-resumed", _fake);
        Assert.Equal(SourceVerdictKind.Restored, resumed.Verdict);
        Assert.Equal("preloader_2.log", Assert.Single(resumed.Added).Path);
        Assert.Equal("moved, not kept", File.ReadAllText(Path.Combine(Output + "-resumed", "source-changes", "client", "added", "preloader_2.log")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Parent));
    }

    // Negative controls: a run that wrote nothing is unchanged and leaves no evidence folder; a preloader log the install already had stays.
    [Fact]
    public async Task NoWritesAreUnchangedAndALogAlreadyInTheBaselineIsNotMoved()
    {
        Write("game.bin", "game");
        Write("preloader_old.log", "from manual BepInEx use");
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();

        var verdict = await SettleAsync(check);

        Assert.Equal(SourceVerdictKind.Unchanged, verdict.Verdict);
        Assert.True(File.Exists(Path.Combine(_root, "preloader_old.log")));
        Assert.False(Directory.Exists(Output));
        var report = new ScenarioReport("unchanged");
        verdict.Record(report);
        Assert.Equal("unchanged", report.Provenance["sourceIntegrity.client"]);
        Assert.True(Assert.Single(report.Steps).Passed);
    }

    // The baseline a recovery settles from: persisted beside the profile, journalled with its hash, read back only when it matches.
    [Fact]
    public async Task APersistedBaselineIsReadBackFromItsJournalAndATamperedOneIsRefused()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Directory.CreateDirectory(Parent);
        var check = await PrepareOnAsync(_fake);
        await PersistAsync(check, _fake);
        var baseline = Assert.Single(_journal);
        Assert.Equal(JournalEntry.SourceBaseline, baseline.Kind);
        Assert.Equal(Path.Combine(Parent, ProfileSourceCheck.BaselineFile), baseline.Fields["baseline"]);
        Assert.Equal((_root, "client", check.MovedFilesFolder(Runtime)), (baseline.Fields["sourceRoot"], baseline.Fields["sourceActor"], baseline.Fields["sourceAdded"]));

        Write("preloader_1.log", "after the run");
        var verdict = await RecoverAsync(baseline.Fields, host: _fake);
        Assert.Equal(SourceVerdictKind.Restored, verdict!.Verdict);
        Assert.Equal("after the run", File.ReadAllText(Path.Combine(RecoveredProfile, "added", "preloader_1.log")));
        Assert.Contains("\"verdict\": \"restored\"", File.ReadAllText(Path.Combine(RecoveredProfile, "source-changes.json")));
        Assert.False(File.Exists(baseline.Fields["baseline"])); // settled and journalled: the baseline went last
        Assert.Equal([JournalEntry.SourceBaseline, JournalEntry.SourceMoveIntended, JournalEntry.SourceSettled], _journal.Select(entry => entry.Kind));

        await PersistAsync(check, _fake);
        File.AppendAllText(baseline.Fields["baseline"], " ");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RecoverAsync(baseline.Fields, host: _fake));
        Assert.Contains("differs from its journalled SHA-256", error.Message);
    }

    // The copy's one recovered folder (its journal actor's, beside its logs), and in it this profile's own source-changes-<id>.
    private string Recovered => Path.Combine(_root + "-runs", "run", "recovered-regression-test");
    private string RecoveredProfile => Assert.Single(Directory.GetDirectories(Recovered, "source-changes-*"));
    private Task PersistAsync(ProfileSourceCheck check, IGameHost? host = null) =>
        check.PersistAsync(host ?? _host, Path.GetFullPath(Runtime), "client", entry => { _journal.Add(entry); return Task.CompletedTask; }, TimeSpan.FromSeconds(30));
    private Task<SourceVerdict?> RecoverAsync(IReadOnlyDictionary<string, string> fields, Func<string, bool>? keptApart = null, bool teardown = false, IGameHost? host = null) =>
        ProfileSourceCheck.RecoverAsync(host ?? _host, Path.GetFullPath(Runtime), "regression-test", fields, Recovered, keptApart ?? (_ => false), teardown,
            entry => { _journal.Add(entry); return Task.CompletedTask; }, TimeSpan.FromSeconds(60));

    // #612 item 1: the baseline goes only after source-settled is journalled. One gone without that line is an explicit verdict,
    // never a copy kept forever; one gone after it is settled, and what it had moved but not kept is kept now.
    [Fact]
    public async Task ARecoveryOfAGoneBaselineNamesItOrKeepsWhatWasMovedAndNeverWedges()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareOnAsync(_fake);
        await PersistAsync(check, _fake);
        var fields = new Dictionary<string, string>(_journal[0].Fields);
        File.Delete(fields["baseline"]);

        var unknown = await RecoverAsync(fields, host: _fake);
        Assert.Equal(SourceVerdictKind.Unknown, unknown!.Verdict);
        Assert.Contains("is gone, so the install was not compared", unknown.Reason);

        // Settled by the run, then interrupted before its moved files were kept.
        fields["sourceVerdict"] = "Restored";
        Assert.Null(await RecoverAsync(fields, host: _fake));
        Directory.CreateDirectory(check.MovedFilesFolder(Runtime));
        File.WriteAllText(Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log"), "moved, not kept");
        var adopted = await RecoverAsync(fields, host: _fake);
        Assert.Equal(SourceVerdictKind.Restored, adopted!.Verdict);
        Assert.Equal("moved, not kept", File.ReadAllText(Path.Combine(RecoveredProfile, "added", "preloader_1.log")));
        Assert.False(Directory.Exists(check.MovedFilesFolder(Runtime)));
    }

    // #612 item 2: a settlement that does not finish (here its evidence step fails) keeps the profile, its baseline and its moved files,
    // journalled kept, and a recovery settles it again.
    [Fact]
    public async Task ASettlementThatDoesNotFinishKeepsTheProfileForARecoveryToSettleAgain()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareOnAsync(_fake);
        await PersistAsync(check, _fake);
        var fields = _journal[0].Fields;
        Write("preloader_1.log", "the game's");
        // The evidence step's connection is lost.
        _fake.Failures["source-evidence"] = new HostResult(HostOutcome.TransportFailed, null, "", "connection lost", TimeSpan.Zero, false);

        var verdict = await check.SettleAsync(_fake, SourceSettlement.Run("client", Path.GetFullPath(Runtime), Output),
            entry => { _journal.Add(entry); return Task.CompletedTask; }, TimeSpan.FromSeconds(60));
        _fake.Failures.Remove("source-evidence");

        Assert.True(verdict.KeepsProfile);
        Assert.False(verdict.Settled);
        Assert.Equal(SourceVerdictKind.Failed, verdict.Verdict);
        Assert.Contains("did not finish", verdict.Reason);
        Assert.Equal(JournalEntry.CopyKept, _journal[^1].Kind);
        Assert.Equal(Path.GetFullPath(Runtime), _journal[^1].Fields["runtime"]);
        Assert.True(File.Exists(fields["baseline"]));
        Assert.True(File.Exists(Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log")));
        Assert.Throws<IOException>(() => verdict.Record(null));

        var settled = await RecoverAsync(new Dictionary<string, string>(fields) { ["sourceVerdict"] = "Failed" }, host: _fake);
        Assert.Equal(SourceVerdictKind.Restored, settled!.Verdict);
        Assert.True(settled.Settled);
        Assert.Equal("the game's", File.ReadAllText(Path.Combine(RecoveredProfile, "added", "preloader_1.log")));
        Assert.False(File.Exists(fields["baseline"]));
    }

    // #612 item 1: the baseline goes only after source-settled is journalled; a journal that refuses that line leaves the baseline,
    // the moved files and the profile for a recovery, never a recovery that finds neither a baseline nor a settlement.
    [Fact]
    public async Task ABaselineStaysWhenItsSettlementCannotBeJournalled()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();
        await PersistAsync(check);
        string baseline = _journal[0].Fields["baseline"];
        Write("preloader_1.log", "the game's");

        var verdict = await check.SettleAsync(_host, SourceSettlement.Run("client", Runtime, Output), entry =>
        {
            if (entry.Kind == JournalEntry.SourceSettled) throw new IOException("the journal's disk is full");
            _journal.Add(entry);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(60));

        Assert.True(verdict.KeepsProfile);
        Assert.True(File.Exists(baseline));
        Assert.True(File.Exists(Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log")));
        Assert.Equal(JournalEntry.CopyKept, _journal[^1].Kind);
    }

    // #612 r2 item 2: a refusal that cannot change (here a profile whose moved-files folder would lie inside the install) is a finished
    // failure naming it, never a retry: the profile may retire, and what the settlement kept is journalled for env teardown.
    [Fact]
    public async Task ARefusedSettlementIsAFinishedFailureAndTheProfileRetires()
    {
        Write("game.bin", "game");
        var check = await PrepareAsync();
        string inside = Path.Combine(_root, "vt-prep-inside", "runtime");
        Write("preloader_1.log", "the game's");

        var verdict = await check.SettleAsync(_host, SourceSettlement.Run("client", inside, Output),
            entry => { _journal.Add(entry); return Task.CompletedTask; }, TimeSpan.FromSeconds(60));

        Assert.Equal(SourceVerdictKind.Failed, verdict.Verdict);
        Assert.True(verdict.Settled);
        verdict.RequireRetirable();
        Assert.Contains("refused", verdict.Reason);
        Assert.Contains("Resolve it by hand", verdict.Reason);
        Assert.True(File.Exists(Path.Combine(_root, "preloader_1.log")));
        // Nothing moved, and the folder would lie inside the install: nothing is journalled kept; the settlement once.
        Assert.Equal([JournalEntry.SourceMoveIntended, JournalEntry.SourceSettled], _journal.Select(entry => entry.Kind));
        Assert.Empty(verdict.Added);
    }

    // #612 r2 item 2: each recovered profile has its own folder, and a folder already there is a finished refusal, never a retry.
    [Fact]
    public async Task ARecoveryIntoAFolderAlreadyThereIsAFinishedRefusal()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();
        await PersistAsync(check);
        var fields = _journal[0].Fields;
        string id = Path.GetFileName(fields["sourceAdded"])[(ProfileSourceCheck.AddedFolder.Length + 1)..];
        Directory.CreateDirectory(Path.Combine(Recovered, "source-changes-" + id, "added"));
        // What an interrupted run moved, which the recovery adopts first.
        Directory.CreateDirectory(check.MovedFilesFolder(Runtime));
        File.WriteAllText(Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log"), "the game's");

        var verdict = await RecoverAsync(fields);

        Assert.Equal(SourceVerdictKind.Failed, verdict!.Verdict);
        Assert.True(verdict.Settled);
        Assert.Contains("already exists", verdict.Reason);
        Assert.True(File.Exists(Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log"))); // kept where it was, journalled
        // r3 item 4: named where it is, not where it would have gone.
        Assert.Equal(_host.Name + ":" + Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log"), Assert.Single(verdict.Added).KeptAs);
        Assert.Contains(_journal, entry => entry.Kind == JournalEntry.CopyKept && entry.Fields["runtime"] == check.MovedFilesFolder(Runtime));
        Assert.Single(_journal, entry => entry.Kind == JournalEntry.SourceSettled);
    }

    // r3 item 1: an install that is gone, or that now holds a link, is a state of the host: a finished failure naming it, so the
    // profile retires instead of waiting on a recovery that can never settle it.
    [Fact]
    public async Task AnInstallThatIsGoneOrHoldsALinkIsAFinishedFailure()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareOnAsync(_fake);
        await PersistAsync(check, _fake);
        var fields = _journal[0].Fields;
        string outside = _root + "-runs";
        DirectoryLinks.Create(Path.Combine(_root, "linked"), outside);

        var linked = await SettleAsync(check, host: _fake);
        Assert.Equal((SourceVerdictKind.Failed, true), (linked.Verdict, linked.Settled));
        Assert.Contains("now holds a link", linked.Reason);
        DirectoryLinks.Remove(Path.Combine(_root, "linked"));

        // r4 item 0: an install that is not there may be an offline NAS or an unplugged volume: kept for a later settlement, except
        // under env teardown, which removes the profile and names the install gone.
        Directory.Delete(_root, recursive: true);
        var unreachable = await SettleAsync(check, Output + "-again", _fake);
        Assert.Equal((SourceVerdictKind.Unknown, false), (unreachable.Verdict, unreachable.Settled));
        Assert.Contains("not reachable", unreachable.Reason);
        Assert.Throws<SourceProfileKeptException>(() => unreachable.RequireRetirable());
        var recovered = await RecoverAsync(fields, host: _fake);
        Assert.Equal((SourceVerdictKind.Unknown, false), (recovered!.Verdict, recovered.Settled));
        var gone = await RecoverAsync(fields, teardown: true, host: _fake);
        Assert.Equal((SourceVerdictKind.Failed, true), (gone!.Verdict, gone.Settled));
        Assert.Contains("is gone", gone.Reason);
        gone.RequireRetirable();
        Directory.CreateDirectory(_root);
    }

    // r4 item 6: any other listing failure (here the host times out) may pass: not compared, the profile kept, never a finished verdict.
    [Fact]
    public async Task AnInstallListingThatTimesOutKeepsTheProfile()
    {
        var host = new FakeServerHost("server-host", Path.Combine(_root + "-runs", "mirror"));
        Directory.CreateDirectory(host.Local("/srv/game"));
        File.WriteAllText(Path.Combine(host.Local("/srv/game"), "game.bin"), "game");
        var check = ProfileSourceCheck.Prepare(await ProfileSourceCheck.InspectAsync(host, "/srv/game", TimeSpan.FromSeconds(30)), [], ProfileLayout.SourceExecutable);
        host.Failures["list"] = new HostResult(HostOutcome.Unknown, null, "", "", TimeSpan.FromSeconds(30), true);

        var verdict = await check.SettleAsync(host, SourceSettlement.Run("server", "/srv/runs/vt-prep-x/runtime", Output), _ => Task.CompletedTask, TimeSpan.FromSeconds(30));

        Assert.Equal((SourceVerdictKind.Unknown, false), (verdict.Verdict, verdict.Settled));
        Assert.Contains("before it compared", verdict.Reason);
    }

    // r4 item 3: under env teardown a baseline it cannot read (unreadable here; also one over the read limit) is not compared, and
    // the teardown ends; env recover still stops on it.
    [UnixFact, System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AnUnreadableBaselineIsNotComparedByTeardown()
    {
        if (Environment.UserName == "root") return; // root reads a mode 000 file
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();
        await PersistAsync(check);
        var fields = _journal[0].Fields;
        File.SetUnixFileMode(fields["baseline"], UnixFileMode.None);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RecoverAsync(fields));
            var verdict = await RecoverAsync(fields, teardown: true);
            Assert.Equal(SourceVerdictKind.Unknown, verdict!.Verdict);
            Assert.Contains("cannot be read", verdict.Reason);
            verdict.RequireRetirable();
        }
        finally { File.SetUnixFileMode(fields["baseline"], UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    // r3 item 2: a baseline that does not match its journal stays refused by env recover; env teardown finishes it, not compared,
    // keeping what the run had moved.
    [Fact]
    public async Task ATamperedBaselineIsNotComparedByTeardownAndItsMovedFilesAreKept()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();
        await PersistAsync(check);
        var fields = _journal[0].Fields;
        File.AppendAllText(fields["baseline"], " ");
        Directory.CreateDirectory(check.MovedFilesFolder(Runtime));
        File.WriteAllText(Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log"), "the game's");

        await Assert.ThrowsAsync<InvalidDataException>(() => RecoverAsync(fields));
        Assert.True(File.Exists(Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log")));

        var verdict = await RecoverAsync(fields, teardown: true);
        Assert.Equal(SourceVerdictKind.Unknown, verdict!.Verdict);
        Assert.Contains("not compared", verdict.Reason);
        Assert.Contains("differs from its journalled SHA-256", verdict.Reason);
        Assert.Equal("the game's", File.ReadAllText(Path.Combine(RecoveredProfile, "added", "preloader_1.log")));
        verdict.RequireRetirable();
    }

    // r3 item 6: when the baseline is gone and adopting the moved files is refused, the recovery still ends: not compared, the
    // files named where they are.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusedAdoptionOfAGoneBaselineStillEnds(bool settledByTheRun)
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareOnAsync(_fake);
        await PersistAsync(check, _fake);
        var fields = new Dictionary<string, string>(_journal[0].Fields);
        if (settledByTheRun) fields["sourceVerdict"] = "Restored";
        File.Delete(fields["baseline"]);
        string id = Path.GetFileName(fields["sourceAdded"])[(ProfileSourceCheck.AddedFolder.Length + 1)..];
        Directory.CreateDirectory(Path.Combine(Recovered, "source-changes-" + id, "added"));
        string earlierReport = Path.Combine(Recovered, "source-changes-" + id, "source-changes.json");
        File.WriteAllText(earlierReport, "an earlier recovery's report");
        Directory.CreateDirectory(check.MovedFilesFolder(Runtime));
        File.WriteAllText(Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log"), "the game's");

        var verdict = await RecoverAsync(fields, host: _fake);

        // r7 items 1 and 2: nothing was compared, so no settlement is journalled (a later recovery would read it as the run's) and the
        // earlier recovery's report is not overwritten; a second recovery is refused the same way.
        Assert.DoesNotContain(_journal, entry => entry.Kind == JournalEntry.SourceSettled);
        Assert.Equal("an earlier recovery's report", File.ReadAllText(earlierReport));
        Assert.Equal(SourceVerdictKind.Failed, (await RecoverAsync(fields, host: _fake))!.Verdict);
        Assert.Equal("an earlier recovery's report", File.ReadAllText(earlierReport));
        // r4 item 2: a finished failure on either branch, never a Restored that did not happen; the folder stays, journalled kept.
        Assert.Equal((SourceVerdictKind.Failed, true), (verdict!.Verdict, verdict.Settled));
        Assert.Contains("already exists", verdict.Reason);
        Assert.Equal(_fake.Name + ":" + Path.Combine(check.MovedFilesFolder(Runtime), "preloader_1.log"), Assert.Single(verdict.Added).KeptAs);
        Assert.Contains(_journal, entry => entry.Kind == JournalEntry.CopyKept && entry.Fields["runtime"] == check.MovedFilesFolder(Runtime));
        Assert.Equal([check.MovedFilesFolder(Runtime)], verdict.KeptFolders);
    }

    // #612 r2 item 3: a moved-files folder the run kept on purpose (past the size limits, or no evidence folder) has its own kept
    // entry: a recovery never adopts it as "not kept yet", and leaves it to env teardown.
    [Fact]
    public async Task ARecoveryLeavesAMovedFilesFolderTheRunKeptOnPurpose()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();
        await PersistAsync(check);
        var fields = new Dictionary<string, string>(_journal[0].Fields) { ["sourceVerdict"] = "Restored" };
        File.Delete(fields["baseline"]);
        Directory.CreateDirectory(check.MovedFilesFolder(Runtime));
        File.WriteAllText(Path.Combine(check.MovedFilesFolder(Runtime), "crash.dmp"), "past the size limits");

        Assert.Null(await RecoverAsync(fields, path => path == check.MovedFilesFolder(Runtime)));

        Assert.True(File.Exists(Path.Combine(check.MovedFilesFolder(Runtime), "crash.dmp")));
        Assert.False(Directory.Exists(Recovered));
    }

    // #612 r2 item 1: a runtime written with / on Windows (an inventory may) is the same folder to the evidence step's guard, which
    // compares full paths; before, GetDirectoryName's \ never equalled the written / and every settlement was refused. Real pwsh.
    [Fact]
    public async Task AProfileFolderWrittenWithForwardSlashesSettlesOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var check = await PrepareAsync();
        Write("preloader_1.log", "the game's");

        var verdict = await check.SettleAsync(_host, SourceSettlement.Run("client", Runtime.Replace('\\', '/'), Output),
            entry => { _journal.Add(entry); return Task.CompletedTask; }, TimeSpan.FromSeconds(60));

        Assert.Equal((SourceVerdictKind.Restored, true), (verdict.Verdict, verdict.Settled));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Parent));
    }

    // #612 r2 item 6: the Mac server's native code lies under valheim_server/, not at the root. Every native file the link table names
    // is re-checked before each boot, so an executable swapped in the install after staging is refused before the server starts.
    [Fact]
    public async Task AMacServersLinkedExecutableIsRecheckedBeforeEachBoot()
    {
        Write("valheim_server/Valheim", "the server");
        Write("valheim_server/libsteam_api.dylib", "steam");
        Write("valheim_server/Data/Managed/assembly_valheim.dll", "game code");
        var source = await ProfileSourceCheck.InspectAsync(_host, _root, TimeSpan.FromSeconds(30));
        var check = ProfileSourceCheck.Prepare(source, ["valheim_server/Valheim", "valheim_server/libsteam_api.dylib"], ProfileLayout.Linked);
        Assert.Contains("valheim_server/Valheim", check.LinkedNativeFiles.Keys);
        Assert.Contains("valheim_server/libsteam_api.dylib", check.BeforeBoot.Keys);
        string profile = Path.Combine(Parent, "runtime");
        FakeServerHost.CopyDirectory(_root, profile); // a stand-in for the linked profile
        File.AppendAllText(Path.Combine(_root, "valheim_server", "Valheim"), " swapped after staging");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => HostInstall.CheckProfilePinsAsync(_host, new InstallPins(), profile, profile, check,
            "prepared server profile before launch", TimeSpan.FromSeconds(30), CancellationToken.None));

        Assert.Contains("valheim_server/Valheim", error.Message);
    }

    [Fact]
    public void AnUnknownStepNameNeverCutsAWindowsPathInTheReason()
    {
        Assert.Equal("verify client's source install after the run (unknown: not compared)",
            SourceVerdict.NotCompared("client", @"its source baseline C:\vt\runs\x is gone").StepName);
        Assert.Equal("verify client's source install after the run (unknown: stop not proven)", SourceVerdict.Unknown("client", "C:\\x still runs").StepName);
    }

    // #612 item 4: a linked profile's game cannot write into the install, so what appears there is named, left in place and fails
    // nothing; in place, nothing is moved when a file also changed, and the verdict names every path.
    [Fact]
    public async Task ALinkedProfileLeavesAddedPathsInPlaceAndAnInPlaceInstallThatAlsoChangedMovesNothing()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var linked = ProfileSourceCheck.Prepare(await ProfileSourceCheck.InspectAsync(_host, _root, TimeSpan.FromSeconds(30)), ["game.bin"], ProfileLayout.Linked);
        Assert.True(linked.Linked);
        Write("steamapps/appmanifest.acf", "Steam wrote this");

        var elsewhere = await SettleAsync(linked);
        Assert.Equal(SourceVerdictKind.AddedElsewhere, elsewhere.Verdict);
        Assert.True(File.Exists(Path.Combine(_root, "steamapps", "appmanifest.acf")));
        Assert.Contains("steamapps/appmanifest.acf", elsewhere.NotMoved.Select(file => file.Path));
        var report = new ScenarioReport("linked");
        elsewhere.Record(report);
        Assert.True(Assert.Single(report.Steps).Passed);
        Assert.Contains("by something other than the game's profile", report.Provenance["sourceIntegrity.client"]);
        Assert.Contains("steamapps/appmanifest.acf", report.Provenance["sourceIntegrity.client"]);

        File.Delete(Path.Combine(_root, "steamapps", "appmanifest.acf"));
        Directory.Delete(Path.Combine(_root, "steamapps"));
        var inPlace = await PrepareAsync();
        Assert.False(inPlace.Linked);
        Write("preloader_1.log", "the game's");
        File.AppendAllText(Path.Combine(_root, "game.bin"), " changed");
        var failed = await SettleAsync(inPlace, Output + "-content");
        Assert.Equal(SourceVerdictKind.Failed, failed.Verdict);
        Assert.True(File.Exists(Path.Combine(_root, "preloader_1.log"))); // nothing was moved
        Assert.Empty(failed.Added);
        var message = Assert.Throws<IOException>(() => failed.Record(null)).Message;
        Assert.Contains("game.bin", message);
        Assert.Contains("preloader_1.log", message);
    }

    // #612 item 7: each prepared profile has its own moved-files folder, so a later one in the same folder (the next regression arm)
    // never takes an earlier one's kept files as its own.
    [Fact]
    public async Task AProfileNeverTakesAnEarlierProfilesMovedFilesAsItsOwn()
    {
        Write("game.bin", "game");
        Directory.CreateDirectory(Parent);
        var first = await PrepareAsync();
        var second = await PrepareAsync();
        Assert.NotEqual(first.MovedFilesFolder(Runtime), second.MovedFilesFolder(Runtime));
        Directory.CreateDirectory(first.MovedFilesFolder(Runtime));
        File.WriteAllText(Path.Combine(first.MovedFilesFolder(Runtime), "big.dmp"), "kept for env teardown");

        var verdict = await SettleAsync(second);

        Assert.Equal(SourceVerdictKind.Unchanged, verdict.Verdict);
        Assert.Empty(verdict.Added);
        Assert.True(File.Exists(Path.Combine(first.MovedFilesFolder(Runtime), "big.dmp")));
    }

    // #612 item 9: a Windows host trims a trailing dot, so an added "game.bin." would name the install's game.bin there: it is never
    // sent to the move. (This machine's filesystem must hold such a name, so not on Windows.)
    [Fact]
    public async Task AnAddedNameAWindowsHostWouldReadAsAnInstallFileIsNeverMoved()
    {
        if (OperatingSystem.IsWindows()) return;
        var host = new FakeServerHost("windows-host", Path.Combine(_root + "-runs", "mirror"), windows: true);
        string source = @"C:\valheim", runtime = @"C:\vt\vt-prep-test\runtime";
        Directory.CreateDirectory(host.Local(source));
        File.WriteAllText(Path.Combine(host.Local(source), "game.bin"), "game");
        var check = ProfileSourceCheck.Prepare(await ProfileSourceCheck.InspectAsync(host, source, TimeSpan.FromSeconds(30)), [], ProfileLayout.SourceExecutable);
        File.WriteAllText(Path.Combine(host.Local(source), "game.bin."), "a name Windows trims");

        var verdict = await check.SettleAsync(host, SourceSettlement.Run("client", runtime, Output), _ => Task.CompletedTask, TimeSpan.FromSeconds(30));

        Assert.Equal(SourceVerdictKind.Failed, verdict.Verdict);
        Assert.Equal("it would name a file or folder of the install on this host", Assert.Single(verdict.NotMoved).Reason);
        Assert.True(File.Exists(Path.Combine(host.Local(source), "game.bin")));
        Assert.DoesNotContain(host.Runs, run => run.Script == "source-settle" && run.Variables["files"].Length != 0);
    }

    // The settlement and read scripts on this machine's real shell, each refusal among them, are SourceScriptShellTests'.

    [Fact]
    public async Task TheBeforeBootScopeIncludesEveryRootFileChosenByTheWindowsLinkPlan()
    {
        foreach (string relative in new[] { "UnityPlayer.dll", "steam_api64.dll", "valheim.exe", "valheim_server.exe", "UnityCrashHandler64.exe", "unrelated.txt" })
            File.WriteAllText(Path.Combine(_root, relative), relative);
        Directory.CreateDirectory(Path.Combine(_root, "valheim_Data"));
        File.WriteAllText(Path.Combine(_root, "valheim_Data", "data.unity3d"), "game data");
        var source = await ProfileSourceCheck.InspectAsync(_host, _root, TimeSpan.FromSeconds(30));
        var plan = HostedRuntimeStage.PlanLinkedProfile(Valheim.Testing.Game.ClientPlatform.Windows, source.Files.Keys);
        var check = ProfileSourceCheck.Prepare(source, plan.Checked, ProfileLayout.Linked); // Windows checks its root files

        Assert.Contains("UnityPlayer.dll", check.BeforeBoot.Keys);
        Assert.Contains("steam_api64.dll", check.BeforeBoot.Keys);
        Assert.DoesNotContain("unrelated.txt", check.BeforeBoot.Keys);
        Assert.Contains("valheim.exe", check.LinkedNativeFiles.Keys);
        Assert.Contains("valheim_server.exe", check.LinkedNativeFiles.Keys);
        Assert.Contains("UnityCrashHandler64.exe", check.LinkedNativeFiles.Keys);
        Assert.DoesNotContain("valheim_Data/data.unity3d", check.LinkedNativeFiles.Keys);
    }

    [Fact]
    public void TheStagingScopeIsCopiedFilesBepInExLoaderAndPins()
    {
        var paths = ProfileSourceCheck.StagingVerificationPaths(["steam-runtime/libsteam_api.dylib"]);
        Assert.Contains("BepInEx", paths);
        Assert.Contains("*_Data/Managed", paths);
        Assert.Contains("steam-runtime/libsteam_api.dylib", paths);
        Assert.DoesNotContain("logs", paths);
    }

    [Fact]
    public void CallersDoNotRestoreTheRemovedFullSourceRules()
    {
        string source = Path.Combine(FixtureProjects.RepositoryRoot(), "src");
        string stage = File.ReadAllText(Path.Combine(source, "Valheim.Testing.GameSessions", "HostedRuntimeStage.Profile.cs"));
        string hostInstall = File.ReadAllText(Path.Combine(source, "Valheim.Testing.GameSessions", "HostInstall.cs"));
        string phases = File.ReadAllText(Path.Combine(source, "Valheim.Testing.NativeSmoke", "ServerLoadPhases.cs"));
        int directoryInputs = phases.IndexOf("DirectoryInputs", StringComparison.Ordinal);
        string phaseDirectories = phases[directoryInputs..phases.IndexOf(';', directoryInputs)];
        Assert.DoesNotContain("ListAsync(host, source", stage);
        Assert.DoesNotContain("PreparedSourceFiles", hostInstall);
        Assert.DoesNotContain("sourceFiles == null", hostInstall);
        Assert.DoesNotContain("\"--server\"", phaseDirectories);

        string sessions = string.Concat(Directory.EnumerateFiles(Path.Combine(source, "Valheim.Testing.GameSessions"), "*.cs")
            .Select(File.ReadAllText));
        static int Calls(string text, string name) => text.Split(name + "(", StringSplitOptions.None).Length - 1;
        Assert.Equal(2, Calls(sessions, "ListProfileSourceAsync")); // definition and ProfileSourceCheck owner
        Assert.Equal(2, Calls(sessions, "ListMetadataAsync")); // definition and ProfileSourceCheck
        Assert.Equal(2, Calls(sessions, "StagingVerificationPaths")); // definition and profile stage
    }
}
