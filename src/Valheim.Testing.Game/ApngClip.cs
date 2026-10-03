using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Valheim.Testing.Game;

// A small PNG-container muxer. It does not decode pixels or accept arbitrary PNG features: Unity's RGB24
// EncodeToPNG produces IHDR, IDAT and IEND only. Refusing an unfamiliar chunk is safer than dropping colour data.
internal static class ApngClip
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    internal sealed record Frame(string Path, int ElapsedMs, string Sha256, long Bytes);

    internal static (int Width, int Height, long Bytes, string Sha256) Write(string output, IReadOnlyList<Frame> frames)
    {
        if (frames.Count is < 2 or > 60) throw new InvalidDataException("A clip needs 2-60 frames.");
        if (File.Exists(output)) throw new IOException("Refusing to replace an existing clip.");
        byte[]? header = null;
        int width = 0, height = 0;
        uint sequence = 0;
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        stream.Write(Signature);
        for (int index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            byte[] png = File.ReadAllBytes(frame.Path);
            if (png.LongLength != frame.Bytes ||
                !Convert.ToHexString(SHA256.HashData(png)).Equals(frame.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A captured frame differs from its game-side digest.");
            var chunks = ReadFrame(png);
            byte[] ihdr = chunks[0].Data;
            if (index == 0)
            {
                header = ihdr;
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(0, 4));
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4, 4));
                WriteChunk(stream, "IHDR", ihdr);
                var animation = new byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(animation.AsSpan(0, 4), (uint)frames.Count);
                BinaryPrimitives.WriteUInt32BigEndian(animation.AsSpan(4, 4), 1); // play once
                WriteChunk(stream, "acTL", animation);
            }
            else if (!ihdr.AsSpan().SequenceEqual(header)) throw new InvalidDataException("Clip frame dimensions or PNG format changed.");
            int duration = index + 1 < frames.Count ? frames[index + 1].ElapsedMs - frame.ElapsedMs :
                frame.ElapsedMs - frames[index - 1].ElapsedMs;
            if (duration is < 1 or > 5000) throw new InvalidDataException("Clip timestamps are not bounded and increasing.");
            var control = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(0, 4), sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4, 4), (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(8, 4), (uint)height);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(20, 2), (ushort)duration);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(22, 2), 1000);
            WriteChunk(stream, "fcTL", control);
            foreach (var chunk in chunks.Where(c => c.Type == "IDAT"))
            {
                if (index == 0) WriteChunk(stream, "IDAT", chunk.Data);
                else
                {
                    var data = new byte[chunk.Data.Length + 4];
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), sequence++);
                    chunk.Data.CopyTo(data, 4);
                    WriteChunk(stream, "fdAT", data);
                }
            }
        }
        WriteChunk(stream, "IEND", []);
        stream.Flush(true);
        long bytes = stream.Length;
        stream.Position = 0;
        return (width, height, bytes, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private sealed record Chunk(string Type, byte[] Data);

    private static List<Chunk> ReadFrame(byte[] png)
    {
        if (png.Length < 45 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Captured frame is not PNG.");
        var chunks = new List<Chunk>();
        int position = 8;
        while (position + 12 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position, 4));
            if (length > 2 * 1024 * 1024 || position + 12L + length > png.Length)
                throw new InvalidDataException("Captured PNG chunk is truncated or oversized.");
            string type = Encoding.ASCII.GetString(png, position + 4, 4);
            if (type is not ("IHDR" or "IDAT" or "IEND")) throw new InvalidDataException("Captured PNG has an unsupported chunk: " + type);
            var data = png.AsSpan(position + 8, (int)length).ToArray();
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + (int)length, 4));
            if (Crc(png.AsSpan(position + 4, 4 + (int)length)) != expected) throw new InvalidDataException("Captured PNG CRC is invalid.");
            chunks.Add(new Chunk(type, data));
            position += 12 + (int)length;
            if (type == "IEND") break;
        }
        if (position != png.Length || chunks.Count < 3 || chunks[0].Type != "IHDR" || chunks[0].Data.Length != 13 ||
            chunks[^1].Type != "IEND" || chunks[^1].Data.Length != 0 || chunks.Skip(1).SkipLast(1).Any(c => c.Type != "IDAT"))
            throw new InvalidDataException("Captured PNG chunk order is unsupported.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(chunks[0].Data.AsSpan(0, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(chunks[0].Data.AsSpan(4, 4));
        if (width is < 160 or > 640 || height is < 90 or > 360 || chunks[0].Data[8] != 8 || chunks[0].Data[9] != 2)
            throw new InvalidDataException("Captured frame is not bounded RGB24 at eight bits per channel.");
        return chunks;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        stream.Write(number);
        byte[] name = Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);
        byte[] crcInput = new byte[4 + data.Length];
        name.CopyTo(crcInput, 0);
        data.CopyTo(crcInput.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(crcInput));
        stream.Write(number);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
