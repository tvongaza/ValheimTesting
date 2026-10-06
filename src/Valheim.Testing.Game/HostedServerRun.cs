using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>Test seams for a <see cref="PinnedServerRun"/> on an environment's hosts: fake hosts and transports instead of ssh and sockets.</summary>
internal sealed class HostedSeams
{
    /// <summary>Builds the host of that name instead of <see cref="ResolvedEnvironment.CreateHost"/>.</summary>
    public Func<string, IGameHost>? Host { get; init; }
    /// <summary>Connects to ValheimCLI at a tunnel's local port instead of a <c>CliTransport</c>.</summary>
    public Func<int, IGameTransport>? Connect { get; init; }
    /// <summary>False skips the state pushes (a fake transport has none).</summary>
    public bool StateWaits { get; init; } = true;
    public string? RunId { get; init; }
    /// <summary>Shorter Steam account leases and renewals than the pool's, so a test sees them lapse.</summary>
    public TimeSpan? SteamLeaseTime { get; init; }
    public TimeSpan? SteamRenewEvery { get; init; }
    /// <summary>Overrides the local macOS GUI-session probe in controlled tests.</summary>
    public Action? RequireMacGui { get; init; }
    /// <summary>Starts a local macOS client without opening the real game in controlled tests.</summary>
    public Func<ClientRunPlan, string, SteamAccountHold?, CancellationToken, Action<IOwnedProcess>?, ClientSession>? LocalMacLaunch { get; init; }
    /// <summary>The run's Ctrl+C owner instead of one the runner makes, so a test can signal it.</summary>
    public RunCancellation? Cancellation { get; init; }
}

/// <summary>
/// The parts of a <see cref="PinnedServerRun"/> that differ when its dedicated server runs on the environment's server
/// host (<c>--inventory</c> or a campaign): the host lock, the runtime copied from the host's install and verified there, the world copy
/// shipped and verified there, the port check, the loopback CLI tunnel, the owned session through <see cref="HostServer"/>,
/// remote clients through <see cref="InteractiveClient"/> and a local macOS GUI client through <see cref="ClientSession"/>,
/// each client's Steam identity lease in a campaign, and the teardown
/// that fetches evidence, closes the tunnel and releases the leases and locks.
/// </summary>
internal sealed class HostedServerRun
{
    internal const string BepInExLog = "BepInEx/LogOutput.log", UnityLog = "toolkit-unity.log";
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(60), Long = TimeSpan.FromMinutes(15);
    private readonly HostedSeams _seams;
    private readonly object _clientState = new();
    private readonly SemaphoreSlim _clientLockGate = new(1, 1);
    private readonly string _owner;
    private readonly List<(string Host, HostLock Lock)> _clientLocks = [];
    private readonly List<(string Host, IOwnedProcess Process)> _localMacProcesses = [];
    private readonly List<ClientAccount> _accounts = [];
    private IGameHost? _leaseHost;
    private HostLock? _lock;
    private CliTunnel? _tunnel;
    private HostListing? _runtime;
    private bool _worldShipped, _serverMayRun;
    private int _clients;

    private HostedServerRun(ResolvedEnvironment profile, GameRole role, HostProfile hostProfile, IGameHost host, string runId, string runner, HostedSeams seams,
        string? preparedRuntime)
    {
        Profile = profile; Role = role; HostProfile = hostProfile; Host = host; RunId = runId; _seams = seams; _owner = runner + " " + runId;
        _journal = new RunJournal(runId);
        // A campaign prepared the server's disposable install already (<runtime>/vt-prep-<id>-server/runtime): that is the one
        // copy the server runs from. A standalone run makes its own under <runtime>/<runId>.
        Prepared = preparedRuntime != null;
        RunDirectory = HostInstall.Join(role.Runtime, runId);
        RuntimeDirectory = preparedRuntime ?? HostInstall.Join(RunDirectory, "runtime");
        WorldDirectory = HostInstall.Join(RunDirectory, "world");
    }

