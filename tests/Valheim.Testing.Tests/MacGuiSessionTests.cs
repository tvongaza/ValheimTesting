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
        await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteMacClientPreflight.RequireGameAsync(host,
            "/Users/test/Valheim", ClientArchitecture.Arm64, TimeSpan.FromSeconds(15), CancellationToken.None));
    }

    // #657: one game-slice decision and one Doorstop selector. A Mac on this machine, a Mac host (its slices read with lipo)
    // and a reviewed loader package before staging give the same verdict with the same refusal; only the place named differs.
    [Fact]
    public async Task ThisMachineAHostAndAPackageShareOneMacSliceVerdict()
    {
        using var x64Game = ClientLaunchTests.Install.Mac(game: ClientLaunchTests.Thin(ClientLaunchTests.X86_64), universalDoorstop: true,
            core: ClientLaunchTests.NativeDetour);
        string local = Assert.Throws<InvalidOperationException>(() => GameLaunch.RequireMacArchitecture(x64Game.Root, ClientArchitecture.Arm64)).Message;
        using var pack = ClientLaunchTests.Install.Mac(core: ClientLaunchTests.NativeDetour); // Universal game, the pack's x64-only Doorstop.
        string localLoader = Assert.Throws<InvalidOperationException>(() => GameLaunch.RequireMacArchitecture(pack.Root, ClientArchitecture.Arm64)).Message;
        Assert.Equal("doorstop_libs/libdoorstop_x64.dylib", Path.GetRelativePath(pack.Root, GameLaunch.RequireMacArchitecture(pack.Root, ClientArchitecture.X64)).Replace('\\', '/'));

        var fake = new FakeLauncher()
            .Exits(0, "VT-MAC-GAME x86_64\n", FakeLauncher.Report(0))
            .Exits(0, "VT-MAC-GAME missing\n", FakeLauncher.Report(0))
            .Exits(0, "VT-MAC-DOORSTOPS doorstop_libs/libdoorstop_x64.dylib|x86_64;\n", FakeLauncher.Report(0))
            .Exits(0, "VT-MAC-DOORSTOPS \n", FakeLauncher.Report(0));
        var host = new SshGameHost("mac-box", "tester@mac-box.example", HostShell.Bash, 0, null, null, "ssh", fake);
        var timeout = TimeSpan.FromSeconds(15);
        string remote = (await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteMacClientPreflight.RequireGameAsync(host,
            "/Users/test/Valheim", ClientArchitecture.Arm64, timeout, CancellationToken.None))).Message;
        Assert.Equal(local[local.IndexOf(" has no ", StringComparison.Ordinal)..], remote[remote.IndexOf(" has no ", StringComparison.Ordinal)..]);
        Assert.StartsWith("/Users/test/Valheim/Valheim.app/Contents/MacOS/Valheim on mac-box has no arm64 slice (found: x86_64).", remote);
        await Assert.ThrowsAsync<FileNotFoundException>(() => RemoteMacClientPreflight.RequireGameAsync(host,
            "/Users/test/Valheim", ClientArchitecture.X64, timeout, CancellationToken.None));
        string remoteLoader = (await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteMacClientPreflight.SelectLoaderAsync(host,
            "/Users/test/profile", ClientArchitecture.Arm64, timeout, CancellationToken.None))).Message;
        Assert.Equal(localLoader.Replace("in the install", "in the Mac client loader on mac-box", StringComparison.Ordinal), remoteLoader);
        Assert.Contains("missing from the Mac client loader on mac-box", (await Assert.ThrowsAsync<FileNotFoundException>(() =>
            RemoteMacClientPreflight.SelectLoaderAsync(host, "/Users/test/profile", ClientArchitecture.X64, timeout, CancellationToken.None))).Message);

        var package = BepInExLoaderPackage.Capture(pack.Root, "BepInExPack_Valheim", "5.4.2333");
        Assert.Equal("doorstop_libs/libdoorstop_x64.dylib", RemoteMacClientPreflight.SelectPackage(package, ClientArchitecture.X64));
        Assert.Equal(localLoader.Replace("in the install", "in the remote Mac client loader package", StringComparison.Ordinal),
            Assert.Throws<InvalidOperationException>(() => RemoteMacClientPreflight.SelectPackage(package, ClientArchitecture.Arm64)).Message);
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
