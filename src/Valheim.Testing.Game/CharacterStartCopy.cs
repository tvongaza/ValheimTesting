using System.Security.Cryptography;

namespace Valheim.Testing.Game;

/// <summary>
/// Prepares a positioned copy of a registered disposable test character (<see cref="DisposableCharacterStore"/>). Only a
/// character taken from a store is accepted, never a path, so a personal character cannot be prepared by pointing at its
/// file. This refuses Valheim character directories as output; a runner must stage the resulting file separately while
/// the game is stopped and verify arrival after joining. <see cref="Prepare"/> updates a visited world's logout point;
/// <see cref="PrepareForNewWorld"/> explicitly adds the first entry for an unvisited world.
/// </summary>
/// <example>
/// Start with a game-created character registered in <see cref="DisposableCharacterStore"/>. Prepare a fresh file in an
/// evidence directory, then pin its returned SHA256 in the owned-client plan:
/// <code>
/// var store = DisposableCharacterStore.Open(storeDirectory);
/// var character = store.Get("tester");
/// string prepared = Path.Combine(evidenceDirectory, "tester-run-001.fch");
/// string sha256 = CharacterStartCopy.Prepare(
///     character, prepared, worldUid, 125f, 45f, -380f);
/// </code>
/// Set the owned-client plan's character name to <c>tester-run-001</c>, and its character-start fields to the prepared
/// file, SHA256, and store directory. For a clean character that has never visited the fixture world, call
/// <see cref="PrepareForNewWorld"/> instead. The evidence directory must already exist. <c>Prepare</c> does not install the
/// copy or launch the game; see the
/// <see href="https://github.com/tvongaza/ValheimTesting/blob/main/examples/FullLifecycle/README.md#optional-character-start-at-the-first-site-preview">owned-client example</see>
/// for staging and verifying the actual arrival. This is a position-only edit, not character creation or customization.
/// </example>
public static class CharacterStartCopy
{
    /// <summary>
    /// Copies <paramref name="character"/> to a new <c>.fch</c> at <paramref name="destination"/>, setting only the
    /// logout point for <paramref name="worldUid"/>. Returns the prepared file's SHA256 for the owned-client plan.
    /// </summary>
    /// <remarks>
    /// The character must have completed its first spawn and visited this world. The output directory must exist, the
    /// destination must not exist, and neither may be a live character directory. This does not create a new player ID.
    /// </remarks>
    public static string Prepare(DisposableCharacter character, string destination, long worldUid, float x, float y, float z)
        => PrepareCore(character, destination, worldUid, x, y, z, addWorld: false);

    /// <summary>
    /// Prepares a registered disposable character for its first visit to <paramref name="worldUid"/>. The character must
    /// have completed its first spawn but have no entry for this world. An explicit new-world operation prevents a typo in
    /// <see cref="Prepare"/>'s UID from silently adding an entry. The game still decides the final grounded spawn; verify
    /// client support after joining. This format-gated edit is for Valheim 1.0.16 only.
    /// </summary>
    public static string PrepareForNewWorld(DisposableCharacter character, string destination, long worldUid, float x, float y, float z)
        => PrepareCore(character, destination, worldUid, x, y, z, addWorld: true);

    private static string PrepareCore(DisposableCharacter character, string destination, long worldUid, float x, float y, float z, bool addWorld)
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
        byte[] changed = addWorld ? CharacterSavePosition.AtNewWorld(source, worldUid, x, y, z)
                                  : CharacterSavePosition.AtWorld(source, worldUid, x, y, z);
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
