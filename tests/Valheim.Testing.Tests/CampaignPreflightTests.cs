using System.Text.Json;
using System.Text.Json.Nodes;
using Valheim.Testing.Game;
using Xunit;

public sealed class CampaignPreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("campaign-preflight-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name, object data)
    {
        string file = Path.Combine(_root, name);
        File.WriteAllText(file, JsonSerializer.Serialize(data));
        return file;
    }

    // One Windows host carrying a server environment and a client environment: both actors may share it.
    private string Inventory(bool valid = true) => Write("inventory.json", new
    {
        hosts = new { pc = new { kind = "ssh", platform = "windows", shell = "powershell", @lock = @"C:\locks\test.lock", destination = "test@pc" } },
        environments = new object[]
        {
            new { name = "pc-server", host = "pc", roles = new[] { "server" }, install = @"C:\game", runtime = @"C:\runs", cliPort = 5577, localCliPort = 6577, gamePort = 2456 },
            new { name = "pc-client", host = valid ? "pc" : "unlisted", roles = new[] { "client" }, install = @"C:\client", runtime = @"C:\runs", cliPort = 5578, localCliPort = 6578 },
        },
        leaseHost = "pc",
        leaseDirectory = @"C:\leases",
    });

    [Fact]
    public async Task IndependentFaultsAcrossRolesAreAllReportedBeforeContactingAnyHost()
    {
        string inventory = Inventory(valid: false); // A client on an unlisted host: the inventory is invalid independently of the files below.
        string file = Write("campaign.json", new
        {
            inventory, world = Path.Combine(_root, "missing-world"),
            server = new { dependencyLock = "missing-server-lock.json", loaderPackage = "missing-loader.json" },
            clients = new Dictionary<string, object> { ["client-a"] = new { dependencyLock = "missing-client-lock.json" } },
        });
        var report = HostedCampaignPreparation.Inspect(file);
        Assert.False(report.Ready);
        Assert.Contains(report.Problems, problem => problem.Actor == "campaign" && problem.Input == "inventory");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "loader");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "dependencies and CLI packs");
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "character");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "world fixture");
        Assert.True(report.Problems.Count >= 6);
        using var output = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", file], output, new StringWriter()));
        Assert.Empty(report.Actors); // An invalid inventory selects no actor, even when other inputs can be checked.
        Assert.Contains("REFUSED server loader", output.ToString());
        Assert.Contains("REFUSED client-a character", output.ToString());
        var error = Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.Check(file));
        Assert.Contains("client-a character", error.Message);
        Assert.Contains("server loader", error.Message);
    }

    [Fact]
    public async Task ReviewedFixtureUidIsCheckedIndependentlyOfMissingPackages()
    {
        string inventory = Inventory();
        string world = Path.Combine(_root, "world");
        FakeInstalls.World(world, "Small", seed: "Seed");
        string file = Write("campaign.json", new
        {
            inventory, world, worldUid = "9999", server = new { dependencyLock = "missing-lock.json" },
            clients = new Dictionary<string, object>(),
        });
        var report = HostedCampaignPreparation.Inspect(file);
        Assert.Contains(report.Actors, actor => actor.Name == "server" && actor.Host == "pc" && actor.Platform == "windows");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "world fixture" && problem.Message.Contains("world UID"));
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        using var output = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", file, "--json"], output, new StringWriter()));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("Ready").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("Problems").EnumerateArray(),
            problem => problem.GetProperty("Input").GetString() == "world fixture");
    }

    [Fact]
    public async Task EnvCommandRefusesInvalidArgumentsWithoutReadingAHost()
    {
        foreach (string[] args in new string[][] { [], ["preflight", "--hosts"], ["preflight", "a.json", "--inventory", "b.json"], ["preflight", "--inventory"], ["preflight", "a", "b"] })
        {
            var error = new StringWriter();
            Assert.Equal(2, await EnvCommand.RunAsync(args, new StringWriter(), error));
            Assert.Contains("Usage: valheim-test env", error.ToString());
        }
    }

    // With no campaign, preflight shows the inventory: this machine (here a test machine without Steam) plus the file's
    // machines. It names what was not found and refuses an inventory a one-off cannot run on.
    [Fact]
    public async Task EnvPreflightWithoutACampaignReportsTheInventoryAndWhatIsMissing()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight"], output, error));
        Assert.Contains("This machine has none", error.ToString());
        Assert.Contains("Steam was not found", error.ToString());

        // A file of other machines is shown as written: nothing is detected for it.
        using var listed = new StringWriter();
        Assert.Equal(0, await EnvCommand.RunAsync(["preflight", "--inventory", Inventory()], listed, new StringWriter()));
        Assert.DoesNotContain("detected:", listed.ToString());
        Assert.Contains("ELIGIBLE: the inventory has a server and a client environment", listed.ToString());

        // A local environment the test machine cannot fill is named, with what was tried.
        string local = Write("local.json", new { environments = new object[] { new { name = "local-client", roles = new[] { "client" } } } });
        using var refused = new StringWriter();
        using var refusal = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", local], refused, refusal));
        Assert.Contains("Environment local-client needs absolute install and runtime paths on local", refusal.ToString());
        Assert.Contains("Steam was not found (tried", refusal.ToString());

        // An empty file is refused, not a crash.
        File.WriteAllText(local, "null");
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", local], new StringWriter(), new StringWriter()));
    }

    // A campaign that leaves out its inventory is assigned on this machine; without Steam that is refused with what was tried.
    [Fact]
    public void ACampaignWithoutAnInventoryUsesThisMachine()
    {
        string file = Write("campaign.json", new
        {
            server = new { dependencyLock = "server.lock.json" },
            clients = new Dictionary<string, object>(),
        });
        var report = HostedCampaignPreparation.Inspect(file);
        var problem = Assert.Single(report.Problems, problem => problem.Input == "inventory");
        Assert.Contains("This machine has none", problem.Message);
        Assert.Contains("Steam was not found", problem.Message);
    }

    [Fact]
    public async Task ReadOnlyHostCheckKeepsIndependentLocalSessionAndInstallFailures()
    {
        string inventory = Inventory();
        string file = Write("campaign.json", new
        {
            inventory, server = new { dependencyLock = "missing-lock.json" }, clients = new Dictionary<string, object>(),
        });
        var host = new FakeServerHost("pc", Path.Combine(_root, "mirror"), windows: true) { GameActive = true, PortBusy = true };
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.Contains(report.Problems, problem => problem.Actor == "pc" && problem.Input == "session");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "game and loader");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "ValheimCLI port");
        Assert.Contains(host.Scripts, script => script == "game-process");
        Assert.Contains(host.Scripts, script => script == "list");
        Assert.DoesNotContain(host.Scripts, script => script is "copy" or "start" or "apply-stage");
        Assert.DoesNotContain(report.Problems, problem => problem.Input == "server task"); // an elevated host registers its S4U task
    }

    // A Windows server host whose account can register neither server task (not elevated, and no desktop session of its own)
    // is refused before anything is copied, naming why; the station's first zero-config launch failed there instead.
    [Fact]
    public async Task AServerHostThatCannotRegisterTheServerTaskIsRefusedBeforeCopying()
    {
        string file = Write("campaign.json", new
        {
            inventory = Inventory(), server = new { dependencyLock = "missing-lock.json" }, clients = new Dictionary<string, object>(),
        });
        var host = new FakeServerHost("pc", Path.Combine(_root, "mirror"), windows: true)
        { ServerTaskLogon = "unsupported pc\\tester is not elevated, which a session-0 server task needs, and has no desktop session for one in its own session; run from your own desktop session, or over SSH as an administrator" };
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        var problem = Assert.Single(report.Problems, problem => problem.Input == "server task");
        Assert.Equal("server", problem.Actor);
        Assert.Contains("is not elevated", problem.Message);
        Assert.Contains(host.Scripts, script => script == "server-logon");
        Assert.DoesNotContain(host.Scripts, script => script is "copy" or "start");
        // A desktop session's interactive task is as good as an elevated one's S4U task.
        host.ServerTaskLogon = "interactive";
        report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.DoesNotContain(report.Problems, problem => problem.Input == "server task");
    }

    // A probe's refusal of a kind no check listed (a bash host that cannot tell which ports are in use) fills its own line
    // beside the others; it used to escape the concurrent preflight as an exception and lose every other fault.
    [Fact]
    public async Task AHostThatCannotTellItsPortsIsReportedBesideTheOtherFaultsNotThrown()
    {
        string inventory = Write("linux-inventory.json", new
        {
            hosts = new { box = new { kind = "ssh", platform = "linux", shell = "bash", @lock = "/vt/lock", destination = "test@box" } },
            environments = new object[]
            {
                new { name = "box-server", host = "box", roles = new[] { "server" }, install = "/opt/server", runtime = "/vt/runs", cliPort = 5577, localCliPort = 6577, gamePort = 2456 },
            },
            leaseHost = "box", leaseDirectory = "/vt/leases",
        });
        string file = Write("campaign.json", new
        {
            inventory, server = new { dependencyLock = "missing-lock.json" }, clients = new Dictionary<string, object>(),
        });
        var host = new FakeServerHost("box", Path.Combine(_root, "mirror")) { PortReply = "VT-PORT unknown\n", GameActive = true };
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.False(report.Ready);
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "ValheimCLI port" && problem.Message.Contains("cannot tell which ports are in use"));
        Assert.Contains(report.Problems, problem => problem.Actor == "box" && problem.Input == "session");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.DoesNotContain(host.Scripts, script => script is "copy" or "ship" or "start" or "apply-stage");
    }

    [Fact]
    public async Task UnverifiableSteamIdentityIsReportedWithoutHidingOtherHostFaults()
    {
        string inventory = Inventory();
        string file = Write("campaign.json", new
        {
            inventory, server = new { dependencyLock = "missing-server-lock.json" },
            clients = new Dictionary<string, object> { ["client-a"] = new { dependencyLock = "missing-client-lock.json" } },
        });
        var host = new FakeServerHost("pc", Path.Combine(_root, "mirror"), windows: true) { SteamUserReply = "VT-STEAMUSER unreadable\n" };
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "Steam identity");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "game and loader");
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "game and loader");
        // The server and the client share the host: that is no conflict.
        Assert.Equal(["pc", "pc"], report.Actors.Select(actor => actor.Host));
    }
}
