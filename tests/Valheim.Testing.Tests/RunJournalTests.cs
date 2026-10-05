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
        var read = await RunJournal.ReadAsync(host, journal, run.RunId, TimeSpan.FromSeconds(30));
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone], read.Where(record => record.Actor == "server").Select(record => record.Entry.Kind));
        var first = read.First(record => record.Actor == "server");
        Assert.Equal(odd, first.Entry.Fields["runtime"]);
        Assert.Equal("é ü 漢", first.Entry.Fields["stage"]);
        Assert.Equal("vt0123abcd", Assert.Single(read, record => record.Actor == "client-a").Entry.Fields["fileName"]);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(journal, run.RunId, "server.jsonl")).Length);
        // A run with no journal on this host reads as empty, not as a failure.
        Assert.Empty(await RunJournal.ReadAsync(host, journal, "run-unknown", TimeSpan.FromSeconds(30)));
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
