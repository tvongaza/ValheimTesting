using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>What a mod's system-test runner supplies to <see cref="PinnedServerRun"/>.</summary>
public sealed class PinnedServerRunOptions<TPlan> where TPlan : ServerRunPlan
{
    /// <summary>The runner's name: the report's name and the usage line's program.</summary>
    public required string Name { get; init; }
    /// <summary>Reads and validates the plan (for example <c>ServerRunPlan.Read&lt;MyPlan&gt;</c> then the plan's own rules).</summary>
    public required Func<string, TPlan> ReadPlan { get; init; }
    /// <summary>The mod's read-only session capability, for example <c>my.mod.testing/session</c>.</summary>
    public required string SessionCapability { get; init; }
    /// <summary>The environment variable that passes the owned session token to the in-game adapter.</summary>
    public required string SessionTokenVariable { get; init; }
    /// <summary>Launching modes beyond <c>run</c>, for fixture preparation; they never pass an acceptance test.</summary>
    public IReadOnlyList<string> PrepareModes { get; init; } = [];
    /// <summary>Refuses a mode and plan that do not belong together (throw <see cref="ArgumentException"/>).</summary>
    public Action<string, TPlan>? CheckMode { get; init; }
    /// <summary>Adds the mod's provenance (scenario details) to the report.</summary>
    public Action<TPlan, IDictionary<string, string>>? Provenance { get; init; }
    /// <summary>Turns devcommands on through the session capability's <c>devcommands</c> field before the scenario.</summary>
    public bool EnableDevcommands { get; init; } = true;
    /// <summary>The scenario for a launching mode, given the started, strictly pinned server.</summary>
    public required Func<PinnedServerRunContext<TPlan>, Task> Scenario { get; init; }
    /// <summary>Test seam: builds the owned session instead of launching the copied runtime.</summary>
    internal Func<PinnedServerRunContext<TPlan>, OwnedServerSession>? SessionOverride { get; init; }
}

/// <summary>A launching run's state, handed to the scenario.</summary>
public sealed class PinnedServerRunContext<TPlan> where TPlan : ServerRunPlan
{
    public required string Mode { get; init; }
    public required TPlan Plan { get; init; }
    public required ScenarioReport Report { get; init; }
    /// <summary>The new output directory: reports, per-boot logs, command records and the fixture copies.</summary>
    public required string Output { get; init; }
    public required string RuntimeDirectory { get; init; }
    public required string WorldDirectory { get; init; }
    public required CancellationToken Cancellation { get; init; }
    public OwnedServerSession Session { get; internal set; } = null!;
    public GameActor Server { get; internal set; } = null!;
    /// <summary>
    /// The logs the teardown scan reads: each owned server boot's are added as it launches; add an owned client's
    /// <see cref="ClientSession.Logs"/> here. They are scanned after the scenario, once the processes have stopped.
    /// </summary>
    public List<RunLog> Logs { get; } = [];
}

