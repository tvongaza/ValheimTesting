using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
    /// <summary>Refuses a mode and plan that do not belong together (throw <see cref="ArgumentException"/>).</summary>
    public Action<string, TPlan>? CheckMode { get; init; }
    /// <summary>Adds the mod's provenance (scenario details) to the report.</summary>
    public Action<TPlan, IDictionary<string, string>>? Provenance { get; init; }
    /// <summary>The scenario for a launching mode, given the started, strictly pinned server.</summary>
    public required Func<PinnedServerRunContext<TPlan>, Task> Scenario { get; init; }
    /// <summary>
    /// A runtime copy the runner already made and staged (<see cref="WorldFixture.Copy"/>, then its plugins), to run in place
    /// of a second copy of it: the plan's runtime source must be this copy. The run verifies it against the plan's hashes,
    /// runs the server from it and retires it at the end like its own copy, comparing against the staged state, so a run
    /// needs room for one runtime, not two. The copy becomes the run's: the run sets its <see cref="WorldFixture.Preserve"/>, so
    /// the caller's Dispose never removes one the run keeps. Not for a run on another host, whose runtime is copied there.
    /// </summary>
    public WorldFixture? StagedRuntime { get; init; }
    /// <summary>Test seam: builds the owned session instead of launching the copied runtime.</summary>
    internal Func<PinnedServerRunContext<TPlan>, OwnedServerSession>? SessionOverride { get; init; }
    /// <summary>Test seam for runs on other hosts: fake hosts and transports.</summary>
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
    /// <summary>The campaign's client names (<see cref="PinnedServerRun.RunCampaignAsync{TPlan}"/>), each on its assigned host; empty when clients open on this machine.</summary>
    public IReadOnlyList<string> CampaignClients => Hosted?.Profile.Clients.Keys.Order(StringComparer.Ordinal).ToList() ?? [];
    /// <summary>The host a campaign client runs on, for reading its files or capturing there.</summary>
    public IGameHost ClientHost(string campaignClient) => Hosted?.ClientHost(campaignClient)
        ?? throw new ArgumentException("This run is not a campaign with named clients.", nameof(campaignClient));
    /// <summary>The host the dedicated server runs on (<c>--inventory</c> or a campaign), or null when it runs on this machine.</summary>
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
    /// started (a client that never reached its menu is still scanned and listed in the result). In a campaign
    /// (<see cref="PinnedServerRun.RunCampaignAsync{TPlan}"/>), an owned client starts on the host its environment was assigned,
    /// inside that host's desktop session (<see cref="InteractiveClient"/>), or in this runner's GUI session for a local macOS
    /// host: the install and CLI port are its prepared ones, the install's patchers and pins are checked on the host, the host's
    /// lock is held for the rest of the run, and ValheimCLI is reached through the host's loopback tunnel.
    /// <paramref name="campaignClient"/> names the campaign's client when it declares several. That client's observed Steam
    /// identity is leased first, owned or attached (<see cref="SteamAccountHold"/>), and its host must still be signed in to it: a
    /// held account refuses the client, a lost lease stops it and cancels the run, and the lease is released at teardown.
    /// Otherwise this is <see cref="ClientSession.Open(ClientRunPlan, string, CancellationToken)"/> on this machine. Disposing the
    /// session stops only the client it started.
    /// </summary>
    public ClientSession OpenClient(ClientRunPlan client, string? campaignClient = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ClientSession session;
        if (Hosted != null && Hosted.Profile.Clients.Count != 0 && (client.Owned || Hosted.Profile.SteamAccounts != null))
        {
            var clients = Hosted.Profile.Clients.Keys.Order(StringComparer.Ordinal).ToList();
            string name = campaignClient ?? (clients.Count == 1 ? clients[0]
                : throw new ArgumentException($"The campaign declares clients {string.Join(", ", clients)}; say which one opens.", nameof(campaignClient)));
            // A startup that fails after the client started still kept its logs: they are scanned and listed like an opened client's.
            try { session = client.Owned ? Hosted.OpenClient(Report, Output, client, name, Cancellation) : Hosted.AttachClient(Report, Output, client, name, Cancellation); }
            catch (Exception error) { lock (Logs) Logs.AddRange(ClientSession.KeptLogs(error)); throw; }
            lock (Logs) Logs.AddRange(session.Logs); // Scanned at teardown, after all parallel client opens settle.
            return session;
        }
        if (campaignClient != null) throw new ArgumentException("A named client opens only in a campaign that declares clients (PinnedServerRun.RunCampaignAsync).", nameof(campaignClient));
        return ClientSession.Open(client, Output, Logs, Cancellation);
    }
}

