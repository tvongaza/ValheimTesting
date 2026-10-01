using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// The registered disposable test characters: a directory outside every game character folder holding one copy of each
/// character and a manifest of their names and the game's player IDs. Registering is the one explicit step that declares
/// a character disposable; <see cref="CharacterStartCopy.Prepare"/> and character-start staging accept only characters
/// registered here, so pointing a test at a personal character file is refused rather than edited.
/// </summary>
/// <remarks>
/// The player ID is the save's own identity and survives the game re-saving the character, so <see cref="Refresh"/> can
/// take a registered character's newer save (after it visited a world) but never swaps in a different character.
/// A registration is not a security boundary against someone deliberately editing the manifest; it stops a run, an
/// example or a plan from using a character nobody registered.
/// </remarks>
/// <example>
/// Create the test character in Valheim, complete its first spawn, visit the fixture world, then save it locally.
/// Register that <c>characters_local</c> file once; on a later run, open the store and get a fresh handle:
/// <code>
/// var store = DisposableCharacterStore.Create(storeDirectory);
/// store.Register("tester", localCharacterFile);
///
/// store = DisposableCharacterStore.Open(storeDirectory);
/// var character = store.Get("tester");
/// </code>
/// After the game saves this same character again, use <see cref="Refresh"/> and then take a new handle with
/// <see cref="Get"/>. The store must be outside the game's character folders.
/// Pass <c>character</c> to <see cref="CharacterStartCopy.Prepare"/>. Copies keep the same player ID; they are not
/// independent characters for simultaneous players.
/// </example>
public sealed class DisposableCharacterStore
{
    public const string ManifestFile = "disposable-characters.json";
    private const int Format = 1;
    private static readonly Regex Name = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The store's full path.</summary>
    public string Root { get; }

    private DisposableCharacterStore(string root) => Root = root;

    /// <summary>Creates an empty store in a new or empty directory outside the game's character folders.</summary>
    public static DisposableCharacterStore Create(string directory)
    {
        directory = CheckLocation(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("A new character store needs a new or empty directory.");
        Directory.CreateDirectory(directory);
        var store = new DisposableCharacterStore(directory);
        store.WriteManifest(new Manifest { Format = Format });
        return store;
    }

    /// <summary>Opens an existing store; a directory without the store's manifest is refused.</summary>
    public static DisposableCharacterStore Open(string directory)
    {
        directory = CheckLocation(directory);
        var store = new DisposableCharacterStore(directory);
        store.ReadManifest();
        return store;
    }

    /// <summary>The registered names.</summary>
    public IReadOnlyList<string> Names => ReadManifest().Characters.Select(entry => entry.Name).ToList();

    /// <summary>
    /// Declares a local character disposable: copies its save into the store under <paramref name="name"/> and records its
    /// player ID. The source must be a <c>.fch</c> in <c>characters_local</c> (never a Steam Cloud folder) and is not changed.
    /// Register only characters created for tests; the game still owns and may rewrite the local original.
    /// </summary>
    public DisposableCharacter Register(string name, string localCharacterFile)
    {
        CheckName(name);
        byte[] bytes = ReadLocalCharacter(localCharacterFile);
        CharacterIdentity identity = CharacterSavePosition.ReadIdentity(bytes);
        Manifest manifest = ReadManifest();
        if (manifest.Characters.Any(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"The store already registers {name}; use Refresh to take a newer save of it.");
        if (manifest.Characters.FirstOrDefault(entry => entry.PlayerId == identity.PlayerId) is { } other)
            throw new IOException($"This character is already registered as {other.Name}.");
        string file = StoredFile(name);
        using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            try
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            catch
            {
                output.Dispose();
                File.Delete(file);
                throw;
            }
        }
        try
        {
            manifest.Characters.Add(new Entry { Name = name, PlayerId = identity.PlayerId });
            WriteManifest(manifest);
        }
        catch
        {
            File.Delete(file);
            throw;
        }
        return Get(name);
    }

    /// <summary>
    /// Replaces a registered character's stored copy with a newer local save of the same character, for example after it
    /// has visited a fixture world. A save with a different player ID is refused and the stored copy is kept.
    /// </summary>
    public DisposableCharacter Refresh(string name, string localCharacterFile)
    {
        Entry entry = Find(ReadManifest(), name);
        byte[] bytes = ReadLocalCharacter(localCharacterFile);
        if (CharacterSavePosition.ReadIdentity(bytes).PlayerId != entry.PlayerId)
            throw new InvalidDataException($"That save is a different character from the registered {entry.Name}; the stored copy is unchanged.");
        string file = StoredFile(entry.Name), temporary = file + ".new";
        File.Delete(temporary);
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, file, overwrite: true);
        return Get(entry.Name);
    }

