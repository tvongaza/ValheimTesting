using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// The same end-to-end checks for every host kind, run through the host's real shell: locally on every CI OS, and over a real
// ssh to localhost and into a real container in the game-hosts CI job. Every file they touch on the host is under one new
// root directory, removed at the end. Nothing here needs a game.
internal static class GameHostChecks
{
    public static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    // #255: one deadline for the real-shell checks, from this machine as it is now: a trivial script's round trip, measured at
    // each call, times a fixed factor (never under Generous, never over 5 min). A machine starved by a game run gets a deadline
    // to match, and a failure past it still says where the time went (HostResult's phases).
    public static async Task<TimeSpan> ShellDeadlineAsync(IGameHost host)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (await host.RunAsync(host.Shell.Kind == HostShellKind.Bash ? "echo ok" : "'ok'", null, TimeSpan.FromMinutes(5))).EnsureSuccess("Measuring the shell's round trip");
        var deadline = TimeSpan.FromTicks(clock.Elapsed.Ticks * 30);
        return deadline < Generous ? Generous : deadline > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : deadline;
    }

    /// <summary>A new, absolute directory path on the host for one test, and its removal.</summary>
    public static async Task WithRootAsync(IGameHost host, string parent, Func<string, Task> test)
    {
        string root = parent.TrimEnd('/', '\\') + "/vt-" + Guid.NewGuid().ToString("N")[..12];
        try { await test(root); }
        finally
        {
            string script = host.Shell.Kind == HostShellKind.Bash ? "rm -rf -- \"$root\"" : "Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue";
            (await host.RunAsync(script, new Dictionary<string, string> { ["root"] = root }, Generous)).EnsureSuccess("Removing " + root);
        }
    }

    public static async Task AppendAsync(IGameHost host, string path, string text)
    {
        string script = host.Shell.Kind == HostShellKind.Bash
            ? "mkdir -p -- \"$(dirname -- \"$path\")\" && printf %s \"$text\" >> \"$path\""
            : "[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)); [IO.File]::AppendAllText($path, $text, (New-Object Text.UTF8Encoding $false))";
        (await host.RunAsync(script, new Dictionary<string, string> { ["path"] = path, ["text"] = text }, Generous)).EnsureSuccess("Appending to " + path);
    }

    public static async Task RunPassesValuesLiterallyAndReportsTheExitCode(IGameHost host)
    {
        string value = "it's $HOME `id` \"q\" ‘typo’ %PATH% ;&| éß\nsecond line";
        string script = host.Shell.Kind == HostShellKind.Bash
            ? "printf %s \"$value\"; echo 'to stderr' >&2; exit 3"
            : "[Console]::Out.Write($value); [Console]::Error.WriteLine('to stderr'); exit 3";
        var result = await host.RunAsync(script, new Dictionary<string, string> { ["value"] = value }, Generous);
        Assert.True(result.Outcome == HostOutcome.Exited, result.Describe());
        Assert.Equal(3, result.ExitCode); Assert.True(result.Failed);
        Assert.Equal(value, result.Stdout);
        Assert.Contains("to stderr", result.Stderr); Assert.DoesNotContain("[vt-exit]", result.Stderr);

        var fine = await host.RunAsync(host.Shell.Kind == HostShellKind.Bash ? "true" : "$null = 1", null, Generous);
        Assert.True(fine.Succeeded, fine.Describe());
    }

    public static async Task ATimeoutIsUnknown(IGameHost host)
    {
        var clock = Stopwatch.StartNew();
        var result = await host.RunAsync(host.Shell.Kind == HostShellKind.Bash ? "sleep 60" : "Start-Sleep -Seconds 60", null, TimeSpan.FromSeconds(3));
        Assert.Equal(HostOutcome.Unknown, result.Outcome); Assert.True(result.TimedOut);
        Assert.False(result.Succeeded); Assert.False(result.Failed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(45), "The deadline did not end the run: " + clock.Elapsed);
    }

    public static Task TwoRunsNeverShareTheLock(IGameHost host, string parent) => WithRootAsync(host, parent, async root =>
    {
        string path = root + "/lock";
        Assert.Equal(HostLockState.Free, (await host.CheckLockAsync(path, "run-a", Generous)).State);
        var first = await host.AcquireLockAsync(path, "run-a", Generous);
        Assert.StartsWith("run-a [", first.Owner);
        var refused = await Assert.ThrowsAsync<HostLockException>(() => host.AcquireLockAsync(path, "run-b", Generous));
        Assert.Equal(HostLockState.HeldByOther, refused.State); Assert.Equal(first.Owner, refused.Holder);
        // Another run that passes the same owner string is refused too, and the bare owner releases nothing.
        var same = await Assert.ThrowsAsync<HostLockException>(() => host.AcquireLockAsync(path, "run-a", Generous));
        Assert.Equal(HostLockState.HeldByOther, same.State);
        Assert.Equal(HostLockState.HeldByOther, (await host.ReleaseLockAsync(path, "run-a", Generous)).State);
        Assert.Equal(HostLockState.Yours, (await host.CheckLockAsync(path, first.Owner, Generous)).State);
        Assert.Equal(HostLockState.Released, (await first.ReleaseAsync()).State);
        Assert.Equal(HostLockState.Free, (await host.CheckLockAsync(path, first.Owner, Generous)).State);
        await using var second = await host.AcquireLockAsync(path, "run-b", Generous);
        Assert.StartsWith("run-b [", second.Owner);
    });

    public static Task ShippedFilesComeBackAsEvidence(IGameHost host, string parent) => WithRootAsync(host, parent, async root =>
    {
        using var source = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(source.Path, "BepInEx", "config"));
        File.WriteAllText(Path.Combine(source.Path, "BepInEx", "config", "my.mod.cfg"), "Enabled = true\n");
        File.WriteAllBytes(Path.Combine(source.Path, "data.bin"), [0, 1, 2, 255, 13, 10]);
        var shipped = await host.ShipFilesAsync(source.Path, root + "/files", Generous);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ShipFilesAsync(source.Path, root + "/files", Generous));

        using var target = new TempDirectory();
        var fetched = await host.FetchDirectoryAsync(root + "/files", Path.Combine(target.Path, "evidence"), Generous);
        Assert.Equal(3, fetched.Files);
        Assert.Equal("Enabled = true\n", File.ReadAllText(Path.Combine(fetched.LocalDirectory, "BepInEx", "config", "my.mod.cfg")));
        Assert.Equal(new byte[] { 0, 1, 2, 255, 13, 10 }, File.ReadAllBytes(Path.Combine(fetched.LocalDirectory, "data.bin")));
        Assert.Contains("sha256=" + shipped.Sha256, File.ReadAllText(Path.Combine(fetched.LocalDirectory, "SOURCE.txt")));
        await Assert.ThrowsAsync<HostOperationException>(() => host.FetchDirectoryAsync(root + "/missing", Path.Combine(target.Path, "none"), Generous));
        Assert.False(Directory.Exists(Path.Combine(target.Path, "none")));
    });

    public static Task ARevisionShipsItsCommittedFilesOnly(IGameHost host, string parent) => WithRootAsync(host, parent, async root =>
    {
        using var repository = new TempDirectory();
        Git(repository.Path, "init", "-q");
        // Windows runners convert line endings by default, in commits and in git archive alike.
        Git(repository.Path, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repository.Path, "plugin.txt"), "committed\n");
        Git(repository.Path, "add", "plugin.txt");
        Git(repository.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "first");
        File.WriteAllText(Path.Combine(repository.Path, "plugin.txt"), "uncommitted\n");
        File.WriteAllText(Path.Combine(repository.Path, "untracked.txt"), "untracked\n");
        string commit = Git(repository.Path, "rev-parse", "HEAD").Trim();

        var shipped = await host.ShipRevisionAsync(repository.Path, "HEAD", root + "/revision", Generous);
        Assert.Equal(commit, shipped.Commit);
        using var target = new TempDirectory();
        var fetched = await host.FetchDirectoryAsync(root + "/revision", Path.Combine(target.Path, "back"), Generous);
        Assert.Equal("committed\n", File.ReadAllText(Path.Combine(fetched.LocalDirectory, "plugin.txt")));
        Assert.False(File.Exists(Path.Combine(fetched.LocalDirectory, "untracked.txt")));
        Assert.Contains("commit=" + commit, File.ReadAllText(Path.Combine(fetched.LocalDirectory, "SOURCE.txt")));
    });

    public static Task ALogWaitSeesOnlyLinesFromItsOffset(IGameHost host, string parent) => WithRootAsync(host, parent, async root =>
    {
        string log = root + "/logs/LogOutput.log";
        Assert.Equal(0, await host.LogOffsetAsync(log, Generous));
        await AppendAsync(host, log, "[Info] Command server listening (previous boot)\n");
        long offset = await host.LogOffsetAsync(log, Generous);
        Assert.Equal(Encoding.UTF8.GetByteCount("[Info] Command server listening (previous boot)\n"), offset);
        await AppendAsync(host, log, "[Info] booting\r\n[Info] Command server listening on 5577\n");
        var success = new Regex(@"Command server listening on (\d+)$");
        var failures = new[] { new Regex(@"^\[Fatal") };
        var matched = await host.WaitForLogAsync(log, offset, success, failures, Generous);
        Assert.Equal("[Info] Command server listening on 5577", matched.EnsureMatched());

        offset = await host.LogOffsetAsync(log, Generous);
        await AppendAsync(host, log, "[Fatal] Could not load [MyMod]\n[Info] Command server listening on 5577\n");
        var failed = await host.WaitForLogAsync(log, offset, success, failures, Generous);
        Assert.Equal(HostLogOutcome.FailureMatched, failed.Outcome); Assert.Equal("[Fatal] Could not load [MyMod]", failed.Line);

        // The deadline counts the shell's start too: long enough for a cold pwsh on a busy runner (over 3 s on macOS) to read the lines already there.
        var expired = await host.WaitForLogAsync(log, offset, new Regex("never written"), null, TimeSpan.FromSeconds(10));
        Assert.Equal(HostLogOutcome.TimedOut, expired.Outcome); Assert.Equal("[Info] Command server listening on 5577", expired.LastLine);

        // A log that appears after the wait began is read from its start.
        string later = root + "/later/LogOutput.log";
        var wait = host.WaitForLogAsync(later, 0, new Regex("^ready$"), null, Generous);
        await AppendAsync(host, later, "booting\nready\n");
        Assert.Equal("ready", (await wait).EnsureMatched());
    });

    public static async Task ATunnelReachesTheHostsLoopbackAndClosesOnDispose(IGameHost host)
    {
        // The "ValheimCLI": a loopback echo on this machine, which is the host's loopback for ssh to localhost.
        var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        try
        {
            int hostPort = ((IPEndPoint)server.LocalEndpoint).Port;
            var tunnel = await host.OpenCliTunnelAsync(hostPort, Generous);
            Assert.True(tunnel.Forwarded); Assert.NotEqual(hostPort, tunnel.LocalPort);
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort);
                // The readiness probe also went through the forward; take connections until one carries data.
                var stream = client.GetStream();
                await stream.WriteAsync("ping\n"u8.ToArray());
                string? echoed = null;
                while (echoed == null)
                {
                    using var accepted = await server.AcceptTcpClientAsync().WaitAsync(Generous);
                    var buffer = new byte[5];
                    int read = await accepted.GetStream().ReadAtLeastAsync(buffer, 5, throwOnEndOfStream: false).AsTask().WaitAsync(Generous);
                    if (read == 5) echoed = Encoding.ASCII.GetString(buffer);
                }
                Assert.Equal("ping\n", echoed);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.OpenCliTunnelAsync(hostPort, Generous, tunnel.LocalPort));
            tunnel.Dispose();
            Assert.True(tunnel.HasExited);
            using var after = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => after.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort));
        }
        finally { server.Stop(); }
    }

    private static string Git(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(repository);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }
}

