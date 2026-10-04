using System.Globalization;
using System.Text.Json;
using MyMod.SystemTests;
using Valheim.Testing;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

namespace MyMod.IntegrationTests;

/// <summary>
/// The server half alone (<c>dry-site-server</c>) and its unattended preparation: the scenario against scripted replies,
/// the plan rules, and how the sites are chosen from generator heights. Nothing here starts Valheim.
/// </summary>
public sealed class ServerOnlyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mymod-server-only-").FullName;
    private readonly TestWorld _world = new();
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private LifecyclePlan ServerPlan()
    {
        var plan = _world.Plan();
        plan.Scenario = LifecyclePlan.ServerScenario; plan.Client = null;
        return plan;
    }
    private ScenarioReport Run(LifecyclePlan plan)
    {
        var report = new ScenarioReport("mymod-system-test");
        try { DrySiteServerScenario.Run(plan, _world.Server(), _world.Restart, report); }
        catch (Exception) { Assert.False(report.Passed); }
        return report;
    }
    private static string[] Failed(ScenarioReport report) => report.Steps.Where(s => !s.Passed).Select(s => s.Name).ToArray();

    [Fact] public void TheServerHalfPassesWithoutAClient()
    {
        var report = Run(ServerPlan());
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Equal(2, _world.MarkCommands); // One per site, never repeated.
        Assert.Equal(1, _world.Restarts);
        Assert.Equal("after restart: the server still has one marker at the dry site, none at the wet site", report.Steps[^1].Name);
    }

    [Fact] public void AnUnconfirmedSaveFailsBeforeAnyRestart()
    {
        _world.ConfirmSaves = false;
        var report = Run(ServerPlan());
        Assert.Equal(new[] { "confirmed world save" }, Failed(report));
        Assert.Equal(0, _world.Restarts);
    }

    [Fact] public void AMarkWhoseReplyIsLostFailsAndIsNotRetried()
    {
        _world.LoseMarkReply = true;
        var report = Run(ServerPlan());
        Assert.Equal(new[] { "the mod marks the dry site" }, Failed(report));
        Assert.Equal(1, _world.MarkCommands);
    }

    [Fact] public void TheServerScenarioRefusesALifecyclePlan() =>
        Assert.Throws<ArgumentException>(() => DrySiteServerScenario.Run(_world.Plan(), _world.Server(), _world.Restart, new ScenarioReport("x")));

    // The plan file, as prepare-server writes it; the pinned sources need not exist to validate a plan.
    private string PlanFile(string scenario, bool client = false, bool arrival = false)
    {
        string hash = new('a', 64), md5 = new('b', 32);
        var plan = new Dictionary<string, object>
        {
            ["scenario"] = scenario,
            ["runtime"] = new { source = Path.Combine(_directory, "runtime"), sha256 = new Dictionary<string, string> { ["valheim_server.x86_64"] = hash } },
            ["world"] = new { source = Path.Combine(_directory, "world"), sha256 = new Dictionary<string, string> { ["adminlist.txt"] = hash } },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}" },
            ["pins"] = new Dictionary<string, string> { ["worlduid"] = "4242", ["valheimCLI.valheimCLI"] = md5, [LifecyclePlan.ModPlugin] = md5, [LifecyclePlan.AdapterPlugin] = md5 },
            ["runtimePins"] = new { game = new string('c', 64), bepinexCore = new string('d', 64), patchers = new string('e', 64) },
            ["drySite"] = new { x = 100, z = -40, ground = 42.5 },
            ["wetSite"] = new { x = 400, z = 300, ground = 22 },
        };
        if (arrival) plan["arrival"] = new { x = 105, z = -40, ground = 42.3 };
        if (client) plan["client"] = new { mode = "attach", port = 5556, join = "127.0.0.1:2456", character = "Tester", pins = new Dictionary<string, string> { ["valheimCLI.valheimCLI"] = md5, [LifecyclePlan.ModPlugin] = "absent" } };
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan));
        return path;
    }

    [Fact] public void AServerPlanNeedsNoArrivalAndRefusesAClient()
    {
        Assert.True(LifecyclePlan.ReadValidated(PlanFile(LifecyclePlan.ServerScenario)).ServerOnly);
        Assert.Throws<ArgumentException>(() => LifecyclePlan.ReadValidated(PlanFile(LifecyclePlan.ServerScenario, client: true)));
        // The full scenario still needs its arrival point; an unknown scenario is refused.
        Assert.Throws<ArgumentException>(() => LifecyclePlan.ReadValidated(PlanFile(LifecyclePlan.LifecycleScenario)));
        Assert.False(LifecyclePlan.ReadValidated(PlanFile(LifecyclePlan.LifecycleScenario, arrival: true)).ServerOnly);
        Assert.Throws<ArgumentException>(() => LifecyclePlan.ReadValidated(PlanFile("dry-site-client")));
    }

    [Fact] public void TheClosestSitesOutsideTheMarginsAreChosen()
    {
        TerrainSample[] samples =
        [
            Sample(0, 0, 33f),        // Dry by the rule, but within the margin: never chosen.
            Sample(10, 0, 30f),       // Wet by the rule, within the margin.
            Sample(0, 64, 34.5f),     // The closest dry site: at least 34.5 m includes 34.5.
            Sample(-64, 64, 40f),
            Sample(40, 60, 12f),      // Wet, but within 50 m of the dry site on both axes.
            Sample(-200, 0, 28.5f),   // The closest usable wet site: at most 28.5 m includes 28.5.
            Sample(300, 0, 5f),
        ];
        var (dry, wet) = ServerFixture.TryChooseSites(samples) ?? throw new InvalidOperationException("No sites chosen.");
        Assert.Equal((0f, 64f, 34.5f), (dry.X, dry.Z, dry.Ground));
        Assert.Equal((-200f, 0f, 28.5f), (wet.X, wet.Z, wet.Ground));
        Assert.Null(ServerFixture.TryChooseSites(samples.Where(s => s.Height > 29))); // No wet site left.
        Assert.Null(ServerFixture.TryChooseSites(samples.Where(s => s.Height < 34)));  // No dry site left.
    }

    private static TerrainSample Sample(float x, float z, float height) => new(x, z, height, TerrainBiome.Meadows);

    // A generator that is dry land (40 m) everywhere except the sea (10 m) east of x = 1000, answering as ValheimCLI's
    // World Tools does (the full capture TerrainCapture reads).
    private static ScriptedTransport Generator(bool complete = true) => new ScriptedTransport().Extension("valheim.world", "terrain-grid", args =>
    {
        float x0 = float.Parse(args[0], CultureInfo.InvariantCulture), z0 = float.Parse(args[1], CultureInfo.InvariantCulture), step = float.Parse(args[2], CultureInfo.InvariantCulture);
        int nx = int.Parse(args[3], CultureInfo.InvariantCulture), nz = int.Parse(args[4], CultureInfo.InvariantCulture);
        Assert.Equal("generator", args[5]);
        var samples = Enumerable.Range(0, nx * nz).Select(i => (X: x0 + i % nx * step, Z: z0 + i / nx * step))
            .Select(p => new { complete = true, height = p.X > 1000 ? 10f : 40f, x = p.X, z = p.Z, biome = p.X > 1000 ? "Ocean" : "Meadows", riverWeight = 0f, riverWidth = 0f }).ToArray();
        return new
        {
            formatVersion = 1, source = "terrain-grid", complete, layer = "generator", units = "metres", consistency = "per-sample",
            originX = x0, originZ = z0, spacing = step, countX = nx, countZ = nz, worldUid = "4242", gameVersion = "fixture",
            gameAssemblyId = Guid.Empty.ToString(), worldGenVersion = 2, startedUtc = "2026-10-04T00:00:00Z", finishedUtc = "2026-10-04T00:00:01Z", samples,
        };
    });

    [Fact] public void SitesComeFromTheGeneratorGridsAndTheSearchStopsOnceBothAreFound()
    {
        var generator = Generator();
        var (dry, wet) = ServerFixture.ChooseSites(generator.Actor(), "4242");
        Assert.Equal((-16f, -16f, 40f), (dry.X, dry.Z, dry.Ground)); // The first grid's samples closest to the centre; ties go to the smaller x, then z.
        Assert.Equal((1152f, -128f, 10f), (wet.X, wet.Z, wet.Ground)); // Only the third grid reaches the sea.
        Assert.Equal(3, generator.Count("cli_extension valheim.world/terrain-grid"));
    }

    [Fact] public void AnIncompleteGridIsRefusedNotSearched()
    {
        var generator = Generator(complete: false);
        Assert.Throws<InvalidOperationException>(() => ServerFixture.ChooseSites(generator.Actor(), "4242"));
        Assert.Equal(1, generator.Count("cli_extension valheim.world/terrain-grid"));
    }

    [Fact] public void ThePluginPinsCoverExactlyTheFivePlugins()
    {
        string plugins = Path.Combine(_directory, "runtime", "BepInEx", "plugins");
        Directory.CreateDirectory(Path.Combine(plugins, "packs"));
        foreach (var (_, file) in ServerFixture.Plugins) File.WriteAllText(Path.Combine(file.StartsWith("Valheim.Cli.", StringComparison.Ordinal) ? Path.Combine(plugins, "packs") : plugins, file), file);
        var pins = ServerFixture.PluginPins(Path.Combine(_directory, "runtime"));
        Assert.Equal(ServerFixture.Plugins.Select(p => p.Guid).Order(), pins.Keys.Order());
        Assert.Equal("7da90b1a4e66a95c85d282381d71330a", pins[LifecyclePlan.ModPlugin]); // MD5 of the file's bytes, "MyMod.dll".
        File.WriteAllText(Path.Combine(plugins, "Other.dll"), "other");
        Assert.Throws<InvalidOperationException>(() => ServerFixture.PluginPins(Path.Combine(_directory, "runtime")));
    }
}
