using System.Security.Cryptography;

namespace Valheim.Testing.Game;

/// <summary>
/// Prepares a positioned copy of a registered disposable test character (<see cref="DisposableCharacterStore"/>). Only a
/// character taken from a store is accepted, never a path, so a personal character cannot be prepared by pointing at its
/// file. This refuses Valheim character directories as output; a runner must stage the resulting file separately while
/// the game is stopped and verify arrival after joining. The character must already have visited the requested world.
/// </summary>
public static class CharacterStartCopy
{
    public static string Prepare(DisposableCharacter character, string destination, long worldUid, float x, float y, float z)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (!Path.IsPathFullyQualified(destination))
            throw new ArgumentException("The output must be a full path.");
        destination = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(destination), ".fch", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output must be a .fch file.");
        RejectLinkedAncestors(Path.GetDirectoryName(destination)!);
        if (Path.GetDirectoryName(destination)!.Equals(character.Store.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Write the copy to a new evidence directory, not into the character store.");
        if (File.Exists(destination)) throw new IOException("The output character already exists; choose a new file.");
        if (!Directory.Exists(Path.GetDirectoryName(destination)))
            throw new DirectoryNotFoundException("Create a new evidence directory for the prepared character first.");

        byte[] source = character.Store.Read(character);
        byte[] changed = CharacterSavePosition.AtWorld(source, worldUid, x, y, z);
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
