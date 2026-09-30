using System.Diagnostics;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// Stopping owned processes: asked to quit first, killed only after the wait, and every stop reported (#85).
public class ProcessStopTests
{
    [Fact] public void EveryBootIsAskedToQuitAndEachStopIsRecorded()
    {
        var fake = new FakeOwnedServer("test.mod");
        var session = fake.Session(TimeSpan.FromSeconds(10));
        session.Start();
        session.Restart();
        session.Dispose();
        Assert.Equal(new[] { StopOutcome.Clean, StopOutcome.Clean }, session.Stops.Select(stop => stop.Outcome));
        Assert.Equal(new[] { "stop1", "stop2" }, fake.Events.Where(e => e.StartsWith("stop")));
        Assert.Equal(TimeSpan.FromMinutes(2), session.QuitTimeout);
    }

    [Fact] public void AServerThatIgnoresTheQuitIsKilledAfterTheWaitAndReportedSo()
    {
        var fake = new FakeOwnedServer("test.mod") { IgnoreQuit = true };
        var session = new OwnedServerSession(fake.Launch, fake.Connect, fake.SaveRoot, "cli_expect worlduid=1", fake.SessionCapability,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1)) { QuitTimeout = TimeSpan.FromSeconds(7) };
        session.Start();
        session.Restart(); // A restart takes the same path: the killed boot is replaced, not left running.
        session.Dispose();
        Assert.All(session.Stops, stop => Assert.Equal(StopOutcome.Killed, stop.Outcome));
        Assert.Equal(TimeSpan.FromSeconds(7), session.Stops[0].Elapsed);
        Assert.StartsWith("killed after 7.0 s (fake quit request; no exit within the wait, exit -1)", session.Stops[0].ToString());
    }

    [Fact] public void AProcessThatCannotBeAskedIsKilledAtOnce()
    {
        IServerProcess process = new StopOnly();
        var stop = process.StopCleanly(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));
        Assert.Equal(StopOutcome.Killed, stop.Outcome);
        Assert.True(((StopOnly)process).Stopped);
        Assert.Equal("this process cannot be asked to quit", stop.Request);
        Assert.StartsWith("killed after ", stop.ToString());
    }

    [Fact] public void StopsRead()
    {
        Assert.Equal("clean after 9.4 s (Ctrl+C, exit 0)", new ProcessStop(StopOutcome.Clean, 0, TimeSpan.FromSeconds(9.4), "Ctrl+C").ToString());
        Assert.Equal("already exited (not asked: it had exited, exit 3)", new ProcessStop(StopOutcome.AlreadyExited, 3, TimeSpan.Zero, "not asked: it had exited").ToString());
    }

    // ---- a real local process ----

    [Fact] public void ALocalProcessAskedWithSigintQuitsWithoutAKill()
    {
        if (OperatingSystem.IsWindows()) return; // Windows: the Ctrl+C test below.
        using var dir = new TempDirectory();
        using var bystander = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        try
        {
            using var owned = new DirectServerProcess(new ProcessStartInfo("/bin/sleep", "30"), Path.Combine(dir.Path, "owned"));
            var stop = owned.StopCleanly(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
            Assert.Equal(StopOutcome.Clean, stop.Outcome);
            Assert.Equal("SIGINT", stop.Request);
            Assert.True(owned.HasExited);
            Assert.False(bystander.HasExited);
        }
        finally { bystander.Kill(); bystander.WaitForExit(); }
    }

    [Fact] public void ALocalProcessIgnoringSigintIsKilledAfterTheWait()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        string ready = Path.Combine(dir.Path, "ready");
        // The trap is set before the ready file appears, and is inherited by sleep; the shell and its sleep are the owned tree.
        var start = new ProcessStartInfo("/bin/sh");
        foreach (string argument in new[] { "-c", "trap '' INT; : > \"$1\"; sleep 30", "sh", ready }) start.ArgumentList.Add(argument);
        using var owned = new DirectServerProcess(start, Path.Combine(dir.Path, "owned"));
        using (var created = new FileSystemWatcher(dir.Path, "ready") { EnableRaisingEvents = true })
            if (!File.Exists(ready)) created.WaitForChanged(WatcherChangeTypes.Created, 10_000);
        var clock = Stopwatch.StartNew();
        var stop = owned.StopCleanly(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Equal(StopOutcome.Killed, stop.Outcome);
        Assert.Equal("SIGINT; no exit within 1.0 s", stop.Request);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1));
        Assert.True(owned.HasExited);
    }

    [Fact] public void AWindowsConsoleProcessQuitsOnCtrlCToItsOwnConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        // ping -t runs until Ctrl+C. Its own windowless console, as ServerLaunch gives the dedicated server, so the Ctrl+C
        // reaches only it and the helper, never this test's console.
        using var owned = new DirectServerProcess(new ProcessStartInfo("ping", "-t 127.0.0.1") { CreateNoWindow = true }, Path.Combine(dir.Path, "owned"));
        var stop = owned.StopCleanly(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        Assert.Equal(StopOutcome.Clean, stop.Outcome);
        Assert.Equal("Ctrl+C", stop.Request);
        Assert.Contains("Ping statistics", File.ReadAllText(Path.Combine(dir.Path, "owned.stdout.log")));
    }

    // ---- crossplay lobbies ----

    private static string Log(TempDirectory dir, string name, params string[] lines)
    {
        string path = Path.Combine(dir.Path, name);
        File.WriteAllLines(path, lines);
        return path;
    }
    private const string Created1 = "[Info   : Unity Log] Created PlayFab lobby with ID \"L1\", ConnectionString \"c\" and owned by \"P\"";
    private const string Created2 = "[Info   : Unity Log] Created PlayFab lobby with ID \"L2\", ConnectionString \"c\" and owned by \"P\"";
    private const string Unregistered = "[Info   : Unity Log] Unregister PlayFab server \"World\" and leaving network \"N\"";
    private static ProcessStop Clean => new(StopOutcome.Clean, 0, TimeSpan.FromSeconds(8), "Ctrl+C");

    [Fact] public void CleanStopsWithDeactivatedLobbiesPass()
    {
        using var dir = new TempDirectory();
        var lines = CrossplayServer.RequireLobbiesRetired([Clean, Clean],
            [Log(dir, "b1", Created1, Unregistered, "[Info   : Unity Log] Deactivated PlayFab lobby L1"), Log(dir, "b2", Created2, Unregistered, "[Info   : Unity Log] Deactivated PlayFab lobby L2")]);
        Assert.Equal("boot-1: clean after 8.0 s (Ctrl+C, exit 0); lobbies created L1; deactivated L1", lines[0]);
        Assert.Equal(2, lines.Count);
    }

    [Fact] public void AKilledBootFailsTheCrossplayRunEvenWhenTheNextJoinWorked()
    {
        using var dir = new TempDirectory();
        var killed = new ProcessStop(StopOutcome.Killed, -1, TimeSpan.FromSeconds(120), "Ctrl+C; no exit within 120.0 s");
        var error = Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([killed, Clean],
            [Log(dir, "b1", Created1), Log(dir, "b2", Created2, Unregistered, "[Info   : Unity Log] Deactivated PlayFab lobby L2")]));
        Assert.Contains("boot-1 was killed after 120.0 s", error.Message);
        Assert.Contains("not a clean crossplay run", error.Message);
        Assert.Contains("boot-1 created PlayFab lobby L1, but its log never says \"Deactivated PlayFab lobby L1\"", error.Message);
        Assert.DoesNotContain("boot-2", error.Message);
    }

    [Fact] public void ACleanStopWhoseLobbyWasNeverConfirmedFails()
    {
        using var dir = new TempDirectory();
        var error = Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([Clean], [Log(dir, "b1", Created1, Unregistered)]));
        Assert.Contains("the shutdown started retiring it", error.Message);
        // A missing log, or a boot never stopped, cannot be read as retired either.
        Assert.Contains("was not kept", Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([Clean], [Path.Combine(dir.Path, "missing")])).Message);
        Assert.Contains("never stopped", Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([], [Log(dir, "b2", Created1)])).Message);
    }

    [Fact] public void ABootThatExitedByItselfCountsOnlyWithTheGamesShutdown()
    {
        using var dir = new TempDirectory();
        var gone = new ProcessStop(StopOutcome.AlreadyExited, 0, TimeSpan.Zero, "not asked: it had exited");
        Assert.Single(CrossplayServer.RequireLobbiesRetired([gone], [Log(dir, "b1", Created1, Unregistered, "Deactivated PlayFab lobby L1")]));
        Assert.Contains("without the game's shutdown", Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([gone], [Log(dir, "b2")])).Message);
    }

    private sealed class StopOnly : IServerProcess
    {
        public bool Stopped;
        public int Id => 1;
        public bool HasExited => Stopped;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => Task.FromResult(-1);
        public void Stop(TimeSpan timeout) => Stopped = true;
        public void Dispose() { }
    }
}
