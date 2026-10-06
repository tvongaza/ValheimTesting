using System.Net;
using System.Net.Sockets;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// The server-on-a-host path through a real shell, without a game: an install shipped to the host, copied and verified there,
// a stand-in server (a script with the server's name that writes ValheimCLI's listening line and keeps running) started,
// awaited in its log, stopped by identity beside a bystander, and its evidence fetched back. Locally on Linux, and over a real
// ssh to localhost and into a real container in the game-hosts CI job. Everything lives under one new root, removed at the end.
internal static class HostServerChecks
{
    private static readonly TimeSpan Generous = GameHostChecks.Generous;

    // A server install as far as the launch and the pins need one: the game's Managed folder, BepInEx core, Doorstop's library
    // (a stand-in: the loader warns and carries on) and the server executable, here a script. Line endings stay LF.
    private static void WriteInstall(string directory)
    {
        FakeInstalls.Server(directory);
        File.WriteAllText(Path.Combine(directory, "BepInEx", "core", "BepInEx.Preloader.dll"), "preloader");
        Directory.CreateDirectory(Path.Combine(directory, "doorstop_libs"));
        File.WriteAllText(Path.Combine(directory, "doorstop_libs", "libdoorstop_x64.so"), "not a library");
        string server = Path.Combine(directory, GameLaunch.ServerLinuxExecutable);
        File.WriteAllText(server, string.Join('\n',
            "#!/bin/bash",
            "mkdir -p BepInEx",
            "printf 'args=%s\\n' \"$*\"",
            "env | grep -E '^(VT_TEST_TOKEN|SteamAppId|DOORSTOP_ENABLED|DOORSTOP_TARGET_ASSEMBLY)=' | sort",
            // A server that ignores the quit request, for the kill fallback; set before the listening line the check waits for.
            "if [ -n \"${VT_IGNORE_INT:-}\" ]; then trap '' INT; fi",
            "printf '[Info   :valheimCLI] Command server listening on 127.0.0.1:5577 this boot\\n' >> BepInEx/LogOutput.log",
            "exec sleep 300", ""));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, File.GetUnixFileMode(server) | UnixFileMode.UserExecute);
    }

    public static Task AServerRunsFromAVerifiedCopyAndStopsByIdentity(IGameHost host, string parent) => GameHostChecks.WithRootAsync(host, parent, async root =>
    {
        using var source = new TempDirectory();
        WriteInstall(source.Path);
        await host.ShipFilesAsync(source.Path, root + "/install", Generous);
        string runtime = root + "/run/runtime";
        await HostInstall.CopyAsync(host, root + "/install", runtime, Generous);
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostInstall.CopyAsync(host, root + "/install", runtime, Generous));

        // The copy on the host is every file of the source, and its pins are the source's.
        var listing = await HostInstall.ListAsync(host, runtime, Generous);
        HostInstall.RequireSame(WorldFixture.Manifest(source.Path), listing, "runtime copy", ["SOURCE.txt"]);
        Assert.Contains(GameLaunch.ServerLinuxExecutable, listing.Executables);
        var pins = InstallPins.Of(source.Path);
        Assert.Equal(pins.Game, HostInstall.CheckPins(pins, listing, "runtime").Game);
        Assert.Equal(ServerPlatform.Linux, HostInstall.DetectServer(listing));
        var partial = await HostInstall.ListAsync(host, runtime, Generous, HostInstall.PinPaths);
        Assert.DoesNotContain(GameLaunch.ServerLinuxExecutable, partial.Files.Keys);
        Assert.Equal(pins.Loader, HostInstall.Pins(partial).Loader);

        // A listener here is a listener there: the host shares this machine's network.
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => HostInstall.RequirePortFreeAsync(host, port, Generous)); }
        finally { listener.Stop(); }
        await HostInstall.RequirePortFreeAsync(host, port, Generous);

        // An earlier boot's log line must not satisfy this boot's wait from offset 0.
        await GameHostChecks.AppendAsync(host, runtime + "/BepInEx/LogOutput.log", "[Info   :valheimCLI] Command server listening on 127.0.0.1:5577 earlier boot\n");
        string bystander = (await host.RunAsync("setsid sleep 300 > /dev/null 2>&1 < /dev/null & echo $!", null, Generous)).EnsureSuccess("Starting a bystander").Stdout.Trim();
        try
        {
            var launch = GameLaunch.ForServer(runtime, ["-batchmode", "-name", "it's a test"], new Dictionary<string, string> { ["VT_TEST_TOKEN"] = "token-123" }, hostPlatform: ServerPlatform.Linux);
            using var evidence = new TempDirectory();
            string local = Path.Combine(evidence.Path, "boot-1");
            var process = await HostServer.StartAsync(host, launch, root + "/run/boot-1", Generous, ["BepInEx/LogOutput.log", "toolkit-unity.log"], local);
            try
            {
                string line = (await host.WaitForLogAsync(runtime + "/BepInEx/LogOutput.log", 0, StartupEvents.CliListening, StartupEvents.StartupFailures, Generous)).EnsureMatched();
                Assert.EndsWith("this boot", line);
                // #257: the boot's pid file names the server by ID and start identity, as an interrupted run's recovery reads it.
                var pidFile = Assert.Single(await RunJournalStatus.ReadPidFilesAsync(host, [root + "/run/boot-1"], Generous, default));
                Assert.Equal((RunJournalStatus.PidFileState.Found, process.Id, process.StartIdentity), (pidFile.State, pidFile.Pid, pidFile.StartIdentity));
                await Assert.ThrowsAsync<InvalidOperationException>(() => HostServer.StartAsync(host, launch, root + "/run/boot-1", Generous));
            }
            catch
            {
                // Stop it anyway, and report what failed first.
                try { await process.StopAsync(TimeSpan.FromSeconds(15)); } catch { }
                throw;
            }
            Assert.Equal(HostServerStop.Stopped, await process.StopAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(137, await process.WaitForExitAsync(CancellationToken.None));
            Assert.Equal(HostServerStop.AlreadyGone, await process.StopAsync(TimeSpan.FromSeconds(15)));
            // The bystander is untouched.
            Assert.True((await host.RunAsync("kill -0 \"$p\"", new Dictionary<string, string> { ["p"] = bystander }, Generous)).Succeeded, "The bystander was stopped.");

            // A clean stop sends SIGINT and the stand-in exits by itself (130: ended by SIGINT, not killed); one that ignores
            // SIGINT is killed once the wait is over (137). The bystander is untouched either way.
            async Task<ProcessStop> StopAfterListening(GameLaunch boot, string name, TimeSpan quit)
            {
                var started = await HostServer.StartAsync(host, boot, root + "/run/" + name, Generous, ["BepInEx/LogOutput.log"], Path.Combine(evidence.Path, name));
                (await host.WaitForLogAsync(runtime + "/BepInEx/LogOutput.log", 0, StartupEvents.CliListening, StartupEvents.StartupFailures, Generous)).EnsureMatched();
                var stop = started.StopCleanly(quit, TimeSpan.FromSeconds(15));
                Assert.True(started.HasExited);
                return stop with { ExitCode = await started.WaitForExitAsync(CancellationToken.None) };
            }
            var clean = await StopAfterListening(launch, "boot-2", TimeSpan.FromSeconds(15));
            Assert.Equal((StopOutcome.Clean, 130, "SIGINT"), (clean.Outcome, clean.ExitCode, clean.Request));
            var ignoring = GameLaunch.ForServer(runtime, ["-batchmode"], new Dictionary<string, string> { ["VT_TEST_TOKEN"] = "token-456", ["VT_IGNORE_INT"] = "1" }, hostPlatform: ServerPlatform.Linux);
            var killed = await StopAfterListening(ignoring, "boot-3", TimeSpan.FromSeconds(1));
            Assert.Equal((StopOutcome.Killed, 137), (killed.Outcome, killed.ExitCode));
            Assert.StartsWith("SIGINT; no exit within 1.0 s", killed.Request);
            Assert.True((await host.RunAsync("kill -0 \"$p\"", new Dictionary<string, string> { ["p"] = bystander }, Generous)).Succeeded, "The bystander was stopped.");

            // The evidence: this boot's log, the earlier one moved aside, an absent Unity log, and the server's output with the
            // arguments and environment the launch gave it.
            Assert.EndsWith("this boot", File.ReadAllText(Path.Combine(local, "game-0.log")).Trim());
            Assert.Contains("earlier boot", File.ReadAllText(Path.Combine(local, "previous-0.log")));
            Assert.True(File.Exists(Path.Combine(local, "game-1.log.absent")));
            string stdout = File.ReadAllText(Path.Combine(local, "stdout.log"));
            Assert.Contains("args=-batchmode -name it's a test", stdout);
            Assert.Contains("VT_TEST_TOKEN=token-123", stdout);
            Assert.Contains("SteamAppId=" + GameLaunch.SteamAppId, stdout);
            Assert.Contains("DOORSTOP_ENABLED=1", stdout);
            Assert.Contains("DOORSTOP_TARGET_ASSEMBLY=" + runtime + "/BepInEx/core/BepInEx.Preloader.dll", stdout);
            // The server's working directory was the runtime: its log is gone from there, into the boot's evidence.
            Assert.Equal(0, await host.LogOffsetAsync(runtime + "/BepInEx/LogOutput.log", Generous));
        }
        finally { await host.RunAsync("kill \"$p\" 2> /dev/null; true", new Dictionary<string, string> { ["p"] = bystander }, Generous); }
    });

    // The host's own loader decides whether crossplay can start. libparty.so here is a copy of the host's `true` (ldd reads an
    // executable as it reads a library): as it is, every dependency resolves; with libc.so.6 renamed in the copy to the
    // same-length libq.so.6, ldd reports that as not found. A crossplay start refuses before anything runs, a missing
    // libparty.so is refused as absent, and the same runtime without -crossplay is never checked.
    public static Task ACrossplayStartNeedsLibpartyToLoadOnTheHost(IGameHost host, string parent) => GameHostChecks.WithRootAsync(host, parent, async root =>
    {
        using var source = new TempDirectory();
        WriteInstall(source.Path);
        string runtime = root + "/runtime";
        await host.ShipFilesAsync(source.Path, runtime, Generous);
        var crossplay = GameLaunch.ForServer(runtime, ["-batchmode", "-crossplay"], hostPlatform: ServerPlatform.Linux);
        await Assert.ThrowsAsync<FileNotFoundException>(() => CrossplayLibraries.RequireAsync(host, runtime, Generous));
        await Assert.ThrowsAsync<FileNotFoundException>(() => HostServer.StartAsync(host, crossplay, root + "/boot-absent", Generous));

        // A client plugin in a mixed install must not stand in for the dedicated server's missing plugin.
        string clientPlugin = runtime + "/valheim_Data/Plugins";
        (await host.RunAsync("set -e; mkdir -p \"$plugins\"; cp \"$(type -P true)\" \"$plugins/libparty.so\"",
            new Dictionary<string, string> { ["plugins"] = clientPlugin }, Generous)).EnsureSuccess("Writing a client-only libparty.so");
        await Assert.ThrowsAsync<FileNotFoundException>(() => CrossplayLibraries.RequireAsync(host, runtime, Generous));
        await Assert.ThrowsAsync<FileNotFoundException>(() => HostServer.StartAsync(host, crossplay, root + "/boot-client-only", Generous));

        string plugin = runtime + "/valheim_server_Data/Plugins";
        async Task Party(string script) => (await host.RunAsync("set -e; mkdir -p \"$plugins\"; " + script, new Dictionary<string, string> { ["plugins"] = plugin }, Generous))
            .EnsureSuccess("Writing a stand-in libparty.so");
        await Party("cp \"$(type -P true)\" \"$plugins/libparty.so\"; ldd \"$plugins/libparty.so\" | grep -q 'libc\\.so\\.6 => '");
        Assert.Equal("valheim_server_Data/Plugins/libparty.so", await CrossplayLibraries.RequireAsync(host, runtime, Generous));

        await Party("LC_ALL=C sed 's/libc\\.so\\.6/libq.so.6/g' \"$(type -P true)\" > \"$plugins/libparty.so\"");
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => CrossplayLibraries.RequireAsync(host, runtime, Generous));
        Assert.Contains("because libq.so.6 is missing", refused.Message);
        var start = await Assert.ThrowsAsync<InvalidOperationException>(() => HostServer.StartAsync(host, crossplay, root + "/boot-refused", Generous));
        Assert.Contains("because libq.so.6 is missing", start.Message);
        Assert.Contains("Nothing was started", start.Message);
        Assert.False((await host.RunAsync("[ -e \"$d\" ]", new Dictionary<string, string> { ["d"] = root + "/boot-refused" }, Generous)).Succeeded, "A refused start created its boot directory.");
    });

    public static async Task AListingMatchesTheLocalManifestAndPins(IGameHost host)
    {
        using var install = new TempDirectory();
        FakeInstalls.Client(install.Path);
        Directory.CreateDirectory(Path.Combine(install.Path, "BepInEx", "patchers", "Hooks"));
        File.WriteAllText(Path.Combine(install.Path, "BepInEx", "patchers", "Hooks", "Hook.dll"), "hook");
        File.WriteAllText(Path.Combine(install.Path, "with space é.txt"), "name");
        // The loader's root files: the pins' listing names them as files, beside its folders.
        File.WriteAllText(Path.Combine(install.Path, "winhttp.dll"), "MZ target_assembly");
        File.WriteAllText(Path.Combine(install.Path, "doorstop_config.ini"), "[General]\nenabled=true\n");
        Directory.CreateDirectory(Path.Combine(install.Path, "doorstop_libs"));
        File.WriteAllText(Path.Combine(install.Path, "doorstop_libs", "libdoorstop_x64.so"), "so");
        {
            var listing = await HostInstall.ListAsync(host, install.Path, Generous);
            HostInstall.RequireSame(WorldFixture.Manifest(install.Path), listing, "install");
            var pins = InstallPins.Of(install.Path);
            var found = HostInstall.Pins(listing);
            Assert.Equal((pins.Game, pins.Loader, pins.Patchers), (found.Game, found.Loader, found.Patchers));
            var partial = await HostInstall.ListAsync(host, install.Path, Generous, HostInstall.PinPaths);
            Assert.DoesNotContain("with space é.txt", partial.Files.Keys);
            Assert.Contains("winhttp.dll", partial.Files.Keys);
            Assert.Equal((pins.Game, pins.Loader, pins.Patchers), (HostInstall.Pins(partial).Game, HostInstall.Pins(partial).Loader, HostInstall.Pins(partial).Patchers));
            // A named loader file that is a link is refused, as a link anywhere in a whole listing is, never left out.
            if (!OperatingSystem.IsWindows())
            {
                File.Move(Path.Combine(install.Path, "winhttp.dll"), Path.Combine(install.Path, "real-winhttp.dll"));
                File.CreateSymbolicLink(Path.Combine(install.Path, "winhttp.dll"), Path.Combine(install.Path, "real-winhttp.dll"));
                var linked = await Assert.ThrowsAsync<IOException>(() => HostInstall.ListAsync(host, install.Path, Generous, HostInstall.PinPaths));
                Assert.Contains("winhttp.dll", linked.Message);
                // A named folder that is a link is refused too, never listed as empty (the local pins refuse it alike).
                File.Delete(Path.Combine(install.Path, "winhttp.dll"));
                File.Move(Path.Combine(install.Path, "real-winhttp.dll"), Path.Combine(install.Path, "winhttp.dll"));
                Directory.Move(Path.Combine(install.Path, "doorstop_libs"), Path.Combine(install.Path, "real-libs"));
                Directory.CreateSymbolicLink(Path.Combine(install.Path, "doorstop_libs"), Path.Combine(install.Path, "real-libs"));
                var linkedFolder = await Assert.ThrowsAsync<IOException>(() => HostInstall.ListAsync(host, install.Path, Generous, HostInstall.PinPaths));
                Assert.Contains("doorstop_libs", linkedFolder.Message);
            }
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => HostInstall.ListAsync(host, Path.Combine(install.Path, "missing"), Generous));
        }
    }
}