    // The run's journal on each host it touches (RunJournal): a process is journalled before it starts, a lock once held.
    private readonly RunJournal _journal;
    private async Task JournalAsync(IGameHost host, string hostName, string actor, JournalEntry entry, CancellationToken cancellation)
    {
        await _journal.AppendAsync(host, RunJournal.DirectoryFor(Profile.Hosts[hostName]), actor, entry, Quick, cancellation).ConfigureAwait(false);
        if (hostName == Role.Host) _serverJournalled = true;
    }
    private bool _serverJournalled;
    // After its effect a lost line is a warning: the effect happened, and the entry before it already names where to look.
    private async Task NoteAsync(IGameHost host, string hostName, string actor, JournalEntry entry)
    {
        try { await JournalAsync(host, hostName, actor, entry, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { Console.Error.WriteLine($"Warning: could not journal {entry.Kind} for {actor} on {hostName}: {error.Message}"); }
    }
    private Task NoteLockAsync(IGameHost host, string hostName, HostLock held, string kind) =>
        NoteAsync(host, hostName, "run", JournalEntry.Of(kind, ("lock", held.Path), ("claimant", held.Owner)));

    private static JournalEntry LeaseEntry(string kind, SteamAccountHold hold, params (string Key, string Value)[] more) =>
        JournalEntry.Of(kind, [("account", hold.Account), ("pool", hold.Pool), ("owner", hold.Owner),
            ("expiresUtc", hold.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture)), ("leaseId", hold.LeaseId),
            ("number", hold.LeaseNumber.ToString(CultureInfo.InvariantCulture)), ("directory", hold.LeaseDirectory), .. more]);

    /// <summary>
    /// The run's end in its server host's journal, when the run wrote there at all (a standalone run; a campaign's preparation
    /// journals its own hosts). A run refused before it touched the host leaves it untouched.
    /// </summary>
    public Task JournalEndAsync(string state, bool cleanupVerified) =>
        JournalEndAsync(JournalEntry.Of(JournalEntry.RunEnded, ("state", state), ("cleanupVerified", cleanupVerified ? "true" : "false")));
    /// <summary>The run's last journal entry on its server host: its end, or that its cleanup was abandoned.</summary>
    internal Task JournalEndAsync(JournalEntry last) => !_serverJournalled ? Task.CompletedTask : NoteAsync(Host, Role.Host, "run", last);

    /// <summary>Whether the runtime is a campaign's prepared install (verified in place) rather than a copy this run makes.</summary>
    public bool Prepared { get; }

    public ResolvedEnvironment Profile { get; }
    public GameRole Role { get; }
    public HostProfile HostProfile { get; }
    public IGameHost Host { get; }
    public string RunId { get; }
    /// <summary>
    /// This run's directory on the server host: <c>runtime</c>, <c>world</c> and <c>boot-N</c>. The world and boot logs stay
    /// (both are fetched too); the runtime copy goes at teardown once the server has stopped (<see cref="TeardownAsync"/>).
    /// </summary>
    public string RunDirectory { get; }
    public string RuntimeDirectory { get; }
    public string WorldDirectory { get; }
    /// <summary>The runtime copy's files on the host, once copied.</summary>
    public IReadOnlyDictionary<string, string> RuntimeHashes => _runtime?.Files ?? new Dictionary<string, string>();
    /// <summary>Runs when a client's Steam account lease is lost: the runner cancels the run (its client was stopped already).</summary>
    public Action? AccountLost { get; set; }

    /// <summary>Refuses an environment and plan that cannot run a server on the environment's server host, before anything is touched.</summary>
    public static HostedServerRun Create(ResolvedEnvironment profile, ServerRunPlan plan, string runner, HostedSeams? seams) =>
        Create(profile, plan, runner, seams, prepared: false);

    /// <summary>With <paramref name="prepared"/>, the server role's install is a campaign's prepared disposable install: the run uses it as its runtime, under the campaign's run id.</summary>
    internal static HostedServerRun Create(ResolvedEnvironment profile, ServerRunPlan plan, string runner, HostedSeams? seams, bool prepared, string? campaignRunId = null)
    {
        var role = profile.Server ?? throw new ArgumentException("The environment places no dedicated server.");
        var hostProfile = profile.Hosts[role.Host];
        if (Refusal(hostProfile, role, plan) is { } refusal) throw new ArgumentException($"The server environment on host '{role.Host}': {refusal}");
        seams ??= new HostedSeams();
        string runId = seams.RunId ?? campaignRunId ?? RunJournal.NewRunId();
        var host = seams.Host?.Invoke(role.Host) ?? profile.CreateHost(role.Host);
        return new HostedServerRun(profile, role, hostProfile, host, runId, runner, seams, prepared ? role.Install : null);
    }

    /// <summary>Why a server environment cannot run <paramref name="plan"/>, or null: its host's platform and shell, and the plan's ports.</summary>
    internal static string? Refusal(HostProfile hostProfile, GameRole role, ServerRunPlan plan)
    {
        if (!((hostProfile.Platform == "linux" && HostShell.Parse(hostProfile.Shell).Kind == HostShellKind.Bash) ||
              (hostProfile.Platform == "windows" && HostShell.Parse(hostProfile.Shell).Kind == HostShellKind.PowerShell)))
            return $"it is {hostProfile.Platform} with {hostProfile.Shell}; a hosted dedicated server needs Linux/bash or Windows/PowerShell.";
        if (hostProfile.Kind == "local" && hostProfile.Platform != HostProfile.CurrentPlatform)
            return $"it is a local {hostProfile.Platform} host, but this machine is {HostProfile.CurrentPlatform}.";
        // The plan's runtime names its platform by its server executable; a pinned manifest lists it.
        string? planned = plan.Executable == GameLaunch.ServerWindowsExecutable || plan.Runtime.Sha256.ContainsKey(GameLaunch.ServerWindowsExecutable) ? "windows"
            : plan.Executable == GameLaunch.ServerLinuxExecutable || plan.Runtime.Sha256.ContainsKey(GameLaunch.ServerLinuxExecutable) ? "linux" : null;
        if (planned != null && planned != hostProfile.Platform)
            return $"the plan's runtime is a {planned} server, but the host is {hostProfile.Platform}.";
        if (role.CliPort != plan.Port)
            return $"the plan's ValheimCLI port {plan.Port} is not its cliPort {role.CliPort}; the runtime's [Server] Port must be both.";
        // The game reads its arguments lowercased, so -Port names the game port too.
        int at = Array.FindIndex(plan.Arguments, argument => argument.Equals("-port", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < plan.Arguments.Length && int.TryParse(plan.Expand(plan.Arguments[at + 1], "", ""), NumberStyles.None, CultureInfo.InvariantCulture, out int gamePort) && gamePort != role.GamePort)
            return $"the plan's -port {gamePort} is not its gamePort {role.GamePort}.";
        return null;
    }

    public void Record(IDictionary<string, string> provenance)
    {
        provenance["serverHost"] = Role.Host;
        provenance["serverHostKind"] = Host.Kind.ToString();
        provenance["hostRunDirectory"] = RunDirectory;
        provenance["runtimeSource"] = Role.Host + ":" + Role.Install;
    }

    /// <summary>Takes the server host's lock, then copies the host's install into this run's runtime and verifies every file there.</summary>
    public async Task LockAndCopyRuntimeAsync(ScenarioReport report, ServerRunPlan plan, bool pinned, CancellationToken cancellation)
    {
        // A Windows host that can register no server task is refused before the copy (a campaign's preflight asked already).
        if (Host.Shell.Kind == HostShellKind.PowerShell)
            await report.StepAsync(StepPhase.Setup, "the server host can start a server task", () => HostServer.RequireTaskLogonAsync(Host, Quick, cancellation)).ConfigureAwait(false);
        await report.StepAsync(StepPhase.Setup, "take the server host's lock", async () => _lock = await Host.AcquireLockAsync(HostProfile.Lock, _owner, Quick, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        await NoteLockAsync(Host, Role.Host, _lock!, JournalEntry.LockHeld).ConfigureAwait(false);
        // Only an unpinned plan may leave out the manifest; the copy is then recorded as found.
        bool verified = pinned || plan.Runtime.Sha256.Count != 0;
        if (Prepared)
        {
            // The campaign's preparation made the one copy; it must still be exactly what the preparation listed and bound.
            await report.StepAsync(StepPhase.Setup, verified ? "verify the prepared runtime on the server host" : "list the prepared runtime on the server host as found", async () =>
            {
                _runtime = await HostInstall.ListAsync(Host, RuntimeDirectory, Long, null, cancellation).ConfigureAwait(false);
                if (verified) HostInstall.RequireSame(plan.Runtime.Sha256, _runtime, "prepared runtime");
            }).ConfigureAwait(false);
            return;
        }
        await report.StepAsync(StepPhase.Setup, verified ? "copy and verify pinned runtime on the server host" : "copy unpinned runtime on the server host as found", async () =>
        {
            // Steam's own runtime output in the install (logs/) is not the runtime's: the copy leaves it out, and the pins never count it.
            await HostInstall.CopyAsync(Host, Role.Install, RuntimeDirectory, Long, HostInstall.ServerRuntimeSkips, cancellation).ConfigureAwait(false);
            _runtime = await HostInstall.ListAsync(Host, RuntimeDirectory, Long, null, cancellation).ConfigureAwait(false);
            if (verified) HostInstall.RequireSame(HostInstall.WithoutSkipped(plan.Runtime.Sha256, HostInstall.ServerRuntimeSkips, _runtime.Names), _runtime, "runtime copy");
        }).ConfigureAwait(false);
    }

    /// <summary>For a crossplay plan: the runtime copy's <c>libparty.so</c> loads on the server host (<see cref="CrossplayLibraries"/>).</summary>
    public Task CheckCrossplayAsync(ScenarioReport report, ServerRunPlan plan, CancellationToken cancellation)
    {
        if (!plan.Crossplay) return Task.CompletedTask;
        if (Host.Shell.Kind == HostShellKind.PowerShell)
        {
            report.Step(StepPhase.Setup, "the Windows runtime has crossplay's native library", () =>
            {
                const string party = "valheim_server_Data/Plugins/x86_64/Party.dll";
                if (!(_runtime?.Files.ContainsKey(party) ?? false))
                    throw new FileNotFoundException($"The copied Windows server on {Host.Name} has no {party}; crossplay cannot start.");
                report.Provenance["crossplayLibraries"] = party + " present on " + Host.Name + "; game startup verifies it loads";
            });
            return Task.CompletedTask;
        }
        return report.StepAsync(StepPhase.Setup, "the server host can load crossplay's libraries", async () =>
            report.Provenance["crossplayLibraries"] = await CrossplayLibraries.RequireAsync(Host, RuntimeDirectory, Quick, cancellation).ConfigureAwait(false) + " loads on " + Host.Name);
    }

    /// <summary>Ships the verified local world copy to the host and verifies every file there.</summary>
    public Task ShipWorldAsync(ScenarioReport report, string localWorld, CancellationToken cancellation) =>
        report.StepAsync(StepPhase.Setup, "ship and verify the world copy on the server host", async () =>
        {
            var manifest = WorldFixture.Manifest(localWorld);
            await Host.ShipFilesAsync(localWorld, WorldDirectory, Long, cancellation).ConfigureAwait(false);
            _worldShipped = true;
            var listing = await HostInstall.ListAsync(Host, WorldDirectory, Long, null, cancellation).ConfigureAwait(false);
            HostInstall.RequireSame(manifest, listing, "world copy", ["SOURCE.txt"]);
        });

    /// <summary>The checks a local runtime copy gets, on the host copy's listing.</summary>
    public void CheckRuntime(ScenarioReport report, ServerRunPlan plan, bool pinned)
    {
        var runtime = _runtime ?? throw new InvalidOperationException("Copy the runtime first.");
        // Hashes do not cover file modes: a launch also requires the copy's execute bit.
        report.Step(StepPhase.Setup, "copied runtime has the plan's server executable", () =>
        {
            var platform = HostInstall.DetectServer(runtime);
            plan.CheckExecutable(platform);
            ServerRunPlan.CheckLaunchHost(platform, Host.Shell.Kind == HostShellKind.PowerShell);
            if (platform == ServerPlatform.Linux && !runtime.Executables.Contains(GameLaunch.ServerLinuxExecutable))
                throw new InvalidOperationException($"{GameLaunch.ServerLinuxExecutable} is not executable in the runtime copy on {Host.Name}; restore its mode (chmod u+x) in the install {Role.Install}.");
        });
        report.Step(StepPhase.Setup, pinned ? "copied runtime is the pinned game build, loader and patchers" : "record the unpinned runtime's game build, loader and patchers", () =>
            (pinned ? HostInstall.CheckPins(plan.RuntimePins ?? throw new ArgumentException("Pin the runtime's game build, loader and patchers in runtimePins, or opt out explicitly with \"pinning\": \"none\"."), runtime, "runtime")
                : HostInstall.Pins(runtime)).Record(report.Provenance, "runtime"));
    }

    /// <summary>Refuses an incoherent Windows Doorstop pair in the copied runtime before its server can start.</summary>
    public Task CheckWindowsLoaderAsync(ScenarioReport report, CancellationToken cancellation) => Host.Shell.Kind != HostShellKind.PowerShell
        ? Task.CompletedTask
        : report.StepAsync(StepPhase.Setup, "copied Windows runtime has a coherent Doorstop loader", () =>
            HostClientPreflight.RequireWindowsLoaderAsync(Host, RuntimeDirectory, "server runtime", Quick, cancellation));

    /// <summary>Refuses a busy CLI port on the host, then opens the loopback tunnel to it.</summary>
    public async Task OpenAsync(ScenarioReport report, CancellationToken cancellation)
    {
        // Catch an occupied port without issuing even a read to an unrelated server.
        await report.StepAsync(StepPhase.Setup, "CLI port is free on the server host", () => HostInstall.RequirePortFreeAsync(Host, Role.CliPort, Quick, cancellation)).ConfigureAwait(false);
        await report.StepAsync(StepPhase.Setup, "open the loopback CLI tunnel to the server host", async () =>
        {
            _tunnel = await Host.OpenCliTunnelAsync(Role.CliPort, Quick, Role.LocalCliPort, cancellation).ConfigureAwait(false);
            report.Provenance["cliTunnel"] = $"{_tunnel.Address}:{_tunnel.LocalPort} -> {Role.Host} 127.0.0.1:{_tunnel.HostPort}" + (_tunnel.Forwarded ? " (ssh forward)" : "");
        }).ConfigureAwait(false);
    }

    /// <summary>The owned session: each boot through <see cref="HostServer"/>, ValheimCLI only through the tunnel.</summary>
    public OwnedServerSession Session<TPlan>(PinnedServerRunContext<TPlan> run, PinnedServerRunOptions<TPlan> options) where TPlan : ServerRunPlan
    {
        var plan = run.Plan;
        var tunnel = _tunnel ?? throw new InvalidOperationException("Open the CLI tunnel before the session.");
        string log = HostInstall.Join(RuntimeDirectory, BepInExLog);
        int boot = 0, connection = 0;
        return new OwnedServerSession(token =>
        {
            var environment = plan.Environment.ToDictionary(entry => entry.Key, entry => plan.Expand(entry.Value, RuntimeDirectory, WorldDirectory));
            environment[options.SessionTokenVariable] = token;
            var launch = GameLaunch.ForServer(RuntimeDirectory, plan.LaunchArguments(RuntimeDirectory, WorldDirectory), environment,
                Host.Shell.Kind == HostShellKind.PowerShell ? ServerPlatform.Windows : ServerPlatform.Linux);
            int n = ++boot;
            string local = Path.Combine(run.Output, "boot-" + n), bootDirectory = HostInstall.Join(RunDirectory, "boot-" + n);
            HostServerProcess process;
            // Journalled before the start: a run interrupted from here leaves a record of where its server's pid file is.
            // With the command line the server will have, so its pid file alone proves it the run's (#257).
            string expected = launch.CommandLineSha256();
            JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.ProcessIntended, ("bootDirectory", bootDirectory),
                ("expectedCommandLineSha256", expected)), run.Cancellation).GetAwaiter().GetResult();
            try { process = HostServer.StartAsync(Host, launch, bootDirectory, Quick, [BepInExLog, UnityLog], local, run.Cancellation).GetAwaiter().GetResult(); }
            catch (Exception error) when (UnknownOutcome(error) != null)
            {
                // The start's reply was lost: a server may be running there that no session knows. The lock stays.
                _serverMayRun = true;
                throw;
            }
            // What the stop keeps and fetches: BepInEx's log, Unity's log when the plan passes -logFile {runtime}/toolkit-unity.log,
            // and the process output (Unity's log on Linux without -logFile).
            run.Logs.Add(new RunLog($"boot-{n} BepInEx log", Path.Combine(local, "game-0.log"), Required: true));
            run.Logs.Add(new RunLog($"boot-{n} Unity log", Path.Combine(local, "game-1.log")));
            run.Logs.Add(new RunLog($"boot-{n} stdout", Path.Combine(local, "stdout.log")));
            try
            {
                File.WriteAllText(Path.Combine(run.Output, "boot-" + n + ".process.json"), JsonSerializer.Serialize(EnvironmentPinning.Stamp(new()
                {
                    ["pid"] = process.Id, ["startIdentity"] = process.StartIdentity, ["host"] = Host.Name, ["bootDirectory"] = bootDirectory,
                    ["startedUtc"] = DateTime.UtcNow, ["world"] = WorldDirectory, ["taskLogon"] = process.TaskLogon,
                }, plan.Pinned)));
            }
            catch { process.Stop(TimeSpan.FromSeconds(15)); throw; }
            // The command line's hash is the third fact env recover requires before it stops the process (#257 Q2).
            string commandLine = HostProcessProbe.CommandLineAsync(Host, process.Id, process.StartIdentity, Quick).GetAwaiter().GetResult() ?? "";
            WarnUnexpectedCommandLine(Host, process.Id, expected, commandLine);
            NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.ProcessStarted, ("pid", process.Id.ToString(CultureInfo.InvariantCulture)),
                ("startIdentity", process.StartIdentity), ("commandLineSha256", commandLine), ("bootDirectory", bootDirectory),
                ("taskLogon", process.TaskLogon ?? ""))).GetAwaiter().GetResult();
            return process;
        }, () => new RecordingTransport(Connect(tunnel), Path.Combine(run.Output, "connection-" + ++connection + ".jsonl"), plan.Pinned ? null : EnvironmentPinning.NotPinned),
            WorldDirectory, plan.ExpectCommand, options.SessionCapability,
            TimeSpan.FromSeconds(plan.StartupSeconds), TimeSpan.FromSeconds(plan.CommandSeconds), cancellation: run.Cancellation)
        {
            QuitTimeout = TimeSpan.FromSeconds(plan.QuitSeconds),
            // This boot's log starts empty (the start moved any earlier one into the boot directory), so offset 0 is this boot's.
            Events = new StartupEvents
            {
                CliListeningWait = async (left, token) =>
                    (await Host.WaitForLogAsync(log, 0, StartupEvents.CliListening, StartupEvents.StartupFailures, left, token).ConfigureAwait(false)).EnsureMatched(),
                States = _seams.StateWaits ? () => StateWait.Connect(tunnel.Address, tunnel.LocalPort) : null,
                ReadyStates = [StateWait.InWorldNoPlayer],
            },
        };
    }

