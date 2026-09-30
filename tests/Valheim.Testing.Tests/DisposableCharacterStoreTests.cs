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
        File.WriteAllBytes(file, CharacterSavePositionTests.Profile(secondUid: secondUid, playerId: playerId).File);
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
    public void AnUnregisteredPersonalCharacterCannotBeTakenOrStaged()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        store.Register("tester", Local("seed", 11));
        string personal = Local("personal", 99);
        Assert.Throws<KeyNotFoundException>(() => store.Get("personal"));

        // Even a positioned copy made without the public API is refused at staging, before anything is written.
        string prepared = Path.Combine(_evidence, "fresh.fch");
        File.WriteAllBytes(prepared, CharacterSavePosition.AtWorld(File.ReadAllBytes(personal), 200, 10, 40, -20));
        var plan = new CharacterStartPlan { PreparedFile = prepared, Sha256 = Sha256(prepared),
            CharactersLocalDirectory = _local, SteamUserDataDirectory = _steam, CharacterStore = StoreDirectory };
        var error = Assert.Throws<InvalidDataException>(() => CharacterStartStage.Install(plan, "fresh", 200, new HeightExpectation(10, -20, 40)));
        Assert.Contains("not a registered disposable character", error.Message);
        Assert.False(File.Exists(Path.Combine(_local, "fresh.fch")));

        // A plan must name the store; an ordinary directory is not one.
        plan.CharacterStore = "";
        Assert.Throws<ArgumentException>(() => CharacterStartStage.Install(plan, "fresh", 200, new HeightExpectation(10, -20, 40)));
        plan.CharacterStore = _evidence;
        Assert.Throws<FileNotFoundException>(() => CharacterStartStage.Install(plan, "fresh", 200, new HeightExpectation(10, -20, 40)));
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
        File.WriteAllBytes(source, CharacterSavePositionTests.Profile(mapBytes: 16, playerId: 11).File);
        var fresh = store.Refresh("tester", source);
        Assert.NotEqual(old.Sha256, fresh.Sha256);
        string output = Path.Combine(_evidence, "copy.fch");
        Assert.Contains("changed since", Assert.Throws<InvalidDataException>(() => CharacterStartCopy.Prepare(old, output, 200, 1, 2, 3)).Message);
        Assert.False(File.Exists(output));
        CharacterStartCopy.Prepare(fresh, output, 200, 1, 2, 3);
        Assert.True(File.Exists(output));

        // A registration removed from the manifest after the handle was taken.
        string manifest = Path.Combine(StoreDirectory, DisposableCharacterStore.ManifestFile);
        File.WriteAllText(manifest, "{ \"format\": 1, \"characters\": [] }");
        Assert.Throws<KeyNotFoundException>(() => CharacterStartCopy.Prepare(fresh, Path.Combine(_evidence, "again.fch"), 200, 1, 2, 3));
        Assert.False(File.Exists(Path.Combine(_evidence, "again.fch")));
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
    public void AFailedPreparationWritesNothing()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        var character = store.Register("tester", Local("seed", 11));
        string output = Path.Combine(_evidence, "copy.fch");
        Assert.Throws<KeyNotFoundException>(() => CharacterStartCopy.Prepare(character, output, 300, 1, 2, 3));
        Assert.False(File.Exists(output));
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
