using System.Security.Cryptography;

namespace Valheim.Testing.Game;

/// <summary>
/// Opt-in, preview-only staging for an owned client's disposable local character. The prepared file stays in evidence;
/// the run installs a fresh filename and removes only files with that filename after its client has stopped.
/// </summary>
public sealed class CharacterStartPlan
{
    public string PreparedFile { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string CharactersLocalDirectory { get; set; } = "";
    /// <summary>Steam's userdata directory, containing account-id/892970/remote/characters.</summary>
    public string SteamUserDataDirectory { get; set; } = "";

    public void Validate(string character)
    {
        if (string.IsNullOrWhiteSpace(character) || character is "." or ".." || character != Path.GetFileName(character) ||
            character.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || character.EndsWith(".fch", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use the prepared file's fresh basename (without .fch) as client.character.");
        if (!Path.IsPathFullyQualified(PreparedFile) ||
            !string.Equals(Path.GetFileName(PreparedFile), character + ".fch", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("characterStart.preparedFile must be a full path to the fresh character filename.");
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("characterStart.sha256 must pin the prepared file by SHA256.");
        if (!Path.IsPathFullyQualified(CharactersLocalDirectory) ||
            !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(CharactersLocalDirectory)), "characters_local", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("characterStart.charactersLocalDirectory must be the owned client's full characters_local path.");
        if (!Path.IsPathFullyQualified(SteamUserDataDirectory) ||
            !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(SteamUserDataDirectory)), "userdata", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("characterStart.steamUserDataDirectory must be Steam's full userdata path.");
    }
}

// Owned by one ClientRounds.Run, from just before client launch until after the client process has stopped.
internal sealed class CharacterStartStage : IDisposable
{
    private readonly string _localDirectory, _character;
    private bool _disposed;

    private CharacterStartStage(string localDirectory, string character)
    { _localDirectory = localDirectory; _character = character; }

    public static CharacterStartStage Install(CharacterStartPlan plan, string character, long worldUid, HeightExpectation arrival)
    {
        plan.Validate(character);
        string prepared = Path.GetFullPath(plan.PreparedFile);
        string local = Path.GetFullPath(plan.CharactersLocalDirectory);
        string steam = Path.GetFullPath(plan.SteamUserDataDirectory);
        CharacterStartCopy.RejectLinkedAncestors(Path.GetDirectoryName(prepared)!);
        CharacterStartCopy.RejectLinkedAncestors(local, allowLiveCharacterLeaf: true);
        if (!File.Exists(prepared) || !Directory.Exists(local) || !Directory.Exists(steam))
            throw new FileNotFoundException("The prepared character, local character directory and Steam userdata directory must already exist.");
        byte[] bytes = File.ReadAllBytes(prepared);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared character changed since its SHA256 was pinned.");
        // AtWorld produces identical bytes only when this save already has the specified world's logout point, has
        // completed its first spawn, and has the exact expected coordinates. The independent client support check follows.
        if (!bytes.AsSpan().SequenceEqual(CharacterSavePosition.AtWorld(bytes, worldUid, arrival.X, arrival.Height, arrival.Z)))
            throw new InvalidDataException("The prepared character's logout point differs from the plan's world and arrival point.");
        string installed = Path.Combine(local, character + ".fch");
        RefuseCollisions(local, character);
        string siblingCloud = Path.Combine(Path.GetDirectoryName(local)!, "characters");
        RefuseCollisions(siblingCloud, character);
        foreach (string account in Directory.EnumerateDirectories(steam))
            RefuseCollisions(Path.Combine(account, "892970", "remote", "characters"), character);

        bool created = false;
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var target = new FileStream(installed, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
        }
        catch
        {
            if (created && File.Exists(installed)) File.Delete(installed);
            throw;
        }
        return new CharacterStartStage(local, character);
    }

    private static void RefuseCollisions(string directory, string character)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string file in Directory.EnumerateFiles(directory))
            if (OwnedFile(Path.GetFileName(file), character))
                throw new IOException($"A character or backup named {character} already exists in {directory}; choose a fresh filename.");
    }

    private static bool OwnedFile(string name, string character) =>
        name.Equals(character + ".fch", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(character + ".fch.old", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(character + "_backup_auto-", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // ClientRounds stops its owned client before this scope ends. RefuseCollisions ensured that every matching
        // filename was absent beforehand, so these files belong to this run (including game-made backup files).
        foreach (string file in Directory.EnumerateFiles(_localDirectory))
            if (OwnedFile(Path.GetFileName(file), _character)) File.Delete(file);
    }
}
