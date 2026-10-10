using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class HostedCampaignProfileChoiceTests
{
    [Theory]
    [InlineData("local", "windows", false, true)]
    [InlineData("local", "windows", true, false)]
    [InlineData("ssh", "windows", false, true)]
    [InlineData("ssh", "windows", true, false)]
    [InlineData("local", "macos", false, true)]
    [InlineData("local", "macos", true, false)]
    [InlineData("local", "linux", false, true)]
    [InlineData("local", "linux", true, false)]
    [InlineData("ssh", "macos", false, false)]
    [InlineData("ssh", "linux", false, true)]
    [InlineData("ssh", "linux", true, false)]
    [InlineData("container", "linux", false, true)]
    [InlineData("container", "linux", true, false)]
    public void ProfileAppliesToSupportedActorsWithoutCopyOverride(
        string kind, string platform, bool copyGame, bool expected)
    {
        Assert.Equal(expected, HostedCampaignPreparation.UseProfile(
            new HostProfile { Kind = kind, Platform = platform }, copyGame));
    }

    [Fact]
    public void UnixCampaignBindsTheSourceGameAndOwnedLoaderAsOnePinnedServer()
    {
        string output = Directory.CreateTempSubdirectory("campaign-profile-binding-").FullName;
        try
        {
            string world = Path.Combine(output, "fixture");
            FakeInstalls.World(world, "Profile", seed: "Seed");
            const string source = "/games/valheim-server", loader = "/owned/run/server/runtime";
            var gameFiles = new Dictionary<string, string> { ["valheim_server/Valheim"] = new('a', 64),
                ["valheim_server/Data/Managed/assembly_valheim.dll"] = new('b', 64) };
            var loaderFiles = new Dictionary<string, string> { ["libdoorstop.dylib"] = new('c', 64),
                ["BepInEx/core/BepInEx.dll"] = new('d', 64) };
            var game = new HostListing("local", HostShellKind.Bash, source, gameFiles, ["valheim_server/Valheim"]);
            var profile = new HostListing("local", HostShellKind.Bash, loader, loaderFiles, []);
            var environment = new ResolvedEnvironment
            {
                Hosts = new() { ["local"] = new HostProfile { Kind = "local", Platform = "macos", Shell = "bash" } },
                Server = new GameRole { Host = "local", Install = loader, Runtime = "/owned", CliPort = 5577,
                    GamePort = 2456, PreparedGameRoot = source, PreparedLoaderRoot = loader },
            };
            var manifest = new HostedCampaignManifest { World = world, Join = "127.0.0.1:2456" };
            var campaign = new PreparedHostedCampaign(manifest, environment,
                new Dictionary<string, HostListing> { ["server"] = game },
                new Dictionary<string, HostListing> { ["server"] = profile },
                new Dictionary<string, HostedRuntimeFile[]> { ["server"] = [] }, [], [], null,
                TimeSpan.FromSeconds(5), new RunJournal("profile-binding"));
            var plan = new ServerRunPlan();
            campaign.ApplyTo(plan, manifest, new Dictionary<string, ClientRunPlan>(), Path.Combine(output, "bound"));
            Assert.Equal(source, plan.Runtime.Source);
            Assert.Equal(gameFiles, plan.Runtime.Sha256);
            Assert.Equal(HostInstall.Pins(game, profile).Game, plan.RuntimePins!.Game);
            Assert.Equal(HostInstall.Pins(game, profile).Loader, plan.RuntimePins.Loader);
            Assert.Equal("server:profile", campaign.LaunchModes(copyGame: false));
        }
        finally { Directory.Delete(output, recursive: true); }
    }
}
