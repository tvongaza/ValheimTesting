using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

/// <summary>
/// The lease host the Steam account tests share: this machine's own shell, so every claim and release runs the real lease
/// scripts. Account names and SteamIDs are obvious fakes (account ids 1 and 2).
/// </summary>
internal static class LeaseBox
{
    public const string Name = "lease-box", Pool = "ci-clients", Account = "vt_client_one", Other = "vt_client_two";
    public const string SteamId = "76561197960265729", OtherSteamId = "76561197960265730";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public static string Shell => OperatingSystem.IsWindows() ? "powershell" : "bash";
    public static IGameHost Host() => new LocalGameHost(Name, HostShell.Parse(Shell));
    // Under this test process's temp folder: the run journal goes beside the lock, on this real machine.
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "vt-lease-box-" + Environment.ProcessId);
    public static object Profile => new { kind = "local", platform = HostProfile.CurrentPlatform, shell = Shell, @lock = Path.Combine(Root, "lease-lock") };
    public static string Journal => Path.Combine(Root, "journal");

    /// <summary>Writes <c>steam-accounts.json</c> in <paramref name="directory"/>, its leases in <paramref name="leases"/>; one account with a steamId by default.</summary>
    public static string WritePool(string directory, string leases, object[]? accounts = null)
    {
        string path = Path.Combine(directory, "steam-accounts.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            pool = Pool, leaseDirectory = leases, accounts = accounts ?? [new { name = Account, steamId = SteamId }],
        }));
        return path;
    }

    public static async Task<SteamAccountStatus> StatusAsync(string poolFile, string account = Account) =>
        (await TestEnvironment.Pool(File.ReadAllText(poolFile)).ListAsync(Host(), Timeout)).Single(status => status.Account == account);
}

// The environment's Steam leases and the pool's steamId: every inconsistency is refused when the environment is validated.
public sealed class SteamAccountProfileTests : IDisposable
{
    private readonly TempDirectory _root = new();
    public void Dispose() => _root.Dispose();

