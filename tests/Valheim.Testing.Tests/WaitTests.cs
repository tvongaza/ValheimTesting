using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Xunit;

// Waits are judged by what they return or throw, never by how fast the machine is: a deadline that should not
// expire is generous, and an expiry test waits for something that cannot arrive.
public class LogWaitTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
    [Fact] public async Task LinesAlreadyInTheFileNeverMatch()
    {
        using var file = new TempLog(); file.Append("ready\n");
        using var log = new LogWait(file.Path);
        file.Append("other\n");
        var error = await Assert.ThrowsAsync<WaitTimeoutException>(() => log.WaitAsync("ready", TimeSpan.FromMilliseconds(300)));
        Assert.Equal("other", error.LastSeen); Assert.Contains("ready", error.Target); Assert.Contains(file.Path, error.Message);
    }
    [Fact] public async Task LinesAppendedByAnotherTaskAreFound()
    {
        using var file = new TempLog(); file.Append("server ready\n");
        using var log = new LogWait(file.Path);
        var writer = Task.Run(() => { for (int i = 0; i < 50; i++) file.Append("noise " + i + "\n"); file.Append("server ready\n"); });
        var line = await log.WaitAsync(new Regex("^server (ready)$"), Generous);
        Assert.Equal("ready", line.Match.Groups[1].Value); await writer;
        Assert.Equal("server ready", log.LastLine);
    }
    [Fact] public async Task AFileCreatedAfterTheWaitStartedIsReadFromItsStart()
    {
        using var file = new TempLog();
        using var log = new LogWait(file.Path);
        var wait = log.WaitAsync("listening", Generous);
        file.Append("booting\nlistening\n");
        Assert.Equal("listening", (await wait).Text);
    }
    [Fact] public async Task AWatcherEventWakesTheWaitWithoutTheSafetyReread()
    {
        using var file = new TempLog(); file.Append("booting\n");
        using var log = new LogWait(file.Path) { SafetyInterval = Timeout.InfiniteTimeSpan };
        var wait = log.WaitAsync("ready", Generous);
        file.Append("ready\n");
        Assert.Equal("ready", (await wait).Text);
    }
    [Fact] public async Task APartialLineWaitsForItsNewline()
    {
        using var file = new TempLog();
        using var log = new LogWait(file.Path);
        file.Append("rea");
        Assert.Equal("rea [incomplete line]", log.Refresh());
        file.Append("dy\r\n");
        Assert.Equal("ready", (await log.WaitAsync(new Regex("^ready$"), Generous)).Text);
    }
    [Fact] public async Task ATruncatedFileIsReadAgainFromItsStart()
    {
        using var file = new TempLog(); file.Append(new string('x', 4000) + "\nlistening\n");
        using var log = new LogWait(file.Path);
        File.WriteAllText(file.Path, "boot 2\nlistening\n");
        Assert.Equal("boot 2", (await log.WaitAsync("boot", Generous)).Text);
        Assert.Equal("listening", (await log.WaitAsync("listening", Generous)).Text);
    }
    [Fact] public async Task AReplacedLongerFileIsReadFromItsStart()
    {
        using var file = new TempLog(); file.Append("boot 1\nlistening\n");
        using var log = new LogWait(file.Path);
        File.Delete(file.Path); file.Append("boot 2\n" + new string('y', 4000) + "\nlistening\n");
        Assert.Equal("boot 2", (await log.WaitAsync("boot", Generous)).Text);
        Assert.Equal("listening", (await log.WaitAsync("listening", Generous)).Text);
    }
    [Fact] public async Task AGivenOffsetSkipsEverythingBeforeIt()
    {
        using var file = new TempLog(); file.Append("ready early\n");
        long offset = new FileInfo(file.Path).Length; file.Append("ready late\n");
        using var log = new LogWait(file.Path, offset);
        Assert.Equal("ready late", (await log.WaitAsync("ready", Generous)).Text);
    }
    [Fact] public async Task AFailureLineEndsTheWaitAtOnce()
    {
        using var file = new TempLog();
        using var log = new LogWait(file.Path);
        file.Append("loading\n[Error  : BepInEx] Could not load plugin\nlistening\n");
        var error = await Assert.ThrowsAsync<WaitFailedException>(() => log.WaitAsync("listening", Generous, ["[Error"]));
        Assert.Equal("[Error  : BepInEx] Could not load plugin", error.LastSeen); Assert.Contains("[Error", error.Reason);
    }
    [Fact] public async Task UnmatchedLinesStayQueuedForTheNextWait()
    {
        using var file = new TempLog();
        using var log = new LogWait(file.Path);
        file.Append("first\nsecond\n");
        await log.WaitAsync("first", Generous);
        Assert.Equal("second", (await log.WaitAsync("second", Generous)).Text);
    }
    [Fact] public async Task AWaitNeedsAnExplicitFiniteTimeout()
    {
        using var file = new TempLog();
        using var log = new LogWait(file.Path);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => log.WaitAsync("x", TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => log.WaitAsync("x", Timeout.InfiniteTimeSpan));
    }
    [Fact] public async Task CancellationIsNotReportedAsExpiry()
    {
        using var file = new TempLog();
        using var log = new LogWait(file.Path);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.WaitAsync("x", Generous, cancellation: cancel.Token));
    }
}

