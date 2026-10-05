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
        // A campaign prepared the server's disposable install already (<runtime>/vt-prep-<id>-server/runtime): that is the one
        // copy the server runs from. A standalone run makes its own under <runtime>/<runId>.
        Prepared = preparedRuntime != null;
        RunDirectory = HostInstall.Join(role.Runtime, runId);
        RuntimeDirectory = preparedRuntime ?? HostInstall.Join(RunDirectory, "runtime");
        WorldDirectory = HostInstall.Join(RunDirectory, "world");
        // The retire keeps what changed beside the copy it retires (<copy's directory>/runtime-changes), then fetches it.
        _copyDirectory = RuntimeDirectory[..RuntimeDirectory.LastIndexOfAny(['/', '\\'])];
        _copyDirectoryName = _copyDirectory[(_copyDirectory.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    private readonly string _copyDirectory, _copyDirectoryName;
    /// <summary>Whether the runtime is a campaign's prepared install (verified in place) rather than a copy this run makes.</summary>
    public bool Prepared { get; }
    /// <summary>After <see cref="TeardownAsync"/>: the runtime copy stayed (kept on request, or its server may still run).</summary>
    public bool RuntimeRetained { get; private set; }

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

    /// <summary>With <paramref name="prepared"/>, the server role's install is a campaign's prepared disposable install: the run uses it as its runtime.</summary>
    internal static HostedServerRun Create(ResolvedEnvironment profile, ServerRunPlan plan, string runner, HostedSeams? seams, bool prepared)
    {
        var role = profile.Server ?? throw new ArgumentException("The environment places no dedicated server.");
        var hostProfile = profile.Hosts[role.Host];
        if (Refusal(hostProfile, role, plan) is { } refusal) throw new ArgumentException($"The server environment on host '{role.Host}': {refusal}");
        seams ??= new HostedSeams();
        string runId = seams.RunId ?? "run-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
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
        string? planned = plan.Executable == ServerLaunch.WindowsExecutable || plan.Runtime.Sha256.ContainsKey(ServerLaunch.WindowsExecutable) ? "windows"
            : plan.Executable == ServerLaunch.LinuxExecutable || plan.Runtime.Sha256.ContainsKey(ServerLaunch.LinuxExecutable) ? "linux" : null;
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
            await HostInstall.CopyAsync(Host, Role.Install, RuntimeDirectory, Long, cancellation).ConfigureAwait(false);
            _runtime = await HostInstall.ListAsync(Host, RuntimeDirectory, Long, null, cancellation).ConfigureAwait(false);
            if (verified) HostInstall.RequireSame(plan.Runtime.Sha256, _runtime, "runtime copy");
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
            if (platform == ServerPlatform.Linux && !runtime.Executables.Contains(ServerLaunch.LinuxExecutable))
                throw new InvalidOperationException($"{ServerLaunch.LinuxExecutable} is not executable in the runtime copy on {Host.Name}; restore its mode (chmod u+x) in the install {Role.Install}.");
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
            var launch = Host.Shell.Kind == HostShellKind.PowerShell
                ? HostServerLaunch.CreateWindows(RuntimeDirectory, plan.LaunchArguments(RuntimeDirectory, WorldDirectory), environment)
                : HostServerLaunch.Create(RuntimeDirectory, plan.LaunchArguments(RuntimeDirectory, WorldDirectory), environment);
            int n = ++boot;
            string local = Path.Combine(run.Output, "boot-" + n), bootDirectory = HostInstall.Join(RunDirectory, "boot-" + n);
            HostServerProcess process;
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
                _accounts.Add(held = new ClientAccount(name, hold));
                hold.Record(report);
            }
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
        var launch = HostClientLaunch.Create(platform, role.Install, plan.LaunchArguments, secretVariables: plan.PasswordVariable is { } password ? new[] { password } : null);
        var host = ClientHost(role);
        var account = await HoldAccountAsync(report, name, () => host, cancellation).ConfigureAwait(false);
        // The server's lock covers its own host; another client host is locked for the rest of the run.
        if (role.Host != Role.Host)
        {
            await _clientLockGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (!_clientLocks.Any(held => held.Host == role.Host))
                    _clientLocks.Add((role.Host, await host.AcquireLockAsync(hostProfile.Lock, _owner, Quick, cancellation).ConfigureAwait(false)));
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
                    var started = account == null ? InteractiveClient.StartAsync(host, launch, launchDirectory, start, display, cancellation)
                        : InteractiveClient.StartAsync(account.Hold, host, launch, launchDirectory, start, display, cancellation);
                    var process = new HostedClientProcess(started.GetAwaiter().GetResult(), host, role.Install, tunnel, local);
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
                        throw new InvalidOperationException($"BepInEx wrote no fresh log line on {host.Name} within {plan.BepInExSeconds}s. " +
                            $"The game may have reached its menu without BepInEx; check winhttp.dll, doorstop_config.ini and BepInEx/core as one pack. " +
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
                    _clientLocks.Add((role.Host, await host.AcquireLockAsync(hostProfile.Lock, _owner, Quick, cancellation).ConfigureAwait(false)));
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
    /// After the owned server stopped: fetches the host's world copy, closes the tunnel and releases the locks, each as its own
    /// step. With <paramref name="serverStopped"/> false the server host's lock is kept, because the server may still run there.
    /// Returns the failures; it never throws.
    /// </summary>
    public async Task<IReadOnlyList<Exception>> TeardownAsync(ScenarioReport report, string output, bool launched, bool serverStopped, bool keepRuntime = false)
    {
        serverStopped &= !_serverMayRun;
        var failures = new List<Exception>();
        async Task Try(string step, Func<Task> action)
        {
            try { await report.StepAsync(StepPhase.Cleanup, step, action).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        }
        if (launched && _worldShipped && serverStopped)
            await Try("fetch the server host's world copy", () => Host.FetchDirectoryAsync(WorldDirectory, Path.Combine(output, "host-world"), Long)).ConfigureAwait(false);
        if (_runtime != null) await Try("remove the server host's runtime copy, keeping what the run changed", () => RetireRuntimeAsync(report, output, launched, serverStopped, keepRuntime)).ConfigureAwait(false);
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
                    throw new SteamAccountLeaseException(SteamAccountLeaseState.Unknown, account.Hold.Pool, [], $"Kept the lease on Steam account {account.Hold.Account}: the client " +
                        $"{account.Client} may still run on {account.Hold.ClientHost}. Without renewals it ends at {account.Hold.ExpiresUtc:u}.");
                }
                await account.Hold.ReleaseAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        foreach (var (name, held) in _clientLocks) await Try($"release client host {name}'s lock", () =>
            _localMacProcesses.Any(item => item.Host == name && !item.Process.HasExited)
                ? throw new HostLockException(new HostLockResult(HostLockState.Unknown, held.Owner,
                    $"Kept {held.Path} on {name}: an owned local Mac client may still run. Confirm its recorded process has stopped before releasing this lock."))
                : ReleaseAsync(held)).ConfigureAwait(false);
        if (_lock != null)
            await Try("release the server host's lock", () => serverStopped ? ReleaseAsync(_lock)
                : throw new HostLockException(new HostLockResult(HostLockState.Unknown, _lock.Owner,
                    $"Kept {_lock.Path} on {Host.Name}: the owned server there may still run. Remove {_lock.Path}/owner by hand once it has stopped."))).ConfigureAwait(false);
        return failures;
    }

    // As on this machine (PinnedServerRun.RetireRuntime): after a clean stop, keep what the run added or changed in the host's
    // runtime copy (by its hashes against the listing made after copying) and remove the copy, which is about 2 GB of the
    // host install's own files. A copy whose server may still run, or one kept on request, stays and the report says where.
    // A cleanup problem is recorded and rethrown, so it fails the Cleanup step (the scenario's own result stands). Still
    // under the server host's lock.
    private async Task RetireRuntimeAsync(ScenarioReport report, string output, bool launched, bool serverStopped, bool keep)
    {
        string where = Host.Name + ":" + RuntimeDirectory;
        if (keep) { RuntimeRetained = true; report.Provenance["runtimeCopy"] = $"kept {where}: kept on request ({PinnedServerRun.KeepRuntimeVariable}=1 or KeepRuntime)"; return; }
        if (!serverStopped)
        {
            RuntimeRetained = true;
            report.Provenance["runtimeCopy"] = $"kept {where}: the owned server there may still run; remove the directory once it has stopped";
            Console.Error.WriteLine("Warning: " + report.Provenance["runtimeCopy"]);
            return;
        }
        try
        {
            var before = _runtime!.Files;
            var after = launched ? (await HostInstall.ListAsync(Host, RuntimeDirectory, Long).ConfigureAwait(false)).Files : before;
            var added = after.Keys.Where(name => !before.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
            var changed = after.Where(file => before.TryGetValue(file.Key, out var hash) && !hash.Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Key).Order(StringComparer.Ordinal).ToList();
            var missing = before.Keys.Where(name => !after.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
            // The failure limits: the run's own result is not final yet (its log scan comes after this teardown, which first keeps
            // the logs of any client still open), so keep as much as a failed run would.
            var (perFile, total) = PinnedServerRun.RetainLimits(passed: false);
            string keepDirectory = HostInstall.Join(_copyDirectory, "runtime-changes");
            var result = (await Host.RunAsync(Host.Shell.Kind == HostShellKind.PowerShell ? HostedRunScripts.WindowsRetire : HostedRunScripts.Retire, new Dictionary<string, string>
            {
                ["runtime"] = RuntimeDirectory, ["keep"] = keepDirectory, ["run"] = _copyDirectoryName,
                ["files"] = string.Join('\n', added.Concat(changed).Select(name => Convert.ToBase64String(Encoding.UTF8.GetBytes(name)))),
                ["perfile"] = perFile.ToString(CultureInfo.InvariantCulture), ["total"] = total.ToString(CultureInfo.InvariantCulture),
            }, Long).ConfigureAwait(false)).EnsureSuccess($"Removing the runtime copy {where}");
            var done = InteractiveClient.Line(result.Stdout, "VT-RETIRED ")?.Split(' ');
            if (done is not [var freed, var kept]) throw new HostOperationException($"Unexpected reply while removing the runtime copy {where}", result);
            // "VT-NOTKEPT <base64 path> <bytes>", with -1 bytes for anything but a regular file inside the copy.
            var notKept = new List<NotKeptFile>();
            foreach (string line in result.Stdout.Split('\n'))
            {
                if (line.Split(' ') is not ["VT-NOTKEPT", var encoded, var size]) continue;
                string name = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                long bytes = long.Parse(size, CultureInfo.InvariantCulture);
                notKept.Add(bytes < 0 ? new(name, null, null, "not a regular file inside the copy")
                    : new(name, bytes, after.GetValueOrDefault(name), $"past the size limits ({DiskSpace.Format(perFile)} per file, {DiskSpace.Format(total)} in all)"));
            }
            if (!launched)
            {
                // Nothing ran, so nothing changed: the (empty) keep folder goes too, leaving the copy's directory empty for its owner.
                (await Host.RunAsync(Host.Shell.Kind == HostShellKind.PowerShell ? HostedRunScripts.WindowsDropKept : HostedRunScripts.DropKept,
                    new Dictionary<string, string> { ["keep"] = keepDirectory, ["run"] = _copyDirectoryName }, Quick).ConfigureAwait(false))
                    .EnsureSuccess($"Removing {Host.Name}:{keepDirectory}");
                report.Provenance["runtimeCopy"] = $"removed {where} ({DiskSpace.Format(long.Parse(freed, CultureInfo.InvariantCulture))}): nothing was launched";
                return;
            }
            string local = Path.Combine(output, "runtime-changes");
            await Host.FetchDirectoryAsync(keepDirectory, local, Long).ConfigureAwait(false);
            // Fetched: the host's copy of the changes is not needed twice.
            (await Host.RunAsync(Host.Shell.Kind == HostShellKind.PowerShell ? HostedRunScripts.WindowsDropKept : HostedRunScripts.DropKept,
                new Dictionary<string, string> { ["keep"] = keepDirectory, ["run"] = _copyDirectoryName }, Quick).ConfigureAwait(false))
                .EnsureSuccess($"Removing {Host.Name}:{keepDirectory} after fetching it");
            var retired = new RetiredCopy(where, local, added, changed, missing, notKept, long.Parse(kept, CultureInfo.InvariantCulture), long.Parse(freed, CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(local, "changes.json"), JsonSerializer.Serialize(retired, new JsonSerializerOptions { WriteIndented = true }));
            report.Provenance["runtimeCopy"] = retired.ToString();
        }
        catch (Exception error) when (error is HostOperationException or IOException or InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            report.Provenance["runtimeCopy"] = $"cleanup failed, {where} may remain: {error.Message}";
            throw;
        }
    }

    private static async Task ReleaseAsync(HostLock held)
    {
        var result = await held.ReleaseAsync().ConfigureAwait(false);
        if (result.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(result);
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
internal sealed class ClientAccount(string client, SteamAccountHold hold)
{
    public string Client { get; } = client;
    public SteamAccountHold Hold { get; } = hold;
    public ClientSession? Session { get; set; }
    public IOwnedProcess? Process { get; set; }
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
