using System.Globalization;
using Valheim.Testing;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// SiteSearch against a scripted World Tools terrain grid: grids read in order and only until the rule is satisfied, the
// capture's own checks applied, and the nearest-to-centre choice independent of read order. No game.
public class SiteSearchTests
{
    // Generator ground 40 m everywhere, sea (10 m) east of x = 1000.
    private static ScriptedTransport Generator(bool complete = true, Func<int, int>? shiftX = null) => new ScriptedTransport().Extension("valheim.world", "terrain-grid", args =>
    {
        float x0 = float.Parse(args[0], CultureInfo.InvariantCulture), z0 = float.Parse(args[1], CultureInfo.InvariantCulture), step = float.Parse(args[2], CultureInfo.InvariantCulture);
        int nx = int.Parse(args[3], CultureInfo.InvariantCulture), nz = int.Parse(args[4], CultureInfo.InvariantCulture);
        var samples = Enumerable.Range(0, nx * nz).Select(i => (X: x0 + i % nx * step + (shiftX?.Invoke(i) ?? 0), Z: z0 + i / nx * step))
            .Select(p => new { complete = true, height = p.X > 1000 ? 10f : 40f, x = p.X, z = p.Z, biome = p.X > 1000 ? "Ocean" : "Meadows", riverWeight = 0f, riverWidth = 0f }).ToArray();
        return new
        {
            formatVersion = 1, source = "terrain-grid", complete, layer = args[5], units = "metres", consistency = "per-sample",
            originX = x0, originZ = z0, spacing = step, countX = nx, countZ = nz, worldUid = "4242", gameVersion = "fixture",
            gameAssemblyId = Guid.Empty.ToString(), worldGenVersion = 2, startedUtc = "2026-10-04T00:00:00Z", finishedUtc = "2026-10-04T00:00:01Z", samples,
        };
    });

    private static readonly TerrainGridRequest[] Grids = [SiteSearch.Around(0, 0, 32, 4), SiteSearch.Around(1024, 0, 256, 4), SiteSearch.Around(2048, 0, 256, 4)];
    private static bool HasSea(IReadOnlyList<TerrainSample> samples) => samples.Any(s => s.Height < 30);

    [Fact] public void GridsAreReadInOrderOnlyUntilTheRuleIsSatisfied()
    {
        var generator = Generator();
        var samples = SiteSearch.Find(generator.Actor(), "4242", Grids, HasSea);
        Assert.Equal(2, generator.Count("cli_extension valheim.world/terrain-grid")); // The second grid (x 640 to 1408) reaches the sea; the third is never read.
        Assert.Equal(32, samples.Count);
        Assert.Contains(samples, s => s.X == 1152 && s.Height == 10 && s.Biome == TerrainBiome.Ocean);
    }

    [Fact] public void NoSatisfyingGridFailsNamingTheSamplesSearched()
    {
        var generator = Generator();
        var error = Assert.Throws<SiteNotFoundException>(() => SiteSearch.Find(generator.Actor(), "4242", Grids, _ => false));
        Assert.Equal(48, error.Samples);
        Assert.Equal(3, generator.Count("cli_extension valheim.world/terrain-grid"));
    }

    // The capture's own checks hold: an incomplete grid, or a sample not where the grid puts it, is refused, not searched.
    [Fact] public void AnIncompleteOrMisplacedGridIsRefusedNotSearched()
    {
        bool asked = false;
        Assert.Throws<InvalidOperationException>(() => SiteSearch.Find(Generator(complete: false).Actor(), "4242", Grids, _ => asked = true));
        Assert.Contains("misplaced", Assert.Throws<InvalidDataException>(() => SiteSearch.Find(Generator(shiftX: i => i == 3 ? 1 : 0).Actor(), "4242", Grids, _ => asked = true)).Message);
        Assert.False(asked);
    }

    // The samples keep the generator's biome and river readings as captured; a missing reading is never a zero.
    [Fact] public void SamplesKeepTheCapturedRiverReadings()
    {
        var samples = SiteSearch.Find(Generator().Actor(), "4242", Grids, _ => true);
        Assert.All(samples, s => Assert.True(s.HasRiverSample));
        Assert.Equal(16, samples.Count);
    }

    // Every grid must come from the world the caller pinned; samples from another world are never mixed in.
    [Fact] public void AGridFromAnotherWorldIsRefused()
    {
        bool asked = false;
        Assert.Contains("not the pinned world 7", Assert.Throws<InvalidOperationException>(() => SiteSearch.Find(Generator().Actor(), "7", Grids, _ => asked = true)).Message);
        Assert.False(asked);
    }

    [Fact] public void OnlyGeneratorGridsAreSearched()
    {
        var generator = Generator();
        Assert.Throws<ArgumentException>(() => SiteSearch.Find(generator.Actor(), "4242", [new TerrainGridRequest(0, 0, 8, 2, 2, "loaded-ground")], _ => true));
        Assert.Throws<ArgumentException>(() => SiteSearch.Find(generator.Actor(), "4242", [], _ => true));
        // Every grid is checked before the first is read: a bad last grid costs no capture.
        Assert.Throws<ArgumentException>(() => SiteSearch.Find(generator.Actor(), "4242", [Grids[0], new TerrainGridRequest(0, 0, 300, 2, 2, "generator")], _ => true));
        Assert.Equal(0, generator.Count("cli_extension valheim.world/terrain-grid"));
    }

    [Fact] public void NearestIsClosestToTheCentreWithAnOrderIndependentTieBreak()
    {
        TerrainSample S(float x, float z, float h = 40) => new(x, z, h, TerrainBiome.Meadows);
        TerrainSample[] samples = [S(10, 0), S(0, -10), S(-10, 0), S(0, 10), S(5, 5, 10), S(20000, 0), S(float.NaN, 0), S(9000, 9000, 10)];
        foreach (var order in new[] { samples, samples.Reverse().ToArray() })
        {
            var nearest = SiteSearch.Nearest(order, s => s.Height > 30)!;
            Assert.Equal((-10f, 0f), (nearest.X, nearest.Z)); // Four at 10 m: the smaller x wins, whatever the order.
            Assert.Equal((5f, 5f), (SiteSearch.Nearest(order, s => s.Height < 30)!.X, SiteSearch.Nearest(order, s => s.Height < 30)!.Z));
        }
        // Outside the 10 km world radius (12.7 km at (9000, 9000)) or not finite: never chosen.
        Assert.Null(SiteSearch.Nearest([S(20000, 0), S(float.NaN, 0), S(9000, 9000)], _ => true));
    }

    [Fact] public void AroundCentresASquareGrid()
    {
        var grid = SiteSearch.Around(2048, -2048, 256, 16);
        Assert.Equal(new TerrainGridRequest(128, -3968, 256, 16, 16, "generator"), grid);
        Assert.Throws<ArgumentException>(() => SiteSearch.Around(0, 0, 32, 17)); // 289 samples: more than one grid may hold.
    }
}
