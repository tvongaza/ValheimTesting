using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Xunit;

// #295: FileHash is the one owner of hashing in Valheim.Testing.Game; a second SHA256 or MD5 helper is refused here.
public sealed class FileHashTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("file-hash-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A hash computation in code: SHA256.HashData, MD5.Create(), IncrementalHash and the like.
    private static readonly Regex Computes = new(@"\b(SHA1|SHA256|SHA384|SHA512|MD5|IncrementalHash|HashAlgorithm)\.(HashData|Create|ComputeHash|CreateHash)", RegexOptions.CultureInvariant);
    // The game's own save integrity check (CharacterSaveReader verifies the SHA512 the game wrote) is not the toolkit's hashing.
    private static bool GameSaveCheck(string path, string line) => Path.GetFileName(path) == "CharacterSaveReader.cs" && line.Contains("SHA512.HashData(payload)");

    // Game and the sessions package built on it (#289) share one hashing owner.
    [Fact] public void OnlyFileHashComputesHashesInTheGamePackages()
    {
        string source = Path.Combine(FixtureProjects.RepositoryRoot(), "src");
        var others = new[] { "Valheim.Testing.Game", "Valheim.Testing.GameSessions" }
            .SelectMany(package => Directory.EnumerateFiles(Path.Combine(source, package), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && Path.GetFileName(path) != "FileHash.cs")
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(item => Computes.IsMatch(item.line) && !item.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && !GameSaveCheck(item.path, item.line))
            .Select(item => $"{Path.GetRelativePath(source, item.path)}:{item.index + 1}").ToList();
        Assert.Empty(others);
        // Negative control: the pattern finds a helper of the shape this issue removed.
        Assert.Matches(Computes, "return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();");
        Assert.Matches(Computes, "using (var hash = MD5.Create())");
    }

    [Fact] public async Task HashesAreLowerCaseHexOfTheContent()
    {
        string file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", FileHash.Sha256(file));
        Assert.Equal(FileHash.Sha256(file), FileHash.Sha256("abc"u8));
        Assert.Equal(FileHash.Sha256(file), await FileHash.Sha256Async(file));
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", FileHash.Md5(file));
    }

    [Fact] public void ALinkInAPinnedFolderIsRefusedAsAHostListingRefusesIt()
    {
        if (OperatingSystem.IsWindows()) return; // Creating a link needs a privilege the Windows runners may not have.
        string install = Path.Combine(_root, "install");
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(_root, "real.dll"), "real");
        _ = InstallPins.Of(install); // The control: the same install without a link is pinned.
        File.CreateSymbolicLink(Path.Combine(install, "BepInEx", "core", "Linked.dll"), Path.Combine(_root, "real.dll"));
        Assert.Contains("Links are unsupported", Assert.Throws<IOException>(() => InstallPins.Of(install)).Message);
        File.Delete(Path.Combine(install, "BepInEx", "core", "Linked.dll"));
        File.CreateSymbolicLink(Path.Combine(install, "winhttp.dll"), Path.Combine(_root, "real.dll"));
        Assert.Contains("Links are unsupported", Assert.Throws<IOException>(() => InstallPins.Of(install)).Message);
        File.Delete(Path.Combine(install, "winhttp.dll"));
        // A linked game assembly and a linked loader folder too: every pin is of the files themselves.
        string managed = Path.Combine(install, "valheim_Data", "Managed");
        File.Move(Path.Combine(managed, "assembly_utils.dll"), Path.Combine(_root, "assembly_utils.dll"));
        File.CreateSymbolicLink(Path.Combine(managed, "assembly_utils.dll"), Path.Combine(_root, "assembly_utils.dll"));
        Assert.Contains("Links are unsupported", Assert.Throws<IOException>(() => InstallPins.Of(install)).Message);
        File.Delete(Path.Combine(managed, "assembly_utils.dll"));
        Directory.CreateDirectory(Path.Combine(_root, "libs"));
        Directory.CreateSymbolicLink(Path.Combine(install, "doorstop_libs"), Path.Combine(_root, "libs"));
        Assert.Contains("Links are unsupported", Assert.Throws<IOException>(() => InstallPins.Of(install)).Message);
    }
}
