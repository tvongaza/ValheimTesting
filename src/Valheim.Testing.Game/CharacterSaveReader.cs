using System.Security.Cryptography;
using System.Buffers.Binary;

namespace Valheim.Testing.Game;

// The game-side PlayerProfile format is not a public contract. Keep this byte-level reader internal and version-gated:
// it reads a disposable character's identity and whether it has spawned; it never edits a save (#298 removed that).
internal static class CharacterSaveReader
{
    private const int SupportedVersion = 46; // Valheim 1.0.16
    private const int StatCount = 205;
    private const int StatGroups = 10;
    private const int EnemyStatGroups = 5;
    private const int MaxPayloadBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 100_000;

    private static byte[] ReadEnvelope(byte[] file)
    {
        using var stream = new MemoryStream(file, writable: false);
        using var reader = new BinaryReader(stream);
        if (file.Length < 4 + 4 + 64) throw new InvalidDataException("Character save is too short.");
        int length = reader.ReadInt32();
        if (length < 4 || length > MaxPayloadBytes || length > stream.Length - stream.Position - 4 - 64)
            throw new InvalidDataException("Character payload length is invalid.");
        byte[] payload = reader.ReadBytes(length);
        if (reader.ReadInt32() != 64 || stream.Length - stream.Position != 64)
            throw new InvalidDataException("Character hash length or trailing data is invalid.");
        byte[] savedHash = reader.ReadBytes(64);
        if (!CryptographicOperations.FixedTimeEquals(savedHash, SHA512.HashData(payload)))
            throw new InvalidDataException("Character payload hash does not match.");
        return payload;
    }

    /// <summary>The save's player name and player ID. The ID is the game's stable identity for a character; it survives saves.</summary>
    internal static CharacterIdentity ReadIdentity(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Walk(ReadEnvelope(file), worldUid: 0).Identity;
    }

    // The packaged smoke character should have skipped the intro but never entered a world.
    internal static bool IsFreshSmokeSeed(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var layout = Walk(ReadEnvelope(file), worldUid: 0);
        return !layout.FirstSpawn && layout.WorldCount == 0;
    }

    private sealed record Layout(bool FirstSpawn, int WorldCount, int WorldCountOffset, int WorldEndOffset,
        (int FlagOffset, int PointOffset)? World, CharacterIdentity Identity);

    private static Layout Walk(byte[] payload, long worldUid)
    {
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream);
            int version = reader.ReadInt32();
            if (version != SupportedVersion)
                throw new NotSupportedException($"Character version {version} is unsupported; expected {SupportedVersion}.");
            if (reader.ReadInt32() != StatCount || reader.ReadInt32() != StatGroups)
                throw new InvalidDataException("Character stat layout is not the expected 1.0.16 layout.");

            for (int group = 0; group < StatGroups; group++)
            {
                Skip(stream, StatCount * sizeof(float));
                SkipNamedFloats(reader);
                SkipNamedFloats(reader);
                SkipNamedFloats(reader);
                if (reader.ReadInt32() != EnemyStatGroups)
                    throw new InvalidDataException("Character enemy-stat layout changed.");
                for (int kind = 0; kind < EnemyStatGroups; kind++) SkipNamedFloats(reader);
                for (int kind = 0; kind < 5; kind++) SkipNamedFloats(reader);
            }

            bool firstSpawn = ReadFlag(reader);
            int worldCountOffset = checked((int)stream.Position);
            int worldCount = ReadCount(reader);
            (int FlagOffset, int PointOffset)? found = null;
            var seen = new HashSet<long>();
            for (int i = 0; i < worldCount; i++)
            {
                long uid = reader.ReadInt64();
                if (!seen.Add(uid)) throw new InvalidDataException("Character contains duplicate world UIDs.");
                ReadFlag(reader); // custom spawn
                Skip(stream, 12); // custom spawn point
                int flagOffset = checked((int)stream.Position);
                ReadFlag(reader); // logout point present
                int pointOffset = checked((int)stream.Position);
                Skip(stream, 12); // logout point
                ReadFlag(reader); // death point present
                Skip(stream, 12 + 12); // death and home points
                if (ReadFlag(reader)) Skip(stream, ReadBlobLength(reader, stream)); // map data
                if (worldUid != 0 && uid == worldUid) found = (flagOffset, pointOffset);
            }
            int worldEndOffset = checked((int)stream.Position);
            string name = reader.ReadString();
            long playerId = reader.ReadInt64();
            reader.ReadString(); // start seed
            ReadFlag(reader); // used cheats
            reader.ReadInt64(); // creation date
            if (ReadFlag(reader)) Skip(stream, ReadBlobLength(reader, stream)); // player data
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Character payload has unexpected trailing data.");
            return new Layout(firstSpawn, worldCount, worldCountOffset, worldEndOffset, found, new CharacterIdentity(name, playerId));
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Character payload ended before the world data was complete.", ex);
        }
    }

    private static void SkipNamedFloats(BinaryReader reader)
    {
        int count = ReadCount(reader);
        for (int i = 0; i < count; i++)
        {
            reader.ReadString();
            reader.ReadSingle();
        }
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxEntries) throw new InvalidDataException("Character entry count is invalid.");
        return count;
    }

    private static int ReadBlobLength(BinaryReader reader, Stream stream)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > stream.Length - stream.Position)
            throw new InvalidDataException("Character map data length is invalid.");
        return length;
    }

    private static bool ReadFlag(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        if (value > 1) throw new InvalidDataException("Character boolean field is invalid.");
        return value == 1;
    }

    private static void Skip(Stream stream, int bytes)
    {
        if (bytes < 0 || bytes > stream.Length - stream.Position)
            throw new EndOfStreamException();
        stream.Position += bytes;
    }
}

internal readonly record struct CharacterIdentity(string Name, long PlayerId);