    private IGameTransport Connect(CliTunnel tunnel) => _seams.Connect?.Invoke(tunnel.LocalPort) ?? new CliTransport(tunnel.Address, tunnel.LocalPort);

    /// <summary>
    /// An owned client on its environment's client host, started in its desktop session (<see cref="InteractiveClient"/>) with the
    /// checks <see cref="ClientSession.Launch(ClientRunPlan, string, CancellationToken)"/> makes locally, made on the host: the
    /// install's pins, a free CLI port. ValheimCLI is reached through the host's tunnel. Disposing the session stops
    /// only that client, keeps its logs, fetches them to <c>client-N</c> in the output and closes the tunnel. With the environment's
    /// Steam leases, the client's observed identity is leased (and its host's signed-in user checked, when asked) before anything
    /// else on its host is touched, and released at teardown once the client is gone.
    /// </summary>
    public ClientSession OpenClient(ScenarioReport report, string output, ClientRunPlan plan, string name, CancellationToken cancellation) =>
        OpenClientAsync(report, output, plan, name, cancellation).GetAwaiter().GetResult();

    /// <summary>An attached campaign client: its account is leased (and checked) before the session assumes the client.</summary>
    public ClientSession AttachClient(ScenarioReport report, string output, ClientRunPlan plan, string name, CancellationToken cancellation) =>
        AttachClientAsync(report, output, plan, name, cancellation).GetAwaiter().GetResult();

