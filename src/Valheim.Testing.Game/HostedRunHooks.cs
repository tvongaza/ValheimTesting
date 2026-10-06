namespace Valheim.Testing.Game;

/// <summary>
/// Everything a hosted run (<see cref="HostedServerRun"/>, and <see cref="PinnedServerRun"/> around it) reaches outside this
/// process: the environment's hosts, ValheimCLI connections and their state pushes, Steam account leases, this machine's
/// macOS desktop session and its local client launch, and the run's id and Ctrl+C owner. Runs use
/// <see cref="HostedRunHooks.Production"/>; tests pass their own implementation (fake hosts and transports, no game).
/// </summary>
internal interface IHostedRunHooks
{
    /// <summary>The environment's host of that name.</summary>
    IGameHost CreateHost(ResolvedEnvironment environment, string name);
    /// <summary>A ValheimCLI connection at that address and port (a tunnel's local end, or an attached client's port).</summary>
    IGameTransport Connect(string address, int port);
    /// <summary>Whether a connection pushes game states to wait on (a fake transport has none).</summary>
    bool StateWaits { get; }
    /// <summary>The run's id: the campaign's when it has one, else a new one.</summary>
    string RunId(string? campaignRunId);
    /// <summary>Leases a Steam account for the client (<see cref="SteamAccountHold.AcquireAsync"/>).</summary>
    Task<SteamAccountHold> LeaseAsync(ResolvedEnvironment environment, string client, string owner, IGameHost leaseHost, TimeSpan timeout, CancellationToken cancellation);
    /// <summary>Whether a local macOS client can be launched from this process.</summary>
    bool LocalMacClients { get; }
    /// <summary>Refuses unless this runner is in an unlocked macOS GUI session.</summary>
    void RequireMacGui();
    /// <summary>Launches a local macOS client in this runner's GUI session.</summary>
    ClientSession LaunchLocalMac(ClientRunPlan plan, string output, SteamAccountHold? account, CancellationToken cancellation, Action<IOwnedProcess>? processStarted);
    /// <summary>The run's Ctrl+C owner, and whether the run owns (and disposes) it.</summary>
    RunCancellation Cancellation(out bool owned);
}

/// <summary>The real hosted-run hooks: ssh and local hosts, CLI sockets, the pool's lease times, this machine's session.</summary>
internal sealed class HostedRunHooks : IHostedRunHooks
{
    public static readonly IHostedRunHooks Production = new HostedRunHooks();
    private HostedRunHooks() { }

    public IGameHost CreateHost(ResolvedEnvironment environment, string name) => environment.CreateHost(name);
    public IGameTransport Connect(string address, int port) => new CliTransport(address, port);
    public bool StateWaits => true;
    public string RunId(string? campaignRunId) => campaignRunId ?? RunJournal.NewRunId();
    public Task<SteamAccountHold> LeaseAsync(ResolvedEnvironment environment, string client, string owner, IGameHost leaseHost, TimeSpan timeout, CancellationToken cancellation) =>
        SteamAccountHold.AcquireAsync(environment, client, owner, leaseHost, timeout, cancellation: cancellation);
    public bool LocalMacClients => OperatingSystem.IsMacOS();
    public void RequireMacGui() => MacGuiSession.Require();
    public ClientSession LaunchLocalMac(ClientRunPlan plan, string output, SteamAccountHold? account, CancellationToken cancellation, Action<IOwnedProcess>? processStarted) =>
        ClientSession.Launch(plan, output, account, cancellation, processStarted);
    public RunCancellation Cancellation(out bool owned) { owned = true; return new RunCancellation(); }
}
