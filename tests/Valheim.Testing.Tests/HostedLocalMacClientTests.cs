using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed partial class HostedServerRunTests
{
    private sealed class MacProcess : IServerProcess
    {
        private readonly TaskCompletionSource<int> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 91;
        public int Stops { get; private set; }
        public bool StopIsUnproven { get; set; }
        public bool HasExited => _ended.Task.IsCompleted;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => _ended.Task.WaitAsync(cancellation);
        public void Stop(TimeSpan timeout)
        {
            Stops++;
            if (StopIsUnproven) throw new IOException("stop could not be proven");
            _ended.TrySetResult(-1);
        }
        public void Dispose() { }
    }

    private (EnvironmentProfile Profile, ServerRunPlan Server, ClientRunPlan Client) LocalMacProfile(FakeServerHost serverHost, bool local = true)
    {
        var (planPath, profilePath) = Write(serverHost);
        var profile = EnvironmentProfile.Read(profilePath);
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
            InstallPins = new() { Game = new string('c', 64), BepInExCore = new string('d', 64), Patchers = new string('e', 64) },
        };
        return (profile, ServerRunPlan.Read<ServerRunPlan>(planPath), client);
    }

    [Fact]
    public async Task LocalMacProfileLaunchKeepsTheExactProcessAndReleasesItsHostLock()
    {
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        client.Architecture = "arm64";
        var process = new MacProcess(); bool launched = false;
        var hosted = HostedServerRun.Create(profile, server, "test", new HostedSeams
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
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost, local);
        bool launched = false;
        var hosted = HostedServerRun.Create(profile, server, "test", new HostedSeams
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
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        if (mismatch == "install") client.Install = Path.Combine(_root, "some-other-install");
        if (mismatch == "port") client.Port++;
        if (mismatch == "host") client.Host = "remote.example";
        bool launched = false;
        var hosted = HostedServerRun.Create(profile, server, "test", new HostedSeams
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
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        profile.SteamAccounts = new SteamAccountsProfile
        {
            Pool = "fake-accounts.json", LeaseHost = "lease-box", CheckSignedIn = true,
            Accounts = new SteamAccountPool
            {
                Pool = "mac-profile-test", LeaseDirectory = Path.Combine(_root, "leases"), SteamGuard = SteamAccountPool.SignedIn,
                Accounts = [new SteamPoolAccount { Name = "test_mac", Host = "mac", SteamId = LeaseBox.SteamId }],
            },
        };
        profile.Hosts["lease-box"] = new HostProfile
        {
            Kind = "local", Platform = HostProfile.CurrentPlatform, Shell = LeaseBox.Shell, Lock = Path.Combine(_root, "lease.lock"),
        };
        profile.Validate();
        bool launched = false;
        var hosted = HostedServerRun.Create(profile, server, "test", new HostedSeams
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
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        var process = new MacProcess { StopIsUnproven = true };
        var hosted = HostedServerRun.Create(profile, server, "test", new HostedSeams
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
        var serverHost = NewHost(); var macHost = new FakeServerHost("mac", Path.Combine(_root, "mac-mirror"), kind: GameHostKind.Local);
        var (profile, server, client) = LocalMacProfile(serverHost);
        var process = new MacProcess();
        var hosted = HostedServerRun.Create(profile, server, "test", new HostedSeams
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
