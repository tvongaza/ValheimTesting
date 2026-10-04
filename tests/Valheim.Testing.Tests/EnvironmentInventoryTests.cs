using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

public sealed class EnvironmentInventoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("environment-inventory-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static HostProfile Host() => new()
    {
        Kind = "ssh", Platform = "windows", Shell = "powershell", Destination = "test@example",
        Lock = @"C:\locks\test.lock",
    };

    private static EnvironmentRecipe Recipe(string name, string host, string kind, int port,
        string? account = null, string? steamId = null) => new()
    {
        Name = name, Host = host, Roles = [kind], Install = @"C:\game", Runtime = @"C:\runs",
        CliPort = port, LocalCliPort = port + 1000, GamePort = kind == "server" ? 2456 : 0,
        SteamAccount = account, SteamId = steamId,
    };

    private static EnvironmentInventory Inventory() => new()
    {
        Hosts = new() { ["pc"] = Host(), ["mac"] = Host(), ["vm"] = Host() },
        Environments =
        [
            Recipe("server-pc", "pc", "server", 5501),
            Recipe("client-pc", "pc", "client", 5502, "alice", "76561197960265731"),
            Recipe("client-mac", "mac", "client", 5503, "bob", "76561197960265732"),
            Recipe("client-vm", "vm", "client", 5504, "carol", "76561197960265733"),
        ],
        LeaseHost = "pc",
        AccountPool = new SteamAccountPool
        {
            Pool = "test", LeaseDirectory = @"C:\leases", SteamGuard = SteamAccountPool.SignedIn,
            Accounts =
            [
                new() { Name = "alice", SteamId = "76561197960265731", Host = "pc" },
                new() { Name = "bob", SteamId = "76561197960265732", Host = "mac" },
                new() { Name = "carol", SteamId = "76561197960265733", Host = "vm" },
            ],
        },
    };

    private static HostedCampaignManifest Campaign() => new()
    {
        Server = new(),
        Clients = new() { ["client-a"] = new(), ["client-b"] = new() },
    };

    [Fact]
    public void AssignsServerAndClientOnOneHostThenAnotherClientElsewhere()
    {
        var inventory = Inventory();
        inventory.Validate(_root);
        var result = inventory.Resolve(Campaign());
        Assert.Equal("pc", result.Profile.Server!.Host);
        Assert.Equal("pc", result.Profile.Clients["client-a"].Host);
        Assert.Equal("mac", result.Profile.Clients["client-b"].Host);
        Assert.Equal("alice", result.Profile.Clients["client-a"].SteamAccount);
        Assert.True(result.Profile.SteamAccounts!.CheckSignedIn);
        Assert.Equal(["server-pc", "client-pc", "client-mac"], result.Assignments.Select(a => a.Environment));
    }

    [Fact]
    public void OrderedResolutionBacktracksForLaterHostConstraint()
    {
        var inventory = Inventory();
        inventory.Validate(_root);
        var campaign = Campaign();
        campaign.Clients["client-b"].EnvironmentCandidates = ["client-mac"];
        campaign.Clients["client-a"].DifferentHostFrom = ["client-b"];
        var result = inventory.Resolve(campaign);
        Assert.Equal("client-pc", result.Assignments.Single(a => a.Actor == "client-a").Environment);
        Assert.Equal("client-mac", result.Assignments.Single(a => a.Actor == "client-b").Environment);

        campaign.Clients["client-a"].EnvironmentCandidates = ["client-mac", "client-vm"];
        result = inventory.Resolve(campaign);
        Assert.Equal("client-vm", result.Assignments.Single(a => a.Actor == "client-a").Environment);
    }

    [Fact]
    public void RefusesUnavailableDistinctHostsAndDuplicateSteamIdentity()
    {
        var inventory = Inventory();
        inventory.Validate(_root);
        var campaign = Campaign();
        campaign.Clients["client-a"].EnvironmentCandidates = ["client-mac"];
        campaign.Clients["client-b"].EnvironmentCandidates = ["client-mac"];
        Assert.Contains("No environment assignment", Assert.Throws<ArgumentException>(() => inventory.Resolve(campaign)).Message);

        inventory.Environments[3].SteamId = inventory.Environments[2].SteamId;
        Assert.Contains("does not match an account", Assert.Throws<ArgumentException>(() => inventory.Validate(_root)).Message);
    }

    [Fact]
    public void EmbeddedAccountPoolRejectsCredentialFieldsBeforeDeserializing()
    {
        var inventory = Inventory();
        string json = JsonSerializer.Serialize(inventory, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        json = json.Replace("\"steamGuard\":\"signed-in\"", "\"steamGuard\":\"signed-in\",\"password\":\"never-log-this\"");
        string file = Path.Combine(_root, "inventory.json");
        File.WriteAllText(file, json);
        string message = Assert.Throws<ArgumentException>(() => EnvironmentInventory.Read(file)).Message;
        Assert.Contains("credentials", message);
        Assert.DoesNotContain("never-log-this", message);
    }

    [Fact]
    public void ProfileRoundTripKeepsResolvedAccountPool()
    {
        var inventory = Inventory();
        inventory.Validate(_root);
        var profile = inventory.Resolve(Campaign()).Profile;
        string file = Path.Combine(_root, "profile.json");
        File.WriteAllText(file, JsonSerializer.Serialize(profile, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var parsed = EnvironmentProfile.Read(file);
        Assert.Equal(3, parsed.SteamAccounts!.Accounts!.Accounts.Count);
        Assert.True(parsed.SteamAccounts.CheckSignedIn);
    }

    [Fact]
    public void InventoryCanMixContainerServerLocalClientAndRemoteClient()
    {
        var inventory = Inventory();
        inventory.Hosts["pc"] = new HostProfile
        {
            Kind = "container", Platform = "linux", Shell = "bash", Container = "test-server",
            Lock = "/tmp/valheim-test-lock",
        };
        inventory.Environments[0].Install = "/srv/valheim";
        inventory.Environments[0].Runtime = "/srv/runs";
        inventory.Environments[0].LocalCliPort = 0;
        inventory.Environments.RemoveAt(1);
        inventory.Hosts["mac"] = new HostProfile
        {
            Kind = "local", Platform = HostProfile.CurrentPlatform, Shell = HostProfile.CurrentPlatform == "windows" ? "powershell" : "bash",
            Lock = HostProfile.CurrentPlatform == "windows" ? @"C:\locks\test.lock" : "/tmp/valheim-test-lock",
        };
        inventory.Environments[1].Install = HostProfile.CurrentPlatform == "windows" ? @"C:\game" : "/tmp/game";
        inventory.Environments[1].Runtime = HostProfile.CurrentPlatform == "windows" ? @"C:\runs" : "/tmp/runs";
        inventory.Environments[1].LocalCliPort = 0;
        inventory.AccountPool!.LeaseDirectory = "/tmp/test-leases";
        inventory.Validate(_root);
        var result = inventory.Resolve(Campaign());
        Assert.Equal("container", result.Profile.Hosts[result.Profile.Server!.Host].Kind);
        Assert.Equal("local", result.Profile.Hosts[result.Profile.Clients["client-a"].Host].Kind);
        Assert.Equal("ssh", result.Profile.Hosts[result.Profile.Clients["client-b"].Host].Kind);
    }

    [Fact]
    public void CampaignPreflightSelectsInventoryBeforeReportingOtherInputFaults()
    {
        var inventory = Inventory();
        string inventoryFile = Path.Combine(_root, "inventory.json");
        File.WriteAllText(inventoryFile, JsonSerializer.Serialize(inventory,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        string campaignFile = Path.Combine(_root, "campaign.json");
        File.WriteAllText(campaignFile, JsonSerializer.Serialize(new
        {
            inventory = inventoryFile,
            server = new { dependencyLock = "missing-server-lock.json" },
            clients = new Dictionary<string, object>
            {
                ["client-a"] = new { dependencyLock = "missing-client-lock.json" },
                ["client-b"] = new { dependencyLock = "missing-second-lock.json", environmentCandidates = new[] { "client-vm" } },
            },
        }));
        var report = HostedCampaignPreparation.Inspect(campaignFile);
        Assert.DoesNotContain(report.Problems, problem => problem.Input == "inventory");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.Equal(["server-pc", "client-pc", "client-vm"], report.Actors.Select(actor => actor.Environment));
        Assert.All(report.Actors, actor => Assert.NotNull(actor.SelectionReason));
    }
}
