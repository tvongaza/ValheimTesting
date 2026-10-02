using System.Security.Cryptography;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>
/// A clean, game-created Valheim 1.0.16 local character for an owned smoke run. It has completed its first spawn,
/// has visited no worlds and contains no data copied from another character. Every run stages a copy from a
/// <see cref="DisposableCharacterStore"/>; the packaged save is never played or rewritten.
/// </summary>
/// <remarks>
/// The save format belongs to Valheim. Recheck this fixture in the native client after a game update.
/// Each copy has the same player ID, so use it for one client at a time, never for simultaneous players.
/// </remarks>
public static class DefaultSmokeCharacter
{
    public const string Name = "vtseedclean";
    private const string Resource = "Valheim.Testing.Game.SmokeCharacter.vtseedclean.fch";
    private const string Sha256 = "16712a2a3166e9b93c3aa006ea8d3d85a2d56372f7363728808f30d18bc8d626";
    private const string Marker = "default-smoke-character.txt";
    private const string MarkerText = "Valheim.Testing.Game default smoke character v1\n" + Sha256 + "\n";

    /// <summary>
    /// Creates a registered, disposable character store under a new directory. Pass the returned store's
    /// <see cref="DisposableCharacterStore.Root"/> as <c>client.characterStore</c> and <see cref="Name"/> as
    /// <c>client.character</c> in a targeted run. The source save stays embedded in the package.
    /// </summary>
    public static DisposableCharacterStore Prepare(string newRoot)
    {
        newRoot = Path.GetFullPath(newRoot);
        CharacterStartCopy.RejectLinkedAncestors(newRoot);
        if (Path.Exists(newRoot)) throw new IOException("Use a new directory for the default character; an existing character store is never replaced.");
        Directory.CreateDirectory(newRoot);
        try
        {
            var store = BuildStore(newRoot);
            File.WriteAllText(Path.Combine(newRoot, Marker), MarkerText);
            return store;
        }
        catch
        {
            Directory.Delete(newRoot, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// Stages this registered fixture under its exact local filename for one owned client run. Dispose the returned
    /// scope only after the client has stopped; it removes only that filename and its game-made backups. A collision
    /// with an existing local or Steam Cloud character is refused before writing anything.
    /// </summary>
    public static IDisposable StageForRun(DisposableCharacterStore store, string charactersLocalDirectory,
        string steamUserDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        return CharacterStartStage.InstallRegistered(store.Root, Name, charactersLocalDirectory,
            steamUserDataDirectory, Name);
    }

    /// <summary>
    /// Reuses a valid store or repairs a missing/changed copy in a directory previously made by <see cref="Prepare"/>.
    /// It refuses an unfamiliar directory or any extra file; it never repairs a live or personal character folder.
    /// </summary>
    public static DisposableCharacterStore Ensure(string root)
    {
        root = Path.GetFullPath(root);
        CharacterStartCopy.RejectLinkedAncestors(root);
        if (!Path.Exists(root)) return Prepare(root);
        if (!Directory.Exists(root)) throw new IOException("The default character path is not a directory.");
        string marker = Path.Combine(root, Marker), storePath = Path.Combine(root, "store");
        if (!File.Exists(marker) || IsLink(marker) || File.ReadAllText(marker) != MarkerText)
            throw new IOException("This is not a marked default smoke character directory; it will not be changed.");
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            if (entry != marker && entry != storePath)
                throw new IOException("The default character directory has an unfamiliar entry: " + Path.GetFileName(entry));
        CharacterStartCopy.RejectLinkedAncestors(storePath);
        if (Path.Exists(storePath) && !Directory.Exists(storePath)) throw new IOException("The default character store is not a directory.");
        if (Directory.Exists(storePath))
            foreach (string entry in Directory.EnumerateFileSystemEntries(storePath))
                if (Directory.Exists(entry) || IsLink(entry) || (Path.GetFileName(entry) != DisposableCharacterStore.ManifestFile &&
                    Path.GetFileName(entry) != Name + ".fch"))
                    throw new IOException("The default character store has an unfamiliar entry: " + Path.GetFileName(entry));

        string copy = Path.Combine(storePath, Name + ".fch");
        if (File.Exists(copy) && FileHash(copy) == Sha256)
        {
            try { return DisposableCharacterStore.Open(storePath).Get(Name).Store; }
            catch (Exception error) when (error is IOException or InvalidDataException or KeyNotFoundException or NotSupportedException or JsonException) { }
        }
        if (Directory.Exists(storePath))
        {
            File.Delete(copy);
            File.Delete(Path.Combine(storePath, DisposableCharacterStore.ManifestFile));
        }
        return BuildStore(root);
    }

    private static DisposableCharacterStore BuildStore(string root)
    {
        byte[] bytes = ReadFixture();
        string sourceDir = Path.Combine(root, "source", "characters_local");
        Directory.CreateDirectory(sourceDir);
        try
        {
            string source = Path.Combine(sourceDir, Name + ".fch");
            File.WriteAllBytes(source, bytes);
            var store = DisposableCharacterStore.Create(Path.Combine(root, "store"));
            store.Register(Name, source);
            return store;
        }
        finally { Directory.Delete(Path.Combine(root, "source"), recursive: true); }
    }

    private static byte[] ReadFixture()
    {
        using var input = typeof(DefaultSmokeCharacter).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidDataException("The packaged default character is missing.");
        using var output = new MemoryStream();
        input.CopyTo(output);
        byte[] bytes = output.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase) ||
            CharacterSavePosition.ReadIdentity(bytes).Name != "VTSeedClean" || !CharacterSavePosition.IsFreshSmokeSeed(bytes))
            throw new InvalidDataException("The packaged default character changed from its verified game save.");
        return bytes;
    }

    private static string FileHash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private static bool IsLink(string entry) => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0;
}
