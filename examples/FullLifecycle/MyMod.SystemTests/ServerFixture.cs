using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing;
using Valheim.Testing.Game;
using valheimCLI;

namespace MyMod.SystemTests;

/// <summary>
/// Prepares a <c>dry-site-server</c> plan on a world the game creates now, so the server scenario can run where nobody
/// prepared a fixture by hand, such as a scheduled CI job: <c>prepare-server &lt;server-runtime&gt; &lt;new-output-directory&gt;</c>.
/// <list type="number">
/// <item>Pins the runtime by the SHA256 of every file, and exactly five plugins in its <c>BepInEx/plugins</c> by the MD5 of
/// their files: ValheimCLI with its Standard and WorldTools packs, the mod and its adapter.</item>
/// <item>Starts one owned server on a copy of the runtime (the source is never launched), and the game creates a new world
/// in <c>world/</c>. Its uid is not known yet, so this one boot pins the plugins strictly and accepts any world.</item>
/// <item>Reads the world's uid (<c>cli_world</c>) and picks the sites from the world generator's heights, read through
/// ValheimCLI (<c>valheim.world/terrain-grid</c>) and never from the mod: the dry site at least <see cref="Margin"/> above
/// the rule's 31.5 m threshold, the wet one at least as far below it, each the closest such sample to the world's centre.</item>
/// <item>Confirms a save, stops only that server and writes <c>plan.json</c>: the runtime and the new world pinned by hash,
/// the world uid, the plugins and the sites. <c>run plan.json &lt;new-output-directory&gt;</c> then checks it like any plan.</item>
/// </list>
/// The runtime's ValheimCLI must listen on <see cref="CliPort"/> (<c>[Server] Port</c> in its configuration). The output
/// holds <c>result.json</c>, <c>junit.xml</c>, the boot's logs and recorded commands, the world and the plan. The plan holds
/// the server's generated throwaway password: publish the reports and logs, not the plan or the world.
/// </summary>
public static class ServerFixture
{
    public const string Mode = "prepare-server";
    public const string WorldName = "MyModServerCheck";
    public const int CliPort = 5577, StartupSeconds = 600, CommandSeconds = 60;
    /// <summary>How far above or below the rule's threshold a site's generator ground must be, so rounding never decides.</summary>
    public const float Margin = 3f;
    public const float DryAtLeast = LifecyclePlan.WaterLevel + LifecyclePlan.Clearance + Margin;
    public const float WetAtMost = LifecyclePlan.WaterLevel + LifecyclePlan.Clearance - Margin;
    /// <summary>The plugins the plan pins, by GUID and file name. Any other plugin file in the runtime is refused.</summary>
    public static readonly IReadOnlyList<(string Guid, string File)> Plugins =
    [
        ("valheimCLI.valheimCLI", "valheimCLI.dll"), ("valheimCLI.standard", "Valheim.Cli.Standard.dll"), ("valheimCLI.worldtools", "Valheim.Cli.WorldTools.dll"),
        (LifecyclePlan.ModPlugin, "MyMod.dll"), (LifecyclePlan.AdapterPlugin, "MyMod.TestAdapter.dll"),
    ];
    /// <summary>Generator grids of 16 by 16 samples (centre and spacing in metres), read in order until both sites are found.</summary>
    public static readonly IReadOnlyList<TerrainGridRequest> SearchGrids =
        [.. new (float X, float Z, float Spacing)[] { (0, 0, 32), (0, 0, 96), (0, 0, 256), (2048, 2048, 256), (-2048, 2048, 256), (-2048, -2048, 256), (2048, -2048, 256) }
            .Select(grid => SiteSearch.Around(grid.X, grid.Z, grid.Spacing, 16))];