    private string Write(object? steamAccounts, string? clientAccount = null, object[]? accounts = null, bool serverAccount = false)
    {
        LeaseBox.WritePool(_root.Path, Path.Combine(_root.Path, "leases"), accounts);
        var player = new Dictionary<string, object> { ["host"] = "gaming-pc", ["install"] = @"C:\Games\Valheim", ["runtime"] = @"C:\vt\runs", ["cliPort"] = 5578 };
        if (clientAccount != null) player["steamAccount"] = clientAccount;
        var server = new Dictionary<string, object> { ["host"] = "linux-box", ["install"] = "/opt/valheim/server", ["runtime"] = "/srv/vt/runs", ["cliPort"] = 5577, ["gamePort"] = 2456 };
        if (serverAccount) server["steamAccount"] = LeaseBox.Account;
        var profile = new Dictionary<string, object>
        {
            ["hosts"] = new Dictionary<string, object>
            {
                [LeaseBox.Name] = LeaseBox.Profile,
                ["linux-box"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-box.example", @lock = "/var/tmp/vt/lock" },
                ["gaming-pc"] = new { kind = "ssh", platform = "windows", shell = "powershell", destination = "tester@gaming-pc.example", @lock = @"C:\vt\lock" },
            },
            ["server"] = server,
            ["clients"] = new Dictionary<string, object> { ["player"] = player },
        };
        if (steamAccounts != null) profile["steamAccounts"] = steamAccounts;
        string path = Path.Combine(_root.Path, "environment.json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile));
        return path;
    }

    private static object Section(string leaseHost = LeaseBox.Name, bool check = false) => new { pool = "steam-accounts.json", leaseHost, checkSignedIn = check };

    [Theory]
    [InlineData("lease host", "the lease host 'nowhere' is not listed")]
    [InlineData("unknown account", "names Steam identity vt_nobody, which the observed identities do not list")]
    [InlineData("no account for the host", "No observed Steam identity is for the client player's host 'gaming-pc'")]
    [InlineData("check without steamId", "identity vt_client_one, which has no SteamID64")]
    [InlineData("account without leases", "the environment has no Steam leases to lease it from")]
    [InlineData("server account", "a dedicated server needs no Steam account")]
    [InlineData("bad steamId", "steamId must be the account's SteamID64")]
    public void AnInconsistentEnvironmentIsRefused(string problem, string expected)
    {
        string path = problem switch
        {
            "lease host" => Write(Section(leaseHost: "nowhere")),
            "unknown account" => Write(Section(), "vt_nobody"),
            "no account for the host" => Write(Section(), null, [new { name = LeaseBox.Account, host = "linux-gpu" }]),
            "check without steamId" => Write(Section(check: true), null, [new { name = LeaseBox.Account }]),
            "account without leases" => Write(null, LeaseBox.Account),
            "server account" => Write(Section(), serverAccount: true),
            _ => Write(Section(), null, [new { name = LeaseBox.Account, steamId = "12345" }]),
        };
        var error = Assert.ThrowsAny<ArgumentException>(() => TestEnvironment.Read(path));
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("76561197960265729", 1u)]
    [InlineData("76561198000000000", 39734272u)]
    [InlineData("76561197960265728", null)] // account id 0
    [InlineData("76561202255233024", null)] // not an individual account
    [InlineData("7656119796026572", null)]
    [InlineData("7656119796026572x", null)]
    [InlineData("", null)]
    public void ASteamIdIsAnIndividualAccountsSteamId64(string steamId, uint? account) => Assert.Equal(account, SteamPoolAccount.AccountId(steamId));
}

// Reading a host's signed-in Steam user: the script each shell gets, how its replies are read, and the real scripts on this machine.
public sealed class SteamSignedInUserTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static SshGameHost Host(FakeLauncher fake, string shell) => new("gaming-pc", "tester@gaming-pc.example", HostShell.Parse(shell), 0, null, null, "ssh", fake);

    [Theory]
    [InlineData("VT-STEAMUSER id 76561197960265729", SteamSignedInState.Matches, 1u)]
    [InlineData("VT-STEAMUSER account 2", SteamSignedInState.Matches, 2u)]
    [InlineData("VT-STEAMUSER none", SteamSignedInState.NotSignedIn, null)]
    [InlineData("VT-STEAMUSER unreadable the host user has no loginusers.vdf in its Steam directories", SteamSignedInState.Unknown, null)]
    [InlineData("VT-STEAMUSER id lots", SteamSignedInState.Unknown, null)]
    [InlineData("VT-STEAMUSER id 76561197960265728", SteamSignedInState.Unknown, null)]
    [InlineData("VT-STEAMUSER id 18446744073709551615", SteamSignedInState.Unknown, null)]
    [InlineData("something else", SteamSignedInState.Unknown, null)]
    public async Task EachReplyIsReadAndOnlyAnIdIsKept(string reply, SteamSignedInState state, uint? account)
    {
        var fake = new FakeLauncher().Exits(0, reply + "\n", FakeLauncher.Report(0));
        var (read, id, _) = await SteamSignedInUsers.ReadAsync(Host(fake, "bash"), Timeout, default);
        Assert.Equal(state, read); Assert.Equal(account, id);
    }

    [Fact] public async Task AReplyThatDidNotCompleteIsUnknownAndNeverRepeated()
    {
        var fake = new FakeLauncher().TimesOut("VT-STEAMUSER id 76561197960265729\n");
        var (state, id, detail) = await SteamSignedInUsers.ReadAsync(Host(fake, "powershell"), Timeout, default);
        Assert.Equal(SteamSignedInState.Unknown, state); Assert.Null(id);
        Assert.Contains("timed out", detail); Assert.DoesNotContain("7656119", detail);
    }

    [Fact] public async Task EachShellGetsItsOwnScript()
    {
        var fake = new FakeLauncher().Exits(0, "VT-STEAMUSER none\n", FakeLauncher.Report(0)).Exits(0, "VT-STEAMUSER none\n", FakeLauncher.Report(0));
        await SteamSignedInUsers.ReadAsync(Host(fake, "bash"), Timeout, default);
        await SteamSignedInUsers.ReadAsync(Host(fake, "powershell"), Timeout, default);
        Assert.Contains("loginusers.vdf", FakeLauncher.Script(fake.Calls[0])); Assert.DoesNotContain("ActiveProcess", FakeLauncher.Script(fake.Calls[0]));
        Assert.Contains(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", FakeLauncher.Script(fake.Calls[1]));
    }

    // A loginusers.vdf as Steam writes it, through every local shell on Linux and macOS: the MostRecent user is the second, and
    // its other fields are not read.
    [Fact] public async Task OnLinuxAndMacOSTheMostRecentUserOfLoginusersIsRead()
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var row in LocalGameHostShellTests.Shells) await ReadsLoginusers((string)row[0]);
    }

