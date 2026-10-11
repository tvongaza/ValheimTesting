using Valheim.Testing.Game;
namespace Valheim.Testing.GameSessions;

/// <summary>
/// The client of a run that hosts its own world (#258 step 8, Q2): one game process with both capabilities, a client with a
/// local player and the server of its world (a listen server). Its plan is a <see cref="ClientRunPlan"/> with a
/// <see cref="ClientRunPlan.HostWorld"/> section; the inventory has no role for it. Its lifecycle:
/// <list type="number">
/// <item><see cref="Prepare"/>: the plan's static preflight (<see cref="ClientRunPlan.Preflight(IEnumerable{string})"/> with
/// <see cref="CliCapabilities.HostedRounds"/>), the native client's save directory, then the fixture world placed in its local
/// worlds (<see cref="HostedWorld.Place"/>). <see cref="GameSession.StartAsync"/> records each part as its own step, before any
/// actor starts.</item>
/// <item><see cref="Start"/>: the client to its strictly pinned menu, through its placement, once (<see cref="ClientActor"/>).</item>
/// <item><see cref="Host"/>: it hosts the placed world (<see cref="HostWorlds.Start"/>), protected unless
/// <see cref="ProtectPlayer"/> is false, and an owned host's disposable character gets test access.</item>
/// </list>
/// As the session's <see cref="IOwnedServer"/>, <see cref="WaitUntilJoinable"/> waits until its world is open to peers and
/// <see cref="Restart"/> re-hosts it: the host leaves its world (the game saves it), is re-pinned at its menu and hosts it again.
/// <see cref="Game"/> is its in-game handle, as server and as client. A peer joins it through the session
/// (<see cref="GameSession.Join"/>; <see cref="SessionControl.JoinHost"/>). Closing it stops only a process it started, or detaches
/// from an operator's; the placed world is moved into the evidence only once no client can still host it (#332): it never
/// opened, its owned process stopped, or it left its world. Otherwise the world stays in place (<see cref="World"/> is not
/// collected; a session names it in the report).
/// </summary>
public sealed class HostingClientActor : IOwnedServer, IDisposable
{
    private readonly ClientActor _client;
    private readonly HostedWorldLifecycle _world;
    private readonly CancellationToken _cancellation;
    private readonly bool _local, _retiresCopy;
    private readonly IClientPlacement _placement;

