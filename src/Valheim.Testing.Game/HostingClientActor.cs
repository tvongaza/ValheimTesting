namespace Valheim.Testing.Game;

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
    private readonly bool _local;

    /// <param name="site">A campaign client's host, where its world is placed; null for this machine.</param>
    internal HostingClientActor(string name, ClientRunPlan plan, string output, IClientPlacement placement, CancellationToken cancellation,
        HostedWorldOnHost.Site? site = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.HostWorld == null) throw new ArgumentException($"Client {name} hosts a world: give its plan a hostWorld section.", nameof(plan));
        _client = new ClientActor(name, plan, output, placement, cancellation);
        _world = new HostedWorldLifecycle(plan, output, cancellation, site);
        _cancellation = cancellation; _local = placement is LocalClientPlacement;
    }

    /// <summary>
    /// A hosting client on this machine, its evidence (command and process records, kept logs, the fixture copy and the collected
    /// world) written to <paramref name="output"/>: launched from the plan's install or attached to the operator's client, as its
    /// mode says; the fixture is placed in this machine's client data directory (or the plan's <see cref="HostWorldPlan.SaveDirectory"/>).
    /// </summary>
    public static HostingClientActor OnThisMachine(string name, ClientRunPlan plan, string output, CancellationToken cancellation = default) =>
        new(name, plan, output, LocalClientPlacement.Instance, cancellation);

    /// <summary>The hosting client's name in the run, for example <c>host</c>.</summary>
    public string Name => _client.Name;
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
    internal SessionState StartWorld(GameActor host) => _world.StartWorld(host, ProtectPlayer);
    /// <summary>The host leaves its world (the game saves it) and is re-pinned at its menu.</summary>
    internal void LeaveWorld(GameActor host) => _world.LeaveWorld(host);

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

/// <summary>
/// What a hosting client does with its fixture world, apart from opening and closing the client: the preflight, the save
/// directory, the placement, the start and leave, and the release once no client can still host it (#332). Shared by
/// <see cref="HostingClientActor"/> and <see cref="ClientRounds"/>' hosted rounds, which open their client themselves.
/// </summary>
internal sealed class HostedWorldLifecycle(ClientRunPlan plan, string output, CancellationToken cancellation, HostedWorldOnHost.Site? site = null)
{
    private readonly object _state = new();
    private HostedWorld? _world;
    private string? _saveDirectory;
    // Whether the world may be open: from the moment a start is issued until a leave completed.
    private bool _hosting;
    // Whether the owned process stopped: then nothing hosts the world any more.
    private bool _stopped;

    private HostWorldPlan Section => plan.HostWorld!;
    public HostedWorld? World { get { lock (_state) return _world; } }
    public bool Hosting { get { lock (_state) return _hosting; } }
    /// <summary>Whether no client can still host the placed world: never started, back at its menu, or its owned process stopped.</summary>
    public bool WorldReleased { get { lock (_state) return !_hosting || _stopped; } }
    public void Stopped() { lock (_state) _stopped = true; }

