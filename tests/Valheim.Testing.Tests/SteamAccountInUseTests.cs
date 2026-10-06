using System.Diagnostics;
using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

// #257's lease decision, parts 1 and 2: a Steam account plays on one computer at a time. Preflight refuses a campaign whose client
// account is playing on another inventory host; a client start that Steam signs out ("Logged In Elsewhere") fails at once, naming it.
public sealed class SteamAccountInUseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("steam-in-use-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    // Steam's connection_log on the station PC, 5 Oct 2026, as Steam wrote it when the Mac played the same account (account id and
    // server address replaced). steamdup1 (10:39 pm MDT) and steamdup2 (11:20 pm MDT).
    internal const string SteamDup1 = """
        [2026-10-05 22:39:37] [Logged On, 4, 43] [U:1:12345678] RecvMsgClientLoggedOff('Logged In Elsewhere')
        [2026-10-05 22:39:37] [Logged On, 4, 43] [U:1:12345678] AsyncDisconnect( bDontWaitOnTCPShutdown: false )
        [2026-10-05 22:39:37] [Logged Off, 4, 0] [U:1:12345678] ConnectionDisconnected('Disconnected By Remote Host') : 'Logged In Elsewhere' (192.0.2.1:27018, WebSocket)
        [2026-10-05 22:39:37] [Logged Off, 4, 0] [U:1:12345678] ConnectionDisconnected() not auto reconnecting due to Logged In Elsewhere
        [2026-10-05 22:39:48] [Logged Off, 0, 0] [U:1:12345678] Log session ended

        """;
    internal const string SteamDup2 = """
        [2026-10-05 23:20:51] [Logged On, 4, 7] [U:1:12345678] RecvMsgClientLoggedOff('Logged In Elsewhere')
        [2026-10-05 23:20:51] [Logged On, 4, 7] [U:1:12345678] AsyncDisconnect( bDontWaitOnTCPShutdown: false )
        [2026-10-05 23:20:52] [Logged Off, 4, 0] [U:1:12345678] ConnectionDisconnected('Disconnected By Remote Host') : 'Logged In Elsewhere' (192.0.2.1:443, WebSocket)
        [2026-10-05 23:20:52] [Logged Off, 4, 0] [U:1:12345678] ConnectionDisconnected() not auto reconnecting due to Logged In Elsewhere
        [2026-10-05 23:20:56] [Logged Off, 0, 0] [U:1:12345678] Log session ended

        """;
    // A healthy session's lines (the Mac, the same evening): nothing in them is a sign-out.
    internal const string Healthy = """
        [2026-10-05 23:29:11] [Connecting, 4, 7] [U:1:12345678] ConnectionCompleted() (192.0.2.1:27018, WebSocket) local address (192.0.2.2:52812)
        [2026-10-05 23:29:11] [Logging On, 4, 7] [U:1:12345678] RecvMsgClientLogOnResponse() : [U:1:12345678] 'OK'
        [2026-10-05 23:29:11] [Logged On, 4, 7] [U:1:12345678] RecvMsgClientLogOnResponse() : processing complete

        """;

    [Fact]
    public void TheDecidedMessageNamesTheAccountAndTheMachine()
    {
        Assert.Equal("Steam account steam-a is playing on another computer; Steam on this machine (pc) has exited; when that session has ended, start Steam here and retry. " +
            "(Steam logged 'Logged In Elsewhere' as the client started; this tool never signs in or starts Steam.)", SteamSessionLog.Message("steam-a", "pc"));
        Assert.StartsWith("The Steam account signed in here is playing on another computer;", SteamSessionLog.Message(null, "pc"));
    }

    [Fact]
    public void BothNativeSignOutsMatchAndAHealthySessionDoesNot()
    {
        foreach (string fixture in new[] { SteamDup1, SteamDup2 })
            Assert.Contains(fixture.Split('\n'), SteamSessionLog.LoggedInElsewhere.IsMatch);
        Assert.DoesNotContain(Healthy.Split('\n'), SteamSessionLog.LoggedInElsewhere.IsMatch);
    }

    [Fact]
    public void AFileCountsOnlyTheLinesAfterTheLaunch()
    {
        string log = Path.Combine(_root, "connection_log.txt");
        File.WriteAllText(log, Healthy + SteamDup1); // an earlier evening's sign-out is not this launch's
        long offset = new FileInfo(log).Length;
        Assert.False(SteamSessionLog.SeenInFile(log, offset));
        File.AppendAllText(log, Healthy);
        Assert.False(SteamSessionLog.SeenInFile(log, offset));
        File.AppendAllText(log, SteamDup2);
        Assert.True(SteamSessionLog.SeenInFile(log, offset));
        // Rotated since the launch (Steam starts a new log): all of the new one is after it.
        File.WriteAllText(log, SteamDup2);
        Assert.True(SteamSessionLog.SeenInFile(log, offset));
        Assert.False(SteamSessionLog.SeenInFile(Path.Combine(_root, "missing.txt"), 0));
    }

    [Fact]
    public async Task TheGuardEndsAStartAtOnceWhenSteamSignsItOut()
    {
        var never = new TaskCompletionSource();
        var guard = SteamSessionLog.Guard((_, token) => never.Task.WaitAsync(token), (_, _) => Task.FromResult(true), () => Task.FromResult(false), () => "signed out");
        var error = await Assert.ThrowsAsync<SteamLoggedInElsewhereException>(() => guard(TimeSpan.FromMinutes(5), default).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("signed out", error.Message);
    }

    [Fact]
    public async Task TheGuardPassesAStartSteamLeftAlone()
    {
        var ready = SteamSessionLog.Guard((_, _) => Task.CompletedTask, (_, token) => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => false),
            () => Task.FromResult(false), () => "signed out");
        await ready(TimeSpan.FromMinutes(5), default).WaitAsync(TimeSpan.FromSeconds(30));
        // A watch that cannot read the log (false at once) leaves the start to finish or fail by itself.
        var unread = SteamSessionLog.Guard((_, _) => Task.Delay(50), (_, _) => Task.FromResult(false), () => Task.FromResult(false), () => "signed out");
        await unread(TimeSpan.FromMinutes(5), default).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AStartThatFailsAnotherWayIsNamedForTheSignOutWhenSteamLoggedIt()
    {
        var failing = (TimeSpan _, CancellationToken _) => Task.FromException(new WaitTimeoutException("client at its main menu", TimeSpan.FromSeconds(1), null));
        var named = SteamSessionLog.Guard(failing, (_, _) => Task.FromResult(false), () => Task.FromResult(true), () => "signed out");
        var error = await Assert.ThrowsAsync<SteamLoggedInElsewhereException>(() => named(TimeSpan.FromMinutes(5), default));
        Assert.IsType<WaitTimeoutException>(error.InnerException);
        var plain = SteamSessionLog.Guard(failing, (_, _) => Task.FromResult(false), () => Task.FromResult(false), () => "signed out");
        await Assert.ThrowsAsync<WaitTimeoutException>(() => plain(TimeSpan.FromMinutes(5), default));
    }

    // The real follower on this machine's shell (PowerShell on Windows, bash elsewhere): only the lines written after the mark count.
    [Fact]
    public async Task TheHostWatchSeesASignOutWrittenAfterTheMark()
    {
        var host = OperatingSystem.IsWindows() ? new LocalGameHost("client", HostShell.WindowsPowerShell) : new LocalGameHost("client", HostShell.Bash);
        string steam = Path.Combine(_root, "Steam");
        Directory.CreateDirectory(Path.Combine(steam, "logs"));
        string log = Path.Combine(steam, "logs", "connection_log.txt");
        File.WriteAllText(log, Healthy + SteamDup1);
        var mark = await SteamSessionLogOnHost.MarkAsync(host, TimeSpan.FromSeconds(60), default, steam);
        Assert.NotNull(mark);
        Assert.Equal(Path.GetFullPath(log), Path.GetFullPath(mark.Value.Path));
        Assert.Equal(new FileInfo(log).Length, mark.Value.Offset);
        Assert.False(await SteamSessionLogOnHost.SeenAsync(host, mark.Value, TimeSpan.FromSeconds(3), default));
        File.AppendAllText(log, Healthy + SteamDup2);
        Assert.True(await SteamSessionLogOnHost.SeenAsync(host, mark.Value, TimeSpan.FromSeconds(60), default));
    }

    [Fact]
    public async Task TheRealPlayingCheckAnswersOnThisMachine()
    {
        var host = OperatingSystem.IsWindows() ? new LocalGameHost("here", HostShell.WindowsPowerShell) : new LocalGameHost("here", HostShell.Bash);
        var (playing, processes) = await SteamAccountInUse.ReadAsync(host, TimeSpan.FromSeconds(60), default);
        // No test launches Valheim; a person's game on this machine would be named by its process ids.
        if (playing == SteamAccountInUse.Playing.No) Assert.Empty(processes); else Assert.Matches("^[0-9]+(,[0-9]+)*$", processes);
    }

    // ps -axo pid=,uid=,comm= as it prints them; this user is 501. Another user's game is that user's Steam session, so it is left out.
    [Theory]
    [InlineData("  4242   501 /Applications/Valheim.app/Contents/MacOS/Valheim", "VT-PLAYING process 4242")]
    [InlineData("  4242   501 /tmp/vt run/Valheim.app/Contents/MacOS/Valheim", "VT-PLAYING process 4242")]
    [InlineData("  4242  1000 valheim.x86_64", "VT-PLAYING no")]
    [InlineData("  4242   501 valheim.x86_64\n  5151   501 /opt/valheim/valheim.x86_64", "VT-PLAYING process 4242,5151")]
    [InlineData("  4242   501 /opt/valheim_server.x86_64", "VT-PLAYING no")]
    [InlineData("  4242   501 /usr/bin/ordinary", "VT-PLAYING no")]
    public async Task TheBashPlayingCheckListsThisUsersValheim(string processes, string expected)
    {
        if (OperatingSystem.IsWindows()) return;
        string bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        foreach (var (name, text) in new[] { ("ps", "printf '%b\\n' '" + processes + "'"), ("id", "echo 501") })
        {
            File.WriteAllText(Path.Combine(bin, name), "#!/bin/sh\n" + text + "\n");
            File.SetUnixFileMode(Path.Combine(bin, name), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        var start = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(SteamAccountInUse.Bash);
        start.Environment["PATH"] = bin + ":" + start.Environment["PATH"];
        using var child = Process.Start(start)!;
        string output = await child.StandardOutput.ReadToEndAsync();
        string errors = await child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        Assert.Equal(0, child.ExitCode);
        Assert.Equal(expected, output.Trim());
        Assert.Empty(errors);
    }

    private static HostProfile Host() => new()
    {
        Kind = "ssh", Platform = "windows", Shell = "powershell", Destination = "test@example", Lock = @"C:\locks\test.lock",
    };

    private static EnvironmentRecipe Recipe(string name, string host, string kind, int port) => new()
    {
        Name = name, Host = host, Roles = [kind], Install = @"C:\game", Runtime = @"C:\runs",
        CliPort = port, LocalCliPort = port + 1000, GamePort = kind == "server" ? 2456 : 0,
    };

    // pc runs the server and may run a client; mac and vm are client hosts. The campaign has one client, on the host named.
    private (string Campaign, Dictionary<string, FakeServerHost> Hosts) Campaign(string clientEnvironment)
    {
        var inventory = new EnvironmentInventory
        {
            Hosts = new() { ["pc"] = Host(), ["mac"] = Host(), ["vm"] = Host() },
            Environments = [Recipe("server-pc", "pc", "server", 5501), Recipe("client-pc", "pc", "client", 5502),
                Recipe("client-mac", "mac", "client", 5503), Recipe("client-vm", "vm", "client", 5504)],
            LeaseHost = "pc", LeaseDirectory = @"C:\leases",
        };
        string inventoryFile = Path.Combine(_root, "inventory.json");
        File.WriteAllText(inventoryFile, JsonSerializer.Serialize(inventory, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        string campaignFile = Path.Combine(_root, "campaign.json");
        File.WriteAllText(campaignFile, JsonSerializer.Serialize(new
        {
            inventory = inventoryFile,
            server = new { dependencyLock = "missing-server-lock.json" },
            clients = new Dictionary<string, object> { ["client-a"] = new { dependencyLock = "missing-client-lock.json", environmentCandidates = new[] { clientEnvironment } } },
        }));
        var hosts = new[] { "pc", "mac", "vm" }.ToDictionary(name => name, name => new FakeServerHost(name, Path.Combine(_root, name), windows: true)
            { SteamUserReply = "VT-STEAMUSER account " + (name == "vm" ? 2 : 1) + "\n" });
        return (campaignFile, hosts);
    }

    private static IEnumerable<CampaignPreflightProblem> InUse(CampaignPreflightReport report) => report.Problems.Where(problem => problem.Input == "Steam account in use");

    [Fact]
    public async Task PreflightRefusesAClientAccountPlayingOnAnotherInventoryHost()
    {
        var (campaign, hosts) = Campaign("client-pc"); // client-a on pc, signed in to account 1; mac is too
        var report = await HostedCampaignPreparation.InspectAsync(campaign, TimeSpan.FromSeconds(3), name => hosts[name]);
        Assert.Empty(InUse(report)); // nothing plays anywhere
        // The campaign's own client host refuses a running Valheim by itself; this check asks only the other client hosts.
        Assert.DoesNotContain("steam-playing", hosts["pc"].Scripts);
        Assert.Contains("steam-playing", hosts["mac"].Scripts); Assert.Contains("steam-playing", hosts["vm"].Scripts);

        hosts["mac"].PlayingReply = "VT-PLAYING process 4242\n";
        var refused = Assert.Single(InUse(await HostedCampaignPreparation.InspectAsync(campaign, TimeSpan.FromSeconds(3), name => hosts[name])));
        Assert.Equal("client-a", refused.Actor);
        Assert.Contains("Valheim (process 4242) is running on mac, signed in to the Steam account client client-a would use", refused.Message);
        Assert.DoesNotContain("12345678", refused.Message);

        // Valheim on a host signed in to another account is no conflict.
        hosts["mac"].SteamUserReply = "VT-STEAMUSER account 2\n";
        Assert.Empty(InUse(await HostedCampaignPreparation.InspectAsync(campaign, TimeSpan.FromSeconds(3), name => hosts[name])));
        // Valheim running where the account cannot be read, or none is signed in, or run by a user this cannot identify: unknown is never a pass.
        hosts["mac"].SteamUserReply = "VT-STEAMUSER unreadable ActiveUser is not a number\n";
        refused = Assert.Single(InUse(await HostedCampaignPreparation.InspectAsync(campaign, TimeSpan.FromSeconds(3), name => hosts[name])));
        Assert.Equal("mac", refused.Actor);
        Assert.Contains("which Steam account it uses cannot be read (ActiveUser is not a number)", refused.Message);
        hosts["mac"].SteamUserReply = "VT-STEAMUSER none\n";
        refused = Assert.Single(InUse(await HostedCampaignPreparation.InspectAsync(campaign, TimeSpan.FromSeconds(3), name => hosts[name])));
        Assert.Contains("which Steam account it uses cannot be read (no account is signed in there)", refused.Message);
        hosts["mac"].SteamUserReply = "VT-STEAMUSER account 2\n";
        hosts["mac"].PlayingReply = "VT-PLAYING unowned 4242\n";
        refused = Assert.Single(InUse(await HostedCampaignPreparation.InspectAsync(campaign, TimeSpan.FromSeconds(3), name => hosts[name])));
        Assert.Equal("mac", refused.Actor);
        Assert.Contains("Valheim (process 4242) is running on mac as a user this check cannot identify", refused.Message);
        // A host that cannot be asked is refused, naming it.
        hosts["mac"].PlayingReply = "VT-PLAYING no\n";
        hosts["vm"].Failures["steam-playing"] = FakeServerHost.TransportFailure;
        refused = Assert.Single(InUse(await HostedCampaignPreparation.InspectAsync(campaign, TimeSpan.FromSeconds(3), name => hosts[name])));
        Assert.Equal("vm", refused.Actor);
        Assert.Contains("Could not ask vm, a client host in the inventory", refused.Message);
        Assert.All(hosts.Values, host => Assert.DoesNotContain(host.Scripts, script => script is "copy" or "start" or "client-start"));
    }

    [Fact]
    public async Task PreflightAsksTheServerHostWhenItIsAlsoAnInventoryClientHost()
    {
        var (campaign, hosts) = Campaign("client-mac"); // client-a on mac (account 1); pc only runs the server here
        hosts["pc"].PlayingReply = "VT-PLAYING process 77\n";
        var refused = Assert.Single(InUse(await HostedCampaignPreparation.InspectAsync(campaign, TimeSpan.FromSeconds(3), name => hosts[name])));
        Assert.Equal("client-a", refused.Actor);
        Assert.Contains("Valheim (process 77) is running on pc, signed in to the Steam account client client-a would use", refused.Message);
        Assert.DoesNotContain("steam-playing", hosts["mac"].Scripts);
    }
}