// ObservedWait is the one polling wait; these pin its rules once for every caller (sessions, placement, fixtures, startup).
public class ObservedWaitTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
    [Fact] public async Task ReturnsTheFirstMatchingObservation()
    {
        int reads = 0;
        Assert.Equal(3, await ObservedWait.UntilAsync("three reads", () => ++reads, n => n == 3, Generous, TimeSpan.FromMilliseconds(1)));
        reads = 0;
        Assert.Equal(3, ObservedWait.Until("three reads", () => ++reads, n => n == 3, Generous, TimeSpan.FromMilliseconds(1)));
    }
    [Fact] public async Task ExpiryNamesTheTargetAndTheLastObservation()
    {
        int reads = 0;
        var error = await Assert.ThrowsAsync<WaitTimeoutException>(() => ObservedWait.UntilAsync("a value that never comes", () => ++reads, _ => false,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5), describe: n => "read " + n));
        Assert.Equal("a value that never comes", error.Target); Assert.Equal("read " + reads, error.LastSeen);
        Assert.Throws<WaitTimeoutException>(() => ObservedWait.Until("a value that never comes", () => false, x => x, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5)));
    }
    [Fact] public void AFailingObservationEndsTheWaitAtOnce()
    {
        int reads = 0;
        var error = Assert.Throws<WaitFailedException>(() => ObservedWait.Until("ready", () => ++reads, _ => false, Generous, TimeSpan.FromMilliseconds(1),
            fails: n => n == 2 ? "the source broke" : null));
        Assert.Equal("the source broke", error.Reason); Assert.Equal(2, reads); Assert.True(error.Elapsed < Generous);
    }
    [Fact] public async Task TheChangeEventEndsThePauseEarly()
    {
        int reads = 0;
        // The interval is a minute: only the event can bring the second observation within the test's lifetime.
        Assert.Equal(2, await ObservedWait.UntilAsync("the second read", () => ++reads, n => n == 2, Generous, Generous, changed: (_, _) => Task.CompletedTask));
        reads = 0;
        int woken = 0;
        Assert.Equal(2, ObservedWait.RunBlocking("the second read", _ => ++reads, n => n == 2, Generous, Generous, default, changed: (_, _) => woken++).Value);
        Assert.Equal(1, woken);
    }
    [Fact] public async Task AnExpiredOrFailedEventOnlyEndsTheInterval()
    {
        int reads = 0;
        Assert.Equal(3, await ObservedWait.UntilAsync("the third read", () => ++reads, n => n == 3, Generous, TimeSpan.FromMilliseconds(5),
            changed: (left, token) => Task.Delay(Timeout.Infinite, token)));
    }
    [Fact] public async Task CancellationEndsThePauseNotTheDeadline()
    {
        // A minute's interval and deadline: only the token can end these waits quickly.
        using var cancel = new CancellationTokenSource();
        int reads = 0;
        var waiting = ObservedWait.UntilAsync("never", () => { if (++reads == 1) cancel.CancelAfter(20); return false; }, x => x, Generous, Generous, cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        using var blocking = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => ObservedWait.Until("never", () => { blocking.CancelAfter(20); return false; }, x => x, Generous, Generous, blocking.Token));
        using var during = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => ObservedWait.RunBlocking("never", _ => false, x => x, Generous, Generous, during.Token,
            changed: (wait, token) => { during.Cancel(); token.WaitHandle.WaitOne(wait); }));
    }
    [Fact] public async Task EveryWaitStatesAFiniteTimeoutAndInterval()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ObservedWait.UntilAsync("x", () => true, x => x, TimeSpan.Zero, Generous));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObservedWait.Until("x", () => true, x => x, Generous, TimeSpan.Zero));
    }
}

