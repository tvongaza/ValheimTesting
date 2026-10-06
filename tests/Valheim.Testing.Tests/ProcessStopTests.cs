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
        IOwnedProcess process = new StopOnly();
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

    [Fact] public void WindowsSignalHelperCannotOutwaitTheSmokeQuitBudget()
    {
        Assert.Equal(20_000, ProcessQuit.WindowsHelperWaitMilliseconds(TimeSpan.FromSeconds(20)));
        Assert.Equal(30_000, ProcessQuit.WindowsHelperWaitMilliseconds(TimeSpan.FromSeconds(120)));
        Assert.Equal(1, ProcessQuit.WindowsHelperWaitMilliseconds(TimeSpan.Zero));
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

    [Fact] public void ZeroQuitTimeoutKillsWithoutSendingAQuitRequest()
    {
        using var dir = new TempDirectory();
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t 127.0.0.1") { CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sleep", "30");
        using var owned = new DirectServerProcess(start, Path.Combine(dir.Path, "owned"));
        var stop = owned.StopCleanly(TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Equal(StopOutcome.Killed, stop.Outcome);
        Assert.Equal("not asked to quit", stop.Request);
        Assert.True(owned.HasExited);
    }

    [Fact] public void ADisposedOwnedProcessStillProvesItsExitToAccountAndHostLocks()
    {
        using var dir = new TempDirectory();
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t 127.0.0.1") { CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sleep", "30");
        var owned = new DirectServerProcess(start, Path.Combine(dir.Path, "owned"));
        owned.Stop(TimeSpan.FromSeconds(5));
        owned.Dispose();
        Assert.True(owned.HasExited);
        owned.Dispose(); // Cleanup is idempotent.
    }

    // A capture can only stay open after the exit when another process holds the pipe; the failure names which one.
    [Fact] public void ACaptureHeldOpenByAnEscapedProcessIsNamed()
    {
        if (OperatingSystem.IsWindows()) return; // The escape below is a POSIX double fork.
        using var dir = new TempDirectory();
        string pidFile = Path.Combine(dir.Path, "escaped.pid"), ready = Path.Combine(dir.Path, "ready");
        // The subshell starts a sleep with the inherited stdout (stderr closed) and exits, so the sleep leaves the owned tree.
        var start = new ProcessStartInfo("/bin/sh");
        foreach (string argument in new[] { "-c", "(sleep 30 2>/dev/null & echo $! > \"$1\"); : > \"$2\"; sleep 30", "sh", pidFile, ready }) start.ArgumentList.Add(argument);
        using var owned = new DirectServerProcess(start, Path.Combine(dir.Path, "owned"));
        try
        {
            using (var created = new FileSystemWatcher(dir.Path, "ready") { EnableRaisingEvents = true })
                if (!File.Exists(ready)) created.WaitForChanged(WatcherChangeTypes.Created, 10_000);
            var error = Assert.Throws<TimeoutException>(() => owned.StopCleanly(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
            Assert.Contains("stdout still open", error.Message);
            Assert.DoesNotContain("stderr", error.Message);
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
                try { using var escaped = Process.GetProcessById(pid); escaped.Kill(); escaped.WaitForExit(); } catch (ArgumentException) { }
        }
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

    private static IReadOnlyList<string> Logs(params string[] paths) => paths;

    [Fact] public void CleanStopsThatRetiredTheirLobbiesPassAndSayWhetherPlayFabConfirmed()
    {
        using var dir = new TempDirectory();
        // Boot 1's lines are in its Unity log only (as on the Windows server); boot 2's in its BepInEx log, with PlayFab's reply.
        var lines = CrossplayServer.RequireLobbiesRetired([Clean, Clean],
            [Logs(Log(dir, "b1-bepinex", "[Info   :   BepInEx] Chainloader startup complete"), Log(dir, "b1-unity", Created1, Unregistered), Path.Combine(dir.Path, "b1-stdout-missing")),
             Logs(Log(dir, "b2-bepinex", Created2, Unregistered, "[Info   : Unity Log] Deactivated PlayFab lobby L2"))]);
        Assert.Equal("boot-1: clean after 8.0 s (Ctrl+C, exit 0); lobby L1; retired yes; PlayFab confirmation not logged", lines[0]);
        Assert.Equal("boot-2: clean after 8.0 s (Ctrl+C, exit 0); lobby L2; retired yes; PlayFab confirmation logged", lines[1]);
    }

    [Fact] public void AKilledBootFailsTheCrossplayRunEvenWhenTheNextJoinWorked()
    {
        using var dir = new TempDirectory();
        var killed = new ProcessStop(StopOutcome.Killed, -1, TimeSpan.FromSeconds(120), "Ctrl+C; no exit within 120.0 s");
        var error = Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([killed, Clean],
            [Logs(Log(dir, "b1", Created1)), Logs(Log(dir, "b2", Created2, Unregistered))]));
        Assert.Contains("boot-1 was killed after 120.0 s", error.Message);
        Assert.Contains("not a clean crossplay run", error.Message);
        Assert.DoesNotContain("boot-2", error.Message);
    }

    [Fact] public void ACleanStopThatNeverRetiredItsLobbyOrLogsWithoutALobbyFail()
    {
        using var dir = new TempDirectory();
        Assert.Contains("never retired it", Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([Clean], [Logs(Log(dir, "b1", Created1))])).Message);
        // Logs without the game's lines cannot pass by showing nothing.
        Assert.Contains("hold no \"Created PlayFab lobby\" line", Assert.Throws<InvalidOperationException>(() =>
            CrossplayServer.RequireLobbiesRetired([Clean], [Logs(Log(dir, "b2", "[Info   :   BepInEx] Chainloader startup complete", Unregistered))])).Message);
        Assert.Contains("no log was kept", Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([Clean], [Logs(Path.Combine(dir.Path, "missing"))])).Message);
        Assert.Contains("never stopped", Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([], [Logs(Log(dir, "b3", Created1))])).Message);
    }

    [Fact] public void ABootThatExitedByItselfCountsOnlyWithTheGamesShutdown()
    {
        using var dir = new TempDirectory();
        var gone = new ProcessStop(StopOutcome.AlreadyExited, 0, TimeSpan.Zero, "not asked: it had exited");
        Assert.Single(CrossplayServer.RequireLobbiesRetired([gone], [Logs(Log(dir, "b1", Created1, Unregistered))]));
        Assert.Contains("without the game's shutdown", Assert.Throws<InvalidOperationException>(() => CrossplayServer.RequireLobbiesRetired([gone], [Logs(Log(dir, "b2", Created1))])).Message);
    }

    [Fact] public void TheGamesLogIsTheLogFileArgument()
    {
        var plan = new ServerRunPlan { Arguments = ["-batchmode", "-logFile", "{runtime}/toolkit-unity.log"] };
        Assert.Equal("/rt/toolkit-unity.log", plan.GameLogFile("/rt", "/w"));
        Assert.Null(new ServerRunPlan { Arguments = ["-batchmode"] }.GameLogFile("/rt", "/w"));
    }

    // Not FakeOwnedProcess: that fake overrides StopCleanly, and this one must reach the interface's default (kill at once).
    private sealed class StopOnly : IOwnedProcess
    {
        public bool Stopped;
        public int Id => 1;
        public bool HasExited => Stopped;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => Task.FromResult(-1);
        public void Stop(TimeSpan timeout) => Stopped = true;
        public void Dispose() { }
    }
}

