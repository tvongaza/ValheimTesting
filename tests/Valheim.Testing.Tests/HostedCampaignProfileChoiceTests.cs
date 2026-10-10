using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class HostedCampaignProfileChoiceTests
{
    [Theory]
    [InlineData("local", "windows", false, true)]
    [InlineData("local", "windows", true, false)]
    [InlineData("ssh", "windows", false, false)]
    [InlineData("local", "macos", false, false)]
    [InlineData("local", "linux", false, false)]
    public void WindowsProfileAppliesOnlyToLocalActorsWithoutCopyOverride(
        string kind, string platform, bool copyGame, bool expected)
    {
        Assert.Equal(expected, HostedCampaignPreparation.UseLocalWindowsProfile(
            new HostProfile { Kind = kind, Platform = platform }, copyGame));
    }
}
