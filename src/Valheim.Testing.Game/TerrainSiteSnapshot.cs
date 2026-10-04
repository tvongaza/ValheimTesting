using System.Globalization;

namespace Valheim.Testing.Game;

/// <summary>A bounded, read-only observation of loaded terrain at an explicitly named site.</summary>
public sealed record TerrainSitePoint(int X, int Z);
/// <summary>
/// One point's loaded layers, read through the same capabilities as the probes: the ground height
/// (<c>valheim.world/terrain</c>, <c>loaded-ground</c>, as <see cref="TerrainProbe"/>), the terrain vertex's own collider
/// height (<c>valheim.world/terrain-surface</c>, as <see cref="SurfaceProbe"/>) and the raw paint texel
/// (<c>valheim.world/terrain-paint</c>, as <see cref="PaintProbe"/>: R dirt, G cultivated, B paved, A vegetation: 1 where it may grow, 0 where cleared).
/// </summary>
public sealed record TerrainSiteReading(TerrainSitePoint Point, float GroundHeight, float ColliderHeight, float R, float G, float B, float A);
public sealed record TerrainSiteDelta(TerrainSitePoint Point, double GroundHeight, double ColliderHeight, double R, double G, double B, double A);

/// <summary>
/// Captures the loaded client ground, terrain collider and paint. A dedicated server can also be supplied to verify
/// the same world and area readiness, but paint and collider observations always come from the client. Every command
/// and its exact reply is retained so a test report can be audited without trusting only the parsed values. A snapshot
/// is the probes' readings without expectations.
/// </summary>
public sealed record TerrainSiteSnapshot(string Site, string WorldUid, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc,
    IReadOnlyList<TerrainSiteReading> Readings, IReadOnlyList<ObservedCommand> Commands) : IEvidence
{
    public string Kind => "terrain-site";
    private static readonly string[] Layers = ["valheim.world/terrain", "valheim.world/terrain-surface", "valheim.world/terrain-paint"];

    /// <summary>Wait for every point to be ready, then capture raw replies and parsed values. No game state is changed.</summary>
    public static TerrainSiteSnapshot Capture(GameActor client, string site, string worldUid, IReadOnlyList<TerrainSitePoint> points,
        TimeSpan readinessTimeout, GameActor? server = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(site) || site.Length > 80 || site.Any(char.IsControl)) throw new ArgumentException("Give a short site name.", nameof(site));
        if (!long.TryParse(worldUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) throw new ArgumentException("Give the expected numeric world UID.", nameof(worldUid));
        if (points is null || points.Count is < 1 or > 16 || points.Distinct().Count() != points.Count ||
            points.Any(p => Math.Abs((long)p.X) > 20000 || Math.Abs((long)p.Z) > 20000))
            throw new ArgumentException("Supply 1..16 distinct points inside the supported world range.", nameof(points));
        if (readinessTimeout <= TimeSpan.Zero || readinessTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(readinessTimeout), "Use a positive timeout of at most two minutes.");
        if (!client.Pinned || server is not null && !server.Pinned) throw new InvalidOperationException("Terrain snapshots require strict environment pins on every actor.");

        var started = DateTimeOffset.UtcNow;
        var commands = new List<ObservedCommand>();
        SiteObservation.CheckWorld(client, worldUid, "client");
        if (server is not null) SiteObservation.CheckWorld(server, worldUid, "server");
        var layers = client.RequireCapabilities(Layers); // One listing.
        Capability ground = layers[0], surface = layers[1], paint = layers[2];
        // Point by point, the client's area then the server's.
        SiteObservation.WaitAreasReady([.. points.SelectMany(point => server is null ? [(client, "client", point.X, point.Z)]
            : new[] { (client, "client", point.X, point.Z), (server, "server", point.X, point.Z) })], readinessTimeout, commands, cancellation);
        var readings = new List<TerrainSiteReading>(points.Count);
        foreach (var point in points)
        {
            cancellation.ThrowIfCancellationRequested();
            string x = point.X.ToString(CultureInfo.InvariantCulture), z = point.Z.ToString(CultureInfo.InvariantCulture);
            float height = TerrainProbe.Height(SiteObservation.Observe(client, "client", ground, commands, x, z, "loaded-ground"), point.X, point.Z, "loaded-ground");
            var (_, collider) = SurfaceProbe.Observed(SiteObservation.Observe(client, "client", surface, commands, x, z), point.X, point.Z);
            var texel = PaintProbe.Observed(SiteObservation.Observe(client, "client", paint, commands, x, z), point.X, point.Z);
            readings.Add(new(point, height, collider, texel.R, texel.G, texel.B, texel.A));
        }
        SiteObservation.CheckWorld(client, worldUid, "client");
        if (server is not null) SiteObservation.CheckWorld(server, worldUid, "server");
        return new(site, worldUid, started, DateTimeOffset.UtcNow, readings, commands);
    }

    /// <summary>Compare the same site's readings across two captures, excluding timestamps and raw command text.</summary>
    public static IReadOnlyList<TerrainSiteDelta> Compare(TerrainSiteSnapshot before, TerrainSiteSnapshot after)
    {
        if (before.Site != after.Site || before.WorldUid != after.WorldUid ||
            !before.Readings.Select(r => r.Point).SequenceEqual(after.Readings.Select(r => r.Point)))
            throw new InvalidOperationException("Terrain snapshots must name the same site, world and ordered points.");
        return before.Readings.Zip(after.Readings, (a, b) => new TerrainSiteDelta(a.Point,
            (double)b.GroundHeight - a.GroundHeight, (double)b.ColliderHeight - a.ColliderHeight,
            (double)b.R - a.R, (double)b.G - a.G, (double)b.B - a.B, (double)b.A - a.A)).ToArray();
    }
}