    private static async Task ReadsLoginusers(string shell)
    {
        using var steam = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(steam.Path, "config"));
        string vdf = Path.Combine(steam.Path, "config", "loginusers.vdf");
        File.WriteAllText(vdf, "\"users\"\n{\n\t\"76561197960265730\"\n\t{\n\t\t\"AccountName\"\t\t\"vt_client_two\"\n\t\t\"MostRecent\"\t\t\"0\"\n\t}\n" +
            "\t\"76561197960265729\"\n\t{\n\t\t\"AccountName\"\t\t\"vt_client_one\"\n\t\t\"RememberPassword\"\t\t\"1\"\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}\n");
        var host = new LocalGameHost("here", HostShell.Parse(shell));
        var Timeout = await GameHostChecks.ShellDeadlineAsync(host);
        var (state, id, detail) = await SteamSignedInUsers.ReadAsync(host, Timeout, default, steam.Path);
        Assert.True(state == SteamSignedInState.Matches, $"{shell}: {state} {detail}"); Assert.Equal(1u, id);
        // Older Steam writes the key in lower case; no user marked MostRecent means none is signed in.
        File.WriteAllText(vdf, File.ReadAllText(vdf).Replace("\"MostRecent\"\t\t\"1\"", "\"mostrecent\"\t\t\"1\""));
        var lower = await SteamSignedInUsers.ReadAsync(host, Timeout, default, steam.Path);
        Assert.True(lower.State == SteamSignedInState.Matches, $"{shell}: {lower.State} {lower.Detail}"); Assert.Equal(1u, lower.AccountId);
        File.WriteAllText(vdf, File.ReadAllText(vdf).Replace("\"mostrecent\"\t\t\"1\"", "\"mostrecent\"\t\t\"0\""));
        Assert.Equal(SteamSignedInState.NotSignedIn, (await SteamSignedInUsers.ReadAsync(host, Timeout, default, steam.Path)).State);

        // The current macOS client writes no MostRecent key; each user's Timestamp is its last sign-in, and the newest is the account.
        // The newer user is listed first here, so file order cannot pass for the rule.
        string Current(long first, long? second) => "\"users\"\n{\n\t\"76561197960265731\"\n\t{\n\t\t\"AccountName\"\t\t\"vt_client_three\"\n" +
            $"\t\t\"AutoLogin\"\t\t\"1\"\n\t\t\"Timestamp\"\t\t\"{first}\"\n\t}}\n\t\"76561197960265730\"\n\t{{\n\t\t\"AccountName\"\t\t\"vt_client_two\"\n" +
            (second is { } t ? $"\t\t\"Timestamp\"\t\t\"{t}\"\n" : "") + "\t}\n}\n";
        File.WriteAllText(vdf, Current(1790396214, 1789360035));
        Assert.Equal(3u, (await SteamSignedInUsers.ReadAsync(host, Timeout, default, steam.Path)).AccountId);
        File.WriteAllText(vdf, Current(1789360035, 1790396214));
        Assert.Equal(2u, (await SteamSignedInUsers.ReadAsync(host, Timeout, default, steam.Path)).AccountId);
        File.WriteAllText(vdf, Current(1790396214, 1790396214));
        var tie = await SteamSignedInUsers.ReadAsync(host, Timeout, default, steam.Path);
        Assert.Equal(SteamSignedInState.Unknown, tie.State); Assert.Contains("newest Timestamp", tie.Detail);
        File.WriteAllText(vdf, Current(1790396214, null).Replace("\t\t\"Timestamp\"\t\t\"1790396214\"\n", ""));
        Assert.Equal(SteamSignedInState.Unknown, (await SteamSignedInUsers.ReadAsync(host, Timeout, default, steam.Path)).State);
        File.WriteAllText(vdf, "\"users\"\n{\n}\n");
        Assert.Equal(SteamSignedInState.NotSignedIn, (await SteamSignedInUsers.ReadAsync(host, Timeout, default, steam.Path)).State);
    }

    // On Windows the registry script runs for real: whatever this machine's Steam state is, it answers in a known form. The id is
    // never shown.
    [Fact] public async Task OnWindowsTheRegistryScriptAnswersInAKnownForm()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var row in LocalGameHostShellTests.Shells)
        {
            var (state, _, detail) = await SteamSignedInUsers.ReadAsync(new LocalGameHost("here", HostShell.Parse((string)row[0])), Timeout, default);
            Assert.True(state != SteamSignedInState.Unknown || detail.Contains("ActiveUser", StringComparison.Ordinal), $"{row[0]}: {state} {detail}");
        }
    }
}

