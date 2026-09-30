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
        await Assert.ThrowsAsync<NotSupportedException>(() => HostInstall.CopyAsync(new QueueHost(HostShell.Pwsh), "/a", "/b", TimeSpan.FromSeconds(30)));
    }

    [Fact] public void HostPathsJoinInTheHostsOwnStyle()
    {
        Assert.Equal("/srv/runs/run-1/world", HostInstall.Join("/srv/runs/", "run-1", "world"));
        Assert.Equal(@"C:\vt\runs\run-1\client-1", HostInstall.Join(@"C:\vt\runs", "run-1", "client-1"));
        Assert.Equal(@"C:\Games\Valheim\BepInEx\LogOutput.log", HostInstall.Join(@"C:\Games\Valheim", "BepInEx/LogOutput.log"));
        Assert.Equal("/x", HostInstall.Join("/", "x"));
    }
}
