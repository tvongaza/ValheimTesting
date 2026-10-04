using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Valheim.Testing;

/// <summary>
/// One pinned ValheimCLI world-dump CSV and the facts recorded when it was captured. <c>WorldDump.CaptureAsync</c> in
/// Valheim.Testing.Game writes it as JSON beside the CSV after checking the game's reply against the file; nobody needs
/// to write one by hand. The CSV holds no world identity, so <see cref="WorldUid"/>, <see cref="Seed"/> and
/// <see cref="GameBuild"/> come from the same pinned session, and <see cref="Sha256"/> binds them to the exact file.
/// <see cref="Command"/> and <see cref="Reply"/> are kept as evidence of how the file was made; <see cref="Verify"/>
/// does not interpret them, because their grammar belongs to ValheimCLI.
/// </summary>
public sealed class WorldDumpManifest
{
    /// <summary>The native header, exactly: a dump with another layout is not a pinned native dump.</summary>
    public const string Header = "x,z,height,biome,river,river_width,base_height";
    /// <summary>ValheimCLI's dump lattice starts here on both axes, so every node is <c>-10000 + i * step</c>.</summary>
    public const float LatticeOrigin = -10000f;

    /// <summary>The layer's name; a layered reader selects the base-height layer by it and names it in errors.</summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The CSV file. The capture writes the file name only, relative to the manifest, and <c>WorldDump.ReadManifest</c>
    /// resolves it; if you read the JSON yourself, combine it with the manifest's directory, or a relative path resolves
    /// against the current directory.
    /// </summary>
    public string Path { get; set; } = "";
    public string WorldUid { get; set; } = "";
    public string Seed { get; set; } = "";
    public string GameBuild { get; set; } = "";
    /// <summary>The command that wrote the file, as sent (evidence only).</summary>
    public string Command { get; set; } = "";
    /// <summary>The game's complete reply to <see cref="Command"/> (evidence only).</summary>
    public string Reply { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int Step { get; set; }
    public float MinX { get; set; }
    public float MinZ { get; set; }
    public float MaxX { get; set; }
    public float MaxZ { get; set; }

    /// <summary>
    /// Loads <see cref="Path"/> only when it is the exact file this manifest describes: its SHA-256, the native header,
    /// the biome, river and base-height layers, a complete grid (no missing, repeated or ragged node), the recorded
    /// spacing and bounds, and nodes on ValheimCLI's lattice. The grid's provenance is <see cref="Name"/>.
    /// </summary>
    public GridDumpTerrain Verify()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("A dump manifest needs a layer name.");
        if (string.IsNullOrWhiteSpace(Path)) throw new InvalidDataException($"Dump '{Name}' names no CSV file.");
        if (string.IsNullOrWhiteSpace(WorldUid) || string.IsNullOrWhiteSpace(Seed) || string.IsNullOrWhiteSpace(GameBuild))
            throw new InvalidDataException($"Dump '{Name}' needs the world UID, seed and game build from its pinned native run.");
        if (string.IsNullOrWhiteSpace(Command) || string.IsNullOrWhiteSpace(Reply))
            throw new InvalidDataException($"Dump '{Name}' needs the recorded command and reply that made it.");
        if (Step < 5 || Step > 1000) throw new InvalidDataException($"Dump '{Name}': the step must be 5..1000 metres.");
        if (Sha256 == null || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException($"Dump '{Name}' needs a 64-digit SHA-256.");
        string file = System.IO.Path.GetFileName(Path);
        if (!Hash(Path).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{file}: SHA-256 differs from the pinned dump manifest.");
        using (var input = new StreamReader(Path))
            if (input.ReadLine()?.TrimEnd('\r') != Header)
                throw new InvalidDataException($"{file}: expected native header '{Header}'.");
        GridDumpTerrain grid;
        using (var input = new StreamReader(Path))
            grid = GridDumpTerrain.Read(input, Name); // also refuses missing, duplicate, ragged and non-finite cells
        if (!grid.HasBaseHeight || !grid.HasBiome || !grid.HasRiver)
            throw new InvalidDataException($"{file}: native biome, river and base_height layers are required.");
        if (grid.Spacing != Step || grid.OriginX != MinX || grid.OriginZ != MinZ || grid.MaxX != MaxX || grid.MaxZ != MaxZ)
            throw new InvalidDataException($"{file}: grid bounds or spacing differ from the pinned dump manifest.");
        OnLattice(grid.OriginX, "x");
        OnLattice(grid.OriginZ, "z");
        return grid;
    }

    /// <summary>Refuses two layers recorded as coming from different worlds or game builds.</summary>
    internal void RequireSameWorld(WorldDumpManifest other)
    {
        if (WorldUid != other.WorldUid || Seed != other.Seed || GameBuild != other.GameBuild)
            throw new InvalidDataException($"Dump layers '{Name}' and '{other.Name}' name different world UIDs, seeds or game builds.");
    }

    private void OnLattice(float coordinate, string axis)
    {
        double index = (coordinate - (double)LatticeOrigin) / Step;
        if (double.IsNaN(index) || double.IsInfinity(index) || Math.Abs(index - Math.Round(index)) > 1e-5)
            throw new InvalidDataException($"Dump '{Name}': {axis} origin {coordinate} is not on ValheimCLI's -10000 + i*{Step} lattice.");
    }

    private static string Hash(string path)
    {
        using (var input = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }
}
