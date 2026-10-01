using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    /// <summary>
    /// Which scenarios a mode runs, for example <c>["prepare-bridge"] = ["bridge-respawn"]</c>: a listed mode with a plan
    /// of any other scenario is refused before anything is copied (<see cref="ServerRunPlan.CheckModeScenario"/>). Modes
    /// not listed run every scenario. Each key must be <c>validate</c>, <c>run</c> or one of <see cref="PrepareModes"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> ModeScenarios { get; init; } = new Dictionary<string, string[]>();
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
    /// <summary>Test seam for <c>--profile</c> runs: fake hosts and transports.</summary>
    internal HostedSeams? HostSeams { get; init; }
}

/// <summary>A launching run's state, handed to the scenario.</summary>
public sealed class PinnedServerRunContext<TPlan> where TPlan : ServerRunPlan
{
    public required string Mode { get; init; }
    public required TPlan Plan { get; init; }
    public required ScenarioReport Report { get; init; }
    /// <summary>The new output directory: reports, per-boot logs, command records and the fixture copies.</summary>
    public required string Output { get; init; }
    /// <summary>The runtime copy the server runs from: a path on <see cref="ServerHost"/> when the run has one.</summary>
    public required string RuntimeDirectory { get; init; }
    /// <summary>The world copy the server saves to: a path on <see cref="ServerHost"/> when the run has one.</summary>
    public required string WorldDirectory { get; init; }
    public required CancellationToken Cancellation { get; init; }
    /// <summary>The environment profile given with <c>--profile</c>, or null for a run on this machine.</summary>
    public EnvironmentProfile? Profile => Hosted?.Profile;
    /// <summary>The host the dedicated server runs on (<c>--profile</c>), or null when it runs on this machine.</summary>
    public IGameHost? ServerHost => Hosted?.Host;
    internal HostedServerRun? Hosted { get; init; }
    public OwnedServerSession Session { get; internal set; } = null!;
    public GameActor Server { get; internal set; } = null!;
    /// <summary>
    /// The logs the teardown scan reads: each owned server boot's are added as it launches; add an owned client's
    /// <see cref="ClientSession.Logs"/> here. They are scanned after the scenario, once the processes have stopped.
    /// </summary>
    public List<RunLog> Logs { get; } = [];

    /// <summary>
    /// Opens the plan's game client and adds its logs to <see cref="Logs"/>, also when its startup fails after the process
    /// started (a client that never reached its menu is still scanned and listed in the result). With a profile that names clients, an owned client
    /// starts on its profile host, inside that host's desktop session (<see cref="InteractiveClient"/>): the install, CLI port
    /// and host come from the profile (the plan's <c>install</c> is not read), the install's patchers and pins are checked on the
    /// host, the host's lock is held for the rest of the run, and ValheimCLI is reached through the host's loopback tunnel.
    /// <paramref name="profileClient"/> names the profile's client when it lists several. Otherwise, and for an attached client,
    /// this is <see cref="ClientSession.Open"/>. Disposing the session stops only the client it started.
    /// </summary>
    public ClientSession OpenClient(ClientRunPlan client, string? profileClient = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.StartAtCharacterSave && Hosted != null && Hosted.Profile.Clients.Count != 0)
            throw new NotSupportedException("A prepared character is staged on this runner's machine; remote profile clients need host-side staging and are not supported yet.");
        if (Hosted != null && Hosted.Profile.Clients.Count != 0 && client.Owned)
        {
            var clients = Hosted.Profile.Clients.Keys.Order(StringComparer.Ordinal).ToList();
            string name = profileClient ?? (clients.Count == 1 ? clients[0]
                : throw new ArgumentException($"The environment profile names clients {string.Join(", ", clients)}; say which one opens.", nameof(profileClient)));
            ClientSession session;
            // A startup that fails after the client started still kept its logs: they are scanned and listed like an opened client's.
            try { session = Hosted.OpenClient(Output, client, name, Cancellation); }
            catch (Exception error) { Logs.AddRange(ClientSession.KeptLogs(error)); throw; }
            Logs.AddRange(session.Logs); // Scanned with the server's at teardown, after the scenario closes the client.
            return session;
        }
        if (profileClient != null) throw new ArgumentException("A profile client opens only for an owned client in a run with an environment profile that names clients.", nameof(profileClient));
        return ClientSession.Open(client, Output, Logs, Cancellation);
    }
}