public class LocalHostServerTests
{
    // The start, stop and port check read Linux's /proc; the listing runs in every local shell.
    [Fact] public Task AServerRunsFromAVerifiedCopyAndStopsByIdentity() =>
        OperatingSystem.IsLinux() ? HostServerChecks.AServerRunsFromAVerifiedCopyAndStopsByIdentity(new LocalGameHost("local-bash", HostShell.Bash), Path.GetTempPath()) : Task.CompletedTask;

    [Fact] public Task ACrossplayStartNeedsLibpartyToLoadOnTheHost() =>
        OperatingSystem.IsLinux() ? HostServerChecks.ACrossplayStartNeedsLibpartyToLoadOnTheHost(new LocalGameHost("local-bash", HostShell.Bash), Path.GetTempPath()) : Task.CompletedTask;

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public Task AListingMatchesTheLocalManifestAndPins(string shell) => HostServerChecks.AListingMatchesTheLocalManifestAndPins(new LocalGameHost("local-" + shell, HostShell.Parse(shell)));

    // A macOS client host (a campaign's local Mac client) has no /proc: bash reads netstat's listeners there instead (run A, #258),
    // for a loopback, a wildcard and a dual-stack IPv6 listener alike. Runs on the macOS CI leg; elsewhere it has nothing to check.
    [Fact] public async Task AMacBashHostTellsABusyPortFromAFreeOne()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var host = new LocalGameHost("local-bash", HostShell.Bash);
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.Any, IPAddress.IPv6Any })
        {
            var listener = new TcpListener(address, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try { await Assert.ThrowsAsync<InvalidOperationException>(() => HostInstall.RequirePortFreeAsync(host, port, GameHostChecks.Generous)); }
            finally { listener.Stop(); }
            await HostInstall.RequirePortFreeAsync(host, port, GameHostChecks.Generous);
        }
    }

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task APowerShellHostTellsABusyPortFromAFreeOne(string shell)
    {
        // Bash reads /proc/net/tcp on Linux and netstat on macOS (both above); a PowerShell host is a Windows host's.
        if (shell == "bash" || !OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("local-" + shell, HostShell.Parse(shell));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => HostInstall.RequirePortFreeAsync(host, port, GameHostChecks.Generous)); }
        finally { listener.Stop(); }
        await HostInstall.RequirePortFreeAsync(host, port, GameHostChecks.Generous);
    }
}

