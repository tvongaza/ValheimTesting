using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using valheim_cli.Testing;

/// <summary>
/// One check against an already prepared game: verify the strict world and plugin pins, run one probe, write the report.
/// It never launches, stops, moves or teleports anything; only the session probe's save, join and leave change the game.
/// </summary>
internal static class ObserveCheck
{
    public const string Usage = """
        ObserveCheck <host> <port> <pins-file> <new-output-directory> <probe> [probe arguments]
          ground  <height-plan.json>        generator or loaded-ground heights against declared values
          surface <surface-plan.json>       a client's loaded heightmap, its collider, and optional stationary support
          paint   <paint-plan.json>         loaded raw paint RGBA
          capture <x> <z> <spacing> <countX> <countZ> generator|loaded-ground   a bounded grid saved for exact replay
          walk    <route.json> <seconds 5..600>   records a person walking a route; never moves the player
          session state | save <worldUid> | join <address> <character> [password-env] | leave
        """;

    private static readonly JsonSerializerOptions Strict = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Exit 0: the probe passed. 1: it failed, or its input was refused. 2: invalid usage.</summary>
    public static int Run(string[] args, Func<string, int, IGameTransport> connect)
    {
        if (args.Length < 5 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        { Console.Error.WriteLine(Usage); return 2; }
        string output = Path.GetFullPath(args[3]), name = args[4];
        var report = new ScenarioReport("observe-" + name);
        try
        {
            // Inputs are checked before anything is created or connected.
            var probe = Probe(name, args[5..]);
            if (probe == null) { Console.Error.WriteLine(Usage); return 2; }
            string pins = StrictExpectations.Load(args[2]);
            report.Provenance["pinsSha256"] = FileHash.Sha256(args[2]);
            // Input files by name only, and no join address or character: reports get shared.
            report.Provenance["probe"] = name == "session" ? "session " + args[5] : string.Join(' ', args[4..].Select(arg => File.Exists(arg) ? Path.GetFileName(arg) : arg));
            foreach (string input in args[5..].Where(File.Exists)) report.Provenance["inputSha256"] = FileHash.Sha256(input);
            if (Path.Exists(output)) throw new IOException("Use a new output directory.");
            Directory.CreateDirectory(output);
            try
            {
                using var actor = new GameActor(name, new RecordingTransport(connect(args[0], port), Path.Combine(output, "commands.jsonl")));
                report.Step(StepPhase.Preflight, "verify the world and plugin pins", () => actor.VerifyEnvironment(pins));
                report.Step(name, () => probe(actor, report, output));
            }
            catch (Exception error)
            {
                if (report.Steps.All(step => step.Passed)) report.RecordFailure(name + " could not run", error);
                Console.Error.WriteLine(error.Message);
            }
            report.Write(output);
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        Console.WriteLine((report.Passed ? "PASS" : "FAIL") + ": " + Path.Combine(output, "result.json"));
        return report.Passed ? 0 : 1;
    }

    // Each probe reads and validates its input now and returns what it does once the pins hold; null is invalid usage.
    private static Action<GameActor, ScenarioReport, string>? Probe(string name, string[] a) => (name, a.Length) switch
    {
        ("ground", 1) => Ground(Read<HeightPlan>(a[0])),
        ("surface", 1) => Surface(SurfacePlan.Read(a[0])),
        ("paint", 1) => Paint(PaintPlan.Read(a[0])),
        ("capture", 6) => Capture(new TerrainGridRequest(Float(a[0]), Float(a[1]), Float(a[2]), Int(a[3]), Int(a[4]), a[5])),
        ("walk", 2) => Walk(Read<WalkCheckpoint[]>(a[0]), Int(a[1])),
        ("session", _) when a is ["state"] or ["save", _] or ["join", _, _] or ["join", _, _, _] or ["leave"] => Session(a),
        _ => null,
    };

    // TerrainProbe: declared heights on one layer. The plan says where its expected values came from.
    private static Action<GameActor, ScenarioReport, string> Ground(HeightPlan plan)
    {
        TerrainProbe.Validate(plan.Layer, plan.ExpectedFrom, plan.Samples, plan.Tolerance);
        return (actor, report, output) =>
        {
            var result = TerrainProbe.Compare(actor, plan.Layer, plan.ExpectedFrom, plan.Samples, plan.Tolerance);
            Save(output, "terrain.json", result);
            if (!result.Passed) throw new InvalidOperationException("Height mismatch; inspect every residual in terrain.json.");
        };
    }

    // SurfaceProbe: each loaded vertex and that heightmap's own collider; then, if declared, a player settled on dry ground.
    private static Action<GameActor, ScenarioReport, string> Surface(SurfacePlan plan) => (actor, report, output) =>
    {
        var readings = SurfaceProbe.Compare(actor, plan.ExpectedFrom, plan.Samples, plan.Tolerance);
        Save(output, "surfaces.json", readings);
        if (readings.Any(r => !r.Passed)) throw new InvalidOperationException("Heightmap or collider mismatch; inspect every residual in surfaces.json.");
        report.Provenance["grounding"] = plan.Support == null ? "not checked: the plan declares no support point" : "checked";
        if (plan.Support is not { } support) return;
        try { Save(output, "support.json", PlayerPlacement.RequireSupported(actor, support)); }
        catch (SupportException e)
        {
            Save(output, "support.json", e.Readings);
            throw new InvalidOperationException("The player is not settled on the declared ground; this check never moves the player.", e);
        }
    };

    // PaintProbe: the loaded heightmap's raw paint texels, not a screenshot.
    private static Action<GameActor, ScenarioReport, string> Paint(PaintPlan plan) => (actor, report, output) =>
    {
        report.Provenance["expectedFrom"] = plan.ExpectedFrom;
        var readings = PaintProbe.Compare(actor, plan.ExpectedFrom, plan.Samples, plan.Tolerance);
        Save(output, "paint.json", readings);
        if (readings.Any(r => !r.Passed)) throw new InvalidOperationException("Paint mismatch; inspect the channel residuals in paint.json.");
    };

    // TerrainCapture: recorded input for exact replay (TerrainCapture.Load(path).Terrain), not an independent expectation.
    private static Action<GameActor, ScenarioReport, string> Capture(TerrainGridRequest grid)
    {
        grid.Validate();
        return (actor, report, output) => TerrainCapture.Read(actor, grid, "ValheimCLI terrain-grid observation with strict pins, sha256 " + report.Provenance["pinsSha256"])
            .Save(Path.Combine(output, "capture.json"));
    }

    // WalkingProbe: samples a person driving the client; exit 0 means the trace qualifies for a human verdict, not a usable road.
    private static Action<GameActor, ScenarioReport, string> Walk(WalkCheckpoint[] route, int seconds)
    {
        WalkingProbe.Validate(route);
        if (seconds is < 5 or > 600) throw new ArgumentException("Duration must be 5..600 seconds.");
        return (actor, report, output) =>
        {
            string world = new SessionControl(actor).Read().WorldUid ?? throw new InvalidOperationException("The client is not in a world; join the pinned world first.");
            var support = actor.RequireCapability("valheim.world/player-support");
            report.Provenance["observerInstance"] = support.Instance;
            actor.CommandTimeout = TimeSpan.FromSeconds(2);
            var samples = new List<WalkSample>();
            for (var clock = Stopwatch.StartNew(); clock.Elapsed.TotalSeconds < seconds; Thread.Sleep(250))
            {
                samples.Add(WalkingProbe.Read(actor.Observe(support), clock.Elapsed.TotalSeconds));
                File.AppendAllText(Path.Combine(output, "samples.jsonl"), JsonSerializer.Serialize(samples[^1]) + Environment.NewLine);
            }
            var evidence = WalkingProbe.Assess(route, samples);
            string review = Save(output, "review.json", new WalkingReview(evidence));
            report.Attach(new EvidenceReference("walking-review", "route", world, review, FileHash.Sha256(review)));
            if (!evidence.Sufficient) throw new InvalidOperationException("The trace does not qualify for review: " + string.Join(", ", evidence.Issues));
        };
    }

    // SessionControl: state is read-only; save, join and leave are issued exactly once and never retried.
    private static Action<GameActor, ScenarioReport, string> Session(string[] a) => (actor, report, output) =>
    {
        var session = new SessionControl(actor);
        switch (a)
        {
            case ["state"]: Save(output, "session.json", session.Read()); break;
            case ["save", var world]: report.Provenance["saveNumber"] = session.Save(world, TimeSpan.FromSeconds(120)).ToString(CultureInfo.InvariantCulture); break;
            case ["join", var address, var character, .. var password]: session.Join(address, character, password.FirstOrDefault()); break;
            case ["leave"]: session.Leave(); break;
            default: throw new ArgumentException("Invalid session action.");
        }
    };

    private static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file), Strict) ?? throw new ArgumentException("Empty input: " + file);
    private static float Float(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static int Int(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static string Save(string output, string file, object value)
    {
        string path = Path.Combine(output, file);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Indented));
        return path;
    }
}

/// <summary>The ground probe's input: heights on one layer, each derived independently of the game being checked.</summary>
internal sealed class HeightPlan
{
    public string Layer { get; set; } = "";
    public string ExpectedFrom { get; set; } = "";
    public float Tolerance { get; set; }
    public List<HeightExpectation> Samples { get; set; } = [];
}