    public string PreflightStep => plan.Owned ? "preflight the fixture world and the owned client's install, before anything is copied or started" : "preflight the fixture world, before it is copied";
    /// <summary>
    /// The plan's static preflight: the fixture's pinned files and own world UID, and an owned client's install with the session
    /// commands hosting uses. A client on a campaign host has its install checked on that host by its preparation and launch; here
    /// only its staged ValheimCLI set is (its manifest).
    /// </summary>
    public void Preflight()
    {
        if (site == null) { plan.Preflight(CliCapabilities.HostedRounds); return; }
        plan.HostWorld!.Preflight();
        // The staged set's manifest (bound by the campaign) must offer the session commands hosting uses; its files are on the host.
        if (plan.Owned && plan.CliManifest != null)
        {
            var offered = CliCapabilityManifest.Read(plan.CliManifest).Capabilities;
            var missing = CliCapabilities.HostedRounds.Concat(plan.Capabilities.Where(CliCapabilities.IsPackCapability)).Distinct(StringComparer.Ordinal)
                .Where(capability => !offered.ContainsKey(capability)).ToList();
            if (missing.Count != 0) throw new InvalidOperationException($"The client's staged ValheimCLI set does not offer {string.Join(", ", missing)}, which hosting a world uses.");
        }
    }
    /// <summary>The native client's save directory, which holds <c>worlds_local</c>; a native macOS client must use its signed-in user's default.</summary>
    public void CheckSaveDirectory()
    {
        if (site != null)
        {
            // The host user's own data directory, beside the characters_local the campaign staged the character in.
            if (Section.SaveDirectory != null) throw new ArgumentException("hostWorld.saveDirectory: a campaign client's world goes to its host user's own worlds_local; leave it out.");
            lock (_state) _saveDirectory = site.WorldsDirectory;
            return;
        }
        var platform = plan.Owned ? GameLaunch.DetectClient(plan.Install) : HostedWorld.CurrentPlatform;
        string defaultSaveDirectory = HostedWorld.DefaultSaveDirectory(platform);
        HostedWorld.RequireNativeSaveDirectory(platform, Section.SaveDirectory, plan.LaunchArguments, defaultSaveDirectory);
        lock (_state) _saveDirectory = Section.SaveDirectory ?? defaultSaveDirectory;
    }
    /// <summary>Copies the fixture into the output (verified) and into the client's local worlds, refusing a world already named for it.</summary>
    public void PlaceWorld()
    {
        string saveDirectory;
        lock (_state)
        {
            if (_world != null) throw new InvalidOperationException("The fixture world is placed already.");
            saveDirectory = _saveDirectory ?? throw new InvalidOperationException("Check the client's save directory before placing the fixture world.");
        }
        var world = site != null ? HostedWorldOnHost.Place(site, Section, output, plan.Pinned, cancellation) : HostedWorld.Place(Section, saveDirectory, output, plan.Pinned);
        lock (_state) _world = world;
    }
    /// <summary>The three preparation steps in <paramref name="report"/>: preflight, save directory, placement; the placed world's name is recorded.</summary>
    public void Prepare(ScenarioReport report)
    {
        report.Step(StepPhase.Preflight, PreflightStep, Preflight);
        report.Step(StepPhase.Preflight, "preflight the native client's hosted-world save directory", CheckSaveDirectory);
        report.Step(StepPhase.Setup, "place the disposable fixture world in the client's local worlds", PlaceWorld);
        report.Provenance["hostWorld"] = World!.Name;
    }

    /// <summary>From its idle menu, <paramref name="host"/> hosts the placed world (<see cref="HostWorlds.Start"/>, within the plan's join time).</summary>
    public SessionState StartWorld(GameActor host, bool protectPlayer)
    {
        var world = World ?? throw new InvalidOperationException("Place the fixture world first: a host starts only the placed copy.");
        lock (_state) _hosting = true; // A start that may have begun can host the world.
        return HostWorlds.Start(host, plan, world.Name, TimeSpan.FromSeconds(plan.JoinSeconds), cancellation, protectPlayer);
    }
    /// <summary>The owned host's disposable character and fixture acknowledge cheats; an operator's client keeps devcommands only.</summary>
    public static void EstablishTestAccess(GameActor host) => TestAccess.Ensure(host, TestActorRole.ClientInWorld);
    /// <summary>The host leaves its world (the game saves it; then no client hosts it) and is re-pinned at its menu.</summary>
    public void LeaveWorld(GameActor host)
    {
        new SessionControl(host).Leave();
        lock (_state) _hosting = false; // The leave completed: whatever the pins say next, the world is no longer open.
        host.VerifyEnvironment(plan.MenuExpectations); // A transition always needs fresh pins.
    }

    /// <summary>
    /// Once no client can still host it (<paramref name="released"/>, or <see cref="WorldReleased"/>), moves the placed world, with
    /// what the game wrote for it, into <c>host-world</c> in the evidence (a Cleanup step, <c>hostWorldEvidence</c>); otherwise leaves
    /// it in place and names it (<c>hostWorldLeftInPlace</c>). Nothing when no world was placed.
    /// </summary>
    public void ReleaseWorld(ScenarioReport report, bool? released = null)
    {
        var world = World;
        if (world == null) return;
        if (!(released ?? WorldReleased)) { report.Provenance["hostWorldLeftInPlace"] = (world.Host != null ? world.Host + ": " : "") + world.WorldsDirectory + " (" + world.Name + ")"; return; }
        report.Step(StepPhase.Cleanup, "move the hosted world from the client's local worlds into the evidence", world.Collect);
        report.Provenance["hostWorldEvidence"] = world.CollectedTo!;
    }
}
