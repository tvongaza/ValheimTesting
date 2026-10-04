using Valheim.Testing.Game;
using Xunit;

public sealed class DisposableCharacterStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-character-store-").FullName;
    private readonly string _local, _evidence, _steam;

    public DisposableCharacterStoreTests()
    {
        _local = Path.Combine(_root, "characters_local");
        _evidence = Path.Combine(_root, "evidence");
        _steam = Path.Combine(_root, "userdata");
        foreach (string directory in new[] { _local, _evidence, _steam }) Directory.CreateDirectory(directory);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Local(string name, long playerId, long secondUid = 200)
    {
        string file = Path.Combine(_local, name + ".fch");
        File.WriteAllBytes(file, CharacterSaveReaderTests.Profile(secondUid: secondUid, playerId: playerId).File);
        return file;
    }

    private string StoreDirectory => Path.Combine(_root, "store");

    [Fact]
    public void RegisteringCopiesTheSaveAndRecordsItsPlayer()
    {
        string source = Local("seed", 11);
        var store = DisposableCharacterStore.Create(StoreDirectory);
        var character = store.Register("tester", source);
        Assert.Equal(new[] { "tester" }, store.Names);
        Assert.Equal(11, character.PlayerId);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(StoreDirectory, "tester.fch")));
        var reopened = DisposableCharacterStore.Open(StoreDirectory).Get("TESTER");
        Assert.Equal(character.Sha256, reopened.Sha256);
        Assert.Equal("tester", reopened.Name);
    }

    [Fact]
    public void HostedSmokeStagesOnlyARegisteredCharacterAndRestoresTheFolder()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        store.Register("tester", Local("seed", 11));
        byte[] original = File.ReadAllBytes(Path.Combine(_local, "seed.fch"));
        try
        {
            using var stage = RegisteredCharacterStage.InstallRegistered(StoreDirectory, "tester", _local, _steam, "smoke-only");
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(_local, "smoke-only.fch")));
            throw new InvalidOperationException("simulate a failing native scenario");
        }
        catch (InvalidOperationException error) when (error.Message == "simulate a failing native scenario") { }
        Assert.False(File.Exists(Path.Combine(_local, "smoke-only.fch")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_local, "seed.fch")));
        Assert.Equal(new[] { "seed.fch" }, Directory.EnumerateFiles(_local).Select(Path.GetFileName));
    }

    [Fact]
    public void HostedSmokeRefusesANameAlreadyPresentInSteamCloud()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        store.Register("tester", Local("seed", 11));
        string cloud = Path.Combine(_steam, "12345", "892970", "remote", "characters");
        Directory.CreateDirectory(cloud);
        File.WriteAllText(Path.Combine(cloud, "smoke-only.fch"), "someone else's character");
        Assert.Throws<IOException>(() => RegisteredCharacterStage.InstallRegistered(StoreDirectory, "tester", _local, _steam, "smoke-only"));
        Assert.False(File.Exists(Path.Combine(_local, "smoke-only.fch")));
    }

    [Fact]
    public void AnUnregisteredPersonalCharacterCannotBeTakenOrStaged()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        store.Register("tester", Local("seed", 11));
        string personal = Local("personal", 99);
        Assert.Throws<KeyNotFoundException>(() => store.Get("personal"));

        // Staging takes only a registered name: there is no way to stage the personal file.
        Assert.Throws<KeyNotFoundException>(() => RegisteredCharacterStage.InstallRegistered(StoreDirectory, "personal", _local, _steam, "fresh"));
        Assert.False(File.Exists(Path.Combine(_local, "fresh.fch")));
    }

    [Fact]
    public void AStoredCopySwappedForAnotherCharacterIsRefused()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        store.Register("tester", Local("seed", 11));
        File.Copy(Local("personal", 99), Path.Combine(StoreDirectory, "tester.fch"), overwrite: true);
        Assert.Contains("different character", Assert.Throws<InvalidDataException>(() => store.Get("tester")).Message);
    }

    [Fact]
    public void AHandleGoesStaleWhenTheStoreChangesAfterItWasTaken()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        string source = Local("seed", 11);
        var old = store.Register("tester", source);

        // The game re-saved the same character after a visit: Refresh accepts it, and the earlier handle no longer does.
        File.WriteAllBytes(source, CharacterSaveReaderTests.Profile(mapBytes: 16, playerId: 11).File);
        var fresh = store.Refresh("tester", source);
        Assert.NotEqual(old.Sha256, fresh.Sha256);
        Assert.Contains("changed since", Assert.Throws<InvalidDataException>(() => store.Read(old)).Message);
        Assert.NotEmpty(store.Read(fresh));

        // A registration removed from the manifest after the handle was taken.
        string manifest = Path.Combine(StoreDirectory, DisposableCharacterStore.ManifestFile);
        File.WriteAllText(manifest, "{ \"format\": 1, \"characters\": [] }");
        Assert.Throws<KeyNotFoundException>(() => store.Read(fresh));
    }

    [Fact]
    public void RefreshNeverSwapsInADifferentCharacter()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        var character = store.Register("tester", Local("seed", 11));
        byte[] stored = File.ReadAllBytes(Path.Combine(StoreDirectory, "tester.fch"));
        Assert.Throws<InvalidDataException>(() => store.Refresh("tester", Local("personal", 99)));
        Assert.Equal(stored, File.ReadAllBytes(Path.Combine(StoreDirectory, "tester.fch")));
        Assert.Equal(character.Sha256, store.Get("tester").Sha256);
        Assert.Throws<KeyNotFoundException>(() => store.Refresh("other", Local("other", 12)));
    }

    [Fact]
    public void AForgedOrDamagedManifestIsRefused()
    {
        Directory.CreateDirectory(StoreDirectory);
        Assert.Throws<FileNotFoundException>(() => DisposableCharacterStore.Open(StoreDirectory));
        string manifest = Path.Combine(StoreDirectory, DisposableCharacterStore.ManifestFile);
        File.WriteAllText(manifest, "{ \"format\": 1, \"characters\": [ { \"name\": \"a\", \"playerId\": 5 }, { \"name\": \"b\", \"playerId\": 5 } ] }");
        Assert.Throws<InvalidDataException>(() => DisposableCharacterStore.Open(StoreDirectory));
        File.WriteAllText(manifest, "{ \"format\": 2, \"characters\": [] }");
        Assert.Throws<NotSupportedException>(() => DisposableCharacterStore.Open(StoreDirectory));
        File.WriteAllText(manifest, "{ \"format\": 1, \"characters\": [ { \"name\": \"../escape\", \"playerId\": 5 } ] }");
        Assert.Throws<ArgumentException>(() => DisposableCharacterStore.Open(StoreDirectory));
        File.WriteAllText(manifest, "{ \"format\": 1, \"characters\": [ { \"name\": \"listed\", \"playerId\": 5 } ] }");
        Assert.Throws<FileNotFoundException>(() => DisposableCharacterStore.Open(StoreDirectory).Get("listed"));
    }

    [Fact]
    public void AFailedRegistrationLeavesTheStoreUnchanged()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        store.Register("tester", Local("seed", 11));
        string manifest = File.ReadAllText(Path.Combine(StoreDirectory, DisposableCharacterStore.ManifestFile));

        string damaged = Local("damaged", 12);
        byte[] bytes = File.ReadAllBytes(damaged);
        bytes[20] ^= 0x01;
        File.WriteAllBytes(damaged, bytes);
        Assert.Throws<InvalidDataException>(() => store.Register("damaged", damaged));
        Assert.Throws<IOException>(() => store.Register("tester", Local("other", 13)));
        Assert.Throws<IOException>(() => store.Register("again", Local("seed", 11)));
        Assert.Throws<ArgumentException>(() => store.Register("bad name", Local("other", 13)));

        Assert.Equal(manifest, File.ReadAllText(Path.Combine(StoreDirectory, DisposableCharacterStore.ManifestFile)));
        Assert.Equal(new[] { DisposableCharacterStore.ManifestFile, "tester.fch" },
            Directory.EnumerateFiles(StoreDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheStoreStaysOutsideTheGamesCharacterFolders()
    {
        Assert.Throws<ArgumentException>(() => DisposableCharacterStore.Create(Path.Combine(_local, "store")));
        Assert.Throws<ArgumentException>(() => DisposableCharacterStore.Create(Path.Combine(_root, "characters", "store")));
        Assert.Throws<ArgumentException>(() => DisposableCharacterStore.Create("relative-store"));
        File.WriteAllText(Path.Combine(_evidence, "something"), "");
        Assert.Throws<IOException>(() => DisposableCharacterStore.Create(_evidence));
    }

    private static string Sha256(string file) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
}
