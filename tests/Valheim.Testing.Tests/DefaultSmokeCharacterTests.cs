using System.Security.Cryptography;
using Valheim.Testing.Game;
using Xunit;

public sealed class DefaultSmokeCharacterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-default-character-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void GameCreatedSeedIsRegisteredAndRestoredOnlyInItsOwnedStore()
    {
        string root = Path.Combine(_root, "fixture");
        var store = DefaultSmokeCharacter.Prepare(root);
        var character = store.Get(DefaultSmokeCharacter.Name);
        byte[] original = File.ReadAllBytes(Path.Combine(store.Root, DefaultSmokeCharacter.Name + ".fch"));
        Assert.Equal("VTSeedClean", CharacterSaveReader.ReadIdentity(original).Name);
        Assert.True(CharacterSaveReader.IsFreshSmokeSeed(original));
        Assert.Equal("16712a2a3166e9b93c3aa006ea8d3d85a2d56372f7363728808f30d18bc8d626",
            Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant());
        Assert.Equal(character.Sha256, store.Get(DefaultSmokeCharacter.Name).Sha256);
        Assert.DoesNotContain("MWL_KnownPorts", System.Text.Encoding.UTF8.GetString(original));
        Assert.Throws<IOException>(() => DefaultSmokeCharacter.Prepare(root));
        Assert.Equal(character.Sha256, DefaultSmokeCharacter.Ensure(root).Get(DefaultSmokeCharacter.Name).Sha256);

        string copy = Path.Combine(store.Root, DefaultSmokeCharacter.Name + ".fch");
        File.WriteAllText(copy, "stale copy");
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(DefaultSmokeCharacter.Ensure(root).Root, DefaultSmokeCharacter.Name + ".fch")));
        File.Delete(Path.Combine(store.Root, DisposableCharacterStore.ManifestFile));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(DefaultSmokeCharacter.Ensure(root).Root, DefaultSmokeCharacter.Name + ".fch")));
        Assert.Equal(character.PlayerId, DefaultSmokeCharacter.Ensure(root).Get(DefaultSmokeCharacter.Name).PlayerId);
    }

    [Fact]
    public void UnknownFilesAndPersonalDirectoriesAreNeverRepaired()
    {
        var first = DefaultSmokeCharacter.Ensure(Path.Combine(_root, "new-owned-store"));
        Assert.Equal([DefaultSmokeCharacter.Name], first.Names);
        string personal = Path.Combine(_root, "personal");
        Directory.CreateDirectory(personal);
        string valued = Path.Combine(personal, "valued.fch");
        File.WriteAllText(valued, "personal character");
        Assert.Throws<IOException>(() => DefaultSmokeCharacter.Ensure(personal));
        Assert.Equal("personal character", File.ReadAllText(valued));

        string root = Path.Combine(_root, "fixture");
        var store = DefaultSmokeCharacter.Prepare(root);
        string stranger = Path.Combine(store.Root, "other.fch");
        File.WriteAllText(stranger, "another character");
        File.WriteAllText(Path.Combine(store.Root, DefaultSmokeCharacter.Name + ".fch"), "stale copy");
        Assert.Throws<IOException>(() => DefaultSmokeCharacter.Ensure(root));
        Assert.Equal("another character", File.ReadAllText(stranger));
        Assert.Equal("stale copy", File.ReadAllText(Path.Combine(store.Root, DefaultSmokeCharacter.Name + ".fch")));
    }

    [Fact]
    public void StagedSeedLeavesTheExistingCharactersUnchangedAfterFailure()
    {
        var store = DefaultSmokeCharacter.Prepare(Path.Combine(_root, "fixture"));
        string local = Path.Combine(_root, "characters_local"), steam = Path.Combine(_root, "userdata");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(steam);
        string personal = Path.Combine(local, "personal.fch");
        File.WriteAllText(personal, "personal character");
        try
        {
            using var stage = RegisteredCharacterStage.InstallRegistered(store.Root, DefaultSmokeCharacter.Name, local, steam, "vtseedclean");
            Assert.True(File.Exists(Path.Combine(local, "vtseedclean.fch")));
            throw new InvalidOperationException("simulated native failure");
        }
        catch (InvalidOperationException error) when (error.Message == "simulated native failure") { }
        Assert.Equal("personal character", File.ReadAllText(personal));
        Assert.Equal(["personal.fch"], Directory.EnumerateFiles(local).Select(Path.GetFileName));
    }
}
