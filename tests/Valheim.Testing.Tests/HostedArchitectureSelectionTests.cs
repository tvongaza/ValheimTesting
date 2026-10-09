using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class HostedArchitectureSelectionTests
{
    [Theory]
    [InlineData("x64", "arm64", "x64")]
    [InlineData("", "arm64", "arm64")]
    [InlineData("arm64", "x64", "arm64")]
    public void PreflightChecksTheSliceTheCampaignWillLaunch(string requested, string environment, string expected)
    {
        var input = new HostedCampaignRole { Architecture = requested };
        var role = new GameRole { Install = "/disposable/Valheim.app", Architecture = environment };
        string? observed = null;
        HostedCampaignPreparation.RequireLocalMacClientArchitecture(input, role, "loader.json",
            (install, selected, manifest) =>
            {
                Assert.Equal(role.Install, install);
                Assert.Equal("loader.json", manifest);
                observed = selected;
            });
        Assert.Equal(expected, observed);
    }
}
