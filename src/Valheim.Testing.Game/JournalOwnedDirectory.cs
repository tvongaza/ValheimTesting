using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>
/// An ephemeral directory owned by one journalled run. Unlike a <see cref="WorldFixture"/>, recovery normally discards
/// its contents; a caller may name a small, explicit set of evidence files to copy elsewhere before retirement.
/// </summary>
internal static class JournalOwnedDirectory
{
    internal const string OwnerFile = ".valheim-testing-owner.json";
    private sealed record Owner(string Run, string Id, string Purpose);

    internal sealed class InterruptedAfterCreateForTestException : Exception { }

    /// <summary>Journals the directory before creating it, populates it, then journals completion.</summary>
    internal static string Create(string parent, string actor, string purpose, Action<string> populate,
        params (string Key, string Value)[] recovery) => Create(parent, actor, purpose, populate, null, recovery);

    /// <summary>Deterministic crash boundary for recovery tests: leaves the just-created, already-journalled directory in place.</summary>
    internal static string InterruptAfterCreateForTest(string parent, string actor, string purpose,
        params (string Key, string Value)[] recovery) => Create(parent, actor, purpose, _ => { },
            _ => throw new InterruptedAfterCreateForTestException(), recovery);

    private static string Create(string parent, string actor, string purpose, Action<string> populate, Action<string>? afterCreate,
        (string Key, string Value)[] recovery)
    {
        ArgumentNullException.ThrowIfNull(populate);
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetFullPath(parent), "valheim-test-" + id);
        var journal = RunJournal.ThisProcess;
        JournalEntry Entry(string kind, params (string Key, string Value)[] more) => JournalEntry.Of(kind,
            [("runtime", path), ("local", "true"), ("cleanup", "delete"), ("ownerId", id), ("purpose", purpose), .. recovery, .. more]);

        journal.AppendLocal(actor, Entry(JournalEntry.CopyIntended));
        try
        {
            Directory.CreateDirectory(path);
            afterCreate?.Invoke(path);
            string owner = Path.Combine(path, OwnerFile), temporary = owner + ".new";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, new Owner(journal.RunId, id, purpose));
            File.Move(temporary, owner);
            populate(path);
            journal.AppendLocal(actor, Entry(JournalEntry.CopyDone, ("completed", "true")));
            return path;
        }
        catch (Exception error) when (error is not InterruptedAfterCreateForTestException)
        {
            try
            {
                if (Directory.Exists(path)) WorldFixture.DeleteTree(path);
                journal.AppendLocal(actor, Entry(JournalEntry.CopyRetired));
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"Warning: could not retire interrupted owned directory {path}: {cleanup.Message}"); }
            throw;
        }
    }

    /// <summary>Deletes the exact directory named by its journal fields, or refuses a different directory at that path.</summary>
    internal static IReadOnlyList<string> Retire(string run, IReadOnlyDictionary<string, string> fields)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fields["runtime"]));
        string id = fields.GetValueOrDefault("ownerId") ?? "";
        string purpose = fields.GetValueOrDefault("purpose") ?? "";
        if (id.Length != 32 || !id.All(Uri.IsHexDigit) || Path.GetFileName(path) != "valheim-test-" + id)
            throw new InvalidDataException("the journal does not name a discard-only valheim-test-<32 hex> directory");
        if (!Directory.Exists(path)) return [];
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("the owned directory became a link");
        string ownerFile = Path.Combine(path, OwnerFile);
        if (!File.Exists(ownerFile))
        {
            if (fields.GetValueOrDefault("completed") == "true")
                throw new InvalidDataException("the completed owned directory has no owner marker");
        }
        else
        {
            var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(ownerFile))
                ?? throw new InvalidDataException("the owned directory has an empty owner marker");
            if (owner != new Owner(run, id, purpose))
                throw new InvalidDataException("the owned directory's marker names another owner");
        }
        var kept = new List<string>();
        string? keepIn = fields.GetValueOrDefault("keepIn");
        foreach (string relative in (fields.GetValueOrDefault("keepFiles") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (keepIn == null || Path.IsPathFullyQualified(relative) || relative != Path.GetFileName(relative))
                throw new InvalidDataException("the owned directory's evidence destination is invalid");
            string source = Path.Combine(path, relative);
            if (!File.Exists(source)) continue;
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("an owned evidence file became a link");
            Directory.CreateDirectory(keepIn);
            string destination = Path.Combine(keepIn, "detached-" + relative);
            if (File.Exists(destination))
            {
                if (FileHash.Sha256(source) != FileHash.Sha256(destination))
                    throw new IOException("different evidence already exists at " + destination);
            }
            else File.Copy(source, destination);
            kept.Add(destination);
        }
        DeleteMarkerLast(path, ownerFile);
        return kept;
    }

    // The owner marker goes last: a delete that fails part-way (a locked file) leaves the marker, so a retry can still prove ownership.
    private static void DeleteMarkerLast(string path, string ownerFile)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (entry.FullName == ownerFile) continue;
            if (entry is DirectoryInfo directory && (directory.Attributes & FileAttributes.ReparsePoint) == 0) WorldFixture.DeleteTree(directory.FullName);
            else if (entry is DirectoryInfo link) link.Delete();
            else
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        if (File.Exists(ownerFile)) File.Delete(ownerFile);
        Directory.Delete(path);
    }
}
