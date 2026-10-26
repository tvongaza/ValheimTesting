using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

// #791: a run's own folder on each host (<runtime>/<runId>) is the run's to remove once fetched, and only the run's. On the
// station, 151 finished runs had left 12.9 GB of run folders in ValheimTesting's runs/ (local-server, local-client, pc-client,
// pc-server) that env status never listed. These run a dedicated server with a client, on fake hosts (no shell, no game).
public sealed partial class HostedServerRunTests
{
    private const string ClientRuns = "/Users/tester/runs", ClientRunDirectory = ClientRuns + "/" + RunId;

    // Both hosts' journals, as env status and env recover read them.
    private static Dictionary<string, HostProfile> BothHosts() => new()
    {
        ["linux-box"] = new() { Kind = "ssh", Platform = "linux", Shell = "bash", Lock = "/var/tmp/vt/lock" },
        ["mac-gpu"] = new() { Kind = "ssh", Platform = "macos", Shell = "bash", Lock = "/Users/tester/lock" },
    };

    private (FakeServerHost Server, FakeServerHost Client, int Code) RunWithAClient(Action<FakeServerHost>? clientHost = null)
    {
        var server = NewServer(); var host = NewHost(server);
        var client = new FakeServerHost("mac-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        FakeInstalls.MacClient(client.Local("/Users/tester/valheim"));
        // An earlier run's BepInEx log, which the client's launch moves aside into its run folder, and the user's recent servers.
        File.WriteAllText(client.Local("/Users/tester/valheim/BepInEx/LogOutput.log"), "an earlier run's log\n");
        string recent = client.Local("/Users/tester/" + HostedWorld.MacSaveRelative + "/serverlist_local/recent");
        Directory.CreateDirectory(Path.GetDirectoryName(recent)!);
        File.WriteAllText(recent, "the user's servers\n");
        clientHost?.Invoke(client);
        var (plan, profile) = Write(host, withClient: true);
        var prepared = PreparedClient(client);
        int code = PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            using (run.OpenClient(prepared)) { }
            return Task.CompletedTask;
        }, client, new ScriptedTransport())).GetAwaiter().GetResult();
        return (host, client, code);
    }

    private static IReadOnlyList<string> Entries(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).ToList() : [];

    // server-load's shape: a dedicated server and its joining client. A passing run leaves nothing in either host's runtime folder
    // (each run folder fetched into the output, then removed), and env status lists nothing.
    [Fact] public async Task APassingRunWithAServerAndAClientLeavesNoRunFolderOnEitherHost()
    {
        bool? folderAtRelease = null;
        var (host, client, code) = RunWithAClient(client => client.AfterRelease = _ => folderAtRelease = Directory.Exists(client.Local(ClientRunDirectory)));
        Assert.Equal(0, code);
        Assert.Empty(Entries(host.Local(Runs)));
        Assert.Empty(Entries(client.Local(ClientRuns)));
        Assert.True(Directory.Exists(client.Local(ClientRuns))); // the environment's runtime folder is never the run's
        // What the client's folder held is in the output: its launch folder, the earlier log it moved aside, its user-file backup.
        string fetched = Path.Combine(Output, RunFolder.OutputFolder, "mac-gpu", "runs");
        Assert.True(File.Exists(Path.Combine(fetched, "client-1", "game-0.log")));
        Assert.Equal("an earlier run's log\n", File.ReadAllText(Path.Combine(fetched, "client-1.previous-LogOutput.log")));
        Assert.True(Directory.Exists(Path.Combine(fetched, UserSaveFiles.BackupName(RunId, "client-1"))));
        // The folder's receipt is journalled before its removal, and both under the client host's lock.
        var journal = await RunJournalOnHost.ReadAsync(client, "/Users/tester/journal", RunId, TimeSpan.FromSeconds(5));
        var folder = journal.Where(record => record.Entry.Fields.GetValueOrDefault("runtime") == ClientRunDirectory).ToList();
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.CopyRetired], folder.Select(record => record.Entry.Kind));
        Assert.Equal("true", folder[1].Entry.Fields["fetched"]);
        var runs = client.Runs.ToList();
        int fetch = runs.FindIndex(run => run.Script == "fetch" && run.Variables["dir"] == ClientRunDirectory);
        int remove = runs.FindIndex(run => run.Script == "cleanup-stage" && run.Variables["runtime"] == ClientRunDirectory);
        // The receipt is journalled between the fetch and the removal (the host's own script log orders them; a journal read back is
        // ordered per actor file only), and the folder is gone by the time the client host's lock is released.
        int receipt = runs.FindIndex(run => run.Script == "journal" &&
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(run.Variables["line"])) is var line &&
            line.Contains("\"fetched\":\"true\"", StringComparison.Ordinal) && line.Contains(ClientRunDirectory, StringComparison.Ordinal));
        Assert.True(fetch >= 0 && receipt > fetch && remove > receipt, $"fetch {fetch}, receipt {receipt}, remove {remove}");
        Assert.False(folderAtRelease);
        Assert.Equal("1", runs[remove].Variables["keepparent"]); // the environment's runtime folder stays
        var status = Assert.Single((await RunJournalStatus.InspectAsync(BothHosts(), name => name == "mac-gpu" ? client : host, TimeSpan.FromSeconds(5))).Runs);
        Assert.Equal(JournalRunState.Ended, status.State);
        Assert.Empty(status.Items);
    }

    // A run folder whose fetch fails is the run's only copy of what it holds: it stays, journalled kept, so env status lists it;
    // env recover keeps it (its fetch was never proven), and env teardown removes it.
    [Fact] public async Task ARunFolderWhoseFetchFailedIsKeptListedAndRemovedByEnvTeardown()
    {
        var (host, client, code) = RunWithAClient(client => client.AfterFetch = target =>
        {
            if (target.Contains(RunFolder.OutputFolder, StringComparison.Ordinal)) throw new IOException("the archive failed verification");
        });
        Assert.Equal(1, code);
        Assert.Contains("its fetch failed", Step("fetch and remove client host mac-gpu's run folder").GetProperty("Error").GetString());
        Assert.True(File.Exists(client.Local(ClientRunDirectory + "/client-1/game-0.log")));
        IGameHost Of(string name) => name == "mac-gpu" ? client : host;
        var listed = Assert.Single((await RunJournalStatus.InspectAsync(BothHosts(), Of, TimeSpan.FromSeconds(5))).Runs);
        var kept = Assert.Single(listed.Items);
        Assert.Equal(("copy", ClientRunDirectory, true), (kept.Kind, kept.What, kept.Kept));
        Assert.Contains("its fetch failed", kept.Status);

        var recovered = await RunRecovery.RecoverAsync(BothHosts(), Of, RunId, teardown: false, TimeSpan.FromSeconds(5));
        Assert.False(recovered.Clean);
        Assert.True(Directory.Exists(client.Local(ClientRunDirectory)));
        var torn = await RunRecovery.RecoverAsync(BothHosts(), Of, RunId, teardown: true, TimeSpan.FromSeconds(5));
        Assert.True(torn.Clean, string.Join("\n", torn.Steps));
        Assert.Empty(Entries(client.Local(ClientRuns)));
    }

    // A run folder fetched and journalled, whose removal then failed: env status lists it, and env recover removes it.
    [Fact] public async Task AFetchedRunFolderWhoseRemovalFailedIsRemovedByEnvRecover()
    {
        // On the client's host the run folder is the only copy removed (its client ran in place).
        var (host, client, code) = RunWithAClient(client =>
            client.Failures["cleanup-stage"] = new HostResult(HostOutcome.Exited, 3, "", "rm: cannot remove: Device or resource busy", TimeSpan.Zero, false));
        Assert.Equal(1, code);
        Assert.True(File.Exists(Path.Combine(Output, RunFolder.OutputFolder, "mac-gpu", "runs", "client-1", "game-0.log")));
        Assert.True(Directory.Exists(client.Local(ClientRunDirectory)));
        client.Failures.Remove("cleanup-stage");
        IGameHost Of(string name) => name == "mac-gpu" ? client : host;
        var left = Assert.Single(Assert.Single((await RunJournalStatus.InspectAsync(BothHosts(), Of, TimeSpan.FromSeconds(5))).Runs).Items);
        Assert.Equal(("copy", ClientRunDirectory, "copied, not retired"), (left.Kind, left.What, left.Status));

        var recovered = await RunRecovery.RecoverAsync(BothHosts(), Of, RunId, teardown: false, TimeSpan.FromSeconds(5));
        Assert.True(recovered.Clean, string.Join("\n", recovered.Steps));
        Assert.Contains("its fetch was journalled", Assert.Single(recovered.Steps, step => step.What == "copy " + ClientRunDirectory).Outcome);
        Assert.Empty(Entries(client.Local(ClientRuns)));
    }

    // A person who asked to keep runtimes keeps the run folders too, journalled kept and listed.
    [Fact] public async Task ARunFolderIsKeptOnRequest()
    {
        RunRetirement.KeepOverride.Value = true;
        try
        {
            var (host, client, code) = RunWithAClient();
            Assert.Equal(0, code);
            Assert.True(File.Exists(client.Local(ClientRunDirectory + "/client-1/game-0.log")));
            var items = Assert.Single((await RunJournalStatus.InspectAsync(BothHosts(), name => name == "mac-gpu" ? client : host, TimeSpan.FromSeconds(5))).Runs).Items;
            var folder = Assert.Single(items, item => item.What == ClientRunDirectory);
            Assert.True(folder.Kept);
            Assert.Contains(RunRetirement.KeptOnRequest, folder.Status);
        }
        finally { RunRetirement.KeepOverride.Value = null; }
    }

    // A folder already at the run's path is someone else's: the run refuses it, writes nothing in it, and never removes it.
    [Fact] public void AFolderAlreadyAtTheRunsPathIsRefusedAndKept()
    {
        var (host, client, code) = RunWithAClient(client =>
        {
            Directory.CreateDirectory(client.Local(ClientRunDirectory));
            File.WriteAllText(client.Local(ClientRunDirectory + "/theirs.txt"), "not the run's");
        });
        Assert.Equal(1, code);
        Assert.Equal(["theirs.txt"], Directory.EnumerateFileSystemEntries(client.Local(ClientRunDirectory)).Select(Path.GetFileName));
        Assert.DoesNotContain(client.Runs, run => run.Script is "client-start" or "move-aside" or "cleanup-stage");
        Assert.Empty(Entries(host.Local(Runs)));
    }
}

