using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class LocalHostPreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("local-preflight-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private EnvironmentInventory Inventory(bool withClient = true)
    {
        var inventory = new EnvironmentInventory
        {
            Hosts = new() { ["here"] = new HostProfile
            {
                Kind = "local", Platform = "macos", Shell = "bash", Lock = Path.Combine(_root, "host.lock"),
            } },
            Environments = [new EnvironmentRecipe { Name = "server", Host = "here", Roles = ["server"],
                Install = Path.Combine(_root, "server"), Runtime = Path.Combine(_root, "runs", "server"), CliPort = 5551 }],
        };
        if (withClient) inventory.Environments.Add(new EnvironmentRecipe { Name = "client", Host = "here", Roles = ["client"],
            Install = Path.Combine(_root, "client"), Runtime = Path.Combine(_root, "runs", "client"), CliPort = 5552 });
        return inventory;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChecksEachSelectedLocalActorAndOnlyNeedsSteamForAClient(bool withClient)
    {
        var inventory = Inventory(withClient);
        var host = new FakeServerHost("here", Path.Combine(_root, "mirror"));
        var ports = new List<int>();
        var processKinds = new List<bool>();
        int steam = 0, desktop = 0, journal = 0;
        var probes = new LocalHostPreflight.Probes(
            Processes: (_, client, _, _) => { processKinds.Add(client); return Task.CompletedTask; },
            Port: (_, port, _, _) => { ports.Add(port); return Task.CompletedTask; },
            Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
            MacDesktop: () => desktop++, SteamRunning: () => { steam++; return true; },
            Journals: (_, _) => { journal++; return Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]); },
            Packaged: () => null);

        var problems = await LocalHostPreflight.InspectAsync(inventory,
            inventory.Environments.Select(recipe => new LocalHostPreflight.Actor(recipe.Name, recipe)),
            TimeSpan.FromSeconds(1), hostFactory: _ => host, probes: probes);

        Assert.Empty(problems);
        Assert.Equal(withClient ? [5551, 5552] : [5551], ports);
        Assert.Equal([withClient], processKinds); // one process scan for the shared host
        Assert.Equal(withClient ? 1 : 0, steam);
        Assert.Equal(withClient ? 1 : 0, desktop);
        Assert.Equal(1, journal);
    }

    [Fact]
    public async Task ReportsIndependentPreflightProblemsWithoutStoppingAtTheFirstOne()
    {
        var inventory = Inventory();
        var host = new FakeServerHost("here", Path.Combine(_root, "mirror"));
        var probes = new LocalHostPreflight.Probes(
            Processes: (_, _, _, _) => throw new InvalidOperationException("client already active"),
            Port: (_, port, _, _) => port == 5552 ? throw new InvalidOperationException("port busy") : Task.CompletedTask,
            Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.HeldByOther, "another", "held by another run")),
            MacDesktop: () => throw new InvalidOperationException("screen locked"),
            SteamRunning: () => false,
            Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([new("here", "run journal", "run not recovered")]),
            Packaged: () => "packaged app");

        var problems = await LocalHostPreflight.InspectAsync(inventory,
            inventory.Environments.Select(recipe => new LocalHostPreflight.Actor(recipe.Name, recipe)),
            TimeSpan.FromSeconds(1), hostFactory: _ => host, probes: probes);

        Assert.Equal(["packaged app", "run journal", "host lock", "client desktop", "Steam session", "session", "ValheimCLI port"],
            problems.Select(problem => problem.Input));
        Assert.Contains(problems, problem => problem.Actor == "client" && problem.Input == "ValheimCLI port" && problem.Message == "port busy");
    }
}
