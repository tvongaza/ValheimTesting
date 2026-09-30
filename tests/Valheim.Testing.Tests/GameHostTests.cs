using System.Formats.Tar;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Xunit;

// Each host kind with fake processes: what it starts, and how a transport failure, a timeout and a held lock are reported.
// A timed-out or report-less run must come back as an unknown outcome, never as a pass or a script failure.
public class GameHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string Lock = "/var/tmp/vt-lock";

    public static TheoryData<string> Kinds => new() { "local", "ssh", "container" };
    private static ScriptedGameHost Host(string kind, FakeLauncher fake, HostShell? shell = null) => kind switch
    {
        "local" => new LocalGameHost("here", shell ?? HostShell.Bash, fake),
        "ssh" => new SshGameHost("box", "tester@box.example", shell ?? HostShell.Bash, 0, null, null, "ssh", fake),
        _ => new ContainerGameHost("ctr", "vt-server", shell ?? HostShell.Bash, "valheim", "docker", fake),
    };

    [Fact] public async Task ALocalHostStartsTheWrapperInItsOwnShell()
    {
        var fake = new FakeLauncher().Exits(0, "hi\n", FakeLauncher.Report(0));
        var result = await Host("local", fake).RunAsync("echo hi", null, Timeout);
        Assert.True(result.Succeeded); Assert.Equal("hi\n", result.Stdout);
        Assert.Equal("bash", fake.Calls[0].Executable);
        Assert.Equal(new[] { "-c", HostScripts.BashWrapper }, fake.Calls[0].Arguments);
        fake.Exits(0, "", FakeLauncher.Report(0));
        await Host("local", fake, HostShell.Pwsh).RunAsync("'hi'", null, Timeout);
        Assert.Equal("pwsh", fake.Calls[1].Executable);
        Assert.Equal(new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", ScriptedGameHost.EncodedPowerShellWrapper }, fake.Calls[1].Arguments);
    }

    [Fact] public async Task AnSshHostSetsItsFixedOptionsFirstAndTheCommandAfterTheDestination()
    {
        var fake = new FakeLauncher().Exits(0, "", FakeLauncher.Report(0));
        var host = new SshGameHost("box", "tester@box.example", HostShell.Bash, 2222, ["IdentityFile=/keys/test key"], TimeSpan.FromSeconds(5), "ssh", fake);
        await host.RunAsync("true", null, Timeout);
        var arguments = fake.Calls[0].Arguments;
        Assert.Equal(new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "-o", "GatewayPorts=no", "-o", "ClearAllForwardings=yes", "-o", "IdentityFile=\"/keys/test key\"" }, arguments.Take(10));
        int end = arguments.ToList().IndexOf("--");
        Assert.Equal(new[] { "-p", "2222", "-a", "-x", "-T" }, arguments.Skip(end - 5).Take(5));
        Assert.Equal(new[] { "--", "tester@box.example", "bash -c '" + HostScripts.BashWrapper + "'" }, arguments.Skip(end));
        // The script and its values travel on stdin, never on the command line.
        Assert.DoesNotContain("true", string.Join(" ", arguments.Skip(end + 2)).Replace(HostScripts.BashWrapper, ""));
        Assert.Contains("true", FakeLauncher.Script(fake.Calls[0]));
    }

    [Fact] public void AnSshHostRefusesPasswordsForwardsAndOverridesOfItsFixedOptions()
    {
        var fake = new FakeLauncher();
        Assert.Throws<ArgumentException>(() => new SshGameHost("box", "tester:secret@box", HostShell.Bash, 0, null, null, "ssh", fake));
        Assert.Throws<ArgumentException>(() => new SshGameHost("box", "-oProxyCommand=x", HostShell.Bash, 0, null, null, "ssh", fake));
        foreach (string option in new[] { "BatchMode=no", "batchmode=no", "GatewayPorts=yes", "LocalForward=0.0.0.0:5577 127.0.0.1:5577", "RemoteForward=1 x:2", "Port=22", "Bad Option=1" })
            Assert.Throws<ArgumentException>(() => new SshGameHost("box", "box", HostShell.Bash, 0, [option], null, "ssh", fake));
        Assert.Throws<ArgumentException>(() => new SshGameHost("box", "ssh://tester@box:2222", HostShell.Bash, 2222, null, null, "ssh", fake));
        _ = new SshGameHost("box", "ssh://tester@box:2222", HostShell.Bash, 0, ["StrictHostKeyChecking=accept-new"], null, "ssh", fake);
    }

    [Fact] public async Task AContainerHostExecsTheWrapperAsItsUser()
    {
        var fake = new FakeLauncher().Exits(0, "", FakeLauncher.Report(0));
        await Host("container", fake).RunAsync("true", null, Timeout);
        Assert.Equal("docker", fake.Calls[0].Executable);
        Assert.Equal(new[] { "exec", "-i", "--user", "valheim", "vt-server", "bash", "-c", HostScripts.BashWrapper }, fake.Calls[0].Arguments);
        Assert.Throws<ArgumentException>(() => new ContainerGameHost("ctr", "--privileged", HostShell.Bash, null, "docker", fake));
        Assert.Throws<ArgumentException>(() => new ContainerGameHost("ctr", "vt", HostShell.Bash, "root;id", "docker", fake));
    }

    [Theory, MemberData(nameof(Kinds))] public async Task TheHostsExitReportIsTheScriptsExitCode(string kind)
    {
        // A Windows login shell may turn any failure into 1 (or ssh into 255); the report still carries the real code.
        var fake = new FakeLauncher().Exits(kind == "ssh" ? 255 : 1, "out\r\n", "warning\n" + FakeLauncher.Report(7));
        var result = await Host(kind, fake).RunAsync("exit 7", null, Timeout);
        Assert.Equal(HostOutcome.Exited, result.Outcome); Assert.Equal(7, result.ExitCode);
        Assert.True(result.Failed); Assert.False(result.Succeeded);
        Assert.Equal("out\n", result.Stdout); Assert.Equal("warning\n", result.Stderr);
        var error = Assert.Throws<HostOperationException>(() => result.EnsureSuccess("Starting"));
        Assert.Contains("exited with code 7", error.Message);
    }

    [Theory, MemberData(nameof(Kinds))] public async Task ATimeoutIsAnUnknownOutcomeNeitherPassedNorFailed(string kind)
    {
        var fake = new FakeLauncher().TimesOut("partial");
        var result = await Host(kind, fake).RunAsync("sleep 600", null, Timeout);
        Assert.Equal(HostOutcome.Unknown, result.Outcome); Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode); Assert.False(result.Succeeded); Assert.False(result.Failed);
        var error = Assert.Throws<HostOperationException>(() => result.EnsureSuccess("Waiting"));
        Assert.Equal(HostOutcome.Unknown, error.Outcome);
        Assert.Contains("outcome is unknown", error.Message);
    }

    [Fact] public async Task SshExit255WithoutAReportIsATransportFailure()
    {
        var fake = new FakeLauncher().Exits(255, "", "ssh: connect to host box.example port 22: Connection refused\n").Exits(1, "", "");
        var failed = await Host("ssh", fake).RunAsync("true", null, Timeout);
        Assert.Equal(HostOutcome.TransportFailed, failed.Outcome); Assert.False(failed.Failed); Assert.False(failed.Succeeded);
        Assert.Contains("Connection refused", failed.Describe());
        // Any other end without the report is not known to be the transport: the script may have run.
        var unknown = await Host("ssh", fake).RunAsync("true", null, Timeout);
        Assert.Equal(HostOutcome.Unknown, unknown.Outcome); Assert.False(unknown.TimedOut);
    }

    [Fact] public async Task ADockerErrorWithoutAReportIsATransportFailure()
    {
        var fake = new FakeLauncher().Exits(1, "", "Error response from daemon: No such container: vt-server\n").Exits(126, "", "OCI runtime exec failed\n");
        Assert.Equal(HostOutcome.TransportFailed, (await Host("container", fake).RunAsync("true", null, Timeout)).Outcome);
        Assert.Equal(HostOutcome.TransportFailed, (await Host("container", fake).RunAsync("true", null, Timeout)).Outcome);
    }

    [Theory, MemberData(nameof(Kinds))] public async Task AShellOrClientThatCannotStartIsATransportFailure(string kind)
    {
        var fake = new FakeLauncher().Reply(_ => new ProcessExit(ProcessEnd.NotStarted, -1, "", "could not start ssh: No such file or directory", TimeSpan.Zero));
        var result = await Host(kind, fake).RunAsync("true", null, Timeout);
        Assert.Equal(HostOutcome.TransportFailed, result.Outcome); Assert.Contains("could not start", result.Stderr);
    }

    [Theory, MemberData(nameof(Kinds))] public async Task ALockHeldByAnotherRunIsRefusedWithItsHolder(string kind)
    {
        var fake = new FakeLauncher().Exits(0, "VT-LOCK held\nrun-41 on another machine\n", FakeLauncher.Report(0));
        var error = await Assert.ThrowsAsync<HostLockException>(() => Host(kind, fake).AcquireLockAsync(Lock, "run-42", Timeout));
        Assert.Equal(HostLockState.HeldByOther, error.State); Assert.Equal("run-41 on another machine", error.Holder);
        string script = FakeLauncher.Script(fake.Calls[0]);
        Assert.Contains("action='claim'", script); Assert.Contains("lock='/var/tmp/vt-lock'", script);
        Assert.Matches(@"owner='run-42 \[[0-9a-f]{32}\]'", script);
    }

    [Theory, MemberData(nameof(Kinds))] public async Task AClaimWhoseReplyTimedOutIsUnknownNotHeldOrFree(string kind)
    {
        var fake = new FakeLauncher().TimesOut();
        var error = await Assert.ThrowsAsync<HostLockException>(() => Host(kind, fake).AcquireLockAsync(Lock, "run-42", Timeout));
        Assert.Equal(HostLockState.Unknown, error.State); Assert.Null(error.Holder);
        Assert.Contains("not proven", error.Message); Assert.Contains("The claim was made as 'run-42 [", error.Message);
    }

    [Fact] public async Task ALockIsReleasedOnDisposeAndAnUnprovenReleaseThrows()
    {
        var fake = new FakeLauncher().Exits(0, "VT-LOCK claimed\n", FakeLauncher.Report(0)).Exits(0, "VT-LOCK released\n", FakeLauncher.Report(0));
        var host = Host("ssh", fake);
        string claimant;
        await using (var held = await host.AcquireLockAsync(Lock, "run-42", Timeout)) claimant = held.Owner;
        Assert.StartsWith("run-42 [", claimant);
        Assert.Contains($"owner='{claimant}'", FakeLauncher.Script(fake.Calls[0]));
        Assert.Contains("action='release'", FakeLauncher.Script(fake.Calls[1])); Assert.Contains($"owner='{claimant}'", FakeLauncher.Script(fake.Calls[1]));

        fake.Exits(0, "VT-LOCK yours\n", FakeLauncher.Report(0)).Exits(255, "", "Connection reset\n");
        var again = await host.AcquireLockAsync(Lock, "run-42", Timeout);
        var error = await Assert.ThrowsAsync<HostLockException>(async () => await again.DisposeAsync());
        Assert.Equal(HostLockState.Unknown, error.State);
    }

    [Fact] public async Task TwoAcquisitionsWithTheSameOwnerClaimAsDifferentClaimants()
    {
        // Two runs that both call themselves "nightly" must not both hold the lock: each claim carries its own id.
        var fake = new FakeLauncher().Exits(0, "VT-LOCK claimed\n", FakeLauncher.Report(0)).Exits(0, "VT-LOCK held\nnightly [x]\n", FakeLauncher.Report(0));
        var host = Host("local", fake);
        var first = await host.AcquireLockAsync(Lock, "nightly", Timeout);
        var error = await Assert.ThrowsAsync<HostLockException>(() => host.AcquireLockAsync(Lock, "nightly", Timeout));
        Assert.Equal(HostLockState.HeldByOther, error.State);
        string Owner(int call) => Regex.Match(FakeLauncher.Script(fake.Calls[call]), "owner='([^']*)'").Groups[1].Value;
        Assert.Equal(first.Owner, Owner(0)); Assert.NotEqual(Owner(0), Owner(1));
        await Assert.ThrowsAsync<ArgumentException>(() => host.AcquireLockAsync(Lock, new string('x', 201), Timeout));
    }

    [Fact] public void LockRepliesAreReadStrictly()
    {
        HostResult Ok(string stdout) => new(HostOutcome.Exited, 0, stdout, "", TimeSpan.Zero, false);
        Assert.Equal(HostLockState.Unknown, ScriptedGameHost.ReadLockVerdict("claim", "a", Lock, "h", Ok("VT-LOCK unowned\n")).State);
        Assert.Equal(HostLockState.Unknown, ScriptedGameHost.ReadLockVerdict("claim", "a", Lock, "h", Ok("VT-LOCK released\n")).State);
        Assert.Equal(HostLockState.Unknown, ScriptedGameHost.ReadLockVerdict("claim", "a", Lock, "h", Ok("VT-LOCK held\n")).State);
        Assert.Equal(HostLockState.Unknown, ScriptedGameHost.ReadLockVerdict("check", "a", Lock, "h", new(HostOutcome.Exited, 3, "", "", TimeSpan.Zero, false)).State);
        Assert.Equal(HostLockState.Free, ScriptedGameHost.ReadLockVerdict("release", "a", Lock, "h", Ok("VT-LOCK free\n")).State);
        Assert.Equal(HostLockState.Yours, ScriptedGameHost.ReadLockVerdict("check", "a", Lock, "h", Ok("VT-LOCK yours\n")).State);
    }

    [Fact] public async Task VariablesArriveAsLiteralsThatNoShellInterprets()
    {
        string value = "it's $HOME `id` \"q\" \u2018typo\u2019\nline 2 %PATH%";
        var fake = new FakeLauncher().Exits(0, "", FakeLauncher.Report(0)).Exits(0, "", FakeLauncher.Report(0));
        await Host("ssh", fake).RunAsync("printf %s \"$value\"\r\n", new Dictionary<string, string> { ["value"] = value }, Timeout);
        Assert.Equal("value='it'\\''s $HOME `id` \"q\" \u2018typo\u2019\nline 2 %PATH%'\nprintf %s \"$value\"\n", FakeLauncher.Script(fake.Calls[0]));
        await Host("ssh", fake, HostShell.WindowsPowerShell).RunAsync("$value", new Dictionary<string, string> { ["value"] = value }, Timeout);
        Assert.Equal("$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n$value = 'it''s $HOME `id` \"q\" \u2018\u2018typo\u2019\u2019\nline 2 %PATH%'\n$value\nexit 0\n",
            FakeLauncher.Script(fake.Calls[1]));
        Assert.StartsWith("powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", fake.Calls[1].Arguments.Last());
    }

    [Fact] public async Task ReservedOrInvalidVariablesAreRefusedBeforeAnythingStarts()
    {
        var fake = new FakeLauncher();
        foreach (var (shell, name) in new[] { (HostShell.Bash, "PATH"), (HostShell.Bash, "SECONDS"), (HostShell.Bash, "VT_UPLOAD"), (HostShell.Bash, "1x"),
                     (HostShell.Pwsh, "ErrorActionPreference"), (HostShell.Pwsh, "lastexitcode"), (HostShell.Pwsh, "a-b") })
            await Assert.ThrowsAsync<ArgumentException>(() => Host("local", fake, shell).RunAsync("x", new Dictionary<string, string> { [name] = "v" }, Timeout));
        await Assert.ThrowsAsync<ArgumentException>(() => Host("local", fake).RunAsync("x", new Dictionary<string, string> { ["v"] = "a\0b" }, Timeout));
        await Assert.ThrowsAsync<ArgumentException>(() => Host("local", fake, HostShell.Pwsh).RunAsync("x", new Dictionary<string, string> { ["v"] = "1", ["V"] = "2" }, Timeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Host("local", fake).RunAsync("x", null, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentException>(() => Host("ssh", fake).AcquireLockAsync("relative/lock", "run", Timeout));
        Assert.Empty(fake.Calls);
    }

    [Theory, MemberData(nameof(Kinds))] public async Task ALogWaitMatchesLinesHereAndChecksFailuresFirst(string kind)
    {
        ProcessExit Feed(ProcessCall call, params string[] lines)
        {
            foreach (string line in lines) if (call.Lines!(line)) return new ProcessExit(ProcessEnd.Stopped, -1, "", "", TimeSpan.FromSeconds(1));
            return new ProcessExit(ProcessEnd.TimedOut, -1, "", "", TimeSpan.FromSeconds(3));
        }
        var fake = new FakeLauncher()
            .Reply(call => Feed(call, "\uFEFFbooting\r", "Command server listening on 5577\r", "later"))
            .Reply(call => Feed(call, "booting", "[Fatal] Could not load [MyMod]", "Command server listening"))
            .Reply(call => Feed(call, "booting", "still booting"));
        var host = Host(kind, fake);
        var success = new Regex("^Command server listening"); var failures = new[] { new Regex(@"^\[Fatal") };
        var matched = await host.WaitForLogAsync("/srv/run/BepInEx/LogOutput.log", 120, success, failures, Timeout);
        Assert.Equal(HostLogOutcome.Matched, matched.Outcome); Assert.Equal("Command server listening on 5577", matched.EnsureMatched());
        string script = FakeLauncher.Script(fake.Calls[0]);
        Assert.Contains("offset='120'", script); Assert.Contains("seconds='60'", script);

        var failed = await host.WaitForLogAsync("/srv/run/BepInEx/LogOutput.log", 0, success, failures, Timeout);
        Assert.Equal(HostLogOutcome.FailureMatched, failed.Outcome); Assert.Equal("[Fatal] Could not load [MyMod]", failed.Line);
        Assert.Throws<WaitFailedException>(() => failed.EnsureMatched());

        var expired = await host.WaitForLogAsync("/srv/run/BepInEx/LogOutput.log", 0, success, failures, Timeout);
        Assert.Equal(HostLogOutcome.TimedOut, expired.Outcome); Assert.Equal("still booting", expired.LastLine);
        Assert.Equal("still booting", Assert.Throws<WaitTimeoutException>(() => expired.EnsureMatched()).LastSeen);
    }

    [Fact] public async Task AFollowerThatEndsBeforeAMatchReportsItsTransport()
    {
        var fake = new FakeLauncher().Exits(255, "", "Connection closed by remote host\n");
        var error = await Assert.ThrowsAsync<HostOperationException>(() =>
            Host("ssh", fake).WaitForLogAsync("/srv/log.txt", 0, new Regex("ready"), null, Timeout));
        Assert.Equal(HostOutcome.TransportFailed, error.Outcome);
    }

    [Fact] public async Task ShippedFilesAreHashedHereAndCheckedOnTheHost()
    {
        using var source = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(source.Path, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(source.Path, "BepInEx", "plugins", "MyMod.dll"), "not really a dll");
        byte[]? uploaded = null;
        string? script = null;
        var fake = new FakeLauncher().Reply(call =>
        {
            var copy = new MemoryStream(); call.Upload!.CopyTo(copy); uploaded = copy.ToArray();
            script = FakeLauncher.Script(call);
            return FakeLauncher.Exit(0, "VT-SHIP shipped " + Convert.ToHexStringLower(SHA256.HashData(uploaded)) + "\n", FakeLauncher.Report(0));
        });
        var shipment = await Host("ssh", fake).ShipFilesAsync(source.Path, "/srv/runs/42/plugins", Timeout);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(uploaded!)), shipment.Sha256); Assert.Equal(uploaded!.Length, shipment.Bytes);
        Assert.Null(shipment.Commit);
        Assert.Contains("dest='/srv/runs/42/plugins'", script); Assert.Contains($"sha256='{shipment.Sha256}'", script);
        var names = new List<string>();
        using (var reader = new TarReader(new MemoryStream(uploaded!))) while (reader.GetNextEntry() is { } entry) names.Add(entry.Name.TrimEnd('/'));
        Assert.Contains("BepInEx/plugins/MyMod.dll", names);
    }

    [Fact] public void AShipVerdictOtherThanTheSentHashIsRefused()
    {
        HostResult Ok(string stdout) => new(HostOutcome.Exited, 0, stdout, "", TimeSpan.Zero, false);
        string sha = new('a', 64);
        Assert.Throws<IOException>(() => ScriptedGameHost.ReadShipVerdict(Ok("VT-SHIP hash " + new string('b', 64) + "\n"), "/d", sha, 1, null, null));
        Assert.Throws<InvalidOperationException>(() => ScriptedGameHost.ReadShipVerdict(Ok("VT-SHIP exists\n"), "/d", sha, 1, null, null));
        Assert.Throws<HostOperationException>(() => ScriptedGameHost.ReadShipVerdict(Ok("VT-SHIP shipped " + new string('b', 64) + "\n"), "/d", sha, 1, null, null));
        Assert.Throws<HostOperationException>(() => ScriptedGameHost.ReadShipVerdict(new(HostOutcome.Unknown, null, "", "", TimeSpan.Zero, true), "/d", sha, 1, null, null));
        Assert.Equal("c1", ScriptedGameHost.ReadShipVerdict(Ok("VT-SHIP shipped " + sha + "\n"), "/d", sha, 1, "c1", "t1").Commit);
    }

    [Theory, InlineData(true), InlineData(false)] public async Task FetchedEvidenceIsExtractedOnlyWhenItsHashMatches(bool intact)
    {
        using var evidence = new TempDirectory();
        File.WriteAllText(Path.Combine(evidence.Path, "LogOutput.log"), "[Info] ready\n");
        var tar = new MemoryStream();
        await TarFile.CreateFromDirectoryAsync(evidence.Path, tar, includeBaseDirectory: false);
        byte[] bytes = tar.ToArray();
        string reported = intact ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : new string('0', 64);
        var fake = new FakeLauncher().Reply(call =>
        {
            call.Output!.Write(bytes);
            return FakeLauncher.Exit(0, "", $"VT-FETCH {reported} {bytes.Length}\n" + FakeLauncher.Report(0));
        });
        using var target = new TempDirectory();
        string local = Path.Combine(target.Path, "evidence");
        if (intact)
        {
            var fetched = await Host("container", fake).FetchDirectoryAsync("/srv/runs/42/logs", local, Timeout);
            Assert.Equal(1, fetched.Files); Assert.Equal("[Info] ready\n", File.ReadAllText(Path.Combine(local, "LogOutput.log")));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => Host("container", fake).FetchDirectoryAsync("/srv/runs/42/logs", local, Timeout));
            Assert.False(Directory.Exists(local));
        }
    }

    [Fact] public async Task AnSshTunnelBindsLoopbackOnlyAndStopsItsOwnProcessOnDispose()
    {
        FakeForward? forward = null;
        var fake = new FakeLauncher { OnStart = arguments => forward = new FakeForward(arguments) }.Exits(0, "user tester\nhostname box.example\nport 22\n");
        using (var tunnel = await Host("ssh", fake).OpenCliTunnelAsync(5577, Timeout))
        {
            Assert.Contains("-G", fake.Calls.Single().Arguments);
            var arguments = fake.Started.Single().Arguments;
            Assert.Contains("ExitOnForwardFailure=yes", arguments); Assert.Contains("GatewayPorts=no", arguments); Assert.DoesNotContain("ClearAllForwardings=yes", arguments);
            Assert.Equal($"127.0.0.1:{tunnel.LocalPort}:127.0.0.1:5577", arguments[arguments.ToList().IndexOf("-L") + 1]);
            Assert.Equal("127.0.0.1", tunnel.Address); Assert.Equal(5577, tunnel.HostPort); Assert.True(tunnel.Forwarded);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort);
        }
        Assert.True(forward!.Stopped); Assert.True(forward.Disposed);
    }

    [Fact] public async Task AnSshTunnelRefusesALocalPortInUseBeforeStartingSsh()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0); busy.Start();
        try
        {
            var fake = new FakeLauncher { OnStart = arguments => new FakeForward(arguments) };
            int port = ((IPEndPoint)busy.LocalEndpoint).Port;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Host("ssh", fake).OpenCliTunnelAsync(5577, Timeout, port));
            Assert.Contains("already in use", error.Message); Assert.Empty(fake.Calls); Assert.Empty(fake.Started);
        }
        finally { busy.Stop(); }
    }

    [Fact] public async Task AnSshTunnelWhoseSshExitsFailsAtOnceAndIsCleanedUp()
    {
        FakeForward? forward = null;
        var fake = new FakeLauncher { OnStart = arguments => forward = new FakeForward(arguments, listen: false, exitCode: 255, stderr: "Permission denied (publickey).\n") }.Exits(0, "hostname box\n");
        var error = await Assert.ThrowsAsync<WaitFailedException>(() => Host("ssh", fake).OpenCliTunnelAsync(5577, Timeout));
        Assert.Contains("exited with code 255", error.Message); Assert.Equal("Permission denied (publickey).", error.LastSeen);
        Assert.True(forward!.Disposed);
    }

    [Theory]
    [InlineData("localforward 0.0.0.0:8080 [127.0.0.1]:80")]
    [InlineData("RemoteForward [0.0.0.0]:9000 [localhost]:9000")]
    [InlineData("dynamicforward 1080")]
    public async Task AnSshTunnelRefusesAHostWhoseConfigAddsForwards(string forward)
    {
        // ssh would open these beside the tunnel, possibly beyond loopback; ClearAllForwardings would clear the tunnel's own too.
        var fake = new FakeLauncher { OnStart = arguments => new FakeForward(arguments) }.Exits(0, "user tester\nhostname box.example\n" + forward + "\r\nport 22\n");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Host("ssh", fake).OpenCliTunnelAsync(5577, Timeout));
        Assert.Contains(forward, error.Message); Assert.Empty(fake.Started);
        var broken = new FakeLauncher { OnStart = arguments => new FakeForward(arguments) }.Exits(255, "", "Bad configuration option\n");
        Assert.Contains("Bad configuration option", (await Assert.ThrowsAsync<InvalidOperationException>(() => Host("ssh", broken).OpenCliTunnelAsync(5577, Timeout))).Message);
        Assert.Empty(broken.Started);
    }

    [Fact] public async Task LocalAndContainerHostsReachTheCliPortItselfOrRefuse()
    {
        var local = await Host("local", new FakeLauncher()).OpenCliTunnelAsync(5577, Timeout);
        Assert.Equal(5577, local.LocalPort); Assert.False(local.Forwarded);
        await Assert.ThrowsAsync<ArgumentException>(() => Host("local", new FakeLauncher()).OpenCliTunnelAsync(5577, Timeout, 6000));

        var fake = new FakeLauncher().Exits(0, "host\n").Exits(0, "bridge\n");
        var direct = await Host("container", fake).OpenCliTunnelAsync(5577, Timeout);
        Assert.Equal(5577, direct.LocalPort);
        Assert.Equal(new[] { "inspect", "--format", "{{.HostConfig.NetworkMode}}", "vt-server" }, fake.Calls[0].Arguments);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Host("container", fake).OpenCliTunnelAsync(5577, Timeout));
        Assert.Contains("--network host", error.Message);
    }

    [Fact] public void PowerShellClixmlOnStderrBecomesText()
    {
        string stderr = "#< CLIXML\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><S S=\"Error\">boom_x000D__x000A_</S><S S=\"Warning\">careful</S></Objs>\n";
        Assert.Equal("boom\nWARNING: careful\n", ScriptedGameHost.ReadableStderr(stderr));
    }
}