// SteamAccountHold with the real lease scripts on this machine: two profiles on different client hosts share one account safely, a
// crashed run's lease never lapses (only the escape hatch hands it on), and a session starts only on a held, checked lease.
public sealed class SteamAccountHoldTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private string Leases => Path.Combine(_root.Path, "leases");
    public void Dispose() => _root.Dispose();

    // One client on one host per profile; both profiles use the same pool and lease host.
    private ResolvedEnvironment Profile(string clientHost, string client, bool check = false)
    {
        var hosts = new Dictionary<string, object> { [LeaseBox.Name] = LeaseBox.Profile };
        object role;
        if (clientHost == "gaming-pc")
        {
            hosts[clientHost] = new { kind = "ssh", platform = "windows", shell = "powershell", destination = "tester@gaming-pc.example", @lock = @"C:\vt\lock" };
            role = new { host = clientHost, install = @"C:\Games\Valheim", runtime = @"C:\vt\runs", cliPort = 5578 };
        }
        else
        {
            hosts[clientHost] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-gpu.example", @lock = "/home/tester/lock" };
            role = new { host = clientHost, install = "/home/tester/valheim", runtime = "/home/tester/runs", cliPort = 5578 };
        }
        string path = Path.Combine(_root.Path, $"environment-{client}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            hosts, clients = new Dictionary<string, object> { [client] = role }, steamAccounts = new { pool = "steam-accounts.json", leaseHost = LeaseBox.Name, checkSignedIn = check },
        }));
        return TestEnvironment.Read(path);
    }

    private static async Task<SteamAccountHold?> TryAcquireAsync(ResolvedEnvironment profile, string client, string owner)
    {
        try { return await SteamAccountHold.AcquireAsync(profile, client, owner, LeaseBox.Host()); }
        catch (SteamAccountLeaseException error) when (error.State == SteamAccountLeaseState.NoneFree) { return null; }
    }

    [Fact] public async Task TwoProfilesOnDifferentHostsNeverHoldOneAccount()
    {
        LeaseBox.WritePool(_root.Path, Leases, [new { name = LeaseBox.Account }]);
        var pc = Profile("gaming-pc", "player"); var gpu = Profile("linux-gpu", "tester");
        // Six runs at once, alternating between the two profiles: exactly one gets the account.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => i % 2 == 0 ? TryAcquireAsync(pc, "player", "run-" + i + " pc") : TryAcquireAsync(gpu, "tester", "run-" + i + " gpu")));
        var winner = Assert.Single(attempts.OfType<SteamAccountHold>());
        var refused = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => SteamAccountHold.AcquireAsync(winner.Client == "player" ? gpu : pc,
            winner.Client == "player" ? "tester" : "player", "run-late", LeaseBox.Host()));
        Assert.Contains($"{LeaseBox.Account} is held by {winner.Owner}", refused.Message);
        await winner.ReleaseAsync();
        await using var next = await SteamAccountHold.AcquireAsync(gpu, "tester", "run-after", LeaseBox.Host());
        Assert.Equal(("tester", "linux-gpu", LeaseBox.Account), (next.Client, next.ClientHost, next.Account));
    }

    [Fact] public async Task ACrashedRunsLeaseNeverLapsesAndOnlyTheEscapeHatchHandsItToTheNextRun()
    {
        LeaseBox.WritePool(_root.Path, Leases, [new { name = LeaseBox.Account }]);
        var pc = Profile("gaming-pc", "player"); var gpu = Profile("linux-gpu", "tester");
        // The crashed run never releases. No timer ends its lease (#257).
        var crashed = await SteamAccountHold.AcquireAsync(pc, "player", "runner run-crashed client player", LeaseBox.Host(), "run-crashed");
        await Task.Delay(TimeSpan.FromSeconds(2));
        var refused = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => SteamAccountHold.AcquireAsync(gpu, "tester", "run-next", LeaseBox.Host()));
        Assert.Contains("held by runner run-crashed client player until it is released; if run run-crashed is over, valheim-test env recover --run run-crashed releases it", refused.Message);
        // Its machine is gone for good: the maintainer's escape hatch releases it, and the next run takes the account.
        var pool = TestEnvironment.Pool(File.ReadAllText(Path.Combine(_root.Path, "steam-accounts.json")));
        Assert.Contains("VT-LEASE-ABANDONED", (await pool.RunAsync(LeaseBox.Host(), "abandon", LeaseBox.Timeout, default, run: "run-crashed")).Stdout);
        await using var next = await SteamAccountHold.AcquireAsync(gpu, "tester", "run-next", LeaseBox.Host());
        var late = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => crashed.ReleaseAsync());
        Assert.Equal(SteamAccountLeaseState.Lost, late.State); Assert.Contains("Another run may have used", late.Message);
        var status = await LeaseBox.StatusAsync(Path.Combine(_root.Path, "steam-accounts.json"));
        Assert.Equal((SteamAccountState.Held, "run-next"), (status.State, status.Holder));
    }

    // Against a scripted lease host: a release that cannot be proven is a failed teardown, never taken as proven.
    [Fact] public async Task AnUnprovenReleaseIsNeverTakenAsProven()
    {
        LeaseBox.WritePool(_root.Path, Leases);
        var profile = Profile("gaming-pc", "player");
        SshGameHost LeaseHost(FakeLauncher launcher) => new(LeaseBox.Name, "tester@lease-box.example", HostShell.Parse(LeaseBox.Shell), 0, null, null, "ssh", launcher);
        var release = new FakeLauncher().Exits(0, $"VT-LEASE claimed {LeaseBox.Account} 2\n", FakeLauncher.Report(0)).TimesOut();
        var unproven = await SteamAccountHold.AcquireAsync(profile, "player", "run-9", LeaseHost(release), "run-9");
        var error = await Assert.ThrowsAsync<SteamAccountLeaseException>(() => unproven.ReleaseAsync());
        Assert.Equal(SteamAccountLeaseState.Unknown, error.State); Assert.Contains("not proven", error.Message);
        Assert.Contains("valheim-test env recover --run run-9", error.Message);
    }

    private ClientRunPlan Plan(string mode = "owned") => new() { Mode = mode, Install = mode == "owned" ? _root.Path : "", Port = 5556, Pinning = "none" };

    [Fact] public async Task AClientStartsOnlyOnAHeldCheckedLease()
    {
        LeaseBox.WritePool(_root.Path, Leases);
        using var output = new TempDirectory();
        // The profile asks for the signed-in check: nothing starts before it passed.
        await using (var pending = await SteamAccountHold.AcquireAsync(Profile("gaming-pc", "player", check: true), "player", "run-1", LeaseBox.Host()))
        {
            bool started = false;
            Assert.Contains("run CheckSignedInAsync", Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(Plan(), output.Path,
                () => { started = true; return new FakeOwnedProcess(99); }, () => new ScriptedTransport(), (_, _) => Task.CompletedTask, default, null, null, pending)).Message);
            Assert.False(started);
            var pc = new FakeServerHost("gaming-pc", _root.Path) { SteamUserReply = "VT-STEAMUSER account 1\n" };
            await pending.CheckSignedInAsync(pc);
            Assert.True(pending.SignedInChecked);
        }

        // Nothing starts or attaches on a released lease.
        var held = await SteamAccountHold.AcquireAsync(Profile("gaming-pc", "player"), "player", "run-2", LeaseBox.Host());
        await held.ReleaseAsync();
        bool again = false;
        Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(Plan(), output.Path, () => { again = true; return new FakeOwnedProcess(99); },
            () => new ScriptedTransport(), (_, _) => Task.CompletedTask, default, null, null, held));
        var unused = new ScriptedTransport();
        Assert.Throws<InvalidOperationException>(() => ClientSession.Attach(Plan("attach"), output.Path, held, unused));
        Assert.False(again); Assert.Empty(unused.Commands);
    }

    [Fact] public async Task AnInteractiveStartNeedsALiveLeaseForItsOwnHost()
    {
        LeaseBox.WritePool(_root.Path, Leases);
        await using var held = await SteamAccountHold.AcquireAsync(Profile("gaming-pc", "player"), "player", "run-4", LeaseBox.Host());
        var fake = new FakeLauncher();
        var elsewhere = new SshGameHost("linux-gpu", "tester@linux-gpu.example", HostShell.Bash, 0, null, null, "ssh", fake);
        var launch = GameLaunch.ForClient("/home/tester/valheim", [], hostPlatform: ClientPlatform.Linux);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => InteractiveClient.StartAsync(held, elsewhere, launch, "/home/tester/runs/run-4/client-1", LeaseBox.Timeout));
        Assert.Contains("leased for the client player on host 'gaming-pc'", error.Message);
        Assert.Empty(fake.Calls);
    }
}
