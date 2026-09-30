using System.Security.Cryptography;
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

    private sealed record Fixture(byte[] File, int World100Flag, int World200Flag);

    // A hand-built 1.0.16 profile payload. No game save or decompiled source is checked in.
    private static Fixture Profile(int mapBytes = 0, long secondUid = 200)
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
        writer.Write(false); // first spawn
        writer.Write(2); // per-world records
        int first = World(writer, 100, mapBytes);
        int second = World(writer, secondUid, 0);
        writer.Write("Test character"); writer.Write(5678L); writer.Write("seed");
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
