using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// HostInstall's listing patterns (#669): one check before anything is sent, and a case-insensitive listing that uses each glob
// as written. The end-to-end listings run this machine's own listing script (bash, or PowerShell on Windows).
public sealed class HostInstallListingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("host-listing-").FullName;
    private readonly LocalGameHost _host = new("this machine", OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash);

    public HostInstallListingTests()
    {
        foreach (string managed in new[] { "Valheim_Data/Managed", "valheim_server_Data/Managed", "Other/Managed", "Deep/x_Data/Managed" })
        {
            Directory.CreateDirectory(Path.Combine(_root, managed));
            File.WriteAllText(Path.Combine(_root, managed, "assembly_valheim.dll"), managed);
        }
        Directory.CreateDirectory(Path.Combine(_root, "save"));
        foreach (string name in new[] { "Name.fch", "Name_Backup_Auto-2026", "Other.fch", "Another_backup_auto-1", "!Bang.fch", "^Hat.fch", "[x].fch" })
            File.WriteAllText(Path.Combine(_root, "save", name), name);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ListingPatternsUseSlashSeparators() =>
        Assert.Equal(new[] { "save/Name.fch", "*_Data/Managed", "a b/c" }, HostInstall.ListingPatterns([@"save\Name.fch", "*_Data/Managed", @"a b\c"]));

    // A leading backslash is a leading separator too: refused, never turned into an absolute path.
    [Theory]
    [InlineData("/save"), InlineData(@"\save"), InlineData("C:/save"), InlineData("c:save"), InlineData("save/../x"), InlineData(@"save\..\x"),
     InlineData(".."), InlineData("."), InlineData("./save"), InlineData("save//x"), InlineData(@"save\\x"), InlineData("save/"), InlineData(@"save\"),
     InlineData(""), InlineData(" "), InlineData("save\nx")]
    public async Task APatternOutsideTheRootIsRefusedBeforeTheHostIsAsked(string pattern)
    {
        Assert.Throws<ArgumentException>(() => HostInstall.ListingPatterns([pattern]));
        await Assert.ThrowsAsync<ArgumentException>(() => HostInstall.ListAsync(_host, _root, TimeSpan.FromSeconds(20), ["save", pattern]));
        await Assert.ThrowsAsync<ArgumentException>(() => HostInstall.ListCaseInsensitiveAsync(_host, _root, TimeSpan.FromSeconds(20), ["save", pattern]));
    }

    [Theory]
    [InlineData("vt01_backup_auto-", "vt01_backup_auto-")]
    [InlineData("Hero[2]", "Hero?2]")]
    [InlineData(@"a*b?c\d", "a?b?c?d")]
    [InlineData("!^]", "!^]")]
    public void ALiteralNameBecomesAPatternBothHostsMatchIt(string name, string pattern) => Assert.Equal(pattern, HostInstall.LiteralPattern(name));

    [Theory]
    [InlineData(""), InlineData("."), InlineData(".."), InlineData("save/Name.fch")]
    public void ALiteralPatternNamesOneEntry(string name) => Assert.Throws<ArgumentException>(() => HostInstall.LiteralPattern(name));

    [Fact]
    public void ACharactersPatternsAreItsLiteralNamesAndItsBackupPrefixWithAWildcard()
    {
        Assert.Equal(new[] { "vt01.fch", "vt01.fch.old", "vt01.fch.new", "vt01_backup_auto-*" },
            DisposableCharacterStore.OwnedFilePatterns("vt01", HostInstall.LiteralPattern));
        Assert.Equal(new[] { "v?1].fch", "v?1].fch.old", "v?1].fch.new", "v?1]_backup_auto-*" },
            DisposableCharacterStore.OwnedFilePatterns("v[1]", HostInstall.LiteralPattern));
    }

    // On bash the listing names each file as the disk spells it, so a literal segment that was not folded (left in the
    // caller's spelling, or not found on a case-sensitive filesystem) fails even where the filesystem ignores case. Windows'
    // listing keeps a literal segment as the caller spelled it; there only which files were listed is compared.
    private async Task AssertListsAsync(string[] expected, params string[] patterns)
    {
        var listing = await HostInstall.ListCaseInsensitiveAsync(_host, _root, TimeSpan.FromSeconds(20), patterns);
        var names = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), listing.Files.Keys.Order(StringComparer.OrdinalIgnoreCase), names);
    }

    [Fact]
    public Task AWildcardLeadingPatternMatchesIgnoringCaseWithinOneSegment() =>
        AssertListsAsync(["Valheim_Data/Managed/assembly_valheim.dll", "valheim_server_Data/Managed/assembly_valheim.dll"], "*_DATA/managed");

    [Fact]
    public Task ALiteralPatternMatchesIgnoringCaseInEverySegment() => AssertListsAsync(["save/Name.fch"], "SAVE/name.FCH");

    [Fact]
    public Task ABackslashSeparatesSegments() => AssertListsAsync(["save/Name.fch"], @"SAVE\name.FCH");

    [Theory]
    [InlineData("!BANG.fch", "save/!Bang.fch")]
    [InlineData("^HAT.fch", "save/^Hat.fch")]
    [InlineData("?AME.FCH", "save/Name.fch")]
    [InlineData("name_backup_auto-*", "save/Name_Backup_Auto-2026")]
    public Task AFileNamePatternMatchesIgnoringCase(string name, string expected) => AssertListsAsync([expected], "SAVE/" + name);

    [Fact]
    public Task ALiteralNameWithABracketMatchesThroughLiteralPattern() =>
        AssertListsAsync(["save/[x].fch"], "save/" + HostInstall.LiteralPattern("[X].FCH"));

    [Fact]
    public Task SeveralPatternsAreAllHonoured() => AssertListsAsync(["save/Name.fch", "save/Other.fch"], "save/name.fch", "SAVE/OTHER.*");

    [Fact]
    public Task APatternThatMatchesNothingListsNothing() => AssertListsAsync([], "SAVE/missing.fch", "nowhere/name.fch");

    [UnixFact]
    public async Task ANamedEntryThatIsALinkIsRefused()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "save", "Link.fch"), Path.Combine(_root, "save", "Name.fch"));

        var error = await Assert.ThrowsAsync<IOException>(() => HostInstall.ListCaseInsensitiveAsync(_host, _root, TimeSpan.FromSeconds(20), ["SAVE/link.FCH"]));

        Assert.Contains("link.fch", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