// SSH_CONNECTION and SSH_CLIENT are process-wide and choose the Windows quit signal. Keep these Windows-only tests isolated
// and restore the variables even on failure.
[CollectionDefinition(nameof(WindowsSshQuitSignal), DisableParallelization = true)]
public sealed class WindowsSshQuitSignal { }

[Collection(nameof(WindowsSshQuitSignal))]
public sealed class WindowsSshQuitSignalTests
{
    // Not over SSH, the runner sends Ctrl+C. The variables are cleared because a runner that is itself started over SSH (as
    // on a test station) would otherwise send Ctrl+Break, which ping -t answers with statistics and keeps running.
    [Fact] public void AWindowsConsoleProcessQuitsOnCtrlCToItsOwnConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        string? previousConnection = Environment.GetEnvironmentVariable("SSH_CONNECTION"), previousClient = Environment.GetEnvironmentVariable("SSH_CLIENT");
        Environment.SetEnvironmentVariable("SSH_CONNECTION", null);
        Environment.SetEnvironmentVariable("SSH_CLIENT", null);
        try
        {
            // ping -t runs until Ctrl+C. Its own windowless console, as GameLaunch.ToStartInfo gives the dedicated server, so the Ctrl+C
            // reaches only it and the helper, never this test's console.
            using var owned = new DirectServerProcess(new ProcessStartInfo("ping", "-t 127.0.0.1") { CreateNoWindow = true }, Path.Combine(dir.Path, "owned"));
            var stop = owned.StopCleanly(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
            Assert.Equal(StopOutcome.Clean, stop.Outcome);
            Assert.Equal("Ctrl+C", stop.Request);
            Assert.Contains("Ping statistics", File.ReadAllText(Path.Combine(dir.Path, "owned.stdout.log")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SSH_CONNECTION", previousConnection);
            Environment.SetEnvironmentVariable("SSH_CLIENT", previousClient);
        }
    }

    [Fact] public void AnSshLaunchedChildReceivesCtrlBreakBeforeTheQuitDeadline()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        string marker = Path.Combine(dir.Path, "control-event.txt");
        string? previousSsh = Environment.GetEnvironmentVariable("SSH_CONNECTION");
        Environment.SetEnvironmentVariable("SSH_CONNECTION", "test-runner-over-ssh");
        try
        {
            var start = new ProcessStartInfo("dotnet") { CreateNoWindow = true };
            start.ArgumentList.Add(typeof(Valheim.Testing.ProcessSignalProbe.Marker).Assembly.Location);
            start.ArgumentList.Add(marker);
            using var owned = new DirectServerProcess(start, Path.Combine(dir.Path, "owned"));
            var ready = Stopwatch.StartNew();
            while (!File.Exists(marker + ".ready") && ready.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(25);
            Assert.True(File.Exists(marker + ".ready"), "the probe did not install its console handler");

            var stop = owned.StopCleanly(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(5));
            Assert.Equal(StopOutcome.Clean, stop.Outcome);
            Assert.Equal("Ctrl+Break", stop.Request);
            Assert.Equal("ControlBreak", File.ReadAllText(marker));
            Assert.True(stop.Elapsed < TimeSpan.FromSeconds(25), stop.ToString());
        }
        finally { Environment.SetEnvironmentVariable("SSH_CONNECTION", previousSsh); }
    }
}

// #121: the captures must not need the thread pool. With every pool thread blocked, as when many tests in a run block at once,
// a killed process's output is still kept within the stop bound. It starves the pool, so it runs alone.
[CollectionDefinition(nameof(ThreadPoolStarvation), DisableParallelization = true)]
public sealed class ThreadPoolStarvation { }

[Collection(nameof(ThreadPoolStarvation))]
public class ProcessCaptureStarvationTests
{
    [Fact] public void TheOutputIsKeptWhileTheThreadPoolIsStarved()
    {
        using var dir = new TempDirectory();
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t 127.0.0.1") { CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sleep", "30");
        // Never disposed: blockers still queued when the test ends start afterwards, and a Wait on a disposed event would throw
        // on a pool thread and crash the test host.
        var release = new ManualResetEventSlim();
        ThreadPool.GetMinThreads(out int workers, out _);
        // More blockers than the pool has threads or adds in the test's time (about two a second while starved), so any work
        // item queued after them waits.
        int blockers = workers + 100, finished = 0;
        for (int i = 0; i < blockers; i++) ThreadPool.UnsafeQueueUserWorkItem(_ => { release.Wait(); Interlocked.Increment(ref finished); }, null);
        try
        {
            using var owned = new DirectServerProcess(start, Path.Combine(dir.Path, "owned"));
            var clock = Stopwatch.StartNew();
            var stop = owned.StopCleanly(TimeSpan.Zero, TimeSpan.FromSeconds(5));
            Assert.Equal(StopOutcome.Killed, stop.Outcome);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"stopped and kept in {clock.Elapsed}");
            Assert.True(File.Exists(Path.Combine(dir.Path, "owned.stdout.log")));
        }
        finally
        {
            release.Set();
            // The released pool drains the blockers before the next test runs.
            var drained = Stopwatch.StartNew();
            while (Volatile.Read(ref finished) < blockers && drained.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(50);
        }
    }
}
