using Valheim.Testing;

namespace Valheim.Testing.Game;

/// <summary>
/// Finds test sites on a world the game has just created, from the world generator's heights read through ValheimCLI
/// (<c>valheim.world/terrain-grid</c>, <see cref="TerrainCapture"/>), never from the mod under test. Grids are read in the
/// given order, each sample kept, until the mod's own rule (<c>choose</c>) finds what it needs; the rule decides what a
/// site is, the search only decides where to look and in what order.
/// </summary>
public static class SiteSearch
{
    /// <summary>Samples farther than this from the world's centre are never chosen (Valheim's world radius is 10 km).</summary>
    public const float WorldLimit = 10000;

    /// <summary>
    /// Reads each of <paramref name="grids"/> (generator layer only, all checked before anything is read) in order through
    /// <see cref="TerrainCapture.Read"/>, which refuses an incomplete or misplaced grid or a world that changed during it,
    /// and requires every grid to come from <paramref name="worldUid"/>, the world the caller pinned. After each grid it asks
    /// <paramref name="enough"/> with every sample read so far (with the generator's biome and river readings) and returns
    /// them once it says yes; grids after that are not read. Fails with <see cref="SiteNotFoundException"/>, naming how many
    /// samples were searched, when no grid is enough.
    /// </summary>
    public static IReadOnlyList<TerrainSample> Find(GameActor server, string worldUid, IReadOnlyList<TerrainGridRequest> grids, Func<IReadOnlyList<TerrainSample>, bool> enough)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldUid);
        ArgumentNullException.ThrowIfNull(enough);
        if (grids is not { Count: > 0 }) throw new ArgumentException("Name at least one grid to search.", nameof(grids));
        foreach (var grid in grids)
        {
            if (grid == null) throw new ArgumentException("A grid to search is missing.", nameof(grids));
            if (grid.Layer != "generator") throw new ArgumentException("A site search reads the world generator's heights (layer generator), not loaded ground.", nameof(grids));
            grid.Validate();
        }
        var samples = new List<TerrainSample>();
        foreach (var grid in grids)
        {
            var capture = TerrainCapture.Read(server, grid, "site search: world generator heights");
            if (capture.WorldUid != worldUid)
                throw new InvalidOperationException($"The terrain grid came from world {capture.WorldUid}, not the pinned world {worldUid}; no site was chosen.");
            samples.AddRange(capture.Samples);
            if (enough(samples.AsReadOnly())) return samples.ToArray();
        }
        throw new SiteNotFoundException($"No site matched among {samples.Count} generator samples in {grids.Count} grid(s).", samples.Count);
    }

    /// <summary>
    /// The sample closest to the world's centre that <paramref name="where"/> accepts, among finite samples within
    /// <see cref="WorldLimit"/> of the centre; null when none does. Ties go to the smaller x, then the smaller z, so the choice never
    /// depends on the order the samples were read in.
    /// </summary>
    public static TerrainSample? Nearest(IEnumerable<TerrainSample> samples, Func<TerrainSample, bool> where) =>
        samples.Where(s => float.IsFinite(s.X) && float.IsFinite(s.Z) && float.IsFinite(s.Height) && (double)s.X * s.X + (double)s.Z * s.Z <= (double)WorldLimit * WorldLimit)
            .OrderBy(s => (double)s.X * s.X + (double)s.Z * s.Z).ThenBy(s => s.X).ThenBy(s => s.Z).FirstOrDefault(where);

    /// <summary>
    /// A square generator grid of <paramref name="side"/> by <paramref name="side"/> samples <paramref name="spacing"/> metres
    /// apart, centred on (<paramref name="centreX"/>, <paramref name="centreZ"/>).
    /// </summary>
    public static TerrainGridRequest Around(float centreX, float centreZ, float spacing, int side)
    {
        float half = (side - 1) / 2f * spacing;
        var grid = new TerrainGridRequest(centreX - half, centreZ - half, spacing, side, side, "generator");
        grid.Validate();
        return grid;
    }
}

/// <summary><see cref="SiteSearch.Find"/> read every grid and none was enough; <see cref="Samples"/> says how many were searched.</summary>
public sealed class SiteNotFoundException(string message, int samples) : InvalidOperationException(message)
{
    public int Samples { get; } = samples;
}
