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
    public void CharacterJournalRecordsIntentOnlyAfterCollisionCheckAndRetiresAfterCleanup()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        store.Register("tester", Local("seed", 11));
        var events = new List<CharacterStageEvent>();
        void Record(CharacterStageEvent point, string local, string steam, string name)
        {
            Assert.Equal(_local, local);
            Assert.Equal("smoke-only", name);
            events.Add(point);
        }
        File.WriteAllText(Path.Combine(_local, "smoke-only.fch"), "personal");
        Assert.Throws<IOException>(() => RegisteredCharacterStage.InstallRegistered(StoreDirectory, "tester", _local, _steam, "smoke-only", Record));
        Assert.Empty(events);
        Assert.Equal("personal", File.ReadAllText(Path.Combine(_local, "smoke-only.fch")));
        File.Delete(Path.Combine(_local, "smoke-only.fch"));

        using (RegisteredCharacterStage.InstallRegistered(StoreDirectory, "tester", _local, _steam, "smoke-only", Record))
            Assert.Equal([CharacterStageEvent.Intended, CharacterStageEvent.Done], events);
        Assert.Equal([CharacterStageEvent.Intended, CharacterStageEvent.Done, CharacterStageEvent.Retired], events);
        Assert.False(File.Exists(Path.Combine(_local, "smoke-only.fch")));
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

    // The one character-file rule (DisposableCharacterStore.IsCharacterFile) in each folder CharacterFolders names. The
    // trailing separator case is the negative control for #301: before it the sibling cloud folder was taken as
    // characters_local/characters, so a cloud save of the same name went unnoticed.
    [Theory]
    [InlineData("local", "Smoke-Only.fch", false)] [InlineData("local", "smoke-only.fch.old", false)] [InlineData("local", "smoke-only_backup_auto-20261004120000.fch", false)]
    [InlineData("cloud", "smoke-only.fch", false)] [InlineData("cloud", "SMOKE-ONLY.FCH.OLD", true)] [InlineData("cloud", "smoke-only_backup_auto-1.fch", true)]
    [InlineData("account", "smoke-only.fch", true)] [InlineData("account", "smoke-only.fch.old", false)] [InlineData("account", "smoke-only_backup_auto-1.fch", false)]
    [InlineData("local", "smoke-only.fch.new", false)] [InlineData("cloud", "smoke-only.FCH.NEW", true)]
    public void TheLocalStageRefusesAnyFileTheCharacterOwnsInEveryFolder(string folder, string existing, bool trailingSeparator)
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        store.Register("tester", Local("seed", 11));
        string directory = folder switch
        {
            "local" => _local, "cloud" => Path.Combine(_root, "characters"),
            _ => Path.Combine(_steam, "12345", "892970", "remote", "characters"),
        };
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, existing), "someone else's");
        Assert.True(DisposableCharacterStore.IsCharacterFile(existing, "smoke-only"));
        string local = trailingSeparator ? _local + Path.DirectorySeparatorChar : _local;
        var before = Directory.EnumerateFiles(_local).Order(StringComparer.Ordinal).ToList();
        Assert.Throws<IOException>(() => RegisteredCharacterStage.InstallRegistered(StoreDirectory, "tester", local, _steam, "smoke-only"));
        Assert.Equal(before, Directory.EnumerateFiles(_local).Order(StringComparer.Ordinal));
        Assert.Equal("someone else's", File.ReadAllText(Path.Combine(directory, existing)));
    }

    [Fact]
    public void TheLocalStageRetiresExactlyTheFilesTheGameKeptForItsCharacter()
    {
        var store = DisposableCharacterStore.Create(StoreDirectory);
        store.Register("tester", Local("seed", 11));
        string[] others = ["seed.fch", "smoke-only2.fch", "asmoke-only.fch", "smoke-only.fch.bak", "smoke-only_backup.fch"];
        foreach (string other in others.Skip(1)) File.WriteAllText(Path.Combine(_local, other), "not the run's");
        using (RegisteredCharacterStage.InstallRegistered(StoreDirectory, "tester", _local, _steam, "smoke-only"))
        {
            // What the game writes for the character while it plays: its previous save and an automatic backup.
            File.WriteAllText(Path.Combine(_local, "smoke-only.fch.old"), "previous");
            File.WriteAllText(Path.Combine(_local, "smoke-only_backup_auto-20261004120000.fch"), "backup");
            File.WriteAllText(Path.Combine(_local, "smoke-only.fch.new"), "a save the stop interrupted");
        }
        Assert.Equal(others.Order(StringComparer.Ordinal), Directory.EnumerateFiles(_local).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(others, name => Assert.False(DisposableCharacterStore.IsCharacterFile(name, "smoke-only")));
    }

    [Theory]
    [InlineData(@"C:\Users\tester\AppData\LocalLow\IronGate\Valheim\characters_local", @"C:\Users\tester\AppData\LocalLow\IronGate\Valheim\characters")]
    [InlineData(@"C:\Valheim\characters_local\", @"C:\Valheim\characters")]
    [InlineData("/home/steam/.config/unity3d/IronGate/Valheim/characters_local/", "/home/steam/.config/unity3d/IronGate/Valheim/characters")]
    [InlineData("/characters_local", "/characters")]
    public void HostScriptsGetTheRuleAndTheSiblingCloudFolderInTheHostsStyle(string charactersLocal, string cloud)
    {
        var variables = DisposableCharacterStore.HostScriptVariables("fresh", charactersLocal);
        Assert.Equal(cloud, variables["cloud"]);
        Assert.Equal("892970/remote/characters", variables["remote"]);
        Assert.Equal("fresh.fch", variables["save"]);
        Assert.Equal(new[] { "fresh.fch", "fresh.fch.old", "fresh.fch.new" }, variables["names"].Split('\n'));
        Assert.Equal(new[] { "fresh_backup_auto-" }, variables["prefixes"].Split('\n'));
    }

    [Theory][InlineData("characters_local")][InlineData("")]
    public void HostScriptsRefuseACharactersFolderThatIsNoFullPath(string charactersLocal) =>
        Assert.Throws<ArgumentException>(() => DisposableCharacterStore.HostScriptVariables("fresh", charactersLocal));

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
