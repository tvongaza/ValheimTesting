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
        MacGuiSession.RequireRemoteEvidence("tester", "tester\n", guiDomain: true, Unlocked);
    }

    [Fact]
    public void RemoteMacNeedsItsOwnUnlockedConsoleAndGuiDomain()
    {
        Assert.Throws<InvalidOperationException>(() => MacGuiSession.RequireRemoteEvidence("tester", "tester", false, Unlocked));
        Assert.Throws<InvalidOperationException>(() => MacGuiSession.RequireRemoteEvidence("tester", "other", true, Unlocked));
        Assert.Throws<InvalidOperationException>(() => MacGuiSession.RequireRemoteEvidence("tester", "tester", true,
            Unlocked.Replace("})", ",\"CGSSessionScreenIsLocked\"=Yes})", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RemoteMacDesktopProbeRefusesALockedSessionBeforeStaging()
    {
        var fake = new FakeLauncher().Exits(0,
            "VT-MAC-USER tester\nVT-MAC-CONSOLE tester\nVT-MAC-GUI ready\n" +
            Unlocked.Replace("})", ",\"CGSSessionScreenIsLocked\"=Yes})", StringComparison.Ordinal) +
            "\nVT-MAC-REGISTRY-END\n", FakeLauncher.Report(0));
        var host = new SshGameHost("mac-box", "tester@mac-box.example", HostShell.Bash, 0, null, null, "ssh", fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteMacClientPreflight.RequireDesktopAsync(
            host, TimeSpan.FromSeconds(15), CancellationToken.None));
        Assert.Single(fake.Calls);
        Assert.DoesNotContain("mkdir", FakeLauncher.Script(fake.Calls[0]));
    }

    [Fact]
    public async Task RemoteMacPreflightSelectsAnX64LoaderAndRejectsAMissingArmSlice()
    {
        var fake = new FakeLauncher()
            .Exits(0, "VT-MAC-GAME x86_64 arm64\n", FakeLauncher.Report(0))
            .Exits(0, "VT-MAC-DOORSTOPS doorstop_libs/libdoorstop_x64.dylib|x86_64;libdoorstop.dylib|arm64;\n", FakeLauncher.Report(0))
            .Exits(0, "VT-MAC-GAME x86_64\n", FakeLauncher.Report(0));
        var host = new SshGameHost("mac-box", "tester@mac-box.example", HostShell.Bash, 0, null, null, "ssh", fake);
        await RemoteMacClientPreflight.RequireGameAsync(host, "/Users/test/Valheim", ClientArchitecture.X64,
            TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.Equal("doorstop_libs/libdoorstop_x64.dylib", await RemoteMacClientPreflight.SelectLoaderAsync(host,
            "/Users/test/profile", ClientArchitecture.X64, TimeSpan.FromSeconds(15), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteMacClientPreflight.RequireGameAsync(host,
            "/Users/test/Valheim", ClientArchitecture.Arm64, TimeSpan.FromSeconds(15), CancellationToken.None));
    }

    [MacLockedFact]
    public async Task TheRealMacConsoleLockIsRefusedBeforeAnyProfileIsStaged()
    {
        var host = new LocalGameHost("mac-locked-control", HostShell.Bash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteMacClientPreflight.RequireDesktopAsync(
            host, TimeSpan.FromSeconds(15), CancellationToken.None));
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

public sealed class MacLockedFactAttribute : FactAttribute
{
    public MacLockedFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("VALHEIM_TESTING_MAC_LOCKED_CONTROL") != "1")
            Skip = "Opt-in control on a locked Mac console";
    }
}
