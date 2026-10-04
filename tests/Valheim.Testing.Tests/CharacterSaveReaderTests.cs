using System.Security.Cryptography;
using System.Diagnostics;
using Valheim.Testing.Game;
using Xunit;

public class CharacterSaveReaderTests
{
    [Fact]
    public void TheIdentityAndWhetherTheCharacterHasSpawnedAreRead()
    {
        Assert.Equal(new CharacterIdentity("Test character", 5678), CharacterSaveReader.ReadIdentity(Profile().File));
        Assert.Equal(4321, CharacterSaveReader.ReadIdentity(Profile(playerId: 4321).File).PlayerId);
        Assert.True(CharacterSaveReader.IsFreshSmokeSeed(Profile(includeWorlds: false).File));
        Assert.False(CharacterSaveReader.IsFreshSmokeSeed(Profile().File)); // it has entered worlds
    }

    [Fact]
    public void UnfamiliarVersionOrStatLayoutIsRefused()
    {
        var original = Profile();
        var version = Payload(original.File);
        BitConverter.GetBytes(47).CopyTo(version, 0);
        Assert.Throws<NotSupportedException>(() => CharacterSaveReader.ReadIdentity(Envelope(version)));
        var layout = Payload(original.File);
        BitConverter.GetBytes(204).CopyTo(layout, 4);
        Assert.Throws<InvalidDataException>(() => CharacterSaveReader.ReadIdentity(Envelope(layout)));
    }

    [Fact]
    public void DamagedHashAndTruncatedSaveAreRefused()
    {
        byte[] file = Profile().File;
        byte[] damaged = (byte[])file.Clone();
        damaged[20] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => CharacterSaveReader.ReadIdentity(damaged));
        Assert.Throws<InvalidDataException>(() => CharacterSaveReader.ReadIdentity(file[..^1]));
        byte[] extra = [.. file, 0];
        Assert.Throws<InvalidDataException>(() => CharacterSaveReader.ReadIdentity(extra));
        byte[] extraPayload = [.. Payload(file), 0];
        Assert.Throws<InvalidDataException>(() => CharacterSaveReader.ReadIdentity(Envelope(extraPayload)));
    }

    [Fact]
    public void CloudSourcesAreNotRegisteredAndLiveCharacterFoldersAreRefused()
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
            Assert.Throws<ArgumentException>(() => DisposableCharacterStore.RejectLinkedAncestors(local));
            Assert.Throws<ArgumentException>(() => DisposableCharacterStore.RejectLinkedAncestors(cloud));
            DisposableCharacterStore.RejectLinkedAncestors(local, allowLiveCharacterLeaf: true); // the one live folder a stage writes into
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
            Assert.Throws<ArgumentException>(() => DisposableCharacterStore.RejectLinkedAncestors(link));
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
    public void StageCopiesOnlyARegisteredCharacterAndRemovesOnlyItsFreshCharacterFiles()
    {
        string root = Directory.CreateTempSubdirectory("vt-character-stage-").FullName;
        string local = Path.Combine(root, "characters_local"), steam = Path.Combine(root, "userdata");
        Directory.CreateDirectory(local); Directory.CreateDirectory(steam);
        try
        {
            string original = Path.Combine(local, "seed.fch");
            File.WriteAllBytes(original, Profile().File);
            Register(root, original);
            string store = Path.Combine(root, "store");
            using (var stage = RegisteredCharacterStage.InstallRegistered(store, "tester", local, steam, "fresh"))
            {
                Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(Path.Combine(local, "fresh.fch")));
                File.WriteAllText(Path.Combine(local, "fresh.fch.old"), "game backup");
                File.WriteAllText(Path.Combine(local, "fresh_backup_auto-1.fch"), "game backup");
            }
            Assert.True(File.Exists(original));
            Assert.False(File.Exists(Path.Combine(local, "fresh.fch")));
            Assert.False(File.Exists(Path.Combine(local, "fresh.fch.old")));
            Assert.False(File.Exists(Path.Combine(local, "fresh_backup_auto-1.fch")));
            Assert.Throws<KeyNotFoundException>(() => RegisteredCharacterStage.InstallRegistered(store, "nobody", local, steam, "fresh"));
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
            string original = Path.Combine(local, "seed.fch");
            File.WriteAllBytes(original, Profile().File);
            Register(root, original);
            foreach (var collision in new[] { Path.Combine(local, "FRESH.fch.old"),
                Path.Combine(cloud, "fresh.fch"), Path.Combine(remote, "fresh.fch") })
            {
                File.WriteAllText(collision, "existing");
                Assert.Throws<IOException>(() => RegisteredCharacterStage.InstallRegistered(Path.Combine(root, "store"), "tester", local, steam, "fresh"));
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