public class EnvironmentProfileTests
{
    private static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
    private static string Sample => """
        {
          // Comments are allowed.
          "hosts": {
            "linux-box": { "kind": "ssh", "platform": "linux", "shell": "bash", "destination": "tester@linux-box.example", "port": 2222,
                           "sshOptions": ["IdentityFile=~/.ssh/valheim_tests"], "lock": "/var/tmp/valheim-testing.lock" },
            "windows-pc": { "kind": "ssh", "platform": "windows", "shell": "powershell", "destination": "tester@windows-pc.example",
                            "lock": "C:\\ValheimTesting\\lock" },
            "server-ctr": { "kind": "container", "platform": "linux", "shell": "bash", "container": "valheim-server", "user": "valheim",
                            "lock": "/home/valheim/lock" }
          },
          "server": { "host": "linux-box", "install": "/opt/valheim/server", "runtime": "/srv/valheim-testing/runs", "cliPort": 5577, "gamePort": 2456 },
          "clients": {
            "player": { "host": "windows-pc", "install": "C:\\Games\\Valheim", "runtime": "D:/ValheimTesting/runs", "cliPort": 5578, "localCliPort": 15578 }
          }
        }
        """;

    [Fact] public void AProfileNamesTheHostOfTheServerAndOfEachClient()
    {
        var profile = EnvironmentProfile.Parse(Sample);
        var server = Assert.IsType<SshGameHost>(profile.CreateServerHost());
        Assert.Equal("tester@linux-box.example", server.Destination); Assert.Equal(2222, server.Port); Assert.Equal(HostShellKind.Bash, server.Shell.Kind);
        var client = Assert.IsType<SshGameHost>(profile.CreateClientHost("player"));
        Assert.Same(HostShell.WindowsPowerShell, client.Shell);
        var container = Assert.IsType<ContainerGameHost>(profile.CreateHost("server-ctr"));
        Assert.Equal("valheim-server", container.Container); Assert.Equal("valheim", container.User);
        Assert.Equal(15578, profile.Clients["player"].LocalCliPort);
    }

