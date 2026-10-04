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

    private static EnvironmentRecipe Recipe(string name, string host, string kind, int port) => new()
    {
        Name = name, Host = host, Roles = [kind], Install = @"C:\game", Runtime = @"C:\runs",
        CliPort = port, LocalCliPort = port + 1000, GamePort = kind == "server" ? 2456 : 0,
    };

    private static EnvironmentInventory Inventory() => new()
    {
        Hosts = new() { ["pc"] = Host(), ["mac"] = Host(), ["vm"] = Host() },
        Environments =
        [
            Recipe("server-pc", "pc", "server", 5501),
            Recipe("client-pc", "pc", "client", 5502),
            Recipe("client-mac", "mac", "client", 5503),
            Recipe("client-vm", "vm", "client", 5504),
        ],
        LeaseHost = "pc",
        LeaseDirectory = @"C:\leases",
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
        Assert.Null(result.Profile.Clients["client-a"].SteamAccount);
        Assert.True(result.Profile.SteamAccounts!.CheckSignedIn);
        Assert.Equal(@"C:\leases", result.Profile.SteamAccounts.ObservedLeaseDirectory);
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
    public void LoaderSelectionDoesNotChangeCampaignWhenResolvedAgain()
    {
        var inventory = Inventory();
        inventory.Environments.Single(recipe => recipe.Name == "client-pc").LoaderPackage = "pc-loader.json";
        inventory.Environments.Single(recipe => recipe.Name == "client-mac").LoaderPackage = "mac-loader.json";
        inventory.Validate(_root);
        var campaign = Campaign();

        var first = inventory.Resolve(campaign);
        Assert.EndsWith("pc-loader.json", first.LoaderPackages["client-a"]);
        Assert.Null(campaign.Clients["client-a"].LoaderPackage);

        campaign.Clients["client-a"].EnvironmentCandidates = ["client-mac"];
        campaign.Clients["client-b"].EnvironmentCandidates = ["client-vm"];
        var second = inventory.Resolve(campaign);
        Assert.EndsWith("mac-loader.json", second.LoaderPackages["client-a"]);
        Assert.Null(campaign.Clients["client-a"].LoaderPackage);
    }

    [Fact]
    public void AnotherClientEnvironmentNeedsNoAccountEntryOrStaticSteamId()
    {
        var inventory = Inventory();
        inventory.Validate(_root);
        var campaign = Campaign();
        campaign.Clients["client-a"].EnvironmentCandidates = ["client-vm"];
        campaign.Clients["client-b"].EnvironmentCandidates = ["client-pc"];
        var result = inventory.Resolve(campaign);
        Assert.Equal("vm", result.Profile.Clients["client-a"].Host);
        Assert.Null(result.Profile.SteamAccounts!.Accounts);
        Assert.Null(result.Profile.Clients["client-a"].SteamAccount);
    }

    [Fact]
    public void RefusesUnavailableDistinctHosts()
    {
        var inventory = Inventory();
        inventory.Validate(_root);
        var campaign = Campaign();
        campaign.Clients["client-a"].EnvironmentCandidates = ["client-mac"];
        campaign.Clients["client-b"].EnvironmentCandidates = ["client-mac"];
        Assert.Contains("No environment assignment", Assert.Throws<ArgumentException>(() => inventory.Resolve(campaign)).Message);

    }

    [Theory]
    [InlineData("hosting-client")]
    [InlineData("server,client")]
    public void RecipeRolesAreOneServerOrOneClient(string roles)
    {
        var inventory = Inventory();
        inventory.Environments[1].Roles = [.. roles.Split(',')];
        Assert.Contains("roles [\"server\"] or [\"client\"]", Assert.Throws<ArgumentException>(() => inventory.Validate(_root)).Message);
    }

    [Fact]
    public void ActorsOnOneHostNeverShareACliPort()
    {
        var inventory = Inventory();
        inventory.Environments[1].CliPort = 5501; // client-pc on the server's port on the same host
        inventory.Validate(_root);
        var campaign = Campaign();
        campaign.Clients["client-a"].EnvironmentCandidates = ["client-pc"];
        string message = Assert.Throws<ArgumentException>(() => inventory.Resolve(campaign)).Message;
        Assert.Contains("ValheimCLI port conflicts on host pc", message);
    }

    [Fact]
    public void InventoryRejectsCredentialFieldsBeforeDeserializing()
    {
        var inventory = Inventory();
        string json = JsonSerializer.Serialize(inventory, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        json = json.Replace("\"name\":\"client-pc\"", "\"name\":\"client-pc\",\"password\":\"never-log-this\"");
        string file = Path.Combine(_root, "inventory.json");
        File.WriteAllText(file, json);
        string message = Assert.Throws<JsonException>(() => EnvironmentInventory.Read(file)).Message;
        Assert.Contains("password", message);
        Assert.DoesNotContain("never-log-this", message);
    }

    [Theory]
    [InlineData("steamAccount", "old_name")]
    [InlineData("steamId", "76561197960265729")]
    public void InventoryRejectsRedundantAccountFields(string field, string value)
    {
        string json = JsonSerializer.Serialize(Inventory(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        json = json.Replace("\"name\":\"client-pc\"", $"\"name\":\"client-pc\",\"{field}\":\"{value}\"");
        string file = Path.Combine(_root, "inventory.json");
        File.WriteAllText(file, json);
        string message = Assert.Throws<JsonException>(() => EnvironmentInventory.Read(file)).Message;
        Assert.Contains(field, message);
        Assert.DoesNotContain(value, message);
    }

    [Fact]
    public void UnpreparedInventoryProfileCannotBeSerializedAsReady()
    {
        var inventory = Inventory();
        inventory.Validate(_root);
        var profile = inventory.Resolve(Campaign()).Profile;
        string file = Path.Combine(_root, "profile.json");
        File.WriteAllText(file, JsonSerializer.Serialize(profile, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Throws<ArgumentException>(() => EnvironmentProfile.Read(file));
    }

    [Fact]
    public void ObservedAccountsBecomeAValidPrivateProfileOnlyAfterDistinctIdentityChecks()
    {
        var inventory = Inventory();
        inventory.Validate(_root);
        var profile = inventory.Resolve(Campaign()).Profile;
        string first = SteamPoolAccount.SteamId64(101), second = SteamPoolAccount.SteamId64(202);
        Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.CompleteObservedSteamAccounts(profile,
            new Dictionary<string, string> { ["client-a"] = first, ["client-b"] = first }));
        HostedCampaignPreparation.CompleteObservedSteamAccounts(profile,
            new Dictionary<string, string> { ["client-a"] = first, ["client-b"] = second });
        string file = Path.Combine(_root, "prepared-profile.json");
        File.WriteAllText(file, JsonSerializer.Serialize(profile,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var parsed = EnvironmentProfile.Read(file);
        Assert.Equal(2, parsed.SteamAccounts!.Accounts!.Accounts.Count);
        Assert.Equal(SteamPoolAccount.LeaseKey(first), parsed.Clients["client-a"].SteamAccount);
        Assert.Equal(SteamPoolAccount.LeaseKey(second), parsed.Clients["client-b"].SteamAccount);
        Assert.DoesNotContain(first, parsed.Clients["client-a"].SteamAccount!);
        Assert.True(parsed.SteamAccounts.CheckSignedIn);
    }

    [Fact]
    public void ClientInventoryRequiresSharedLeaseLocation()
    {
        var inventory = Inventory();
        inventory.LeaseHost = "";
        Assert.Contains("leaseHost", Assert.Throws<ArgumentException>(() => inventory.Validate(_root)).Message);
        inventory.LeaseHost = "pc";
        inventory.LeaseDirectory = "relative-leases";
        Assert.Contains("leaseDirectory", Assert.Throws<ArgumentException>(() => inventory.Validate(_root)).Message);
    }

    [Fact]
    public void ObservedSteamIdentityHasStablePrivateLeaseKey()
    {
        string id = SteamPoolAccount.SteamId64(101);
        string key = SteamPoolAccount.LeaseKey(id);
        Assert.Equal(key, SteamPoolAccount.LeaseKey(id));
        Assert.NotEqual(key, SteamPoolAccount.LeaseKey(SteamPoolAccount.SteamId64(202)));
        Assert.DoesNotContain(id, key);
        Assert.Matches("^steam_[a-f0-9]{40}$", key);
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
        inventory.LeaseDirectory = "/tmp/test-leases";
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
        inventory.Environments[0].LoaderPackage = "missing-loader.json";
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
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "loader");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.Equal(["server-pc", "client-pc", "client-vm"], report.Actors.Select(actor => actor.Environment));
        Assert.All(report.Actors, actor => Assert.NotNull(actor.SelectionReason));
    }

    [Fact]
    public async Task HostPreflightDiscoversAccountsAndRefusesTwoClientsOnOneSteamIdentity()
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
        var hosts = new Dictionary<string, FakeServerHost>
        {
            ["pc"] = new("pc", Path.Combine(_root, "pc"), windows: true) { SteamUserReply = "VT-STEAMUSER account 1\n" },
            ["vm"] = new("vm", Path.Combine(_root, "vm"), windows: true) { SteamUserReply = "VT-STEAMUSER account 1\n" },
            ["mac"] = new("mac", Path.Combine(_root, "mac"), windows: true) { SteamUserReply = "VT-STEAMUSER account 2\n" },
        };
        var report = await HostedCampaignPreparation.InspectAsync(campaignFile, TimeSpan.FromSeconds(3), name => hosts[name]);
        Assert.Contains(report.Problems, problem => problem.Input == "Steam identities" && problem.Message.Contains("same signed-in Steam account"));
        Assert.All(hosts.Values, host => Assert.DoesNotContain(host.Scripts, script => script is "copy" or "start"));

        hosts["vm"].SteamUserReply = "VT-STEAMUSER account 2\n";
        report = await HostedCampaignPreparation.InspectAsync(campaignFile, TimeSpan.FromSeconds(3), name => hosts[name]);
        Assert.DoesNotContain(report.Problems, problem => problem.Input is "Steam identity" or "Steam identities");
    }
}