/// <summary>
/// The lifecycle of a mod's owned dedicated-server test runner, so the mod supplies only its plan fields, modes and
/// scenarios. Usage: <c>&lt;runner&gt; [--inventory &lt;environments.json&gt;] validate|run|&lt;prepare modes&gt; &lt;plan.json&gt; &lt;new-output-directory&gt;</c>.
/// <list type="number">
/// <item>Refuses an existing output directory (evidence is never overwritten) and one inside a pinned source.</item>
/// <item>Reads the plan, detects the runtime's platform and checks the host before copying anything.</item>
/// <item>Records provenance: plan, runner and toolkit hashes, mode, platform, <c>crossplay</c>, the copies and their input hashes.</item>
/// <item>Copies and verifies the pinned runtime and world (kept for inspection), checks the copy's executable and its
/// game build, loader and patchers against <see cref="ServerRunPlan.RuntimePins"/> (recorded
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
/// With <c>--inventory &lt;environments.json&gt;</c> the dedicated server runs on a host of the private environment inventory instead:
/// the first server environment in inventory order whose host (Linux with bash over SSH, in a container or this machine, or
/// Windows with PowerShell) and ports fit the plan, its choice and reason recorded as <c>serverEnvironment</c>. The runner takes that host's lock for the run, copies the host's install into
/// a new run directory there and verifies every file against the plan's runtime manifest, ships the verified world copy and
/// verifies it there, makes the executable, patcher and <see cref="ServerRunPlan.RuntimePins"/> checks on the host's copy,
/// checks the CLI port on the host, reaches ValheimCLI only through a loopback tunnel (<see cref="IGameHost.OpenCliTunnelAsync"/>),
/// starts each boot with <see cref="HostServer"/> and waits for its listening line in the host's log, and at teardown stops only
/// the process it started, fetches each boot's logs (<c>boot-N/</c>) and the world copy (<c>host-world/</c>), closes the tunnel
/// and releases the lock. Clients open on this machine; remote clients and several actors are a campaign's
/// (<see cref="RunCampaignAsync{TPlan}"/>), where <see cref="PinnedServerRunContext{TPlan}.OpenClient"/> starts each in its host's
/// desktop session on its leased, observed Steam identity.
/// A host operation whose outcome is unknown (a lost reply, a transport failure, an unproven lock or lease release), when nothing else
/// failed for certain, prints UNKNOWN and returns 3: neither a pass nor a failure.
/// </para>
/// Returns the process exit code: 0 when every step passed, 1 on failure, 2 on bad usage, 3 when the outcome is unknown.
/// </summary>
/// <example>
/// A mod supplies its validated plan, adapter session capability and scenario. The toolkit owns fixture copies,
/// startup, teardown and the report:
/// <code>
/// return await PinnedServerRun.MainAsync(args, new PinnedServerRunOptions&lt;LifecyclePlan&gt;
/// {
///     Name = "mymod-system-test",
///     ReadPlan = LifecyclePlan.ReadValidated,
///     SessionCapability = "mymod.testing/session",
///     SessionTokenVariable = LifecyclePlan.SessionTokenVariable,
///     Scenario = run =&gt;
///     {
///         DrySiteServerScenario.Run(run.Plan, run.Server, run.Session.Restart, run.Report);
///         return Task.CompletedTask;
///     },
/// });
/// </code>
/// <c>validate</c> checks the pinned inputs without launching the game; <c>run</c> executes the scenario on a disposable
/// copy. For client rounds, restarts and failure-safe cleanup, see the compiling
/// <see href="https://github.com/tvongaza/ValheimTesting/blob/main/examples/FullLifecycle/MyMod.SystemTests/Program.cs">full lifecycle runner</see>.
/// </example>
public static class PinnedServerRun
{
    // Preparing a campaign copies a game install per actor, on every host at once.
    private static readonly TimeSpan CampaignTimeout = TimeSpan.FromMinutes(10);
    // A plan's unused sites are NaN until set.
    private static readonly JsonSerializerOptions BoundPlanJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>The option that names the private environment inventory the dedicated server is placed from; it comes before the mode.</summary>
    public const string InventoryOption = "--inventory";