    /// <summary>A registered character, verified now: listed in the manifest, stored as a plain file, and still the same player.</summary>
    public DisposableCharacter Get(string name)
    {
        Entry entry = Find(ReadManifest(), name);
        byte[] bytes = ReadStored(entry);
        return new DisposableCharacter(this, entry.Name, entry.PlayerId, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>
    /// Whether a save belongs to a registered character: its player ID is in the manifest and that entry's stored copy is
    /// a plain file of the same player. A listed entry whose copy is missing, linked or another character throws, as
    /// <see cref="Get"/> does; so does any save format error.
    /// </summary>
    public bool Registers(byte[] characterFile)
    {
        long playerId = CharacterSavePosition.ReadIdentity(characterFile).PlayerId;
        if (ReadManifest().Characters.FirstOrDefault(entry => entry.PlayerId == playerId) is not { } entry) return false;
        ReadStored(entry);
        return true;
    }

    // The bytes a handle stands for, re-verified at use: the registration still exists and the stored copy is unchanged.
    internal byte[] Read(DisposableCharacter character)
    {
        if (!ReferenceEquals(character.Store, this) && !character.Store.Root.Equals(Root, PathComparison))
            throw new ArgumentException("The character belongs to another store.");
        Entry entry = Find(ReadManifest(), character.Name);
        if (entry.PlayerId != character.PlayerId)
            throw new InvalidDataException($"{character.Name} was re-registered as a different character; get it from the store again.");
        byte[] bytes = ReadStored(entry);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(character.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{character.Name}'s stored copy changed since it was taken from the store; get it again.");
        return bytes;
    }

    private byte[] ReadStored(Entry entry)
    {
        string file = StoredFile(entry.Name);
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException($"The store lists {entry.Name} but has no {Path.GetFileName(file)}.");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{entry.Name}'s stored copy is a link; the store keeps plain copies only.");
        byte[] bytes = File.ReadAllBytes(file);
        if (CharacterSavePosition.ReadIdentity(bytes).PlayerId != entry.PlayerId)
            throw new InvalidDataException($"{entry.Name}'s stored copy is a different character from the one registered.");
        return bytes;
    }

    private static byte[] ReadLocalCharacter(string file)
    {
        if (!Path.IsPathFullyQualified(file)) throw new ArgumentException("The character must be a full path.");
        file = Path.GetFullPath(file);
        if (!string.Equals(Path.GetExtension(file), ".fch", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The character must be a .fch file.");
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "characters_local", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Register a characters_local character, never one from a cloud characters folder.");
        CharacterStartCopy.RejectLinkedAncestors(Path.GetDirectoryName(file)!, allowLiveCharacterLeaf: true);
        return File.ReadAllBytes(file);
    }

    private static string CheckLocation(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("The character store must be a full path.");
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        CharacterStartCopy.RejectLinkedAncestors(directory);
        if (new DirectoryInfo(directory) is { Exists: true } info && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("The character store may not itself be a link.");
        return directory;
    }

    private static void CheckName(string name)
    {
        if (name is null || !Name.IsMatch(name))
            throw new ArgumentException("A registered name is 1 to 64 letters, digits, '_' or '-', starting with a letter or digit.");
    }

    private static Entry Find(Manifest manifest, string name)
    {
        CheckName(name);
        return manifest.Characters.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"{name} is not a registered disposable character.");
    }

    private string StoredFile(string name) => Path.Combine(Root, name + ".fch");

    private Manifest ReadManifest()
    {
        string path = Path.Combine(Root, ManifestFile);
        if (!File.Exists(path)) throw new FileNotFoundException($"{Root} is not a disposable character store (no {ManifestFile}).");
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{ManifestFile} is empty.");
        if (manifest.Format != Format) throw new NotSupportedException($"{ManifestFile} format {manifest.Format} is unsupported; expected {Format}.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var players = new HashSet<long>();
        foreach (Entry entry in manifest.Characters)
        {
            CheckName(entry.Name);
            if (!names.Add(entry.Name) || !players.Add(entry.PlayerId))
                throw new InvalidDataException($"{ManifestFile} registers {entry.Name} or its player ID twice.");
        }
        return manifest;
    }

    private void WriteManifest(Manifest manifest)
    {
        string path = Path.Combine(Root, ManifestFile), temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class Manifest
    {
        public int Format { get; set; }
        public List<Entry> Characters { get; set; } = [];
    }

    private sealed class Entry
    {
        public string Name { get; set; } = "";
        public long PlayerId { get; set; }
    }
}

/// <summary>
/// A character taken from a <see cref="DisposableCharacterStore"/>. Only the store creates these; each use re-reads the
/// store and refuses the handle if the registration was removed or the stored copy changed since it was taken.
/// </summary>
public sealed class DisposableCharacter
{
    internal DisposableCharacter(DisposableCharacterStore store, string name, long playerId, string sha256)
    {
        Store = store; Name = name; PlayerId = playerId; Sha256 = sha256;
    }

    public DisposableCharacterStore Store { get; }
    /// <summary>The registered name (the store's file name, not the in-game display name).</summary>
    public string Name { get; }
    /// <summary>The game's player ID from the save.</summary>
    public long PlayerId { get; }
    /// <summary>SHA256 of the stored copy when this handle was taken.</summary>
    public string Sha256 { get; }
}
