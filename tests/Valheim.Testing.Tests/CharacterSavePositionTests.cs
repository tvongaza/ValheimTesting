using System.Security.Cryptography;
using System.Diagnostics;
using Valheim.Testing.Game;
using Xunit;

public class CharacterSavePositionTests
{
    [Fact]
    public void OnlyTheRequestedWorldsLogoutFlagAndPositionChange()
    {
        var original = Profile(mapBytes: 140_000);
        byte[] changed = CharacterSavePosition.AtWorld(original.File, 200, 12.5f, 80f, -7f);
        var after = Payload(changed);
        var before = Payload(original.File);

        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before.AsSpan(0, original.World200Flag).ToArray(), after.AsSpan(0, original.World200Flag).ToArray());
        Assert.Equal(1, after[original.World200Flag]);
        Assert.Equal(12.5f, BitConverter.ToSingle(after, original.World200Flag + 1));
        Assert.Equal(80f, BitConverter.ToSingle(after, original.World200Flag + 5));
        Assert.Equal(-7f, BitConverter.ToSingle(after, original.World200Flag + 9));
        Assert.Equal(before.AsSpan(original.World200Flag + 13).ToArray(), after.AsSpan(original.World200Flag + 13).ToArray());
        Assert.Equal(0, after[original.World100Flag]);
        Assert.NotEqual(original.File, changed);
    }

    [Fact]
    public void MissingOrDuplicatedWorldUidIsRefused()
    {
        var original = Profile();
        Assert.Throws<KeyNotFoundException>(() => CharacterSavePosition.AtWorld(original.File, 300, 1, 2, 3));
        var duplicate = Profile(secondUid: 100);
        Assert.Throws<InvalidDataException>(() => CharacterSavePosition.AtWorld(duplicate.File, 100, 1, 2, 3));
    }

    [Fact]
    public void CleanCharacterCanGainExactlyOneWorldEntryWithoutChangingItsIdentity()
    {
        byte[] clean = Profile(includeWorlds: false).File;
        Assert.True(CharacterSavePosition.IsFreshSmokeSeed(clean));
        Assert.Throws<KeyNotFoundException>(() => CharacterSavePosition.AtWorld(clean, 300, 12, 43, -8));

        byte[] prepared = CharacterSavePosition.AtNewWorld(clean, 300, 12, 43, -8);
        Assert.Equal(CharacterSavePosition.ReadIdentity(clean), CharacterSavePosition.ReadIdentity(prepared));
        Assert.False(CharacterSavePosition.IsFreshSmokeSeed(prepared));
        Assert.Equal(prepared, CharacterSavePosition.AtWorld(prepared, 300, 12, 43, -8));
        Assert.Throws<InvalidOperationException>(() => CharacterSavePosition.AtNewWorld(prepared, 300, 12, 43, -8));
        Assert.Equal(clean, Profile(includeWorlds: false).File); // The supplied bytes were never edited in place.
    }

    [Fact]
    public void AddingAWorldPreservesExistingWorldsAndRejectsFirstSpawn()
    {
        var fixture = Profile(mapBytes: 140_000);
        byte[] original = fixture.File;
        byte[] added = CharacterSavePosition.AtNewWorld(original, 300, -21, 61, 14);
        Assert.Equal(added, CharacterSavePosition.AtWorld(added, 300, -21, 61, 14));
        Assert.Equal(Payload(original).AsSpan(fixture.World100Flag, 13).ToArray(), Payload(added).AsSpan(fixture.World100Flag, 13).ToArray());
        Assert.Equal(Payload(original).AsSpan(fixture.World200Flag, 13).ToArray(), Payload(added).AsSpan(fixture.World200Flag, 13).ToArray());
        Assert.Throws<InvalidDataException>(() => CharacterSavePosition.AtNewWorld(Profile(firstSpawn: true).File, 300, 1, 2, 3));
    }

    [Fact]
    public void UnfamiliarVersionOrStatLayoutIsRefused()
    {
        var original = Profile();
        var version = Payload(original.File);
        BitConverter.GetBytes(47).CopyTo(version, 0);
        Assert.Throws<NotSupportedException>(() => CharacterSavePosition.AtWorld(Envelope(version), 200, 1, 2, 3));
        var layout = Payload(original.File);
        BitConverter.GetBytes(204).CopyTo(layout, 4);
        Assert.Throws<InvalidDataException>(() => CharacterSavePosition.AtWorld(Envelope(layout), 200, 1, 2, 3));
    }

    [Fact]
    public void DamagedHashAndTruncatedSaveAreRefused()
    {
        byte[] file = Profile().File;
        byte[] damaged = (byte[])file.Clone();
        damaged[20] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => CharacterSavePosition.AtWorld(damaged, 200, 1, 2, 3));
        Assert.Throws<InvalidDataException>(() => CharacterSavePosition.AtWorld(file[..^1], 200, 1, 2, 3));
        byte[] extra = [.. file, 0];
        Assert.Throws<InvalidDataException>(() => CharacterSavePosition.AtWorld(extra, 200, 1, 2, 3));
        byte[] extraPayload = [.. Payload(file), 0];
        Assert.Throws<InvalidDataException>(() => CharacterSavePosition.AtWorld(Envelope(extraPayload), 200, 1, 2, 3));
    }

    [Fact]
    public void InvalidUidAndNonFinitePositionAreRefused()
    {
        byte[] file = Profile().File;
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterSavePosition.AtWorld(file, 0, 1, 2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterSavePosition.AtWorld(file, 200, float.NaN, 2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterSavePosition.AtWorld(file, 200, 1, float.PositiveInfinity, 3));
    }

    [Fact]
    public void ACharacterStillOnItsFirstSpawnCannotUseALogoutPoint()
    {
        var firstSpawn = Profile(firstSpawn: true);
        Assert.Throws<InvalidDataException>(() => CharacterSavePosition.AtWorld(firstSpawn.File, 200, 1, 2, 3));
    }

    [Fact]
    public void HostedWithoutDirectStartAndAttachedPlansCannotClaimAPreparedCharacterStart()
    {
        var hosted = new ClientRunPlan { Mode = "attach", HostWorld = new HostWorldPlan(), Port = 5556,
            Character = "fresh", StartAtCharacterSave = true, Pinning = "none" };
        Assert.Contains("requires directStart", Assert.Throws<ArgumentException>(() => hosted.Validate()).Message);
        var attached = new ClientRunPlan { Mode = "attach", Join = "127.0.0.1:2456", Port = 5556,
            Character = "fresh", StartAtCharacterSave = true, Pinning = "none" };
        Assert.Contains("owned client", Assert.Throws<ArgumentException>(() => attached.Validate()).Message);
    }

    [Fact]
    public void PreparationWritesOnlyANewCopyOutsideTheCharacterDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-character-" + Guid.NewGuid().ToString("N"));
        string local = Path.Combine(root, "characters_local"), evidence = Path.Combine(root, "evidence");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(evidence);
        try
        {
            string source = Path.Combine(local, "test.fch"), output = Path.Combine(evidence, "test.fch");
            byte[] original = Profile().File;
            File.WriteAllBytes(source, original);
            var character = Register(root, source);
            string digest = CharacterStartCopy.Prepare(character, output, 200, 14, 50, -4);
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "store", "tester.fch")));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant(), digest);
            Assert.Equal(SHA512.HashData(Payload(File.ReadAllBytes(output))), File.ReadAllBytes(output)[^64..]);
            Assert.NotEqual(original, File.ReadAllBytes(output));
            Assert.Throws<IOException>(() => CharacterStartCopy.Prepare(character, output, 200, 1, 2, 3));
            Assert.Equal(14f, BitConverter.ToSingle(Payload(File.ReadAllBytes(output)), Profile().World200Flag + 1));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RegisteredCleanCharacterCanBePreparedForFirstWorldAndStaged()
    {
        string root = Directory.CreateTempSubdirectory("vt-character-new-world-").FullName;
        string local = Path.Combine(root, "characters_local"), evidence = Path.Combine(root, "evidence");
        string steam = Path.Combine(root, "userdata");
        Directory.CreateDirectory(local); Directory.CreateDirectory(evidence); Directory.CreateDirectory(steam);
        try
        {
            string source = Path.Combine(local, "seed.fch"), prepared = Path.Combine(evidence, "newworld.fch");
            byte[] clean = Profile(includeWorlds: false).File;
            File.WriteAllBytes(source, clean);
            var character = Register(root, source);
            string hash = CharacterStartCopy.PrepareForNewWorld(character, prepared, 300, 12, 43, -8);
            Assert.Equal(clean, File.ReadAllBytes(source));
            var plan = new CharacterStartPlan { PreparedFile = prepared, Sha256 = hash,
                CharactersLocalDirectory = local, SteamUserDataDirectory = steam, CharacterStore = Path.Combine(root, "store") };
            using (CharacterStartStage.Install(plan, "newworld", 300, new HeightExpectation(12, -8, 43)))
                Assert.Equal(File.ReadAllBytes(prepared), File.ReadAllBytes(Path.Combine(local, "newworld.fch")));
            Assert.False(File.Exists(Path.Combine(local, "newworld.fch")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PreparationRefusesCloudSourcesAndLiveCharacterDestinations()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-character-" + Guid.NewGuid().ToString("N"));
        string local = Path.Combine(root, "characters_local"), cloud = Path.Combine(root, "characters");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(cloud);
        try
        {
            string localFile = Path.Combine(local, "test.fch"), cloudFile = Path.Combine(cloud, "test.fch");
            File.WriteAllBytes(localFile, Profile().File);
            File.WriteAllBytes(cloudFile, Profile().File);
            var store = DisposableCharacterStore.Create(Path.Combine(root, "store"));
            Assert.Throws<ArgumentException>(() => store.Register("cloudy", cloudFile));
            Assert.Empty(store.Names);
            var character = store.Register("tester", localFile);
            Assert.Throws<ArgumentException>(() => CharacterStartCopy.Prepare(character, Path.Combine(local, "copy.fch"), 200, 1, 2, 3));
            Assert.Throws<ArgumentException>(() => CharacterStartCopy.Prepare(character, Path.Combine(cloud, "copy.fch"), 200, 1, 2, 3));
            Assert.Throws<ArgumentException>(() => CharacterStartCopy.Prepare(character, Path.Combine(store.Root, "copy.fch"), 200, 1, 2, 3));
            Assert.False(File.Exists(Path.Combine(local, "copy.fch")));
            Assert.False(File.Exists(Path.Combine(store.Root, "copy.fch")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ALinkedOutputCannotEscapeIntoTheLiveCharacterDirectory()
    {
        string root = Directory.CreateTempSubdirectory("vt-character-link-").FullName;
        string local = Path.Combine(root, "characters_local"), evidence = Path.Combine(root, "evidence");
        Directory.CreateDirectory(local); Directory.CreateDirectory(evidence);
        try
        {
            string source = Path.Combine(local, "seed.fch");
            File.WriteAllBytes(source, Profile().File);
            var character = Register(root, source);
            string link = Path.Combine(evidence, "link");
            if (OperatingSystem.IsWindows())
            {
                // A junction is the form that escaped the lexical guard on the station; creating one needs no
                // Developer Mode symlink privilege on Windows.
                using var junction = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{local}\"")
                { UseShellExecute = false, CreateNoWindow = true });
                junction!.WaitForExit();
                Assert.Equal(0, junction.ExitCode);
            }
            else Directory.CreateSymbolicLink(link, local);
            Assert.Throws<ArgumentException>(() => CharacterStartCopy.Prepare(character, Path.Combine(link, "copy.fch"), 200, 1, 2, 3));
            Assert.False(File.Exists(Path.Combine(local, "copy.fch")));
        }
        finally
        {
            // Windows' recursive remover cannot traverse a junction here; remove the link itself first.
            string link = Path.Combine(evidence, "link");
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StagePinsThePreparedPointAndRemovesOnlyItsFreshCharacterFiles()
    {
        string root = Directory.CreateTempSubdirectory("vt-character-stage-").FullName;
        string local = Path.Combine(root, "characters_local"), evidence = Path.Combine(root, "evidence");
        string steam = Path.Combine(root, "userdata");
        Directory.CreateDirectory(local); Directory.CreateDirectory(evidence); Directory.CreateDirectory(steam);
        try
        {
            string original = Path.Combine(local, "seed.fch"), prepared = Path.Combine(evidence, "fresh.fch");
            File.WriteAllBytes(original, Profile().File);
            string hash = CharacterStartCopy.Prepare(Register(root, original), prepared, 200, 10, 40, -20);
            var plan = new CharacterStartPlan { PreparedFile = prepared, Sha256 = hash,
                CharactersLocalDirectory = local, SteamUserDataDirectory = steam, CharacterStore = Path.Combine(root, "store") };
            var point = new HeightExpectation(10, -20, 40);
            using (var stage = CharacterStartStage.Install(plan, "fresh", 200, point))
            {
                Assert.Equal(File.ReadAllBytes(prepared), File.ReadAllBytes(Path.Combine(local, "fresh.fch")));
                File.WriteAllText(Path.Combine(local, "fresh.fch.old"), "game backup");
                File.WriteAllText(Path.Combine(local, "fresh_backup_auto-1.fch"), "game backup");
            }
            Assert.True(File.Exists(original)); Assert.True(File.Exists(prepared));
            Assert.False(File.Exists(Path.Combine(local, "fresh.fch")));
            Assert.False(File.Exists(Path.Combine(local, "fresh.fch.old")));
            Assert.False(File.Exists(Path.Combine(local, "fresh_backup_auto-1.fch")));
            Assert.Throws<InvalidDataException>(() => CharacterStartStage.Install(plan, "fresh", 200, new HeightExpectation(11, -20, 40)));
            plan.Sha256 = new string('0', 64);
            Assert.Throws<InvalidDataException>(() => CharacterStartStage.Install(plan, "fresh", 200, point));
            Assert.False(File.Exists(Path.Combine(local, "fresh.fch")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void StageRefusesLocalAndBothCloudNameCollisionsBeforeItWrites()
    {
        string root = Directory.CreateTempSubdirectory("vt-character-collision-").FullName;
        string local = Path.Combine(root, "characters_local"), evidence = Path.Combine(root, "evidence");
        string steam = Path.Combine(root, "userdata"), cloud = Path.Combine(root, "characters");
        string remote = Path.Combine(steam, "12345", "892970", "remote", "characters");
        foreach (var directory in new[] { local, evidence, steam, cloud, remote }) Directory.CreateDirectory(directory);
        try
        {
            string original = Path.Combine(local, "seed.fch"), prepared = Path.Combine(evidence, "fresh.fch");
            File.WriteAllBytes(original, Profile().File);
            var plan = new CharacterStartPlan { PreparedFile = prepared,
                Sha256 = CharacterStartCopy.Prepare(Register(root, original), prepared, 200, 10, 40, -20),
                CharactersLocalDirectory = local, SteamUserDataDirectory = steam, CharacterStore = Path.Combine(root, "store") };
            var point = new HeightExpectation(10, -20, 40);
            foreach (var collision in new[] { Path.Combine(local, "FRESH.fch.old"),
                Path.Combine(cloud, "fresh.fch"), Path.Combine(remote, "fresh.fch") })
            {
                File.WriteAllText(collision, "existing");
                Assert.Throws<IOException>(() => CharacterStartStage.Install(plan, "fresh", 200, point));
                Assert.Equal("existing", File.ReadAllText(collision));
                Assert.False(File.Exists(Path.Combine(local, "fresh.fch")));
                File.Delete(collision);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Registers a characters_local save as "tester" in a new store at <root>/store.
    internal static DisposableCharacter Register(string root, string localCharacterFile) =>
        DisposableCharacterStore.Create(Path.Combine(root, "store")).Register("tester", localCharacterFile);

    internal sealed record Fixture(byte[] File, int World100Flag, int World200Flag);

    // A hand-built 1.0.16 profile payload. No game save or decompiled source is checked in.
    internal static Fixture Profile(int mapBytes = 0, long secondUid = 200, bool firstSpawn = false, long playerId = 5678,
        bool includeWorlds = true)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(46); writer.Write(205); writer.Write(10);
        for (int group = 0; group < 10; group++)
        {
            for (int stat = 0; stat < 205; stat++) writer.Write(0f);
            for (int dictionary = 0; dictionary < 3; dictionary++)
            {
                bool populated = group == 0 && dictionary == 0;
                writer.Write(populated ? 1 : 0);
                if (populated) { writer.Write("known-world"); writer.Write(3.5f); }
            }
            writer.Write(5); // enemy-stat groups
            for (int dictionary = 0; dictionary < 10; dictionary++)
            {
                bool populated = group == 0 && dictionary == 0;
                writer.Write(populated ? 1 : 0);
                if (populated) { writer.Write("enemy"); writer.Write(2f); }
            }
        }
        writer.Write(firstSpawn);
        writer.Write(includeWorlds ? 2 : 0); // per-world records
        int first = includeWorlds ? World(writer, 100, mapBytes) : -1;
        int second = includeWorlds ? World(writer, secondUid, 0) : -1;
        writer.Write("Test character"); writer.Write(playerId); writer.Write("seed");
        writer.Write(false); // cheats
        writer.Write(1_700_000_000L); // creation date
        writer.Write(true); // player data present
        writer.Write(3); writer.Write(new byte[] { 7, 8, 9 });
        return new(Envelope(stream.ToArray()), first, second);
    }

    private static int World(BinaryWriter writer, long uid, int mapBytes)
    {
        writer.Write(uid);
        writer.Write(false); // custom spawn
        Vector(writer, 0, 0, 0);
        int flag = checked((int)writer.BaseStream.Position);
        writer.Write(false); // logout point not yet set
        Vector(writer, 0, 0, 0);
        writer.Write(false); // death point
        Vector(writer, 0, 0, 0);
        Vector(writer, 0, 0, 0); // home
        writer.Write(mapBytes != 0);
        if (mapBytes != 0)
        {
            writer.Write(mapBytes);
            writer.Write(new byte[mapBytes]);
        }
        return flag;
    }

    private static void Vector(BinaryWriter writer, float x, float y, float z)
    {
        writer.Write(x); writer.Write(y); writer.Write(z);
    }

    private static byte[] Envelope(byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(payload.Length);
        writer.Write(payload);
        byte[] hash = SHA512.HashData(payload);
        writer.Write(hash.Length);
        writer.Write(hash);
        return stream.ToArray();
    }

    private static byte[] Payload(byte[] file)
    {
        using var stream = new MemoryStream(file);
        using var reader = new BinaryReader(stream);
        return reader.ReadBytes(reader.ReadInt32());
    }
}
