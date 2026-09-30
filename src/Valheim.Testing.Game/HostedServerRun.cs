using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>Test seams for a <see cref="PinnedServerRun"/> on a profile's hosts: fake hosts and transports instead of ssh and sockets.</summary>
internal sealed class HostedSeams
{
    /// <summary>Builds the host of that profile name instead of <see cref="EnvironmentProfile.CreateHost"/>.</summary>
    public Func<string, IGameHost>? Host { get; init; }
    /// <summary>Connects to ValheimCLI at a tunnel's local port instead of a <c>CliTransport</c>.</summary>
    public Func<int, IGameTransport>? Connect { get; init; }
    /// <summary>False skips the state pushes (a fake transport has none).</summary>
    public bool StateWaits { get; init; } = true;
    public string? RunId { get; init; }
}

/// <summary>
/// The parts of a <see cref="PinnedServerRun"/> that differ when its dedicated server runs on the environment profile's server
/// host (<c>--profile</c>): the host lock, the runtime copied from the host's install and verified there, the world copy
/// shipped and verified there, the port check, the loopback CLI tunnel, the owned session through <see cref="HostServer"/>,
/// clients through <see cref="InteractiveClient"/>, and the teardown that fetches evidence, closes the tunnel and releases the
/// locks.
/// </summary>
internal sealed class HostedServerRun
{
    internal const string BepInExLog = "BepInEx/LogOutput.log", UnityLog = "toolkit-unity.log";
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(60), Long = TimeSpan.FromMinutes(15);
    private readonly HostedSeams _seams;
    private readonly string _owner;
    private readonly List<(string Host, HostLock Lock)> _clientLocks = [];
    private HostLock? _lock;
    private CliTunnel? _tunnel;
    private HostListing? _runtime;
    private bool _worldShipped, _serverMayRun;
    private int _clients;

    private HostedServerRun(EnvironmentProfile profile, GameRole role, HostProfile hostProfile, IGameHost host, string runId, string runner, HostedSeams seams)
    {
        Profile = profile; Role = role; HostProfile = hostProfile; Host = host; RunId = runId; _seams = seams; _owner = runner + " " + runId;
        RunDirectory = HostInstall.Join(role.Runtime, runId);
        RuntimeDirectory = HostInstall.Join(RunDirectory, "runtime");
        WorldDirectory = HostInstall.Join(RunDirectory, "world");
    }

    public EnvironmentProfile Profile { get; }
    public GameRole Role { get; }
    public HostProfile HostProfile { get; }
    public IGameHost Host { get; }
    public string RunId { get; }
    /// <summary>This run's directory on the server host: <c>runtime</c>, <c>world</c> and <c>boot-N</c>. Never deleted automatically.</summary>
    public string RunDirectory { get; }
    public string RuntimeDirectory { get; }
    public string WorldDirectory { get; }
    /// <summary>The runtime copy's files on the host, once copied.</summary>
    public IReadOnlyDictionary<string, string> RuntimeHashes => _runtime?.Files ?? new Dictionary<string, string>();