/// <summary>
/// The lifecycle of a mod's owned dedicated-server test runner, so the mod supplies only its plan fields, modes and
/// scenarios. Usage: <c>&lt;runner&gt; [--profile &lt;environment.json&gt;] validate|run|&lt;prepare modes&gt; &lt;plan.json&gt; &lt;new-output-directory&gt;</c>.
/// <list type="number">
/// <item>Refuses an existing output directory (evidence is never overwritten) and one inside a pinned source.</item>
/// <item>Reads the plan, detects the runtime's platform and checks the host before copying anything.</item>
/// <item>Records provenance: plan, runner and toolkit hashes, mode, platform, <c>crossplay</c>, the copies and their input hashes.</item>
/// <item>Copies and verifies the pinned runtime and world (kept for inspection), checks the copy's executable, its
/// patcher names, and its game build, BepInEx core and patchers against <see cref="ServerRunPlan.RuntimePins"/> (recorded
/// as provenance).</item>
/// <item><c>validate</c> stops there. Otherwise: checks the CLI port is free, starts the owned session on the copies with
/// per-boot logs (<c>boot-N.*</c>) and recorded commands (<c>connection-N.jsonl</c>), waits on the dedicated startup
/// events, enables devcommands and runs the scenario.</item>
/// <item>Always stops only the owned server, records its PIDs, scans every boot's logs and the scenario's
/// <see cref="PinnedServerRunContext{TPlan}.Logs"/> (<see cref="ScenarioReport.ScanLogs"/>, with the plan's
/// <see cref="ServerRunPlan.LogScan"/>), writes <c>result.json</c> and <c>junit.xml</c>, and prints PASS (only for
/// <c>run</c>), VALIDATED or PREPARED, or FAIL. Ctrl+C and SIGTERM cancel the run.</item>
/// </list>
/// A plan with <see cref="ServerRunPlan.Pinning"/> <c>none</c> runs without pins: the runner prints
/// <see cref="EnvironmentPinning.Warning"/> once the plan is read, copies fixtures without a manifest as found, records the
/// runtime's hashes without checking them, and marks <c>result.json</c>, <c>junit.xml</c>, <c>input-hashes.json</c>,
/// <c>boot-N.process.json</c>, <c>connection-N.jsonl</c> and the result banner "environment not pinned".
/// <para>
/// With <c>--profile &lt;environment.json&gt;</c> the dedicated server runs on the profile's server host (a Linux host over SSH,
/// a container, or this Linux machine) instead: the runner takes that host's lock for the run, copies the host's install into
/// a new run directory there and verifies every file against the plan's runtime manifest, ships the verified world copy and
/// verifies it there, makes the executable, patcher and <see cref="ServerRunPlan.RuntimePins"/> checks on the host's copy,
/// checks the CLI port on the host, reaches ValheimCLI only through a loopback tunnel (<see cref="IGameHost.OpenCliTunnelAsync"/>),
/// starts each boot with <see cref="HostServer"/> and waits for its listening line in the host's log, and at teardown stops only
/// the process it started, fetches each boot's logs (<c>boot-N/</c>) and the world copy (<c>host-world/</c>), closes the tunnel
/// and releases the lock. <see cref="PinnedServerRunContext{TPlan}.OpenClient"/> starts a profile client in its host's desktop
/// session. A host operation whose outcome is unknown (a lost reply, a transport failure, an unproven lock), when nothing else
/// failed for certain, prints UNKNOWN and returns 3: neither a pass nor a failure.
/// </para>
/// Returns the process exit code: 0 when every step passed, 1 on failure, 2 on bad usage, 3 when the outcome is unknown.
/// </summary>
public static class PinnedServerRun
{
    /// <summary>The option that names an environment profile; it comes before the mode.</summary>
    public const string ProfileOption = "--profile";