    /// <param name="site">A campaign client's host, where its world is placed; null for this machine.</param>
    internal HostingClientActor(string name, ClientRunPlan plan, string output, IClientPlacement placement, CancellationToken cancellation,
        HostedWorldOnHost.Site? site = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.HostWorld == null) throw new ArgumentException($"Client {name} hosts a world: give its plan a hostWorld section.", nameof(plan));
        // A hosting client on this machine has its own placement: closing it removes its disposable copy.
        _client = new ClientActor(name, plan, output, placement, cancellation);
        _retiresCopy = placement is LocalClientPlacement;
        _world = new HostedWorldLifecycle(plan, output, cancellation, site);
        _cancellation = cancellation; _local = placement is LocalClientPlacement; _placement = placement;
    }

    /// <summary>
    /// A hosting client on this machine, its evidence (command and process records, kept logs, the fixture copy and the collected
    /// world) written to <paramref name="output"/>: launched from the plan's install or attached to the operator's client, as its
    /// mode says; the fixture is placed in this machine's client data directory (or the plan's <see cref="HostWorldPlan.SaveDirectory"/>).
    /// An owned client runs from a disposable copy of its install, made when it opens and removed when it is closed, unless its
    /// plan runs it in place (<see cref="ClientRunPlan.InPlace"/>).
    /// </summary>
    public static HostingClientActor OnThisMachine(string name, ClientRunPlan plan, string output, CancellationToken cancellation = default) =>
        new(name, plan, output, new LocalClientPlacement(), cancellation);

    /// <summary>The hosting client's name in the run, for example <c>host</c>.</summary>
    public string Name => _client.Name;
    internal string Output => _client.Output;
    public ClientRunPlan Plan => _client.Plan;
    /// <summary>The hosted fixture's world UID (<see cref="HostWorldPlan.WorldUid"/>), which every peer's join verifies.</summary>
    public string WorldUid => Plan.HostWorld!.WorldUid;
    /// <summary>Whether hosting protects the host's player (god, ghost and debug mode); default true.</summary>
    public bool ProtectPlayer { get; init; } = true;
    /// <summary>The fixture world placed in the client's local worlds, or null before it is placed.</summary>
    public HostedWorld? World => _world.World;
    /// <summary>The open client's session, or null before <see cref="Start"/> and once it is closed.</summary>
    public ClientSession? Session => _client.Session;
    /// <summary>The open host's in-game handle: the server of its world and a client with a local player.</summary>
    public GameActor Game => _client.Game;
    /// <summary>The open's kept logs, for the teardown scan once the client is closed.</summary>
    public IReadOnlyList<RunLog> Logs => _client.Logs;
    /// <summary>
    /// An owned host's live BepInEx log on this machine, for a scenario's in-run log reads; null for an operator's client (its logs
    /// are its operator's).
    /// </summary>
    public string? LiveLog => LiveLogSource != null ? LiveLogSource() : _local && Plan.Owned ? Path.Combine(Plan.Install, "BepInEx", "LogOutput.log") : null;
    // Test seam: a scripted host's log (Fakes.FakeGameSession.Hosted).
    internal Func<string?>? LiveLogSource { get; init; }
    /// <summary>Whether the world may be open: a start was issued and the host has not left it since.</summary>
    internal bool Hosting => _world.Hosting;
    internal string PreflightStep => _world.PreflightStep;
    internal void Preflight() => _world.Preflight();

    /// <summary>
    /// Before anything starts: the plan's static preflight, the client's save directory, then the fixture world placed in its local
    /// worlds (refusing a world already named for it). A wrong fixture or install stops here, before the fixture is copied.
    /// </summary>
    public void Prepare() { _world.Preflight(); _world.CheckSaveDirectory(); _world.PlaceWorld(); }
    /// <summary><see cref="Prepare"/> as three steps in <paramref name="report"/>; the placed world's name is recorded.</summary>
    internal void Prepare(ScenarioReport report) => _world.Prepare(report);

    /// <summary>Opens the client to its menu, plugins pinned, once, and returns its session (<see cref="ClientActor.Start"/>).</summary>
    public ClientSession Start() => _client.Start();

    /// <summary>
    /// From its idle menu, the open client hosts the placed fixture world (<see cref="HostWorlds.Start"/>, within the plan's join
    /// time), protected unless <see cref="ProtectPlayer"/> is false; an owned host's disposable character then gets test access.
    /// </summary>
    public SessionState Host()
    {
        var host = Game;
        var state = StartWorld(host);
        if (Plan.Owned) HostedWorldLifecycle.EstablishTestAccess(host);
        return state;
    }

    /// <summary>The start alone (<see cref="HostWorlds.Start"/>), protected as <see cref="ProtectPlayer"/> says.</summary>
    internal SessionState StartWorld(GameActor host)
    {
        var state = _world.StartWorld(host, ProtectPlayer);
        Session?.WorldEntered(Plan, WorldUid);
        return state;
    }
    /// <summary>The host leaves its world (the game saves it) and is re-pinned at its menu.</summary>
    internal void LeaveWorld(GameActor host)
    {
        try { _world.LeaveWorld(host); }
        finally { Session?.WorldLeft(); }
    }

    /// <summary>
    /// Waits until <paramref name="host"/>, this host's <see cref="Game"/>, is in its fixture world with its player, within the
    /// plan's join time, and is an open server a peer can join (<c>cli_multiplayer_identity</c>: a server, open).
    /// </summary>
    public void WaitUntilJoinable(GameActor host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!ReferenceEquals(host, Game)) throw new ArgumentException($"Pass the host's own actor ({Name}'s Game).", nameof(host));
        new SessionControl(host).WaitForWorld(WorldUid, TimeSpan.FromSeconds(Plan.JoinSeconds), _cancellation, protectPlayer: false);
        var identity = MultiplayerIdentity.Read(host);
        if (!identity.IsServer || !identity.IsOpenServer)
            throw new InvalidOperationException($"The host {Name} is in world {WorldUid} but is not an open server (isServer={identity.IsServer}, isOpenServer={identity.IsOpenServer}), so no peer can join it.");
    }

    /// <summary>
    /// The host's restart: it leaves its world (the game saves it on the way out; confirm a save first with
    /// <see cref="SessionControl.Save"/> when the check is about persistence), is re-pinned at its menu and hosts the same world
    /// again, protected as <see cref="ProtectPlayer"/> says, with test access on an owned host. Peers in the world are disconnected
    /// by the leave. Returns the host's actor, the same process.
    /// </summary>
    public GameActor Restart()
    {
        var host = Game;
        LeaveWorld(host);
        Host();
        return host;
    }

    /// <summary>Closes the client: stops only a process it started, or detaches from an operator's. A failed stop leaves the world hosted.</summary>
    internal void CloseClient()
    {
        bool owned = _client.Session?.Owned ?? false;
        _client.Dispose(); // Throws when the owned process could not be stopped.
        if (owned) _world.Stopped();
        // Its disposable copy on this machine, once it stopped; a failure here is the copy's alone (the world is released).
        if (_retiresCopy && _placement is LocalClientPlacement local) local.RetireAsync().GetAwaiter().GetResult();
    }

    /// <summary>Moves the placed world into the evidence once no client can still host it, else names it (a step and provenance in <paramref name="report"/>).</summary>
    internal void ReleaseWorld(ScenarioReport report) => _world.ReleaseWorld(report);

    /// <summary>
    /// Closes the client, then moves the placed world into the evidence once no client can still host it; an operator's client
    /// still in the world keeps it (<see cref="HostedWorld.CollectedTo"/> stays null). A failed close is rethrown after that.
    /// </summary>
    public void Dispose()
    {
        Exception? failed = null;
        try { CloseClient(); }
        catch (Exception error) { failed = error; }
        if (_world.WorldReleased) _world.World?.Collect();
        if (failed != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();
    }
}
