using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>
/// The one owner of what a run held at the main menu (<c>start --menu-only</c>, #1009) leaves in the client's own save folders,
/// <c>characters_local</c> and <c>worlds_local</c>: their files are listed before the launch (name, size and modified time, written to
/// <see cref="BeforeFile"/> in the evidence), and once the client is proven stopped every file the game added there (a character a
/// command made at the menu, with its backups) is moved into <see cref="AddedFolder"/> in the evidence, so the user's character and
/// world lists end as they began. A file that was there before is never moved or changed: if one changed or went missing, nothing
/// is moved and the run fails, naming it. Only the top level of the two folders is read; Steam Cloud character folders are never
/// read or touched.
/// </summary>
internal sealed class SaveFolderChanges
{
    internal const string BeforeFile = "save-folders-before.json", AddedFolder = "game-added";
    internal static readonly IReadOnlyList<string> Folders = ["characters_local", "worlds_local"];

    private sealed record Entry(string Folder, string Name, long Size, DateTime ModifiedUtc);

    private readonly string _saveRoot;
    private readonly IReadOnlyList<Entry> _before;
    private static StringComparer Names => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private SaveFolderChanges(string saveRoot, IReadOnlyList<Entry> before) { _saveRoot = saveRoot; _before = before; }

    /// <summary>
    /// Lists the save folders under <paramref name="saveRoot"/> (the client's data directory) as they are before the launch and writes
    /// the listing to <see cref="BeforeFile"/> in <paramref name="evidence"/>, so a person can tell what a killed run's game added.
    /// </summary>
    internal static SaveFolderChanges Capture(string saveRoot, string evidence)
    {
        saveRoot = Path.GetFullPath(saveRoot);
        var before = List(saveRoot);
        File.WriteAllText(Path.Combine(evidence, BeforeFile), JsonSerializer.Serialize(new
        {
            saveRoot,
            files = before.Select(entry => new { folder = entry.Folder, name = entry.Name, size = entry.Size, modifiedUtc = entry.ModifiedUtc }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return new SaveFolderChanges(saveRoot, before);
    }

    /// <summary>
    /// Moves every file added to the save folders since <see cref="Capture"/> into <c>&lt;evidence&gt;/game-added/&lt;folder&gt;/</c>
    /// and returns them as <c>folder/name</c>, in order. Call it only once the client is proven stopped. Refuses, moving nothing, when a
    /// file listed before changed or went missing: that file is the user's, and the run cannot tell what else it wrote.
    /// </summary>
    internal IReadOnlyList<string> MoveAdded(string evidence)
    {
        var now = List(_saveRoot);
        var current = now.ToDictionary(entry => Key(entry.Folder, entry.Name), Names);
        var problems = new List<string>();
        foreach (var entry in _before)
        {
            string key = Key(entry.Folder, entry.Name);
            if (!current.TryGetValue(key, out var found)) problems.Add(key + " is missing");
            else if (found.Size != entry.Size || found.ModifiedUtc != entry.ModifiedUtc) problems.Add(key + " changed");
        }
        if (problems.Count != 0)
            throw new InvalidOperationException($"The client's save folders in {_saveRoot} changed during the run ({string.Join("; ", problems)}); " +
                "nothing was moved. Those files were there before the run: inspect them, and move what the game added yourself.");
        var known = _before.Select(entry => Key(entry.Folder, entry.Name)).ToHashSet(Names);
        var added = now.Where(entry => !known.Contains(Key(entry.Folder, entry.Name))).ToList();
        var moved = new List<string>();
        foreach (var entry in added)
        {
            string source = Path.Combine(_saveRoot, entry.Folder, entry.Name);
            string folder = Path.Combine(evidence, AddedFolder, entry.Folder);
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, entry.Name);
            string sha256 = FileHash.Sha256(source);
            File.Move(source, target, overwrite: false);
            if (FileHash.Sha256(target) != sha256) throw new IOException($"{Key(entry.Folder, entry.Name)} changed while it was moved into {folder}.");
            moved.Add(Key(entry.Folder, entry.Name));
        }
        return moved;
    }

    private static string Key(string folder, string name) => folder + "/" + name;

    private static List<Entry> List(string saveRoot)
    {
        var entries = new List<Entry>();
        foreach (string folder in Folders)
        {
            string path = Path.Combine(saveRoot, folder);
            if (!Directory.Exists(path)) continue;
            if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"The client's {folder} ({path}) is a link; a menu-only run moves nothing out of a folder it cannot prove is the client's own.");
            foreach (string file in Directory.EnumerateFiles(path).Order(StringComparer.Ordinal))
            {
                FileHash.RefuseLink(file);
                var info = new FileInfo(file);
                entries.Add(new Entry(folder, info.Name, info.Length, info.LastWriteTimeUtc));
            }
        }
        return entries;
    }
}
