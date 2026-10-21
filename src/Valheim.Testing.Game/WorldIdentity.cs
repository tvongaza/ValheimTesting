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
    /// The one owner of a world fixture's structure, completeness and identity: the identity of the one world in
    /// <paramref name="fixture"/>, refusing a fixture the game would not load as it is, before anything is copied.
    /// The fixture holds one Valheim 1.0 world folder <c>&lt;name&gt;/</c> exactly as the game saved it (the layout rules are
    /// <see cref="HostedWorld.NameOf"/>'s), and the files are checked as the game (1.0.17) reads them:
    /// <list type="bullet">
    /// <item>one save set <c>_main.&lt;n&gt;.fwl2</c>, <c>.db2</c>, <c>.chunks</c> and <c>.ok</c>; or a world never saved, a lone
    /// <c>_main.0.fwl2</c> whose metadata says it has no data yet (without its <c>.db2</c> the game silently makes a fresh world);</item>
    /// <item>every chunk file <c>_main.&lt;n&gt;.chunks</c> lists, and no other (the game fails on a missing one and deletes an unlisted one);</item>
    /// <item>optional backups the game made beside the world (<c>&lt;name&gt;_backup_auto-&lt;yyyyMMdd-HHmmss&gt;/</c> and every other
    /// kind the game names a backup, <see cref="BackupOf"/>), checked only for the same world name and UID.</item>
    /// </list>
    /// A pre-1.0 world (<c>&lt;name&gt;.fwl</c> and <c>&lt;name&gt;.db</c>) is refused: open and save it once in Valheim 1.0 or later.
    /// The metadata holds a length-prefixed package that starts with the world version, the name, the seed name, the seed and
    /// the UID. Throws <see cref="ArgumentException"/> for a layout problem and <see cref="InvalidDataException"/> for a file
    /// that does not hold what the game needs; each names the file and what to do.
    /// </summary>
    public static WorldIdentity Read(string fixture)
    {
        fixture = Path.GetFullPath(fixture);
        if (!Directory.Exists(fixture)) throw new DirectoryNotFoundException("The fixture world does not exist: " + fixture);
        // A copy's own record (WorldFixture.Copy) is not part of the world; a pinned manifest never lists it (NameOf refuses it there).
        var relative = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(fixture, path))
            .Where(path => path != WorldFixture.ProvenanceFile).ToList();
        var layout = Layout(relative);
        byte[] Bytes(string file) => System.IO.File.ReadAllBytes(Path.Combine(fixture, file));
        var identity = ParseMetadata(Bytes(layout.Metadata), layout.Metadata, out bool? needsDb);
        if (layout.Lone && needsDb != false)
            throw new InvalidDataException($"The fixture's world {layout.Name} is incomplete: {Slash(layout.Metadata)} does not say the world is new (never saved), but " +
                $"{layout.Name}/_main.0.db2, .chunks and .ok are missing, so the game would generate the world afresh with another UID. " +
                "Copy the world folder again, exactly as the game saved it, after the game has stopped.");
        if (layout.ChunkList is { } chunkList)
        {
            var listed = ChunkFiles(Bytes(chunkList), chunkList);
            var present = layout.Chunks.Select(path => Path.GetFileName(path.Replace('\\', '/'))).ToHashSet(StringComparer.Ordinal);
            var missing = listed.Where(name => !present.Contains(name)).Order(StringComparer.Ordinal).ToList();
            if (missing.Count != 0)
                throw new InvalidDataException($"The fixture's world {layout.Name} is incomplete: {Slash(chunkList)} lists {Few(missing)}, which " +
                    $"{(missing.Count == 1 ? "is" : "are")} not in {layout.Name}/; the game fails to load a world with a missing chunk. Copy the world folder again, exactly as the game saved it, after the game has stopped.");
            var unlisted = present.Where(name => !listed.Contains(name)).Order(StringComparer.Ordinal).ToList();
            if (unlisted.Count != 0)
                throw new InvalidDataException($"The fixture's world {layout.Name} holds {Few(unlisted)}, which {Slash(chunkList)} does not list: the game deletes " +
                    "unlisted chunk files when it loads the world, so the run would not load the pinned files. Copy the world folder again after the game has stopped, or remove those files.");
        }
        foreach (string backup in layout.BackupMetadata)
        {
            var other = Parse(Bytes(backup), backup);
            if (other.Uid != identity.Uid || other.Name != identity.Name)
                throw new InvalidDataException($"The fixture holds saves of different worlds: {Describe(identity)} and {Describe(other)}. " +
                    $"A {layout.Name}_backup_ folder holds the game's backup of the same world; remove the other world's copy.");
        }
        return identity;
    }

    /// <summary>The identity in one metadata file's bytes; <paramref name="file"/> names it in messages and the result.</summary>
    public static WorldIdentity Parse(byte[] bytes, string file) => ParseMetadata(bytes, file, out _);

    // The world's files by name alone: what Read checks, and what a pinned manifest (HostedWorld.NameOf) must already hold.
    internal sealed record WorldFiles(string Name, string Metadata, bool Lone, string? ChunkList, IReadOnlyList<string> Chunks, IReadOnlyList<string> BackupMetadata);

    private static readonly Regex SaveFile = new(@"^_main\.(0|[1-9][0-9]*)\.(fwl2|db2|chunks|ok)$", RegexOptions.CultureInvariant);
    private static readonly Regex Timestamp = new(@".+(\d{4})-*(\d{2})-*(\d{2})-*(\d{2})-*(\d{2})-*(\d{2})");

    /// <summary>
    /// The save a folder is a backup of, as the game classifies a save's name (SaveSystem.UpdateSaveNameAndReturnSaveFileType with
    /// GetTimestampFromPath, SaveSystem.cs:1100 in 1.0.17): a name holding a valid timestamp whose second-to-last <c>_</c> starts
    /// <c>_backup_</c> is a backup (automatic <c>_backup_auto-</c>, <c>_backup_cloud-</c>, <c>_backup_restore-</c> or a standard
    /// <c>_backup_</c>) of the save named before it. Null for any other name.
    /// </summary>
    internal static string? BackupOf(string folder)
    {
        var stamp = Timestamp.Match(folder);
        if (!stamp.Success) return null;
        try
        {
            var parts = stamp.Groups.Values.Skip(1).Select(group => int.Parse(group.Value, CultureInfo.InvariantCulture)).ToArray();
            _ = new DateTime(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or FormatException or OverflowException) { return null; } // the game reads no timestamp, so no backup
        int last = folder.LastIndexOf('_');
        int second = last > 0 ? folder.LastIndexOf('_', last - 1) : -1;
        return second > 0 && string.CompareOrdinal(folder, second, "_backup_", 0, "_backup_".Length) == 0 ? folder[..second] : null;
    }
    private static readonly string[] SetParts = ["fwl2", "db2", "chunks", "ok"];

    /// <summary>
    /// The layout rules of a world fixture, from its relative file paths: exactly one world folder <c>&lt;name&gt;/</c> at the root,
    /// holding one complete save set and its chunk files and nothing else; optionally the game's backup folders of that world;
    /// nothing else at the root. Refuses with an <see cref="ArgumentException"/> that names what is wrong and what to do.
    /// </summary>
    internal static WorldFiles Layout(IEnumerable<string> relativePaths)
    {
        var paths = relativePaths.Select(path => path.Replace('\\', '/')).ToList();
        var flat = paths.Where(path => !path.Contains('/') && Regex.IsMatch(path, @"\.(fwl|db)(\.old)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)).ToList();
        if (flat.Count != 0)
        {
            string stem = Regex.Replace(flat[0], @"\.(fwl|db)(\.old)?$", "", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            throw new ArgumentException($"The fixture holds {string.Join(" and ", flat.Take(2))}: a world saved before Valheim 1.0, as the flat <name>.fwl and <name>.db pair. " +
                $"The toolkit loads only Valheim 1.0's chunked saves. Open {stem} once in Valheim 1.0 or later and save it: the game then writes {stem}/ " +
                $"(_main.<n>.fwl2, .db2, .chunks, .ok and its chunk files). Make the fixture of that {stem}/ folder alone.");
        }
        var loose = paths.Where(path => !path.Contains('/')).ToList();
        if (loose.Count != 0)
            throw new ArgumentException($"The fixture holds {Few(loose)} beside the world folder; a fixture holds only one world folder <name>/ (and the game's <name>_backup_…<date> copies of it). Remove {(loose.Count == 1 ? "it" : "them")}.");
        var folders = paths.Select(path => path[..path.IndexOf('/')]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var worlds = folders.Where(folder => BackupOf(folder) == null).ToList();
        if (worlds.Count != 1)
            throw new ArgumentException(worlds.Count == 0
                ? $"The fixture holds no world folder{(folders.Count == 0 ? "" : " (only backups: " + Few(folders) + ")")}. A fixture holds one Valheim 1.0 world folder <name>/ with its _main.<n>.fwl2."
                : $"A fixture holds exactly one world, not {worlds.Count}: {Few(worlds.Select(world => world + "/").ToList())}. Copy only the world under test into a new empty directory.");
        string name = RequireName(worlds[0]);
        foreach (string folder in folders.Where(folder => folder != name))
            if (BackupOf(folder) != name)
                throw new ArgumentException($"The fixture holds {folder}/, a backup of another world than {name}; keep only {name}/ and the game's own {name}_backup_…<date> copies of it.");

        var files = paths.Where(path => path.StartsWith(name + "/", StringComparison.Ordinal)).Select(path => path[(name.Length + 1)..]).ToList();
        var stray = files.Where(file => file.Contains('/') || !(SaveFile.IsMatch(file) || file.EndsWith(".chunk", StringComparison.Ordinal))).ToList();
        if (stray.Count != 0)
            throw new ArgumentException($"The fixture's world {name}/ holds {Few(stray.Select(file => name + "/" + file).ToList())}, which the game does not write: a world folder holds " +
                "_main.<n>.fwl2, .db2, .chunks and .ok and its chunk files. Copy the world folder again, exactly as the game saved it.");
        var sets = files.Select(file => SaveFile.Match(file)).Where(match => match.Success)
            .GroupBy(match => match.Groups[1].Value, StringComparer.Ordinal).OrderBy(set => set.Key.Length).ThenBy(set => set.Key, StringComparer.Ordinal).ToList();
        if (sets.Count != 1)
            throw new ArgumentException(sets.Count == 0
                ? $"The fixture's world {name}/ holds no save (no _main.<n>.fwl2). Copy the world folder again, exactly as the game saved it, after the game has stopped."
                : $"The fixture's world {name}/ holds saves {string.Join(", ", sets.Select(set => set.Key))}: the game keeps only the newest complete save " +
                    "and deletes the others when it loads the world, so the run would not load the pinned files. Copy the world folder again after the game has stopped (a save leaves one set).");
        string n = sets[0].Key;
        var parts = sets[0].Select(match => match.Groups[2].Value).ToHashSet(StringComparer.Ordinal);
        bool lone = n == "0" && parts.SetEquals(["fwl2"]);
        if (!lone && !parts.SetEquals(SetParts))
        {
            var missing = SetParts.Where(part => !parts.Contains(part)).Select(part => $"{name}/_main.{n}.{part}").ToList();
            throw new ArgumentException($"The fixture's world {name} is incomplete: it has no {string.Join(", ", missing)}. A save is the four files " +
                $"_main.{n}.fwl2, .db2, .chunks and .ok; without them the game fails to load the world or generates it afresh. " +
                "Copy the world folder again, exactly as the game saved it, after the game has stopped.");
        }
        var chunks = files.Where(file => file.EndsWith(".chunk", StringComparison.Ordinal)).Select(file => name + "/" + file).ToList();
        if (lone && chunks.Count != 0)
            throw new ArgumentException($"The fixture's world {name} is incomplete: it holds chunk files ({Few(chunks)}) but no {name}/_main.0.chunks listing them. Copy the world folder again, exactly as the game saved it.");

        var backups = new List<string>();
        foreach (string folder in folders.Where(folder => folder != name))
        {
            var inside = paths.Where(path => path.StartsWith(folder + "/", StringComparison.Ordinal)).Select(path => path[(folder.Length + 1)..]).ToList();
            var odd = inside.Where(file => file.Contains('/') || !(SaveFile.IsMatch(file) || file.EndsWith(".chunk", StringComparison.Ordinal))).ToList();
            if (odd.Count != 0)
                throw new ArgumentException($"The fixture's backup {folder}/ holds {Few(odd.Select(file => folder + "/" + file).ToList())}, which the game does not write. Copy the backup again as the game made it, or leave it out.");
            var metadata = inside.Where(file => file.EndsWith(".fwl2", StringComparison.Ordinal)).Select(file => folder + "/" + file).ToList();
            if (metadata.Count == 0)
                throw new ArgumentException($"The fixture's backup {folder}/ holds no _main.<n>.fwl2, so it cannot be shown to be {name}'s. Copy the backup again as the game made it, or leave it out.");
            backups.AddRange(metadata);
        }
        return new WorldFiles(name, $"{name}/_main.{n}.fwl2", lone, lone ? null : $"{name}/_main.{n}.chunks", chunks, backups);
    }

    /// <summary>A fixture world's name: at least 3 characters, one token and a valid file name on every platform.</summary>
    internal static string RequireName(string name)
    {
        if (name.Length < 3 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsWhiteSpace))
            throw new ArgumentException($"The fixture world's name \"{name}\" must be at least 3 characters, one token and a valid file name on every platform.");
        return name;
    }

    // The chunk files a _main.<n>.chunks lists, named as the game names them (ChunkSaveMapping, 1.0.17): a short version, the
    // ZDO count and the chunk count, then per chunk its index (ushort), size (byte), version (uint) and ZDO count (int).
    private static HashSet<string> ChunkFiles(byte[] bytes, string file)
    {
        try
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(6));
            if (count < 0 || count > (bytes.Length - 10) / 11) throw new InvalidDataException("its chunk count does not fit the file");
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0, at = 10; i < count; i++, at += 11)
            {
                ushort chunk = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
                uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 3));
                names.Add(string.Create(CultureInfo.InvariantCulture, $"{chunk >> 8:x2}_{chunk & 0xFF:x2}__{bytes[at + 2]}_{version}.chunk"));
            }
            return names;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidDataException($"Cannot read the chunk list {Slash(file)}: it ends early. Copy the world folder again, exactly as the game saved it.");
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException($"Cannot read the chunk list {Slash(file)}: {error.Message}. Copy the world folder again, exactly as the game saved it.", error);
        }
    }

    private static WorldIdentity ParseMetadata(byte[] bytes, string file, out bool? needsDb)
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
            long uid = BinaryPrimitives.ReadInt64LittleEndian(package[at..]); at += 8;
            if (name.Length == 0) throw new InvalidDataException("it names no world");
            // World.LoadWorld: the world-generation version from 26, then whether the world has saved data from 30 (null when
            // the package ends before it: only a never-saved world is read by it).
            if (version >= 26) at += 4;
            needsDb = version < 30 ? false : at < package.Length ? package[at] != 0 : null;
            return new WorldIdentity(name, seedName, seed, uid, version, file);
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException or DecoderFallbackException or InvalidDataException)
        {
            throw new InvalidDataException($"Cannot read the world's identity from {file}: {(error is InvalidDataException ? error.Message : "it ends early or is not world metadata")}. " +
                "A fixture holds the world as Valheim 1.0 saved it: a <name>/ folder with _main.<n>.fwl2, .db2, .chunks, .ok and its chunk files.", error);
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

    private static string Slash(string path) => path.Replace('\\', '/');
    private static string Few(IReadOnlyList<string> items) =>
        string.Join(", ", items.Take(5)) + (items.Count > 5 ? $" and {items.Count - 5} more" : "");
    private static string Describe(WorldIdentity identity) => $"{identity.Name} (UID {identity.UidText}, {Slash(identity.File)})";
}