    [Fact] public void ALocalHostMustBeThisMachinesPlatform()
    {
        string json = """{ "hosts": { "here": { "kind": "local", "platform": "PLATFORM", "shell": "SHELL", "lock": "LOCK" } }, "clients": { "me": { "host": "here", "install": "INSTALL", "runtime": "RUNTIME", "cliPort": 5578 } } }""";
        string Fill(string platform) => json.Replace("PLATFORM", platform).Replace("SHELL", platform == "windows" ? "powershell" : "bash")
            .Replace("LOCK", platform == "windows" ? "C:/vt/lock" : "/tmp/vt/lock").Replace("INSTALL", platform == "windows" ? "C:/Games/Valheim" : "/games/valheim")
            .Replace("RUNTIME", platform == "windows" ? "C:/vt/runs" : "/tmp/vt/runs");
        Assert.IsType<LocalGameHost>(EnvironmentProfile.Parse(Fill(Platform)).CreateHost("here"));
        string other = Platform == "linux" ? "windows" : "linux";
        Assert.Throws<PlatformNotSupportedException>(() => EnvironmentProfile.Parse(Fill(other)).CreateHost("here"));
    }

    [Theory]
    [InlineData("\"server\": {", "\"extra\": 1, \"server\": {", "'extra'")]
    [InlineData("\"host\": \"linux-box\"", "\"host\": \"nowhere\"", "not listed")]
    [InlineData("\"platform\": \"linux\", \"shell\": \"bash\", \"destination\"", "\"platform\": \"macos\", \"shell\": \"bash\", \"destination\"", "no dedicated server")]
    [InlineData("\"host\": \"windows-pc\", \"install\": \"C:\\\\Games\\\\Valheim\", \"runtime\": \"D:/ValheimTesting/runs\", \"cliPort\": 5578",
                "\"host\": \"linux-box\", \"install\": \"/opt/valheim/client\", \"runtime\": \"/srv/vt/client\", \"cliPort\": 5577", "same ValheimCLI port 5577 on host 'linux-box'")]
    [InlineData("\"lock\": \"/var/tmp/valheim-testing.lock\"", "\"lock\": \"var/tmp/lock\"", "lock must be an absolute path")]
    [InlineData("\"lock\": \"C:\\\\ValheimTesting\\\\lock\"", "\"lock\": \"/ValheimTesting/lock\"", "lock must be an absolute path")]
    [InlineData("\"runtime\": \"/srv/valheim-testing/runs\"", "\"runtime\": \"/opt/valheim/server/runs\"", "outside the install")]
    [InlineData("\"runtime\": \"D:/ValheimTesting/runs\"", "\"runtime\": \"c:\\\\games\\\\valheim\\\\runs\"", "outside the install")]
    [InlineData("\"destination\": \"tester@linux-box.example\", ", "", "needs a destination")]
    [InlineData("\"shell\": \"powershell\"", "\"shell\": \"cmd\"", "shell must be")]
    [InlineData("\"kind\": \"container\", \"platform\": \"linux\", \"shell\": \"bash\"", "\"kind\": \"container\", \"platform\": \"linux\", \"shell\": \"powershell\"", "only on Windows")]
    [InlineData("\"gamePort\": 2456", "\"gamePort\": 0", "gamePort must be")]
    [InlineData("\"localCliPort\": 15578", "\"localCliPort\": 15578, \"gamePort\": 2458", "only the server has a gamePort")]
    [InlineData("\"user\": \"valheim\",", "\"user\": \"valheim\", \"port\": 22,", "belong to an ssh host")]
    [InlineData("\"platform\": \"windows\", \"shell\": \"powershell\"", "\"platform\": \"windows\", \"shell\": \"bash\"", "bash on Windows")]
    [InlineData("\"destination\": \"tester@linux-box.example\", \"port\": 2222", "\"destination\": \"ssh://tester@linux-box.example:2200\", \"port\": 2222", "give the port once")]
    [InlineData("\"destination\": \"tester@windows-pc.example\"", "\"destination\": \"tester:secret@windows-pc.example\"", "Passwords are never accepted")]
    [InlineData("[\"IdentityFile=~/.ssh/valheim_tests\"]", "[\"LocalForward=0.0.0.0:1 127.0.0.1:2\"]", "LocalForward is refused")]
    public void AnInconsistentProfileIsRefusedBeforeAnythingStarts(string find, string replace, string expected)
    {
        string json = Sample.Replace(find, replace);
        Assert.NotEqual(Sample, json);
        var error = Record.Exception(() => EnvironmentProfile.Parse(json));
        Assert.NotNull(error);
        Assert.Contains(expected, error.Message);
    }

