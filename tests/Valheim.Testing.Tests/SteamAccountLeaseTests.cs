using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// Account pools and leases with fake processes: what a pool may hold, what the lease host is sent, and how a claim and a
// release are read, including lost replies. The contention, no-lapse and release checks run through real shells below. Names only: a credential never appears in anything this API writes, sends or reports.
public class SteamAccountPoolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string Sample = """
        {
          // Names only.
          "pool": "valheim-clients",
          "leaseDirectory": "/var/tmp/valheim-testing/leases",
          "accounts": [
            { "name": "vt_client_one", "host": "gaming-pc" },
            { "name": "vt_client_two" }
          ]
        }
        """;

    // #255: a timeout says whether the shell ever began the script. Never began: process start or transport starved (a game
    // run loading the machine). Began but still running at the deadline: the script itself was slow. Either way the outcome
    // stays unknown, never a lost lease.
    [Fact] public async Task AClaimThatTimesOutSaysWhetherTheShellEverBeganTheScript()
    {
        var never = new FakeLauncher().TimesOut();
        var error = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => TestEnvironment.Pool(Sample).AcquireAsync(Host(never), "run-42", Timeout));
        Assert.Equal(SteamAccountLeaseState.Unknown, error.State);
        Assert.Contains("the shell had not begun the script after 2.0 s", error.Message);

        long began = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var slow = new FakeLauncher().TimesOut($"VT-PHASE began {began}\n");
        error = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => TestEnvironment.Pool(Sample).AcquireAsync(Host(slow), "run-43", Timeout));
        Assert.Equal(SteamAccountLeaseState.Unknown, error.State);
        Assert.Contains("the script began on the host, and it was still running at the deadline", error.Message);
    }

    // The phase line is taken out of the reply before anything reads it, and a local host's start is timed by this machine's clock.
    [Fact] public void ThePhaseLineLeavesTheReplyAndTimesTheShellStart()
    {
        var invoked = DateTimeOffset.UtcNow;
        var read = ShellPhases.Read(new HostResult(HostOutcome.Exited, 0, $"VT-PHASE began {(invoked + TimeSpan.FromSeconds(71)).ToUnixTimeMilliseconds()}\nVT-LEASE released\n", "",
            TimeSpan.FromSeconds(72), false), invoked, sameClock: true);
        Assert.Equal("VT-LEASE released", read.Stdout.Split('\n')[0]);
        Assert.Equal("the shell began the script after about 71.0 s", read.Phases);
        var never = ShellPhases.Read(new HostResult(HostOutcome.Unknown, null, "", "", TimeSpan.FromSeconds(90), true), invoked, sameClock: true);
        Assert.Equal("the shell had not begun the script after 90.0 s", never.Phases);
        Assert.Contains("(the shell had not begun the script after 90.0 s)", never.Describe());
    }

    private static ScriptedGameHost Host(FakeLauncher fake) => new SshGameHost("lease-box", "tester@lease-box.example", HostShell.Bash, 0, null, null, "ssh", fake);
    private static string Reply(params string[] lines) => string.Join('\n', lines) + "\n";

    [Fact] public void APoolNamesAccounts()
    {
        var pool = TestEnvironment.Pool(Sample);
        Assert.Equal("valheim-clients", pool.Pool);
        Assert.Equal(new[] { "vt_client_one", "vt_client_two" }, pool.Accounts.Select(account => account.Name));
        Assert.Equal("gaming-pc", pool.Accounts[0].Host);
    }

    [Theory]
    [InlineData("\"vt_client_two\"", "\"user:password\"", "must be a Steam account name")]
    [InlineData("\"vt_client_two\"", "\"VT_CLIENT_ONE\"", "listed twice")]
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
        var fake = new FakeLauncher().Exits(0, Reply("VT-LEASE claimed vt_client_two 7"), FakeLauncher.Report(0));
        var lease = await pool.AcquireAsync(Host(fake), "run-42 on ci", Timeout, clientHost: "linux-gpu", run: "run-42");
        Assert.Equal("vt_client_two", lease.Account); Assert.Equal("valheim-clients", lease.Pool); Assert.Equal("run-42 on ci", lease.Owner); Assert.Equal("run-42", lease.Run);
        Assert.Matches("^[0-9a-f]{32}$", lease.LeaseId);
        Assert.Equal("lease-box", lease.LeaseHostName);
        string script = FakeLauncher.Script(fake.Calls[0]);
        // Only the accounts for that host (or any host) are offered.
        Assert.Contains("accounts='vt_client_two'", script);
        Assert.Contains("action='claim'", script); Assert.Contains("owner='run-42 on ci'", script); Assert.Contains("run='run-42'", script);
        Assert.Contains("directory='/var/tmp/valheim-testing/leases'", script); Assert.Contains("pool='valheim-clients'", script);
        Assert.Contains("lease='" + lease.LeaseId + "'", script);

        fake.Exits(0, Reply("VT-LEASE claimed vt_client_one 1"), FakeLauncher.Report(0));
        await pool.AcquireAsync(Host(fake), "run-43", Timeout, clientHost: "gaming-pc");
        Assert.Contains("accounts='vt_client_one\nvt_client_two'", FakeLauncher.Script(fake.Calls[1]));
        await Assert.ThrowsAsync<ArgumentException>(() => TestEnvironment.Pool(Sample.Replace("{ \"name\": \"vt_client_two\" }", "{ \"name\": \"vt_client_two\", \"host\": \"x\" }"))
            .AcquireAsync(Host(fake), "run-44", Timeout, clientHost: "elsewhere"));
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact] public async Task WhenEveryAccountIsHeldTheHoldersAreReported()
    {
        var fake = new FakeLauncher().Exits(0, Reply("VT-LEASE none", "held vt_client_one run-41 another-runner run-41 client player", "held vt_client_two - run-40 on an older runner",
            "taken vt_client_three"), FakeLauncher.Report(0));
        var error = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => TestEnvironment.Pool(Sample).AcquireAsync(Host(fake), "run-42", Timeout));
        Assert.Equal(SteamAccountLeaseState.NoneFree, error.State);
        Assert.Equal(new[] { SteamAccountState.Held, SteamAccountState.Held, SteamAccountState.Contended }, error.Accounts.Select(account => account.State));
        Assert.Equal(("another-runner run-41 client player", "run-41"), (error.Accounts[0].Holder, error.Accounts[0].Run));
        Assert.Null(error.Accounts[1].Run);
        // A held lease names its holder run and the command that releases it once that run is over; it never lapses.
        Assert.Contains("vt_client_one is held by another-runner run-41 client player until it is released; if run run-41 is over, valheim-test env recover --run run-41 releases it", error.Message);
        Assert.Contains("vt_client_two is held by run-40 on an older runner until it is released; see valheim-test env status", error.Message);
    }

    [Theory, InlineData(true), InlineData(false)] public async Task AnUnprovenClaimIsUnknownAndHeldUntilARecoveryReleasesIt(bool timedOut)
    {
        var fake = timedOut ? new FakeLauncher().TimesOut() : new FakeLauncher().Exits(0, Reply("VT-LEASE claimed someone_else 1"), FakeLauncher.Report(0));
        var error = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => TestEnvironment.Pool(Sample).AcquireAsync(Host(fake), "run-42 on ci", Timeout, run: "run-42"));
        Assert.Equal(SteamAccountLeaseState.Unknown, error.State);
        // No journal names an unproven claim, so env recover cannot find it: the escape hatch for its run releases it.
        Assert.Contains("held until it is released: see valheim-test env status; once run run-42 is over, valheim-test env teardown --run run-42 --machine-gone releases it", error.Message);
        Assert.DoesNotContain("expire", error.Message);
    }

    [Fact] public async Task ReleasingNeedsTheLeasesOwnClaim()
    {
        var pool = TestEnvironment.Pool(Sample);
        var fake = new FakeLauncher()
            .Exits(0, Reply("VT-LEASE claimed vt_client_one 3"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-LEASE released"), FakeLauncher.Report(0));
        await using (var lease = await pool.AcquireAsync(Host(fake), "run-42", Timeout)) { }
        string release = FakeLauncher.Script(fake.Calls[1]);
        Assert.Contains("action='release'", release); Assert.Contains("account='vt_client_one'", release); Assert.Contains("number='3'", release);

        // A lease a maintainer released and another run took since: its release says it is no longer this run's.
        fake.Exits(0, Reply("VT-LEASE claimed vt_client_one 4"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-LEASE lost taken"), FakeLauncher.Report(0));
        var taken = await pool.AcquireAsync(Host(fake), "run-43", Timeout);
        Assert.Equal(SteamAccountLeaseState.Lost, (await taken.ReleaseAsync()).State);

        // An unproven release is a failed teardown; the handle acts only once.
        fake.Exits(0, Reply("VT-LEASE claimed vt_client_one 5"), FakeLauncher.Report(0)).TimesOut();
        var held = await pool.AcquireAsync(Host(fake), "run-44", Timeout, run: "run-44");
        var error = await Assert.ThrowsAsync<SteamAccountLeaseException>(async () => await held.DisposeAsync());
        Assert.Equal(SteamAccountLeaseState.Unknown, error.State);
        Assert.Contains("held until it is released: see valheim-test env status, then valheim-test env recover --run run-44", error.Message);
        Assert.Equal(SteamAccountLeaseState.Released, (await held.ReleaseAsync()).State);
        Assert.Equal(6, fake.Calls.Count);
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
                .Exits(0, Reply("VT-LEASE claimed vt_client_two 1"), FakeLauncher.Report(0))
                .Exits(0, Reply("VT-LEASE none", "held vt_client_one - run-7", "held vt_client_two - run-42"), FakeLauncher.Report(0))
                .Exits(0, Reply("VT-LEASE released"), FakeLauncher.Report(0));
            var report = new ScenarioReport("lease");
            var seen = new List<string>();
            await using (var lease = await pool.AcquireAsync(Host(fake), "run-42", Timeout))
            {
                lease.Record(report, "player");
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
            fake.Exits(0, Reply("VT-LEASE claimed vt_client_one 2"), FakeLauncher.Report(0));
            await pool.AcquireAsync(Host(fake), "owner " + canary, Timeout);
            Assert.Contains(canary, FakeLauncher.Script(fake.Calls[^1]));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
}

// The lease scripts through every local shell this machine has, and over ssh and into a container in the game-hosts job: two
// runs never hold one account, a crashed holder's lease never lapses (only the escape hatch for a run whose machine is gone
// releases it), a failed run releases its lease, and old claims are pruned.
internal static class LeaseChecks
{
    public static SteamAccountPool Pool(string root, int accounts) => new()
    {
        Pool = "ci-pool", LeaseDirectory = root,
        Accounts = Enumerable.Range(1, accounts).Select(i => new SteamPoolAccount { Name = "vt_client_" + i }).ToList(),
    };

    private static async Task<(SteamAccountLease? Lease, SteamAccountLeaseException? Error)> TryAcquireAsync(SteamAccountPool pool, IGameHost host, string owner, TimeSpan deadline)
    {
        try { return (await pool.AcquireAsync(host, owner, deadline), null); }
        catch (SteamAccountLeaseException error) { return (null, error); }
    }

    public static Task TwoRunsNeverHoldOneAccount(IGameHost host, string parent) => GameHostChecks.WithRootAsync(host, parent, async root =>
    {
        var pool = Pool(root, 3);
        // Calibrate before contention. Calibrating inside every attempt launched ten more SSH/PowerShell sessions at once,
        // so the test sometimes measured SSH startup failure instead of whether claims are exclusive (#583).
        TimeSpan deadline = await GameHostChecks.ShellDeadlineAsync(host);
        var attempts = new List<(SteamAccountLease? Lease, SteamAccountLeaseException? Error)>();
        // Four simultaneous callers still race for three accounts. Keep all ten attempts, but bound the number of SSH and
        // PowerShell startups competing for the CI runner at once; later waves must all see the first wave's held claims.
        for (int first = 0; first < 10; first += 4)
            attempts.AddRange(await Task.WhenAll(Enumerable.Range(first, Math.Min(4, 10 - first))
                .Select(i => TryAcquireAsync(pool, host, "run-" + i, deadline))));
        var leases = attempts.Where(attempt => attempt.Lease != null).Select(attempt => attempt.Lease!).ToList();
        try
        {
            Assert.Equal(3, leases.Count);
            Assert.Equal(3, leases.Select(lease => lease.Account).Distinct().Count());
            Assert.All(attempts.Where(attempt => attempt.Lease == null), attempt => Assert.Equal(SteamAccountLeaseState.NoneFree, attempt.Error!.State));
            var held = await pool.ListAsync(host, GameHostChecks.Generous);
            Assert.All(held, status => Assert.Equal(SteamAccountState.Held, status.State));
            Assert.Equal(leases.Select(lease => lease.Owner).Order(), held.Select(status => status.Holder!).Order());
        }
        finally
        {
            // A failed assertion must not leave proven claims live while the scratch root is removed. Unknown claims remain
            // a failure above; the scratch root is the test's escape hatch for any claim whose reply was lost.
            foreach (var lease in leases) await lease.ReleaseAsync();
        }
        Assert.All(await pool.ListAsync(host, GameHostChecks.Generous), status => Assert.Equal(SteamAccountState.Free, status.State));
    });

    public static Task ACrashedHoldersLeaseNeverLapses(IGameHost host, string parent) => GameHostChecks.WithRootAsync(host, parent, async root =>
    {
        var pool = Pool(root, 1);
        // The holder "crashes": it never releases. No timer ends its lease; the claim also tells an older runner it never expires.
        var crashed = await pool.AcquireAsync(host, "runner run-crashed client player", GameHostChecks.Generous, run: "run-crashed");
        string claim = (await host.RunAsync(host.Shell.Kind == HostShellKind.Bash ? "cat -- \"$f\"" : "[IO.File]::ReadAllText($f)",
            new Dictionary<string, string> { ["f"] = root + "/ci-pool/vt_client_1/claim-000000001" }, GameHostChecks.Generous)).EnsureSuccess("Reading the claim").Stdout;
        Assert.Equal([crashed.LeaseId, LeaseScripts.Never, "runner run-crashed client player", "run-crashed"], claim.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')));
        await Task.Delay(TimeSpan.FromSeconds(2));
        var refused = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => pool.AcquireAsync(host, "run-next", GameHostChecks.Generous));
        var holder = Assert.Single(refused.Accounts);
        Assert.Equal((SteamAccountState.Held, "runner run-crashed client player", "run-crashed"), (holder.State, holder.Holder, holder.Run));
        Assert.Contains("valheim-test env recover --run run-crashed", refused.Message);
        // Another run's id releases nothing; the escape hatch for that run's gone machine releases it.
        Assert.DoesNotContain("VT-LEASE-ABANDONED", (await pool.RunAsync(host, "abandon", GameHostChecks.Generous, default, run: "run-other")).Stdout);
        var abandoned = (await pool.RunAsync(host, "abandon", GameHostChecks.Generous, default, run: "run-crashed")).EnsureSuccess("Abandoning").Stdout;
        Assert.Contains($"VT-LEASE-ABANDONED ci-pool vt_client_1 1 {crashed.LeaseId} runner run-crashed client player", abandoned);
        await using var next = await pool.AcquireAsync(host, "run-next", GameHostChecks.Generous);
        Assert.Equal("vt_client_1", next.Account);
        // The old holder may not release the new holder's lease.
        Assert.Equal(SteamAccountLeaseState.Lost, (await crashed.ReleaseAsync()).State);
        var status = Assert.Single(await pool.ListAsync(host, GameHostChecks.Generous));
        Assert.Equal((SteamAccountState.Held, "run-next"), (status.State, status.Holder));
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

[Trait("Category", "CiShell")]
public class LocalLeaseShellTests
{
    public static TheoryData<string> Shells => LocalGameHostShellTests.Shells;
    private static IGameHost Host(string shell) => new LocalGameHost("local-" + shell, HostShell.Parse(shell));
    private static string Parent => Path.GetTempPath();

    [Theory, MemberData(nameof(Shells))] public Task TwoRunsNeverHoldOneAccount(string shell) => LeaseChecks.TwoRunsNeverHoldOneAccount(Host(shell), Parent);
    [Theory, MemberData(nameof(Shells))] public Task ACrashedHoldersLeaseNeverLapses(string shell) => LeaseChecks.ACrashedHoldersLeaseNeverLapses(Host(shell), Parent);
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
    [SshTheory, MemberData(nameof(Kinds))] public Task ACrashedHoldersLeaseNeverLapsesOverSsh(string kind) => LeaseChecks.ACrashedHoldersLeaseNeverLapses(SshHost(kind), Path.GetTempPath());
    [ContainerTheory, InlineData("bash")] public Task TwoRunsNeverHoldOneAccountInAContainer(string _) => LeaseChecks.TwoRunsNeverHoldOneAccount(Container(), "/tmp");
    [ContainerTheory, InlineData("bash")] public Task AFailedRunReleasesItsLeaseInAContainer(string _) => LeaseChecks.AFailedRunReleasesItsLease(Container(), "/tmp");
}