    public static int Run(string[] args)
    {
        if (args.Length != 3 || args[0] != Mode)
        {
            Console.Error.WriteLine($"Usage: mymod-system-test {Mode} <server-runtime> <new-output-directory>");
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
        var report = new ScenarioReport("mymod-server-fixture");
        report.Provenance["mode"] = Mode;
        string runtime = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        bool ownOutput = false;
        OwnedServerSession? session = null;
        WorldFixture? copy = null;
        // Stops only the process this preparation started. A copy whose server may still run is kept, never deleted.
        void StopOwned()
        {
            if (session == null) return;
            var owned = session; session = null;
            report.Provenance["ownedPids"] = string.Join(",", owned.StartedProcesses);
            try { report.Step(StepPhase.Cleanup, "stop only the owned server", owned.Dispose); }
            catch { if (copy != null) copy.Preserve = true; throw; }
        }
        try
        {
            if (Path.Exists(output)) throw new IOException("Use a new output directory; existing evidence is never overwritten.");
            if (IsInside(output, runtime)) throw new ArgumentException("The output must be outside the runtime.");
            var platform = ServerLaunch.Detect(runtime);
            ServerRunPlan.CheckLaunchHost(platform, ServerLaunch.LocalPlatform);
            report.Provenance["serverPlatform"] = platform.ToString();
            report.Provenance["steamBuildId"] = SteamBuildId(runtime);
            Directory.CreateDirectory(output); ownOutput = true;
            string world = Path.Combine(output, "world");
            Directory.CreateDirectory(world);

            IReadOnlyDictionary<string, string> runtimeHashes = null!;
            Dictionary<string, string> pins = [];
            report.Step(StepPhase.Preflight, "pin the runtime and its five plugins", () => { runtimeHashes = WorldFixture.Manifest(runtime); pins = PluginPins(runtime); });
            report.Provenance["pins"] = string.Join(" ", pins.Select(pin => pin.Key + "=" + pin.Value));
            report.Step(StepPhase.Setup, "copy the runtime; the source is never launched", () =>
            {
                copy = WorldFixture.Copy(runtime, output, runtimeHashes);
                ServerLaunch.RequireExecutable(copy.DirectoryPath);
            });

            // A throwaway password for a server that is never listed (-public 0) and whose port nothing publishes.
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            string[] arguments = ["-batchmode", "-nographics", "-name", WorldName, "-port", "2456", "-world", WorldName, "-password", password,
                "-public", "0", "-savedir", "{world}", "-logFile", "{runtime}/toolkit-unity.log"];
            var plan = new LifecyclePlan { Scenario = LifecyclePlan.ServerScenario, Arguments = arguments, Port = CliPort };
            var started = session = Session(plan, copy!.DirectoryPath, world, pins, output, cancellation.Token);
            started.EnsureTestAccess = true; // The session's own test access, on this boot and any restart.
            GameActor server = null!;
            report.Step(StepPhase.Setup, "start the owned server on a new world, plugins pinned, with test access", () => server = started.Start());
            WorldFacts facts = new();
            report.Step("read the new world's uid", () => facts = ReadWorld(server));
            report.Provenance["worldUid"] = facts.Uid; report.Provenance["worldSeed"] = facts.Seed;
            (Site Dry, Site Wet) sites = (new(), new());
            report.Step("choose the sites from the world generator's heights", () => sites = ChooseSites(server, facts.Uid));
            report.Provenance["drySite"] = Describe(sites.Dry); report.Provenance["wetSite"] = Describe(sites.Wet);
            report.Step("confirmed world save", () => server.SaveConfirmed());
            StopOwned();
            string planPath = Path.Combine(output, "plan.json");
            report.Step("write and validate the plan", () =>
            {
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
            Console.CancelKeyPress -= onCancel;
            try { StopOwned(); } catch (Exception error) { Console.Error.WriteLine("Teardown: " + error.Message); }
            // The copy only served this boot; its logs were copied into the output when the server stopped.
            if (copy != null)
                try { report.Step(StepPhase.Cleanup, "remove the runtime copy", copy.Dispose); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Could not remove the runtime copy: " + error.Message); }
            if (ownOutput) report.Write(output);
        }
        Console.WriteLine(report.Passed ? "PREPARED (a new world and its dry-site-server plan; not an acceptance test)" : "FAIL");
        return report.Passed ? 0 : 1;
    }

    /// <summary>The MD5 of each of <see cref="Plugins"/> under the runtime's <c>BepInEx/plugins</c>; refuses a missing, duplicate or extra plugin file.</summary>
    public static Dictionary<string, string> PluginPins(string runtime)
    {
        string directory = Path.Combine(runtime, "BepInEx", "plugins");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The runtime has no BepInEx/plugins directory.");
        string[] files = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories);
        var unknown = files.Where(file => !Plugins.Any(plugin => Path.GetFileName(file) == plugin.File)).Select(file => Path.GetRelativePath(runtime, file)).ToArray();
        if (unknown.Length != 0) throw new InvalidOperationException("Unexpected plugin files; the plan pins exactly five plugins: " + string.Join(", ", unknown));
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
    /// MyMod's rule for the two sites, on the samples closest to the world's centre (<see cref="SiteSearch.Nearest"/>). The dry
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

    private static OwnedServerSession Session(LifecyclePlan plan, string runtime, string world, IReadOnlyDictionary<string, string> pins, string output, CancellationToken cancellation)
    {
        // Strict: every loaded plugin must be one of the five pinned builds. The world is the one the game is creating now.
        string expectations = "cli_expect --strict world=any " + string.Join(" ", pins.Select(pin => pin.Key + "=" + pin.Value));
        int boot = 0, connection = 0;
        return new OwnedServerSession(token =>
        {
            var start = ServerLaunch.CreateStartInfo(runtime, plan.Arguments.Select(argument => plan.Expand(argument, runtime, world)),
                new Dictionary<string, string> { [LifecyclePlan.SessionTokenVariable] = token });
            return new DirectServerProcess(start, Path.Combine(output, "boot-" + ++boot),
                Path.Combine(runtime, "BepInEx", "LogOutput.log"), Path.Combine(runtime, "toolkit-unity.log"));
        }, () => new RecordingTransport(new CliTransport("127.0.0.1", plan.Port), Path.Combine(output, "connection-" + ++connection + ".jsonl")),
            world, expectations, "mymod.testing/session", TimeSpan.FromSeconds(StartupSeconds), TimeSpan.FromSeconds(CommandSeconds), cancellation: cancellation)
        { Events = plan.DedicatedStartupEvents(runtime) };
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
