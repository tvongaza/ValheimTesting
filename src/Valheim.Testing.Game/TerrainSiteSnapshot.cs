using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>A bounded, read-only observation of loaded terrain at an explicitly named site.</summary>
public sealed record TerrainSitePoint(int X, int Z);
public sealed record TerrainSiteReading(TerrainSitePoint Point, double GroundHeight, double TerrainSurfaceHeight,
    double Dirt, double Cultivated, double Paved, double ClearVegetation);
public sealed record TerrainSiteDelta(TerrainSitePoint Point, double GroundHeight, double TerrainSurfaceHeight,
    double Dirt, double Cultivated, double Paved, double ClearVegetation);

/// <summary>
/// Captures the loaded client ground, terrain collider and paint. A dedicated server can also be supplied to verify
/// the same world and area readiness, but paint and collider observations always come from the client. Raw command
/// replies are retained so a test report can be audited without trusting only the parsed values.
/// </summary>
public sealed record TerrainSiteSnapshot(string Site, string WorldUid, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc,
    IReadOnlyList<TerrainSiteReading> Readings, IReadOnlyList<ObservedCommand> Commands) : IEvidence
{
    public string Kind => "terrain-site";
    private static readonly Regex Surface = new(@"^SURFACE (?<x>-?\d+\.\d),(?<z>-?\d+\.\d) hit=(?<hit>\d+) name=(?<name>\S+) y=(?<height>-?\d+\.\d+) layer=\S+ zdo=\S+ trigger=(True|False)$", RegexOptions.CultureInvariant);
    private static readonly Regex SurfaceEnd = new(@"^OK: SURFACE_AT (?<x>-?\d+\.\d),(?<z>-?\d+\.\d) hits=(?<hits>\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Paint = new(@"^PAINT (?<x>-?\d+\.\d),(?<z>-?\d+\.\d) dirt=(?<dirt>\d+\.\d+) cultivated=(?<cultivated>\d+\.\d+) paved=(?<paved>\d+\.\d+) clearveg=(?<clearveg>\d+\.\d+) -> .+$", RegexOptions.CultureInvariant);

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
        var clock = Stopwatch.StartNew();
        foreach (var point in points)
        {
            SiteObservation.WaitAreaReady(client, "client", point.X, point.Z, readinessTimeout, clock, commands, cancellation);
            if (server is not null) SiteObservation.WaitAreaReady(server, "server", point.X, point.Z, readinessTimeout, clock, commands, cancellation);
        }
        var readings = new List<TerrainSiteReading>(points.Count);
        foreach (var point in points)
        {
            cancellation.ThrowIfCancellationRequested();
            double height = SiteObservation.GroundHeight(client, "client", point.X, point.Z, commands);
            var surface = SiteObservation.Lines(client, "client", $"cli_surface_at {point.X} {point.Z}", commands);
            double terrain = TerrainSurface(surface, point);
            var paint = SiteObservation.Lines(client, "client", $"cli_paint_at {point.X} {point.Z}", commands);
            var p = Only(paint, Paint, point);
            double dirt = Channel(p, "dirt"), cultivated = Channel(p, "cultivated"), paved = Channel(p, "paved"), clear = Channel(p, "clearveg");
            readings.Add(new(point, height, terrain, dirt, cultivated, paved, clear));
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
            b.GroundHeight - a.GroundHeight, b.TerrainSurfaceHeight - a.TerrainSurfaceHeight,
            b.Dirt - a.Dirt, b.Cultivated - a.Cultivated, b.Paved - a.Paved, b.ClearVegetation - a.ClearVegetation)).ToArray();
    }

    private static Match Only(IReadOnlyList<string> lines, Regex pattern, TerrainSitePoint point) => SiteObservation.One(lines, pattern, point.X, point.Z);

    private static double TerrainSurface(IReadOnlyList<string> lines, TerrainSitePoint point)
    {
        if (lines.Count < 2) throw new InvalidOperationException("Surface reply has no terrain hit and terminator.");
        var end = Only([lines[^1]], SurfaceEnd, point);
        if (!int.TryParse(end.Groups["hits"].Value, out int count) || count != lines.Count - 1 || count > 256)
            throw new InvalidOperationException("Surface hit count does not match the reply.");
        double? terrain = null;
        for (int i = 0; i < count; i++)
        {
            var match = Only([lines[i]], Surface, point);
            if (match.Groups["hit"].Value != (i + 1).ToString(CultureInfo.InvariantCulture)) throw new InvalidOperationException("Surface hits are not sequential.");
            if (match.Groups["name"].Value == "terrain") terrain ??= Number(match.Groups["height"].Value);
        }
        return terrain ?? throw new InvalidOperationException("Surface reply contains no terrain collider.");
    }

    private static double Channel(Match match, string name)
    {
        double value = Number(match.Groups[name].Value);
        if (value is < 0 or > 1) throw new InvalidOperationException("Paint channel is outside [0,1].");
        return value;
    }
    private static double Number(string text) => SiteObservation.Number(text);
}
