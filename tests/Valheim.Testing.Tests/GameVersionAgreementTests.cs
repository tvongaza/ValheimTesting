using System.Reflection.Metadata;
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
        Assert.Equal((40u, "1.0.17", (string?)null), (Read(FakeInstalls.GameAssembly()).NetworkVersion, Read(FakeInstalls.GameAssembly()).GameVersion, Read(FakeInstalls.GameAssembly()).Unknown));
        Assert.Equal((41u, "1.0.20"), (Read(FakeInstalls.GameAssembly("1.0.20", 41)).NetworkVersion, Read(FakeInstalls.GameAssembly("1.0.20", 41)).GameVersion));
        Assert.Equal("Valheim 1.0.20 (network version 41)", Read(FakeInstalls.GameAssembly("1.0.20", 41)).ToString());
    }

    [Fact]
    public void AGameVersionBuiltFromAnythingButThreeIntsIsLeftUnreadAndTheNetworkVersionStillDecides()
    {
        Assert.Equal((41u, (string?)null), (Read(FakeInstalls.GameAssembly("1.0.20.3", 41)).NetworkVersion, Read(FakeInstalls.GameAssembly("1.0.20.3", 41)).GameVersion));
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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BrokenNetworkOrDisplayVersionMetadataCannotEraseVerifiedWorldSupport(bool network)
    {
        byte[] image = (byte[])FakeInstalls.GameAssembly(networkVersion: network ? 123u : 40u).Clone();
        using (var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(image)))
        {
            var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var version = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(type => reader.GetString(type.Name) == "Version" && type.GetDeclaringType().IsNil);
            if (network)
            {
                var field = version.GetFields().Select(reader.GetFieldDefinition).Single(field => reader.GetString(field.Name) == "c_networkVersion");
                var blob = reader.GetConstant(field.GetDefaultValue()).Value;
                int offset = pe.PEHeaders.MetadataStartOffset + System.Reflection.Metadata.Ecma335.MetadataReaderExtensions.GetHeapMetadataOffset(reader, System.Reflection.Metadata.Ecma335.HeapIndex.Blob) +
                    System.Reflection.Metadata.Ecma335.MetadataTokens.GetHeapOffset(blob);
                image[offset] = 3; // constant table still UInt32, but its blob is truncated
            }
            else
            {
                var method = version.GetMethods().Select(reader.GetMethodDefinition).Single(method => reader.GetString(method.Name) == ".cctor");
                var section = pe.PEHeaders.SectionHeaders.Single(section => method.RelativeVirtualAddress >= section.VirtualAddress && method.RelativeVirtualAddress < section.VirtualAddress + section.VirtualSize);
                int offset = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
                offset += (image[offset] & 3) == 2 ? 1 : ((image[offset + 1] << 8 | image[offset]) >> 12) * 4;
                int store = Array.IndexOf(image, (byte)0x80, offset, pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!.Length);
                new byte[] { 0xff, 0xff, 0xff, 0x04 }.CopyTo(image, store + 1); // invalid field token in the optional display-version initializer
            }
        }
        var build = Read(image);
        Assert.NotNull(build.WorldSupport);
        if (network) { Assert.Null(build.NetworkVersion); Assert.Contains("unreadable network", build.Unknown); }
        else { Assert.Equal(40u, build.NetworkVersion); Assert.Null(build.GameVersion); }
    }

    [Theory]
    [InlineData("interval", true)]
    [InlineData("primitive", false)]
    [InlineData("signature", false)]
    [InlineData("missing", false)]
    [InlineData("unsigned", false)]
    [InlineData("contradictory", false)]
    [InlineData("extra", false)]
    public void WorldSupportRequiresTheExactEnumDeclarationsAndWholeSignedPredicate(string shape, bool known)
    {
        var image = FakeInstalls.GameAssembly(minimum: 12, maximum: 60, chunked: 50, shape: shape);
        var build = Read(image);
        Assert.Equal(40u, build.NetworkVersion);
        if (known)
        {
            Assert.NotNull(build.WorldSupport);
            Assert.Equal((12, 60, 50), (build.WorldSupport.Minimum, build.WorldSupport.Maximum, build.WorldSupport.ChunkedSave));
            Assert.Equal(FileHash.Sha256(image), build.WorldSupport.AssemblySha256);
            Assert.Null(build.WorldUnknown);
        }
        else { Assert.Null(build.WorldSupport); Assert.Contains("world-format", build.WorldUnknown); }
    }

    [Fact]
    public void WorldSupportIsIndependentOfNetworkMetadataAndRejectsContradictoryThresholds()
    {
        Assert.NotNull(Read(FakeInstalls.GameAssembly(networkConstant: null)).WorldSupport);
        Assert.Null(Read(FakeInstalls.GameAssembly(minimum: 41, maximum: 42, chunked: 40)).WorldSupport);
        Assert.Null(Read(FakeInstalls.GameAssembly(minimum: 9, maximum: 39, chunked: 40)).WorldSupport);
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
    public async Task EarlyInspectionRequestsOnlyTheAssemblyAndLaterInspectionRequestsTheWholeInstall()
    {
        string root = Directory.CreateTempSubdirectory("world923-assembly-request-").FullName;
        try
        {
            FakeInstalls.Server(root);
            var host = FakeServerHost.ThisMachine();
            var early = await GameVersionAgreement.ReadInstallationAsync(host, root, GameHostChecks.Generous, CancellationToken.None);
            Assert.NotNull(early.WorldSupport);
            var request = Assert.Single(host.Runs.Where(run => run.Variables.ContainsKey("dirs")));
            Assert.Equal(InstallPins.ManagedFolders.Select(folder => folder + "/" + InstallPins.GameAssemblyName), request.Variables["dirs"].Split('\n'));
            Assert.Equal("1", request.Variables["nocase"]);
            var full = await HostInstall.ListProfileSourceAsync(host, root, GameHostChecks.Generous);
            Assert.Equal("", host.Runs.Last(run => run.Variables.ContainsKey("dirs")).Variables["dirs"]);
            Assert.Contains(full.Files.Keys, name => name.StartsWith("BepInEx/", StringComparison.Ordinal));
            WorldIdentity.RequireSameTarget(early, await GameVersionAgreement.ReadAsync(host, full, GameHostChecks.Generous, CancellationToken.None));
        }
        finally { DirectoryLinks.DeleteTree(root); }
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
        Assert.Equal(40u, (await GameVersionAgreement.ReadInPlaceAsync(_root, CancellationToken.None)).NetworkVersion);
        File.WriteAllBytes(Path.Combine(_root, "valheim_Data", "Managed", InstallPins.GameAssemblyName), FakeInstalls.GameAssembly("1.0.20", 41));
        var builds = new GameVersionAgreement.Ledger();
        builds.Admit(new("server", "pc", @"C:\game", new GameBuild(40, "1.0.17", null)));
        Assert.Contains("is Valheim 1.0.20 (network version 41), but server's install", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builds.AdmitInPlaceAsync("client", _root, CancellationToken.None))).Message);
        var missing = await GameVersionAgreement.ReadInPlaceAsync(Path.Combine(_root, "missing"), CancellationToken.None);
        Assert.Null(missing.NetworkVersion);
        Assert.Contains("missing", missing.Unknown);
    }

    public static TheoryData<string, string> InstallLayouts => new()
    {
        { "valheim_Data/Managed", "client" },
        { "valheim_server_Data/Managed", "server" },
        { "Valheim.app/Contents/Resources/Data/Managed", "mac-client" },
        { "valheim_server/Data/Managed", "mac-server" },
    };

    [Theory, MemberData(nameof(InstallLayouts))]
    public async Task EarlyAssemblyInspectionSupportsEveryLayoutAndTheLaterListingRemainsComplete(string managed, string name)
    {
        string root = Path.Combine(_root, name), assembly = Path.Combine(root, managed, InstallPins.GameAssemblyName);
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        File.WriteAllBytes(assembly, FakeInstalls.GameAssembly());
        string asset = Path.Combine(root, "bulk-assets", "world.bundle");
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!); File.WriteAllText(asset, "bulk asset excluded from early admission");
        var host = new LocalGameHost("local", HostShell.ThisMachine);
        var early = await GameVersionAgreement.ReadInstallationAsync(host, root, GameHostChecks.Generous, CancellationToken.None);
        Assert.NotNull(early.WorldSupport);
        var full = await HostInstall.ListProfileSourceAsync(host, root, GameHostChecks.Generous);
        Assert.Contains("bulk-assets/world.bundle", full.Files.Keys);
        var later = await GameVersionAgreement.ReadAsync(host, full, GameHostChecks.Generous, CancellationToken.None);
        WorldIdentity.RequireSameTarget(early, later);
        // A different assembly with an unchanged network version must still invalidate the early target.
        File.WriteAllBytes(assembly, FakeInstalls.GameAssembly(maximum: 43));
        var changed = await GameVersionAgreement.ReadInstallationAsync(host, root, GameHostChecks.Generous, CancellationToken.None);
        Assert.Equal(early.NetworkVersion, changed.NetworkVersion);
        Assert.Throws<InvalidOperationException>(() => WorldIdentity.RequireSameTarget(early, changed));
    }

    [Fact]
    public async Task EarlyAssemblyInspectionRefusesMissingAndAmbiguousInstalls()
    {
        var host = new LocalGameHost("local", HostShell.ThisMachine);
        var missing = await GameVersionAgreement.ReadInstallationAsync(host, _root, GameHostChecks.Generous, CancellationToken.None);
        Assert.Null(missing.WorldSupport); Assert.Contains("No game assembly", missing.Unknown);
        foreach (string managed in new[] { "valheim_Data/Managed", "valheim_server_Data/Managed" })
        {
            Directory.CreateDirectory(Path.Combine(_root, managed));
            File.WriteAllBytes(Path.Combine(_root, managed, InstallPins.GameAssemblyName), FakeInstalls.GameAssembly());
        }
        var ambiguous = await GameVersionAgreement.ReadInstallationAsync(host, _root, GameHostChecks.Generous, CancellationToken.None);
        Assert.Null(ambiguous.WorldSupport); Assert.Contains("More than one game assembly", ambiguous.Unknown);
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
        Assert.Equal(40u, (await GameVersionAgreement.ReadAsync(host, listing, GameHostChecks.Generous, CancellationToken.None)).NetworkVersion);
        File.WriteAllBytes(assembly, FakeInstalls.GameAssembly("1.0.20", 41));
        Assert.EndsWith("changed after the install was listed", (await GameVersionAgreement.ReadAsync(host, listing, GameHostChecks.Generous, CancellationToken.None)).Unknown);
        File.Delete(assembly);
        Assert.EndsWith("is missing", (await GameVersionAgreement.ReadAsync(host, listing, GameHostChecks.Generous, CancellationToken.None)).Unknown);
    }
}