    /// <summary>
    /// Set to <c>1</c> to keep every actor's whole runtime copy after the run, for hands-on debugging in it, with any runner.
    /// By default a run that stopped its server keeps only what the run added or changed in the copy (<c>runtime-changes/</c>,
    /// see <see cref="WorldFixture.Retire"/>) and removes the rest, which is the pinned runtime's own files.
    /// </summary>
    public const string KeepRuntimeVariable = "VALHEIM_TESTING_KEEP_RUNTIME";

    /// <summary>
    /// How much of what a run wrote in its runtime copy is kept (per file, in all): 64 MB and 256 MB after a pass, and 1 GB
    /// and 2 GB after a failure, where a large file the run wrote (a mod's cache, a dump) may be the evidence.
    /// </summary>
    internal static (long PerFile, long Total) RetainLimits(bool passed) => passed ? (64L << 20, 256L << 20) : (1L << 30, 2L << 30);

    public static async Task<int> MainAsync<TPlan>(string[] args, PinnedServerRunOptions<TPlan> options) where TPlan : ServerRunPlan
    {
        string[] modes = ["validate", "run", .. options.PrepareModes];
        if (args.Length >= 1 && args[0] == "--profile")
        {
            Console.Error.WriteLine($"{options.Name}: --profile was removed. Place the dedicated server with {InventoryOption} <environments.json> " +
                "(a private environment inventory), or run remote clients and several actors as a campaign (PinnedServerRun.RunCampaignAsync).");
            return 2;
        }
        string? inventoryPath = null;
        if (args.Length >= 2 && args[0] == InventoryOption) { inventoryPath = args[1]; args = args[2..]; }
        if (args.Length != 3 || !modes.Contains(args[0]))
        {
            Console.Error.WriteLine($"Usage: {options.Name} [{InventoryOption} <environments.json>] {string.Join("|", modes)} <plan.json> <new-output-directory>");
            return 2;
        }
        string planFile = args[1];
        using var cancellation = new RunCancellation();
        return await RunAsync(args[0], () => options.ReadPlan(planFile), () => FileHash.Sha256(planFile), Path.GetFileName(planFile),
            args[2], options, cancellation, inventoryPath, campaign: null).ConfigureAwait(false);
    }