// RunFolder's admission failures, with the existing host mirror and post-effect reply-loss seam.
public sealed class RunFolderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private const string RunId = "run-folder-failure";
    public void Dispose() => _temp.Dispose();
    private FakeServerHost Host(bool windows = false) => new("box", Path.Combine(_temp.Path, "host"), windows: windows);
    private static RunFolder Folder(IGameHost host, Func<JournalEntry, CancellationToken, Task> journal, RunFolder.Claims? claims = null) =>
        new(host, "box", "/journal", host.Shell.Kind == HostShellKind.PowerShell ? "C:/runtime" : "/runtime", RunId, journal, claims);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AFailedNotMadeReceiptKeepsTheForeignFolderAndExplainsTheRefusal(bool windows, bool receiptTimedOut)
    {
        var host = Host(windows);
        var attempts = new List<JournalEntry>();
        var durable = new List<JournalEntry>();
        Exception receiptFailure = receiptTimedOut
            ? new HostOperationException("Appending the refusal receipt", new HostResult(HostOutcome.Unknown, null, "", "journal append timed out", TimeSpan.FromSeconds(1), true))
            : new IOException("journal storage unavailable");
        var folder = Folder(host, (entry, token) =>
        {
            attempts.Add(entry);
            if (entry.Kind == JournalEntry.CopyRetired)
            {
                Assert.Equal(CancellationToken.None, token);
                Assert.Equal("it already existed", entry.Fields["notMade"]);
                throw receiptFailure;
            }
            durable.Add(entry);
            return Task.CompletedTask;
        });
        string foreign = host.Local(folder.Path);
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "theirs.txt"), "foreign sentinel");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => folder.MakeAsync(default));

        Assert.Contains(folder.Path, error.Message);
        Assert.Contains(host.Name, error.Message);
        Assert.Contains("already exists", error.Message);
        Assert.Contains("did not create or own", error.Message);
        Assert.Contains("notMade refusal receipt could not be recorded", error.Message);
        Assert.Contains("exited with code 4", error.Message);
        Assert.Same(receiptFailure, error.InnerException);
        if (receiptFailure is HostOperationException append)
        {
            Assert.Equal(HostOutcome.Unknown, append.Result.Outcome);
            Assert.True(append.Result.TimedOut);
        }
        Assert.False(folder.Made);
        Assert.Null(await folder.SettleAsync(Path.Combine(_temp.Path, "output"), null, default));
        Assert.Contains("already claimed by this run", (await Assert.ThrowsAsync<InvalidOperationException>(() => folder.MakeAsync(default))).Message);
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyRetired], attempts.Select(entry => entry.Kind));
        Assert.Equal(JournalEntry.CopyIntended, Assert.Single(durable).Kind);
        Assert.Equal("world-reserve", Assert.Single(host.Runs).Script);
        Assert.Equal(["theirs.txt"], Directory.EnumerateFileSystemEntries(foreign).Select(Path.GetFileName));
        Assert.Equal("foreign sentinel", File.ReadAllText(Path.Combine(foreign, "theirs.txt")));
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "output")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ALostReservationReplyRetainsItsClaimAndRefusesASecondMutation(bool windows, bool timedOut)
    {
        var inner = Host(windows);
        var lost = timedOut ? new HostResult(HostOutcome.Unknown, null, "", "reply timed out", TimeSpan.FromSeconds(1), true)
            : FakeServerHost.TransportFailure;
        var host = new ForwardingWorldHost(inner)
        {
            AfterRun = (script, variables, result) => script == (windows ? HostedWorldOnHost.WindowsReserve : HostedWorldOnHost.BashReserve) ? lost : result,
        };
        var journal = new List<JournalEntry>();
        var claims = new RunFolder.Claims();
        Task Append(JournalEntry entry, CancellationToken token) { journal.Add(entry); return Task.CompletedTask; }
        var folder = Folder(host, Append, claims);

        var first = await Assert.ThrowsAsync<HostOperationException>(() => folder.MakeAsync(default));
        Assert.Same(lost, first.Result);
        Assert.False(folder.Made);
        Assert.True(Directory.Exists(inner.Local(folder.Path))); // The effect happened; only its reply was lost.
        File.WriteAllText(Path.Combine(inner.Local(folder.Path), "unconfirmed.txt"), "do not touch");

        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => folder.MakeAsync(default));
        var secondRole = Folder(host, Append, claims);
        await Assert.ThrowsAsync<InvalidOperationException>(() => secondRole.MakeAsync(default));
        Assert.Null(await folder.SettleAsync(Path.Combine(_temp.Path, "output"), null, default));

        Assert.Contains(folder.Path, retry.Message);
        Assert.Contains(host.Name, retry.Message);
        Assert.Contains("already claimed by this run", retry.Message);
        Assert.Contains("another role or an earlier unconfirmed reservation", retry.Message);
        Assert.False(secondRole.Made);
        Assert.Equal(JournalEntry.CopyIntended, Assert.Single(journal).Kind);
        Assert.Equal("world-reserve", Assert.Single(inner.Runs).Script);
        Assert.Equal("do not touch", File.ReadAllText(Path.Combine(inner.Local(folder.Path), "unconfirmed.txt")));
        Assert.Equal(["unconfirmed.txt"], Directory.EnumerateFileSystemEntries(inner.Local(folder.Path)).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedInitialIntentReleasesItsClaimBeforeAnyReservation(bool cancelled)
    {
        var host = Host();
        var claims = new RunFolder.Claims();
        Exception failure = cancelled ? new OperationCanceledException("injected intent cancellation") : new IOException("intent could not be written");
        var first = Folder(host, (_, _) => throw failure, claims);

        Assert.Same(failure, await Record.ExceptionAsync(() => first.MakeAsync(default)));
        Assert.False(first.Made);
        Assert.Empty(host.Runs);
        Assert.False(Directory.Exists(host.Local(first.Path)));

        var journal = new List<JournalEntry>();
        var second = Folder(host, (entry, _) => { journal.Add(entry); return Task.CompletedTask; }, claims);
        await second.MakeAsync(default);

        Assert.True(second.Made);
        Assert.Equal(JournalEntry.CopyIntended, Assert.Single(journal).Kind);
        Assert.Equal("world-reserve", Assert.Single(host.Runs).Script);
        Assert.Contains("already claimed by this run", (await Assert.ThrowsAsync<InvalidOperationException>(() => first.MakeAsync(default))).Message);
        Assert.Single(journal);
        Assert.Single(host.Runs);
    }

    [Fact] public async Task AnInjectedReceiptCancellationKeepsItsIdentityAndLeavesTheFolderUnowned()
    {
        var host = Host();
        var cancelled = new OperationCanceledException("injected receipt cancellation");
        var folder = Folder(host, (entry, _) => entry.Kind == JournalEntry.CopyRetired ? throw cancelled : Task.CompletedTask);
        Directory.CreateDirectory(host.Local(folder.Path));
        File.WriteAllText(Path.Combine(host.Local(folder.Path), "theirs.txt"), "foreign sentinel");

        Assert.Same(cancelled, await Assert.ThrowsAsync<OperationCanceledException>(() => folder.MakeAsync(default)));
        Assert.False(folder.Made);
        Assert.Null(await folder.SettleAsync(Path.Combine(_temp.Path, "output"), null, default));
        Assert.Equal("foreign sentinel", File.ReadAllText(Path.Combine(host.Local(folder.Path), "theirs.txt")));
        Assert.Equal("world-reserve", Assert.Single(host.Runs).Script);
    }
}

