using Valheim.Testing.Game;
using Xunit;

// #869: WorldIdentity.Read is the one owner of a world fixture's structure, completeness and identity. It accepts exactly what
// Valheim 1.0 (1.0.17) loads as it is, and refuses before anything is copied what the game would load differently or not at all.
public sealed class WorldFixtureLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-world-layout-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Fixture(string name = "fixture") => FakeInstalls.World(Path.Combine(_root, name), "Campaign", 4242);
    private static string World(string fixture, string file) => Path.Combine(fixture, "Campaign", file);

    [Fact] public void AGameSavedWorldIsReadWithItsIdentity()
    {
        var identity = WorldIdentity.Read(Fixture());
        Assert.Equal(("Campaign", 4242L, "Campaign/_main.1.fwl2", 41), (identity.Name, identity.Uid, identity.File, identity.WorldVersion));
    }

    [Theory]
    [InlineData(39, false)]
    [InlineData(40, true)]
    [InlineData(42, true)]
    [InlineData(43, false)]
    [InlineData(1001, false)]
    public void AdmissionUsesTheActualHostsInclusiveBoundsAndChunkedThreshold(int version, bool accepted)
    {
        string fixture = Fixture();
        File.WriteAllBytes(World(fixture, "_main.1.fwl2"), FakeInstalls.Metadata("Campaign", 4242, version: version));
        Assert.Equal(version, WorldIdentity.Read(fixture).WorldVersion); // inspection is structural, even for future versions
        var build = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly(minimum: 40, maximum: 42, chunked: 40)), "selected host");
        if (accepted) Assert.Equal(version, WorldIdentity.Read(fixture, build).WorldVersion);
        else
        {
            string error = Assert.Throws<InvalidDataException>(() => WorldIdentity.Read(fixture, build)).Message;
            Assert.Contains("Campaign/_main.1.fwl2", error);
            Assert.Contains("world version " + version, error);
            Assert.Contains("40 through 42 inclusive", error);
            Assert.Contains("selected host", error);
        }
    }

    [Theory]
    [InlineData(39, false)]
    [InlineData(40, true)]
    public void ARangeCompatibleVersionStillRequiresTheChunkedLayoutThreshold(int version, bool accepted)
    {
        string fixture = Fixture();
        File.WriteAllBytes(World(fixture, "_main.1.fwl2"), FakeInstalls.Metadata("Campaign", 4242, version: version));
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly(minimum: 9, maximum: 42, chunked: 40)), "selected host");
        Assert.InRange(version, target.WorldSupport!.Minimum, target.WorldSupport.Maximum);
        if (accepted) Assert.Equal(version, WorldIdentity.Capture(fixture, target).Identity.WorldVersion);
        else Assert.Contains("chunked .fwl2 saves require version 40", Assert.Throws<InvalidDataException>(() => WorldIdentity.Capture(fixture, target)).Message);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("metadata")]
    [InlineData("data")]
    public void AVerifiedCopyIgnoresOnlyItsGeneratedRecordAndRetainsEveryWorldPin(string drift)
    {
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_root, "journal"));
        string fixture = Fixture();
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "host");
        var admission = WorldIdentity.Capture(fixture, target);
        using var copy = WorldFixture.Copy(fixture, Path.Combine(_root, "output"), admission.Files);
        Assert.True(File.Exists(Path.Combine(copy.DirectoryPath, WorldFixture.ProvenanceFile)));
        admission.Verify(copy.DirectoryPath); // the generated record is the only extra file
        Assert.Throws<InvalidOperationException>(() => WorldFixture.Verify(copy.DirectoryPath, admission.Files)); // source contract stays exact
        if (drift == "extra") File.WriteAllText(Path.Combine(copy.DirectoryPath, "Campaign", "unexpected.chunk"), "extra");
        else if (drift == "metadata") File.WriteAllBytes(Path.Combine(copy.DirectoryPath, "Campaign", "_main.1.fwl2"), FakeInstalls.Metadata("Campaign", 4242, version: 40));
        else File.WriteAllText(Path.Combine(copy.DirectoryPath, "Campaign", "_main.1.db2"), "changed");
        Assert.Throws<InvalidOperationException>(() => admission.Verify(copy.DirectoryPath));
        admission.Verify();
    }

    [Theory]
    [InlineData("_main.1.fwl2")]
    [InlineData("_main.1.chunks")]
    [InlineData("backup")]
    public void ParsedMetadataMustBeTheBytesOfTheContentObservation(string changed)
    {
        string fixture = FakeInstalls.Backup(Fixture());
        var observed = WorldFixture.Manifest(fixture);
        string file = changed == "backup"
            ? Path.Combine(fixture, "Campaign_backup_auto-20261020-120000", "_main.0.fwl2") : World(fixture, changed);
        byte[] bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 1;
        File.WriteAllBytes(file, bytes);
        Assert.Contains("changed after its content was observed", Assert.Throws<InvalidDataException>(() => WorldIdentity.ReadObserved(fixture, observed)).Message);
    }

    [Fact]
    public void NativeManifestKeysBindMainChunkListAndBackupMetadata()
    {
        string fixture = FakeInstalls.Backup(Fixture());
        var observed = WorldFixture.Manifest(fixture);
        Assert.Contains(Path.Combine("Campaign", "_main.1.fwl2"), observed.Keys);
        Assert.Contains(Path.Combine("Campaign", "_main.1.chunks"), observed.Keys);
        Assert.Contains(Path.Combine("Campaign_backup_auto-20261020-120000", "_main.0.fwl2"), observed.Keys);
        var identity = WorldIdentity.ReadObserved(fixture, observed);
        Assert.Equal("Campaign/_main.1.fwl2", identity.File);
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "host");
        var pinned = WorldIdentity.Capture(fixture, target, observed);
        Assert.Equal(identity, pinned.Identity);
        pinned.Verify();
        var unpinned = WorldIdentity.Capture(fixture, target);
        Assert.Equal(identity, unpinned.Identity);
        unpinned.Verify();
    }

    [Fact]
    public void OnlyACopiedObservationMayAddItsGeneratedProvenanceRecord()
    {
        string fixture = Fixture();
        var observed = WorldFixture.Manifest(fixture);
        File.WriteAllText(Path.Combine(fixture, WorldFixture.ProvenanceFile), "generated record");
        Assert.Throws<InvalidOperationException>(() => WorldIdentity.ReadObserved(fixture, observed));
        Assert.Equal(4242, WorldIdentity.ReadObserved(fixture, observed, copied: true).Uid);
        File.WriteAllText(Path.Combine(fixture, "unexpected.txt"), "not generated provenance");
        Assert.Throws<InvalidOperationException>(() => WorldIdentity.ReadObserved(fixture, observed, copied: true));
    }

    [Theory]
    [InlineData("_main.1.db2")]
    [InlineData("00_00__0_1.chunk")]
    public void AdmissionRejectsContentDriftWithTheSameSizeAndRestoredTimestamp(string relative)
    {
        string fixture = Fixture();
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "host");
        var admission = WorldIdentity.Capture(fixture, target);
        string file = World(fixture, relative);
        DateTime modified = File.GetLastWriteTimeUtc(file);
        byte[] bytes = File.ReadAllBytes(file);
        bytes[0] ^= 1;
        File.WriteAllBytes(file, bytes);
        File.SetLastWriteTimeUtc(file, modified);
        Assert.Throws<InvalidOperationException>(() => admission.Verify());
    }

    [Theory]
    [InlineData("")]
    [InlineData("worlds_local")]
    public void AdmittedCopyMapsTheWorldAndRetainsEveryParentPin(string worldRoot)
    {
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_root, "journal"));
        string source = Path.Combine(_root, "source"), worlds = FakeInstalls.World(Path.Combine(source, worldRoot));
        if (worldRoot.Length != 0) File.WriteAllText(Path.Combine(source, "server.cfg"), "parent pin");
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "host");
        var admission = WorldIdentity.Capture(worlds, target); var parentPins = WorldFixture.Manifest(source);
        using var copy = WorldFixture.CopyAdmitted(source, Path.Combine(_root, "output"), parentPins, admission, worldRoot);
        Assert.Equal(parentPins, copy.SourceHashes);
        Assert.Equal(admission.Identity, WorldIdentity.Read(Path.Combine(copy.DirectoryPath, worldRoot)));
        Assert.True(File.Exists(Path.Combine(copy.DirectoryPath, WorldFixture.ProvenanceFile)));
        if (worldRoot.Length != 0) Assert.Equal("parent pin", File.ReadAllText(Path.Combine(copy.DirectoryPath, "server.cfg")));
        admission.Verify();
    }

    [Theory]
    [InlineData("")]
    [InlineData("worlds_local")]
    public void AdmittedCopyRefusesAnEarlierOriginalChangedDuringALaterCopy(string worldRoot)
    {
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_root, "journal"));
        string source = Path.Combine(_root, "source"), worlds = FakeInstalls.World(Path.Combine(source, worldRoot));
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "host");
        var admission = WorldIdentity.Capture(worlds, target); var pins = WorldFixture.Manifest(source);
        string output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        string keep = Path.Combine(output, "unrelated.txt"); File.WriteAllText(keep, "keep");
        string? earlier = null; int copies = 0;
        Assert.Throws<InvalidOperationException>(() => WorldFixture.CopyAdmitted(source, output, pins, admission, worldRoot, copyFile: (from, to) =>
        {
            File.Copy(from, to);
            if (++copies == 1) earlier = from;
            else if (copies == 2) ChangeByte(earlier!);
        }));
        Assert.True(copies > 1); Assert.Empty(Directory.GetDirectories(output));
        Assert.True(Directory.Exists(source)); Assert.Equal("keep", File.ReadAllText(keep));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_root, "journal"), "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines).Select(RunJournal.ParseLine), record => record.Entry.Kind == JournalEntry.CopyDone);
    }

    [Theory]
    [InlineData("", "world")]
    [InlineData("worlds_local", "world")]
    [InlineData("worlds_local", "parent")]
    [InlineData("worlds_local", "extra")]
    public void AdmittedCopyChecksTheWholeDestinationAfterFinalSourceVerification(string worldRoot, string fault)
    {
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_root, "journal"));
        string source = Path.Combine(_root, "source"), worlds = FakeInstalls.World(Path.Combine(source, worldRoot));
        if (worldRoot.Length != 0) File.WriteAllText(Path.Combine(source, "server.cfg"), "parent pin");
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "host");
        var admission = WorldIdentity.Capture(worlds, target); var pins = WorldFixture.Manifest(source);
        string output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        string keep = Path.Combine(output, "unrelated.txt"); File.WriteAllText(keep, "keep");
        Assert.Throws<InvalidOperationException>(() => WorldFixture.CopyAdmitted(source, output, pins, admission, worldRoot,
            afterSourceVerification: copy =>
            {
                if (fault == "extra") File.WriteAllText(Path.Combine(copy, "extra.cfg"), "outside world");
                else ChangeByte(fault == "parent" ? Path.Combine(copy, "server.cfg") : Path.Combine(copy, worldRoot, "Campaign", FakeInstalls.ChunkFile));
            }));
        Assert.Empty(Directory.GetDirectories(output)); Assert.Equal("keep", File.ReadAllText(keep));
        WorldFixture.Verify(source, pins); admission.Verify(); // corruption was destination-only
    }

    [Theory]
    [InlineData("../worlds_local")]
    [InlineData("/worlds_local")]
    [InlineData("other")]
    public void AdmittedCopyRefusesAnUnsupportedWorldRoot(string worldRoot)
    {
        string fixture = Fixture();
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "host");
        var admission = WorldIdentity.Capture(fixture, target);
        Assert.Throws<ArgumentException>(() => WorldFixture.CopyAdmitted(fixture, Path.Combine(_root, "output"), admission.Files, admission, worldRoot));
        Assert.False(Directory.Exists(Path.Combine(_root, "output")));
    }

    [Fact]
    public void AdmittedCopyRefusesAnotherIdenticalSourceAndWrongIdentity()
    {
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_root, "journal"));
        string fixture = Fixture(), other = Fixture("other");
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "host");
        var admission = WorldIdentity.Capture(fixture, target); string output = Path.Combine(_root, "output");
        Assert.Throws<InvalidOperationException>(() => WorldFixture.CopyAdmitted(other, output, admission.Files, admission, ""));
        Assert.False(Directory.Exists(output));
        var wrong = admission with { Identity = admission.Identity with { Uid = 999 } };
        Assert.Throws<InvalidDataException>(() => WorldFixture.CopyAdmitted(fixture, output, admission.Files, wrong, ""));
        Assert.Empty(Directory.GetDirectories(output)); admission.Verify();
    }

    private static void ChangeByte(string path)
    {
        DateTime modified = File.GetLastWriteTimeUtc(path); byte[] bytes = File.ReadAllBytes(path); bytes[0] ^= 1;
        File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, modified);
    }

    [Fact]
    public void MatchingNetworkVersionsDoNotEstablishWorldSupportAndUnknownSingleHostsRefuse()
    {
        string fixture = Fixture();
        GameBuild Read(int maximum) => GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly(maximum: maximum)), "host");
        var accepting = Read(42); var refusing = Read(40);
        Assert.Equal(accepting.NetworkVersion, refusing.NetworkVersion);
        Assert.Equal(4242, WorldIdentity.Read(fixture, accepting).Uid);
        Assert.Throws<InvalidDataException>(() => WorldIdentity.Read(fixture, refusing));
        Assert.Contains("Cannot establish world-format", Assert.Throws<InvalidOperationException>(() => WorldIdentity.Read(fixture, new GameBuild(40, "1.0.17", null))).Message);
    }

    [Fact]
    public void ConversionLeftoversAndAnUnderscoreOnlyAtTheStartHaveDistinctRules()
    {
        string fixture = Fixture();
        File.WriteAllText(Path.Combine(fixture, "Campaign.fwl.old"), "old");
        Assert.Contains("conversion leftovers", Assert.Throws<ArgumentException>(() => WorldIdentity.Read(fixture)).Message);
        string named = FakeInstalls.World(Path.Combine(_root, "named"), "_Base20261021120000");
        Assert.Equal("_Base20261021120000", WorldIdentity.Read(named).Name);
        Assert.Equal("_Base20261021120000", HostedWorld.NameOf(WorldFixture.Manifest(named).Keys));
        Assert.Throws<ArgumentException>(() => WorldIdentity.RequireName("Base_20261021120000"));
    }

    [Fact] public void ThePackagedSmokeWorldIsComplete()
    {
        string root = Path.Combine(_root, "smoke");
        Assert.Equal(DefaultSmokeWorld.Uid, DefaultSmokeWorld.Prepare(root).UidText);
        Assert.Equal(DefaultSmokeWorld.Uid, WorldIdentity.Read(root).UidText); // its .chunks lists exactly its one chunk file
    }

    // The issue's case: metadata alone, with every companion of the save named.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservationDoesNotHideAddedOrMissingPayloadNames(bool added)
    {
        string fixture = Fixture();
        var observed = WorldFixture.Manifest(fixture);
        if (added) File.WriteAllText(World(fixture, "01_01__0_1.chunk"), "extra");
        else File.Delete(World(fixture, FakeInstalls.ChunkFile));
        Assert.Throws<InvalidOperationException>(() => WorldIdentity.ReadObserved(fixture, observed));
    }

    [UnixFact]
    public void ObservationDoesNotAllowAPayloadToBecomeALink()
    {
        string fixture = Fixture();
        var observed = WorldFixture.Manifest(fixture);
        string chunk = World(fixture, FakeInstalls.ChunkFile), outside = Path.Combine(_root, "outside.chunk");
        File.Move(chunk, outside);
        File.CreateSymbolicLink(chunk, outside);
        Assert.Contains("Links are unsupported", Assert.Throws<IOException>(() => WorldIdentity.ReadObserved(fixture, observed)).Message);
    }

    [Theory]
    [InlineData("_main.1.db2")]
    [InlineData("_main.1.chunks")]
    [InlineData("_main.1.ok")]
    public void AnIncompleteSaveIsRefusedNamingWhatIsMissing(string missing)
    {
        string fixture = Fixture();
        File.Delete(World(fixture, missing));
        var error = Assert.Throws<ArgumentException>(() => WorldIdentity.Read(fixture));
        Assert.Contains("is incomplete", error.Message);
        Assert.Contains("Campaign/" + missing, error.Message);
    }

    [Fact] public void MetadataAloneIsRefusedUnlessItIsAWorldNeverSaved()
    {
        string fixture = Path.Combine(_root, "metadata-only");
        Directory.CreateDirectory(Path.Combine(fixture, "Campaign"));
        File.WriteAllBytes(World(fixture, "_main.3.fwl2"), FakeInstalls.Metadata("Campaign", 4242));
        var error = Assert.Throws<ArgumentException>(() => WorldIdentity.Read(fixture));
        Assert.Contains("has no Campaign/_main.3.db2, Campaign/_main.3.chunks, Campaign/_main.3.ok", error.Message);

        // A new world the game has not saved yet is a lone _main.0.fwl2 that says so; one that says it has data is not.
        File.Delete(World(fixture, "_main.3.fwl2"));
        File.WriteAllBytes(World(fixture, "_main.0.fwl2"), FakeInstalls.Metadata("Campaign", 4242, needsDb: false));
        Assert.Equal(4242, WorldIdentity.Read(fixture).Uid);
        File.WriteAllBytes(World(fixture, "_main.0.fwl2"), FakeInstalls.Metadata("Campaign", 4242, needsDb: true));
        Assert.Contains("generate the world afresh", Assert.Throws<InvalidDataException>(() => WorldIdentity.Read(fixture)).Message);
    }

    [Fact] public void AChunkTheListNamesMustBeThereAndNoOther()
    {
        string fixture = Fixture();
        File.Delete(World(fixture, FakeInstalls.ChunkFile));
        var missing = Assert.Throws<InvalidDataException>(() => WorldIdentity.Read(fixture));
        Assert.Contains("lists 00_00__0_1.chunk", missing.Message);

        File.WriteAllText(World(fixture, FakeInstalls.ChunkFile), "chunk");
        File.WriteAllText(World(fixture, "01_ff__2_7.chunk"), "an older save's chunk");
        var unlisted = Assert.Throws<InvalidDataException>(() => WorldIdentity.Read(fixture));
        Assert.Contains("01_ff__2_7.chunk", unlisted.Message);
        Assert.Contains("does not list", unlisted.Message);

        // The list's own names: chunk 0x01ff of size 2 at version 7.
        File.WriteAllBytes(World(fixture, "_main.1.chunks"), FakeInstalls.ChunkList((0, 0, 1), (0x01ff, 2, 7)));
        Assert.Equal(4242, WorldIdentity.Read(fixture).Uid);
        File.WriteAllBytes(World(fixture, "_main.1.chunks"), FakeInstalls.ChunkList((0, 0, 1), (0x01ff, 2, 7))[..15]);
        Assert.Contains("Cannot read the chunk list Campaign/_main.1.chunks", Assert.Throws<InvalidDataException>(() => WorldIdentity.Read(fixture)).Message);
    }

    // The game keeps only the newest complete set and deletes the rest when it loads: a pinned copy would not be what loads.
    [Fact] public void TwoSavesInTheWorldFolderAreRefused()
    {
        string fixture = Fixture();
        File.WriteAllBytes(World(fixture, "_main.2.fwl2"), FakeInstalls.Metadata("Campaign", 4242));
        var error = Assert.Throws<ArgumentException>(() => WorldIdentity.Read(fixture));
        Assert.Contains("holds saves 1, 2", error.Message);
    }

    // D5: the game's automatic backups ride along, checked only for the world's name and UID (#869: they were counted as a second world).
    [Fact] public void TheGamesBackupsAreAcceptedWhenTheyAreTheSameWorld()
    {
        string fixture = FakeInstalls.Backup(Fixture());
        Assert.Equal("Campaign", HostedWorld.NameOf(WorldFixture.Manifest(fixture).Keys));
        Assert.Equal(4242, WorldIdentity.Read(fixture).Uid);

        File.WriteAllBytes(Path.Combine(fixture, "Campaign_backup_auto-20261020-120000", "_main.0.fwl2"), FakeInstalls.Metadata("Campaign", 9999));
        Assert.Contains("saves of different worlds", Assert.Throws<InvalidDataException>(() => WorldIdentity.Read(fixture)).Message);
    }

    [Fact] public void APreOneZeroWorldIsRefusedWithHowToConvertIt()
    {
        string fixture = Path.Combine(_root, "flat");
        Directory.CreateDirectory(fixture);
        File.WriteAllBytes(Path.Combine(fixture, "Campaign.fwl"), FakeInstalls.Metadata("Campaign", 4242));
        File.WriteAllText(Path.Combine(fixture, "Campaign.db"), "world");
        var error = Assert.Throws<ArgumentException>(() => WorldIdentity.Read(fixture));
        Assert.Contains("a world saved before Valheim 1.0", error.Message);
        Assert.Contains("Open Campaign once in Valheim 1.0 or later and save it", error.Message);
        // The same refusal by name alone, before any file is read (a plan's pinned manifest).
        Assert.Contains("before Valheim 1.0", Assert.Throws<ArgumentException>(() => HostedWorld.NameOf(["Campaign.fwl", "Campaign.db"])).Message);
    }

    [Theory]
    [InlineData(new[] { "LifecycleFixture/_main.1.chunks", "LifecycleFixture/_main.1.db2", "LifecycleFixture/_main.1.fwl2", "LifecycleFixture/_main.1.ok" }, "LifecycleFixture")]
    [InlineData(new[] { "Abc\\_main.12.fwl2", "Abc\\_main.12.db2", "Abc\\_main.12.chunks", "Abc\\_main.12.ok", "Abc\\00_00__0_3.chunk" }, "Abc")]
    [InlineData(new[] { "Abc/_main.0.fwl2" }, "Abc")] // a world never saved
    [InlineData(new[] { "Abc/_main.1.fwl2", "Abc/_main.1.db2", "Abc/_main.1.chunks", "Abc/_main.1.ok", "Abc_backup_auto-20261020-120000/_main.0.fwl2" }, "Abc")]
    // Every backup kind the game names (SaveSystem.cs:1100): a standard one as a 1.0.20 dedicated server wrote beside its world
    // (#869's native check), a restore's and a cloud one; a world name with underscores of its own.
    [InlineData(new[] { "Abc/_main.0.fwl2", "Abc_backup_20261021-092942/_main.1.fwl2", "Abc_backup_restore-20261021-092942/_main.1.fwl2",
        "Abc_backup_cloud-20261021-092942/_main.1.fwl2" }, "Abc")]
    [InlineData(new[] { "My_Base/_main.0.fwl2", "My_Base_backup_auto-20261021-092942/_main.1.fwl2" }, "My_Base")]
    // A world a client opened: its minimap cache beside the save (Minimap.cs).
    [InlineData(new[] { "Abc/_main.0.fwl2", "Abc/cacheMinimapMask", "Abc/cacheMinimapBiome", "Abc/cacheMinimapHeight", "Abc/cacheMinimapMeta" }, "Abc")]
    public void TheWorldIsNamedByItsFolder(string[] files, string name) => Assert.Equal(name, HostedWorld.NameOf(files));

    public static TheoryData<string[]> NotOneCompleteWorld => new()
    {
        new[] { "HostFixture.fwl", "HostFixture.db" }, // A pre-1.0 world.
        new[] { "HostFixture/_main.1.fwl2", "HostFixture/_main.1.db2", "HostFixture/_main.1.chunks", "HostFixture/_main.1.ok", "HostFixture.db.old" }, // Beside a pre-1.0 one.
        new[] { "A1c/_main.0.fwl2", "B2c/_main.0.fwl2" }, // Two worlds.
        new[] { "HostFixture/_main.0.fwl2", "readme.txt" }, // A file that is not the world's.
        new[] { "HostFixture/_main.0.fwl2", "HostFixture/notes.txt" }, // A file the game does not write in the world folder.
        new[] { "HostFixture/deeper/_main.0.fwl2" }, // The metadata is not directly in the world folder.
        new[] { "Ab/_main.0.fwl2" }, // Too short a name for the game.
        new[] { "My World/_main.0.fwl2" }, // Not one command token.
        new[] { "Base_20261021-120000/_main.0.fwl2" }, // A name the game reads as a rolling save of "Base".
        new[] { "HostFixture/_main.0.fwl2", "HostFixture/.DS_Store" }, // Finder's file in the world folder.
        new[] { "HostFixture/_main.1.fwl2" }, // Metadata alone of a saved world.
        new[] { "HostFixture/_main.01.fwl2", "HostFixture/_main.01.db2", "HostFixture/_main.01.chunks", "HostFixture/_main.01.ok" }, // Not a name the game writes.
        new[] { "HostFixture/_main.0.fwl2", "HostFixture/_main.1.fwl2", "HostFixture/_main.1.db2", "HostFixture/_main.1.chunks", "HostFixture/_main.1.ok" }, // An orphan set.
        new[] { "HostFixture/_main.0.fwl2", "HostFixture/00_00__0_1.chunk" }, // Chunks no list names.
        new[] { "HostFixture/_main.0.fwl2", "Other_backup_auto-20261020-120000/_main.0.fwl2" }, // Another world's backup.
        new[] { "HostFixture/_main.0.fwl2", "HostFixture_backup_auto-20261020-120000/_main.0.db2" }, // A backup without metadata.
        new[] { "HostFixture/_main.0.fwl2", "HostFixture_backup_auto-2026102-120000/_main.0.fwl2" }, // No timestamp the game reads.
        new[] { "HostFixture/_main.0.fwl2", "HostFixture_backup_auto-20261340-120000/_main.0.fwl2" }, // Not a valid date: the game reads no timestamp.
        new[] { "HostFixture/_main.0.fwl2", "fixture-provenance.json" }, // A copy's record in a pinned manifest: the placement would ship it.
        new[] { "HostFixture_backup_auto-20261020-120000/_main.0.fwl2" }, // Only a backup.
        Array.Empty<string>(), // Nothing.
    };
    [Theory] [MemberData(nameof(NotOneCompleteWorld))]
    public void AFixtureThatIsNotOneCompleteWorldIsRefusedByName(string[] files) => Assert.Throws<ArgumentException>(() => HostedWorld.NameOf(files));

    // The hosted and dedicated preflights both reach the owner before anything is copied.
    [Fact] public void BothPreflightsRefuseAnIncompleteWorldBeforeCopying()
    {
        string fixture = Fixture();
        File.Delete(World(fixture, "_main.1.ok"));
        var hosted = new HostWorldPlan { World = new() { Source = fixture }, WorldUid = "4242" };
        Assert.Contains("is incomplete", Assert.Throws<ArgumentException>(hosted.Preflight).Message);

        string save = Path.Combine(_root, "server-save");
        Directory.CreateDirectory(save);
        Directory.Move(fixture, Path.Combine(save, "worlds_local"));
        var server = new ServerRunPlan { World = new() { Source = save } };
        Assert.Contains("is incomplete", Assert.Throws<ArgumentException>(() => server.PreflightWorld()).Message);
        File.WriteAllBytes(Path.Combine(save, "worlds_local", "Campaign", "_main.1.ok"), BitConverter.GetBytes(41));
        Assert.Equal(4242, server.PreflightWorld()!.Uid);
    }

    // D3: a dedicated server's fixture is its -savedir with exactly one world in worlds_local/; world=any alone may start without one.
    [Fact] public void ADedicatedServerFixtureIsOneWorldInItsSaveDirectory()
    {
        string save = Path.Combine(_root, "server-save");
        FakeInstalls.World(Path.Combine(save, "worlds_local"), "Campaign", 4242);
        var plan = new ServerRunPlan { World = new() { Source = save }, Pins = new() { ["worlduid"] = "4242" } };
        Assert.Equal("Campaign", plan.PreflightWorld()!.Name);
        plan.Pins["worlduid"] = "7";
        Assert.Contains("the same name, another world", Assert.Throws<InvalidOperationException>(() => plan.PreflightWorld()).Message);

        FakeInstalls.World(Path.Combine(save, "worlds_local"), "Second", 5);
        Assert.Contains("exactly one world, not 2", Assert.Throws<ArgumentException>(() => plan.PreflightWorld()).Message);

        // The world folder beside worlds_local/ instead of in it: the server would make a fresh world.
        string bare = FakeInstalls.World(Path.Combine(_root, "bare"), "Campaign", 4242);
        var unwrapped = new ServerRunPlan { World = new() { Source = bare } };
        Assert.Contains("has no worlds_local/", Assert.Throws<ArgumentException>(() => unwrapped.PreflightWorld()).Message);

        string empty = Path.Combine(_root, "new-world");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "NEW-WORLD.txt"), "the game makes the world on first boot");
        Assert.Null(new ServerRunPlan { World = new() { Source = empty }, Pins = new() { ["world"] = "any" } }.PreflightWorld());
        Directory.CreateDirectory(Path.Combine(empty, "worlds_local")); // an empty worlds_local/ the game fills
        Assert.Null(new ServerRunPlan { World = new() { Source = empty }, Pins = new() { ["world"] = "any" } }.PreflightWorld());
        Assert.Throws<ArgumentException>(() => new ServerRunPlan { World = new() { Source = empty } }.PreflightWorld());
    }

    [Fact] public void FixtureLayoutShowsTheTreeForAnIncompleteWorld()
    {
        string fixture = Fixture();
        File.Delete(World(fixture, "_main.1.db2"));
        var error = Assert.Throws<InvalidOperationException>(() => FixtureLayout.Discover(fixture, "4242"));
        Assert.Contains("Campaign/_main.1.db2", error.Message);
        Assert.Contains("Expected:", error.Message);
        Assert.Contains("_main.<n>.chunks", error.Message);
        Assert.Contains("Found:", error.Message);
    }
}
