using System.Diagnostics;
using Valheim.Testing.ProcessSignalProbe;
using Xunit;

public sealed class RunCancellationTests
{
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
