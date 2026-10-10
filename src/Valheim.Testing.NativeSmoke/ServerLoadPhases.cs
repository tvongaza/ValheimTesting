using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

/// <summary>Runs ordered server-only fixture bakes through the existing owned server-load lifecycle.</summary>
internal static class ServerLoadPhases
{
    internal const string Usage = "valheim-test server-load-phases --plan FILE --output NEW_DIR";
    private static readonly Regex PhaseName = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> CommonOptions = new(StringComparer.Ordinal)
    {
        "--inventory", "--server", "--server-env", "--loader-package", "--adapter", "--cli-manifest", "--cli-files",
    };
    private static readonly HashSet<string> PhaseOptions = new(StringComparer.Ordinal)
    {
        "--mod", "--config", "--plugin-file", "--plugin-dir", "--search-root", "--optional-reference",
        "--assert-command", "--assert-line", "--before-save-command", "--before-save-line",
        "--expected-log-error", "--expected-log-reason",
    };

    internal sealed class Plan
    {
        public int Schema { get; set; }
        public string WorldFixture { get; set; } = "";
        public bool KeepFinal { get; set; }
        public string[] CommonArgs { get; set; } = [];
        public Phase[] Phases { get; set; } = [];
    }
    internal sealed class Phase
    {
        public string Name { get; set; } = "";
        public string[] Args { get; set; } = [];
    }

    internal static async Task<int> RunAsync(string[] args, Func<string[], Task<int>>? runPhase = null)
    {
        if (args is not ["--plan", var planFile, "--output", var outputGiven] ||
            !Path.IsPathFullyQualified(planFile) || !Path.IsPathFullyQualified(outputGiven))
        {
            Console.Error.WriteLine("Usage: " + Usage + " (both paths must be absolute)");
            return 2;
        }
        bool started = false;
        try
        {
            string output = SmokeCommandOptions.Output(new Dictionary<string, string> { ["--output"] = outputGiven });
            var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The phase plan is empty.");
            Validate(plan);
            bool packaged = plan.WorldFixture == "packaged";
            string? generatedSource = packaged ? Path.Combine(output, "source") : null;
            string fixture = packaged ? Path.Combine(generatedSource!, "worlds_local") : Path.GetFullPath(plan.WorldFixture);
            if (!packaged)
            {
                if (!Directory.Exists(fixture)) throw new DirectoryNotFoundException("The source world fixture is missing: " + fixture);
                FixtureBake.RefuseOutput(output, fixture);
            }
            var phases = plan.Phases.Select((phase, index) =>
            {
                string name = (index + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "-" + phase.Name;
                return (phase, name, evidence: Path.Combine(output, "phases", name), checkpoint: Path.Combine(output, "checkpoints", name));
            }).ToArray();
            // Validate every phase's exact server-load command before any copy or game process exists.
            foreach (var (phase, _, evidence, checkpoint) in phases)
                BuildArgs(plan.CommonArgs, phase.Args, fixture, evidence, checkpoint);
            Directory.CreateDirectory(output);
            File.Copy(planFile, Path.Combine(output, "phase-plan.json"));
            if (generatedSource != null) DefaultSmokeWorld.PrepareServerSaveRoot(generatedSource);
            runPhase ??= phaseArgs => ServerLoad.RunAsync(phaseArgs);
            int completed = 0;
            foreach (var (phase, name, evidence, checkpoint) in phases)
            {
                string[] phaseArgs = BuildArgs(plan.CommonArgs, phase.Args, fixture, evidence, checkpoint);
                WriteState(output, name, "starting", fixture, checkpoint);
                started = true;
                int result;
                try { result = await runPhase(phaseArgs).ConfigureAwait(false); }
                catch
                {
                    WriteState(output, name, "failed", fixture, checkpoint);
                    throw;
                }
                if (result != 0)
                {
                    WriteState(output, name, "failed", fixture, checkpoint);
                    Console.Error.WriteLine($"Phase {name} failed ({result}); its evidence is in {evidence}. Recover any active run ID shown by valheim-test env status before retrying.");
                    return result == 3 ? 3 : 1;
                }
                if (!Directory.Exists(checkpoint))
                {
                    WriteState(output, name, "failed", fixture, checkpoint);
                    throw new IOException($"Phase {name} reported success without its confirmed baked fixture: {checkpoint}");
                }
                WriteState(output, name, "baked", fixture, checkpoint);
                if (completed > 0) Directory.Delete(fixture, recursive: true); // the prior phase's checkpoint, not the source
                completed++;
                fixture = checkpoint;
            }
            if (!plan.KeepFinal) Directory.Delete(fixture, recursive: true);
            if (generatedSource != null) Directory.Delete(generatedSource, recursive: true);
            WriteState(output, phases[^1].name, "finished", plan.WorldFixture,
                plan.KeepFinal ? fixture : "removed by plan");
            Console.WriteLine("PHASES PASS: " + phases.Length + " saved server phases; " +
                (plan.KeepFinal ? "final fixture " + fixture : "all checkpoints removed") + "; evidence in " + output);
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine((started ? "FAIL: " : "REFUSED: ") + error.Message);
            return started ? 1 : 3;
        }
    }

    private static void Validate(Plan plan)
    {
        if (plan.Schema != 1 || plan.Phases is not { Length: >= 2 and <= 10 })
            throw new InvalidDataException("A phase plan needs schema 1 and 2-10 ordered phases.");
        if (plan.WorldFixture != "packaged" && !Path.IsPathFullyQualified(plan.WorldFixture))
            throw new InvalidDataException("worldFixture must be an absolute path or 'packaged'.");
        if (plan.CommonArgs == null || plan.Phases.Any(phase => phase == null || phase.Args == null))
            throw new InvalidDataException("commonArgs and every phase's args must be arrays.");
        if (plan.Phases.Any(phase => !PhaseName.IsMatch(phase.Name ?? "")) ||
            plan.Phases.Select(phase => phase.Name).Distinct(StringComparer.Ordinal).Count() != plan.Phases.Length)
            throw new InvalidDataException("Phase names must be distinct lowercase letters, digits or hyphens (1-40 characters).");
        CheckOptions(plan.CommonArgs, CommonOptions, "commonArgs");
        foreach (var phase in plan.Phases)
        {
            CheckOptions(phase.Args, PhaseOptions, "phase " + phase.Name);
            if (!phase.Args.Contains("--mod"))
                throw new InvalidDataException("Phase " + phase.Name + " must select at least one mod with --mod.");
        }
    }

    private static void CheckOptions(string[] args, HashSet<string> allowed, string owner)
    {
        if (args.Length % 2 != 0) throw new InvalidDataException(owner + " must contain option/value pairs.");
        for (int i = 0; i < args.Length; i += 2)
            if (!allowed.Contains(args[i]) || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new InvalidDataException(owner + " contains an unsupported or valueless option: " + args[i]);
    }

    private static string[] BuildArgs(string[] common, string[] phase, string fixture, string evidence, string checkpoint)
    {
        string[] args = [.. common, .. phase, "--server-only", "--world-fixture", fixture,
            "--bake-fixture", checkpoint, "--output", evidence];
        if (!ServerLoad.TryRead(args, out _, out string error, allowImplicitMod: false))
            throw new InvalidDataException("Invalid server-load phase: " + error);
        return args;
    }

    private static void WriteState(string output, string phase, string state, string input, string checkpoint)
    {
        string destination = Path.Combine(output, "phase-state.json"), temporary = destination + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { phase, state, input, checkpoint },
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temporary, destination, overwrite: true);
    }
}