    [Fact] public void ALocalHostAndAHostNetworkContainerNeverShareACliPort()
    {
        // Both are reached on this machine's own loopback, so one port would reach whichever answered first.
        string shell = Platform == "windows" ? "powershell" : "bash", root = Platform == "windows" ? "C:/vt" : "/tmp/vt";
        string json = $$"""
            { "hosts": { "here": { "kind": "local", "platform": "{{Platform}}", "shell": "{{shell}}", "lock": "{{root}}/lock" },
                         "ctr": { "kind": "container", "platform": "linux", "shell": "bash", "container": "vt-server", "lock": "/home/valheim/lock" },
                         "far": { "kind": "ssh", "platform": "linux", "shell": "bash", "destination": "far.example", "lock": "/tmp/lock" } },
              "server": { "host": "ctr", "install": "/opt/valheim/server", "runtime": "/home/valheim/runs", "cliPort": 5577, "gamePort": 2456 },
              "clients": { "me": { "host": "here", "install": "{{root}}/game", "runtime": "{{root}}/runs", "cliPort": CLIENTPORT },
                           "far": { "host": "far", "install": "/games/valheim", "runtime": "/tmp/runs", "cliPort": 5577, "localCliPort": FARPORT } } }
            """;
        Assert.Contains("server and client me would all be reached on local port 5577",
            Assert.Throws<ArgumentException>(() => EnvironmentProfile.Parse(json.Replace("CLIENTPORT", "5577").Replace("FARPORT", "0"))).Message);
        Assert.Contains("client me and client far would all be reached on local port 5578",
            Assert.Throws<ArgumentException>(() => EnvironmentProfile.Parse(json.Replace("CLIENTPORT", "5578").Replace("FARPORT", "5578"))).Message);
        EnvironmentProfile.Parse(json.Replace("CLIENTPORT", "5578").Replace("FARPORT", "15577"));
    }

    [Fact] public void TwoClientsNeverShareAHost()
    {
        string json = Sample.Replace("\"clients\": {", "\"clients\": { \"second\": { \"host\": \"windows-pc\", \"install\": \"C:/Games/Valheim\", \"runtime\": \"C:/vt/runs\", \"cliPort\": 5579 },");
        Assert.Contains("share host 'windows-pc'", Assert.Throws<ArgumentException>(() => EnvironmentProfile.Parse(json)).Message);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Path = Directory.CreateTempSubdirectory("vt-host-test-").FullName;
    public string Path { get; }
    public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
