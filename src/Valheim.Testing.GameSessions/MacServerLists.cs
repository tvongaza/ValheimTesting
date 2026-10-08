using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The macOS dedicated server ignores -savedir for its three access lists. Keep the user's versions under the
/// run's directory before launch, journal the backup, then move them out of the game's path. Restore them only after
/// the owned process has stopped. A version written
/// during the run is kept beside the originals as evidence rather than discarded.
/// </summary>
internal sealed class MacServerLists
{
    internal static readonly string[] Names = ["adminlist.txt", "permittedlist.txt", "bannedlist.txt"];
    private readonly string _saveRoot, _backup;
    private readonly string[] _hashes;

    private MacServerLists(string saveRoot, string backup, string[] hashes)
    { _saveRoot = saveRoot; _backup = backup; _hashes = hashes; }

    internal static MacServerLists Capture(string saveRoot, string backup)
    {
        if (Directory.Exists(backup) && Directory.EnumerateFileSystemEntries(backup).Any())
            throw new IOException($"The Mac server list backup {backup} is not empty; refusing to overwrite it.");
        Directory.CreateDirectory(backup);
        string[] hashes = new string[Names.Length];
        for (int i = 0; i < Names.Length; i++)
        {
            string source = Path.Combine(saveRoot, Names[i]);
            if (new FileInfo(source).LinkTarget != null)
                throw new IOException($"The Mac server list {source} is a link; refusing to replace it.");
            if (Directory.Exists(source)) throw new IOException($"The Mac server list {source} is a directory, not a file.");
            if (!File.Exists(source)) { hashes[i] = "-"; continue; }
            string target = Path.Combine(backup, Names[i]);
            File.Copy(source, target, overwrite: false);
            hashes[i] = FileHash.Sha256(source);
            if (FileHash.Sha256(target) != hashes[i]) throw new IOException($"The Mac server list {Names[i]} changed while it was copied.");
        }
        return new(saveRoot, backup, hashes);
    }

    internal JournalEntry Captured() => JournalEntry.Of(JournalEntry.MacListsCaptured,
        ("saveRoot", _saveRoot), ("backup", _backup), ("admin", _hashes[0]), ("permitted", _hashes[1]), ("banned", _hashes[2]));

    internal void Isolate()
    {
        // Check all three before changing any. A crash partway through deletion is recoverable because Captured
        // was journalled first and every original byte remains in the run's backup.
        for (int i = 0; i < Names.Length; i++)
        {
            string path = Path.Combine(_saveRoot, Names[i]);
            if (new FileInfo(path).LinkTarget != null)
                throw new IOException($"The Mac server list {Names[i]} became a link during preparation.");
            string found = File.Exists(path) ? FileHash.Sha256(path) : "-";
            if (found != _hashes[i]) throw new IOException($"The Mac server list {Names[i]} changed during preparation; no server will start.");
        }
        for (int i = 0; i < Names.Length; i++)
            if (_hashes[i] != "-") File.Delete(Path.Combine(_saveRoot, Names[i]));
    }

    internal static MacServerLists FromJournal(IReadOnlyDictionary<string, string> fields, string runId)
    {
        string root = fields.GetValueOrDefault("saveRoot") ?? "", backup = fields.GetValueOrDefault("backup") ?? "";
        if (!OperatingSystem.IsMacOS() || !Path.GetFullPath(root).Equals(HostedWorld.DefaultSaveDirectory(ClientPlatform.MacOS), StringComparison.Ordinal) ||
            Path.GetFileName(backup) != "server-lists" || Path.GetFileName(Path.GetDirectoryName(backup)) != runId)
            throw new InvalidDataException("The journal does not name this Mac user's save root and this run's server-lists directory.");
        string[] hashes = [fields.GetValueOrDefault("admin") ?? "", fields.GetValueOrDefault("permitted") ?? "", fields.GetValueOrDefault("banned") ?? ""];
        if (hashes.Any(hash => hash != "-" && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))))
            throw new InvalidDataException("The journal has an invalid Mac server list hash.");
        return new(root, backup, hashes);
    }

    internal void Restore()
    {
        // Validate the entire backup before changing any of the user's files. A failed or partial backup must
        // leave all three live lists alone so recovery can be retried after the backup is repaired.
        for (int i = 0; i < Names.Length; i++)
        {
            string name = Names[i], original = Path.Combine(_saveRoot, name), before = Path.Combine(_backup, name);
            if (new FileInfo(original).LinkTarget != null || new FileInfo(before).LinkTarget != null)
                throw new IOException($"The Mac server list {name} or its backup is a link; refusing to restore it.");
            if (Directory.Exists(original) || Directory.Exists(before))
                throw new IOException($"The Mac server list {name} or its backup is a directory; refusing to restore it.");
            if (_hashes[i] != "-" && (!File.Exists(before) || FileHash.Sha256(before) != _hashes[i]))
                throw new IOException($"The saved copy of {name} is missing or changed; the user's lists were not overwritten.");
            string after = Path.Combine(_backup, "after", name);
            if (new FileInfo(after).LinkTarget != null)
                throw new IOException($"The run copy of Mac server list {name} is a link; refusing to restore it.");
            if (File.Exists(original) && (_hashes[i] == "-" || FileHash.Sha256(original) != _hashes[i]) &&
                File.Exists(after) && FileHash.Sha256(after) != FileHash.Sha256(original))
                throw new IOException($"The Mac server list {name} changed again after an interrupted restore; its earlier run copy is kept at {after}, and the current file was not overwritten.");
        }
        for (int i = 0; i < Names.Length; i++)
        {
            string name = Names[i], original = Path.Combine(_saveRoot, name), before = Path.Combine(_backup, name);
            string expected = _hashes[i];
            if (File.Exists(original) && (expected == "-" || FileHash.Sha256(original) != expected))
            {
                // Copy first: an interrupted restore can retry safely. Never overwrite a captured post-run version.
                string after = Path.Combine(_backup, "after", name);
                Directory.CreateDirectory(Path.GetDirectoryName(after)!);
                if (!File.Exists(after)) File.Copy(original, after);
            }
            if (expected == "-")
            {
                if (File.Exists(original)) File.Delete(original);
            }
            else if (!File.Exists(original) || FileHash.Sha256(original) != expected)
                File.Copy(before, original, overwrite: true);
        }
    }
}
