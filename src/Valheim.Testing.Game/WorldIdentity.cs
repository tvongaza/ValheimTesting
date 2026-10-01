using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// The identity a world fixture's own metadata file states: its name, seed and UID, as the game reads them when it loads
/// the world and as <c>cli_world</c> and the session state report the UID (<c>m_uid</c>). Reading it before a run tells a
/// wrong fixture (a reused world name holding another snapshot) from a world that fails to load, without starting the game.
/// </summary>
/// <param name="File">The metadata file read, relative to the fixture directory.</param>
public sealed record WorldIdentity(string Name, string SeedName, int Seed, long Uid, int WorldVersion, string File)
{
    /// <summary>The UID as a plan pins it (<c>worlduid</c>, <see cref="HostWorldPlan.WorldUid"/>).</summary>
    public string UidText => Uid.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The identity of the one world in <paramref name="fixture"/>, in either layout <see cref="HostedWorld.NameOf"/> accepts:
    /// a chunked save's <c>&lt;name&gt;/_main.&lt;n&gt;.fwl2</c> (every such file must state the same world; the highest
    /// <c>n</c> is reported) or the older <c>&lt;name&gt;.fwl</c>. Both hold a length-prefixed package that starts with the
    /// world version, the name, the seed name, the seed and the UID (Valheim 1.0.16 writes version 41; 37 was read the same
    /// way). Refuses a file it cannot read as that, rather than guess.
    /// </summary>
    public static WorldIdentity Read(string fixture)
    {
        fixture = Path.GetFullPath(fixture);
        if (!Directory.Exists(fixture)) throw new DirectoryNotFoundException("The fixture world does not exist: " + fixture);
        var relative = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(fixture, path)).ToList();
        string name = HostedWorld.NameOf(relative);
        var metadata = relative.Select(path => (Path: path, Match: Regex.Match(path.Replace('\\', '/'), @"^" + Regex.Escape(name) + @"/_main\.(\d+)\.fwl2$", RegexOptions.CultureInvariant)))
            .Where(file => file.Match.Success).OrderBy(file => long.Parse(file.Match.Groups[1].Value, CultureInfo.InvariantCulture)).Select(file => file.Path).ToList();
        if (metadata.Count == 0) metadata.Add(name + ".fwl");
        var read = metadata.Select(path => Parse(System.IO.File.ReadAllBytes(Path.Combine(fixture, path)), path)).ToList();
        var others = read.Where(identity => identity.Uid != read[^1].Uid || identity.Name != read[^1].Name).ToList();
        if (others.Count != 0)
            throw new InvalidDataException($"The fixture at {fixture} holds saves of different worlds: {Describe(read[^1])} and {string.Join(", ", others.Select(Describe))}.");
        return read[^1];
    }

    /// <summary>The identity in one metadata file's bytes; <paramref name="file"/> names it in messages and the result.</summary>
    public static WorldIdentity Parse(byte[] bytes, string file)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (length < 0 || length > bytes.Length - 4) throw new InvalidDataException("its length prefix does not fit the file");
            var package = bytes.AsSpan(4, length);
            int at = 0;
            int version = BinaryPrimitives.ReadInt32LittleEndian(package[at..]); at += 4;
            if (version is < 1 or > 1000) throw new InvalidDataException($"world version {version} is not one the game writes");
            string name = ReadString(package, ref at), seedName = ReadString(package, ref at);
            int seed = BinaryPrimitives.ReadInt32LittleEndian(package[at..]); at += 4;
            long uid = BinaryPrimitives.ReadInt64LittleEndian(package[at..]);
            if (name.Length == 0) throw new InvalidDataException("it names no world");
            return new WorldIdentity(name, seedName, seed, uid, version, file);
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException or DecoderFallbackException or InvalidDataException)
        {
            throw new InvalidDataException($"Cannot read the world's identity from {file}: {(error is InvalidDataException ? error.Message : "it ends early or is not world metadata")}. " +
                "A fixture holds the world as the game saved it: a chunked <name>/_main.<n>.fwl2 save or <name>.fwl with its .db.", error);
        }
    }

    // A .NET BinaryWriter string, as the game's package writes one: a 7-bit encoded byte length, then UTF-8.
    private static string ReadString(ReadOnlySpan<byte> package, ref int at)
    {
        int length = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (shift > 28) throw new InvalidDataException("a string length is malformed");
            byte part = package[at++];
            length |= (part & 0x7F) << shift;
            if (part < 0x80) break;
        }
        if (length < 0 || length > package.Length - at) throw new InvalidDataException("a string runs past the end");
        string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(package.Slice(at, length));
        at += length;
        return text;
    }

    private static string Describe(WorldIdentity identity) => $"{identity.Name} (UID {identity.UidText}, {identity.File})";
}