    /// <summary>Test seam: a run on an environment resolved in code, as a campaign hands it over (remote clients included).</summary>
    internal static async Task<int> MainAsync<TPlan>(ResolvedEnvironment environment, string[] args, PinnedServerRunOptions<TPlan> options) where TPlan : ServerRunPlan
    {
        if (args.Length != 3) throw new ArgumentException("mode, plan and output.", nameof(args));
        string planFile = args[1];
        using var cancellation = new RunCancellation();
        return await RunAsync(args[0], () => options.ReadPlan(planFile), () => FileHash.Sha256(planFile), Path.GetFileName(planFile),
            args[2], options, cancellation, inventoryPath: null, campaign: null, environment).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="plan"/> on a campaign's actors (<see cref="HostedCampaignManifest"/>): the dedicated server and each
    /// named client on the environment the inventory assigns it. The campaign's static preflight and the plan's agreement with
    /// it (every plugin the plan pins is one a role selects; no angle-bracket placeholder left in the server's arguments) are
    /// Preflight steps; preparing the disposable installs and characters (<see cref="HostedCampaignPreparation.PrepareAsync"/>,
    /// with its read-only host checks first) and binding them to the plan (<see cref="PreparedHostedCampaign.ApplyTo"/>) are
    /// Setup steps; retiring them after the processes stopped is a Cleanup step. <paramref name="clients"/> names the plan's
    /// client sections by the campaign's client names. The evidence, <c>result.json</c> and <c>junit.xml</c> are written to
    /// <paramref name="output"/> (new), the prepared inputs to its <c>prepared/</c>. Exit codes as <see cref="MainAsync{TPlan}"/>.
    /// </summary>
    public static async Task<int> RunCampaignAsync<TPlan>(string manifestFile, TPlan plan,
        Func<TPlan, IReadOnlyDictionary<string, ClientRunPlan>> clients, string output, PinnedServerRunOptions<TPlan> options)
        where TPlan : ServerRunPlan
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(clients);
        using var cancellation = new RunCancellation();
        // The bound plan is kept as evidence (prepared/plan.json, never read back): its hash is the run's planSha256.
        string full = Path.GetFullPath(output);
        return await RunAsync("run", () => plan, () => FileHash.Sha256(Path.Combine(full, "prepared", "plan.json")),
            Path.GetFileName(manifestFile), output, options, cancellation, inventoryPath: null, campaign: (manifestFile, clients)).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync<TPlan>(string mode, Func<TPlan> readPlan, Func<string> planHash, string planName, string outputArgument,
        PinnedServerRunOptions<TPlan> options, RunCancellation cancellation, string? inventoryPath,
        (string Manifest, Func<TPlan, IReadOnlyDictionary<string, ClientRunPlan>> Clients)? campaign, ResolvedEnvironment? given = null) where TPlan : ServerRunPlan
    {
        var report = new ScenarioReport(options.Name);
        OwnedServerSession? session = null;
        WorldFixture? runtime = null, world = null;
        PinnedServerRunContext<TPlan>? launched = null;
        HostedServerRun? hosted = null;
        PreparedHostedCampaign? prepared = null;
        string output = Path.GetFullPath(outputArgument);
        bool ownOutput = false, pinned = true, definite = false, unknownOutcome = false;
        string? unknown = null;
        var phase = StepPhase.Preflight; // Where a failure outside any step happened: before copying, until the scenario, or in it.
        // A host operation with an unknown outcome is not a failure of the test; anything else is.
        void Classify(Exception error) { if (HostedServerRun.UnknownOutcome(error) is { } why) unknown ??= why; else definite = true; }
        try
        {
            if (Path.Exists(output)) throw new IOException("Use a new output directory; existing evidence is never overwritten.");
            var plan = readPlan(); plan.CheckLogScan(); plan.CheckCrossplay();
            if (campaign == null) plan.CheckOutput(output); // A campaign's sources are its prepared copies, bound below.
            pinned = plan.Pinned;
            if (!pinned)
            {
                // Only the plan's own explicit "pinning": "none" gets here; warned before anything is copied or launched.
                EnvironmentPinning.Warn($"{options.Name} with plan {planName}");
                report.MarkNotPinned("the plan sets pinning \"none\"");
            }
            ResolvedEnvironment? environment = given;
            if (campaign is var (manifestFile, bind))
            {
                // Stages 1 and 2 never copy: every independent problem is reported before the first host write.
                Directory.CreateDirectory(output); ownOutput = true;
                report.Provenance["campaignSha256"] = FileHash.Sha256(manifestFile);
                HostedCampaignPreparation.Inspection inspection = null!;
                // One run id for the whole campaign: its prepared installs, its journal and the server run's directory.
                string campaignRunId = RunJournal.NewRunId();
                report.Provenance["runId"] = campaignRunId;
                report.Step(StepPhase.Preflight, "campaign inputs and actor assignment", () =>
                {
                    inspection = HostedCampaignPreparation.InspectInputs(manifestFile);
                    inspection.Report.RequireReady();
                    // With no inventory file the actors are on this machine; what was detected for it is recorded either way.
                    if (inspection.Inputs!.Manifest.Inventory.Length != 0)
                        report.Provenance["inventorySha256"] = FileHash.Sha256(inspection.Inputs.Manifest.Inventory);
                    else report.Provenance["inventory"] = "this machine";
                    if (inspection.Report.Detected.Count != 0) report.Provenance["inventoryDetected"] = string.Join("; ", inspection.Report.Detected);
                });
                report.Step(StepPhase.Preflight, "the plan agrees with the campaign", () => HostedCampaignPreparation.CheckPlan(inspection, plan, bind(plan)));
                phase = StepPhase.Setup; // From here the hosts are written to.
                await report.StepAsync(StepPhase.Setup, "check the hosts and prepare every actor's disposable install", async () =>
                    prepared = await HostedCampaignPreparation.PrepareAsync(inspection, Path.Combine(output, "prepared"), CampaignTimeout,
                        options.HostSeams?.Host, cancellation.Token, campaignRunId).ConfigureAwait(false)).ConfigureAwait(false);
                report.Step(StepPhase.Setup, "bind the prepared actors to the plan", () =>
                {
                    prepared!.ApplyTo(plan, prepared.Manifest, bind(plan), Path.Combine(output, "prepared"));
                    plan.CheckOutput(output);
                    File.WriteAllText(Path.Combine(output, "prepared", "plan.json"), JsonSerializer.Serialize(plan, plan.GetType(), BoundPlanJson) + "\n");
                });
                environment = prepared!.Environment;
            }
            ServerPlatform platform;
            if (inventoryPath != null)
            {
                // A standalone run has one actor to place: its dedicated server. Clients are a campaign's.
                var inventory = EnvironmentInventory.Read(inventoryPath);
                var (placed, assignment) = inventory.PlaceServer(plan);
                if (inventory.Detected.Count != 0) report.Provenance["inventoryDetected"] = string.Join("; ", inventory.Detected);
                environment = placed;
                report.Provenance["inventorySha256"] = FileHash.Sha256(inventoryPath);
                report.Provenance["serverEnvironment"] = assignment.Environment + ": " + assignment.Reason;
            }
            if (environment != null)
            {
                // The runtime is the server host's install, copied and checked there; nothing local is read for it.
                hosted = HostedServerRun.Create(environment, plan, options.Name, options.HostSeams, prepared: prepared != null, prepared?.Journal.RunId);
                // A client's lost Steam account lease stops that client, then the run, as Ctrl+C would.
                hosted.AccountLost = () => { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } };
                hosted.Record(report.Provenance);
                platform = hosted.HostProfile.Platform == "windows" ? ServerPlatform.Windows : ServerPlatform.Linux;
            }
            else
            {
                // The runtime's contents decide its platform; checked on the pinned source so a wrong host fails before copying.
                platform = ServerLaunch.Detect(plan.Runtime.Source); plan.CheckExecutable(platform);
                if (mode != "validate") ServerRunPlan.CheckLaunchHost(platform, ServerLaunch.LocalPlatform);
            }
            if (hosted != null && options.StagedRuntime != null) throw new ArgumentException("A run on another host copies its runtime on the server host; a staged local runtime copy cannot stand in for it.");
            options.CheckMode?.Invoke(mode, plan);
            report.Provenance["planSha256"] = planHash();
            report.Provenance["scenario"] = plan.Scenario;
            options.Provenance?.Invoke(plan, report.Provenance);
            if (Assembly.GetEntryAssembly()?.Location is { Length: > 0 } runner) report.Provenance["runnerSha256"] = FileHash.Sha256(runner);
            report.Provenance["toolkitSha256"] = FileHash.Sha256(typeof(GameActor).Assembly.Location);
            report.Provenance["mode"] = mode;
            report.Provenance["serverPlatform"] = platform.ToString();
            report.Provenance["crossplay"] = plan.Crossplay ? "true" : "false";
            // Never deleted automatically: a failed stop or partial save must stay inspectable. Only the runtime copy goes at
            // the end, after a clean stop, keeping what the run changed in it.
            if (!ownOutput) { Directory.CreateDirectory(output); ownOutput = true; }
            // Before copying: a drive that fills part-way through a copy leaves a broken runtime behind.
            report.Step(StepPhase.Preflight, "enough free disk space for the copies", () =>
            {
                long bytes = DiskSpace.DirectoryBytes(plan.World.Source) + (hosted == null && options.StagedRuntime == null ? DiskSpace.DirectoryBytes(plan.Runtime.Source) : 0);
                if (DiskSpace.Require(output, bytes, hosted == null ? "this run's runtime and world copies" : "this run's world copy") is { } free)
                    report.Provenance["freeBytesBeforeCopies"] = free.ToString(CultureInfo.InvariantCulture);
            });
            phase = StepPhase.Setup;
            // Only an unpinned plan may leave out a manifest; its copy is then recorded as found.
            bool Verified(PinnedDirectory fixture) => pinned || fixture.Sha256.Count != 0;
            WorldFixture CopyOf(PinnedDirectory fixture) =>
                Verified(fixture) ? WorldFixture.Copy(fixture.Source, output, fixture.Sha256) : WorldFixture.CopyAsFound(fixture.Source, output);
            if (hosted != null) await hosted.LockAndCopyRuntimeAsync(report, plan, pinned, cancellation.Token).ConfigureAwait(false);
            else if (options.StagedRuntime is { } staged)
                report.Step(StepPhase.Setup, "verify the staged runtime copy", () =>
                {
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (!Path.GetFullPath(plan.Runtime.Source).TrimEnd(Path.DirectorySeparatorChar).Equals(staged.DirectoryPath, comparison))
                        throw new ArgumentException($"The plan's runtime source {plan.Runtime.Source} is not the staged runtime copy {staged.DirectoryPath}.");
                    if (Verified(plan.Runtime)) WorldFixture.Verify(staged.DirectoryPath, plan.Runtime.Sha256);
                    // The copy is the run's now: the caller's Dispose must not remove one the run keeps (its server may still run).
                    staged.Preserve = true;
                    // Its state now is what the run is compared against at the end, so what staging added is not counted as the run's.
                    runtime = WorldFixture.Existing(staged.DirectoryPath, new Dictionary<string, string>(Verified(plan.Runtime) ? plan.Runtime.Sha256 : WorldFixture.Manifest(staged.DirectoryPath), StringComparer.Ordinal));
                    runtime.Preserve = true;
                });
            else report.Step(StepPhase.Setup, Verified(plan.Runtime) ? "copy and verify pinned runtime" : "copy unpinned runtime as found", () => { runtime = CopyOf(plan.Runtime); runtime.Preserve = true; });
            report.Step(StepPhase.Setup, Verified(plan.World) ? "copy and verify pinned world" : "copy unpinned world as found", () => { world = CopyOf(plan.World); world.Preserve = true; });
            if (hosted != null) await hosted.ShipWorldAsync(report, world!.DirectoryPath, cancellation.Token).ConfigureAwait(false);
            string runtimeDirectory = hosted?.RuntimeDirectory ?? runtime!.DirectoryPath, worldDirectory = hosted?.WorldDirectory ?? world!.DirectoryPath;
            report.Provenance["runtime"] = runtimeDirectory; report.Provenance["world"] = world!.DirectoryPath;
            if (hosted != null) report.Provenance["hostWorld"] = worldDirectory;
            File.WriteAllText(Path.Combine(output, "input-hashes.json"), JsonSerializer.Serialize(
                EnvironmentPinning.Stamp(new() { ["runtime"] = hosted?.RuntimeHashes ?? runtime!.SourceHashes, ["world"] = world.SourceHashes }, pinned), new JsonSerializerOptions { WriteIndented = true }));
            if (hosted != null)
            {
                hosted.CheckRuntime(report, plan, pinned);
                await hosted.CheckWindowsLoaderAsync(report, cancellation.Token).ConfigureAwait(false);
                await hosted.CheckCrossplayAsync(report, plan, cancellation.Token).ConfigureAwait(false);
            }
            else
            {
                // Hashes do not cover file modes: a launch also requires the copy's Linux or macOS execute bit.
                report.Step(StepPhase.Setup, "copied runtime has the plan's server executable", () =>
                {
                    plan.CheckExecutable(ServerLaunch.Detect(runtime!.DirectoryPath));
                    if (mode != "validate") ServerLaunch.RequireExecutable(runtime.DirectoryPath);
                });
                // What the game cannot report in game: its build and the loader, pinned on disk before anything launches.
                report.Step(StepPhase.Setup, pinned ? "copied runtime is the pinned game build, loader and patchers" : "record the unpinned runtime's game build, loader and patchers",
                    () => plan.CheckRuntimePins(runtime!.DirectoryPath).Record(report.Provenance, "runtime"));
                // Only this machine's own loader can say whether a Linux runtime's libparty.so loads here.
                if (plan.Crossplay && platform == ServerPlatform.Linux && OperatingSystem.IsLinux())
                    await report.StepAsync(StepPhase.Setup, "this machine can load crossplay's libraries", async () =>
                        report.Provenance["crossplayLibraries"] = await CrossplayLibraries.RequireAsync(new LocalGameHost("this machine", HostShell.Bash), runtime!.DirectoryPath,
                            TimeSpan.FromMinutes(1), cancellation.Token).ConfigureAwait(false) + " loads on this machine").ConfigureAwait(false);
            }
            if (mode == "validate") report.Step(StepPhase.Setup, "prepared only; no game launched", () => { });
            else await Launch(plan, runtimeDirectory, worldDirectory).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            report.RecordFailure(phase, "runner failed", error);
            Console.Error.WriteLine(error.Message);
            Classify(error);
        }
        finally
        {
            bool stopped = true;
            if (session != null)
            {
                try { report.Step(StepPhase.Cleanup, "stop only owned server", session.Dispose); }
                catch (Exception error) { stopped = false; Console.Error.WriteLine("Teardown: " + error.Message); Classify(error); }
                report.Provenance["ownedPids"] = string.Join(",", session.StartedProcesses);
                // How each boot ended, restarts included: asked to quit, then killed only after the plan's quitSeconds.
                report.Provenance["serverStops"] = string.Join("; ", session.Stops.Select((stop, i) => $"boot-{i + 1} {stop}"));
                foreach (var (stop, i) in session.Stops.Select((stop, i) => (stop, i)).Where(entry => entry.stop.Outcome == StopOutcome.Killed))
                    Console.Error.WriteLine($"Warning: owned server boot-{i + 1} was {stop}; the game's shutdown (its world save at quit) did not run.");
                if (stopped && launched is { Plan.Crossplay: true } crossplayRun)
                    try
                    {
                        report.Step(StepPhase.Cleanup, "every boot quit cleanly and retired its crossplay lobby", () =>
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
            // One owner retires what the run leaves (#257): a hosted run's teardown retires its copy and the campaign's characters
            // and installs under the locks it holds; a campaign whose server run was never created is retired here.
            var retirement = new RunRetirement(report, output);
            if (hosted != null)
                foreach (var failure in await hosted.TeardownAsync(report, output, session != null, stopped, prepared, retirement).ConfigureAwait(false)) Classify(failure);
            else if (prepared != null)
                foreach (var failure in await retirement.CampaignAsync(prepared, []).ConfigureAwait(false))
                { Console.Error.WriteLine("Teardown: " + failure.Message); Classify(failure); }
            // After the stop, which keeps each boot's logs; a client's were kept when the scenario closed it. Logs missing after
            // an unknown outcome are explained by it, so the scan then decides nothing on its own.
            if (launched != null && launched.Logs.Count != 0 && !report.ScanLogs(launched.Logs, launched.Plan.LogScan) && unknown == null) definite = true;
            // A copy that could not be removed is a definite (local) failure, so it comes before the outcome is decided.
            if (runtime != null)
                try { retirement.Local(runtime, stopped, launchedNothing: session == null); }
                catch (Exception error) { Console.Error.WriteLine("Warning: runtime copy cleanup failed: " + error.Message); definite = true; } // Recorded as its failed step.
            unknownOutcome = !report.Passed && unknown != null && !definite;
            if (unknownOutcome) report.Provenance["outcome"] = "unknown: " + unknown;
            // The journal's last word on each host the campaign prepared: how the run ended and whether its cleanup was proven.
            if (prepared != null)
                foreach (string hostName in prepared.Copies.Select(copy => copy.Host).Distinct(StringComparer.OrdinalIgnoreCase))
                    try
                    {
                        await prepared.Journal.AppendAsync(prepared.HostFor(hostName), prepared.JournalOf(hostName), "run", JournalEntry.Of(JournalEntry.RunEnded,
                            ("state", report.Passed ? "passed" : unknownOutcome ? "unknown" : "failed"),
                            ("cleanupVerified", report.CleanupVerified ? "true" : "false")), prepared.Timeout).ConfigureAwait(false);
                    }
                    catch (Exception error) { Console.Error.WriteLine($"Warning: could not journal the run's end on {hostName}: {error.Message}"); }
            else if (hosted != null)
                await hosted.JournalEndAsync(report.Passed ? "passed" : unknownOutcome ? "unknown" : "failed", report.CleanupVerified).ConfigureAwait(false);
            // The run ends here: it no longer holds the copies it keeps (their owner records go; see OwnedCopies).
            runtime?.Dispose(); world?.Dispose();
            if (ownOutput)
            {
                long bytes = DiskSpace.DirectoryBytes(output);
                report.Provenance["outputBytes"] = bytes.ToString(CultureInfo.InvariantCulture);
                report.Write(output);
                Console.WriteLine($"Output: {DiskSpace.Format(bytes)} in {output}");
            }
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
                report.Step(StepPhase.Setup, "CLI port is free", () =>
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
            // The session owns test access on every boot it starts, so a scenario's restart comes back with it too.
            session.EnsureTestAccess = true;
            report.Step(StepPhase.Setup, "start and verify owned dedicated fixture", () => context.Server = session.Start());
            phase = StepPhase.Scenario;
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
