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

    private string Profile(bool client)
    {
        return Write("profile.json", new
        {
            hosts = new { pc = new { kind = "ssh", platform = "windows", shell = "powershell", @lock = @"C:\locks\test.lock", destination = "test@pc" } },
            server = new { host = "pc", install = @"C:\game", runtime = @"C:\runs", cliPort = 5577, localCliPort = 6577, gamePort = 2456 },
            clients = client ? new Dictionary<string, object>
            {
                ["client-a"] = new { host = "pc", install = @"C:\game", runtime = @"C:\runs", cliPort = 5578, localCliPort = 6578, steamAccount = "test_a" },
            } : new Dictionary<string, object>(),
        });
    }

    [Fact]
    public async Task IndependentFaultsAcrossRolesAreAllReportedBeforeContactingAnyHost()
    {
        string profile = Profile(client: true); // No account pool: profile is invalid independently of the files below.
        string file = Write("campaign.json", new
        {
            profile, world = Path.Combine(_root, "missing-world"),
            server = new { dependencyLock = "missing-server-lock.json", loaderPackage = "missing-loader.json" },
            clients = new Dictionary<string, object> { ["client-a"] = new { dependencyLock = "missing-client-lock.json" } },
        });
        var report = HostedCampaignPreparation.Inspect(file);
        Assert.False(report.Ready);
        Assert.Contains(report.Problems, problem => problem.Actor == "campaign" && problem.Input == "profile");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "loader");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "dependencies and CLI packs");
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "character");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "world fixture");
        Assert.True(report.Problems.Count >= 6);
        using var output = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", file], output, new StringWriter()));
        Assert.Empty(report.Actors); // Invalid profiles have no selected actor, even when other inputs can be checked.
        Assert.Contains("REFUSED server loader", output.ToString());
        Assert.Contains("REFUSED client-a character", output.ToString());
        var error = Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.Check(file));
        Assert.Contains("client-a character", error.Message);
        Assert.Contains("server loader", error.Message);
    }

    [Fact]
    public async Task ReviewedFixtureUidIsCheckedIndependentlyOfMissingPackages()
    {
        string profile = Profile(client: false);
        string world = Path.Combine(_root, "world");
        Directory.CreateDirectory(world);
        using (var payload = new MemoryStream())
        {
            using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
            { writer.Write(41); writer.Write("Small"); writer.Write("Seed"); writer.Write(1234); writer.Write(4242L); }
            File.WriteAllBytes(Path.Combine(world, "Small.fwl"), [.. BitConverter.GetBytes((int)payload.Length), .. payload.ToArray()]);
        }
        File.WriteAllText(Path.Combine(world, "Small.db"), "fixture");
        string file = Write("campaign.json", new
        {
            profile, world, worldUid = "9999", server = new { dependencyLock = "missing-lock.json" },
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
        var error = new StringWriter();
        Assert.Equal(2, await EnvCommand.RunAsync([], new StringWriter(), error));
        Assert.Contains("Usage: valheim-test env", error.ToString());
    }

    [Fact]
    public async Task ReadOnlyHostCheckKeepsIndependentLocalSessionAndInstallFailures()
    {
        string profile = Profile(client: false);
        string file = Write("campaign.json", new
        {
            profile, server = new { dependencyLock = "missing-lock.json" }, clients = new Dictionary<string, object>(),
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
    }

    [Fact]
    public async Task MissingAccountPoolIsReportedWithoutHidingOtherHostFaults()
    {
        string profile = Profile(client: true);
        var json = JsonNode.Parse(File.ReadAllText(profile))!;
        json["clients"]!["client-a"]!.AsObject().Remove("steamAccount");
        File.WriteAllText(profile, json.ToJsonString());
        string file = Write("campaign.json", new
        {
            profile, server = new { dependencyLock = "missing-server-lock.json" },
            clients = new Dictionary<string, object> { ["client-a"] = new { dependencyLock = "missing-client-lock.json" } },
        });
        var host = new FakeServerHost("pc", Path.Combine(_root, "mirror"), windows: true);
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.Contains(report.Problems, problem => problem.Actor == "clients" && problem.Input == "Steam identities");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "game and loader");
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "game and loader");
    }
}
