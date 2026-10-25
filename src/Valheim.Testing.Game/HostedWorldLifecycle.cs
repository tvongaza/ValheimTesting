namespace Valheim.Testing.Game;

/// <summary>
/// What a hosting client does with its fixture world, apart from opening and closing the client: the preflight, the save
/// directory, the placement, the start and leave, and the release once no client can still host it (#332). Shared by
/// a session's hosting client and <see cref="ClientRounds"/>' hosted rounds, which open their client themselves.
/// Every placement goes through the hosting layer's journalled site (#829): a campaign client's world to its <paramref name="site"/>;
/// a client on this machine's to the save directory checked here, through <paramref name="localSite"/> given that <c>worlds_local</c>.
/// </summary>
internal sealed class HostedWorldLifecycle
{
    private readonly ClientRunPlan plan;
    private readonly string output;
    private readonly CancellationToken cancellation;
    private readonly IHostedWorldSite? site;
    private readonly Func<string, IHostedWorldSite>? localSite;

    /// <param name="site">A campaign client's site, whose <c>worlds_local</c> is known before the save directory is checked.</param>
    /// <param name="localSite">For a client on this machine: its journalled site, given the checked <c>worlds_local</c>.</param>
    public HostedWorldLifecycle(ClientRunPlan plan, string output, CancellationToken cancellation, IHostedWorldSite? site = null,
        Func<string, IHostedWorldSite>? localSite = null)
    {
        if ((site == null) == (localSite == null))
            throw new ArgumentException("A hosting client's world is placed through exactly one journalled site: its campaign host's, or this machine's.");
        this.plan = plan; this.output = output; this.cancellation = cancellation; this.site = site; this.localSite = localSite;
    }

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
        var target = plan.Owned
            ? plan.PreparedBuild ?? GameAssemblyVersion.ReadInstallAsync(plan.Install, cancellation).GetAwaiter().GetResult()
            : new GameBuild(null, null, "the running host's build is unknown") { WorldUnknown = "an attached client's operator-owned install is not inspected" };
        Section.AdmittedIdentity = Section.Preflight(target, Section.World.Source);
        Section.AdmittedBuild = target;
        if (plan.Owned) plan.PreparedSourceCheck?.Invoke();
        if (Section.World.Sha256.Count == 0)
            Section.World.Sha256 = new Dictionary<string, string>(WorldFixture.Manifest(Section.World.Source), StringComparer.Ordinal);
        if (site == null) { plan.Preflight(CliCapabilities.HostedRounds); return; }
        plan.HostWorld!.Preflight();
        // The staged set (bound by the campaign) must offer the session commands hosting uses; its files are read on the host.
        plan.CheckCliManifest(CliCapabilities.HostedRounds);
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
        if (plan.Owned && site == null && Section.AdmittedBuild?.WorldSupport is { } admitted)
        {
            var current = GameAssemblyVersion.ReadInstallAsync(plan.Install, cancellation).GetAwaiter().GetResult();
            if (current.WorldSupport?.AssemblySha256 != admitted.AssemblySha256)
                throw new InvalidOperationException("The hosting game assembly changed after world-format admission; inspect the selected host again.");
        }
        string saveDirectory;
        lock (_state)
        {
            if (_world != null) throw new InvalidOperationException("The fixture world is placed already.");
            saveDirectory = _saveDirectory ?? throw new InvalidOperationException("Check the client's save directory before placing the fixture world.");
        }
        var world = (site ?? localSite!(Path.Combine(saveDirectory, "worlds_local"))).Place(Section, output, cancellation);
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
        if (!(released ?? WorldReleased)) { report.Provenance["hostWorldLeftInPlace"] = world.Host + ": " + world.WorldsDirectory + " (" + world.Name + ")"; return; }
        report.Step(StepPhase.Cleanup, "move the hosted world from the client's local worlds into the evidence", world.Collect);
        report.Provenance["hostWorldEvidence"] = world.CollectedTo!;
    }
}

/// <summary>
/// Where a hosting client's world goes, its machine's <c>worlds_local</c>, and how the fixture is placed there: shipped, verified
/// and journalled by the hosting layer, so <c>env recover</c> can move a world a killed run left (a campaign client's on any host,
/// one on this machine for <c>start</c>, a session or rounds a caller drives). The one placement owner (#829).
/// </summary>
internal interface IHostedWorldSite
{
    /// <summary>The client's <c>worlds_local</c> on its machine.</summary>
    string WorldsDirectory { get; }
    /// <summary>Copies the fixture into <paramref name="output"/> (verified) and into <see cref="WorldsDirectory"/>, refusing a world already named for it.</summary>
    HostedWorld Place(HostWorldPlan plan, string output, CancellationToken cancellation);
}