    private async Task<ClientSession> AttachClientAsync(ScenarioReport report, string output, ClientRunPlan plan, string name, CancellationToken cancellation)
    {
        if (!Profile.Clients.TryGetValue(name, out var role)) throw new ArgumentException($"No client '{name}' in the environment.", nameof(name));
        var account = await HoldAccountAsync(report, name, () => ClientHost(role), cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The environment has no Steam leases; attach with ClientSession.Attach.");
        return account.Session = ClientSession.Attach(plan, output, account.Hold, _seams.Connect?.Invoke(plan.Port));
    }

    private IGameHost ClientHost(GameRole role) => role.Host == Role.Host ? Host : _seams.Host?.Invoke(role.Host) ?? Profile.CreateHost(role.Host);
    /// <summary>The named client's host, as its client is reached.</summary>
    public IGameHost ClientHost(string name) => Profile.Clients.TryGetValue(name, out var role) ? ClientHost(role)
        : throw new ArgumentException($"No client '{name}' in the environment.", nameof(name));

    // With Steam leases: leases the client's account and, when the environment asks, checks its host's signed-in user, each as its own
    // step, before the client starts or is attached. A held account refuses the client here, naming its holder.
    private async Task<ClientAccount?> HoldAccountAsync(ScenarioReport report, string name, Func<IGameHost> clientHost, CancellationToken cancellation)
    {
        var section = Profile.SteamAccounts;
        if (section == null) return null;
        ClientAccount? held = null;
        await report.StepAsync(StepPhase.Setup, $"lease a Steam account for client {name}", async () =>
        {
            IGameHost leaseHost;
            lock (_clientState)
                leaseHost = _leaseHost ??= section.LeaseHost == Role.Host ? Host : _seams.Host?.Invoke(section.LeaseHost) ?? Profile.CreateHost(section.LeaseHost);
            var hold = await SteamAccountHold.AcquireAsync(Profile, name, _owner + " client " + name, leaseHost, Quick, _seams.SteamLeaseTime, _seams.SteamRenewEvery,
                cancellation).ConfigureAwait(false);
            lock (_clientState)
            {
                _accounts.Add(held = new ClientAccount(name, hold, leaseHost, section.LeaseHost));
                hold.Record(report);
            }
            // On the lease host, where the lease lives: which account this run holds for the client, and as whom.
            await NoteAsync(leaseHost, section.LeaseHost, name, LeaseEntry(JournalEntry.LeaseHeld, hold)).ConfigureAwait(false);
            hold.Lost.Register(() => AccountLost?.Invoke());
        }).ConfigureAwait(false);
        if (section.CheckSignedIn)
            await report.StepAsync(StepPhase.Setup, $"client {name}'s host is signed in to Steam account {held!.Hold.Account} (signed-in check)",
                () => held.Hold.CheckSignedInAsync(clientHost(), cancellation)).ConfigureAwait(false);
        return held;
    }

    private async Task<ClientSession> OpenClientAsync(ScenarioReport report, string output, ClientRunPlan plan, string name, CancellationToken cancellation)
    {
        if (!Profile.Clients.TryGetValue(name, out var role)) throw new ArgumentException($"No client '{name}' in the environment.", nameof(name));
        var hostProfile = Profile.Hosts[role.Host];
        var platform = hostProfile.Platform switch { "windows" => ClientPlatform.Windows, "linux" => ClientPlatform.Linux, _ => ClientPlatform.MacOS };
        if (platform == ClientPlatform.MacOS)
            return await OpenLocalMacClientAsync(report, output, plan, name, role, hostProfile, cancellation).ConfigureAwait(false);
        // Remote Windows and Linux clients are x64 only.
        if (plan.LaunchArchitecture != ClientArchitecture.X64)
            throw new ArgumentException($"Profile client '{name}' starts in a remote host's desktop session, where only x64 Windows and Linux clients run; " +
                "architecture arm64 is for a macOS client launched locally in this runner's GUI session. Leave architecture out.");
        var launch = GameLaunch.ForClient(role.Install, plan.LaunchArguments, hostPlatform: platform, secretVariables: plan.PasswordVariable is { } password ? new[] { password } : null);
        var host = ClientHost(role);
        var account = await HoldAccountAsync(report, name, () => host, cancellation).ConfigureAwait(false);
        // The server's lock covers its own host; another client host is locked for the rest of the run.
        if (role.Host != Role.Host)
        {
            await _clientLockGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (!_clientLocks.Any(held => held.Host == role.Host))
                {
                    var taken = await host.AcquireLockAsync(hostProfile.Lock, _owner, Quick, cancellation).ConfigureAwait(false);
                    _clientLocks.Add((role.Host, taken));
                    await NoteLockAsync(host, role.Host, taken, JournalEntry.LockHeld).ConfigureAwait(false);
                }
            }
            finally { _clientLockGate.Release(); }
        }
        int n = Interlocked.Increment(ref _clients);
        string runDirectory = HostInstall.Join(role.Runtime, RunId), launchDirectory = HostInstall.Join(runDirectory, "client-" + n);
        string log = HostInstall.Join(role.Install, BepInExLog);
        var listing = await HostInstall.ListAsync(host, role.Install, Long, HostInstall.PinPaths, cancellation).ConfigureAwait(false);
        if (plan.Pinned)
            HostInstall.CheckPins(plan.InstallPins ?? throw new ArgumentException("Pin the owned client's game build, loader and patchers in installPins, or opt out explicitly with \"pinning\": \"none\"."), listing, "client install");
        await HostClientPreflight.CheckAsync(host, role.Install, platform, plan, Quick, cancellation).ConfigureAwait(false);
        await HostInstall.RequirePortFreeAsync(host, role.CliPort, Quick, cancellation).ConfigureAwait(false);
        // BepInEx rewrites its log at each start; an earlier one moves aside so the wait from offset 0 sees this start's lines only.
        var moved = (await host.RunAsync(HostedClientScripts.MoveAside(host.Shell.Kind), new Dictionary<string, string>
            { ["log"] = log, ["to"] = HostInstall.Join(runDirectory, $"client-{n}.previous-LogOutput.log") }, Quick, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Moving the client's previous BepInEx log aside on {host.Name}");
        if (InteractiveClient.Line(moved.Stdout, "VT-MOVED") == null && InteractiveClient.Line(moved.Stdout, "VT-NONE") == null)
            throw new HostOperationException($"Unexpected reply while moving the client's previous log on {host.Name}", moved);
        var tunnel = await host.OpenCliTunnelAsync(role.CliPort, Quick, role.LocalCliPort, cancellation).ConfigureAwait(false);
        try
        {
            string local = Path.Combine(output, "client-" + n);
            var display = platform != ClientPlatform.Linux ? null : new LinuxDisplay();
            var start = TimeSpan.FromSeconds(Math.Max(30, plan.StartSeconds));
            var session = ClientSession.Launch(plan, output,
                () =>
                {
                    string expected = launch.CommandLineSha256();
                    JournalAsync(host, role.Host, name, JournalEntry.Of(JournalEntry.ProcessIntended, ("launchDirectory", launchDirectory),
                        ("expectedCommandLineSha256", expected)), cancellation).GetAwaiter().GetResult();
                    var started = account == null ? InteractiveClient.StartAsync(host, launch, launchDirectory, start, display, cancellation)
                        : InteractiveClient.StartAsync(account.Hold, host, launch, launchDirectory, start, display, cancellation);
                    var client = started.GetAwaiter().GetResult();
                    string commandLine = HostProcessProbe.CommandLineAsync(host, client.Id, client.StartIdentity, Quick).GetAwaiter().GetResult() ?? "";
                    WarnUnexpectedCommandLine(host, client.Id, expected, commandLine);
                    NoteAsync(host, role.Host, name, JournalEntry.Of(JournalEntry.ProcessStarted, ("pid", client.Id.ToString(CultureInfo.InvariantCulture)),
                        ("startIdentity", client.StartIdentity), ("commandLineSha256", commandLine), ("launchDirectory", launchDirectory))).GetAwaiter().GetResult();
                    var process = new HostedClientProcess(client, host, role.Install, tunnel, local);
                    if (account != null) account.Process = process; // Its lease is released only once this process is gone.
                    return process;
                },
                () => Connect(tunnel),
                async (left, token) =>
                {
                    var clock = Stopwatch.StartNew();
                    var bepInEx = TimeSpan.FromSeconds(plan.BepInExSeconds);
                    if (bepInEx > left) bepInEx = left;
                    try
                    {
                        (await host.WaitForLogAsync(log, 0, new System.Text.RegularExpressions.Regex("^"), StartupEvents.StartupFailures,
                            bepInEx, token).ConfigureAwait(false)).EnsureMatched();
                    }
                    catch (WaitTimeoutException error)
                    {
                        // A preloader crash log this launch wrote says why (#254); an older one is not this launch's and says nothing.
                        var preloader = await HostedClientScripts.ReadPreloaderAsync(host, role.Install, launchDirectory).ConfigureAwait(false);
                        // A nullable projection: FirstOrDefault of a tuple list is a default tuple, never null.
                        var failed = preloader?.Fresh.Where(log => log.FirstError != null).Select(log => ((string Name, string Error)?)(log.Name, log.FirstError!)).FirstOrDefault();
                        string why = failed is { } hit
                            ? $"BepInEx's preloader failed: {hit.Error} (from {hit.Name}, which the client's evidence keeps as game-2.preloader-*.log). "
                            : preloader?.Fresh.Count > 0 ? $"BepInEx's preloader wrote {string.Join(", ", preloader.Value.Fresh.Select(log => log.Name))} with no error line (the client's evidence keeps it as game-2.preloader-*.log). "
                            : "The game may have reached its menu without BepInEx; check winhttp.dll, doorstop_config.ini and BepInEx/core as one pack. ";
                        string stale = preloader?.Stale.Count > 0 ? $"Older preloader logs beside the game ({string.Join(", ", preloader.Value.Stale)}) predate this launch and are not its. " : "";
                        throw new InvalidOperationException($"BepInEx wrote no fresh log line on {host.Name} within {plan.BepInExSeconds}s. {why}{stale}" +
                            $"The client's Player.log and boot output are kept in {local}.", error);
                    }
                    (await host.WaitForLogAsync(log, 0, StartupEvents.CliListening, StartupEvents.StartupFailures, left - clock.Elapsed, token).ConfigureAwait(false)).EnsureMatched();
                    if (!_seams.StateWaits) return;
                    using var states = StateWait.Connect(tunnel.Address, tunnel.LocalPort);
                    await states.WaitAsync([StateWait.MainMenu], left - clock.Elapsed, cancellation: token).ConfigureAwait(false);
                }, cancellation, null,
                [new RunLog($"client-{n} BepInEx log", Path.Combine(local, "game-0.log"), Required: true), new RunLog($"client-{n} Player.log", Path.Combine(local, "game-1.log"))],
                account?.Hold);
            if (account != null) account.Session = session;
            return session;
        }
        catch { tunnel.Dispose(); throw; }
    }

    private async Task<ClientSession> OpenLocalMacClientAsync(ScenarioReport report, string output, ClientRunPlan plan, string name,
        GameRole role, HostProfile hostProfile, CancellationToken cancellation)
    {
        if (hostProfile.Kind != "local" || (!OperatingSystem.IsMacOS() && _seams.LocalMacLaunch == null))
            throw new PlatformNotSupportedException($"Profile client '{name}' needs a local macOS host in this runner's logged-in GUI session; SSH cannot launch it there.");
        if (!plan.Owned) throw new ArgumentException($"Profile client '{name}' must be an owned client for local macOS launch.");
        if (Path.GetFullPath(plan.Install) != Path.GetFullPath(role.Install) || plan.Port != role.CliPort ||
            plan.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new ArgumentException($"Profile client '{name}' must pin the local role's exact install and CLI port on loopback.");
        (_seams.RequireMacGui ?? MacGuiSession.Require)();
        var host = ClientHost(role);
        if (host.Kind != GameHostKind.Local)
            throw new PlatformNotSupportedException($"Profile client '{name}' must use a local host; a remote process cannot enter this runner's GUI session.");
        var account = await HoldAccountAsync(report, name, () => host, cancellation).ConfigureAwait(false);
        if (role.Host != Role.Host)
        {
            await _clientLockGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (!_clientLocks.Any(held => held.Host == role.Host))
                {
                    var taken = await host.AcquireLockAsync(hostProfile.Lock, _owner, Quick, cancellation).ConfigureAwait(false);
                    _clientLocks.Add((role.Host, taken));
                    await NoteLockAsync(host, role.Host, taken, JournalEntry.LockHeld).ConfigureAwait(false);
                }
            }
            finally { _clientLockGate.Release(); }
        }
        // ClientSession owns the direct child process and its logs. No SSH-launched GUI process or remote task is involved.
        ClientSession session;
        Action<IOwnedProcess> processStarted = process =>
        {
            lock (_clientState) _localMacProcesses.Add((role.Host, process));
            if (account != null) account.Process = process;
        };
        session = _seams.LocalMacLaunch?.Invoke(plan, output, account?.Hold, cancellation, processStarted)
            ?? ClientSession.Launch(plan, output, account?.Hold, cancellation, processStarted);
        if (session.OwnedProcess is { } owned)
            lock (_clientState)
                if (!_localMacProcesses.Any(item => ReferenceEquals(item.Process, owned)))
                    _localMacProcesses.Add((role.Host, owned));
        if (account != null) { account.Process = session.OwnedProcess; account.Session = session; }
        return session;
    }

    /// <summary>
    /// After the owned server stopped: fetches the host's world copy, retires the runtime copy (<see cref="RunRetirement"/>),
    /// closes the tunnel, releases the clients' accounts, retires <paramref name="prepared"/>'s characters and installs under
    /// the locks this run holds, and releases the locks, each as its own step. With <paramref name="serverStopped"/> false
    /// the server host's lock and runtime are kept, because the server may still run there. Returns the failures; it never throws.
    /// </summary>
    public async Task<IReadOnlyList<Exception>> TeardownAsync(ScenarioReport report, string output, bool launched, bool serverStopped,
        PreparedHostedCampaign? prepared = null, RunRetirement? retirement = null, CancellationToken cleanup = default)
    {
        retirement ??= new RunRetirement(report, output);
        serverStopped &= !_serverMayRun;
        var failures = new List<Exception>();
        async Task Try(string step, Func<Task> action)
        {
            // An abandoned cleanup (RunCancellation.BeginCleanup) attempts nothing more; the journal names what is left.
            if (cleanup.IsCancellationRequested) action = () => throw new OperationCanceledException("Not attempted: the cleanup was abandoned.", cleanup);
            try { await report.StepAsync(StepPhase.Cleanup, step, action).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        }
        if (launched && _worldShipped && serverStopped)
            await Try("fetch the server host's world copy", () => Host.FetchDirectoryAsync(WorldDirectory, Path.Combine(output, "host-world"), Long, cleanup)).ConfigureAwait(false);
        if (_runtime != null)
            try { await retirement.HostAsync(Role.Host, Host, _runtime, RuntimeDirectory, launched, serverStopped, cleanup).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        if (_tunnel != null) await Try("close the CLI tunnel", () => { _tunnel.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        foreach (var account in _accounts)
        {
            // An account is released only once its client is gone: a session the scenario left open is closed first.
            if (account.Session is { Closed: false } open)
                await Try(open.Owned ? $"stop only the owned client {account.Client}" : $"detach from the operator's client {account.Client}",
                    () => { open.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
            await Try($"release client {account.Client}'s Steam account lease", async () =>
            {
                if (account.Process is { HasExited: false })
                {
                    await account.Hold.KeepAsync().ConfigureAwait(false);
                    await NoteAsync(account.LeaseHost, account.LeaseHostName, account.Client, LeaseEntry(JournalEntry.LeaseKept, account.Hold,
                        ("why", "its client may still run"))).ConfigureAwait(false);
                    throw new SteamAccountLeaseException(SteamAccountLeaseState.Unknown, account.Hold.Pool, [], $"Kept the lease on Steam account {account.Hold.Account}: the client " +
                        $"{account.Client} may still run on {account.Hold.ClientHost}. Without renewals it ends at {account.Hold.ExpiresUtc:u}.");
                }
                await account.Hold.ReleaseAsync().ConfigureAwait(false);
                await NoteAsync(account.LeaseHost, account.LeaseHostName, account.Client, LeaseEntry(JournalEntry.LeaseReleased, account.Hold)).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        // Every process is stopped (or named as possibly running), so the campaign's characters and prepared installs go, under the
        // locks held here; a host whose lock this run never took (a client that never opened) is locked for its retire.
        if (prepared != null)
        {
            var locked = _clientLocks.Select(held => held.Host).ToList();
            if (_lock != null) locked.Add(Host.Name);
            // A server that may still run kept its own copy above; the process check below only looks at the copies retired.
            foreach (var failure in await retirement.CampaignAsync(prepared, locked, cleanup).ConfigureAwait(false))
            { failures.Add(failure); Console.Error.WriteLine("Teardown: " + failure.Message); }
        }
        foreach (var (name, held) in _clientLocks) await Try($"release client host {name}'s lock", () =>
            _localMacProcesses.Any(item => item.Host == name && !item.Process.HasExited)
                ? throw new HostLockException(new HostLockResult(HostLockState.Unknown, held.Owner,
                    $"Kept {held.Path} on {name}: an owned local Mac client may still run. Confirm its recorded process has stopped before releasing this lock."))
                : ReleaseAndNoteAsync(ClientHost(Profile.Clients.Values.First(client => client.Host == name)), name, held)).ConfigureAwait(false);
        if (_lock != null)
            await Try("release the server host's lock", () => serverStopped ? ReleaseAndNoteAsync(Host, Role.Host, _lock)
                : throw new HostLockException(new HostLockResult(HostLockState.Unknown, _lock.Owner,
                    $"Kept {_lock.Path} on {Host.Name}: the owned server there may still run. Remove {_lock.Path}/owner by hand once it has stopped."))).ConfigureAwait(false);
        return failures;
    }

    private async Task ReleaseAndNoteAsync(IGameHost host, string hostName, HostLock held)
    {
        await ReleaseAsync(held).ConfigureAwait(false);
        await NoteLockAsync(host, hostName, held, JournalEntry.LockReleased).ConfigureAwait(false);
    }

    private static async Task ReleaseAsync(HostLock held)
    {
        var result = await held.ReleaseAsync().ConfigureAwait(false);
        if (result.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(result);
    }

    // The started game's command line should be the one its launch journalled: where it is not, a run interrupted before its
    // process was journalled could not prove that process its own from the pid file (env status leaves it unrecoverable).
    private static void WarnUnexpectedCommandLine(IGameHost host, int pid, string expected, string read)
    {
        if (read.Length != 0 && !string.Equals(read, expected, StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"Warning: process {pid} on {host.Name} runs another command line than its launch journalled (SHA-256 {read}, expected {expected}); " +
                "had the run been interrupted before journalling it, env recover could not have proven it the run's from its pid file.");
    }

    /// <summary>
    /// Why a failure leaves the outcome unknown rather than failed: a host operation whose reply was lost or whose transport
    /// failed, or a lock whose state could not be proven. Null for every other failure.
    /// </summary>
    internal static string? UnknownOutcome(Exception? error)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            if (current is HostOperationException { Outcome: not HostOutcome.Exited } host) return host.Message;
            if (current is HostLockException { State: HostLockState.Unknown } hostLock) return hostLock.Message;
            if (current is SteamAccountLeaseException { State: SteamAccountLeaseState.Unknown } lease) return lease.Message;
            if (current is AggregateException aggregate) return aggregate.InnerExceptions.Select(UnknownOutcome).FirstOrDefault(reason => reason != null);
        }
        return null;
    }
}

/// <summary>A campaign client's leased Steam account, with what must be gone before its lease is released.</summary>
internal sealed class ClientAccount(string client, SteamAccountHold hold, IGameHost leaseHost, string leaseHostName)
{
    public string Client { get; } = client;
    public SteamAccountHold Hold { get; } = hold;
    public ClientSession? Session { get; set; }
    public IOwnedProcess? Process { get; set; }
    /// <summary>The host the lease lives on, and its inventory name: where the run journals the lease.</summary>
    public IGameHost LeaseHost { get; } = leaseHost;
    public string LeaseHostName { get; } = leaseHostName;
}

/// <summary>
/// A client <see cref="InteractiveClient"/> started for a <see cref="PinnedServerRun"/>: stopping it stops only that process (a
/// clean stop asks it to quit first),
/// keeps its BepInEx log and Player.log in its launch directory, fetches that directory here and closes its CLI tunnel.
/// </summary>
internal sealed class HostedClientProcess(InteractiveClientProcess process, IGameHost host, string install, CliTunnel tunnel, string evidence) : IOwnedProcess
{
    private bool _kept;
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public Task<int> WaitForExitAsync(CancellationToken cancellation) => process.WaitForExitAsync(cancellation);

    public void Stop(TimeSpan timeout) => Finish(() => process.Stop(timeout));

    /// <summary>Asks the client to quit (<see cref="InteractiveClientProcess.StopCleanly"/>), then keeps and fetches its logs as <see cref="Stop"/> does.</summary>
    public ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill)
    {
        ProcessStop? stopped = null;
        Finish(() => stopped = process.StopCleanly(quit, kill));
        return stopped!;
    }

    private void Finish(Action stop)
    {
        try
        {
            stop();
            if (_kept) return;
            var kept = host.RunAsync(HostedClientScripts.Keep(host.Shell.Kind), new Dictionary<string, string> { ["install"] = install, ["dir"] = process.LaunchDirectory },
                TimeSpan.FromSeconds(60)).GetAwaiter().GetResult().EnsureSuccess($"Keeping the logs of client process {Id} on {host.Name}");
            if (InteractiveClient.Line(kept.Stdout, "VT-KEPT") == null) throw new HostOperationException($"Unexpected reply while keeping the client's logs on {host.Name}", kept);
            host.FetchDirectoryAsync(process.LaunchDirectory, evidence, TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
            _kept = true;
        }
        finally { tunnel.Dispose(); }
    }

    public void Dispose()
    {
        try { if (!_kept) Stop(TimeSpan.FromSeconds(30)); }
        finally { tunnel.Dispose(); }
    }
}

// Scripts for a hosted client's logs. Values arrive as variables; each ends with its verdict.
internal static class HostedClientScripts
{
    public static string MoveAside(HostShellKind kind) => kind == HostShellKind.Bash ? BashMoveAside : PowerShellMoveAside;
    public static string Keep(HostShellKind kind) => kind == HostShellKind.Bash ? BashKeep : PowerShellKeep;
    public static string Preloader(HostShellKind kind) => kind == HostShellKind.Bash ? BashPreloader : PowerShellPreloader;

    // Variables: install, dir. Reads only: each preloader_*.log beside the game, whether this launch wrote it (not older than
    // the launch's pid file) and the first [Error or [Fatal line of each fresh one, base64-encoded:
    // VT-PRELOADER fresh|stale <name> <line or ->, then VT-PRELOADER-END.
    public static readonly string BashPreloader = """
        set -u
        ref="$dir/pid"; [ -e "$ref" ] || ref="$dir"
        for f in "$install"/preloader_*.log; do
          [ -f "$f" ] || continue
          name=$(basename -- "$f" | base64 | tr -d '\n')
          if [ "$ref" -nt "$f" ]; then echo "VT-PRELOADER stale $name -"; continue; fi
          line=$(grep -m1 -E '\[(Error|Fatal)' -- "$f" 2> /dev/null | base64 | tr -d '\n')
          echo "VT-PRELOADER fresh $name ${line:--}"
        done
        echo "VT-PRELOADER-END"
        """.ReplaceLineEndings("\n");
    public static readonly string PowerShellPreloader = """
        $ref = Join-Path $dir 'pid'
        $since = if ([IO.File]::Exists($ref)) { [IO.File]::GetLastWriteTimeUtc($ref) } else { [IO.Directory]::GetLastWriteTimeUtc($dir) }
        $utf8 = New-Object Text.UTF8Encoding $false
        if ([IO.Directory]::Exists($install)) {
            foreach ($f in @([IO.Directory]::GetFiles($install, 'preloader_*.log'))) {
                $name = [Convert]::ToBase64String($utf8.GetBytes([IO.Path]::GetFileName($f)))
                if ([IO.File]::GetLastWriteTimeUtc($f) -lt $since) { 'VT-PRELOADER stale ' + $name + ' -'; continue }
                $first = @([IO.File]::ReadAllLines($f) | Where-Object { $_ -match '\[(Error|Fatal)' } | Select-Object -First 1)
                $line = if ($first.Count -ne 0) { [Convert]::ToBase64String($utf8.GetBytes($first[0])) } else { '-' }
                'VT-PRELOADER fresh ' + $name + ' ' + $line
            }
        }
        'VT-PRELOADER-END'
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Why a client never wrote a BepInEx log line, from BepInEx's preloader logs beside the game: the first error of each one
    /// this launch wrote (#254), and the names of older ones, which are not this launch's and explain nothing. Null when it
    /// cannot be read.
    /// </summary>
    public static async Task<(IReadOnlyList<(string Name, string? FirstError)> Fresh, IReadOnlyList<string> Stale)?> ReadPreloaderAsync(IGameHost host, string install, string launchDirectory)
    {
        try
        {
            var result = await host.RunAsync(Preloader(host.Shell.Kind), new Dictionary<string, string> { ["install"] = install, ["dir"] = launchDirectory },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!result.Succeeded || InteractiveClient.Line(result.Stdout, "VT-PRELOADER-END") == null) return null;
            var fresh = new List<(string, string?)>(); var stale = new List<string>();
            foreach (string line in result.Stdout.Split('\n'))
                if (line.Trim().Split(' ') is ["VT-PRELOADER", var kind, var name, var error])
                {
                    string Decode(string value) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)).Trim();
                    if (kind == "stale") stale.Add(Decode(name));
                    else fresh.Add((Decode(name), error == "-" ? null : Decode(error)));
                }
            return (fresh, stale);
        }
        catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or FormatException or InvalidOperationException) { return null; }
    }

    // Variables: log, to.
    public static readonly string BashMoveAside = """
        set -u
        if [ ! -e "$log" ]; then echo "VT-NONE"; exit 0; fi
        mkdir -p -- "$(dirname -- "$to")" && mv -f -- "$log" "$to" || exit 3
        echo "VT-MOVED"
        """.ReplaceLineEndings("\n");

    public static readonly string PowerShellMoveAside = """
        if (-not [IO.File]::Exists($log)) { 'VT-NONE'; exit 0 }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
        [IO.File]::Move($log, $to)
        'VT-MOVED'
        """.ReplaceLineEndings("\n");

    // Variables: install, dir. The game's BepInEx log and Unity's Player.log (in the host user's profile) are copied into the
    // launch directory as game-0.log and game-1.log, or an .absent note says the game never wrote one.
    public static readonly string BashKeep = """
        set -u
        [ -d "$dir" ] || exit 3
        keep() { if [ -f "$1" ]; then cp -- "$1" "$dir/$2" || exit 3; else printf 'The game did not write %s\n' "$1" > "$dir/$2.absent" || exit 3; fi; }
        keep "$install/BepInEx/LogOutput.log" game-0.log
        keep "${HOME:-/nonexistent}/.config/unity3d/IronGate/Valheim/Player.log" game-1.log
        # BepInEx's preloader crash logs beside the game (#254): this launch's (not older than its pid file) are kept; older
        # ones, left by an earlier start, are only named.
        ref="$dir/pid"; [ -e "$ref" ] || ref="$dir"
        n=0; stale=""
        for f in "$install"/preloader_*.log; do
          [ -f "$f" ] || continue
          if [ "$ref" -nt "$f" ]; then stale="$stale$(basename -- "$f")
        "; else n=$((n + 1)); cp -- "$f" "$dir/game-2.preloader-$n.log" || exit 3; fi
        done
        if [ -n "$stale" ]; then printf 'Older preloader logs beside the game, from before this launch (not kept):\n%s' "$stale" > "$dir/preloader.stale" || exit 3; fi
        echo "VT-KEPT"
        """.ReplaceLineEndings("\n");

    public static readonly string PowerShellKeep = """
        if (-not [IO.Directory]::Exists($dir)) { exit 3 }
        function Save-VtLog([string]$from, [string]$name) {
            $to = Join-Path $dir $name
            if ([IO.File]::Exists($from)) { [IO.File]::Copy($from, $to, $true) } else { [IO.File]::WriteAllText($to + '.absent', 'The game did not write ' + $from) }
        }
        Save-VtLog (Join-Path $install 'BepInEx\LogOutput.log') 'game-0.log'
        Save-VtLog (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\LocalLow\IronGate\Valheim\Player.log') 'game-1.log'
        # BepInEx's preloader crash logs beside the game (#254): this launch's (not older than its pid file) are kept; older ones are only named.
        $ref = Join-Path $dir 'pid'; if (-not [IO.File]::Exists($ref)) { $ref = $dir }
        $since = [IO.File]::GetLastWriteTimeUtc($ref); if ($ref -eq $dir) { $since = [IO.Directory]::GetLastWriteTimeUtc($dir) }
        $n = 0; $stale = @()
        $logs = if ([IO.Directory]::Exists($install)) { @([IO.Directory]::GetFiles($install, 'preloader_*.log')) } else { @() }
        foreach ($f in $logs) {
            if ([IO.File]::GetLastWriteTimeUtc($f) -lt $since) { $stale += [IO.Path]::GetFileName($f) }
            else { $n++; [IO.File]::Copy($f, (Join-Path $dir ('game-2.preloader-' + $n + '.log')), $true) }
        }
        if ($stale.Count -ne 0) { [IO.File]::WriteAllText((Join-Path $dir 'preloader.stale'), "Older preloader logs beside the game, from before this launch (not kept):`n" + ($stale -join "`n")) }
        'VT-KEPT'
        """.ReplaceLineEndings("\n");
}

internal static class HostedRunScripts
{
    // Windows counterpart of Retire. Only this run's own runtime copy may be removed, and retained files must resolve
    // below it without a reparse point. Variables: runtime, keep, run, files, perfile, total.
    public static readonly string WindowsRetire = """
        if (-not $run -or $run -match '[\\/]' -or $run -eq '.' -or $run -eq '..') { exit 3 }
        $parent = [IO.Path]::GetDirectoryName($runtime)
        if ([IO.Path]::GetFileName($runtime) -cne 'runtime' -or [IO.Path]::GetFileName($parent) -cne $run -or
            [IO.Path]::GetFileName($keep) -cne 'runtime-changes' -or [IO.Path]::GetDirectoryName($keep) -cne $parent) { exit 3 }
        if ([IO.Directory]::Exists($keep) -or [IO.File]::Exists($keep)) { exit 3 }
        [void][IO.Directory]::CreateDirectory($keep)
        if (-not [IO.Directory]::Exists($runtime)) { 'VT-RETIRED 0 0'; exit 0 }
        if ([IO.File]::GetAttributes($runtime) -band [IO.FileAttributes]::ReparsePoint) { exit 3 }
        $root = [IO.Path]::GetFullPath($runtime).TrimEnd('\', '/') + '\'
        $kept = [long]0
        foreach ($line in ($files -split "`n")) {
            if (-not $line) { continue }
            $relative = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line))
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Split([char[]]'\/') -contains '..') { 'VT-NOTKEPT ' + $line + ' -1'; continue }
            $source = [IO.Path]::GetFullPath((Join-Path $runtime $relative))
            if (-not $source.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists($source)) { 'VT-NOTKEPT ' + $line + ' -1'; continue }
            $walk = [IO.Path]::GetDirectoryName($source)
            $linked = $false
            while ($walk -and $walk.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                if ([IO.File]::GetAttributes($walk) -band [IO.FileAttributes]::ReparsePoint) { $linked = $true; break }
                $walk = [IO.Path]::GetDirectoryName($walk)
            }
            if ($linked -or ([IO.File]::GetAttributes($source) -band [IO.FileAttributes]::ReparsePoint)) { 'VT-NOTKEPT ' + $line + ' -1'; continue }
            $size = (New-Object IO.FileInfo $source).Length
            if ($size -gt [long]$perfile -or $kept + $size -gt [long]$total) { 'VT-NOTKEPT ' + $line + ' ' + $size; continue }
            $target = Join-Path $keep $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy($source, $target)
            $kept += $size
        }
        $bytes = [long]0
        foreach ($file in [IO.Directory]::EnumerateFiles($runtime, '*', [IO.SearchOption]::AllDirectories)) { $bytes += (New-Object IO.FileInfo $file).Length }
        [IO.Directory]::Delete($runtime, $true)
        'VT-RETIRED ' + $bytes + ' ' + $kept
        """.ReplaceLineEndings("\n");

    public static readonly string WindowsDropKept = """
        if (-not $run -or $run -match '[\\/]' -or $run -eq '.' -or $run -eq '..') { exit 3 }
        if ([IO.Path]::GetFileName($keep) -cne 'runtime-changes' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($keep)) -cne $run) { exit 3 }
        if ([IO.Directory]::Exists($keep)) { [IO.Directory]::Delete($keep, $true) }
        'VT-DROPPED'
        """.ReplaceLineEndings("\n");

    // Keeps the listed files (base64 relative paths, one per line) from a run's runtime copy in $keep, within the size
    // limits, then removes the copy. Only a directory named runtime directly inside the run's own directory ($run) is
    // ever removed; a path that leaves the copy, a link or anything but a regular file is listed, never copied.
    public static readonly string Retire = """
        set -u
        case "$run" in ''|*/*|.|..) exit 3;; esac
        [ "$(basename -- "$runtime")" = runtime ] || exit 3
        [ "$(basename -- "$(dirname -- "$runtime")")" = "$run" ] || exit 3
        [ "$(basename -- "$keep")" = runtime-changes ] || exit 3
        [ "$(dirname -- "$keep")" = "$(dirname -- "$runtime")" ] || exit 3
        # A reused or linked destination could overwrite evidence outside this run. Leave the runtime for review instead.
        [ ! -e "$keep" ] && [ ! -L "$keep" ] || exit 3
        if [ ! -d "$runtime" ] || [ -L "$runtime" ]; then mkdir -p -- "$keep" || exit 3; echo "VT-RETIRED 0 0"; exit 0; fi
        mkdir -p -- "$keep" || exit 3
        real=$(readlink -f -- "$runtime") || exit 3
        kept=0
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            rel=$(printf '%s' "$line" | base64 -d) || exit 3
            case "$rel" in /*|..|../*|*/../*|*/..) printf 'VT-NOTKEPT %s -1\n' "$line"; continue;; esac
            src="$runtime/$rel"
            if [ ! -f "$src" ] || [ -L "$src" ]; then printf 'VT-NOTKEPT %s -1\n' "$line"; continue; fi
            # Never through a linked directory inside the copy: the file's real path must be inside the copy too.
            case "$(readlink -f -- "$src")" in "$real"/*) ;; *) printf 'VT-NOTKEPT %s -1\n' "$line"; continue;; esac
            size=$(stat -c %s -- "$src") || exit 3
            if [ "$size" -gt "$perfile" ] || [ $((kept + size)) -gt "$total" ]; then printf 'VT-NOTKEPT %s %s\n' "$line" "$size"; continue; fi
            mkdir -p -- "$(dirname -- "$keep/$rel")" && cp -p -- "$src" "$keep/$rel" || exit 3
            kept=$((kept + size))
        done <<< "$files"
        bytes=$(du -sb -- "$runtime") || exit 3
        bytes=${bytes%%$'\t'*}
        rm -rf -- "$runtime" || exit 3
        printf 'VT-RETIRED %s %s\n' "$bytes" "$kept"
        """.ReplaceLineEndings("\n");

    // Removes the run's runtime-changes directory on the host once it was fetched; only <run>/runtime-changes.
    public static readonly string DropKept = """
        set -u
        case "$run" in ''|*/*|.|..) exit 3;; esac
        [ "$(basename -- "$keep")" = runtime-changes ] || exit 3
        [ "$(basename -- "$(dirname -- "$keep")")" = "$run" ] || exit 3
        rm -rf -- "$keep" || exit 3
        echo "VT-DROPPED"
        """.ReplaceLineEndings("\n");
}
