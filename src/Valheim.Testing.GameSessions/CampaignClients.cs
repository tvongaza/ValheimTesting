using System.Diagnostics;
using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// A campaign's named clients on their assigned hosts (#258 step 8b: the client half of what was <see cref="HostedServerRun"/>,
/// owned once whether the campaign has a dedicated server or a hosting client): each client's placement (an owned client in its
/// host's desktop session through <see cref="InteractiveClient"/>, a local macOS client in this runner's GUI session, an attached
/// client on its leased account), its Steam identity lease, the lock of each client host the dedicated server does not already
/// hold, and their release at teardown once the clients are gone. Every process and lock is journalled on its host through the
/// owner's journal.
/// </summary>
internal sealed class CampaignClients
{
    private readonly IHostedRunHooks _hooks;
    private readonly object _clientState = new();
    private readonly SemaphoreSlim _clientLockGate = new(1, 1);
    private readonly string _owner;
    private readonly List<(string Host, HostLock Lock)> _clientLocks = [];
    private readonly List<(string Host, IOwnedProcess Process)> _localMacProcesses = [];
    private readonly List<ClientAccount> _accounts = [];
    private readonly (string Name, IGameHost Host)? _serverHost;
    private readonly Func<IGameHost, string, string, JournalEntry, CancellationToken, Task> _journal;
    private IGameHost? _leaseHost;
    private int _clients;

    /// <param name="serverHost">The dedicated server's host, whose lock its run holds and whose connection is reused; null without one.</param>
    /// <param name="journal">Appends an entry to a host's journal for an actor (the owner's run journal); throws when it cannot.</param>
    internal CampaignClients(ResolvedEnvironment profile, string runId, string owner, IHostedRunHooks hooks, (string Name, IGameHost Host)? serverHost,
        Func<IGameHost, string, string, JournalEntry, CancellationToken, Task> journal)
    {
        Profile = profile; RunId = runId; _owner = owner; _hooks = hooks; _serverHost = serverHost; _journal = journal;
    }

    public ResolvedEnvironment Profile { get; }
    public string RunId { get; }
    /// <summary>The client hosts this run locked (a dedicated server's own host excluded).</summary>
    public IReadOnlyList<string> LockedHosts { get { lock (_clientState) return _clientLocks.Select(held => held.Host).ToList(); } }