[Trait("Category", "GameHosts")]
public class SshHostServerIntegrationTests
{
    private static IGameHost Host(string shell) => new SshGameHost("ssh-" + shell, Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION")!, HostShell.Parse(shell),
        int.TryParse(Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_PORT"), out int port) ? port : 0,
        (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_OPTIONS") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    public static TheoryData<string> Shells => new() { "bash" };

    [SshTheory, MemberData(nameof(Shells))] public Task AServerRunsFromAVerifiedCopyAndStopsByIdentity(string shell) =>
        HostServerChecks.AServerRunsFromAVerifiedCopyAndStopsByIdentity(Host(shell), Path.GetTempPath());

    [SshTheory, MemberData(nameof(Shells))] public Task ACrossplayStartNeedsLibpartyToLoadOnTheHost(string shell) =>
        HostServerChecks.ACrossplayStartNeedsLibpartyToLoadOnTheHost(Host(shell), Path.GetTempPath());
}

[Trait("Category", "GameHosts")]
public class WindowsSshServerIntegrationTests
{
    // Opt-in on an owned Windows test host. Uses only a temporary copy of Windows' ping.exe as a stand-in server;
    // it never launches a game or touches the live install. The station claim is held outside this test.
    [Fact] public async Task AHeadlessServerSurvivesSshAndStopsByExactIdentity()
    {
        string? destination = Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_WINDOWS_DESTINATION");
        string? parent = Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_WINDOWS_RUNS");
        if (destination == null || parent == null) return;
        string[] options = (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_WINDOWS_OPTIONS") ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var host = new SshGameHost("windows-server", destination, HostShell.WindowsPowerShell, sshOptions: options);
        string root = HostPath.Join(parent, "windows-host-check-" + Guid.NewGuid().ToString("N"));
        string install = HostPath.Join(root, "install"), runtime = HostPath.Join(root, "run", "runtime");
        bool stopped = false;
        try
        {
            var setup = await host.RunAsync("""
                [void][IO.Directory]::CreateDirectory($install)
                [IO.File]::Copy((Join-Path ([Environment]::SystemDirectory) 'PING.EXE'), (Join-Path $install 'valheim_server.exe'))
                foreach ($file in @('BepInEx/core/BepInEx.Preloader.dll', 'BepInEx/core/BepInEx.dll', 'winhttp.dll', 'doorstop_config.ini')) {
                    $path = Join-Path $install $file
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
                    [IO.File]::WriteAllText($path, 'test-only placeholder')
                }
                'VT-TEST prepared'
                """, new Dictionary<string, string> { ["install"] = install }, TimeSpan.FromSeconds(30));
            Assert.True(setup.Succeeded, setup.Describe() + " " + setup.Stderr);
            await HostInstall.CopyAsync(host, install, runtime, TimeSpan.FromSeconds(45));
            var listing = await HostInstall.ListAsync(host, runtime, TimeSpan.FromSeconds(45));
            Assert.Equal(ServerPlatform.Windows, HostInstall.DetectServer(listing));
            var launch = GameLaunch.ForServer(runtime, ["-n", "60", "127.0.0.1"], hostPlatform: ServerPlatform.Windows);
            var process = await HostServer.StartAsync(host, launch, HostPath.Join(root, "run", "boot-1"), TimeSpan.FromSeconds(90));
            try
            {
                var status = await host.RunAsync("""
                    $p = [Diagnostics.Process]::GetProcessById([int]$game)
                    if ([string]$p.StartTime.ToFileTimeUtc() -cne $start -or $p.HasExited) { exit 3 }
                    'VT-TEST alive'
                    """, new Dictionary<string, string> { ["game"] = process.Id.ToString(), ["start"] = process.StartIdentity }, TimeSpan.FromSeconds(30));
                Assert.True(status.Succeeded, status.Describe() + " " + status.Stderr);
                Assert.Contains("VT-TEST alive", status.Stdout);
            }
            finally { Assert.Equal(HostServerStop.Stopped, await process.StopAsync(TimeSpan.FromSeconds(15))); stopped = true; }
        }
        finally
        {
            // Only the unique test root is removed. An unknown server stop keeps its evidence and runtime for inspection.
            if (stopped)
            {
                var check = await host.RunAsync("""
                if ($root -notmatch 'windows-host-check-[0-9a-f]{32}$') { exit 3 }
                if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $true) }
                'VT-TEST removed'
                """, new Dictionary<string, string> { ["root"] = root }, TimeSpan.FromSeconds(30));
                Assert.True(check.Succeeded, check.Describe() + " " + check.Stderr);
            }
        }
    }

    [Fact] public async Task ADisposableGameServerLoadsBepInExAndQuitsCleanlyOverSsh()
    {
        string? destination = Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_WINDOWS_DESTINATION");
        string? parent = Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_WINDOWS_RUNS");
        string? install = Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_WINDOWS_GAME_INSTALL");
        if (destination == null || parent == null || install == null) return;
        string[] options = (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_WINDOWS_OPTIONS") ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var host = new SshGameHost("windows-server", destination, HostShell.WindowsPowerShell, sshOptions: options);
        string root = HostPath.Join(parent, "windows-game-check-" + Guid.NewGuid().ToString("N"));
        string runtime = HostPath.Join(root, "runtime"), boot = HostPath.Join(root, "boot-1");
        bool stopped = false;
        try
        {
            await HostInstall.CopyAsync(host, install, runtime, TimeSpan.FromMinutes(8));
            // This smoke checks the host launch, not other mods' world-generation behavior. Prune only the disposable copy.
            var prune = await host.RunAsync("""
                $plugins = Join-Path $runtime 'BepInEx\plugins'
                foreach ($entry in [IO.Directory]::GetFileSystemEntries($plugins)) {
                    if ([IO.Path]::GetFileName($entry) -ieq 'ScriptEngine.dll') { continue }
                    if ([IO.Directory]::Exists($entry)) { [IO.Directory]::Delete($entry, $true) }
                    else { [IO.File]::Delete($entry) }
                }
                $config = Join-Path $runtime 'BepInEx\config\valheimCLI.valheimCLI.cfg'
                if ([IO.File]::Exists($config)) {
                    $text = [IO.File]::ReadAllText($config)
                    $text = [Text.RegularExpressions.Regex]::Replace($text, '(?m)^File = .*$', 'File = ')
                    $text = [Text.RegularExpressions.Regex]::Replace($text, '(?m)^Strict = .*$', 'Strict = false')
                    [IO.File]::WriteAllText($config, $text)
                }
                'VT-TEST prepared'
                """, new Dictionary<string, string> { ["runtime"] = runtime }, TimeSpan.FromMinutes(2));
            Assert.True(prune.Succeeded, prune.Describe() + " " + prune.Stderr);
            var launch = GameLaunch.ForServer(runtime,
                ["-batchmode", "-nographics", "-name", "VT Windows host check", "-port", "2486", "-world", "VT249" + Guid.NewGuid().ToString("N")[..8],
                 "-password", "throwaway249", "-public", "0", "-savedir", HostPath.Join(root, "world"), "-logFile", HostPath.Join(runtime, "toolkit-unity.log")], hostPlatform: ServerPlatform.Windows);
            var process = await HostServer.StartAsync(host, launch, boot, TimeSpan.FromSeconds(90), ["BepInEx/LogOutput.log", "toolkit-unity.log"]);
            try
            {
                string bepinex = HostPath.Join(runtime, "BepInEx", "LogOutput.log");
                (await host.WaitForLogAsync(bepinex, 0, new System.Text.RegularExpressions.Regex("Chainloader startup complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    StartupEvents.StartupFailures, TimeSpan.FromMinutes(6))).EnsureMatched();
                // The same run must reach a live CLI listener through an SSH loopback tunnel.
                (await host.WaitForLogAsync(bepinex, 0, StartupEvents.CliListening, StartupEvents.StartupFailures, TimeSpan.FromMinutes(6))).EnsureMatched();
                using var tunnel = await host.OpenCliTunnelAsync(5557, TimeSpan.FromSeconds(20));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(tunnel.Address, tunnel.LocalPort);
                Assert.True(tcp.Connected);
                (await host.WaitForLogAsync(HostPath.Join(runtime, "toolkit-unity.log"), 0,
                    new System.Text.RegularExpressions.Regex("Opened Steam server", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    StartupEvents.StartupFailures, TimeSpan.FromMinutes(6))).EnsureMatched();
                Assert.Equal(HostServerStop.Quit, await process.StopAsync(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15)));
                stopped = true;
            }
            finally { if (!stopped) { try { await process.StopAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(15)); stopped = true; } catch { } } }
            string? evidence = Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_WINDOWS_EVIDENCE");
            if (evidence != null) await host.FetchDirectoryAsync(boot, evidence, TimeSpan.FromMinutes(2));
        }
        finally
        {
            if (stopped)
            {
                var cleanup = await host.RunAsync("""
                    if ($root -notmatch 'windows-game-check-[0-9a-f]{32}$') { exit 3 }
                    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $true) }
                    'VT-TEST removed'
                    """, new Dictionary<string, string> { ["root"] = root }, TimeSpan.FromMinutes(2));
                Assert.True(cleanup.Succeeded, cleanup.Describe() + " " + cleanup.Stderr);
            }
        }
    }
}

[Trait("Category", "GameHosts")]
public class ContainerHostServerIntegrationTests
{
    public static TheoryData<string> Shells => new() { "bash" };

    [ContainerTheory, MemberData(nameof(Shells))] public Task AServerRunsFromAVerifiedCopyAndStopsByIdentity(string shell) =>
        HostServerChecks.AServerRunsFromAVerifiedCopyAndStopsByIdentity(new ContainerGameHost("ctr", Environment.GetEnvironmentVariable("VALHEIM_TESTING_CONTAINER")!, HostShell.Parse(shell)), "/tmp");

    [ContainerTheory, MemberData(nameof(Shells))] public Task ACrossplayStartNeedsLibpartyToLoadOnTheHost(string shell) =>
        HostServerChecks.ACrossplayStartNeedsLibpartyToLoadOnTheHost(new ContainerGameHost("ctr", Environment.GetEnvironmentVariable("VALHEIM_TESTING_CONTAINER")!, HostShell.Parse(shell)), "/tmp");
}