/// <summary>
/// The lifecycle of a mod's owned dedicated-server test runner, so the mod supplies only its plan fields, modes and
/// scenarios. Usage: <c>&lt;runner&gt; validate|run|&lt;prepare modes&gt; &lt;plan.json&gt; &lt;new-output-directory&gt;</c>.
/// <list type="number">
/// <item>Refuses an existing output directory (evidence is never overwritten) and one inside a pinned source.</item>
/// <item>Reads the plan, detects the runtime's platform and checks the host before copying anything.</item>
/// <item>Records provenance: plan, runner and toolkit hashes, mode, platform, the copies and their input hashes.</item>
/// <item>Copies and verifies the pinned runtime and world (kept for inspection) and checks the copy's executable.</item>
/// <item><c>validate</c> stops there. Otherwise: checks the CLI port is free, starts the owned session on the copies with
/// per-boot logs (<c>boot-N.*</c>) and recorded commands (<c>connection-N.jsonl</c>), waits on the dedicated startup
/// events, enables devcommands and runs the scenario.</item>
/// <item>Always stops only the owned server, records its PIDs, scans every boot's logs and the scenario's
/// <see cref="PinnedServerRunContext{TPlan}.Logs"/> (<see cref="ScenarioReport.ScanLogs"/>, with the plan's
/// <see cref="ServerRunPlan.LogScan"/>), writes <c>result.json</c> and <c>junit.xml</c>, and prints PASS (only for
/// <c>run</c>), VALIDATED or PREPARED, or FAIL. Ctrl+C and SIGTERM cancel the run.</item>
/// </list>
/// Returns the process exit code: 0 when every step passed, 1 on failure, 2 on bad usage.
/// </summary>
public static class PinnedServerRun
{
    public static async Task<int> MainAsync<TPlan>(string[] args, PinnedServerRunOptions<TPlan> options) where TPlan : ServerRunPlan
    {
        string[] modes = ["validate", "run", .. options.PrepareModes];
        if (args.Length != 3 || !modes.Contains(args[0]))
        {
            Console.Error.WriteLine($"Usage: {options.Name} {string.Join("|", modes)} <plan.json> <new-output-directory>");
            return 2;
        }
        string mode = args[0];
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
        var report = new ScenarioReport(options.Name);
        OwnedServerSession? session = null;
        PinnedServerRunContext<TPlan>? launched = null;
        string output = Path.GetFullPath(args[2]);
        bool ownOutput = false;
        try
        {
            if (Path.Exists(output)) throw new IOException("Use a new output directory; existing evidence is never overwritten.");
            var plan = options.ReadPlan(args[1]); plan.CheckOutput(output); plan.CheckPatchersAndLogScan();
            // The runtime's contents decide its platform; checked on the pinned source so a wrong host fails before copying.
            var platform = ServerLaunch.Detect(plan.Runtime.Source); plan.CheckExecutable(platform);
            if (mode != "validate")
            {
                if (OperatingSystem.IsMacOS())
                    throw new PlatformNotSupportedException("macOS has no dedicated server and cannot run the Windows or Linux one. Use validate here (it never launches a game), or run in the Linux server container or on a Windows or Linux host.");
                ServerRunPlan.CheckLaunchHost(platform, OperatingSystem.IsWindows());
            }
            options.CheckMode?.Invoke(mode, plan);
            report.Provenance["planSha256"] = WorldFixture.Hash(args[1]);
            report.Provenance["scenario"] = plan.Scenario;
            options.Provenance?.Invoke(plan, report.Provenance);
            if (Assembly.GetEntryAssembly()?.Location is { Length: > 0 } runner) report.Provenance["runnerSha256"] = WorldFixture.Hash(runner);
            report.Provenance["toolkitSha256"] = WorldFixture.Hash(typeof(GameActor).Assembly.Location);
            report.Provenance["mode"] = mode;
            report.Provenance["serverPlatform"] = platform.ToString();
            // Never deleted automatically: a failed stop or partial save must stay inspectable.
            Directory.CreateDirectory(output); ownOutput = true;
            WorldFixture? runtime = null, world = null;
            report.Step("copy and verify pinned runtime", () => { runtime = WorldFixture.Copy(plan.Runtime.Source, output, plan.Runtime.Sha256); runtime.Preserve = true; });
            report.Step("copy and verify pinned world", () => { world = WorldFixture.Copy(plan.World.Source, output, plan.World.Sha256); world.Preserve = true; });
            report.Provenance["runtime"] = runtime!.DirectoryPath; report.Provenance["world"] = world!.DirectoryPath;
            File.WriteAllText(Path.Combine(output, "input-hashes.json"), JsonSerializer.Serialize(new { runtime = runtime.SourceHashes, world = world.SourceHashes }, new JsonSerializerOptions { WriteIndented = true }));
            // Hashes do not cover file modes: a launch also requires the copy's Linux execute bit.
            report.Step("copied runtime has the plan's server executable", () =>
            {
                plan.CheckExecutable(ServerLaunch.Detect(runtime.DirectoryPath));
                if (mode != "validate") ServerLaunch.RequireExecutable(runtime.DirectoryPath);
            });
            report.Step("copied runtime's BepInEx patchers are the plan's", () => plan.CheckRuntimePatchers(runtime.DirectoryPath));
            if (mode == "validate") report.Step("prepared only; no game launched", () => { });
            else await Launch(plan, runtime.DirectoryPath, world.DirectoryPath).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            report.RecordFailure("runner failed", error);
            Console.Error.WriteLine(error.Message);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            if (session != null)
            {
                try { report.Step("stop only owned server", session.Dispose); } catch (Exception error) { Console.Error.WriteLine("Teardown: " + error.Message); }
                report.Provenance["ownedPids"] = string.Join(",", session.StartedProcesses);
            }
            // After the stop, which keeps each boot's logs; a client's were kept when the scenario closed it.
            if (launched != null && launched.Logs.Count != 0) report.ScanLogs(launched.Logs, launched.Plan.LogScan);
            if (ownOutput) report.Write(output);
        }
        // Only `run` is an acceptance result: validate launches nothing, and preparing a fixture never passes a test.
        Console.WriteLine(!report.Passed ? "FAIL" : mode switch
        {
            "run" => "PASS",
            "validate" => "VALIDATED (plan and fixtures only; no game was launched)",
            _ => "PREPARED (fixture preparation; not an acceptance test)",
        });
        return report.Passed ? 0 : 1;

        async Task Launch(TPlan plan, string runtimeDirectory, string worldDirectory)
        {
            // Catch an occupied port without issuing even a read to an unrelated server.
            report.Step("CLI port is free", () =>
            {
                var reservation = new TcpListener(IPAddress.Loopback, plan.Port);
                try { reservation.Start(); } finally { reservation.Stop(); }
            });
            var context = new PinnedServerRunContext<TPlan>
            {
                Mode = mode, Plan = plan, Report = report, Output = output, RuntimeDirectory = runtimeDirectory,
                WorldDirectory = worldDirectory, Cancellation = cancellation.Token,
            };
            launched = context;
            session = context.Session = options.SessionOverride?.Invoke(context) ?? OwnedSession(context, options);
            report.Step("start and verify owned dedicated fixture", () => context.Server = session.Start());
            if (options.EnableDevcommands)
                report.Step("enable test devcommands", () =>
                {
                    var capability = context.Server.RequireCapability(options.SessionCapability);
                    if (!context.Server.Observe(capability).Data.GetProperty("devcommands").GetBoolean()) context.Server.Execute("devcommands");
                    if (!context.Server.Observe(capability).Data.GetProperty("devcommands").GetBoolean()) throw new InvalidOperationException("Devcommands did not enable.");
                });
            await options.Scenario(context).ConfigureAwait(false);
        }
    }

