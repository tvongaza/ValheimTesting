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
        source = Path.GetFullPath(source); outputParent = Path.GetFullPath(outputParent);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (outputParent.Equals(source, pathComparison) || outputParent.StartsWith(source + Path.DirectorySeparatorChar, pathComparison)) throw new ArgumentException("Output must be outside source.");
        var directories = new List<string>();
        var actual = Files(source, directories).ToDictionary(p => Path.GetRelativePath(source, p), Hash, StringComparer.Ordinal);
        Check.SameIdentities(actual.Keys, expectedHashes.Keys);
        foreach (var item in actual) if (!string.Equals(item.Value, expectedHashes[item.Key], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture hash mismatch: " + item.Key);
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
