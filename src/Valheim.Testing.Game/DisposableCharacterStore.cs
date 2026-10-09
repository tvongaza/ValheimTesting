using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// The registered disposable test characters: a directory outside every game character folder holding one copy of each
/// character and a manifest of their names and the game's player IDs. Registering is the one explicit step that declares
/// a character disposable; staging a character for an owned client accepts only characters
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
/// Copies keep the same player ID; they are not independent characters for simultaneous players.
/// </example>
public sealed class DisposableCharacterStore
{
    /// <summary>The file name of this store's own manifest, inside <see cref="Root"/>.</summary>
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
        CharacterIdentity identity = CharacterSaveReader.ReadIdentity(bytes);
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
        if (CharacterSaveReader.ReadIdentity(bytes).PlayerId != entry.PlayerId)
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
        return new DisposableCharacter(this, entry.Name, entry.PlayerId, FileHash.Sha256(bytes));
    }

    /// <summary>
    /// Whether a save belongs to a registered character: its player ID is in the manifest and that entry's stored copy is
    /// a plain file of the same player. A listed entry whose copy is missing, linked or another character throws, as
    /// <see cref="Get"/> does; so does any save format error.
    /// </summary>
    public bool Registers(byte[] characterFile)
    {
        long playerId = CharacterSaveReader.ReadIdentity(characterFile).PlayerId;
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
        if (!FileHash.Sha256(bytes).Equals(character.Sha256, StringComparison.OrdinalIgnoreCase))
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
        if (CharacterSaveReader.ReadIdentity(bytes).PlayerId != entry.PlayerId)
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
        RejectLinkedAncestors(Path.GetDirectoryName(file)!, allowLiveCharacterLeaf: true);
        return File.ReadAllBytes(file);
    }

    private static string CheckLocation(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("The character store must be a full path.");
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        RejectLinkedAncestors(directory);
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

    internal string StoredFile(string name) => Path.Combine(Root, SaveFile(name));

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

    // ---- The files one character owns: the one rule for staging and retiring, here and on another host ----

    // What the game (1.0.16) keeps for a character file <character>.fch in a characters folder: the save, the previous save
    // it moves aside on every save (.old), the new save it writes before the rename (.new, left behind when the process
    // stops mid-save), and its automatic backups. The one table: IsCharacterFile and the host scripts both read it.
    private static string[] OwnedNames(string character) => [SaveFile(character), character + ".fch.old", character + ".fch.new"];
    private static string[] OwnedPrefixes(string character) => [character + "_backup_auto-"];
    // Besides characters_local, where the game finds a character of the same name: the sibling Steam Cloud folder and each
    // Steam account's Valheim cloud folder under userdata.
    private const string CloudFolder = "characters";
    private static readonly string[] SteamCloudFolder = ["892970", "remote", "characters"];

    /// <summary>
    /// Whether <paramref name="fileName"/>, in a characters folder, is one of the files the game keeps for the character file
    /// <paramref name="character"/> (without <c>.fch</c>): <c>&lt;character&gt;.fch</c>, <c>.fch.old</c>, <c>.fch.new</c> and
    /// the automatic backups <c>&lt;character&gt;_backup_auto-*</c>. Compared ignoring case, as Windows does. A stage refuses
    /// a character for which any folder of <see cref="CharacterFolders"/> holds one, so after its run every such file in
    /// <c>characters_local</c> is the run's own to remove. Host scripts get the same table from <see cref="HostScriptVariables"/>.
    /// </summary>
    internal static bool IsCharacterFile(string fileName, string character) =>
        OwnedNames(character).Any(name => fileName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
        OwnedPrefixes(character).Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The character's save file name, <c>&lt;character&gt;.fch</c>.</summary>
    internal static string SaveFile(string character) => character + ".fch";

    /// <summary>
    /// Every folder on this machine that can hold a character named like a staged one: <paramref name="charactersLocal"/>, its
    /// sibling Steam Cloud <c>characters</c> folder and every <c>&lt;account&gt;/892970/remote/characters</c> under
    /// <paramref name="steamUserData"/>. Folders that do not exist are included; a caller skips them.
    /// </summary>
    internal static IEnumerable<string> CharacterFolders(string charactersLocal, string steamUserData)
    {
        yield return charactersLocal;
        yield return CloudFolderBeside(charactersLocal);
        foreach (string account in Directory.EnumerateDirectories(steamUserData))
            yield return Path.Combine([account, .. SteamCloudFolder]);
    }

    /// <summary>
    /// The rule for a script on the client's host, which decides there: <c>names</c> and <c>prefixes</c> (the table, one per
    /// line; the script matches ignoring case), <c>save</c> (the save file name), <c>cloud</c> (the sibling Steam Cloud folder
    /// of <paramref name="charactersLocal"/>, a path on that host) and <c>remote</c> (each Steam account's cloud folder,
    /// relative to the account directory).
    /// </summary>
    internal static Dictionary<string, string> HostScriptVariables(string character, string charactersLocal) => new()
    {
        ["names"] = string.Join('\n', OwnedNames(character)), ["prefixes"] = string.Join('\n', OwnedPrefixes(character)),
        ["save"] = SaveFile(character), ["cloud"] = CloudFolderBeside(charactersLocal), ["remote"] = string.Join('/', SteamCloudFolder),
    };

    // The characters folder beside characters_local, as a path in the same style (Windows or POSIX), on this machine or another.
    private static string CloudFolderBeside(string charactersLocal)
    {
        string local = charactersLocal.TrimEnd('/', '\\');
        int cut = local.LastIndexOfAny(['/', '\\']);
        if (cut < 0) throw new ArgumentException($"characters_local must be a full path; '{charactersLocal}' is not.", nameof(charactersLocal));
        return HostPath.Join(cut == 0 ? local[..1] : local[..cut], CloudFolder);
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

    // A lexical parent check alone lets a junction named "evidence/link" write into characters_local. Inspect every
    // link's resolved target as well as the spelling the caller supplied. Harmless system links (for example macOS's
    // /var -> /private/var) remain usable. Windows short-name aliases can hide a live directory, so refuse those.
    internal static void RejectLinkedAncestors(string directory, bool allowLiveCharacterLeaf = false)
    {
        bool leaf = true;
        for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent, leaf = false)
        {
            if (!(allowLiveCharacterLeaf && leaf) &&
                (parent.Name.Equals("characters_local", StringComparison.OrdinalIgnoreCase) ||
                 parent.Name.Equals("characters", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Write the copy to a new evidence directory, never a live character directory.");
            if (OperatingSystem.IsWindows() && parent.Name.Contains('~'))
                throw new ArgumentException("Windows short-name aliases are not accepted in character paths.");
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (allowLiveCharacterLeaf && leaf)
                    throw new ArgumentException("The source characters_local directory may not itself be a link.");
                for (var target = parent.ResolveLinkTarget(returnFinalTarget: true); target != null; target = target is DirectoryInfo dir ? dir.Parent : null)
                    if (target.Name.Equals("characters_local", StringComparison.OrdinalIgnoreCase) ||
                        target.Name.Equals("characters", StringComparison.OrdinalIgnoreCase) ||
                        OperatingSystem.IsWindows() && target.Name.Contains('~'))
                        throw new ArgumentException("A character path resolves through a live character directory or short-name alias.");
            }
        }
    }
}

/// <summary>
/// A character taken from a <see cref="DisposableCharacterStore"/>. Only the store creates these; each use re-reads the
/// store and refuses the handle if the registration was removed or the stored copy changed since it was taken.
/// </summary>
[ResultShape]
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
