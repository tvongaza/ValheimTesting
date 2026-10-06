using System.Diagnostics;
using Valheim.Testing.Game;
using Valheim.Testing.ProcessSignalProbe;
using Xunit;
using Valheim.Testing.GameSessions;

public sealed class RunCancellationTests
{
    // #257 step 5: the first signal cancels the run, never its cleanup; a later one during cleanup abandons it; so does the budget.
    [Fact]
    public async Task CleanupHasItsOwnTokenThatOnlyASecondSignalOrItsBudgetEnds()
    {
        using (var run = new RunCancellation())
        {
            run.SignalCancel();
            Assert.True(run.Token.IsCancellationRequested);
            var cleanup = run.BeginCleanup();
            Assert.False(cleanup.IsCancellationRequested);
            Assert.Equal(cleanup, run.BeginCleanup());
            run.SignalCancel();
            Assert.True(cleanup.IsCancellationRequested);
            Assert.Equal("a second interrupt during cleanup", run.Abandoned);
        }
        using (var run = new RunCancellation())
        {
            // One signal that arrives only during cleanup is a first signal: the cleanup goes on.
            var cleanup = run.BeginCleanup();
            run.SignalCancel();
            Assert.False(cleanup.IsCancellationRequested);
            Assert.Null(run.Abandoned);
        }
        using (var run = new RunCancellation())
        {
            var cleanup = run.BeginCleanup(TimeSpan.FromMilliseconds(50));
            await Task.Delay(Timeout.Infinite, cleanup).ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("cleanup did not finish within its budget of", run.Abandoned);
        }
    }

    [Fact]
    public void SigtermCancelsAConsoleRunWithoutKillingItsCleanupProcess()
    {
        if (OperatingSystem.IsWindows()) return; // Ctrl+Break is exercised by the Windows process-stop tests.
        using var directory = new TempDirectory();
        string marker = Path.Combine(directory.Path, "cancelled.txt");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(typeof(Marker).Assembly.Location);
        start.ArgumentList.Add("run-cancellation");
        start.ArgumentList.Add(marker);
        using var probe = Process.Start(start)!;
        try
        {
            var ready = Stopwatch.StartNew();
            while (!File.Exists(marker + ".ready") && ready.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(25);
            Assert.True(File.Exists(marker + ".ready"), "the probe did not install its signal handler");

            using var signal = Process.Start(new ProcessStartInfo("/bin/kill")
            {
                UseShellExecute = false,
                ArgumentList = { "-TERM", probe.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            })!;
            Assert.True(signal.WaitForExit(5000), "the signal sender did not exit");
            Assert.Equal(0, signal.ExitCode);
            Assert.True(probe.WaitForExit(10000), "the cancelled probe did not complete its own cleanup path");
            Assert.Equal(0, probe.ExitCode);
            Assert.Equal("cancelled", File.ReadAllText(marker));
        }
        finally
        {
            if (!probe.HasExited) { probe.Kill(entireProcessTree: true); probe.WaitForExit(); }
        }
    }
}