// ExitRace is the one race of work against a process exit and a deadline (owned server and client startup, local process runs).
public class ExitRaceTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
    [Fact] public async Task WhicheverComesFirstEndsTheRace()
    {
        var never = new TaskCompletionSource().Task;
        Assert.Equal(RaceEnd.Completed, await ExitRace.RunAsync(Task.CompletedTask, never, System.Diagnostics.Stopwatch.StartNew(), Generous, default));
        Assert.Equal(RaceEnd.Exited, await ExitRace.RunAsync(never, Task.CompletedTask, System.Diagnostics.Stopwatch.StartNew(), Generous, default));
        using var cancel = new CancellationTokenSource(20);
        Assert.Equal(RaceEnd.Expired, await ExitRace.RunAsync(never, never, System.Diagnostics.Stopwatch.StartNew(), Generous, cancel.Token));
    }
    [Fact] public async Task TheDeadlineNeverEndsTheRaceBeforeTheClockAgrees()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var never = new TaskCompletionSource().Task;
        Assert.Equal(RaceEnd.Expired, await ExitRace.RunAsync(never, never, clock, TimeSpan.FromMilliseconds(200), default));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(200), clock.Elapsed.ToString());
    }
}

public class LogWaitEvidenceTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
    [Fact] public void OutputCountsOnlyWhatWasWrittenAfterTheWaitOpened()
    {
        using var file = new TempLog(); file.Append("previous boot\n");
        using var log = new LogWait(file.Path);
        Assert.False(log.HasOutput()); // The previous boot's log is still there, unchanged: nothing new was written.
        file.Append("[Message:   BepInEx] BepInEx 5.4.23.2\n");
        Assert.True(log.HasOutput());
        using var missing = new TempLog();
        using var never = new LogWait(missing.Path);
        Assert.False(never.HasOutput());
    }
    [Fact] public void AReplacedLogCountsAsOutput()
    {
        using var file = new TempLog(); file.Append(new string('x', 400) + "\n");
        using var log = new LogWait(file.Path);
        File.WriteAllText(file.Path, "boot 2\n");
        Assert.True(log.HasOutput());
    }
    [Fact] public async Task AFailureCarriesTheLinesBeforeItButNotAReplacedFilesLines()
    {
        using var file = new TempLog();
        using var log = new LogWait(file.Path);
        file.Append("boot 1 a\nboot 1 b\n");
        await Assert.ThrowsAsync<WaitTimeoutException>(() => log.WaitAsync("never", TimeSpan.FromMilliseconds(200)));
        File.WriteAllText(file.Path, "boot 2 a\nFATAL\n");
        var error = await Assert.ThrowsAsync<WaitFailedException>(() => log.WaitAsync("never", Generous, ["FATAL"]));
        Assert.Equal("FATAL", error.LastSeen); Assert.Equal(new[] { "boot 2 a" }, error.Context);
        Assert.EndsWith("Lines before it:" + Environment.NewLine + "  boot 2 a", error.Message);
    }
}

// A log file in its own temporary directory, appended the way a game appends: open, write, close.
internal sealed class TempLog : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wait-" + Guid.NewGuid().ToString("N"));
    public string Path { get; }
    public TempLog() { Directory.CreateDirectory(_directory); Path = System.IO.Path.Combine(_directory, "LogOutput.log"); }
    public void Append(string text)
    {
        using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
