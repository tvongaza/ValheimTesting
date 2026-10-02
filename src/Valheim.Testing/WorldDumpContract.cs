using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Valheim.Testing;

/// <summary>
/// Independently recorded facts about one ValheimCLI world dump. The CSV does not contain a world identity: callers must
/// obtain the UID and seed from a strictly pinned `cli_world` reply during the same run and keep that reply as evidence.
/// The SHA-256 binds those facts to the exact CSV that was fetched.
/// </summary>
public sealed class WorldDumpManifest
{
    public string WorldUid { get; set; } = "";
    public string Seed { get; set; } = "";
    public string GameBuild { get; set; } = "";
    public string Command { get; set; } = "";
    public string Reply { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int Step { get; set; }
    public float MinX { get; set; }
    public float MinZ { get; set; }
    public float MaxX { get; set; }
    public float MaxZ { get; set; }
}

/// <summary>Checks a pinned `cli_world_dump` file, its reply and its declared world identity before offline use.</summary>
public static class WorldDumpContract
{
    public const string Header = "x,z,height,biome,river,river_width,base_height";
    public const float LatticeOrigin = -10000f;

    /// <summary>Loads only an exact, complete, correctly spaced dump whose command reply and SHA-256 agree with the manifest.</summary>
    public static GridDumpTerrain Verify(string path, WorldDumpManifest manifest)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        if (string.IsNullOrWhiteSpace(manifest.WorldUid) || string.IsNullOrWhiteSpace(manifest.Seed) ||
            string.IsNullOrWhiteSpace(manifest.GameBuild))
            throw new InvalidDataException("A dump needs world UID, seed and game build from the pinned native run.");
        string[] command = manifest.Command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (command.Length < 2 || command[0] != "cli_world_dump" ||
            !int.TryParse(command[1], NumberStyles.None, CultureInfo.InvariantCulture, out int requestedStep) ||
            requestedStep != manifest.Step ||
            !manifest.Reply.StartsWith("OK: WORLD_DUMP ", StringComparison.Ordinal))
            throw new InvalidDataException("A dump needs an explicit cli_world_dump step matching the manifest and a successful complete reply.");
        if (manifest.Step < 5 || manifest.Step > 1000)
            throw new InvalidDataException("The dump step must be 5..1000 metres.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("A dump needs a 64-digit SHA-256.");
        string actual = Hash(path);
        if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(path)}: SHA-256 differs from the pinned dump manifest.");
        using (var input = new StreamReader(path))
            if (input.ReadLine()?.TrimEnd('\r') != Header)
                throw new InvalidDataException($"{Path.GetFileName(path)}: expected native header '{Header}'.");

        var grid = GridDumpTerrain.Load(path); // also refuses missing, duplicate, ragged and non-finite cells
        if (!grid.HasBaseHeight || !grid.HasBiome || !grid.HasRiver)
            throw new InvalidDataException($"{Path.GetFileName(path)}: native biome, river and base_height layers are required.");
        if (grid.Spacing != manifest.Step || grid.OriginX != manifest.MinX || grid.OriginZ != manifest.MinZ ||
            grid.MaxX != manifest.MaxX || grid.MaxZ != manifest.MaxZ)
            throw new InvalidDataException($"{Path.GetFileName(path)}: grid bounds or spacing differ from the pinned dump manifest.");
        OnLattice(grid.OriginX, manifest.Step, "x");
        OnLattice(grid.OriginZ, manifest.Step, "z");
        long samples = (long)grid.CountX * grid.CountZ;
        if (!ReplyValue(manifest.Reply, "samples").Equals(samples.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            !ReplyValue(manifest.Reply, "step").Equals(manifest.Step.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new InvalidDataException($"{Path.GetFileName(path)}: samples or step differ from the cli_world_dump reply.");
        if (string.IsNullOrWhiteSpace(ReplyValue(manifest.Reply, "world")))
            throw new InvalidDataException("cli_world_dump reply has no output file path.");
        bool windowCommand = command.Contains("--window");
        bool windowReply = OptionalReplyValue(manifest.Reply, "window") != null;
        if (windowCommand != windowReply)
            throw new InvalidDataException("cli_world_dump command and reply disagree about the window.");
        string? extent = OptionalReplyValue(manifest.Reply, "extent");
        if (windowCommand && extent == null)
            throw new InvalidDataException("Windowed cli_world_dump reply lacks extent=.");
        string expectedExtent = string.Format(CultureInfo.InvariantCulture, "{0:F0},{1:F0}..{2:F0},{3:F0}",
            grid.OriginX, grid.OriginZ, grid.MaxX, grid.MaxZ);
        if (extent != null && extent != expectedExtent)
            throw new InvalidDataException($"{Path.GetFileName(path)}: extent differs from the cli_world_dump reply.");
        return grid;
    }

    /// <summary>Refuses two pinned layers recorded as coming from different worlds or game builds.</summary>
    public static void RequireSameWorld(WorldDumpManifest first, WorldDumpManifest second)
    {
        if (first == null || second == null) throw new ArgumentNullException(first == null ? nameof(first) : nameof(second));
        if (first.WorldUid != second.WorldUid || first.Seed != second.Seed || first.GameBuild != second.GameBuild)
            throw new InvalidDataException("World dump layers name different world UIDs, seeds or game builds.");
    }

    private static void OnLattice(float coordinate, int step, string axis)
    {
        double index = (coordinate - (double)LatticeOrigin) / step;
        if (double.IsNaN(index) || double.IsInfinity(index) || Math.Abs(index - Math.Round(index)) > 1e-5)
            throw new InvalidDataException($"Dump {axis} origin {coordinate} is not on ValheimCLI's -10000 + i*{step} lattice.");
    }

    private static string ReplyValue(string reply, string key) =>
        OptionalReplyValue(reply, key) ?? throw new InvalidDataException($"cli_world_dump reply lacks {key}=.");

    private static string? OptionalReplyValue(string reply, string key)
    {
        foreach (string token in reply.Split(' '))
            if (token.StartsWith(key + "=", StringComparison.Ordinal)) return token.Substring(key.Length + 1);
        return null;
    }

    private static string Hash(string path)
    {
        using (var input = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }
}
