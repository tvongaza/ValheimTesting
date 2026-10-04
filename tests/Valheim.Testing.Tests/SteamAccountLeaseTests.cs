using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Xunit;

// Account pools and leases with fake processes: what a pool may hold, what the lease host is sent, and how a claim, a
// renewal and a release are read, including lost replies. The contention, expiry and release checks run through real shells
// below. Names only: a credential never appears in anything this API writes, sends or reports.
public class SteamAccountPoolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string Sample = """
        {
          // Names only.
          "pool": "valheim-clients",
          "leaseDirectory": "/var/tmp/valheim-testing/leases",
          "leaseMinutes": 90,
          "accounts": [
            { "name": "vt_client_one", "host": "gaming-pc" },
            { "name": "vt_client_two" }
          ]
        }
        """;

    private static ScriptedGameHost Host(FakeLauncher fake) => new SshGameHost("lease-box", "tester@lease-box.example", HostShell.Bash, 0, null, null, "ssh", fake);
    private static string Reply(params string[] lines) => string.Join('\n', lines) + "\n";

    [Fact] public void APoolNamesAccounts()
    {
        var pool = TestEnvironment.Pool(Sample);
        Assert.Equal("valheim-clients", pool.Pool); Assert.Equal(90, pool.LeaseMinutes);
        Assert.Equal(new[] { "vt_client_one", "vt_client_two" }, pool.Accounts.Select(account => account.Name));
        Assert.Equal("gaming-pc", pool.Accounts[0].Host);
    }

    [Theory]
    [InlineData("\"vt_client_two\"", "\"user:password\"", "must be a Steam account name")]
    [InlineData("\"vt_client_two\"", "\"VT_CLIENT_ONE\"", "listed twice")]
    [InlineData("\"leaseMinutes\": 90", "\"leaseMinutes\": 0", "leaseMinutes must be")]
    [InlineData("\"/var/tmp/valheim-testing/leases\"", "\"leases\"", "leaseDirectory must be")]
    public void AnInvalidPoolIsRefused(string find, string replace, string expected)
    {
        string json = Sample.Replace(find, replace);
        Assert.NotEqual(Sample, json);
        var error = Record.Exception(() => TestEnvironment.Pool(json));
        Assert.NotNull(error);
        Assert.Contains(expected, error.Message);
    }

    [Fact] public async Task AClaimSendsNamesOnlyAndReadsTheLease()
    {
        var pool = TestEnvironment.Pool(Sample);
        var fake = new FakeLauncher().Exits(0, Reply("VT-LEASE claimed vt_client_two 7 1790000000"), FakeLauncher.Report(0));
        var lease = await pool.AcquireAsync(Host(fake), "run-42 on ci", Timeout, clientHost: "linux-gpu");
        Assert.Equal("vt_client_two", lease.Account); Assert.Equal("valheim-clients", lease.Pool); Assert.Equal("run-42 on ci", lease.Owner);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), lease.ExpiresUtc); Assert.Equal(TimeSpan.FromMinutes(90), lease.LeaseTime);
        Assert.Matches("^[0-9a-f]{32}$", lease.LeaseId);
        Assert.Equal("lease-box", lease.LeaseHostName);
        string script = FakeLauncher.Script(fake.Calls[0]);
        // Only the accounts for that host (or any host) are offered.
        Assert.Contains("accounts='vt_client_two'", script);
        Assert.Contains("action='claim'", script); Assert.Contains("owner='run-42 on ci'", script); Assert.Contains("seconds='5400'", script);
        Assert.Contains("directory='/var/tmp/valheim-testing/leases'", script); Assert.Contains("pool='valheim-clients'", script);
        Assert.Contains("lease='" + lease.LeaseId + "'", script);

        fake.Exits(0, Reply("VT-LEASE claimed vt_client_one 1 1790000000"), FakeLauncher.Report(0));
        await pool.AcquireAsync(Host(fake), "run-43", Timeout, clientHost: "gaming-pc");
        Assert.Contains("accounts='vt_client_one\nvt_client_two'", FakeLauncher.Script(fake.Calls[1]));
        await Assert.ThrowsAsync<ArgumentException>(() => TestEnvironment.Pool(Sample.Replace("{ \"name\": \"vt_client_two\" }", "{ \"name\": \"vt_client_two\", \"host\": \"x\" }"))
            .AcquireAsync(Host(fake), "run-44", Timeout, clientHost: "elsewhere"));
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact] public async Task WhenEveryAccountIsHeldTheHoldersAreReported()
    {
        var fake = new FakeLauncher().Exits(0, Reply("VT-LEASE none", "held vt_client_one 1790000000 run-41 on another machine", "taken vt_client_two"), FakeLauncher.Report(0));
        var error = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => TestEnvironment.Pool(Sample).AcquireAsync(Host(fake), "run-42", Timeout));
        Assert.Equal(SteamAccountLeaseState.NoneFree, error.State);
        Assert.Equal(new[] { SteamAccountState.Held, SteamAccountState.Contended }, error.Accounts.Select(account => account.State));
        Assert.Equal("run-41 on another machine", error.Accounts[0].Holder);
        Assert.Contains("vt_client_one is held by run-41 on another machine", error.Message);
    }

    [Theory, InlineData(true), InlineData(false)] public async Task AnUnprovenClaimIsUnknownAndExpiresOnItsOwn(bool timedOut)
    {
        var fake = timedOut ? new FakeLauncher().TimesOut() : new FakeLauncher().Exits(0, Reply("VT-LEASE claimed someone_else 1 1"), FakeLauncher.Report(0));
        var error = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => TestEnvironment.Pool(Sample).AcquireAsync(Host(fake), "run-42", Timeout, leaseTime: TimeSpan.FromMinutes(5)));
        Assert.Equal(SteamAccountLeaseState.Unknown, error.State);
        Assert.Contains("expires after 300.0 s", error.Message);
    }

    [Fact] public async Task RenewingAndReleasingNeedTheLeasesOwnClaim()
    {
        var pool = TestEnvironment.Pool(Sample);
        var fake = new FakeLauncher()
            .Exits(0, Reply("VT-LEASE claimed vt_client_one 3 1790000000"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-LEASE renewed 1790005400"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-LEASE released"), FakeLauncher.Report(0));
        await using (var lease = await pool.AcquireAsync(Host(fake), "run-42", Timeout))
        {
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790005400), await lease.RenewAsync(Timeout));
            string renew = FakeLauncher.Script(fake.Calls[1]);
            Assert.Contains("action='renew'", renew); Assert.Contains("account='vt_client_one'", renew); Assert.Contains("number='3'", renew);
            Assert.Contains("lease='" + lease.LeaseId + "'", renew);
        }
        Assert.Contains("action='release'", FakeLauncher.Script(fake.Calls[2]));

        // A lapsed lease: renewal says to stop, and its release is already done.
        fake.Exits(0, Reply("VT-LEASE claimed vt_client_one 4 1790000000"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-LEASE lost taken"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-LEASE lost taken"), FakeLauncher.Report(0));
        await using (var lapsed = await pool.AcquireAsync(Host(fake), "run-43", Timeout))
            Assert.Equal(SteamAccountLeaseState.Lost, (await Assert.ThrowsAsync<SteamAccountLeaseException>(() => lapsed.RenewAsync(Timeout))).State);

        // An unproven release is a failed teardown; the handle acts only once.
        fake.Exits(0, Reply("VT-LEASE claimed vt_client_one 5 1790000000"), FakeLauncher.Report(0)).TimesOut();
        var held = await pool.AcquireAsync(Host(fake), "run-44", Timeout);
        var error = await Assert.ThrowsAsync<SteamAccountLeaseException>(async () => await held.DisposeAsync());
        Assert.Equal(SteamAccountLeaseState.Unknown, error.State);
        Assert.Equal(SteamAccountLeaseState.Released, (await held.ReleaseAsync()).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => held.RenewAsync(Timeout));
        Assert.Equal(8, fake.Calls.Count);
    }

    [Fact] public async Task TheReportRecordsTheAccountsNameAndNeverAnUnrelatedSecret()
    {
        string variable = "VT_TEST_CANARY_" + Guid.NewGuid().ToString("N");
        string canary = "canary-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, canary);
        try
        {
            var pool = TestEnvironment.Pool(Sample);
            var fake = new FakeLauncher()
                .Exits(0, Reply("VT-LEASE claimed vt_client_two 1 1790000000"), FakeLauncher.Report(0))
                .Exits(0, Reply("VT-LEASE renewed 1790000100"), FakeLauncher.Report(0))
                .Exits(0, Reply("VT-LEASE none", "held vt_client_one 1790000000 run-7", "held vt_client_two 1790000000 run-42"), FakeLauncher.Report(0))
                .Exits(0, Reply("VT-LEASE released"), FakeLauncher.Report(0));
            var report = new ScenarioReport("lease");
            var seen = new List<string>();
            await using (var lease = await pool.AcquireAsync(Host(fake), "run-42", Timeout))
            {
                lease.Record(report, "player");
                await lease.RenewAsync(Timeout);
                seen.Add((await Assert.ThrowsAsync<SteamAccountLeaseException>(() => pool.AcquireAsync(Host(fake), "run-43", Timeout))).ToString());
                seen.Add(lease.ToString());
            }
            Assert.Equal("vt_client_two", report.Provenance["steamAccount.player"]);
            Assert.Equal("valheim-clients", report.Provenance["steamAccountPool.player"]);
            using var output = new TempDirectory();
            report.Write(output.Path);
            seen.AddRange(Directory.GetFiles(output.Path).Select(File.ReadAllText));
            seen.AddRange(fake.Calls.Select(FakeLauncher.Script));
            seen.AddRange(fake.Calls.Select(call => string.Join(' ', call.Arguments)));
            foreach (string text in seen) Assert.DoesNotContain(canary, text);
            Assert.Contains(seen, text => text.Contains("vt_client_two"));

            // Negative control: a value the caller does pass (here the owner) is found by the same scan.
            fake.Exits(0, Reply("VT-LEASE claimed vt_client_one 2 1790000000"), FakeLauncher.Report(0));
            await pool.AcquireAsync(Host(fake), "owner " + canary, Timeout);
            Assert.Contains(canary, FakeLauncher.Script(fake.Calls[^1]));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
}

// The lease scripts through every local shell this machine has, and over ssh and into a container in the game-hosts job: two
// runs never hold one account, a crashed holder's lease expires, a failed run releases its lease, and old claims are pruned.
internal static class LeaseChecks
{
    public static SteamAccountPool Pool(string root, int accounts) => new()
    {
        Pool = "ci-pool", LeaseDirectory = root,
        Accounts = Enumerable.Range(1, accounts).Select(i => new SteamPoolAccount { Name = "vt_client_" + i }).ToList(),
    };

    private static async Task<(SteamAccountLease? Lease, SteamAccountLeaseException? Error)> TryAcquireAsync(SteamAccountPool pool, IGameHost host, string owner, TimeSpan? life = null)
    {
        try { return (await pool.AcquireAsync(host, owner, GameHostChecks.Generous, leaseTime: life), null); }
        catch (SteamAccountLeaseException error) { return (null, error); }
    }

    public static Task TwoRunsNeverHoldOneAccount(IGameHost host, string parent) => GameHostChecks.WithRootAsync(host, parent, async root =>
    {
        var pool = Pool(root, 3);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => TryAcquireAsync(pool, host, "run-" + i)));
        var leases = attempts.Where(attempt => attempt.Lease != null).Select(attempt => attempt.Lease!).ToList();
        Assert.Equal(3, leases.Count);
        Assert.Equal(3, leases.Select(lease => lease.Account).Distinct().Count());
        Assert.All(attempts.Where(attempt => attempt.Lease == null), attempt => Assert.Equal(SteamAccountLeaseState.NoneFree, attempt.Error!.State));
        var held = await pool.ListAsync(host, GameHostChecks.Generous);
        Assert.All(held, status => Assert.Equal(SteamAccountState.Held, status.State));
        Assert.Equal(leases.Select(lease => lease.Owner).Order(), held.Select(status => status.Holder!).Order());
        foreach (var lease in leases) Assert.Equal(SteamAccountLeaseState.Released, (await lease.ReleaseAsync()).State);
        Assert.All(await pool.ListAsync(host, GameHostChecks.Generous), status => Assert.Equal(SteamAccountState.Free, status.State));
    });

    public static Task ACrashedHoldersLeaseExpires(IGameHost host, string parent) => GameHostChecks.WithRootAsync(host, parent, async root =>
    {
        var pool = Pool(root, 1);
        // The holder "crashes": it never renews or releases. Its lease outlasts a shell's start-up (pwsh over ssh takes seconds).
        var crashed = await pool.AcquireAsync(host, "run-crashed", GameHostChecks.Generous, leaseTime: TimeSpan.FromSeconds(10));
        var refused = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => pool.AcquireAsync(host, "run-next", GameHostChecks.Generous));
        Assert.Equal("run-crashed", Assert.Single(refused.Accounts).Holder);
        // The lease host's clock counts whole seconds; the wait is for time itself.
        await Task.Delay(crashed.ExpiresUtc - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1.5));
        await using var next = await pool.AcquireAsync(host, "run-next", GameHostChecks.Generous, leaseTime: TimeSpan.FromMinutes(5));
        Assert.Equal("vt_client_1", next.Account);
        // The old holder has lost it: it may neither renew nor release the new holder's lease.
        Assert.Equal(SteamAccountLeaseState.Lost, (await Assert.ThrowsAsync<SteamAccountLeaseException>(() => crashed.RenewAsync(GameHostChecks.Generous))).State);
        Assert.Equal(SteamAccountLeaseState.Lost, (await crashed.ReleaseAsync()).State);
        var status = Assert.Single(await pool.ListAsync(host, GameHostChecks.Generous));
        Assert.Equal(SteamAccountState.Held, status.State); Assert.Equal("run-next", status.Holder);
        var before = next.ExpiresUtc;
        Assert.True(await next.RenewAsync(GameHostChecks.Generous) >= before);
    });

    public static Task AFailedRunReleasesItsLease(IGameHost host, string parent) => GameHostChecks.WithRootAsync(host, parent, async root =>
    {
        var pool = Pool(root, 1);
        string? account = null;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var lease = await pool.AcquireAsync(host, "run-failing", GameHostChecks.Generous);
            account = lease.Account;
            throw new InvalidOperationException("the test failed");
        });
        await using var again = await pool.AcquireAsync(host, "run-after", GameHostChecks.Generous);
        Assert.Equal(account, again.Account);
    });

    public static Task OldClaimsArePruned(IGameHost host, string parent) => GameHostChecks.WithRootAsync(host, parent, async root =>
    {
        var pool = Pool(root, 1);
        for (int i = 0; i < 12; i++) await (await pool.AcquireAsync(host, "run-" + i, GameHostChecks.Generous)).DisposeAsync();
        string script = host.Shell.Kind == HostShellKind.Bash
            ? "for f in \"$d\"/claim-*; do [ -e \"$f\" ] && basename -- \"$f\"; done"
            : "foreach ($f in [IO.Directory]::GetFiles($d, 'claim-*')) { [IO.Path]::GetFileName($f) }";
        var listed = (await host.RunAsync(script, new Dictionary<string, string> { ["d"] = root + "/ci-pool/vt_client_1" }, GameHostChecks.Generous)).EnsureSuccess("Listing claims");
        var claims = listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(name => Regex.IsMatch(name, "^claim-[0-9]{9}$")).Order().ToList();
        Assert.Equal(9, claims.Count);
        Assert.Equal("claim-000000012", claims[^1]);
        Assert.Equal("claim-000000004", claims[0]);
    });
}

