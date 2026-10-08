using System.Diagnostics;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class LocalClientJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vt-local-journal-" + Guid.NewGuid().ToString("N"));
    public LocalClientJournalTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AForegroundClientIsRecordedByIdentityAndCannotBeClaimedStoppedWhileItRuns()
    {
        string journal = Path.Combine(_root, "journal"), evidence = Path.Combine(_root, "evidence");
        Directory.CreateDirectory(evidence);
        using var directory = RunJournal.UseLocalDirectory(journal);
        using var run = RunJournal.UseRun("run-journal-test");
        var owner = new LocalClientJournal(new ClientRunPlan(), evidence, desktopTask: false,
            expectedCommandLineForTest: new string('a', 64),
            processProbeForTest: id => new ProbedProcess(id, ProbedState.Same, "123456", new string('b', 64)));
        owner.Begin();
        using var current = Process.GetCurrentProcess();
        var process = new ObservedProcess(current.Id);
        owner.Started(process);
        Assert.Throws<InvalidOperationException>(owner.Complete);
        process.Exited = true;
        owner.Complete();

        var records = File.ReadAllLines(Path.Combine(journal, "run-journal-test", "client.jsonl"))
            .Select(RunJournal.ParseLine).ToArray();
        Assert.Equal([JournalEntry.ProcessIntended, JournalEntry.ProcessStarted, JournalEntry.ProcessStopped],
            records.Select(record => record.Entry.Kind));
        Assert.Equal(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), records[1].Entry.Fields["pid"]);
        Assert.Equal(64, records[1].Entry.Fields["commandLineSha256"].Length);
        Assert.Equal(records[1].Entry.Fields["startIdentity"], records[2].Entry.Fields["startIdentity"]);
    }

    [Fact]
    public void DesktopIntentDoesNotPrecreateTheLaunchersExclusiveDirectory()
    {
        string journal = Path.Combine(_root, "journal"), evidence = Path.Combine(_root, "evidence");
        Directory.CreateDirectory(evidence);
        using var directory = RunJournal.UseLocalDirectory(journal);
        using var run = RunJournal.UseRun("run-desktop-intent");
        var owner = new LocalClientJournal(new ClientRunPlan(), evidence, desktopTask: true,
            expectedCommandLineForTest: new string('a', 64));
        owner.Begin();
        Assert.False(Directory.Exists(Path.Combine(evidence, "desktop-launch")));
        Assert.True(File.Exists(Path.Combine(journal, "run-desktop-intent", "client.jsonl")));
    }

    private sealed class ObservedProcess(int id) : IOwnedProcess
    {
        public int Id => id;
        public bool Exited { get; set; }
        public bool HasExited => Exited;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => Task.FromResult(0);
        public void Stop(TimeSpan timeout) => Exited = true;
        public void Dispose() { }
    }
}
