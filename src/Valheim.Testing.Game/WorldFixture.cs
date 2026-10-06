using System.Text.Json;

namespace Valheim.Testing.Game;
// Owns only a newly-created directory. Never edits or launches the source world.
/// <summary>Verified, disposable copy of a world fixture. Disposing removes only the new copy unless <see cref="Preserve"/> is set.</summary>
/// <example>
/// Obtain hashes with <see cref="Manifest"/> when authoring the fixture, persist and review them in the plan, then
/// use those hashes for the run. Never recalculate them from an unexpected source during the run:
/// <code>
/// WorldFixture.Verify(fixtureSource, reviewedHashes);
/// using var fixture = WorldFixture.Copy(fixtureSource, runDirectory, reviewedHashes);
/// string disposableWorld = fixture.DirectoryPath;
/// // Stop the owned game before the fixture is disposed.
/// </code>
/// See the <see href="https://github.com/tvongaza/ValheimTesting/blob/main/examples/FullLifecycle/MyMod.SystemTests/ServerFixture.cs">runnable server example</see>
/// for the full preparation and cleanup sequence.
/// </example>
public sealed class WorldFixture : IDisposable
{
    public string DirectoryPath { get; }
    public IReadOnlyDictionary<string, string> SourceHashes { get; }
    public bool Preserve { get; set; }
    private bool _disposed;
    // The journal run the copy was made under, kept for its later lines: they land in the same run whatever flow retires it.
    // The journal folder too (null for an earlier process's copy: this machine's journal as it is when the line is written).
    private readonly RunJournal _journal;
    private readonly string? _journalDirectory;
    private WorldFixture(string path, Dictionary<string, string> hashes, RunJournal? journal = null, string? journalDirectory = null)
    {
        DirectoryPath = path; SourceHashes = hashes; _journal = journal ?? RunJournal.ThisProcess; _journalDirectory = journalDirectory;
    }
    /// <summary>
    /// A copy made earlier, from its own manifest: by an earlier process (<see cref="OwnedCopies.Remove"/>), or a staged copy a
    /// run takes over, which keeps journalling in the journal it was made in (<paramref name="madeIn"/>).
    /// </summary>
    internal static WorldFixture Existing(string path, Dictionary<string, string> hashes, WorldFixture? madeIn = null) =>
        new(path, hashes, journalDirectory: madeIn?._journalDirectory);
    /// <summary>Copies an exact, previously pinned fixture into a new directory owned by this instance.</summary>
    public static WorldFixture Copy(string source, string outputParent, IReadOnlyDictionary<string, string> expectedHashes)
    {
        if (expectedHashes.Count == 0) throw new ArgumentException("A pinned fixture manifest is required.");
        return CopyFixture(source, outputParent, expectedHashes);
    }
    /// <summary>
    /// Copies a fixture that has no manifest, for an explicitly unpinned run only: every file's SHA256 is recorded in
    /// <see cref="SourceHashes"/> and <c>fixture-provenance.json</c> and the copy is verified against it, but nothing says
    /// the source is the one intended. Prefer <see cref="Copy"/>.
    /// </summary>
    public static WorldFixture CopyAsFound(string source, string outputParent) => CopyFixture(source, outputParent, null);
    private static WorldFixture CopyFixture(string source, string outputParent, IReadOnlyDictionary<string, string>? expectedHashes)
    {
        source = Path.GetFullPath(source); outputParent = Path.GetFullPath(outputParent);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (outputParent.Equals(source, pathComparison) || outputParent.StartsWith(source + Path.DirectorySeparatorChar, pathComparison)) throw new ArgumentException("Output must be outside source.");
        var directories = new List<string>();
        var actual = Hashes(source, directories);
        if (expectedHashes != null)
        {
            Check.SameIdentities(actual.Keys, expectedHashes.Keys);
            foreach (var item in actual) if (!string.Equals(item.Value, expectedHashes[item.Key], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture hash mismatch: " + item.Key);
        }
        else if (actual.Count == 0) throw new InvalidOperationException("Fixture source has no files: " + source);
        string target = Path.Combine(outputParent, "valheim-test-" + Guid.NewGuid().ToString("N"));
        // Journalled on this machine before the copy (#257): an interrupted process leaves a record of every copy it may own.
        var journal = RunJournal.ThisProcess;
        string journalDirectory = RunJournal.LocalDirectory;
        try { Journal(journal, journalDirectory, JournalEntry.CopyIntended, target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not journal the copy in {journalDirectory}, so nothing was copied: ValheimTesting records every copy " +
                $"it makes there first, so an interrupted run's copies can be found and removed (valheim-test env status). {error.Message}", error);
        }
        Directory.CreateDirectory(target);
        var fixture = new WorldFixture(target, actual, journal, journalDirectory);
        try
        {
            foreach (string directory in directories) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            foreach (var item in actual)
            {
                string destination = Path.Combine(target, item.Key); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(source, item.Key), destination);
                if (FileHash.Sha256(destination) != item.Value) throw new IOException("Fixture changed while copying: " + item.Key);
            }
            File.WriteAllText(Path.Combine(target, ProvenanceFile), JsonSerializer.Serialize(actual));
            Note(journal, journalDirectory, JournalEntry.CopyDone, target);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    /// <summary>
    /// Refuses <paramref name="source"/> unless its files are exactly <paramref name="expectedHashes"/> (relative path to
    /// SHA256), naming each missing, unexpected or changed file: the check <see cref="Copy"/> makes, without copying.
    /// </summary>
    public static void Verify(string source, IReadOnlyDictionary<string, string> expectedHashes)
    {
        if (expectedHashes.Count == 0) throw new ArgumentException("A pinned fixture manifest is required.");
        source = Path.GetFullPath(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Fixture source does not exist: " + source);
        var actual = Hashes(source, []);
        var problems = expectedHashes.Keys.Where(key => !actual.ContainsKey(key)).Order(StringComparer.Ordinal).Select(key => key + " is missing")
            .Concat(actual.Keys.Where(key => !expectedHashes.ContainsKey(key)).Order(StringComparer.Ordinal).Select(key => key + " is not in the manifest"))
            .Concat(actual.Where(item => expectedHashes.TryGetValue(item.Key, out var hash) && !string.Equals(item.Value, hash, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key} is {item.Value}, pinned {expectedHashes[item.Key]}")).ToList();
        if (problems.Count != 0)
            throw new InvalidOperationException($"Fixture hash mismatch in {source}: {string.Join("; ", problems)}. The fixture changed after it was pinned, or the plan names another one; build the manifest from the exact directory (WorldFixture.Manifest).");
    }
    /// <summary>
    /// The SHA256 manifest of every file under <paramref name="source"/>, keyed by this platform's relative path: what a
    /// pinned plan records and <see cref="Copy"/> verifies. Build it on the platform that will copy the fixture, from the
    /// exact directory the plan names. Links are refused, as in <see cref="Copy"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Manifest(string source)
    {
        source = Path.GetFullPath(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Fixture source does not exist: " + source);
        var manifest = Hashes(source, []);
        if (manifest.Count == 0) throw new InvalidOperationException("Fixture source has no files: " + source);
        return manifest;
    }
    // Every file under the source by this platform's relative path, links refused; directories receives each directory walked.
    private static Dictionary<string, string> Hashes(string source, List<string> directories) =>
        FileHash.Files(source, directories).ToDictionary(p => Path.GetRelativePath(source, p), FileHash.Sha256, StringComparer.Ordinal);

    /// <summary>
    /// Keeps what changed in the copy since it was made, then deletes the copy. Every file added, and every copied file whose
    /// SHA256 changed, goes to <paramref name="keepIn"/> under its relative path (the logs, configs and caches a run wrote). A
    /// file larger than <paramref name="maxFileBytes"/>, or past <paramref name="maxKeptBytes"/> in all, is listed with its
    /// size and SHA256 instead; a link is listed and never followed. <c>changes.json</c> in <paramref name="keepIn"/> lists
    /// the added, changed, missing and not-kept files. Every other file is the source's, by its hash in
    /// <see cref="SourceHashes"/>, so nothing the run made is lost. Call it only once no process uses the copy. Refused after
    /// <see cref="Dispose"/>. When the copy cannot be deleted, what was kept stays and the error says which copy remains.
    /// </summary>
    public RetiredCopy Retire(string keepIn, long maxFileBytes = 64L << 20, long maxKeptBytes = 256L << 20)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorldFixture), "The copy was already removed.");
        keepIn = Path.GetFullPath(keepIn);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (keepIn.Equals(DirectoryPath, pathComparison) || keepIn.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, pathComparison))
            throw new ArgumentException("Keep the changes outside the copy that is removed.", nameof(keepIn));
        var added = new List<string>(); var changed = new List<string>(); var links = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        long bytesFreed = 0;
        void Walk(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(DirectoryPath, path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { links.Add(relative); continue; }
                if ((attributes & FileAttributes.Directory) != 0) { Walk(path); continue; }
                bytesFreed += new FileInfo(path).Length;
                seen.Add(relative);
                // The copy's own record, written over any source file of that name (a copy of a copy has one): never the run's.
                if (relative == ProvenanceFile) continue;
                if (!SourceHashes.TryGetValue(relative, out var source)) added.Add(relative);
                else if (!FileHash.Sha256(path).Equals(source, StringComparison.OrdinalIgnoreCase)) changed.Add(relative);
            }
        }
        Walk(DirectoryPath);
        var missing = SourceHashes.Keys.Where(key => !seen.Contains(key)).Order(StringComparer.Ordinal).ToList();
        var notKept = links.Order(StringComparer.Ordinal).Select(link => new NotKeptFile(link, null, null, "a link: listed, not followed")).ToList();
        Directory.CreateDirectory(keepIn);
        long kept = 0;
        added.Sort(StringComparer.Ordinal); changed.Sort(StringComparer.Ordinal);
        foreach (string relative in added.Concat(changed).Order(StringComparer.Ordinal))
        {
            string from = Path.Combine(DirectoryPath, relative);
            long length = new FileInfo(from).Length;
            if (length > maxFileBytes || kept + length > maxKeptBytes)
            {
                notKept.Add(new(relative, length, FileHash.Sha256(from), length > maxFileBytes ? $"larger than {DiskSpace.Format(maxFileBytes)}" : $"past {DiskSpace.Format(maxKeptBytes)} kept in all"));
                continue;
            }
            string to = Path.Combine(keepIn, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: false);
            kept += length;
        }
        var retired = new RetiredCopy(DirectoryPath, keepIn, added, changed, missing, notKept, kept, bytesFreed);
        File.WriteAllText(Path.Combine(keepIn, "changes.json"), JsonSerializer.Serialize(retired, new JsonSerializerOptions { WriteIndented = true }));
        try { DeleteTree(DirectoryPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Kept the run's changes in {keepIn}, but could not remove the copy {DirectoryPath} ({error.Message}). Delete it once no process uses it.", error);
        }
        _disposed = true;
        Note(_journal, _journalDirectory, JournalEntry.CopyRetired, DirectoryPath, ("keptIn", keepIn));
        return retired;
    }
    private const string ProvenanceFile = "fixture-provenance.json";

    /// <summary>
    /// <c>&lt;copy&gt;.owner.json</c>, which copies made before the run journal recorded their owner in (read by
    /// <see cref="OwnedCopies"/> for those copies only; nothing writes it any more).
    /// </summary>
    internal static string OwnerFile(string copy) => copy + ".owner.json";

    /// <summary>
    /// Why a preserved copy stays as left behind rather than handed over (a run's runtime kept on request, or one a server may
    /// still use): journalled as <c>copy-kept</c>, so <c>env status</c> lists it and <c>env teardown</c> removes it. A preserved
    /// copy without a reason (a run's world, its evidence) is handed over to the output it is in.
    /// </summary>
    internal string? KeepReason { get; set; }

    // This machine's journal: before the copy a failed line stops it; after an effect a lost line is a warning.
    private static void Journal(RunJournal journal, string? directory, string kind, string copy, params (string Key, string Value)[] more) =>
        journal.AppendLocal(Actor, JournalEntry.Of(kind, [("runtime", copy), ("local", "true"), .. more]), directory);
    private static void Note(RunJournal journal, string? directory, string kind, string copy, params (string Key, string Value)[] more)
    {
        try { Journal(journal, directory, kind, copy, more); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Warning: could not journal {kind} for {copy}: {error.Message}"); }
    }
    /// <summary>The actor this machine's copies are journalled as.</summary>
    internal const string Actor = "fixture";

    // Windows refuses to delete a read-only file, and File.Copy keeps the source's read-only attribute: clear it and retry.
    // Links are removed, never followed.
    internal static void DeleteTree(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
                if ((file.Attributes & FileAttributes.ReadOnly) != 0) file.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(path, recursive: true);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!Preserve && Directory.Exists(DirectoryPath)) DeleteTree(DirectoryPath);
        _disposed = true;
        if (!Preserve) Note(_journal, _journalDirectory, JournalEntry.CopyRetired, DirectoryPath);
        else if (KeepReason != null) Note(_journal, _journalDirectory, JournalEntry.CopyKept, DirectoryPath, ("why", KeepReason));
        else Note(_journal, _journalDirectory, JournalEntry.CopyRetired, DirectoryPath, ("handedOver", "true"));
    }
}

/// <summary>A file a run made or changed that <see cref="WorldFixture.Retire"/> listed instead of keeping: its size and SHA256 (none for a link), and why.</summary>
[ResultShape]
public sealed record NotKeptFile(string Path, long? Bytes, string? Sha256, string Reason);

/// <summary>
/// What <see cref="WorldFixture.Retire"/> did: the removed copy, where the changes went, the files the run added or changed
/// and those it removed (relative paths), what was listed and not kept, the bytes kept and the bytes the copy held.
/// </summary>
[ResultShape]
public sealed record RetiredCopy(string Copy, string KeptIn, IReadOnlyList<string> Added, IReadOnlyList<string> Changed,
    IReadOnlyList<string> Missing, IReadOnlyList<NotKeptFile> NotKept, long KeptBytes, long BytesFreed)
{
    /// <summary>One line for a report: "removed &lt;copy&gt; (2.1 GB); kept 18 added or changed files (3 MB) in &lt;dir&gt;, 1 listed only, 0 missing (changes.json)".</summary>
    public override string ToString() =>
        $"removed {Copy} ({DiskSpace.Format(BytesFreed)}); kept {Added.Count + Changed.Count - NotKept.Count(file => file.Bytes != null)} added or changed files " +
        $"({DiskSpace.Format(KeptBytes)}) in {KeptIn}, {NotKept.Count} listed only, {Missing.Count} missing (changes.json)";
}

/// <summary>Who holds a copy: <see cref="WorldFixture"/>'s owner record.</summary>
internal sealed record CopyOwner(int Pid, DateTime StartedUtc);
