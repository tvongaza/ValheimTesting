using System.Text;
using Valheim.Testing.Game;
using Xunit;

// HostServerLaunch, HostServer, HostServerProcess and HostInstall against fake hosts: what the scripts are sent and how their
// replies are read. The scripts themselves run for real in HostServerChecks.
public sealed class HostServerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("host-server-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static HostResult Reply(string stdout) => new(HostOutcome.Exited, 0, stdout, "", TimeSpan.FromMilliseconds(2), false);

    /// <summary>A host that answers each script from a queue and records what it was sent.</summary>
    private sealed class QueueHost(HostShell? shell = null) : IGameHost
    {
        public Queue<HostResult> Replies { get; } = new();
        public List<(string Script, IReadOnlyDictionary<string, string> Variables)> Runs { get; } = [];
        public List<string> Fetched { get; } = [];
        public string Name => "box";
        public GameHostKind Kind => GameHostKind.Ssh;
        public HostShell Shell { get; } = shell ?? HostShell.Bash;
        public Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout, CancellationToken cancellation = default)
        {
            Runs.Add((script, variables ?? new Dictionary<string, string>()));
            return Task.FromResult(Replies.Dequeue());
        }
        public Task<FetchedDirectory> FetchDirectoryAsync(string hostDirectory, string localDirectory, TimeSpan timeout, CancellationToken cancellation = default)
        {
            Fetched.Add(hostDirectory + " -> " + localDirectory);
            return Task.FromResult(new FetchedDirectory(localDirectory, "", 0, 0));
        }
        public Task<HostLock> AcquireLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<HostLockResult> CheckLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<Shipment> ShipRevisionAsync(string repository, string revision, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<Shipment> ShipFilesAsync(string localDirectory, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<long> LogOffsetAsync(string logPath, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<HostLogResult> WaitForLogAsync(string logPath, long fromOffset, System.Text.RegularExpressions.Regex success, IReadOnlyList<System.Text.RegularExpressions.Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default) => throw new NotSupportedException();
    }

    // ---- the launch ----

    [Fact] public void ALaunchCarriesBepInExsLoaderForTheServerOnly()
    {
        var launch = HostServerLaunch.Create("/srv/runs/run-1/runtime/", ["-batchmode", "-name", "it's a test"], new Dictionary<string, string> { ["MY_MOD_TOKEN"] = "abc" });
        Assert.Equal("/srv/runs/run-1/runtime", launch.Runtime);
        Assert.Equal("/srv/runs/run-1/runtime/valheim_server.x86_64", launch.Executable);
        Assert.Equal("abc", launch.Environment["MY_MOD_TOKEN"]);
        Assert.Equal(ServerLaunch.DedicatedServerSteamAppId, launch.Environment["SteamAppId"]);
        Assert.Equal("1", launch.Environment["DOORSTOP_ENABLED"]);
        Assert.Equal("/srv/runs/run-1/runtime/BepInEx/core/BepInEx.Preloader.dll", launch.Environment["DOORSTOP_TARGET_ASSEMBLY"]);
        Assert.Equal("/srv/runs/run-1/runtime/linux64:/srv/runs/run-1/runtime/doorstop_libs", launch.Prepended["LD_LIBRARY_PATH"]);
        Assert.Equal("libdoorstop_x64.so", launch.Prepended["LD_PRELOAD"]);
        Assert.Contains("doorstop_libs/libdoorstop_x64.so", launch.RequiredFiles);
        // The spec decodes back to exactly these items, the arguments in order.
        var spec = launch.Spec().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ')).Select(parts => (parts[0], Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])))).ToList();
        Assert.Equal(new[] { "-batchmode", "-name", "it's a test" }, spec.Where(item => item.Item1 == "arg").Select(item => item.Item2));
        Assert.Contains(("unset", "DOORSTOP_DISABLE"), spec);
        Assert.Equal("7", HostServerLaunch.Create("/srv/rt", [], new Dictionary<string, string> { ["SteamAppId"] = "7" }).Environment["SteamAppId"]);
    }

    [Fact] public async Task WindowsServerLaunchUsesHeadlessTaskAndStopsOnlyTheOwnedIdentity()
    {
        var host = new QueueHost(HostShell.Pwsh);
        var launch = HostServerLaunch.CreateWindows(@"C:\runs\r\runtime", ["-batchmode", "-name", "with spaces"]);
        Assert.True(launch.Windows);
        Assert.Equal(@"C:\runs\r\runtime\valheim_server.exe", launch.Executable);
        Assert.Contains("winhttp.dll", launch.RequiredFiles);
        Assert.Contains("-name \"with spaces\"", WindowsCommandLine.Join(launch.Arguments));
        Assert.Throws<ArgumentException>(() => HostServerLaunch.CreateWindows(@"C:\r", [], new Dictionary<string, string> { ["doorstop_enabled"] = "0" }));
        host.Replies.Enqueue(Reply("VT-SERVER started 701 123456789\n"));
        var process = await HostServer.StartAsync(host, launch, @"C:\runs\r\boot-1", TimeSpan.FromSeconds(60));
        Assert.Same(HostServerScripts.WindowsStart, host.Runs[0].Script);
        Assert.Contains("VT-Server-", host.Runs[0].Variables["task"]);
        Assert.DoesNotContain("Steam", host.Runs[0].Variables["launcher"]);
        host.Replies.Enqueue(Reply("VT-STOP quit\n"));
        host.Replies.Enqueue(Reply("VT-KEPT\n"));
        Assert.Equal(HostServerStop.Quit, await process.StopAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
        Assert.Same(HostServerScripts.WindowsStop, host.Runs[1].Script);
        Assert.Equal("701", host.Runs[1].Variables["game"]);
        Assert.Equal("123456789", host.Runs[1].Variables["start"]);
        Assert.Same(HostServerScripts.WindowsKeep, host.Runs[2].Script);
    }

    [Fact] public async Task WindowsServerRefusesAReplyLostDuringLaunchAsUnknown()
    {
        var host = new QueueHost(HostShell.Pwsh);
        host.Replies.Enqueue(new HostResult(HostOutcome.TransportFailed, null, "", "ssh disconnected", TimeSpan.FromSeconds(1), false));
        var error = await Assert.ThrowsAsync<HostOperationException>(() => HostServer.StartAsync(host,
            HostServerLaunch.CreateWindows(@"C:\runs\r\runtime", []), @"C:\runs\r\boot-1", TimeSpan.FromSeconds(60)));
        Assert.Contains("may have started", error.Message);
    }

    [Fact] public async Task WindowsHeadlessTaskKeepsItsChildAfterTheTaskEndsAndStopsByIdentity()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VT_TEST_WINDOWS_HOST_SERVER") != "1") return;
        string runtime = Path.Combine(_root, "runtime"), boot = Path.Combine(_root, "boot");
        Directory.CreateDirectory(runtime);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(runtime, ServerLaunch.WindowsExecutable));
        foreach (string file in HostServerLaunch.CreateWindows(runtime, []).RequiredFiles.Skip(1))
        {
            string path = Path.Combine(runtime, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test-only placeholder");
        }
        var host = new LocalGameHost("windows-task-check", HostShell.WindowsPowerShell);
        var launch = HostServerLaunch.CreateWindows(runtime, ["-n", "60", "127.0.0.1"]);
        var process = await HostServer.StartAsync(host, launch, boot, TimeSpan.FromSeconds(75));
        try
        {
            Assert.False(process.HasExited);
            using var child = System.Diagnostics.Process.GetProcessById(process.Id);
            Assert.False(child.HasExited);
            // A wrong start identity must not kill a live process with this PID.
            string foreignBoot = Path.Combine(_root, "foreign");
            Directory.CreateDirectory(foreignBoot);
            var foreign = new HostServerProcess(host, process.Id, "0", foreignBoot, runtime, [], null);
            Assert.Equal(HostServerStop.AlreadyGone, await foreign.StopAsync(TimeSpan.FromSeconds(1)));
            Assert.False(child.HasExited);
        }
        finally { await process.StopAsync(TimeSpan.FromSeconds(15)); }
        Assert.DoesNotContain(System.Diagnostics.Process.GetProcesses(), other => other.Id == process.Id);
    }

    [Fact] public async Task WindowsHostCopiesAndRetiresOnlyThisRunsRuntime()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("windows-copy", HostShell.WindowsPowerShell);
        string install = Path.Combine(_root, "install"), run = Path.Combine(_root, "run-test");
        string runtime = Path.Combine(run, "runtime"), keep = Path.Combine(run, "runtime-changes");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "base.txt"), "source");
        await HostInstall.CopyAsync(host, install, runtime, TimeSpan.FromSeconds(30));
        Assert.Equal("source", File.ReadAllText(Path.Combine(runtime, "base.txt")));
        File.WriteAllText(Path.Combine(runtime, "new.txt"), "evidence");
        var result = await host.RunAsync(HostedRunScripts.WindowsRetire, new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["keep"] = keep, ["run"] = "run-test",
            ["files"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("new.txt")), ["perfile"] = "1000", ["total"] = "1000",
        }, TimeSpan.FromSeconds(30));
        Assert.True(result.Succeeded, result.Describe() + " " + result.Stderr);
        Assert.Contains("VT-RETIRED", result.Stdout);
        Assert.False(Directory.Exists(runtime));
        Assert.Equal("evidence", File.ReadAllText(Path.Combine(keep, "new.txt")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(install, "base.txt")));
        var drop = await host.RunAsync(HostedRunScripts.WindowsDropKept, new Dictionary<string, string> { ["keep"] = keep, ["run"] = "run-test" }, TimeSpan.FromSeconds(30));
        Assert.True(drop.Succeeded, drop.Describe() + " " + drop.Stderr);
        Assert.False(Directory.Exists(keep));
    }

    [Fact] public void ALaunchRefusesWhatWouldRedirectTheLoaderOrCannotRun()
    {
        Assert.Throws<ArgumentException>(() => HostServerLaunch.Create("/srv/rt", [], new Dictionary<string, string> { ["DOORSTOP_ENABLED"] = "0" }));
        Assert.Throws<ArgumentException>(() => HostServerLaunch.Create("/srv/rt", ["--doorstop-enabled", "false"]));
        Assert.Throws<ArgumentException>(() => HostServerLaunch.Create("/srv/rt", [], new Dictionary<string, string> { ["LD_PRELOAD"] = "other.so" }));
        Assert.Throws<ArgumentException>(() => HostServerLaunch.Create("/srv/a:b", []));
        Assert.Throws<ArgumentException>(() => HostServerLaunch.Create("relative/runtime", []));
        Assert.Throws<ArgumentException>(() => HostServerLaunch.Create("/srv/rt", ["two\nlines"]));
        Assert.Throws<PlatformNotSupportedException>(() => HostServerLaunch.Create(@"C:\valheim\server", []));
    }

    // ---- starting ----

    [Fact] public async Task AStartSendsTheLaunchAndReadsTheIdentity()
    {
        var host = new QueueHost();
        host.Replies.Enqueue(Reply("VT-SERVER started 4321 998877\n"));
        var launch = HostServerLaunch.Create("/srv/rt", ["-batchmode"]);
        var process = await HostServer.StartAsync(host, launch, "/srv/runs/boot-1/", TimeSpan.FromSeconds(60), ["BepInEx/LogOutput.log"], Path.Combine(_root, "boot-1"));
        Assert.Equal(4321, process.Id); Assert.Equal("998877", process.StartIdentity); Assert.Equal("/srv/runs/boot-1", process.BootDirectory);
        var run = Assert.Single(host.Runs);
        Assert.Same(HostServerScripts.Start, run.Script);
        Assert.Equal("/srv/rt", run.Variables["runtime"]); Assert.Equal("/srv/runs/boot-1", run.Variables["dir"]);
        Assert.Equal(launch.Spec(), run.Variables["spec"]); Assert.Equal("BepInEx/LogOutput.log", run.Variables["logs"]);
        Assert.Equal("50", run.Variables["seconds"]);
        Assert.False(process.HasExited);
    }

    [Fact] public async Task EachRefusalIsItsOwnErrorAndALostReplyIsUnknown()
    {
        var launch = HostServerLaunch.Create("/srv/rt", []);
        async Task<Exception> Start(HostResult reply)
        {
            var host = new QueueHost(); host.Replies.Enqueue(reply);
            return await Assert.ThrowsAnyAsync<Exception>(() => HostServer.StartAsync(host, launch, "/srv/boot", TimeSpan.FromSeconds(30)));
        }
        Assert.IsType<FileNotFoundException>(await Start(Reply("VT-SERVER missing valheim_server.x86_64 is not executable\n")));
        Assert.IsType<InvalidOperationException>(await Start(Reply("VT-SERVER exists\n")));
        Assert.IsType<PlatformNotSupportedException>(await Start(Reply("VT-SERVER unsupported this host runs Darwin\n")));
        Assert.Contains("exited at once", (await Start(Reply("VT-SERVER failed the server exited at once: boom\n"))).Message);
        var lost = Assert.IsType<HostOperationException>(await Start(new HostResult(HostOutcome.Unknown, null, "", "", TimeSpan.FromSeconds(30), true)));
        Assert.Equal(HostOutcome.Unknown, lost.Outcome);
        Assert.Contains("a server may have started", lost.Message);
        Assert.NotNull(HostedServerRun.UnknownOutcome(new InvalidOperationException("wrapped", lost)));
        Assert.Null(HostedServerRun.UnknownOutcome(new HostOperationException("failed", Reply("") with { ExitCode = 3 })));
        // A PowerShell host is refused before anything runs.
        var windows = new QueueHost(HostShell.WindowsPowerShell);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => HostServer.StartAsync(windows, launch, "/srv/boot", TimeSpan.FromSeconds(30)));
        Assert.Empty(windows.Runs);
    }

    // ---- crossplay's libraries ----

    // ldd's own format: a missing dependency is "name => not found"; the rest resolve to a path or are the loader and vDSO.
    // The three missing lines are the real 1.0.16 libparty.so's on Ubuntu 24.04 without libpulse0 and libpulse-mainloop-glib0.
    private const string LddMissingPulse = """
        	linux-vdso.so.1 (0x00007ffd5e7f2000)
        	libpulse.so.0 => not found
        	libpulse-simple.so.0 => not found
        	libpulse-mainloop-glib.so.0 => not found
        	libstdc++.so.6 => /lib/x86_64-linux-gnu/libstdc++.so.6 (0x00007f1c2a000000)
        	libc.so.6 => /lib/x86_64-linux-gnu/libc.so.6 (0x00007f1c29c00000)
        	libpulse.so.0 => not found
        	/lib64/ld-linux-x86-64.so.2 (0x00007f1c2a5f4000)
        """;

    // The check script's reply: each ldd line prefixed, then the verdict with ldd's exit code.
    private static string CheckReply(string ldd, int code) =>
        string.Concat(ldd.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => "VT-LDD " + line + "\n")) + $"VT-PARTY checked valheim_server_Data/Plugins/libparty.so {code}\n";

    [Fact] public void AMissingLibraryIsReadFromLddAndNamedWithItsPackage()
    {
        Assert.Equal(new[] { "libpulse.so.0", "libpulse-simple.so.0", "libpulse-mainloop-glib.so.0" }, CrossplayLibraries.Missing(LddMissingPulse));
        Assert.Empty(CrossplayLibraries.Missing("\tlibc.so.6 => /lib/x86_64-linux-gnu/libc.so.6 (0x1)\n\tnot found.so => /lib/not found.so (0x2)\n"));
        var error = Assert.Throws<InvalidOperationException>(() => CrossplayLibraries.Verdict("box", CheckReply(LddMissingPulse, 1)));
        Assert.Contains("Crossplay cannot start on box", error.Message);
        Assert.Contains("libpulse.so.0 (package libpulse0), libpulse-simple.so.0 (package libpulse0), libpulse-mainloop-glib.so.0 (package libpulse-mainloop-glib0) are missing", error.Message);
        Assert.DoesNotContain("also provide", error.Message);
        Assert.Contains("apt-get install libpulse0 libpulse-mainloop-glib0", error.Message);
        Assert.Contains("Nothing was started", error.Message);
        // A library with no known package is named as it is.
        string unknown = CrossplayLibraries.Refusal("box", "valheim_server_Data/Plugins/libparty.so", ["libq.so.6"]);
        Assert.Contains("libq.so.6 is missing", unknown);
        Assert.DoesNotContain("apt-get", unknown);
    }

    [Fact] public void EachCheckVerdictIsItsOwnError()
    {
        Assert.Equal("valheim_server_Data/Plugins/libparty.so", CrossplayLibraries.Verdict("box",
            "VT-LDD 	libc.so.6 => /lib/x86_64-linux-gnu/libc.so.6 (0x1)\nVT-PARTY checked valheim_server_Data/Plugins/libparty.so 0\n"));
        Assert.Contains("has no valheim_server_Data/Plugins/libparty.so",
            Assert.Throws<FileNotFoundException>(() => CrossplayLibraries.Verdict("box", "VT-PARTY absent\n")).Message);
        Assert.Contains("no ldd", Assert.Throws<PlatformNotSupportedException>(() => CrossplayLibraries.Verdict("box", "VT-PARTY noldd\n")).Message);
        Assert.Throws<PlatformNotSupportedException>(() => CrossplayLibraries.Verdict("box", "VT-PARTY unsupported Darwin\n"));
        // ldd failing without a missing library (another architecture, a truncated copy) is no proof either.
        Assert.Contains("not a dynamic executable", Assert.Throws<InvalidOperationException>(() => CrossplayLibraries.Verdict("box",
            "VT-LDD 	not a dynamic executable\nVT-PARTY checked valheim_server_Data/Plugins/libparty.so 1\n")).Message);
        Assert.Null(CrossplayLibraries.Verdict("box", "something else\n"));
        Assert.Null(CrossplayLibraries.Verdict("box", "VT-PARTY checked lib without-a-code\n"));
    }

    [Fact] public async Task TheCheckRunsOnABashHostWithTheRuntimeAndCandidates()
    {
        var host = new QueueHost();
        host.Replies.Enqueue(Reply("VT-LDD 	libc.so.6 => /lib/libc.so.6 (0x1)\nVT-PARTY checked valheim_Data/Plugins/libparty.so 0\n"));
        Assert.Equal("valheim_Data/Plugins/libparty.so", await CrossplayLibraries.RequireAsync(host, "/srv/rt/", TimeSpan.FromSeconds(30)));
        var run = Assert.Single(host.Runs);
        Assert.Same(CrossplayLibraryScripts.Check, run.Script);
        Assert.Equal("/srv/rt", run.Variables["runtime"]);
        Assert.Equal(string.Join('\n', CrossplayLibraries.PartyLibraries), run.Variables["libraries"]);
        var unexpected = new QueueHost(); unexpected.Replies.Enqueue(Reply("hello\n"));
        await Assert.ThrowsAsync<HostOperationException>(() => CrossplayLibraries.RequireAsync(unexpected, "/srv/rt", TimeSpan.FromSeconds(30)));
        var windows = new QueueHost(HostShell.Pwsh);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => CrossplayLibraries.RequireAsync(windows, "/srv/rt", TimeSpan.FromSeconds(30)));
        Assert.Empty(windows.Runs);
    }

    [Fact] public async Task ACrossplayStartChecksTheLibrariesFirstAndRefusesWithTheirNames()
    {
        // Only a launch with -crossplay (in any case, as the game reads it) asks the start script to check.
        Assert.False(HostServerLaunch.Create("/srv/rt", ["-batchmode"]).Crossplay);
        var launch = HostServerLaunch.Create("/srv/rt", ["-batchmode", "-CrossPlay"]);
        Assert.True(launch.Crossplay);
        var host = new QueueHost();
        host.Replies.Enqueue(Reply(CheckReply(LddMissingPulse, 1) + "VT-SERVER libraries\n"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HostServer.StartAsync(host, launch, "/srv/boot", TimeSpan.FromSeconds(30)));
        Assert.Contains("apt-get install libpulse0 libpulse-mainloop-glib0", error.Message);
        var run = Assert.Single(host.Runs);
        Assert.Equal("1", run.Variables["crossplay"]);
        Assert.Equal(string.Join('\n', CrossplayLibraries.PartyLibraries), run.Variables["libraries"]);
        var plain = new QueueHost(); plain.Replies.Enqueue(Reply("VT-SERVER started 12 34\n"));
        await HostServer.StartAsync(plain, HostServerLaunch.Create("/srv/rt", ["-batchmode"]), "/srv/boot", TimeSpan.FromSeconds(30));
        Assert.Equal("", Assert.Single(plain.Runs).Variables["crossplay"]);
        var absent = new QueueHost(); absent.Replies.Enqueue(Reply("VT-PARTY absent\nVT-SERVER libraries\n"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => HostServer.StartAsync(absent, launch, "/srv/boot", TimeSpan.FromSeconds(30)));
    }

    // ---- stopping ----

    [Fact] public async Task AStopKillsOnlyThatIdentityThenKeepsAndFetchesTheLogsOnce()
    {
        var host = new QueueHost();
        host.Replies.Enqueue(Reply("VT-SERVER started 4321 998877\n"));
        var process = await HostServer.StartAsync(host, HostServerLaunch.Create("/srv/rt", []), "/srv/runs/boot-1", TimeSpan.FromSeconds(60), ["BepInEx/LogOutput.log", "toolkit-unity.log"], Path.Combine(_root, "boot-1"));
        host.Replies.Enqueue(Reply("VT-STOP stopped\n")); host.Replies.Enqueue(Reply("VT-KEPT\n"));
        Assert.Equal(HostServerStop.Stopped, await process.StopAsync(TimeSpan.FromSeconds(15)));
        Assert.True(process.HasExited);
        var stop = host.Runs[1];
        Assert.Same(InteractiveScripts.LinuxStop, stop.Script);
        Assert.Equal("4321", stop.Variables["game"]); Assert.Equal("998877", stop.Variables["start"]); Assert.Equal("15", stop.Variables["seconds"]);
        Assert.Same(HostServerScripts.Keep, host.Runs[2].Script);
        Assert.Equal("BepInEx/LogOutput.log\ntoolkit-unity.log", host.Runs[2].Variables["logs"]);
        Assert.Equal("/srv/runs/boot-1 -> " + Path.Combine(_root, "boot-1"), Assert.Single(host.Fetched));
        // Done: a second stop or a dispose runs nothing more.
        Assert.Equal(HostServerStop.AlreadyGone, await process.StopAsync(TimeSpan.FromSeconds(15)));
        process.Dispose();
        Assert.Equal(3, host.Runs.Count);
    }

    [Fact] public async Task ACleanStopAsksWithSigintFirstAndReportsWhetherItQuit()
    {
        var host = new QueueHost();
        host.Replies.Enqueue(Reply("VT-SERVER started 4321 998877\n"));
        var process = await HostServer.StartAsync(host, HostServerLaunch.Create("/srv/rt", []), "/srv/runs/boot-1", TimeSpan.FromSeconds(60));
        host.Replies.Enqueue(Reply("VT-STOP quit\n")); host.Replies.Enqueue(Reply("VT-KEPT\n"));
        var stop = process.StopCleanly(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15));
        Assert.Equal((StopOutcome.Clean, "SIGINT"), (stop.Outcome, stop.Request));
        var sent = host.Runs[1];
        Assert.Same(InteractiveScripts.LinuxStop, sent.Script);
        Assert.Equal(("120", "INT", "15"), (sent.Variables["quit"], sent.Variables["signal"], sent.Variables["seconds"]));
        // One that did not quit in time was killed; a kill-only stop sends no quit request.
        host.Replies.Enqueue(Reply("VT-SERVER started 5 6\n"));
        var stubborn = await HostServer.StartAsync(host, HostServerLaunch.Create("/srv/rt", []), "/srv/runs/boot-2", TimeSpan.FromSeconds(60));
        host.Replies.Enqueue(Reply("VT-STOP stopped\n")); host.Replies.Enqueue(Reply("VT-KEPT\n"));
        var killed = stubborn.StopCleanly(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
        Assert.Equal((StopOutcome.Killed, "SIGINT; no exit within 2.0 s"), (killed.Outcome, killed.Request));
        host.Replies.Enqueue(Reply("VT-SERVER started 7 8\n"));
        var plain = await HostServer.StartAsync(host, HostServerLaunch.Create("/srv/rt", []), "/srv/runs/boot-3", TimeSpan.FromSeconds(60));
        host.Replies.Enqueue(Reply("VT-STOP stopped\n")); host.Replies.Enqueue(Reply("VT-KEPT\n"));
        Assert.Equal(HostServerStop.Stopped, await plain.StopAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("0", host.Runs[^2].Variables["quit"]);
    }

    [Fact] public async Task AReusedProcessIdIsGoneAndAFailedFetchIsRetriedWithoutKillingAgain()
    {
        var host = new QueueHost();
        host.Replies.Enqueue(Reply("VT-SERVER started 4321 998877\n"));
        var process = await HostServer.StartAsync(host, HostServerLaunch.Create("/srv/rt", []), "/srv/runs/boot-1", TimeSpan.FromSeconds(60));
        host.Replies.Enqueue(Reply("VT-STOP gone\n")); host.Replies.Enqueue(new HostResult(HostOutcome.TransportFailed, null, "", "ssh: lost", TimeSpan.Zero, false));
        var failed = await Assert.ThrowsAsync<HostOperationException>(() => process.StopAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(HostOutcome.TransportFailed, failed.Outcome);
        Assert.True(process.HasExited);
        host.Replies.Enqueue(Reply("VT-KEPT\n"));
        Assert.Equal(HostServerStop.AlreadyGone, await process.StopAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(1, host.Runs.Count(run => ReferenceEquals(run.Script, InteractiveScripts.LinuxStop)));
        // A process still there after the kill is a failure, not a stop.
        host.Replies.Enqueue(Reply("VT-SERVER started 5 6\n"));
        var stubborn = await HostServer.StartAsync(host, HostServerLaunch.Create("/srv/rt", []), "/srv/runs/boot-2", TimeSpan.FromSeconds(60));
        host.Replies.Enqueue(Reply("VT-STOP running\n"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stubborn.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.False(stubborn.HasExited);
    }

    [Fact] public async Task TheExitCodeComesFromTheRecorder()
    {
        var host = new QueueHost();
        host.Replies.Enqueue(Reply("VT-SERVER started 4321 998877\n"));
        var process = await HostServer.StartAsync(host, HostServerLaunch.Create("/srv/rt", []), "/srv/runs/boot-1", TimeSpan.FromSeconds(60));
        host.Replies.Enqueue(Reply("VT-WAIT running\n")); host.Replies.Enqueue(Reply("VT-WAIT exited 137\n"));
        Assert.Equal(137, await process.WaitForExitAsync(CancellationToken.None));
        Assert.True(process.HasExited);
        Assert.Equal("/srv/runs/boot-1", host.Runs[1].Variables["dir"]);
    }

    // ---- listings, copies and ports ----

    [Fact] public void AListingReadsSha256sumAndBase64LinesAndRefusesLinks()
    {
        string a = new('a', 64), b = new('b', 64);
        var listing = HostInstall.ReadListing("box", HostShellKind.Bash, "/srv/rt", Reply(
            $"{a}  ./BepInEx/core/BepInEx.dll\n\\{b}  ./odd\\\\name\\nwith break.txt\n" +
            $"VT-FILE {a} {Convert.ToBase64String(Encoding.UTF8.GetBytes("with space/é.dll"))}\n" +
            $"VT-PATCHER {Convert.ToBase64String(Encoding.UTF8.GetBytes("Leftover.dll"))}\nVT-EXEC valheim_server.x86_64\nVT-LIST done\n"));
        Assert.Equal(a, listing.Files["BepInEx/core/BepInEx.dll"]);
        Assert.Equal(b, listing.Files["odd\\name\nwith break.txt".Replace('\\', '/')]);
        Assert.Equal(a, listing.Files["with space/é.dll"]);
        Assert.Equal(new[] { "Leftover.dll" }, listing.Patchers);
        Assert.Equal(new[] { "valheim_server.x86_64" }, listing.Executables);
        Assert.Throws<IOException>(() => HostInstall.ReadListing("box", HostShellKind.Bash, "/srv/rt", Reply("VT-LIST links\n./BepInEx/plugins/link\n")));
        Assert.Throws<DirectoryNotFoundException>(() => HostInstall.ReadListing("box", HostShellKind.Bash, "/srv/rt", Reply("VT-LIST missing\n")));
        // A reply cut short never passes as a complete listing.
        Assert.Throws<HostOperationException>(() => HostInstall.ReadListing("box", HostShellKind.Bash, "/srv/rt", Reply($"{a}  ./x.dll\n")));
    }

    private HostListing ListingOf(string directory, HostShellKind shell = HostShellKind.Bash) =>
        HostInstall.ReadListing("box", shell, "/srv/rt", Reply(string.Concat(WorldFixture.Manifest(directory).Select(pair => $"{pair.Value}  ./{pair.Key.Replace('\\', '/')}\n")) +
            (Directory.Exists(Path.Combine(directory, "BepInEx", "patchers")) ? string.Concat(Directory.EnumerateFileSystemEntries(Path.Combine(directory, "BepInEx", "patchers"))
                .Select(entry => "VT-PATCHER " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFileName(entry))) + "\n")) : "") + "VT-LIST done\n"));

    [Fact] public void PinsFromAHostListingAreTheLocalInstallPins()
    {
        string install = Path.Combine(_root, "install");
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, "valheim_server_Data", "Managed", "assembly_guiutils.dll"), "gui");
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "patchers", "Hooks"));
        File.WriteAllText(Path.Combine(install, "BepInEx", "patchers", "Hooks", "Hook.dll"), "hook");
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", ".DS_Store"), "finder");
        var local = InstallPins.Of(install);
        var listing = ListingOf(install);
        var found = HostInstall.Pins(listing);
        Assert.Equal(local.Game, found.Game); Assert.Equal(local.BepInExCore, found.BepInExCore); Assert.Equal(local.Patchers, found.Patchers);
        Assert.Equal(local.Game, HostInstall.CheckPins(local, listing, "runtime").Game);
        HostInstall.RequirePatchers(listing, ["Hooks"], "runtime");
        Assert.Contains("Hooks", Assert.Throws<InvalidOperationException>(() => HostInstall.RequirePatchers(listing, [], "runtime")).Message);
        // Another game build on the host is named, with the value found.
        File.WriteAllText(Path.Combine(install, "valheim_server_Data", "Managed", InstallPins.GameAssemblyName), "game build 2");
        var changed = Assert.Throws<InvalidOperationException>(() => HostInstall.CheckPins(local, ListingOf(install), "runtime"));
        Assert.Contains("the game build differs (valheim_server_Data/Managed/assembly_*.dll is " + InstallPins.Of(install).Game, changed.Message);
        Assert.Contains("on box", changed.Message);
    }

    [Fact] public void HostPinsRefuseANestedCoreBeforeAnyRuntimeIsCopied()
    {
        string install = Path.Combine(_root, "nested-core");
        FakeInstalls.Server(install);
        string nested = Path.Combine(install, "BepInEx", "core", "core");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "0Harmony20.dll"), "duplicate");
        var error = Assert.Throws<InvalidOperationException>(() => HostInstall.Pins(ListingOf(install)));
        Assert.Contains("nested BepInEx/core/core", error.Message);
    }

    [Fact] public void AHostCopyMustMatchTheManifestExactly()
    {
        string runtime = Path.Combine(_root, "runtime");
        FakeInstalls.Server(runtime);
        var manifest = WorldFixture.Manifest(runtime);
        HostInstall.RequireSame(manifest, ListingOf(runtime), "runtime copy");
        File.WriteAllText(Path.Combine(runtime, "extra.dll"), "extra");
        Assert.Contains("not in the manifest: extra.dll", Assert.Throws<InvalidOperationException>(() => HostInstall.RequireSame(manifest, ListingOf(runtime), "runtime copy")).Message);
        HostInstall.RequireSame(manifest, ListingOf(runtime), "runtime copy", ["extra.dll"]);
        File.Delete(Path.Combine(runtime, "extra.dll"));
        File.Delete(Path.Combine(runtime, "BepInEx", "core", "BepInEx.dll"));
        Assert.Contains("missing: BepInEx/core/BepInEx.dll", Assert.Throws<InvalidOperationException>(() => HostInstall.RequireSame(manifest, ListingOf(runtime), "runtime copy")).Message);
    }

    [Fact] public async Task CopyAndPortRepliesAreRead()
    {
        var host = new QueueHost();
        host.Replies.Enqueue(Reply("VT-COPY copied\n"));
        await HostInstall.CopyAsync(host, "/opt/server", "/srv/runs/r/runtime", TimeSpan.FromSeconds(30));
        Assert.Equal("/opt/server", host.Runs[0].Variables["source"]); Assert.Equal("/srv/runs/r/runtime", host.Runs[0].Variables["dest"]);
        host.Replies.Enqueue(Reply("VT-COPY exists\n"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostInstall.CopyAsync(host, "/opt/server", "/srv/runs/r/runtime", TimeSpan.FromSeconds(30)));
        host.Replies.Enqueue(Reply("VT-COPY missing\n"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => HostInstall.CopyAsync(host, "/opt/none", "/srv/runs/r2/runtime", TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<ArgumentException>(() => HostInstall.CopyAsync(host, "opt/server", "/srv/x", TimeSpan.FromSeconds(30)));
        host.Replies.Enqueue(Reply("VT-PORT free\n"));
        await HostInstall.RequirePortFreeAsync(host, 5577, TimeSpan.FromSeconds(30));
        Assert.Equal("5577", host.Runs[^1].Variables["port"]);
        host.Replies.Enqueue(Reply("VT-PORT busy\n"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostInstall.RequirePortFreeAsync(host, 5577, TimeSpan.FromSeconds(30)));
        var windows = new QueueHost(HostShell.Pwsh);
        windows.Replies.Enqueue(Reply("VT-COPY copied\n"));
        await HostInstall.CopyAsync(windows, @"C:\server", @"C:\runs\r\runtime", TimeSpan.FromSeconds(30));
        Assert.Same(HostInstallScripts.PowerShellCopy, windows.Runs[0].Script);
    }

    [Fact] public void HostPathsJoinInTheHostsOwnStyle()
    {
        Assert.Equal("/srv/runs/run-1/world", HostInstall.Join("/srv/runs/", "run-1", "world"));
        Assert.Equal(@"C:\vt\runs\run-1\client-1", HostInstall.Join(@"C:\vt\runs", "run-1", "client-1"));
        Assert.Equal(@"C:\Games\Valheim\BepInEx\LogOutput.log", HostInstall.Join(@"C:\Games\Valheim", "BepInEx/LogOutput.log"));
        Assert.Equal("/x", HostInstall.Join("/", "x"));
    }
}
