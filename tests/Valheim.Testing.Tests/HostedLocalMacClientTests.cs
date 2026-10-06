using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed partial class HostedServerRunTests
{
    private (ResolvedEnvironment Profile, ServerRunPlan Server, ClientRunPlan Client) LocalMacProfile(FakeServerHost serverHost, bool local = true)
    {
        var (planPath, profilePath) = Write(serverHost);
        var profile = TestEnvironment.Read(profilePath);
        string install = Path.Combine(_root, "mac-install");
        profile.Hosts["mac"] = new HostProfile
        {
            Kind = local ? "local" : "ssh", Platform = "macos", Shell = "bash", Lock = Path.Combine(_root, "mac.lock"),
            Destination = local ? null : "tester@mac.example",
        };
        profile.Clients["mac"] = new GameRole { Host = "mac", Install = install, Runtime = Path.Combine(_root, "mac-runs"), CliPort = 5578 };
        profile.Validate();
        var client = new ClientRunPlan
        {
            Mode = "owned", Install = install, Host = "127.0.0.1", Port = 5578, Join = "127.0.0.1:2456", Character = "Tester",
            Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["example.mymod"] = "absent" },
            InstallPins = new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) },
        };
        return (profile, ServerRunPlan.Read<ServerRunPlan>(planPath), client);
    }

    [Fact]
    public async Task LocalMacProfileLaunchKeepsTheExactProcessAndReleasesItsHostLock()
    {
        if (!OperatingSystem.IsMacOS()) return; // A local macOS role needs macOS path and desktop semantics.
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        client.Architecture = "arm64";
        var process = new FakeOwnedProcess(91); bool launched = false;
        var hosted = HostedServerRun.Create(profile, server, "test", new FakeRunHooks
        {
            RunId = RunId, Host = name => name == "mac" ? macHost : serverHost,
            RequireMacGui = () => { },
            LocalMacLaunch = (plan, output, account, cancellation, processStarted) =>
            {
                launched = true;
                processStarted?.Invoke(process);
                return ClientSession.Launch(plan, output, () => process, () => new ScriptedTransport(), (_, _) => Task.CompletedTask,
                    cancellation, null, null, account);
            },
        });
        Directory.CreateDirectory(Output);
        var session = hosted.OpenClient(new ScenarioReport("mac"), Output, client, "mac", CancellationToken.None);
        Assert.True(launched); Assert.Equal(91, session.ProcessId);
        session.Dispose();
        Assert.Equal(1, process.Stops);
        Assert.Empty(await hosted.TeardownAsync(new ScenarioReport("teardown"), Output, launched: false, serverStopped: true));
        Assert.Equal(macHost.Claims.Count, macHost.Releases.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RemoteOrLockedMacRefusesBeforeClientHostIsTouched(bool local, bool locked)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost, local);
        bool launched = false;
        var hosted = HostedServerRun.Create(profile, server, "test", new FakeRunHooks
        {
            RunId = RunId, Host = name => name == "mac" ? macHost : serverHost,
            RequireMacGui = () => { if (locked) throw new InvalidOperationException("locked Mac"); },
            LocalMacLaunch = (_, _, _, _, _) => { launched = true; throw new InvalidOperationException("must not start"); },
        });
        Directory.CreateDirectory(Output);
        Assert.ThrowsAny<Exception>(() => hosted.OpenClient(new ScenarioReport("mac"), Output, client, "mac", CancellationToken.None));
        Assert.False(launched); Assert.Empty(macHost.Claims); Assert.Empty(macHost.Runs);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("port")]
    [InlineData("host")]
    public void LocalMacRoleAndPlanMustDescribeTheSameOwnedClient(string mismatch)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        if (mismatch == "install") client.Install = Path.Combine(_root, "some-other-install");
        if (mismatch == "port") client.Port++;
        if (mismatch == "host") client.Host = "remote.example";
        bool launched = false;
        var hosted = HostedServerRun.Create(profile, server, "test", new FakeRunHooks
        {
            RunId = RunId, Host = name => name == "mac" ? macHost : serverHost,
            RequireMacGui = () => { },
            LocalMacLaunch = (_, _, _, _, _) => { launched = true; throw new InvalidOperationException("must not start"); },
        });
        Directory.CreateDirectory(Output);
        Assert.Throws<ArgumentException>(() => hosted.OpenClient(new ScenarioReport("mac"), Output, client, "mac", CancellationToken.None));
        Assert.False(launched); Assert.Empty(macHost.Claims);
    }

    [Fact]
    public async Task WrongSignedInAccountRefusesBeforeTheMacClientLaunchesAndReleasesItsLease()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        profile.SteamAccounts = new SteamAccountsProfile
        {
            LeaseHost = "lease-box", CheckSignedIn = true,
            Accounts = new SteamAccountPool
            {
                Pool = "mac-profile-test", LeaseDirectory = Path.Combine(_root, "leases"),
                Accounts = [new SteamPoolAccount { Name = "test_mac", Host = "mac", SteamId = LeaseBox.SteamId }],
            },
        };
        profile.Hosts["lease-box"] = new HostProfile
        {
            Kind = "local", Platform = HostProfile.CurrentPlatform, Shell = LeaseBox.Shell, Lock = Path.Combine(_root, "lease.lock"),
        };
        profile.Validate();
        bool launched = false;
        var hosted = HostedServerRun.Create(profile, server, "test", new FakeRunHooks
        {
            RunId = RunId, Host = name => name switch { "mac" => macHost, "lease-box" => LeaseBox.Host(), _ => serverHost },
            RequireMacGui = () => { },
            LocalMacLaunch = (_, _, _, _, _) => { launched = true; throw new InvalidOperationException("must not start"); },
        });
        Directory.CreateDirectory(Output);
        var error = Assert.Throws<SteamSignedInException>(() => hosted.OpenClient(new ScenarioReport("mac"), Output, client, "mac", CancellationToken.None));
        Assert.Equal(SteamSignedInState.Unknown, error.State);
        Assert.False(launched); Assert.Empty(macHost.Claims);
        Assert.Empty(await hosted.TeardownAsync(new ScenarioReport("teardown"), Output, launched: false, serverStopped: true));
        var status = await profile.SteamAccounts.Accounts!.ListAsync(LeaseBox.Host(), LeaseBox.Timeout);
        Assert.Equal(SteamAccountState.Free, Assert.Single(status).State);
    }

    [Fact]
    public async Task UnprovenLocalMacStopKeepsTheClientHostLock()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        var process = new FakeOwnedProcess(91) { StopFailure = () => new IOException("stop could not be proven") };
        var hosted = HostedServerRun.Create(profile, server, "test", new FakeRunHooks
        {
            RunId = RunId, Host = name => name == "mac" ? macHost : serverHost,
            RequireMacGui = () => { },
            LocalMacLaunch = (plan, output, account, cancellation, processStarted) =>
            {
                processStarted?.Invoke(process);
                return ClientSession.Launch(plan, output, () => process, () => new ScriptedTransport(), (_, _) => Task.CompletedTask,
                    cancellation, null, null, account);
            },
        });
        Directory.CreateDirectory(Output);
        var session = hosted.OpenClient(new ScenarioReport("mac"), Output, client, "mac", CancellationToken.None);
        session.QuitTimeout = TimeSpan.Zero;
        Assert.Throws<IOException>(session.Dispose);
        var failures = await hosted.TeardownAsync(new ScenarioReport("teardown"), Output, launched: false, serverStopped: true);
        Assert.Contains(failures, failure => failure is HostLockException);
        Assert.Single(macHost.Claims);
        Assert.Empty(macHost.Releases);
    }

    [Fact]
    public async Task FailedLocalMacStartupReleasesTheLockOnlyAfterItsProcessIsProvenStopped()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        var process = new FakeOwnedProcess(91);
        var hosted = HostedServerRun.Create(profile, server, "test", new FakeRunHooks
        {
            RunId = RunId, Host = name => name == "mac" ? macHost : serverHost,
            RequireMacGui = () => { },
            LocalMacLaunch = (_, _, _, _, processStarted) =>
            {
                processStarted?.Invoke(process);
                process.Stop(TimeSpan.Zero); // The real launch stops its process after a failed startup.
                throw new InvalidOperationException("menu was not reached");
            },
        });
        Directory.CreateDirectory(Output);
        Assert.Throws<InvalidOperationException>(() => hosted.OpenClient(new ScenarioReport("mac"), Output, client, "mac", CancellationToken.None));
        Assert.True(process.HasExited);
        Assert.Empty(await hosted.TeardownAsync(new ScenarioReport("teardown"), Output, launched: false, serverStopped: true));
        Assert.Equal(macHost.Claims, macHost.Releases);
    }
}