    private static OwnedServerSession OwnedSession<TPlan>(PinnedServerRunContext<TPlan> run, PinnedServerRunOptions<TPlan> options) where TPlan : ServerRunPlan
    {
        var plan = run.Plan;
        int boot = 0, connection = 0;
        return new OwnedServerSession(token =>
        {
            // ServerLaunch adds SteamAppId and, for Linux, the Doorstop loader variables BepInEx needs; the working directory is the copied runtime.
            var environment = plan.Environment.ToDictionary(entry => entry.Key, entry => plan.Expand(entry.Value, run.RuntimeDirectory, run.WorldDirectory));
            environment[options.SessionTokenVariable] = token;
            var start = ServerLaunch.CreateStartInfo(run.RuntimeDirectory, plan.Arguments.Select(argument => plan.Expand(argument, run.RuntimeDirectory, run.WorldDirectory)), environment);
            string prefix = Path.Combine(run.Output, "boot-" + ++boot);
            var process = new DirectServerProcess(start, prefix,
                Path.Combine(run.RuntimeDirectory, "BepInEx", "LogOutput.log"), Path.Combine(run.RuntimeDirectory, "toolkit-unity.log"));
            // What Stop keeps: BepInEx's log, Unity's log when the plan passes -logFile {runtime}/toolkit-unity.log, and the
            // process output (Unity's log on Linux without -logFile).
            run.Logs.Add(new RunLog($"boot-{boot} BepInEx log", prefix + ".game-0.log", Required: true));
            run.Logs.Add(new RunLog($"boot-{boot} Unity log", prefix + ".game-1.log"));
            run.Logs.Add(new RunLog($"boot-{boot} stdout", prefix + ".stdout.log"));
            try { File.WriteAllText(Path.Combine(run.Output, "boot-" + boot + ".process.json"), JsonSerializer.Serialize(new { pid = process.Id, startedUtc = DateTime.UtcNow, world = run.WorldDirectory })); }
            catch { process.Stop(TimeSpan.FromSeconds(15)); process.Dispose(); throw; }
            return process;
        }, () => new RecordingTransport(new CliTransport("127.0.0.1", plan.Port), Path.Combine(run.Output, "connection-" + ++connection + ".jsonl")),
            run.WorldDirectory, plan.ExpectCommand, options.SessionCapability,
            TimeSpan.FromSeconds(plan.StartupSeconds), TimeSpan.FromSeconds(plan.CommandSeconds), cancellation: run.Cancellation)
        { Events = plan.DedicatedStartupEvents(run.RuntimeDirectory) };
    }
}