// The run folder's own scripts in a real shell (bash, or pwsh on Windows): made atomically, fetched whole, removed through the
// protected-path remover, and the environment's runtime folder left in place.
public sealed class RunFolderShellTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-run-folder-").FullName;
    private const string RunId = "run-folder-1";
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IGameHost Host(string shell) => new LocalGameHost("local-" + shell, HostShell.Parse(shell));
    private string Runtime => Path.Combine(_root, "runs", "local-client");
    private string Journal => Path.Combine(_root, "journal");

    private RunFolder Folder(IGameHost host) => new(host, "local", Journal, Runtime, RunId,
        (entry, token) => new RunJournal(RunId).AppendAsync(host, Journal, "client", entry, TimeSpan.FromSeconds(30), token));

    private async Task<IReadOnlyList<string>> KindsOf(IGameHost host, string path) =>
        (await RunJournalOnHost.ReadAsync(host, Journal, RunId, TimeSpan.FromSeconds(30))).Where(record => record.Entry.Fields.GetValueOrDefault("runtime") == path)
            .Select(record => record.Entry.Kind).ToList();

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task ARunFolderIsFetchedWholeAndRemovedAndItsRuntimeFolderStays(string shell)
    {
        var host = Host(shell);
        var folder = Folder(host);
        await folder.MakeAsync(default);
        await folder.MakeAsync(default); // once
        Assert.True(Directory.Exists(folder.Path));
        Directory.CreateDirectory(Path.Combine(folder.Path, "client-1"));
        File.WriteAllText(Path.Combine(folder.Path, "client-1", "toolkit-unity.log"), "unity");
        File.WriteAllText(Path.Combine(folder.Path, "client-1.previous-LogOutput.log"), "earlier");
        string local = folder.LocalDirectory(Path.Combine(_root, "output"));

        Assert.Null(await folder.SettleAsync(local, null, default));

        Assert.False(Directory.Exists(folder.Path));
        Assert.True(Directory.Exists(Runtime));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Runtime));
        Assert.Equal("unity", File.ReadAllText(Path.Combine(local, "client-1", "toolkit-unity.log")));
        Assert.Equal("earlier", File.ReadAllText(Path.Combine(local, "client-1.previous-LogOutput.log")));
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.CopyRetired], await KindsOf(host, folder.Path));
        Assert.Empty(RunJournalStatus.Left("local", await RunJournalOnHost.ReadAsync(host, Journal, RunId, TimeSpan.FromSeconds(30))));
    }

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task ARunFolderHoldingAnUnsettledCopyStaysOpen(string shell)
    {
        var host = Host(shell);
        var folder = Folder(host);
        await folder.MakeAsync(default);
        string world = Path.Combine(folder.Path, "world");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Campaign.db"), "the run's only save");
        await new RunJournal(RunId).AppendAsync(host, Journal, "client", JournalEntry.Of(JournalEntry.CopyKept, ("runtime", world), ("why", "its fetch failed")), TimeSpan.FromSeconds(30));

        Assert.Contains("copy " + world, await folder.SettleAsync(Path.Combine(_root, "output", "run"), null, default));

        Assert.Equal("the run's only save", File.ReadAllText(Path.Combine(world, "Campaign.db")));
        Assert.False(Directory.Exists(Path.Combine(_root, "output", "run")));
        Assert.Equal([JournalEntry.CopyIntended], await KindsOf(host, folder.Path));
    }

    // Two roles of one run with one runtime on a host (which ResolvedEnvironment.Validate refuses): the second is refused before it
    // journals anything, so its refusal can never close the first's record, and the first's folder stays listed until settled.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task ASecondRoleOfTheRunNeverTakesOverItsFolder(string shell)
    {
        var host = Host(shell);
        var claims = new RunFolder.Claims();
        var first = new RunFolder(host, "local", Journal, Runtime, RunId,
            (entry, token) => new RunJournal(RunId).AppendAsync(host, Journal, "client", entry, TimeSpan.FromSeconds(30), token), claims);
        await first.MakeAsync(default);
        var second = new RunFolder(host, "local", Journal, Runtime, RunId,
            (entry, token) => new RunJournal(RunId).AppendAsync(host, Journal, "server", entry, TimeSpan.FromSeconds(30), token), claims);

        Assert.Contains("already claimed by this run", (await Assert.ThrowsAsync<InvalidOperationException>(() => second.MakeAsync(default))).Message);

        Assert.True(Directory.Exists(first.Path));
        Assert.Equal([JournalEntry.CopyIntended], await KindsOf(host, first.Path));
        var open = Assert.Single(RunJournalStatus.Left("local", await RunJournalOnHost.ReadAsync(host, Journal, RunId, TimeSpan.FromSeconds(30))));
        Assert.Equal(("copy", first.Path, "client"), (open.Kind, open.What, open.Actor));
    }

    // The environment's runtime folder is the user's, and may be a link (to another drive, say): the run still makes its folder in it.
    // Only a recovery's evidence reservation, whose parent the toolkit made, refuses a link at its parent.
    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task ARunFolderIsMadeInALinkedRuntimeFolder(string shell)
    {
        if (OperatingSystem.IsWindows()) return; // Creating a symlink may require a privilege on Windows.
        var host = Host(shell);
        string target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(Runtime)!);
        Directory.CreateSymbolicLink(Runtime, target);
        var folder = Folder(host);

        await folder.MakeAsync(default);

        Assert.True(folder.Made);
        Assert.True(Directory.Exists(Path.Combine(target, RunId)));
        Assert.Equal([JournalEntry.CopyIntended], await KindsOf(host, folder.Path));
    }

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task AFolderThatAlreadyExistsIsNeverTheRuns(string shell)
    {
        var host = Host(shell);
        var folder = Folder(host);
        Directory.CreateDirectory(folder.Path);
        File.WriteAllText(Path.Combine(folder.Path, "theirs.txt"), "not the run's");

        await Assert.ThrowsAsync<InvalidOperationException>(() => folder.MakeAsync(default));
        Assert.False(folder.Made);
        Assert.Null(await folder.SettleAsync(Path.Combine(_root, "output", "run"), null, default));

        Assert.Equal("not the run's", File.ReadAllText(Path.Combine(folder.Path, "theirs.txt")));
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyRetired], await KindsOf(host, folder.Path));
        Assert.Empty(RunJournalStatus.Left("local", await RunJournalOnHost.ReadAsync(host, Journal, RunId, TimeSpan.FromSeconds(30))));
    }

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task AFailedNotMadeReceiptNamesTheForeignFolderWithoutTouchingIt(string shell)
    {
        var host = Host(shell);
        var receiptFailure = new IOException("the refusal receipt cannot be written");
        var folder = new RunFolder(host, "local", Journal, Runtime, RunId, (entry, token) =>
            entry.Kind == JournalEntry.CopyRetired ? throw receiptFailure
                : new RunJournal(RunId).AppendAsync(host, Journal, "client", entry, TimeSpan.FromSeconds(30), token));
        Directory.CreateDirectory(folder.Path);
        File.WriteAllText(Path.Combine(folder.Path, "theirs.txt"), "foreign sentinel");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => folder.MakeAsync(default));

        Assert.Contains(folder.Path, error.Message);
        Assert.Contains(host.Name, error.Message);
        Assert.Contains("did not create or own", error.Message);
        Assert.Contains("notMade refusal receipt could not be recorded", error.Message);
        Assert.Contains("exited with code 4", error.Message);
        Assert.Same(receiptFailure, error.InnerException);
        Assert.False(folder.Made);
        string output = Path.Combine(_root, "output");
        Assert.Null(await folder.SettleAsync(output, null, default));
        Assert.False(Directory.Exists(output));
        Assert.Equal(["theirs.txt"], Directory.EnumerateFileSystemEntries(folder.Path).Select(Path.GetFileName));
        Assert.Equal("foreign sentinel", File.ReadAllText(Path.Combine(folder.Path, "theirs.txt")));
        Assert.Equal([JournalEntry.CopyIntended], await KindsOf(host, folder.Path));
    }

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task ALostReservationReplyNeverRepeatsTheRealShellMutation(string shell)
    {
        var calls = new List<string>();
        var host = new ForwardingWorldHost(Host(shell)) { BeforeRun = (script, _) => calls.Add(script) };
        var folder = Folder(host);
        var lost = new HostResult(HostOutcome.Unknown, null, "", "injected lost reply", TimeSpan.FromSeconds(1), true);
        host.AfterRun = (script, variables, result) => script == (host.Shell.Kind == HostShellKind.PowerShell ? HostedWorldOnHost.WindowsReserve : HostedWorldOnHost.BashReserve)
            ? lost : result;

        Assert.Same(lost, (await Assert.ThrowsAsync<HostOperationException>(() => folder.MakeAsync(default))).Result);
        Assert.True(Directory.Exists(folder.Path)); // A real shell reserved it before its successful reply was replaced.
        File.WriteAllText(Path.Combine(folder.Path, "unconfirmed.txt"), "do not touch");
        int beforeRetry = calls.Count;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => folder.MakeAsync(default));
        Assert.Null(await folder.SettleAsync(Path.Combine(_root, "output"), null, default));

        Assert.Contains("another role or an earlier unconfirmed reservation", error.Message);
        Assert.Equal(beforeRetry, calls.Count);
        Assert.False(folder.Made);
        Assert.Equal("do not touch", File.ReadAllText(Path.Combine(folder.Path, "unconfirmed.txt")));
        Assert.Equal(["unconfirmed.txt"], Directory.EnumerateFileSystemEntries(folder.Path).Select(Path.GetFileName));
        host.AfterRun = null;
        Assert.Equal([JournalEntry.CopyIntended], await KindsOf(host, folder.Path));
    }
}