public class LocalLeaseShellTests
{
    public static TheoryData<string> Shells => LocalGameHostShellTests.Shells;
    private static IGameHost Host(string shell) => new LocalGameHost("local-" + shell, HostShell.Parse(shell));
    private static string Parent => Path.GetTempPath();

    [Theory, MemberData(nameof(Shells))] public Task TwoRunsNeverHoldOneAccount(string shell) => LeaseChecks.TwoRunsNeverHoldOneAccount(Host(shell), Parent);
    [Theory, MemberData(nameof(Shells))] public Task ACrashedHoldersLeaseExpires(string shell) => LeaseChecks.ACrashedHoldersLeaseExpires(Host(shell), Parent);
    [Theory, MemberData(nameof(Shells))] public Task AFailedRunReleasesItsLease(string shell) => LeaseChecks.AFailedRunReleasesItsLease(Host(shell), Parent);
    [Theory, MemberData(nameof(Shells))] public Task OldClaimsArePruned(string shell) => LeaseChecks.OldClaimsArePruned(Host(shell), Parent);
}

[Trait("Category", "GameHosts")]
public class RemoteLeaseTests
{
    public static TheoryData<string> Kinds
    {
        get
        {
            var kinds = new TheoryData<string>();
            foreach (string shell in (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_SHELLS") ?? "bash").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                kinds.Add("ssh-" + shell);
            return kinds;
        }
    }
    private static IGameHost SshHost(string kind) => new SshGameHost(kind, Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION")!, HostShell.Parse(kind["ssh-".Length..]),
        int.TryParse(Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_PORT"), out int port) ? port : 0,
        (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_OPTIONS") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private static IGameHost Container() => new ContainerGameHost("ctr", Environment.GetEnvironmentVariable("VALHEIM_TESTING_CONTAINER")!, HostShell.Bash);

    [SshTheory, MemberData(nameof(Kinds))] public Task TwoRunsNeverHoldOneAccountOverSsh(string kind) => LeaseChecks.TwoRunsNeverHoldOneAccount(SshHost(kind), Path.GetTempPath());
    [SshTheory, MemberData(nameof(Kinds))] public Task ACrashedHoldersLeaseExpiresOverSsh(string kind) => LeaseChecks.ACrashedHoldersLeaseExpires(SshHost(kind), Path.GetTempPath());
    [ContainerTheory, InlineData("bash")] public Task TwoRunsNeverHoldOneAccountInAContainer(string _) => LeaseChecks.TwoRunsNeverHoldOneAccount(Container(), "/tmp");
    [ContainerTheory, InlineData("bash")] public Task AFailedRunReleasesItsLeaseInAContainer(string _) => LeaseChecks.AFailedRunReleasesItsLease(Container(), "/tmp");
}
