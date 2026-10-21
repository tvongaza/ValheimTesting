using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// #860: an install's Valheim build, read from its game assembly's metadata, and the one decision whether a campaign's installs can join.
public sealed class GameAssemblyVersionTests
{
    private static GameBuild Read(byte[] image) => GameAssemblyVersion.Read(new MemoryStream(image), "assembly_valheim.dll on host");

    [Fact]
    public void TheNetworkVersionIsTheConstantAndTheGameVersionIsTheOneStoredInCurrentVersion()
    {
        // The static constructor first builds FirstVersionWithNetworkVersion (0.214.301), as the game's does: that is not the version.
        Assert.Equal(new GameBuild(40, "1.0.17", null), Read(FakeInstalls.GameAssembly()));
        Assert.Equal(new GameBuild(41, "1.0.20", null), Read(FakeInstalls.GameAssembly("1.0.20", 41)));
        Assert.Equal("Valheim 1.0.20 (network version 41)", Read(FakeInstalls.GameAssembly("1.0.20", 41)).ToString());
    }

    [Fact]
    public void AGameVersionBuiltFromAnythingButThreeIntsIsLeftUnreadAndTheNetworkVersionStillDecides()
    {
        Assert.Equal(new GameBuild(41, null, null), Read(FakeInstalls.GameAssembly("1.0.20.3", 41)));
        Assert.Equal("Valheim (game version unread) (network version 41)", Read(FakeInstalls.GameAssembly("1.0.20.3", 41)).ToString());
    }

    [Fact]
    public void AnythingButTheGamesShapeIsAnUnknownBuildSayingWhy()
    {
        Assert.Contains("is not a readable .NET assembly", Read("game build 1"u8.ToArray()).Unknown);
        Assert.Contains("is not a readable .NET assembly", Read(FakeInstalls.GameAssembly()[..300]).Unknown);
        // A .NET assembly without the game's Version type, as BepInEx's core holds.
        var library = Read(ClientLaunchTests.ManagedAssembly("MonoMod.RuntimeDetour", new Version(22, 1)));
        Assert.Null(library.NetworkVersion);
        Assert.Equal("assembly_valheim.dll on host declares no Version type", library.Unknown);
        Assert.StartsWith("an unknown Valheim build (", library.ToString());
    }

    [Fact]
    public void ANetworkVersionThatIsNotAConstUintIsUnknown()
    {
        Assert.Equal("assembly_valheim.dll on host's Version.c_networkVersion is Int32, not a uint",
            Read(FakeInstalls.GameAssembly(networkConstant: 40)).Unknown);
        Assert.Equal("assembly_valheim.dll on host has no const Version.c_networkVersion",
            Read(FakeInstalls.GameAssembly(networkConstant: null)).Unknown);
    }
}

public sealed class GameVersionAgreementTests
{
    private static readonly GameBuild Old = new(40, "1.0.17", null), New = new(41, "1.0.20", null);
    private static GameVersionAgreement.Side Side(string actor, GameBuild? build, string host = "pc") =>
        new(actor, host, actor == "server" ? @"C:\game" : @"C:\client-" + actor, build);

    [Fact]
    public void InstallsOnOneNetworkVersionAgreeWhateverTheirGameVersions()
    {
        Assert.Empty(GameVersionAgreement.Decide([Side("server", Old), Side("client", Old)]));
        Assert.Empty(GameVersionAgreement.Decide([Side("server", Old), Side("client", new GameBuild(40, "1.0.18", null))]));
        Assert.Empty(GameVersionAgreement.Decide([Side("server", Old), Side("client", Old with { GameVersion = null })]));
    }

    [Fact]
    public void AClientOnAnotherNetworkVersionIsRefusedNamingBothInstallsAndWhatToDo()
    {
        var problem = Assert.Single(GameVersionAgreement.Decide([Side("client", New, "laptop"), Side("server", Old)]));
        Assert.Equal(("client", "game version"), (problem.Actor, problem.Input));
        Assert.Equal(@"client's install C:\client-client on laptop is Valheim 1.0.20 (network version 41), but server's install C:\game on pc is " +
            "Valheim 1.0.17 (network version 40). Valheim refuses a join across network versions, so this is refused before anything is staged. " +
            "Update the older install (Steam, or SteamCMD for a dedicated server), or choose environments whose installs share one network version.",
            problem.Message);
        // The server is the reference even when it is the newer one.
        Assert.Equal("client", Assert.Single(GameVersionAgreement.Decide([Side("server", New), Side("client", Old)])).Actor);
    }

    [Fact]
    public void WithNoServerTheClientsAgreeWithTheFirstByName()
    {
        var problems = GameVersionAgreement.Decide([Side("joiner2", New), Side("host", Old), Side("joiner1", Old)]);
        Assert.Equal("joiner2", Assert.Single(problems).Actor);
        Assert.Contains("but host's install", problems[0].Message);
    }

