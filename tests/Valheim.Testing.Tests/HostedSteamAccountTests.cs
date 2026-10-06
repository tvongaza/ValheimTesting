using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// A campaign client's Steam identity lease in the hosted runner: the client's account is leased (real lease scripts on this machine)
// before its host is touched, renewed while it runs and released after it stopped; without a pool the run is exactly as before.
public sealed partial class HostedServerRunTests
{
    private string Leases => Path.Combine(_root, "leases");
    private string PoolFile => Path.Combine(_root, "steam-accounts.json");
    private static readonly string[] LeaseSteps = ["lease a Steam account for client player", "release client player's Steam account lease"];

    private static object Accounts(bool check = false) => new { pool = "steam-accounts.json", leaseHost = LeaseBox.Name, checkSignedIn = check };

    // The server host, a Linux client host with an install, and the plan and profile.
    private (FakeServerHost Host, FakeOwnedServer Server, FakeServerHost Client, string Plan, string Profile) WithClient(object? steamAccounts, string mirror = "gpu", string server = "host")
    {
        var fixture = NewServer();
        var host = new FakeServerHost("linux-box", Path.Combine(_root, server), fixture);
        var client = new FakeServerHost("linux-gpu", Path.Combine(_root, mirror), tunnelPort: 15578);
        string install = client.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "core"));
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ClientLinuxExecutable), "client");
        FakeInstalls.LinuxLoader(install);
        var (plan, profile) = Write(host, withClient: true, steamAccounts: steamAccounts);
        return (host, fixture, client, plan, profile);
    }

    private static ScriptedTransport SignedInClientTransport(string id = LeaseBox.SteamId) =>
        new ScriptedTransport().On("cli_multiplayer_identity", _ => ScriptedTransport.Ok("OK: steamId=" + id + ", playFabLoginState=NotLoggedIn, playFabId=none, backend=Steam, gameState=main_menu, connectionStatus=None, isServer=False, isOpenServer=False, server="));

    private ClientRunPlan OwnedClient() => new() { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, LaunchArguments = ["+connect", "linux-box:2456"] };

    private static JsonElement ResultIn(string output) => JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement;
    private static List<string> StepNamesIn(string output) => ResultIn(output).GetProperty("Steps").EnumerateArray().Select(step => step.GetProperty("Name").GetString()!).ToList();

    [Fact] public async Task WithoutAPoolAProfileClientRunIsUnchangedAndAPoolOnlyAddsItsLease()
    {
        var plain = WithClient(null, "gpu-plain", "host-plain");
        string plainOutput = Path.Combine(_root, "out-plain");
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(plain.Profile), ["run", plain.Plan, plainOutput], Options(plain.Host, plain.Server, run =>
        {
            using (run.OpenClient(OwnedClient())) { }
            return Task.CompletedTask;
        }, plain.Client, new ScriptedTransport())));
        // Today's steps, exactly: nothing is leased and no account is recorded.
        Assert.Equal(new[] { "enough free disk space for the copies", "take the server host's lock", "copy and verify pinned runtime on the server host", "copy and verify pinned world", "ship and verify the world copy on the server host",
                "copied runtime has the plan's server executable", "copied runtime is the pinned game build, loader and patchers",
                "CLI port is free on the server host", "open the loopback CLI tunnel to the server host", "start and verify owned dedicated fixture", "stop only owned server",
                "fetch the server host's world copy", "remove the server host's runtime copy, keeping what the run changed", "close the CLI tunnel", "release client host linux-gpu's lock", "release the server host's lock", "scan run logs" }, StepNamesIn(plainOutput));
        var plainProvenance = ResultIn(plainOutput).GetProperty("Provenance").EnumerateObject().Select(entry => entry.Name).ToList();
        Assert.DoesNotContain(plainProvenance, name => name.StartsWith("steamAccount", StringComparison.Ordinal));

        LeaseBox.WritePool(_root, Leases);
        // The lease box is this machine, shared by this class's runs (one run id): start this run's journal there afresh.
        if (Directory.Exists(Path.Combine(LeaseBox.Journal, RunId))) Directory.Delete(Path.Combine(LeaseBox.Journal, RunId), recursive: true);
        var leased = WithClient(Accounts());
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(leased.Profile), ["run", leased.Plan, Output], Options(leased.Host, leased.Server, run =>
        {
            using (run.OpenClient(OwnedClient())) { }
            return Task.CompletedTask;
        }, leased.Client, new ScriptedTransport(), LeaseBox.Host())));
        // The same run with a pool: the same steps plus the lease and its release, the same client launch, and the account's name.
        Assert.Equal(StepNamesIn(plainOutput), StepNames().Where(step => !LeaseSteps.Contains(step)));
        Assert.Equal(LeaseSteps, StepNames().Where(step => LeaseSteps.Contains(step)));
        var provenance = Result().GetProperty("Provenance");
        Assert.Equal(plainProvenance, provenance.EnumerateObject().Select(entry => entry.Name).Where(name => !name.StartsWith("steamAccount", StringComparison.Ordinal)));
        Assert.Equal(LeaseBox.Account, provenance.GetProperty("steamAccount.player").GetString());
        Assert.Equal(LeaseBox.Pool, provenance.GetProperty("steamAccountPool.player").GetString());
        var start = Assert.Single(plain.Client.Runs, run => run.Script == "client-start").Variables;
        Assert.Equal(start, Assert.Single(leased.Client.Runs, run => run.Script == "client-start").Variables);
        Assert.Equal(plain.Client.Runs.Select(run => run.Script), leased.Client.Runs.Select(run => run.Script));
        Assert.Equal(SteamAccountState.Free, (await LeaseBox.StatusAsync(PoolFile)).State);
        // The lease host's journal (#257): the account held for the client, then released, with its holder.
        var journal = (await RunJournal.ReadAsync(LeaseBox.Host(), LeaseBox.Journal, RunId, TimeSpan.FromSeconds(30))).Where(record => record.Actor == "player").ToList();
        Assert.Equal([JournalEntry.LeaseHeld, JournalEntry.LeaseReleased], journal.Select(record => record.Entry.Kind));
        Assert.Equal(LeaseBox.Account, journal[0].Entry.Fields["account"]);
        Assert.Contains("client player", journal[0].Entry.Fields["owner"]);
    }

    [Fact] public async Task AProfileClientHoldsItsAccountWhileItRunsAndReleasesItAfterItStopped()
    {
        LeaseBox.WritePool(_root, Leases);
        var run = WithClient(Accounts());
        SteamAccountStatus? during = null, afterStop = null;
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, async context =>
        {
            using (var session = context.OpenClient(OwnedClient()))
            {
                Assert.Equal(LeaseBox.Account, session.Account!.Account);
                during = await LeaseBox.StatusAsync(PoolFile);
            }
            afterStop = await LeaseBox.StatusAsync(PoolFile); // Released only at teardown, after the client stopped.
        }, run.Client, new ScriptedTransport(), LeaseBox.Host())));
        Assert.Equal((SteamAccountState.Held, "toolkit-smoke run-test client player"), (during!.State, during.Holder));
        Assert.Equal(SteamAccountState.Held, afterStop!.State);
        Assert.Equal(SteamAccountState.Free, (await LeaseBox.StatusAsync(PoolFile)).State);
        var steps = StepNames().ToList();
        Assert.True(steps.IndexOf("release client player's Steam account lease") > steps.IndexOf("close the CLI tunnel"));
    }

    [Fact] public async Task AHeldAccountRefusesTheClientBeforeItsHostIsTouched()
    {
        LeaseBox.WritePool(_root, Leases);
        var run = WithClient(Accounts());
        await using var other = await TestEnvironment.Pool(File.ReadAllText(PoolFile)).AcquireAsync(LeaseBox.Host(), "another-runner run-x client player", LeaseBox.Timeout);
        bool opened = false;
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, context =>
        {
            using (context.OpenClient(OwnedClient())) opened = true;
            return Task.CompletedTask;
        }, run.Client, new ScriptedTransport(), LeaseBox.Host())));
        Assert.False(opened);
        var step = Step("lease a Steam account for client player");
        Assert.False(step.GetProperty("Passed").GetBoolean());
        Assert.Contains($"{LeaseBox.Account} is held by another-runner run-x client player", step.GetProperty("Error").GetString());
        // Nothing ran on the client's host and its lock was never taken; there is no lease of this run's to release.
        Assert.Empty(run.Client.Runs); Assert.Empty(run.Client.Claims);
        Assert.DoesNotContain("release client player's Steam account lease", StepNames());
        Assert.Equal("another-runner run-x client player", (await LeaseBox.StatusAsync(PoolFile)).Holder);
    }

    [Fact] public async Task ALostLeaseStopsTheClientAndFailsTheRun()
    {
        LeaseBox.WritePool(_root, Leases);
        var run = WithClient(Accounts());
        bool cancelled = false;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, async context =>
        {
            using var session = context.OpenClient(OwnedClient());
            LeaseBox.ReleaseBehindTheHoldersBack(Leases);
            try { await Task.Delay(LeaseBox.Timeout, context.Cancellation); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        }, run.Client, new ScriptedTransport(), LeaseBox.Host(), renewEvery: TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, code);
        Assert.True(cancelled);
        // The client was killed once, when the lease was lost; closing the session later stopped nothing more.
        var stop = Assert.Single(run.Client.Runs, entry => entry.Script == "stop");
        Assert.Equal("0", stop.Variables["quit"]);
        var release = Step("release client player's Steam account lease");
        Assert.False(release.GetProperty("Passed").GetBoolean());
        Assert.Contains("was lost during the run", release.GetProperty("Error").GetString());
        Assert.False(Result().GetProperty("Provenance").TryGetProperty("outcome", out _)); // A failure, not an unknown outcome.
    }

    [Fact] public async Task AClientThatFailsToStartStillReleasesItsLease()
    {
        LeaseBox.WritePool(_root, Leases);
        var run = WithClient(Accounts());
        run.Client.Failures["client-start"] = new HostResult(HostOutcome.Exited, 0, "VT-INTERACTIVE no-steam no Steam client runs as tester\n", "", TimeSpan.Zero, false);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, context =>
        {
            using (context.OpenClient(OwnedClient())) { }
            return Task.CompletedTask;
        }, run.Client, new ScriptedTransport(), LeaseBox.Host())));
        Assert.Contains("No Steam client", Result().GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == "runner failed").GetProperty("Error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Step("release client player's Steam account lease").GetProperty("Passed").GetBoolean());
        Assert.Equal(SteamAccountState.Free, (await LeaseBox.StatusAsync(PoolFile)).State);
    }

    [Fact] public async Task AClientWhoseStopIsUnprovenKeepsItsLease()
    {
        LeaseBox.WritePool(_root, Leases);
        if (Directory.Exists(Path.Combine(LeaseBox.Journal, RunId))) Directory.Delete(Path.Combine(LeaseBox.Journal, RunId), recursive: true);
        var run = WithClient(Accounts());
        Assert.Equal(3, await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, context =>
        {
            var session = context.OpenClient(OwnedClient());
            run.Client.Failures["stop"] = new HostResult(HostOutcome.Unknown, null, "", "", TimeSpan.FromSeconds(45), true);
            session.Dispose();
            return Task.CompletedTask;
        }, run.Client, new ScriptedTransport(), LeaseBox.Host())));
        // The client may still run on the account: the lease is kept, to end on its own, and the teardown says so.
        var release = Step("release client player's Steam account lease");
        Assert.False(release.GetProperty("Passed").GetBoolean());
        Assert.Contains("Kept the lease on Steam account vt_client_one", release.GetProperty("Error").GetString());
        var status = await LeaseBox.StatusAsync(PoolFile);
        Assert.Equal((SteamAccountState.Held, "toolkit-smoke run-test client player"), (status.State, status.Holder));
        // The lease host's journal says it was kept, and why: never released while its client may run.
        var journal = (await RunJournal.ReadAsync(LeaseBox.Host(), LeaseBox.Journal, RunId, TimeSpan.FromSeconds(30))).Where(record => record.Actor == "player").ToList();
        Assert.Equal([JournalEntry.LeaseHeld, JournalEntry.LeaseKept], journal.Select(record => record.Entry.Kind));
        Assert.Equal("its client may still run", journal[1].Entry.Fields["why"]);
    }

    [Theory]
    [InlineData("VT-STEAMUSER id 76561197960265729", null)]
    [InlineData("VT-STEAMUSER account 1", null)]
    [InlineData("VT-STEAMUSER id 76561197960265730", "reported another account than vt_client_one")]
    [InlineData("VT-STEAMUSER none", "account check on linux-gpu found no account")]
    [InlineData("VT-STEAMUSER unreadable the host user has no loginusers.vdf in its Steam directories", "is inconclusive (the host user has no loginusers.vdf")]
    [InlineData("", "is inconclusive (unexpected reply")]
    public async Task TheSignedInCheckPassesOnlyTheLeasedAccount(string reply, string? refusal)
    {
        LeaseBox.WritePool(_root, Leases);
        var run = WithClient(Accounts(check: true));
        run.Client.SteamUserReply = reply + "\n";
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, context =>
        {
            using (context.OpenClient(OwnedClient())) { }
            return Task.CompletedTask;
        }, run.Client, SignedInClientTransport(), LeaseBox.Host()));
        var check = Step("client player's host is signed in to Steam account vt_client_one (signed-in check)");
        Assert.Equal("steam-user", run.Client.Runs[0].Script); // Before anything else on the client's host.
        if (refusal == null)
        {
            Assert.Equal(0, code); Assert.True(check.GetProperty("Passed").GetBoolean());
            Assert.Single(run.Client.Runs, entry => entry.Script == "client-start");
        }
        else
        {
            Assert.Equal(1, code);
            Assert.Contains(refusal, check.GetProperty("Error").GetString());
            Assert.Contains("Refused before launch", check.GetProperty("Error").GetString());
            Assert.DoesNotContain(run.Client.Runs, entry => entry.Script is "client-start" or "move-aside");
        }
        Assert.DoesNotContain("7656119", File.ReadAllText(Path.Combine(Output, "result.json")));
        Assert.True(Step("release client player's Steam account lease").GetProperty("Passed").GetBoolean());
        Assert.Equal(SteamAccountState.Free, (await LeaseBox.StatusAsync(PoolFile)).State);
    }

    [Theory]
    [InlineData("76561197960265730")]
    [InlineData("none")]
    public async Task RunningIdentityMismatchStopsTheOwnedClientBeforeTheScenario(string gameId)
    {
        LeaseBox.WritePool(_root, Leases);
        var run = WithClient(Accounts(check: true));
        run.Client.SteamUserReply = "VT-STEAMUSER id " + LeaseBox.SteamId + "\n";
        bool exercised = false;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, context =>
        {
            using var session = context.OpenClient(OwnedClient());
            exercised = true;
            return Task.CompletedTask;
        }, run.Client, SignedInClientTransport(gameId), LeaseBox.Host()));
        Assert.Equal(1, code);
        Assert.False(exercised);
        Assert.Contains(run.Client.Runs, entry => entry.Script == "stop");
        Assert.Equal(SteamAccountState.Free, (await LeaseBox.StatusAsync(PoolFile)).State);
        Assert.DoesNotContain("7656119", File.ReadAllText(Path.Combine(Output, "result.json")));
    }

    [Fact] public async Task AnAttachedProfileClientLeasesItsAccountAndIsNeverStarted()
    {
        LeaseBox.WritePool(_root, Leases);
        var run = WithClient(Accounts());
        var transport = new ScriptedTransport();
        SteamAccountStatus? during = null;
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, async context =>
        {
            using (var session = context.OpenClient(new ClientRunPlan { Mode = "attach", Port = 15578, Pinning = "none" }))
            {
                Assert.False(session.Owned);
                during = await LeaseBox.StatusAsync(PoolFile);
            }
        }, run.Client, transport, LeaseBox.Host())));
        Assert.Equal(SteamAccountState.Held, during!.State);
        Assert.Empty(run.Client.Runs); // Attached: nothing starts or stops on the operator's host.
        Assert.True(transport.Disposed);
        Assert.Equal(LeaseSteps, StepNames().Where(step => LeaseSteps.Contains(step)));
        Assert.Equal(SteamAccountState.Free, (await LeaseBox.StatusAsync(PoolFile)).State);
    }

    // Canary: an unrelated environment secret and the account's SteamID reach no report, evidence file or host script;
    // the account's name does. Negative control: the same scan finds the canary once the run is given it as its name.
    [Fact] public async Task NeitherAnUnrelatedSecretNorASteamIdReachesAReportOrAHost()
    {
        string variable = "VT_TEST_CANARY_" + Guid.NewGuid().ToString("N");
        string canary = "canary" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, canary);
        try
        {
            LeaseBox.WritePool(_root, Leases, [new { name = LeaseBox.Account, steamId = LeaseBox.SteamId }]);
            var run = WithClient(Accounts(check: true));
            run.Client.SteamUserReply = "VT-STEAMUSER id " + LeaseBox.SteamId + "\n";
            Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, context =>
            {
                using (context.OpenClient(OwnedClient())) { }
                return Task.CompletedTask;
            }, run.Client, SignedInClientTransport(), LeaseBox.Host())));
            var seen = Seen(Output, run);
            Assert.Contains(seen, text => text.Contains(LeaseBox.Account, StringComparison.Ordinal));
            foreach (string text in seen) { Assert.DoesNotContain(canary, text); Assert.DoesNotContain(LeaseBox.SteamId, text); }

            var control = WithClient(Accounts(check: true), "gpu-control", "host-control");
            control.Client.SteamUserReply = "VT-STEAMUSER id " + LeaseBox.SteamId + "\n";
            string controlOutput = Path.Combine(_root, "out-control");
            Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(control.Profile), ["run", control.Plan, controlOutput], Options(control.Host, control.Server, context =>
            {
                using (context.OpenClient(OwnedClient())) { }
                return Task.CompletedTask;
            }, control.Client, SignedInClientTransport(), LeaseBox.Host(), name: "toolkit-" + canary)));
            Assert.Contains(Seen(controlOutput, control), text => text.Contains(canary, StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }

        // Every evidence file, every script and variable sent to a game host, and the lease files other runs read.
        List<string> Seen(string output, (FakeServerHost Host, FakeOwnedServer Server, FakeServerHost Client, string Plan, string Profile) hosts)
        {
            var texts = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
            foreach (var host in new[] { hosts.Host, hosts.Client })
                texts.AddRange(host.Runs.Select(entry => entry.Script + " " + string.Join(" ", entry.Variables.Select(pair => pair.Key + "=" + pair.Value))));
            texts.AddRange(Directory.GetFiles(Leases, "*", SearchOption.AllDirectories).Select(file => file + " " + File.ReadAllText(file)));
            return texts;
        }
    }
}