    public static async Task<int> MainAsync<TPlan>(string[] args, PinnedServerRunOptions<TPlan> options) where TPlan : ServerRunPlan
    {
        string[] modes = ["validate", "run", .. options.PrepareModes];
        if (options.ModeScenarios.Keys.FirstOrDefault(key => !modes.Contains(key)) is { } unknownMode)
            throw new ArgumentException($"ModeScenarios lists {unknownMode}, which is not one of this runner's modes ({string.Join(", ", modes)}).", nameof(options));
        string? profilePath = null;
        if (args.Length >= 2 && args[0] == ProfileOption) { profilePath = args[1]; args = args[2..]; }
        if (args.Length != 3 || !modes.Contains(args[0]))
        {
            Console.Error.WriteLine($"Usage: {options.Name} [{ProfileOption} <environment.json>] {string.Join("|", modes)} <plan.json> <new-output-directory>");
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
        HostedServerRun? hosted = null;
        string output = Path.GetFullPath(args[2]);
        bool ownOutput = false, pinned = true, definite = false, unknownOutcome = false;
        string? unknown = null;
        // A host operation with an unknown outcome is not a failure of the test; anything else is.
        void Classify(Exception error) { if (HostedServerRun.UnknownOutcome(error) is { } why) unknown ??= why; else definite = true; }
        try
        {
            if (Path.Exists(output)) throw new IOException("Use a new output directory; existing evidence is never overwritten.");
            var plan = options.ReadPlan(args[1]); plan.CheckOutput(output); plan.CheckPatchersAndLogScan(); plan.CheckCrossplay();
            pinned = plan.Pinned;
            if (!pinned)
            {
                // Only the plan's own explicit "pinning": "none" gets here; warned before anything is copied or launched.
                EnvironmentPinning.Warn($"{options.Name} with plan {Path.GetFileName(args[1])}");
                report.MarkNotPinned("the plan sets pinning \"none\"");
            }
            ServerPlatform platform;
            if (profilePath != null)
            {
                // The runtime is the server host's install, copied and checked there; nothing local is read for it.
                var profile = EnvironmentProfile.Read(profilePath);
                hosted = HostedServerRun.Create(profile, plan, options.Name, options.HostSeams);
                report.Provenance["profileSha256"] = WorldFixture.Hash(profilePath);
                hosted.Record(report.Provenance);
                platform = ServerPlatform.Linux;
            }
            else
            {
                // The runtime's contents decide its platform; checked on the pinned source so a wrong host fails before copying.
                platform = ServerLaunch.Detect(plan.Runtime.Source); plan.CheckExecutable(platform);
                if (mode != "validate") ServerRunPlan.CheckLaunchHost(platform, ServerLaunch.LocalPlatform);
            }
            plan.CheckModeScenario(mode, options.ModeScenarios);
            options.CheckMode?.Invoke(mode, plan);
            report.Provenance["planSha256"] = WorldFixture.Hash(args[1]);
            report.Provenance["scenario"] = plan.Scenario;
            options.Provenance?.Invoke(plan, report.Provenance);
            if (Assembly.GetEntryAssembly()?.Location is { Length: > 0 } runner) report.Provenance["runnerSha256"] = WorldFixture.Hash(runner);
            report.Provenance["toolkitSha256"] = WorldFixture.Hash(typeof(GameActor).Assembly.Location);
            report.Provenance["mode"] = mode;
            report.Provenance["serverPlatform"] = platform.ToString();
            report.Provenance["crossplay"] = plan.Crossplay ? "true" : "false";
            // Never deleted automatically: a failed stop or partial save must stay inspectable.
            Directory.CreateDirectory(output); ownOutput = true;
            WorldFixture? runtime = null, world = null;
            // Only an unpinned plan may leave out a manifest; its copy is then recorded as found.
            bool Verified(PinnedDirectory fixture) => pinned || fixture.Sha256.Count != 0;
            WorldFixture CopyOf(PinnedDirectory fixture) =>
                Verified(fixture) ? WorldFixture.Copy(fixture.Source, output, fixture.Sha256) : WorldFixture.CopyAsFound(fixture.Source, output);
            if (hosted != null) await hosted.LockAndCopyRuntimeAsync(report, plan, pinned, cancellation.Token).ConfigureAwait(false);
            else report.Step(Verified(plan.Runtime) ? "copy and verify pinned runtime" : "copy unpinned runtime as found", () => { runtime = CopyOf(plan.Runtime); runtime.Preserve = true; });
            report.Step(Verified(plan.World) ? "copy and verify pinned world" : "copy unpinned world as found", () => { world = CopyOf(plan.World); world.Preserve = true; });
            if (hosted != null) await hosted.ShipWorldAsync(report, world!.DirectoryPath, cancellation.Token).ConfigureAwait(false);
            string runtimeDirectory = hosted?.RuntimeDirectory ?? runtime!.DirectoryPath, worldDirectory = hosted?.WorldDirectory ?? world!.DirectoryPath;
            report.Provenance["runtime"] = runtimeDirectory; report.Provenance["world"] = world!.DirectoryPath;
            if (hosted != null) report.Provenance["hostWorld"] = worldDirectory;
            File.WriteAllText(Path.Combine(output, "input-hashes.json"), JsonSerializer.Serialize(
                EnvironmentPinning.Stamp(new() { ["runtime"] = hosted?.RuntimeHashes ?? runtime!.SourceHashes, ["world"] = world.SourceHashes }, pinned), new JsonSerializerOptions { WriteIndented = true }));
            if (hosted != null)
            {
                hosted.CheckRuntime(report, plan, pinned);
                await hosted.CheckCrossplayAsync(report, plan, cancellation.Token).ConfigureAwait(false);
            }
            else
            {
                // Hashes do not cover file modes: a launch also requires the copy's Linux or macOS execute bit.
                report.Step("copied runtime has the plan's server executable", () =>
                {
                    plan.CheckExecutable(ServerLaunch.Detect(runtime!.DirectoryPath));
                    if (mode != "validate") ServerLaunch.RequireExecutable(runtime.DirectoryPath);
                });
                report.Step("copied runtime's BepInEx patchers are the plan's", () => plan.CheckRuntimePatchers(runtime!.DirectoryPath));
                // What the game cannot report in game: its build and the loader, pinned on disk before anything launches.
                report.Step(pinned ? "copied runtime is the pinned game build, BepInEx core and patchers" : "record the unpinned runtime's game build, BepInEx core and patchers",
                    () => plan.CheckRuntimePins(runtime!.DirectoryPath).Record(report.Provenance, "runtime"));
                // Only this machine's own loader can say whether a Linux runtime's libparty.so loads here.
                if (plan.Crossplay && platform == ServerPlatform.Linux && OperatingSystem.IsLinux())
                    await report.StepAsync("this machine can load crossplay's libraries", async () =>
                        report.Provenance["crossplayLibraries"] = await CrossplayLibraries.RequireAsync(new LocalGameHost("this machine", HostShell.Bash), runtime!.DirectoryPath,
                            TimeSpan.FromMinutes(1), cancellation.Token).ConfigureAwait(false) + " loads on this machine").ConfigureAwait(false);
            }
            if (mode == "validate") report.Step("prepared only; no game launched", () => { });
            else await Launch(plan, runtimeDirectory, worldDirectory).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            report.RecordFailure("runner failed", error);
            Console.Error.WriteLine(error.Message);
            Classify(error);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            bool stopped = true;
            if (session != null)
            {
                try { report.Step("stop only owned server", session.Dispose); }
                catch (Exception error) { stopped = false; Console.Error.WriteLine("Teardown: " + error.Message); Classify(error); }
                report.Provenance["ownedPids"] = string.Join(",", session.StartedProcesses);
                // How each boot ended, restarts included: asked to quit, then killed only after the plan's quitSeconds.
                report.Provenance["serverStops"] = string.Join("; ", session.Stops.Select((stop, i) => $"boot-{i + 1} {stop}"));
                foreach (var (stop, i) in session.Stops.Select((stop, i) => (stop, i)).Where(entry => entry.stop.Outcome == StopOutcome.Killed))
                    Console.Error.WriteLine($"Warning: owned server boot-{i + 1} was {stop}; the game's shutdown (its world save at quit) did not run.");
                if (stopped && launched is { Plan.Crossplay: true } crossplayRun)
                    try
                    {
                        report.Step("every boot quit cleanly and retired its crossplay lobby", () =>
                        {
                            // Each boot's kept logs: BepInEx's, Unity's (-logFile) and the process output, wherever the game wrote its lines.
                            var logs = crossplayRun.Logs.Select(log => (Match: Regex.Match(log.Role, @"^boot-(\d+) "), log.Path)).Where(log => log.Match.Success)
                                .GroupBy(log => int.Parse(log.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).OrderBy(group => group.Key)
                                .Select(group => (IReadOnlyList<string>)group.Select(log => log.Path).ToList()).ToList();
                            report.Provenance["crossplayLobbies"] = string.Join(" | ", CrossplayServer.RequireLobbiesRetired(session.Stops, logs));
                        });
                    }
                    catch (Exception error) { Console.Error.WriteLine("Teardown: " + error.Message); definite = true; } // Recorded as its failed step.
            }
            if (hosted != null)
                foreach (var failure in await hosted.TeardownAsync(report, output, session != null, stopped).ConfigureAwait(false)) Classify(failure);
            // After the stop, which keeps each boot's logs; a client's were kept when the scenario closed it. Logs missing after
            // an unknown outcome are explained by it, so the scan then decides nothing on its own.
            if (launched != null && launched.Logs.Count != 0 && !report.ScanLogs(launched.Logs, launched.Plan.LogScan) && unknown == null) definite = true;
            unknownOutcome = !report.Passed && unknown != null && !definite;
            if (unknownOutcome) report.Provenance["outcome"] = "unknown: " + unknown;
            if (ownOutput) report.Write(output);
        }
        // Only `run` is an acceptance result: validate launches nothing, and preparing a fixture never passes a test.
        Console.WriteLine((unknownOutcome ? "UNKNOWN (a host operation's outcome could not be established; neither a pass nor a failure: " + unknown + ")"
            : !report.Passed ? "FAIL" : mode switch
            {
                "run" => "PASS",
                "validate" => "VALIDATED (plan and fixtures only; no game was launched)",
                _ => "PREPARED (fixture preparation; not an acceptance test)",
            }) + (pinned ? "" : $" [{EnvironmentPinning.NotPinned}]"));
        return report.Passed ? 0 : unknownOutcome ? 3 : 1;

        async Task Launch(TPlan plan, string runtimeDirectory, string worldDirectory)
        {
            if (hosted != null) await hosted.OpenAsync(report, cancellation.Token).ConfigureAwait(false);
            else
            {
                // Catch an occupied port without issuing even a read to an unrelated server.
                report.Step("CLI port is free", () =>
                {
                    var reservation = new TcpListener(IPAddress.Loopback, plan.Port);
                    try { reservation.Start(); } finally { reservation.Stop(); }
                });
            }
            var context = new PinnedServerRunContext<TPlan>
            {
                Mode = mode, Plan = plan, Report = report, Output = output, RuntimeDirectory = runtimeDirectory,
                WorldDirectory = worldDirectory, Cancellation = cancellation.Token, Hosted = hosted,
            };
            launched = context;
            session = context.Session = options.SessionOverride?.Invoke(context) ?? hosted?.Session(context, options) ?? OwnedSession(context, options);
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
            var start = ServerLaunch.CreateStartInfo(run.RuntimeDirectory, plan.LaunchArguments(run.RuntimeDirectory, run.WorldDirectory), environment);
            string prefix = Path.Combine(run.Output, "boot-" + ++boot);
            var process = new DirectServerProcess(start, prefix,
                Path.Combine(run.RuntimeDirectory, "BepInEx", "LogOutput.log"), Path.Combine(run.RuntimeDirectory, "toolkit-unity.log"));
            // What Stop keeps: BepInEx's log, Unity's log when the plan passes -logFile {runtime}/toolkit-unity.log, and the
            // process output (Unity's log on Linux without -logFile).
            run.Logs.Add(new RunLog($"boot-{boot} BepInEx log", prefix + ".game-0.log", Required: true));
            run.Logs.Add(new RunLog($"boot-{boot} Unity log", prefix + ".game-1.log"));
            run.Logs.Add(new RunLog($"boot-{boot} stdout", prefix + ".stdout.log"));
            try { File.WriteAllText(Path.Combine(run.Output, "boot-" + boot + ".process.json"), JsonSerializer.Serialize(EnvironmentPinning.Stamp(new() { ["pid"] = process.Id, ["startedUtc"] = DateTime.UtcNow, ["world"] = run.WorldDirectory }, plan.Pinned))); }
            catch { process.Stop(TimeSpan.FromSeconds(15)); process.Dispose(); throw; }
            return process;
        }, () => new RecordingTransport(new CliTransport("127.0.0.1", plan.Port), Path.Combine(run.Output, "connection-" + ++connection + ".jsonl"), plan.Pinned ? null : EnvironmentPinning.NotPinned),
            run.WorldDirectory, plan.ExpectCommand, options.SessionCapability,
            TimeSpan.FromSeconds(plan.StartupSeconds), TimeSpan.FromSeconds(plan.CommandSeconds), cancellation: run.Cancellation)
        {
            QuitTimeout = TimeSpan.FromSeconds(plan.QuitSeconds), Events = plan.DedicatedStartupEvents(run.RuntimeDirectory) };
    }
}