// Every local shell this machine has: bash on macOS and Linux, Windows PowerShell on Windows, pwsh wherever it is installed.
[Trait("Category", "CiShell")]
public class LocalGameHostShellTests
{
    public static TheoryData<string> Shells
    {
        get
        {
            var shells = new TheoryData<string>();
            if (!OperatingSystem.IsWindows()) shells.Add("bash");
            else shells.Add("powershell");
            if (OnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")) shells.Add("pwsh");
            return shells;
        }
    }
    private static IGameHost Host(string shell) => new LocalGameHost("local-" + shell, HostShell.Parse(shell));
    private static string Parent => Path.GetTempPath();
    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(dir => File.Exists(Path.Combine(dir, name)));

    // #133, with the real pwsh: a local pwsh host neither reads nor writes pwsh's startup JIT profile. Its cache directory is a
    // fresh one (XDG_CACHE_HOME, Linux and macOS). The control, a pwsh started the same way without the guard, writes the profile
    // there, so the check can see it.
    [Fact] public async Task APwshHostLeavesTheStartupJitProfileAlone()
    {
        if (OperatingSystem.IsWindows() || !OnPath("pwsh")) return;
        using var guarded = new TempDirectory();
        using var control = new TempDirectory();
        var host = new LocalGameHost("local-pwsh", HostShell.Pwsh, new CacheLauncher(guarded.Path));
        var result = await host.RunAsync("$null = [System.Collections.Concurrent.ConcurrentDictionary[string,int]]::new()", null, GameHostChecks.Generous);
        Assert.True(result.Succeeded, result.Describe());
        var unguarded = await new CacheLauncher(control.Path).RunAsync(new ProcessCall("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "exit 0"], [], null, null, null, GameHostChecks.Generous), default);
        Assert.Equal(0, unguarded.ExitCode);
        Assert.Contains(Directory.EnumerateFiles(control.Path, "StartupProfileData-*", SearchOption.AllDirectories), _ => true);
        Assert.Empty(Directory.EnumerateFiles(guarded.Path, "StartupProfileData-*", SearchOption.AllDirectories));
    }
    // Starts processes as the system does, with pwsh's cache directory moved to a test directory.
    private sealed class CacheLauncher(string cache) : IProcessLauncher
    {
        public Task<ProcessExit> RunAsync(ProcessCall call, CancellationToken cancellation) =>
            SystemProcessLauncher.Instance.RunAsync(call with { Environment = new Dictionary<string, string>(call.Environment) { ["XDG_CACHE_HOME"] = cache } }, cancellation);
        public IStartedProcess Start(string executable, IReadOnlyList<string> arguments) => SystemProcessLauncher.Instance.Start(executable, arguments);
    }

    [Theory, MemberData(nameof(Shells))] public Task RunPassesValuesLiterallyAndReportsTheExitCode(string shell) => GameHostChecks.RunPassesValuesLiterallyAndReportsTheExitCode(Host(shell));
    [Theory, MemberData(nameof(Shells))] public Task ATimeoutIsUnknown(string shell) => GameHostChecks.ATimeoutIsUnknown(Host(shell));
    [Theory, MemberData(nameof(Shells))] public Task TwoRunsNeverShareTheLock(string shell) => GameHostChecks.TwoRunsNeverShareTheLock(Host(shell), Parent);
    [Theory, MemberData(nameof(Shells))] public Task ShippedFilesComeBackAsEvidence(string shell) => GameHostChecks.ShippedFilesComeBackAsEvidence(Host(shell), Parent);
    [Theory, MemberData(nameof(Shells))] public Task ARevisionShipsItsCommittedFilesOnly(string shell) => GameHostChecks.ARevisionShipsItsCommittedFilesOnly(Host(shell), Parent);
    [Theory, MemberData(nameof(Shells))] public Task ALogWaitSeesOnlyLinesFromItsOffset(string shell) => GameHostChecks.ALogWaitSeesOnlyLinesFromItsOffset(Host(shell), Parent);

    [Fact] public async Task AShellThatDoesNotExistIsATransportFailure()
    {
        var result = await new LocalGameHost("nowhere", new HostShell(HostShellKind.Bash, "vt-no-such-shell-" + Guid.NewGuid().ToString("N"))).RunAsync("true", null, GameHostChecks.Generous);
        Assert.Equal(HostOutcome.TransportFailed, result.Outcome); Assert.False(result.Failed);
    }
}

/// <summary>
/// Runs only where the environment names an ssh destination (the game-hosts CI job starts a throwaway sshd on localhost):
/// VALHEIM_TESTING_SSH_DESTINATION, optional VALHEIM_TESTING_SSH_PORT, VALHEIM_TESTING_SSH_OPTIONS (one Name=value per line)
/// and VALHEIM_TESTING_SSH_SHELLS (comma separated, bash by default). The destination must be this machine.
/// </summary>
public sealed class SshTheoryAttribute : TheoryAttribute
{
    public SshTheoryAttribute() { if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION") is null or "") Skip = "Set VALHEIM_TESTING_SSH_DESTINATION to an ssh destination on this machine"; }
}
/// <summary>As <see cref="SshTheoryAttribute"/>, for a test without data.</summary>
public sealed class SshFactAttribute : FactAttribute
{
    public SshFactAttribute() { if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION") is null or "") Skip = "Set VALHEIM_TESTING_SSH_DESTINATION to an ssh destination on this machine"; }
}
/// <summary>Runs only where VALHEIM_TESTING_CONTAINER names a running Linux container on this machine's Docker daemon, started with --network host.</summary>
public sealed class ContainerTheoryAttribute : TheoryAttribute
{
    public ContainerTheoryAttribute() { if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_CONTAINER") is null or "") Skip = "Set VALHEIM_TESTING_CONTAINER to a running container"; }
}

[Trait("Category", "GameHosts")]
public class SshGameHostIntegrationTests
{
    public static TheoryData<string> Shells
    {
        get
        {
            var shells = new TheoryData<string>();
            foreach (string shell in (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_SHELLS") ?? "bash").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) shells.Add(shell);
            return shells;
        }
    }
    private static IGameHost Host(string shell) => new SshGameHost("ssh-" + shell, Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION")!, HostShell.Parse(shell),
        int.TryParse(Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_PORT"), out int port) ? port : 0,
        (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_OPTIONS") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    // The ssh user's view of this machine's temporary directory; the destination is this machine.
    private static string Parent => Path.GetTempPath();

    [SshTheory, MemberData(nameof(Shells))] public Task RunPassesValuesLiterallyAndReportsTheExitCode(string shell) => GameHostChecks.RunPassesValuesLiterallyAndReportsTheExitCode(Host(shell));
    [SshTheory, MemberData(nameof(Shells))] public Task ATimeoutIsUnknown(string shell) => GameHostChecks.ATimeoutIsUnknown(Host(shell));
    [SshTheory, MemberData(nameof(Shells))] public Task TwoRunsNeverShareTheLock(string shell) => GameHostChecks.TwoRunsNeverShareTheLock(Host(shell), Parent);
    [SshTheory, MemberData(nameof(Shells))] public Task ShippedFilesComeBackAsEvidence(string shell) => GameHostChecks.ShippedFilesComeBackAsEvidence(Host(shell), Parent);
    [SshTheory, MemberData(nameof(Shells))] public Task ARevisionShipsItsCommittedFilesOnly(string shell) => GameHostChecks.ARevisionShipsItsCommittedFilesOnly(Host(shell), Parent);
    [SshTheory, MemberData(nameof(Shells))] public Task ALogWaitSeesOnlyLinesFromItsOffset(string shell) => GameHostChecks.ALogWaitSeesOnlyLinesFromItsOffset(Host(shell), Parent);
    [SshTheory, MemberData(nameof(Shells))] public Task ATunnelReachesTheHostsLoopbackAndClosesOnDispose(string shell) => GameHostChecks.ATunnelReachesTheHostsLoopbackAndClosesOnDispose(Host(shell));

    [SshTheory, MemberData(nameof(Shells))] public async Task AnUnreachablePortIsATransportFailure(string shell)
    {
        // Nothing listens on this port: ssh fails to connect and exits 255 without the host's report.
        int closed = new Func<int>(() => { var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); return port; })();
        var host = new SshGameHost("unreachable", Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION")!, HostShell.Parse(shell), closed,
            (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_OPTIONS") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var result = await host.RunAsync("true", null, GameHostChecks.Generous);
        Assert.True(result.Outcome == HostOutcome.TransportFailed, result.Describe());
        await Assert.ThrowsAsync<HostLockException>(() => host.AcquireLockAsync("/tmp/vt-unreachable-lock", "run", GameHostChecks.Generous));
    }
}

[Trait("Category", "GameHosts")]
public class ContainerGameHostIntegrationTests
{
    public static TheoryData<string> Shells => new() { "bash" };
    private static IGameHost Host(string shell) => new ContainerGameHost("ctr", Environment.GetEnvironmentVariable("VALHEIM_TESTING_CONTAINER")!, HostShell.Parse(shell));
    private const string Parent = "/tmp";

    [ContainerTheory, MemberData(nameof(Shells))] public Task RunPassesValuesLiterallyAndReportsTheExitCode(string shell) => GameHostChecks.RunPassesValuesLiterallyAndReportsTheExitCode(Host(shell));
    [ContainerTheory, MemberData(nameof(Shells))] public Task ATimeoutIsUnknown(string shell) => GameHostChecks.ATimeoutIsUnknown(Host(shell));
    [ContainerTheory, MemberData(nameof(Shells))] public Task TwoRunsNeverShareTheLock(string shell) => GameHostChecks.TwoRunsNeverShareTheLock(Host(shell), Parent);
    [ContainerTheory, MemberData(nameof(Shells))] public Task ShippedFilesComeBackAsEvidence(string shell) => GameHostChecks.ShippedFilesComeBackAsEvidence(Host(shell), Parent);
    [ContainerTheory, MemberData(nameof(Shells))] public Task ARevisionShipsItsCommittedFilesOnly(string shell) => GameHostChecks.ARevisionShipsItsCommittedFilesOnly(Host(shell), Parent);
    [ContainerTheory, MemberData(nameof(Shells))] public Task ALogWaitSeesOnlyLinesFromItsOffset(string shell) => GameHostChecks.ALogWaitSeesOnlyLinesFromItsOffset(Host(shell), Parent);

    [ContainerTheory, MemberData(nameof(Shells))] public async Task AHostNetworkContainerIsReachedOnTheCliPortAndAMissingOneIsATransportFailure(string shell)
    {
        var tunnel = await Host(shell).OpenCliTunnelAsync(5577, GameHostChecks.Generous);
        Assert.Equal(5577, tunnel.LocalPort); Assert.False(tunnel.Forwarded);
        var missing = new ContainerGameHost("gone", "vt-no-such-container-" + Guid.NewGuid().ToString("N")[..8], HostShell.Parse(shell));
        var result = await missing.RunAsync("true", null, GameHostChecks.Generous);
        Assert.True(result.Outcome == HostOutcome.TransportFailed, result.Describe());
    }
}

// The game-hosts CI job sets VALHEIM_TESTING_REQUIRE_HOSTS=1, so a lost variable fails the job instead of skipping every check.
[Trait("Category", "GameHosts")]
public class GameHostIntegrationSetupTests
{
    [Fact] public void TheRequiredHostsAreConfigured()
    {
        if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_REQUIRE_HOSTS") != "1") return;
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION")), "VALHEIM_TESTING_SSH_DESTINATION is not set");
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VALHEIM_TESTING_CONTAINER")), "VALHEIM_TESTING_CONTAINER is not set");
    }
}
