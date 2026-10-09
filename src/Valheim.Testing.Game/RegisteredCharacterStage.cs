using System.Security.Cryptography;

namespace Valheim.Testing.Game;

// A registered disposable character copied into an owned client's characters_local for one run (the default smoke
// character, TargetedRegression), from just before client launch until after the client process has stopped.
internal sealed class RegisteredCharacterStage : IDisposable
{
    private readonly string _localDirectory, _character;
    private readonly Action<CharacterStageEvent, string, string, string, string>? _journal;
    private bool _disposed;

    private RegisteredCharacterStage(string localDirectory, string character,
        Action<CharacterStageEvent, string, string, string, string>? journal)
    { _localDirectory = localDirectory; _character = character; _journal = journal; }

    // Hosted smoke starts at the game's ordinary spawn. A registered, game-created character is copied only for the
    // lifetime of the owned client; this path never edits its position or takes a personal save by filename.
    internal static RegisteredCharacterStage InstallRegistered(string storeDirectory, string registeredName, string localDirectory,
        string steamUserDataDirectory, string character,
        Action<CharacterStageEvent, string, string, string, string>? journal = null)
    {
        var store = DisposableCharacterStore.Open(storeDirectory);
        byte[] bytes = store.Read(store.Get(registeredName));
        return InstallBytes(bytes, localDirectory, steamUserDataDirectory, character, journal);
    }

    private static RegisteredCharacterStage InstallBytes(byte[] bytes, string local, string steam, string character,
        Action<CharacterStageEvent, string, string, string, string>? journal)
    {
        if (string.IsNullOrWhiteSpace(character) || character is "." or ".." || character != Path.GetFileName(character) ||
            character.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || character.EndsWith(".fch", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a fresh local character filename without .fch.");
        local = Path.GetFullPath(local);
        steam = Path.GetFullPath(steam);
        DisposableCharacterStore.RejectLinkedAncestors(local, allowLiveCharacterLeaf: true);
        if (!Directory.Exists(local) || !Directory.Exists(steam) ||
            !Path.GetFileName(Path.TrimEndingDirectorySeparator(local)).Equals("characters_local", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(Path.TrimEndingDirectorySeparator(steam)).Equals("userdata", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Stage only into the owned client's characters_local folder, with Steam's userdata directory supplied for collision checks.");
        string installed = Path.Combine(local, DisposableCharacterStore.SaveFile(character));
        foreach (string folder in DisposableCharacterStore.CharacterFolders(local, steam))
            RefuseCollisions(folder, character);

        // Intent records the expected bytes before CreateNew, so interrupted recovery need not trust a filename.
        string expectedSha256 = FileHash.Sha256(bytes);
        bool created = false;
        try
        {
            // A collision was ruled out before intent. An interrupted intent without completion does not prove
            // that the save on disk belongs to us, so recovery must leave it for inspection.
            journal?.Invoke(CharacterStageEvent.Intended, local, steam, character, expectedSha256);
            using var source = new MemoryStream(bytes, writable: false);
            using var target = new FileStream(installed, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
            journal?.Invoke(CharacterStageEvent.Done, local, steam, character, expectedSha256);
        }
        catch
        {
            if (created && File.Exists(installed)) File.Delete(installed);
            journal?.Invoke(CharacterStageEvent.Retired, local, steam, character, expectedSha256);
            throw;
        }
        return new RegisteredCharacterStage(local, character, journal);
    }

    private static void RefuseCollisions(string directory, string character)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string file in Directory.EnumerateFiles(directory))
            if (DisposableCharacterStore.IsCharacterFile(Path.GetFileName(file), character))
                throw new IOException($"A character or backup named {character} already exists in {directory}; choose a fresh filename.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // ClientRounds stops its owned client before this scope ends. RefuseCollisions ensured that every matching
        // filename was absent beforehand, so these files belong to this run (including game-made backup files).
        foreach (string file in Directory.EnumerateFiles(_localDirectory))
            if (DisposableCharacterStore.IsCharacterFile(Path.GetFileName(file), _character)) File.Delete(file);
        _journal?.Invoke(CharacterStageEvent.Retired, _localDirectory, "", _character, "");
    }
}

internal enum CharacterStageEvent { Intended, Done, Retired }