    /// <summary>Refuses a profile and plan that cannot run a server on the profile's server host, before anything is touched.</summary>
    public static HostedServerRun Create(EnvironmentProfile profile, ServerRunPlan plan, string runner, HostedSeams? seams)
    {
        var role = profile.Server ?? throw new ArgumentException("The environment profile names no server; --profile runs the dedicated server on the profile's server host.");
        var hostProfile = profile.Hosts[role.Host];
        if (hostProfile.Platform != "linux" || hostProfile.Shell != "bash")
            throw new PlatformNotSupportedException($"The profile's server host '{role.Host}' is {hostProfile.Platform} with {hostProfile.Shell}. A dedicated server on a host runs on Linux through bash " +
                "(over SSH, in a container or on this Linux machine); for a Windows server, run the runner on that machine without --profile.");
        if (role.CliPort != plan.Port)
            throw new ArgumentException($"The plan's ValheimCLI port {plan.Port} is not the profile server's cliPort {role.CliPort}; the runtime's [Server] Port must be both.");
        int at = Array.IndexOf(plan.Arguments, "-port");
        if (at >= 0 && at + 1 < plan.Arguments.Length && int.TryParse(plan.Expand(plan.Arguments[at + 1], "", ""), NumberStyles.None, CultureInfo.InvariantCulture, out int gamePort) && gamePort != role.GamePort)
            throw new ArgumentException($"The plan's -port {gamePort} is not the profile server's gamePort {role.GamePort}.");
        seams ??= new HostedSeams();
        string runId = seams.RunId ?? "run-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var host = seams.Host?.Invoke(role.Host) ?? profile.CreateHost(role.Host);
        return new HostedServerRun(profile, role, hostProfile, host, runId, runner, seams);
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
        await report.StepAsync("take the server host's lock", async () => _lock = await Host.AcquireLockAsync(HostProfile.Lock, _owner, Quick, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        // Only an unpinned plan may leave out the manifest; the copy is then recorded as found.
        bool verified = pinned || plan.Runtime.Sha256.Count != 0;
        await report.StepAsync(verified ? "copy and verify pinned runtime on the server host" : "copy unpinned runtime on the server host as found", async () =>
        {
            await HostInstall.CopyAsync(Host, Role.Install, RuntimeDirectory, Long, cancellation).ConfigureAwait(false);
            _runtime = await HostInstall.ListAsync(Host, RuntimeDirectory, Long, null, cancellation).ConfigureAwait(false);
            if (verified) HostInstall.RequireSame(plan.Runtime.Sha256, _runtime, "runtime copy");
        }).ConfigureAwait(false);
    }

    /// <summary>Ships the verified local world copy to the host and verifies every file there.</summary>
    public Task ShipWorldAsync(ScenarioReport report, string localWorld, CancellationToken cancellation) =>
        report.StepAsync("ship and verify the world copy on the server host", async () =>
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
        report.Step("copied runtime has the plan's server executable", () =>
        {
            var platform = HostInstall.DetectServer(runtime);
            plan.CheckExecutable(platform);
            ServerRunPlan.CheckLaunchHost(platform, windowsHost: false);
            if (!runtime.Executables.Contains(ServerLaunch.LinuxExecutable))
                throw new InvalidOperationException($"{ServerLaunch.LinuxExecutable} is not executable in the runtime copy on {Host.Name}; restore its mode (chmod u+x) in the install {Role.Install}.");
        });
        report.Step("copied runtime's BepInEx patchers are the plan's", () => HostInstall.RequirePatchers(runtime, plan.Patchers, "runtime"));
        report.Step(pinned ? "copied runtime is the pinned game build, BepInEx core and patchers" : "record the unpinned runtime's game build, BepInEx core and patchers", () =>
            (pinned ? HostInstall.CheckPins(plan.RuntimePins ?? throw new ArgumentException("Pin the runtime's game build, BepInEx core and patchers in runtimePins, or opt out explicitly with \"pinning\": \"none\"."), runtime, "runtime")
                : HostInstall.Pins(runtime)).Record(report.Provenance, "runtime"));
    }

    /// <summary>Refuses a busy CLI port on the host, then opens the loopback tunnel to it.</summary>
    public async Task OpenAsync(ScenarioReport report, CancellationToken cancellation)
    {
        // Catch an occupied port without issuing even a read to an unrelated server.
        await report.StepAsync("CLI port is free on the server host", () => HostInstall.RequirePortFreeAsync(Host, Role.CliPort, Quick, cancellation)).ConfigureAwait(false);
        await report.StepAsync("open the loopback CLI tunnel to the server host", async () =>
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
            var launch = HostServerLaunch.Create(RuntimeDirectory, plan.LaunchArguments(RuntimeDirectory, WorldDirectory), environment);
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
                    ["startedUtc"] = DateTime.UtcNow, ["world"] = WorldDirectory,
                }, plan.Pinned)));
            }
            catch { process.Stop(TimeSpan.FromSeconds(15)); throw; }
            return process;
        }, () => new RecordingTransport(Connect(tunnel), Path.Combine(run.Output, "connection-" + ++connection + ".jsonl"), plan.Pinned ? null : EnvironmentPinning.NotPinned),
            WorldDirectory, plan.ExpectCommand, options.SessionCapability,
            TimeSpan.FromSeconds(plan.StartupSeconds), TimeSpan.FromSeconds(plan.CommandSeconds), cancellation: run.Cancellation)
        {
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
    /// An owned client on the profile's client host, started in its desktop session (<see cref="InteractiveClient"/>) with the
    /// checks <see cref="ClientSession.Launch(ClientRunPlan, string, CancellationToken)"/> makes locally, made on the host: the
    /// install's patchers and pins, a free CLI port. ValheimCLI is reached through the host's tunnel. Disposing the session stops
    /// only that client, keeps its logs, fetches them to <c>client-N</c> in the output and closes the tunnel.
    /// </summary>
    public ClientSession OpenClient(string output, ClientRunPlan plan, string name, CancellationToken cancellation) =>
        OpenClientAsync(output, plan, name, cancellation).GetAwaiter().GetResult();

    private async Task<ClientSession> OpenClientAsync(string output, ClientRunPlan plan, string name, CancellationToken cancellation)
    {
        if (!Profile.Clients.TryGetValue(name, out var role)) throw new ArgumentException($"No client '{name}' in the environment profile.", nameof(name));
        var hostProfile = Profile.Hosts[role.Host];
        var platform = hostProfile.Platform switch { "windows" => ClientPlatform.Windows, "linux" => ClientPlatform.Linux, _ => ClientPlatform.MacOS };
        var launch = HostClientLaunch.Create(platform, role.Install, plan.LaunchArguments, secretVariables: plan.PasswordVariable is { } password ? new[] { password } : null);
        var host = role.Host == Role.Host ? Host : _seams.Host?.Invoke(role.Host) ?? Profile.CreateHost(role.Host);
        // The server's lock covers its own host; another client host is locked for the rest of the run.
        if (role.Host != Role.Host && !_clientLocks.Any(held => held.Host == role.Host))
            _clientLocks.Add((role.Host, await host.AcquireLockAsync(hostProfile.Lock, _owner, Quick, cancellation).ConfigureAwait(false)));
        int n = ++_clients;
        string runDirectory = HostInstall.Join(role.Runtime, RunId), launchDirectory = HostInstall.Join(runDirectory, "client-" + n);
        string log = HostInstall.Join(role.Install, BepInExLog);
        var listing = await HostInstall.ListAsync(host, role.Install, Long, ["*_Data/Managed", "BepInEx/core", "BepInEx/patchers"], cancellation).ConfigureAwait(false);
        HostInstall.RequirePatchers(listing, plan.Patchers, "client install");
        if (plan.Pinned)
            HostInstall.CheckPins(plan.InstallPins ?? throw new ArgumentException("Pin the owned client's game build, BepInEx core and patchers in installPins, or opt out explicitly with \"pinning\": \"none\"."), listing, "client install");
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
            var display = platform != ClientPlatform.Linux ? null : host.Kind == GameHostKind.Container ? LinuxDisplay.ClientContainer : new LinuxDisplay();
            var start = TimeSpan.FromSeconds(Math.Max(30, plan.StartSeconds));
            return ClientSession.Launch(plan, output,
                () => new HostedClientProcess(InteractiveClient.StartAsync(host, launch, launchDirectory, start, display, cancellation).GetAwaiter().GetResult(), host, role.Install, tunnel, local),
                () => Connect(tunnel),
                async (left, token) =>
                {
                    var clock = Stopwatch.StartNew();
                    (await host.WaitForLogAsync(log, 0, StartupEvents.CliListening, StartupEvents.StartupFailures, left, token).ConfigureAwait(false)).EnsureMatched();
                    if (!_seams.StateWaits) return;
                    using var states = StateWait.Connect(tunnel.Address, tunnel.LocalPort);
                    await states.WaitAsync([StateWait.MainMenu], left - clock.Elapsed, cancellation: token).ConfigureAwait(false);
                }, cancellation, null,
                [new RunLog($"client-{n} BepInEx log", Path.Combine(local, "game-0.log"), Required: true), new RunLog($"client-{n} Player.log", Path.Combine(local, "game-1.log"))]);
        }
        catch { tunnel.Dispose(); throw; }
    }

    /// <summary>
    /// After the owned server stopped: fetches the host's world copy, closes the tunnel and releases the locks, each as its own
    /// step. With <paramref name="serverStopped"/> false the server host's lock is kept, because the server may still run there.
    /// Returns the failures; it never throws.
    /// </summary>
    public async Task<IReadOnlyList<Exception>> TeardownAsync(ScenarioReport report, string output, bool launched, bool serverStopped)
    {
        serverStopped &= !_serverMayRun;
        var failures = new List<Exception>();
        async Task Try(string step, Func<Task> action)
        {
            try { await report.StepAsync(step, action).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        }
        if (launched && _worldShipped && serverStopped)
            await Try("fetch the server host's world copy", () => Host.FetchDirectoryAsync(WorldDirectory, Path.Combine(output, "host-world"), Long)).ConfigureAwait(false);
        if (_tunnel != null) await Try("close the CLI tunnel", () => { _tunnel.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        foreach (var (name, held) in _clientLocks) await Try($"release client host {name}'s lock", () => ReleaseAsync(held)).ConfigureAwait(false);
        if (_lock != null)
            await Try("release the server host's lock", () => serverStopped ? ReleaseAsync(_lock)
                : throw new HostLockException(new HostLockResult(HostLockState.Unknown, _lock.Owner,
                    $"Kept {_lock.Path} on {Host.Name}: the owned server there may still run. Remove {_lock.Path}/owner by hand once it has stopped."))).ConfigureAwait(false);
        return failures;
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
            if (current is AggregateException aggregate) return aggregate.InnerExceptions.Select(UnknownOutcome).FirstOrDefault(reason => reason != null);
        }
        return null;
    }
}

/// <summary>
/// A client <see cref="InteractiveClient"/> started for a <see cref="PinnedServerRun"/>: stopping it kills only that process,
/// keeps its BepInEx log and Player.log in its launch directory, fetches that directory here and closes its CLI tunnel.
/// </summary>
internal sealed class HostedClientProcess(InteractiveClientProcess process, IGameHost host, string install, CliTunnel tunnel, string evidence) : IServerProcess
{
    private bool _kept;
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public Task<int> WaitForExitAsync(CancellationToken cancellation) => process.WaitForExitAsync(cancellation);

    public void Stop(TimeSpan timeout)
    {
        try
        {
            process.Stop(timeout);
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