    private IGameHost HostNamed(string name) => name == _serverHost?.Name ? _serverHost.Value.Host : _hooks.CreateHost(Profile, name);
    private Task JournalAsync(IGameHost host, string hostName, string actor, JournalEntry entry, CancellationToken cancellation) =>
        _journal(host, hostName, actor, entry, cancellation);
    // After its effect a lost line is a warning: the effect happened, and the entry before it already names where to look.
    private async Task NoteAsync(IGameHost host, string hostName, string actor, JournalEntry entry)
    {
        try { await JournalAsync(host, hostName, actor, entry, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { Console.Error.WriteLine($"Warning: could not journal {entry.Kind} for {actor} on {hostName}: {error.Message}"); }
    }
    private Task NoteLockAsync(IGameHost host, string hostName, HostLock held, string kind) =>
        NoteAsync(host, hostName, "run", JournalEntry.Of(kind, ("lock", held.Path), ("claimant", held.Owner)));
    private static JournalEntry LeaseEntry(string kind, SteamAccountHold hold, params (string Key, string Value)[] more) =>
        JournalEntry.Of(kind, [("account", hold.Account), ("pool", hold.Pool), ("owner", hold.Owner), ("leaseId", hold.LeaseId),
            ("number", hold.LeaseNumber.ToString(CultureInfo.InvariantCulture)), ("directory", hold.LeaseDirectory), .. more]);

    /// <summary>
    /// An owned client on its environment's client host, started in its desktop session (<see cref="InteractiveClient"/>) with the
    /// checks <see cref="ClientSession.Launch(ClientRunPlan, string, CancellationToken)"/> makes locally, made on the host: the
    /// install's pins, a free CLI port. ValheimCLI is reached through the host's tunnel. Disposing the session stops
    /// only that client, keeps its logs, fetches them to <c>client-N</c> in the output and closes the tunnel. With the environment's
    /// Steam leases, the client's observed identity is leased (and its host's signed-in user checked, when asked) before anything
    /// else on its host is touched, and released at teardown once the client is gone.
    /// </summary>
    private ClientSession OpenClient(ScenarioReport report, string output, ClientRunPlan plan, string name, CancellationToken cancellation) =>
        OpenClientAsync(report, output, plan, name, cancellation).GetAwaiter().GetResult();

    /// <summary>An attached campaign client: its account is leased (and checked) before the session assumes the client.</summary>
    private ClientSession AttachClient(ScenarioReport report, string output, ClientRunPlan plan, string name, CancellationToken cancellation) =>
        AttachClientAsync(report, output, plan, name, cancellation).GetAwaiter().GetResult();

    /// <summary>
    /// A named campaign client's placement: owned, on its assigned host (its desktop session, or this runner's GUI session for a
    /// local macOS host); attached, on its leased account. The leases and host locks stay this run's, released at its teardown.
    /// </summary>
    internal IClientPlacement Placement(ScenarioReport report) => new CampaignClientPlacement(this, report);

    private sealed class CampaignClientPlacement(CampaignClients run, ScenarioReport report) : IClientPlacement
    {
        public ClientSession Open(string name, ClientRunPlan plan, string output, CancellationToken cancellation) =>
            plan.Owned ? run.OpenClient(report, output, plan, name, cancellation) : run.AttachClient(report, output, plan, name, cancellation);
    }

    private async Task<ClientSession> AttachClientAsync(ScenarioReport report, string output, ClientRunPlan plan, string name, CancellationToken cancellation)
    {
        if (!Profile.Clients.TryGetValue(name, out var role)) throw new ArgumentException($"No client '{name}' in the environment.", nameof(name));
        var account = await HoldAccountAsync(report, name, () => ClientHost(role), cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The environment has no Steam leases; attach with ClientSession.Attach.");
        return account.Session = ClientSession.Attach(plan, output, account.Hold, () => _hooks.Connect(plan.Host, plan.Port));
    }

    private IGameHost ClientHost(GameRole role) => HostNamed(role.Host);
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
                leaseHost = _leaseHost ??= HostNamed(section.LeaseHost);
            var hold = await _hooks.LeaseAsync(Profile, name, _owner + " client " + name, RunId, leaseHost, HostedTimeouts.Quick, cancellation).ConfigureAwait(false);
            lock (_clientState)
            {
                _accounts.Add(held = new ClientAccount(name, hold, leaseHost, section.LeaseHost));
                hold.Record(report);
            }
            // On the lease host, where the lease lives: which account this run holds for the client, and as whom.
            await NoteAsync(leaseHost, section.LeaseHost, name, LeaseEntry(JournalEntry.LeaseHeld, hold)).ConfigureAwait(false);
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
        // A plan without a slice follows the assigned client's host, not the machine driving this campaign.
        // An unknown remote Mac prefers arm64; x64/Rosetta is an explicit choice.
        if (plan.Owned && plan.Architecture.Length == 0)
            plan.Architecture = EnvironmentInventory.DefaultClientArchitecture(hostProfile.Platform,
                hostProfile.Kind == "local" ? EnvironmentInventory.ThisMachine.OsArchitecture : null);
        if (platform == ClientPlatform.MacOS)
            return await OpenLocalMacClientAsync(report, output, plan, name, role, hostProfile, cancellation).ConfigureAwait(false);
        // Remote Windows and Linux clients are x64 only.
        if (plan.LaunchArchitecture != ClientArchitecture.X64)
            throw new ArgumentException($"Profile client '{name}' starts in a remote host's desktop session, where only x64 Windows and Linux clients run; " +
                "architecture arm64 is for a macOS client launched locally in this runner's GUI session. Leave architecture out.");
        string gameRoot = role.PreparedGameRoot ?? role.Install;
        string loaderRoot = role.PreparedLoaderRoot ?? role.Install;
        var launch = GameLaunch.ForClientWithLoader(gameRoot, loaderRoot, plan.LaunchArguments, plan.Environment,
            platform, plan.LaunchArchitecture, plan.PasswordVariable is { } password ? new[] { password } : null);
        var host = ClientHost(role);
        var account = await HoldAccountAsync(report, name, () => host, cancellation).ConfigureAwait(false);
        // A dedicated server's lock covers its own host; another client host is locked for the rest of the run.
        await LockHostAsync(role.Host, host, hostProfile, cancellation).ConfigureAwait(false);
        int n = Interlocked.Increment(ref _clients);
        string runDirectory = HostPath.Join(role.Runtime, RunId), launchDirectory = HostPath.Join(runDirectory, "client-" + n);
        string log = HostPath.Join(loaderRoot, HostedServerRun.BepInExLog);
        var listing = await HostInstall.ListAsync(host, gameRoot, HostedTimeouts.Long, HostInstall.PinPaths, cancellation).ConfigureAwait(false);
        var loader = loaderRoot == gameRoot ? listing :
            await HostInstall.ListAsync(host, loaderRoot, HostedTimeouts.Long, HostInstall.PinPaths, cancellation).ConfigureAwait(false);
        if (plan.Pinned)
            HostInstall.CheckPins(plan.InstallPins ?? throw new ArgumentException("Pin the owned client's game build, loader and patchers in installPins, or opt out explicitly with \"pinning\": \"none\"."), listing, loader, "client install");
        await HostClientPreflight.CheckAsync(host, loaderRoot, platform, plan, HostedTimeouts.Quick, cancellation).ConfigureAwait(false);
        await HostInstall.RequirePortFreeAsync(host, role.CliPort, HostedTimeouts.Quick, cancellation).ConfigureAwait(false);
        // BepInEx rewrites its log at each start; an earlier one moves aside so the wait from offset 0 sees this start's lines only.
        var moved = (await host.RunAsync(HostedClientScripts.MoveAside(host.Shell.Kind), new Dictionary<string, string>
            { ["log"] = log, ["to"] = HostPath.Join(runDirectory, $"client-{n}.previous-LogOutput.log") }, HostedTimeouts.Quick, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Moving the client's previous BepInEx log aside on {host.Name}");
        if (InteractiveClient.Line(moved.Stdout, "VT-MOVED") == null && InteractiveClient.Line(moved.Stdout, "VT-NONE") == null)
            throw new HostOperationException($"Unexpected reply while moving the client's previous log on {host.Name}", moved);
        // #257: Steam's connection_log from here on. "Logged In Elsewhere" in it means the account plays on another computer.
        var steamLog = await SteamSessionLogOnHost.MarkAsync(host, HostedTimeouts.Quick, cancellation).ConfigureAwait(false);
        string SteamMessage() => SteamSessionLog.Message(account?.Hold.Account, role.Host);
        // A failed start looks once: the readiness guard and the exit's reason share the answer.
        Task<bool>? looked = null;
        Task<bool> FinalLook() => LazyInitializer.EnsureInitialized(ref looked, () => steamLog is { } watched
            ? SteamSessionLogOnHost.SeenAsync(host, watched, SteamSessionLogOnHost.FinalLook, CancellationToken.None) : Task.FromResult(false));
        var tunnel = await host.OpenCliTunnelAsync(role.CliPort, HostedTimeouts.Quick, role.LocalCliPort, cancellation).ConfigureAwait(false);
        try
        {
            string local = Path.Combine(output, "client-" + n);
            var display = platform != ClientPlatform.Linux ? null : new LinuxDisplay();
            var start = HostedTimeouts.ClientStart(plan);
            var session = ClientSession.Launch(plan, output,
                () =>
                {
                    if (role.PreparedSourceRoot is { } source && plan.InstallPins is { } pinned)
                    {
                        HostInstall.CheckProfilePinsAsync(host, pinned, gameRoot, loaderRoot, source,
                            "prepared client profile before launch", HostedTimeouts.Long, cancellation).GetAwaiter().GetResult();
                    }
                    string expected = launch.CommandLineSha256();
                    JournalAsync(host, role.Host, name, JournalEntry.Of(JournalEntry.ProcessIntended, ("launchDirectory", launchDirectory),
                        ("expectedCommandLineSha256", expected)), cancellation).GetAwaiter().GetResult();
                    var started = account == null ? InteractiveClient.StartAsync(host, launch, launchDirectory, start, display, cancellation)
                        : InteractiveClient.StartAsync(account.Hold, host, launch, launchDirectory, start, display, cancellation);
                    var client = started.GetAwaiter().GetResult();
                    string commandLine = HostProcessProbe.CommandLineAsync(host, client.Id, client.StartIdentity, HostedTimeouts.Quick).GetAwaiter().GetResult() ?? "";
                    HostedServerRun.WarnUnexpectedCommandLine(host, client.Id, expected, commandLine);
                    NoteAsync(host, role.Host, name, JournalEntry.Of(JournalEntry.ProcessStarted, ("pid", client.Id.ToString(CultureInfo.InvariantCulture)),
                        ("startIdentity", client.StartIdentity), ("commandLineSha256", commandLine), ("launchDirectory", launchDirectory))).GetAwaiter().GetResult();
                    var process = new HostedClientProcess(client, host, loaderRoot, tunnel, local);
                    if (account != null) account.Process = process; // Its lease is released only once this process is gone.
                    return process;
                },
                () => _hooks.Connect(tunnel.Address, tunnel.LocalPort),
                SteamSessionLog.Guard(async (left, token) =>
                {
                    var clock = Stopwatch.StartNew();
                    var bepInEx = TimeSpan.FromSeconds(plan.BepInExSeconds);
                    if (bepInEx > left) bepInEx = left;
                    try
                    {
                        (await host.WaitForLogAsync(log, 0, new System.Text.RegularExpressions.Regex("^"), StartupEvents.ClientStartupFailures,
                            bepInEx, token).ConfigureAwait(false)).EnsureMatched();
                    }
                    catch (WaitTimeoutException error)
                    {
                        // A preloader crash log this launch wrote says why (#254); an older one is not this launch's and says nothing.
                        var preloader = await HostedClientScripts.ReadPreloaderAsync(host, loaderRoot, launchDirectory).ConfigureAwait(false);
                        string why = PreloaderLogs.Explain(preloader, "game-2.preloader-*.log",
                            "The game may have reached its menu without BepInEx; check winhttp.dll, doorstop_config.ini and BepInEx/core as one pack. ");
                        throw new InvalidOperationException($"BepInEx wrote no fresh log line on {host.Name} within {plan.BepInExSeconds}s. {why}" +
                            $"The client's Player.log and boot output are kept in {local}.", error);
                    }
                    (await host.WaitForLogAsync(log, 0, StartupEvents.CliListening, StartupEvents.ClientStartupFailures, left - clock.Elapsed, token).ConfigureAwait(false)).EnsureMatched();
                    if (!_hooks.StateWaits) return;
                    using var states = StateWait.Connect(tunnel.Address, tunnel.LocalPort);
                    var menuTime = left - clock.Elapsed;
                    await StartupEvents.WaitForClientMenuAsync(
                        next => states.WaitAsync([StateWait.MainMenu], menuTime, cancellation: next),
                        async next => (await host.WaitForLogAsync(log, 0, StartupEvents.Never,
                            StartupEvents.ClientStartupFailures, menuTime, next).ConfigureAwait(false)).EnsureMatched(),
                        token).ConfigureAwait(false);
                },
                (left, token) => steamLog is { } watched ? SteamSessionLogOnHost.SeenAsync(host, watched, left, token) : Task.FromResult(false),
                FinalLook, SteamMessage), cancellation,
                // A client that quit before its menu: Steam's log says whether another computer took the account.
                () => FinalLook().GetAwaiter().GetResult() ? SteamSessionLog.ExitHint(SteamMessage()) : null,
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
        if (hostProfile.Kind != "local" || !_hooks.LocalMacClients)
            throw new PlatformNotSupportedException($"Profile client '{name}' needs a local macOS host in this runner's logged-in GUI session; SSH cannot launch it there.");
        if (!plan.Owned) throw new ArgumentException($"Profile client '{name}' must be an owned client for local macOS launch.");
        if (Path.GetFullPath(plan.Install) != Path.GetFullPath(role.PreparedGameRoot ?? role.Install) || plan.Port != role.CliPort ||
            plan.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new ArgumentException($"Profile client '{name}' must pin the local role's exact install and CLI port on loopback.");
        _hooks.RequireMacGui();
        var host = ClientHost(role);
        if (host.Kind != GameHostKind.Local)
            throw new PlatformNotSupportedException($"Profile client '{name}' must use a local host; a remote process cannot enter this runner's GUI session.");
        var account = await HoldAccountAsync(report, name, () => host, cancellation).ConfigureAwait(false);
        await LockHostAsync(role.Host, host, hostProfile, cancellation).ConfigureAwait(false);
        // ClientSession owns the direct child process and its logs. No SSH-launched GUI process or remote task is involved.
        ClientSession session;
        Action<IOwnedProcess> processStarted = process =>
        {
            lock (_clientState) _localMacProcesses.Add((role.Host, process));
            if (account != null) account.Process = process;
        };
        session = _hooks.LaunchLocalMac(plan, output, account?.Hold, cancellation, processStarted);
        if (session.OwnedProcess is { } owned)
            lock (_clientState)
                if (!_localMacProcesses.Any(item => ReferenceEquals(item.Process, owned)))
                    _localMacProcesses.Add((role.Host, owned));
        if (account != null) { account.Process = session.OwnedProcess; account.Session = session; }
        return session;
    }


    // The client host's lock, once for the run (a dedicated server's own host is its run's).
    private async Task LockHostAsync(string hostName, IGameHost host, HostProfile hostProfile, CancellationToken cancellation)
    {
        if (hostName == _serverHost?.Name) return;
        await _clientLockGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            bool held; lock (_clientState) held = _clientLocks.Any(item => item.Host == hostName);
            if (held) return;
            var taken = await host.AcquireLockAsync(hostProfile.Lock, _owner, HostedTimeouts.Quick, cancellation).ConfigureAwait(false);
            lock (_clientState) _clientLocks.Add((hostName, taken));
            await NoteLockAsync(host, hostName, taken, JournalEntry.LockHeld).ConfigureAwait(false);
        }
        finally { _clientLockGate.Release(); }
    }

    /// <summary>
    /// Before the run changes anything a client's host user owns (its hosted world in their worlds_local): the host's lock is this
    /// run's for the rest of it, and no game runs in that host's desktop session.
    /// </summary>
    public async Task HoldHostAsync(string client, CancellationToken cancellation)
    {
        if (!Profile.Clients.TryGetValue(client, out var role)) throw new ArgumentException($"No client '{client}' in the environment.", nameof(client));
        var host = ClientHost(role);
        await LockHostAsync(role.Host, host, Profile.Hosts[role.Host], cancellation).ConfigureAwait(false);
        await HostedRuntimeStage.RequireStoppedAsync(host, HostedTimeouts.Quick, cancellation, clientSession: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Teardown, once the processes are stopped or named: each client a scenario left open is closed, then its account's lease is
    /// released (kept, and named, when its client may still run), each as its own step through <paramref name="step"/>.
    /// </summary>
    public async Task ReleaseAccountsAsync(Func<string, Func<Task>, Task> step)
    {
        List<ClientAccount> accounts;
        lock (_clientState) accounts = [.. _accounts];
        foreach (var account in accounts)
        {
            // An account is released only once its client is gone: a session the scenario left open is closed first.
            if (account.Session is { Closed: false } open)
                await step(open.Owned ? $"stop only the owned client {account.Client}" : $"detach from the operator's client {account.Client}",
                    () => { open.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
            await step($"release client {account.Client}'s Steam account lease", async () =>
            {
                if (account.Process is { HasExited: false })
                {
                    account.Hold.Keep();
                    await NoteAsync(account.LeaseHost, account.LeaseHostName, account.Client, LeaseEntry(JournalEntry.LeaseKept, account.Hold,
                        ("why", "its client may still run"))).ConfigureAwait(false);
                    throw new SteamAccountLeaseException(SteamAccountLeaseState.Unknown, account.Hold.Pool, [], $"Kept the lease on Steam account {account.Hold.Account}: the client " +
                        $"{account.Client} may still run on {account.Hold.ClientHost}. It is held until that client is proven stopped: valheim-test env teardown --run {RunId} releases it then.");
                }
                await account.Hold.ReleaseAsync().ConfigureAwait(false);
                await NoteAsync(account.LeaseHost, account.LeaseHostName, account.Client, LeaseEntry(JournalEntry.LeaseReleased, account.Hold)).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Releases each client host's lock this run took, as its own step; a host where an owned local Mac client may still run keeps it.</summary>
    public async Task ReleaseLocksAsync(Func<string, Func<Task>, Task> step)
    {
        List<(string Host, HostLock Lock)> locks;
        lock (_clientState) locks = [.. _clientLocks];
        foreach (var (name, held) in locks) await step($"release client host {name}'s lock", () =>
            _localMacProcesses.Any(item => item.Host == name && !item.Process.HasExited)
                ? throw new HostLockException(new HostLockResult(HostLockState.Unknown, held.Owner,
                    $"Kept {held.Path} on {name}: an owned local Mac client may still run. Confirm its recorded process has stopped before releasing this lock."))
                : ReleaseAndNoteAsync(HostNamed(name), name, held)).ConfigureAwait(false);
    }

    private async Task ReleaseAndNoteAsync(IGameHost host, string hostName, HostLock held)
    {
        var result = await held.ReleaseAsync().ConfigureAwait(false);
        if (result.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(result);
        await NoteLockAsync(host, hostName, held, JournalEntry.LockReleased).ConfigureAwait(false);
    }
}
