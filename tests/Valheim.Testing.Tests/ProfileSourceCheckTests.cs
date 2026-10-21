using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// #653: one source-check owner decides the four profile rows and keeps the whole-source post-run verdict; #612: it settles the
// install after the run, moving what the game added into the run's evidence, and a recovery settles it from the journalled baseline.
public sealed class ProfileSourceCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("profile-source-check-").FullName;
    private readonly LocalGameHost _host = new("source-host", HostShell.ThisMachine);

    public void Dispose()
    {
        ProfileSourceCheck.HashAfterRunOverride.Value = null;
        foreach (string folder in new[] { _root, _root + "-runs", Output, Output + "-content", Output + "-again", Output + "-resumed" })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private async Task<ProfileSourceCheck> PrepareAsync(params string[] links) =>
        ProfileSourceCheck.Prepare(await ProfileSourceCheck.InspectAsync(_host, _root, TimeSpan.FromSeconds(30)), links);

    // The profile beside the install, as every owned launch path lays it out: <runs>/vt-prep-*/runtime, its parent holding the baseline.
    private string Runtime => Path.Combine(_root, "..", Path.GetFileName(_root) + "-runs", "vt-prep-test-client", "runtime");
    private string Parent => Path.GetDirectoryName(Path.GetFullPath(Runtime))!;
    private readonly List<JournalEntry> _journal = [];
    private string Output => Path.Combine(Path.GetFullPath(Path.Combine(_root, "..")), Path.GetFileName(_root) + "-output");

    private async Task<SourceVerdict> SettleAsync(ProfileSourceCheck check, string? output = null) => await check.SettleAsync(_host,
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
        var check = await PrepareAsync();
        Write("preloader_1.log", "first");
        Assert.Equal(SourceVerdictKind.Restored, (await SettleAsync(check)).Verdict);
        var again = await SettleAsync(check, Output + "-again");
        Assert.Equal(SourceVerdictKind.Unchanged, again.Verdict);
        Assert.Empty(again.Added);
        Assert.False(Directory.Exists(Output + "-again"));

        // As if a settlement moved this and stopped before keeping it.
        Directory.CreateDirectory(Path.Combine(Parent, ProfileSourceCheck.AddedFolder));
        File.WriteAllText(Path.Combine(Parent, ProfileSourceCheck.AddedFolder, "preloader_2.log"), "moved, not kept");
        var resumed = await SettleAsync(check, Output + "-resumed");
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
        var check = await PrepareAsync();
        await check.PersistAsync(_host, Path.GetFullPath(Runtime), entry => { _journal.Add(entry); return Task.CompletedTask; }, TimeSpan.FromSeconds(30));
        var baseline = Assert.Single(_journal);
        Assert.Equal(JournalEntry.SourceBaseline, baseline.Kind);
        Assert.Equal(Path.Combine(Parent, ProfileSourceCheck.BaselineFile), baseline.Fields["baseline"]);
        Assert.Equal(_root, baseline.Fields["sourceRoot"]);

        var read = await ProfileSourceCheck.FromJournalAsync(_host, baseline.Fields, TimeSpan.FromSeconds(30));
        Assert.NotNull(read);
        Assert.Equal(check.Source.States.OrderBy(file => file.Key, StringComparer.Ordinal), read.Source.States.OrderBy(file => file.Key, StringComparer.Ordinal));
        Assert.Equal(["empty"], read.Source.Directories);
        Write("preloader_1.log", "after the run");
        Assert.Equal(SourceVerdictKind.Restored, (await read.SettleAsync(_host, SourceSettlement.Recovery("client", Path.GetFullPath(Runtime),
            Path.GetFullPath(Path.Combine(Parent, "..", "run", "recovered-client"))), _ => Task.CompletedTask, TimeSpan.FromSeconds(60))).Verdict);
        Assert.False(File.Exists(baseline.Fields["baseline"])); // settled: the baseline went last
        Assert.Null(await ProfileSourceCheck.FromJournalAsync(_host, new Dictionary<string, string>(baseline.Fields) { ["sourceVerdict"] = "Restored" },
            TimeSpan.FromSeconds(30)));

        await check.PersistAsync(_host, Path.GetFullPath(Runtime), _ => Task.CompletedTask, TimeSpan.FromSeconds(30));
        File.AppendAllText(baseline.Fields["baseline"], " ");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProfileSourceCheck.FromJournalAsync(_host, baseline.Fields, TimeSpan.FromSeconds(30)));
        Assert.Contains("differs from its journalled SHA-256", error.Message);
    }

    [Fact]
    public async Task TheBeforeBootScopeIncludesEveryRootFileChosenByTheWindowsLinkPlan()
    {
        foreach (string relative in new[] { "UnityPlayer.dll", "steam_api64.dll", "valheim.exe", "valheim_server.exe", "UnityCrashHandler64.exe", "unrelated.txt" })
            File.WriteAllText(Path.Combine(_root, relative), relative);
        Directory.CreateDirectory(Path.Combine(_root, "valheim_Data"));
        File.WriteAllText(Path.Combine(_root, "valheim_Data", "data.unity3d"), "game data");
        var source = await ProfileSourceCheck.InspectAsync(_host, _root, TimeSpan.FromSeconds(30));
        var plan = HostedRuntimeStage.PlanLinkedProfile(Valheim.Testing.Game.ClientPlatform.Windows, source.Files.Keys);
        var check = ProfileSourceCheck.Prepare(source, plan.Links);

        Assert.Contains("UnityPlayer.dll", check.BeforeBoot.Keys);
        Assert.Contains("steam_api64.dll", check.BeforeBoot.Keys);
        Assert.DoesNotContain("unrelated.txt", check.BeforeBoot.Keys);
        Assert.Contains("valheim.exe", check.LinkedRootFiles.Keys);
        Assert.Contains("valheim_server.exe", check.LinkedRootFiles.Keys);
        Assert.Contains("UnityCrashHandler64.exe", check.LinkedRootFiles.Keys);
        Assert.DoesNotContain("valheim_Data/data.unity3d", check.LinkedRootFiles.Keys);
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
