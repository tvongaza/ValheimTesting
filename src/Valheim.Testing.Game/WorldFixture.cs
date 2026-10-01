using System.Security.Cryptography;
using System.Text.Json;

namespace Valheim.Testing.Game;
// Owns only a newly-created directory. Never edits or launches the source world.
public sealed class WorldFixture : IDisposable
{
    public string DirectoryPath { get; }
    public IReadOnlyDictionary<string, string> SourceHashes { get; }
    public bool Preserve { get; set; }
    private bool _disposed;
    private WorldFixture(string path, Dictionary<string, string> hashes) { DirectoryPath = path; SourceHashes = hashes; }
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
        string target = Path.Combine(outputParent, "valheim-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
        var fixture = new WorldFixture(target, actual);
        try
        {
            foreach (string directory in directories) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            foreach (var item in actual)
            {
                string destination = Path.Combine(target, item.Key); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(source, item.Key), destination);
                if (Hash(destination) != item.Value) throw new IOException("Fixture changed while copying: " + item.Key);
            }
            File.WriteAllText(Path.Combine(target, "fixture-provenance.json"), JsonSerializer.Serialize(actual));
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
    private static Dictionary<string, string> Hashes(string source, List<string> directories) =>
        Files(source, directories).ToDictionary(p => Path.GetRelativePath(source, p), Hash, StringComparer.Ordinal);
    private static IEnumerable<string> Files(string directory, List<string> directories)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture links are unsupported.");
        directories.Add(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture links are unsupported.");
            if ((attrs & FileAttributes.Directory) != 0) { foreach (string child in Files(path, directories)) yield return child; }
            else yield return path;
        }
    }
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public void Dispose()
    {
        if (_disposed) return;
        if (!Preserve && Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        _disposed = true;
    }
}
