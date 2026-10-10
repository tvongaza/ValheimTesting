using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

public sealed class HostCopyCapacityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("copy-capacity-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ReadsActualLocalSourceAndTargetVolumeWithoutMakingARuntime()
    {
        string source = Path.Combine(_root, "source"), runtime = Path.Combine(_root, "new", "runtime");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "game.bin"), new byte[32768]);
        var shell = OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash;
        var result = await HostCopyCapacityProbe.InspectAsync(new LocalGameHost("local", shell), source, runtime,
            TimeSpan.FromSeconds(20));
        Assert.True(result.SourceBytes >= 32768);
        Assert.True(result.FreeBytes > result.SourceBytes);
        Assert.False(Directory.Exists(runtime));
    }

    [Fact]
    public void TwoIndividuallyEligibleCopiesCanFailTogetherOnOneVolume()
    {
        var one = new HostCopyCapacity(3L << 30, 7L << 30, "C:\\");
        HostCopyCapacityProbe.RequireCombined("pc", [("server", one)]);
        HostCopyCapacityProbe.RequireCombined("pc", [("client", one)]);
        string message = Assert.Throws<IOException>(() => HostCopyCapacityProbe.RequireCombined("pc",
            [("server", one), ("client", one)])).Message;
        Assert.Contains("server, client", message);
        Assert.Contains("8.0 GB", message);
    }

    [Fact]
    public async Task CampaignPreflightReportsCopySpaceBeforeAnyHostWrite()
    {
        string inventory = Path.Combine(_root, "inventory.json");
        File.WriteAllText(inventory, """
            {"hosts":{"pc":{"kind":"ssh","platform":"windows","shell":"powershell","lock":"C:\\locks\\test.lock","destination":"test@pc"}},
             "environments":[{"name":"pc-server","host":"pc","roles":["server"],"install":"C:\\game","runtime":"C:\\runs","cliPort":5577,"localCliPort":6577,"gamePort":2456}]}
            """);
        string manifest = Path.Combine(_root, "campaign.json");
        File.WriteAllText(manifest, """
            {"inventory":"inventory.json","server":{"dependencyLock":"missing-lock.json"},"clients":{}}
            """);
        var host = new FakeServerHost("pc", Path.Combine(_root, "mirror"), windows: true)
        { AvailableCopyBytes = 0 };
        var profile = await HostedCampaignPreparation.InspectAsync(manifest, TimeSpan.FromSeconds(2), _ => host);
        Assert.DoesNotContain(profile.Problems, problem => problem.Input == "copy space");
        var report = await HostedCampaignPreparation.InspectAsync(manifest, TimeSpan.FromSeconds(2), _ => host, copyGame: true);
        Assert.Contains(report.Problems, problem => problem.Actor == "pc" && problem.Input == "copy space");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.DoesNotContain(host.Scripts, script => script is "copy" or "ship" or "start");
    }
}
