using Valheim.Testing.Game;
using Xunit;

// The journal's real scripts (#257): bash on macOS and Linux, Windows PowerShell on Windows. A fake host cannot catch a
// shell's quoting or a BSD-tool option mismatch.
public sealed class RunJournalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("run-journal-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static LocalGameHost Host() => OperatingSystem.IsWindows()
        ? new LocalGameHost("journal-host", HostShell.WindowsPowerShell) : new LocalGameHost("journal-host", HostShell.Bash);

    [Fact] public async Task EntriesAreAppendedPerActorAndReadBackExactlyThroughTheHostsShell()
    {
        var host = Host();
        string journal = Path.Combine(_root, "data dir", "journal");
        var run = new RunJournal("run-20261005T150000Z-0123abcd");
        string odd = Path.Combine(_root, "it's a \"path\" with $HOME and `ticks`", "runtime");
        await run.AppendAsync(host, journal, "server", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", odd), ("stage", "é ü 漢")), TimeSpan.FromSeconds(30));
        await run.AppendAsync(host, journal, "client-a", JournalEntry.Of(JournalEntry.CharacterIntended, ("fileName", "vt0123abcd")), TimeSpan.FromSeconds(30));
        await run.AppendAsync(host, journal, "server", JournalEntry.Of(JournalEntry.CopyDone, ("runtime", odd)), TimeSpan.FromSeconds(30));
        var read = await RunJournalOnHost.ReadAsync(host, journal, run.RunId, TimeSpan.FromSeconds(30));
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone], read.Where(record => record.Actor == "server").Select(record => record.Entry.Kind));
        var first = read.First(record => record.Actor == "server");
        Assert.Equal(odd, first.Entry.Fields["runtime"]);
        Assert.Equal("é ü 漢", first.Entry.Fields["stage"]);
        Assert.Equal("vt0123abcd", Assert.Single(read, record => record.Actor == "client-a").Entry.Fields["fileName"]);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(journal, run.RunId, "server.jsonl")).Length);
        // A run with no journal on this host reads as empty, not as a failure.
        Assert.Empty(await RunJournalOnHost.ReadAsync(host, journal, "run-unknown", TimeSpan.FromSeconds(30)));
    }

    [Fact] public async Task EveryRunIsReadBackWithTheRunnerThatWroteIt()
    {
        var host = Host();
        string journal = Path.Combine(_root, "data dir", "journal");
        Assert.Empty((await RunJournalOnHost.ReadAllAsync(host, journal, TimeSpan.FromSeconds(30))).Records);
        foreach (string run in new[] { "run-a", "run-b" })
            await new RunJournal(run).AppendAsync(host, journal, "server", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", "/x/" + run)), TimeSpan.FromSeconds(30));
        await new RunJournal("run-b").AppendAsync(host, journal, "run", JournalEntry.Of(JournalEntry.RunEnded, ("state", "passed")), TimeSpan.FromSeconds(30));
        var (all, unreadable) = await RunJournalOnHost.ReadAllAsync(host, journal, TimeSpan.FromSeconds(30));
        Assert.Equal(["run-a", "run-b", "run-b"], all.Select(record => record.Run).Order());
        Assert.Equal(0, unreadable);
        Assert.All(all, record => Assert.Equal(JournalRunner.Current, record.Runner));
        // A line cut short (a full disk, a killed write) is counted and skipped; the lines around it still read.
        File.AppendAllText(Path.Combine(journal, "run-a", "server.jsonl"), "{\"utc\":\"2026-10-05T18:");
        (all, unreadable) = await RunJournalOnHost.ReadAllAsync(host, journal, TimeSpan.FromSeconds(30));
        Assert.Equal(3, all.Count);
        Assert.Equal(1, unreadable);
        Assert.True(JournalRunner.Current.StillRuns());
        Assert.Equal(Environment.ProcessId, JournalRunner.Current.Pid);
    }

    // The process check through the host's own shell, on this test's process: Linux reads /proc and Windows the process and
    // its CIM command line; a macOS host has neither and says so rather than guessing.
    [Fact] public async Task TheProcessCheckReadsStartIdentityAndCommandLineOrSaysItCannot()
    {
        var host = Host();
        int self = Environment.ProcessId;
        using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh") { ArgumentList = { OperatingSystem.IsWindows() ? "/c" : "-c", "exit 0" } })!;
        exited.WaitForExit();
        var first = await HostProcessProbe.ProbeAsync(host, [(self, ""), (exited.Id, "1")], TimeSpan.FromSeconds(60));
        if (OperatingSystem.IsMacOS())
        {
            Assert.All(first.Values, probed => Assert.Equal(ProbedState.Unreadable, probed.State));
            return;
        }
        var probed = first[(self, "")];
        Assert.Equal(ProbedState.Same, probed.State);
        Assert.Matches("^[0-9]+$", probed.StartIdentity);
        Assert.Matches("^[0-9a-f]{64}$", probed.CommandLineSha256);
        Assert.Equal(ProbedState.Gone, first[(exited.Id, "1")].State);
        // The same start identity matches again with the same command line; another one is a reused ID. Both asked at once
        // get one answer each (#257 review: a reused ID journalled by two runs).
        var both = await HostProcessProbe.ProbeAsync(host, [(self, probed.StartIdentity!), (self, probed.StartIdentity + "1")], TimeSpan.FromSeconds(60));
        Assert.Equal(probed, both[(self, probed.StartIdentity!)]);
        Assert.Equal(ProbedState.Reused, both[(self, probed.StartIdentity + "1")].State);
        Assert.Null(both[(self, probed.StartIdentity + "1")].CommandLineSha256);
    }

    // A Linux recorder's game is `env ... game` until env execs it: the hash journalled at the start is the game's command
    // line, not env's, once the check waits for the exec.
    [Fact] public async Task OnLinuxTheStartCheckHashesTheCommandLineAfterEnvExecs()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var sleeper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("env") { ArgumentList = { "sleep", "30" } })!;
        try
        {
            string? hash = await HostProcessProbe.CommandLineAsync(Host(), sleeper.Id, "", TimeSpan.FromSeconds(60));
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("sleep\u000030\u0000"u8.ToArray())), hash);
        }
        finally { sleeper.Kill(); }
    }

    // Runs append while env status and OwnedCopies read (#257): a reader shares the file for writing, so on Windows neither an
    // append nor a read is refused because the other has the file open.
    [Fact] public async Task TheJournalIsReadWhileAWriterHasItOpen()
    {
        string journal = Path.Combine(_root, "local journal");
        using var local = RunJournal.UseLocalDirectory(journal);
        var run = new RunJournal("run-shared");
        string copy = Path.Combine(_root, "valheim-test-" + new string('c', 32));
        run.AppendLocal(WorldFixture.Actor, JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", copy), ("local", "true")));
        string file = Path.Combine(journal, "run-shared", WorldFixture.Actor + ".jsonl");
        using (var writer = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Single((await RunJournalOnHost.ReadAllAsync(Host(), journal, TimeSpan.FromSeconds(30))).Records);
            Assert.Single((await RunJournalOnHost.ReadAsync(Host(), journal, "run-shared", TimeSpan.FromSeconds(30))));
            // This process, the record's runner, still runs: it holds the copy.
            Assert.Equal(Environment.ProcessId, RunJournal.LocalHolders()[copy]);
            run.AppendLocal(WorldFixture.Actor, JournalEntry.Of(JournalEntry.CopyDone, ("runtime", copy), ("local", "true")));
        }
        Assert.Equal(2, File.ReadAllLines(file).Length);
    }

    [Fact] public void TheJournalSitsBesideTheHostsLockAndNamesAreChecked()
    {
        Assert.Equal("/srv/vt/journal", RunJournal.DirectoryFor(new HostProfile { Lock = "/srv/vt/lock" }));
        Assert.Equal(@"C:\Users\me\AppData\Local\ValheimTesting\journal", RunJournal.DirectoryFor(new HostProfile { Lock = @"C:\Users\me\AppData\Local\ValheimTesting\lock" }));
        Assert.Throws<ArgumentException>(() => new RunJournal("../escape"));
        Assert.Throws<ArgumentException>(() => new RunJournal(".."));
        Assert.Matches("^run-[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$", RunJournal.NewRunId());
    }
}
