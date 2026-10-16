using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing;
using Valheim.Testing.Game;
using valheimCLI;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>
/// Prepares a <c>dry-site-server</c> plan on a world the game creates now, so the server scenario can run where nobody
/// prepared a fixture by hand, such as a scheduled CI job: <c>prepare-server [--game-port PORT] &lt;server-runtime&gt; &lt;new-output-directory&gt;</c>.
/// <list type="number">
/// <item>Pins the runtime by the SHA256 of every file, its game build, loader and patchers (<c>InstallPins</c>), and each required
/// plugin in its <c>BepInEx/plugins</c> by the MD5 of their files: the default ValheimCLI plugin, the mod and its adapter.</item>
/// <item>Runs one owned server through the toolkit's pinned runner (<see cref="PinnedServerRun"/>, as a standalone run on this
/// machine: a disposable profile of the runtime, which is never changed), and the game creates a new world. Its uid is not
/// known yet, so this one boot pins the plugins strictly and accepts any world. The runner's evidence is <c>run/</c>.</item>
/// <item>Reads the world's uid (<c>cli_world</c>) and picks the sites from the world generator's heights, read through
/// ValheimCLI (<c>valheim.world/terrain-grid</c>) and never from the mod: the dry site at least <see cref="Margin"/> above
/// the rule's 31.5 m threshold, the wet one at least as far below it, each the closest such sample to the world's centre.</item>
/// <item>Confirms a save; the runner stops only that server and fetches the world (<c>run/host-world</c>), copied to <c>world/</c>. Writes <c>plan.json</c>:
/// the runtime and the new world pinned by hash, the world uid, the plugins and the sites. <c>run plan.json
/// &lt;new-output-directory&gt;</c> then checks it like any plan.</item>
/// </list>
/// The runtime's ValheimCLI must listen on <see cref="CliPort"/> (<c>[Server] Port</c> in its configuration). The output
/// holds <c>result.json</c>, <c>junit.xml</c>, the runner's evidence (<c>run/</c>: the boot's logs and recorded commands), the
/// world and the plan. The plan holds the server's generated throwaway password: publish the reports and logs, not the plan or the world.
/// </summary>
public static class ServerFixture
{
    public const string Mode = "prepare-server";
    public const string WorldName = "AcceptanceCheck";
    public const int CliPort = 5577, StartupSeconds = 600, CommandSeconds = 60;
    /// <summary>How far above or below the rule's threshold a site's generator ground must be, so rounding never decides.</summary>
    public const float Margin = 3f;
    public const float DryAtLeast = LifecyclePlan.WaterLevel + LifecyclePlan.Clearance + Margin;
    public const float WetAtMost = LifecyclePlan.WaterLevel + LifecyclePlan.Clearance - Margin;
    /// <summary>The plugins the plan pins, by GUID and file name. Any other plugin file in the runtime is refused.</summary>
    public static readonly IReadOnlyList<(string Guid, string File)> Plugins =
    [
        ("valheimCLI.valheimCLI", "valheimCLI.dll"),
        (LifecyclePlan.ModPlugin, "AcceptanceMod.dll"), (LifecyclePlan.AdapterPlugin, "AcceptanceMod.Adapter.dll"),
    ];
    /// <summary>Generator grids of 16 by 16 samples (centre and spacing in metres), read in order until both sites are found.</summary>
    public static readonly IReadOnlyList<TerrainGridRequest> SearchGrids =
        [.. new (float X, float Z, float Spacing)[] { (0, 0, 32), (0, 0, 96), (0, 0, 256), (2048, 2048, 256), (-2048, 2048, 256), (-2048, -2048, 256), (2048, -2048, 256) }
            .Select(grid => SiteSearch.Around(grid.X, grid.Z, grid.Spacing, 16))];

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryArguments(args, out int gamePort, out string? runtimeInput, out string? outputInput))
        {
            Console.Error.WriteLine($"Usage: Valheim.Testing.NativeAcceptance {Mode} [--game-port 1024..65533] <server-runtime> <new-output-directory>");
            return 2;
        }
        var report = new ScenarioReport("acceptancemod-server-fixture");
        report.Provenance["mode"] = Mode;
        string runtime = Path.GetFullPath(runtimeInput!), output = Path.GetFullPath(outputInput!);
        bool ownOutput = false;
        try
        {
            if (Path.Exists(output)) throw new IOException("Use a new output directory; existing evidence is never overwritten.");
            if (IsInside(output, runtime)) throw new ArgumentException("The output must be outside the runtime.");
            var platform = GameLaunch.DetectServer(runtime);
            ServerRunPlan.CheckLaunchHost(platform, GameLaunch.LocalServerPlatform);
            // A macOS server's run places a world fixture in its default worlds (the server ignores -savedir for its lists), so
            // it cannot start on a world the game is still to create.
            if (platform == ServerPlatform.MacOS)
                throw new ArgumentException("prepare-server creates a new world on a Windows or Linux dedicated server; a macOS server runs only an existing world fixture.");
            report.Provenance["serverPlatform"] = platform.ToString();
            report.Provenance["steamBuildId"] = SteamBuildId(runtime);
            Directory.CreateDirectory(output); ownOutput = true;

            IReadOnlyDictionary<string, string> runtimeHashes = null!;
            Dictionary<string, string> pins = [];
            report.Step(StepPhase.Preflight, $"pin the runtime and its {Plugins.Count} plugins", () => { runtimeHashes = WorldFixture.Manifest(runtime); pins = PluginPins(runtime); });
            report.Provenance["pins"] = string.Join(" ", pins.Select(pin => pin.Key + "=" + pin.Value));

            // A throwaway password for a server that is never listed (-public 0) and whose port nothing publishes.
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            string[] arguments = ["-batchmode", "-nographics", "-name", WorldName, "-port", gamePort.ToString(CultureInfo.InvariantCulture), "-world", WorldName, "-password", password,
                "-public", "0", "-savedir", "{world}", "-logFile", "{runtime}/toolkit-unity.log"];
            // The world folder the game creates its world in: empty but for a note, as a strictly pinned world fixture is never empty.
            string newWorld = Path.Combine(output, "new-world");
            Directory.CreateDirectory(newWorld);
            File.WriteAllText(Path.Combine(newWorld, "NEW-WORLD.txt"), $"The game creates {WorldName} here.\n");
            string preparation = Path.Combine(output, "preparation-plan.json");
            // Strict: every loaded plugin must be one of the pinned builds. The world is the one the game is creating now.
            File.WriteAllText(preparation, JsonSerializer.Serialize(new
            {
                scenario = Mode,
                runtime = new { source = runtime, sha256 = runtimeHashes },
                runtimePins = InstallPins.Of(runtime),
                world = new { source = newWorld, sha256 = WorldFixture.Manifest(newWorld) },
                executable = ServerRunPlan.ExecutableFor(platform),
                arguments,
                environment = new Dictionary<string, string>(),
                pins = new Dictionary<string, string>(pins) { ["world"] = "any" },
                port = CliPort,
                startupSeconds = StartupSeconds,
                commandSeconds = CommandSeconds,
            }, new JsonSerializerOptions { WriteIndented = true }));

            WorldFacts facts = new();
            (Site Dry, Site Wet) sites = (new(), new());
            // The actor establishes test access on this boot (and would on any restart).
            var options = new PinnedServerRunOptions<ServerRunPlan>
            {
                Name = "acceptancemod-server-preparation",
                ReadPlan = ServerRunPlan.Read<ServerRunPlan>,
                Mod = new("acceptancemod.testing/session", LifecyclePlan.SessionTokenVariable),
                Scenario = (session, _) =>
                {
                    var server = session.Server!.Game;
                    session.Report.Step("read the new world's uid", () => facts = ReadWorld(server));
                    session.Report.Step("choose the sites from the world generator's heights", () => sites = ChooseSites(server, facts.Uid));
                    session.Report.Step("confirmed world save", () => server.SaveConfirmed());
                    return Task.CompletedTask;
                },
            };
            string run = Path.Combine(output, "run");
            int code = await PinnedServerRun.MainAsync(["run", preparation, run], options).ConfigureAwait(false);
            report.Step(StepPhase.Setup, "start the owned server on a new world, plugins pinned, with test access (run/)", () =>
            {
                if (code != 0) throw new InvalidOperationException($"The preparation run did not pass (exit {code}); see {Path.Combine(run, "result.json")}.");
            });
            report.Provenance["worldUid"] = facts.Uid; report.Provenance["worldSeed"] = facts.Seed;
            report.Provenance["drySite"] = Describe(sites.Dry); report.Provenance["wetSite"] = Describe(sites.Wet);
            // The world the game created and saved, fetched from the server's run directory once it stopped: the plan's fixture is
            // a copy of it without the shipment's own SOURCE.txt (a later run's ship writes it again) or this preparation's note.
            string fetched = Path.Combine(run, "host-world"), world = Path.Combine(output, "world");
            string planPath = Path.Combine(output, "plan.json");
            report.Step("write and validate the plan", () =>
            {
                if (!Directory.Exists(Path.Combine(fetched, "worlds_local"))) throw new DirectoryNotFoundException($"The preparation run fetched no saved world into {fetched}.");
                foreach (string file in Directory.EnumerateFiles(fetched, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(fetched, file);
                    if (relative is "SOURCE.txt" or "NEW-WORLD.txt") continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(world, relative))!);
                    File.Copy(file, Path.Combine(world, relative));
                }
                WritePlan(planPath, runtime, runtimeHashes, world, platform, arguments, pins, facts.Uid, sites.Dry, sites.Wet);
                LifecyclePlan.ReadValidated(planPath);
            });
            report.Provenance["planSha256"] = FileHash.Sha256(planPath);
        }
        catch (Exception error)
        {
            report.RecordFailure("preparation failed", error);
            Console.Error.WriteLine(error.Message);
        }
        finally
        {
            if (ownOutput) report.Write(output);
        }
        Console.WriteLine(report.Passed ? "PREPARED (a new world and its dry-site-server plan; not an acceptance test)" : "FAIL");
        return report.Passed ? 0 : 1;
    }

    /// <summary>Read a fresh game port before any runtime or output path is touched.</summary>
    public static bool TryArguments(string[] args, out int gamePort, out string? runtime, out string? output)
    {
        gamePort = 2456; runtime = null; output = null;
        if (args is [Mode, var source, var destination]) { runtime = source; output = destination; return true; }
        if (args is [Mode, "--game-port", var text, var sourceWithPort, var destinationWithPort] &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) &&
            parsed is >= 1024 and <= 65533 && parsed != CliPort)
        {
            gamePort = parsed; runtime = sourceWithPort; output = destinationWithPort; return true;
        }
        return false;
    }

    /// <summary>The MD5 of each of <see cref="Plugins"/> under the runtime's <c>BepInEx/plugins</c>; refuses a missing, duplicate or extra plugin file.</summary>
    public static Dictionary<string, string> PluginPins(string runtime)
    {
        string directory = Path.Combine(runtime, "BepInEx", "plugins");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The runtime has no BepInEx/plugins directory.");
        string[] files = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories);
        var unknown = files.Where(file => !Plugins.Any(plugin => Path.GetFileName(file) == plugin.File)).Select(file => Path.GetRelativePath(runtime, file)).ToArray();
        if (unknown.Length != 0) throw new InvalidOperationException($"Unexpected plugin files; the plan pins exactly {Plugins.Count} plugins: " + string.Join(", ", unknown));
        var pins = new Dictionary<string, string>();
        foreach (var (guid, name) in Plugins)
        {
            var matches = files.Where(file => Path.GetFileName(file) == name).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"Expected one {name} under BepInEx/plugins for {guid}; found {matches.Length}.");
            pins[guid] = FileHash.Md5(matches[0]);
        }
        return pins;
    }

    /// <summary>The loaded world, from exactly one <c>cli_world</c> line with an integer uid.</summary>
    public static WorldFacts ReadWorld(GameActor server)
    {
        var worlds = server.Execute("cli_world").Output.Select(Expectations.ParseWorld).OfType<WorldFacts>().ToArray();
        if (worlds.Length != 1 || !long.TryParse(worlds[0].Uid, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("cli_world did not report exactly one loaded world with a uid.");
        return worlds[0];
    }

    /// <summary>Searches <see cref="SearchGrids"/> in order (<see cref="SiteSearch.Find"/>) until <see cref="TryChooseSites"/> finds both sites.</summary>
    public static (Site Dry, Site Wet) ChooseSites(GameActor server, string worldUid)
    {
        try { return TryChooseSites(SiteSearch.Find(server, worldUid, SearchGrids, samples => TryChooseSites(samples) != null))!.Value; }
        catch (SiteNotFoundException error)
        {
            throw new InvalidOperationException($"No dry site (generator ground at least {DryAtLeast} m) with a wet site (at most {WetAtMost} m) 50 m from it among {error.Samples} generator samples within about 4 km of the world's centre.", error);
        }
    }

    /// <summary>
    /// AcceptanceMod's rule for the two sites, on the samples closest to the world's centre (<see cref="SiteSearch.Nearest"/>). The dry
    /// site: ground at least <see cref="DryAtLeast"/>. The wet site: ground at most <see cref="WetAtMost"/> and not within 50 m
    /// of the dry site on both axes (the plan's own rule). Null until both exist.
    /// </summary>
    public static (Site Dry, Site Wet)? TryChooseSites(IEnumerable<TerrainSample> samples)
    {
        var all = samples as IReadOnlyCollection<TerrainSample> ?? samples.ToList();
        var dry = SiteSearch.Nearest(all, s => s.Height >= DryAtLeast && s.Height <= 1000);
        if (dry == null) return null;
        var wet = SiteSearch.Nearest(all, s => s.Height <= WetAtMost && s.Height >= -100 && (MathF.Abs(s.X - dry.X) >= 50 || MathF.Abs(s.Z - dry.Z) >= 50));
        if (wet == null) return null;
        return (ToSite(dry), ToSite(wet));
    }

    private static void WritePlan(string path, string runtime, IReadOnlyDictionary<string, string> runtimeHashes, string world, ServerPlatform platform,
        string[] arguments, Dictionary<string, string> pins, string worldUid, Site dry, Site wet)
    {
        var document = new
        {
            scenario = LifecyclePlan.ServerScenario,
            runtime = new { source = runtime, sha256 = runtimeHashes },
            // Strict pins name the runtime's game build, loader and patchers as found in the runtime being pinned.
            runtimePins = InstallPins.Of(runtime),
            world = new { source = world, sha256 = WorldFixture.Manifest(world) },
            executable = ServerRunPlan.ExecutableFor(platform),
            arguments,
            environment = new Dictionary<string, string>(),
            pins = new Dictionary<string, string>(pins) { ["worlduid"] = worldUid },
            port = CliPort,
            startupSeconds = StartupSeconds,
            commandSeconds = CommandSeconds,
            drySite = new { x = dry.X, z = dry.Z, ground = dry.Ground },
            wetSite = new { x = wet.X, z = wet.Z, ground = wet.Ground },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Site ToSite(TerrainSample sample) => new() { X = sample.X, Z = sample.Z, Ground = MathF.Round(sample.Height, 2) };
    private static string Describe(Site site) => string.Create(CultureInfo.InvariantCulture, $"x={site.X} z={site.Z} ground={site.Ground}");
    private static string SteamBuildId(string runtime)
    {
        string manifest = Path.Combine(runtime, "steamapps", "appmanifest_896660.acf");
        var match = File.Exists(manifest) ? Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(\\d+)\"") : Match.Empty;
        return match.Success ? match.Groups[1].Value : "unknown";
    }
    private static bool IsInside(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string root = Path.TrimEndingDirectorySeparator(directory);
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
