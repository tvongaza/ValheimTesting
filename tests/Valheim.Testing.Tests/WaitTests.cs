using System.Diagnostics;
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

public class ProcessWaitTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
    [Fact] public async Task ReturnsAShellsExitCode()
    {
        using var process = Process.Start(Shell("exit 3"))!;
        Assert.Equal(3, await ProcessWait.ForExitAsync(process, Generous));
    }
    [Fact] public async Task ADotnetChildExitsCleanly()
    {
        // The SDK tells child processes which host runs them; PATH is the fallback.
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", "--version") { RedirectStandardOutput = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        Assert.Equal(0, await ProcessWait.ForExitAsync(process, Generous));
        Assert.Matches(@"^\d+\.\d+", (await output).Trim());
    }
    [Fact] public async Task ExpiryReportsWhatItWaitedForAndLeavesTheProcessRunning()
    {
        using var process = Process.Start(LongRunning())!;
        try
        {
            var error = await Assert.ThrowsAsync<WaitTimeoutException>(() => ProcessWait.ForExitAsync(process, TimeSpan.FromMilliseconds(200), lastSeen: () => "still booting"));
            Assert.Contains(process.Id.ToString(CultureInfo.InvariantCulture), error.Target); Assert.Equal("still booting", error.LastSeen);
            Assert.False(process.HasExited);
        }
        finally { process.Kill(entireProcessTree: true); process.WaitForExit(); }
    }
    [Fact] public async Task CancellationIsNotReportedAsExpiry()
    {
        using var process = Process.Start(LongRunning())!;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessWait.ForExitAsync(process, Generous, cancel.Token)); }
        finally { process.Kill(entireProcessTree: true); process.WaitForExit(); }
    }
    internal static ProcessStartInfo Shell(string command)
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("/bin/sh");
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c"); start.ArgumentList.Add(command);
        return start;
    }
    // Runs for minutes; the tests kill it. Output is redirected so it stays out of the test log.
    private static ProcessStartInfo LongRunning()
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("ping", "-n 300 127.0.0.1") : new ProcessStartInfo("/bin/sleep", "300");
        start.RedirectStandardOutput = true;
        return start;
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
