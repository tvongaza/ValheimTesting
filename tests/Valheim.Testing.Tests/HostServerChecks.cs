using System.Net;
using System.Net.Sockets;
using Valheim.Testing.Game;
using Xunit;

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
        string server = Path.Combine(directory, ServerLaunch.LinuxExecutable);
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
        Assert.Contains(ServerLaunch.LinuxExecutable, listing.Executables);
        var pins = InstallPins.Of(source.Path);
        Assert.Equal(pins.Game, HostInstall.CheckPins(pins, listing, "runtime").Game);
        Assert.Equal(ServerPlatform.Linux, HostInstall.DetectServer(listing));
        var partial = await HostInstall.ListAsync(host, runtime, Generous, ["*_Data/Managed", "BepInEx/core", "BepInEx/patchers"]);
        Assert.DoesNotContain(ServerLaunch.LinuxExecutable, partial.Files.Keys);
        Assert.Equal(pins.BepInExCore, HostInstall.Pins(partial).BepInExCore);

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
            var launch = HostServerLaunch.Create(runtime, ["-batchmode", "-name", "it's a test"], new Dictionary<string, string> { ["VT_TEST_TOKEN"] = "token-123" });
            using var evidence = new TempDirectory();
            string local = Path.Combine(evidence.Path, "boot-1");
            var process = await HostServer.StartAsync(host, launch, root + "/run/boot-1", Generous, ["BepInEx/LogOutput.log", "toolkit-unity.log"], local);
            try
            {
                string line = (await host.WaitForLogAsync(runtime + "/BepInEx/LogOutput.log", 0, StartupEvents.CliListening, StartupEvents.StartupFailures, Generous)).EnsureMatched();
                Assert.EndsWith("this boot", line);
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
            async Task<ProcessStop> StopAfterListening(HostServerLaunch boot, string name, TimeSpan quit)
            {
                var started = await HostServer.StartAsync(host, boot, root + "/run/" + name, Generous, ["BepInEx/LogOutput.log"], Path.Combine(evidence.Path, name));
                (await host.WaitForLogAsync(runtime + "/BepInEx/LogOutput.log", 0, StartupEvents.CliListening, StartupEvents.StartupFailures, Generous)).EnsureMatched();
                var stop = started.StopCleanly(quit, TimeSpan.FromSeconds(15));
                Assert.True(started.HasExited);
                return stop with { ExitCode = await started.WaitForExitAsync(CancellationToken.None) };
            }
            var clean = await StopAfterListening(launch, "boot-2", TimeSpan.FromSeconds(15));
            Assert.Equal((StopOutcome.Clean, 130, "SIGINT"), (clean.Outcome, clean.ExitCode, clean.Request));
            var ignoring = HostServerLaunch.Create(runtime, ["-batchmode"], new Dictionary<string, string> { ["VT_TEST_TOKEN"] = "token-456", ["VT_IGNORE_INT"] = "1" });
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
            Assert.Contains("SteamAppId=" + ServerLaunch.DedicatedServerSteamAppId, stdout);
            Assert.Contains("DOORSTOP_ENABLED=1", stdout);
            Assert.Contains("DOORSTOP_TARGET_ASSEMBLY=" + runtime + "/BepInEx/core/BepInEx.Preloader.dll", stdout);
            // The server's working directory was the runtime: its log is gone from there, into the boot's evidence.
            Assert.Equal(0, await host.LogOffsetAsync(runtime + "/BepInEx/LogOutput.log", Generous));
        }
        finally { await host.RunAsync("kill \"$p\" 2> /dev/null; true", new Dictionary<string, string> { ["p"] = bystander }, Generous); }
    });

    public static async Task AListingMatchesTheLocalManifestAndPins(IGameHost host)
    {
        using var install = new TempDirectory();
        FakeInstalls.Client(install.Path);
        Directory.CreateDirectory(Path.Combine(install.Path, "BepInEx", "patchers", "Hooks"));
        File.WriteAllText(Path.Combine(install.Path, "BepInEx", "patchers", "Hooks", "Hook.dll"), "hook");
        File.WriteAllText(Path.Combine(install.Path, "with space é.txt"), "name");
        {
            var listing = await HostInstall.ListAsync(host, install.Path, Generous);
            HostInstall.RequireSame(WorldFixture.Manifest(install.Path), listing, "install");
            Assert.Equal(new[] { "Hooks" }, listing.Patchers);
            var pins = InstallPins.Of(install.Path);
            var found = HostInstall.Pins(listing);
            Assert.Equal((pins.Game, pins.BepInExCore, pins.Patchers), (found.Game, found.BepInExCore, found.Patchers));
            var partial = await HostInstall.ListAsync(host, install.Path, Generous, ["*_Data/Managed", "BepInEx/core", "BepInEx/patchers"]);
            Assert.DoesNotContain("with space é.txt", partial.Files.Keys);
            Assert.Equal(pins.Patchers, HostInstall.Pins(partial).Patchers);
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => HostInstall.ListAsync(host, Path.Combine(install.Path, "missing"), Generous));
        }
    }
}

public class LocalHostServerTests
{
    // The start, stop and port check read Linux's /proc; the listing runs in every local shell.
    [Fact] public Task AServerRunsFromAVerifiedCopyAndStopsByIdentity() =>
        OperatingSystem.IsLinux() ? HostServerChecks.AServerRunsFromAVerifiedCopyAndStopsByIdentity(new LocalGameHost("local-bash", HostShell.Bash), Path.GetTempPath()) : Task.CompletedTask;

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public Task AListingMatchesTheLocalManifestAndPins(string shell) => HostServerChecks.AListingMatchesTheLocalManifestAndPins(new LocalGameHost("local-" + shell, HostShell.Parse(shell)));

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task APowerShellHostTellsABusyPortFromAFreeOne(string shell)
    {
        // Bash reads /proc/net/tcp, checked on Linux above; a PowerShell host is a Windows client's.
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
}

[Trait("Category", "GameHosts")]
public class ContainerHostServerIntegrationTests
{
    public static TheoryData<string> Shells => new() { "bash" };

    [ContainerTheory, MemberData(nameof(Shells))] public Task AServerRunsFromAVerifiedCopyAndStopsByIdentity(string shell) =>
        HostServerChecks.AServerRunsFromAVerifiedCopyAndStopsByIdentity(new ContainerGameHost("ctr", Environment.GetEnvironmentVariable("VALHEIM_TESTING_CONTAINER")!, HostShell.Parse(shell)), "/tmp");
}
