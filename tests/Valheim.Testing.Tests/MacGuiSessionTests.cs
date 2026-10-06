using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

public sealed class MacGuiSessionTests
{
    private const string Unlocked = "\"IOConsoleUsers\" = ({\"kCGSSessionOnConsoleKey\"=Yes,\"kCGSessionLoginDoneKey\"=Yes,\"kCGSSessionUserNameKey\"=\"tester\"})";

    [Fact]
    public void UnlockedConsoleUserInAquaCanLaunch()
    {
        MacGuiSession.RequireEvidence("tester", "Aqua\n", "tester\n", Unlocked);
    }

    [Theory]
    [InlineData("Background", "tester", Unlocked)]
    [InlineData("Aqua", "other", Unlocked)]
    [InlineData("Aqua", "tester", "\"IOConsoleUsers\" = ({\"kCGSSessionOnConsoleKey\"=Yes,\"kCGSessionLoginDoneKey\"=Yes,\"kCGSSessionUserNameKey\"=\"tester\",\"CGSSessionScreenIsLocked\"=Yes})")]
    [InlineData("Aqua", "tester", "\"IOConsoleUsers\" = ({\"kCGSSessionOnConsoleKey\"=No,\"kCGSessionLoginDoneKey\"=Yes,\"kCGSSessionUserNameKey\"=\"tester\"})")]
    [InlineData("Aqua", "tester", "\"IOConsoleUsers\" = ({\"kCGSSessionOnConsoleKey\"=Yes,\"kCGSessionLoginDoneKey\"=Yes,\"kCGSSessionUserNameKey\"=\"other\"})")]
    public void RefusesRemoteLockedOrWrongConsole(string manager, string consoleUser, string registry)
    {
        Assert.Throws<InvalidOperationException>(() => MacGuiSession.RequireEvidence("tester", manager, consoleUser, registry));
    }
}