    [Fact]
    public void AnUnknownBuildIsRefusedAndNeverInferred()
    {
        var problems = GameVersionAgreement.Decide([Side("server", new GameBuild(null, null, "the file is missing")), Side("client", New)]);
        var problem = Assert.Single(problems);
        Assert.Equal(("server", "game version"), (problem.Actor, problem.Input));
        Assert.StartsWith(@"The network version of server's install C:\game on pc is unknown: the file is missing. Compatibility is never inferred " +
            "from Steam app or build IDs", problem.Message);
        // Both unknown: each is refused, and nothing is compared.
        Assert.Equal(["client", "server"], GameVersionAgreement.Decide([Side("server", new GameBuild(null, null, "x")), Side("client", new GameBuild(null, null, "y"))])
            .Select(found => found.Actor).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheLedgerAdmitsActorsThatCanJoinAndRefusesOneThatCannotWithTheSameDecision()
    {
        var builds = new GameVersionAgreement.Ledger();
        Assert.Equal(Old, builds.Admit(Side("client", Old)));
        Assert.Equal(Old, builds.Admit(Side("server", Old)));
        // A server that cannot take the admitted client: refused naming both, and not admitted.
        var refused = Assert.Throws<InvalidOperationException>(() => builds.Admit(Side("server", New)));
        Assert.StartsWith(@"client's install C:\client-client on pc is Valheim 1.0.17 (network version 40), but server's install C:\game on pc is Valheim 1.0.20", refused.Message);
        Assert.Equal(Old, builds.Of("server"));
        // One actor alone is never refused; its unknown build is, once another arrives.
        var alone = new GameVersionAgreement.Ledger();
        alone.Admit(Side("server", new GameBuild(null, null, "unreadable")));
        Assert.Contains("is unknown: unreadable", Assert.Throws<InvalidOperationException>(() => alone.Admit(Side("client", Old))).Message);
        // A campaign's decided builds are recorded as they are.
        var seeded = new GameVersionAgreement.Ledger();
        seeded.Seed([Side("server", Old), Side("client", null)]);
        Assert.Equal(Old, seeded.Of("server"));
        Assert.Null(seeded.Of("client"));
    }

    [Fact]
    public void OneActorOrAnActorWhoseInstallCheckFailedAddsNothing()
    {
        Assert.Empty(GameVersionAgreement.Decide([Side("server", new GameBuild(null, null, "x"))]));
        // The failed install check already refuses the campaign; its build is not guessed.
        Assert.Empty(GameVersionAgreement.Decide([Side("server", null), Side("client", New)]));
    }
}

// The real preflight read script on this OS's shell, and the in-place read on this machine, which the fake hosts do not run.
[Trait("Category", "CiShell")]
public sealed class GameAssemblyReadShellTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("game-assembly-read-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);
    public static TheoryData<string> Shells => LocalGameHostShellTests.Shells;

    [Fact]
    public async Task AnInstallThatRunsInPlaceIsReadWhereItIs()
    {
        FakeInstalls.Client(_root);
        Assert.Equal(new GameBuild(40, "1.0.17", null), await GameVersionAgreement.ReadInPlaceAsync(_root, CancellationToken.None));
        File.WriteAllBytes(Path.Combine(_root, "valheim_Data", "Managed", InstallPins.GameAssemblyName), FakeInstalls.GameAssembly("1.0.20", 41));
        var builds = new GameVersionAgreement.Ledger();
        builds.Admit(new("server", "pc", @"C:\game", new GameBuild(40, "1.0.17", null)));
        Assert.Contains("is Valheim 1.0.20 (network version 41), but server's install", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builds.AdmitInPlaceAsync("client", _root, CancellationToken.None))).Message);
        var missing = await GameVersionAgreement.ReadInPlaceAsync(Path.Combine(_root, "missing"), CancellationToken.None);
        Assert.Null(missing.NetworkVersion);
        Assert.Contains("missing", missing.Unknown);
    }

    [Theory, MemberData(nameof(Shells))]
    public async Task TheHostReadReturnsTheBytesWithinItsLimitAndRefusesALargerFile(string shell)
    {
        var host = new LocalGameHost("local-" + shell, HostShell.Parse(shell));
        string file = Path.Combine(_root, "assembly_valheim.dll");
        byte[] image = FakeInstalls.GameAssembly("1.0.20", 41);
        File.WriteAllBytes(file, image);
        Assert.Equal(image, await HostClientPreflight.Read(host, file, GameHostChecks.Generous, CancellationToken.None, limit: image.Length));
        var refused = await Assert.ThrowsAsync<IOException>(() => HostClientPreflight.Read(host, file, GameHostChecks.Generous, CancellationToken.None, limit: image.Length - 1));
        Assert.Equal($"{file} on {host.Name} exceeds the {image.Length - 1}-byte safety limit.", refused.Message);
        Assert.Null(await HostClientPreflight.Read(host, file + ".missing", GameHostChecks.Generous, CancellationToken.None));
    }

    [Fact]
    public async Task OnThisMachineTheListedAssemblyIsReadInPlaceAndMustStillHashAsListed()
    {
        var host = new LocalGameHost("local", OperatingSystem.IsWindows() ? HostShell.Parse("pwsh") : HostShell.Parse("bash"));
        FakeInstalls.Server(_root);
        string assembly = Path.Combine(_root, "valheim_server_Data", "Managed", InstallPins.GameAssemblyName);
        var listing = new HostListing(host.Name, host.Shell.Kind, _root, new Dictionary<string, string>
        {
            ["valheim_server_Data/Managed/assembly_valheim.dll"] = FileHash.Sha256(assembly),
        }, []);
        Assert.Equal(new GameBuild(40, "1.0.17", null), await GameVersionAgreement.ReadAsync(host, listing, GameHostChecks.Generous, CancellationToken.None));
        File.WriteAllBytes(assembly, FakeInstalls.GameAssembly("1.0.20", 41));
        Assert.EndsWith("changed after the install was listed", (await GameVersionAgreement.ReadAsync(host, listing, GameHostChecks.Generous, CancellationToken.None)).Unknown);
        File.Delete(assembly);
        Assert.EndsWith("is missing", (await GameVersionAgreement.ReadAsync(host, listing, GameHostChecks.Generous, CancellationToken.None)).Unknown);
    }
}
