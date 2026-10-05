namespace Valheim.Testing.Game;

/// <summary>
/// A small, game-created Valheim 1.0.16 world for an owned smoke run. The server that created it had only ValheimCLI
/// core and its Standard and WorldTools packs. It is a starting point, not a synthetic terrain replacement: the game
/// still generates terrain and locations when the client enters it.
/// </summary>
/// <remarks>
/// <see cref="Prepare"/> writes only to a new directory. A run copies that pinned source through
/// <see cref="WorldFixture.Copy"/> or <see cref="HostedWorld.Place"/> and never edits the packaged source.
/// The save format belongs to Valheim; a later game version must pass a native load check before being claimed compatible.
/// </remarks>
public static class DefaultSmokeWorld
{
    public const string Name = "VTDefaultSmoke";
    public const string Uid = "3010640879";
    public const string SeedName = "UFpb0JbRZB";

    private const string ResourcePrefix = "Valheim.Testing.Game.SmokeWorld.";
    private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["00_00__0_1.chunk"] = "106626b95c8f5e57a6ea0f08fcdac28662c2f20a74de226074b467b400dfba7c",
        ["_main.1.chunks"] = "7dd633b96b6ebfad32069d7640cbc74277c9c323074d6b6065e4644065be917d",
        ["_main.1.db2"] = "538d45b6a88858247101c8b8d3d1bb82d4b7833e9213ac72663ed465e28feed8",
        ["_main.1.fwl2"] = "ceb8387d13d92e615d20978a7eaef7b86fff634d90fc4a819b78bce8d0217fbb",
        ["_main.1.ok"] = "4f2d892d6af406902c1aae6e58a78b2e6865fd4b782fc96b12de65d3bf3dc03e",
    };

    /// <summary>
    /// Materializes the pinned one-world fixture into <paramref name="newRoot"/>. The path must not exist, so this
    /// cannot silently replace a valued world or a previous run's evidence. Returns the UID read from the written
    /// world metadata, rather than trusting the folder name.
    /// </summary>
    public static WorldIdentity Prepare(string newRoot)
    {
        newRoot = Path.GetFullPath(newRoot);
        if (Path.Exists(newRoot)) throw new IOException("Use a new directory for the default fixture; an existing world or run is never overwritten.");
        string world = Path.Combine(newRoot, Name);
        Directory.CreateDirectory(world);
        try
        {
            foreach (var (name, hash) in Files)
            {
                using var source = typeof(DefaultSmokeWorld).Assembly.GetManifestResourceStream(ResourcePrefix + name)
                    ?? throw new InvalidDataException("The packaged smoke world is missing " + name);
                string target = Path.Combine(world, name);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
                if (!FileHash.Sha256(target).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The packaged smoke world has a changed file: " + name);
            }
            var identity = WorldIdentity.Read(newRoot);
            if (identity.Name != Name || identity.UidText != Uid || identity.SeedName != SeedName)
                throw new InvalidDataException("The packaged smoke world's metadata differs from its pinned identity.");
            return identity;
        }
        catch
        {
            Directory.Delete(newRoot, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// Creates the same pinned world in a dedicated server's <c>-savedir</c> layout,
    /// <c>worlds_local/VTDefaultSmoke/</c>, under a new root. It checks the embedded world through
    /// <see cref="Prepare"/> before moving its own files; no existing server save is changed.
    /// </summary>
    public static WorldIdentity PrepareServerSaveRoot(string newRoot)
    {
        newRoot = Path.GetFullPath(newRoot);
        if (Path.Exists(newRoot)) throw new IOException("Use a new directory for the dedicated smoke save; an existing save is never replaced.");
        Directory.CreateDirectory(newRoot);
        try
        {
            string temporary = Path.Combine(newRoot, "verified-fixture");
            WorldIdentity identity = Prepare(temporary);
            string local = Path.Combine(newRoot, "worlds_local");
            Directory.CreateDirectory(local);
            Directory.Move(Path.Combine(temporary, Name), Path.Combine(local, Name));
            Directory.Delete(temporary);
            return identity;
        }
        catch
        {
            Directory.Delete(newRoot, recursive: true);
            throw;
        }
    }
}
