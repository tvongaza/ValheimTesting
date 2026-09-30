using System.Security.Cryptography;

namespace Valheim.Testing.Game;

/// <summary>
/// Prepares a positioned copy of an existing local test character. This refuses Valheim character directories as output;
/// a runner must stage the resulting file separately while the game is stopped and verify arrival after joining.
/// The character must already have visited the requested world.
/// </summary>
public static class CharacterStartCopy
{
    public static string Prepare(string source, string destination, long worldUid, float x, float y, float z)
    {
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination))
            throw new ArgumentException("Character source and output must be full paths.");
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(source), ".fch", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(destination), ".fch", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Character source and output must be .fch files.");
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(source)), "characters_local", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The source must be in characters_local, never a cloud characters folder.");
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The output must be a separate copy, not the source character.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(destination)!); parent != null; parent = parent.Parent)
            if (parent.Name.Equals("characters_local", StringComparison.OrdinalIgnoreCase) ||
                parent.Name.Equals("characters", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Write the copy to a new evidence directory, never a live character directory.");
        if (File.Exists(destination)) throw new IOException("The output character already exists; choose a new file.");
        if (!Directory.Exists(Path.GetDirectoryName(destination)))
            throw new DirectoryNotFoundException("Create a new evidence directory for the prepared character first.");

        byte[] changed = CharacterSavePosition.AtWorld(File.ReadAllBytes(source), worldUid, x, y, z);
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            try
            {
                output.Write(changed);
                output.Flush(flushToDisk: true);
            }
            catch
            {
                output.Dispose();
                File.Delete(destination);
                throw;
            }
        }
        return Convert.ToHexString(SHA256.HashData(changed)).ToLowerInvariant();
    }
}
