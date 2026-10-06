using System.Diagnostics;

namespace Valheim.Testing.Game;

/// <summary>
/// One run's game processes in their roles (#258, decided on #289): an owned dedicated server (<see cref="Server"/>) or a client
/// that hosts its own world (<see cref="Host"/>, #258 step 8), and named clients (<see cref="Clients"/>), each with its in-game
/// handle <c>.Game</c>. A scenario is a function of the session and its
/// plan, <c>(GameSession session, TPlan plan) =&gt; Task</c>, and coordinates the actors only through the barriers, each one
/// named Setup step with a bounded wait that names itself when it times out:
/// <list type="bullet">
/// <item><see cref="StartAsync"/>: the server boots (or the host, prepared before anything starts, opens to its menu and hosts its world) and every
/// client reaches its menu at the same time; one failure cancels the others, lets them settle, closes the clients and then the
/// server or host, and is rethrown. With the mod's <see cref="ModDeclaration"/>, its declared Harmony patches must be applied on
/// the server or host when its pins load the mod.</item>
/// <item><see cref="ServerJoinable"/>: the server (or the host's world) accepts game connections.</item>
/// <item><see cref="Join"/>, <see cref="JoinAll"/>: a client (every client) in the server's world, by the one join
/// (<see cref="SessionControl.JoinWorld"/>), or in the host's world by the host's identity (<see cref="SessionControl.JoinHost"/>),
/// optionally standing at an arrival point (<see cref="PlayerPlacement.Arrive"/>).</item>
/// <item><see cref="Rejoin"/>: a client leaves to its menu and joins again.</item>
/// <item><see cref="Barrier"/>: a scenario's own readiness, waited on within a bound.</item>
/// </list>
/// Disposing it closes the clients in reverse order, then stops the server, each a Cleanup step: clients before their host. A
/// hosting client is closed after its peers, then its placed world is moved into the evidence only once no client can still host it.
/// </summary>
public sealed class GameSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationToken _token;
    private readonly List<ClientActor> _clients;
    private readonly string? _worldUid;
    private bool _disposed, _started;

    /// <summary>
    /// A session over the actors <paramref name="server"/> and <paramref name="clients"/> build: each gets the session's token, which
    /// <paramref name="cancellation"/> (the run's: Ctrl+C) and a failed <see cref="StartAsync"/> cancel.
    /// </summary>
    internal GameSession(ScenarioReport report, string output, string? worldUid, Func<CancellationToken, ServerActor>? server,
        IEnumerable<(string Name, Func<CancellationToken, ClientActor> Build)> clients, CancellationToken cancellation,
        Func<CancellationToken, HostingClientActor>? host = null)
    {
        if (server != null && host != null) throw new ArgumentException("A session has a dedicated server or a hosting client, not both.", nameof(host));
        Report = report ?? throw new ArgumentNullException(nameof(report));
        ArgumentException.ThrowIfNullOrEmpty(output);
        Output = output;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _token = _cancellation.Token; // Still readable once the session is disposed.
        _clients = [];
        var byName = new Dictionary<string, ClientActor>(StringComparer.Ordinal);
        try
        {
            Server = server?.Invoke(_token);
            Host = host?.Invoke(_token);
            if (Host != null && worldUid != null && worldUid != Host.WorldUid)
                throw new ArgumentException($"The session's world {worldUid} is not the hosted fixture's world {Host.WorldUid}.", nameof(worldUid));
            _worldUid = worldUid ?? Host?.WorldUid;
            foreach (var (name, build) in clients)
            {
                var actor = build(_token);
                if (actor.Name != name) throw new ArgumentException($"The actor built for client {name} is named {actor.Name}.", nameof(clients));
                if (!byName.TryAdd(name, actor)) throw new ArgumentException($"Two clients are named {name}.", nameof(clients));
                _clients.Add(actor);
            }
        }
        catch { _cancellation.Dispose(); throw; } // Nothing has started; the run's token keeps no registration of ours.
        Clients = byName;
    }

    /// <summary>The owned dedicated server, or null for a session without one.</summary>
    public ServerActor? Server { get; }
    /// <summary>The client that hosts the session's world (a listen server), or null; a session has it or a <see cref="Server"/>.</summary>
    public HostingClientActor? Host { get; }
    /// <summary>The session's clients by name, for example <c>client</c>, or a campaign's <c>client-a</c> and <c>client-b</c>.</summary>
    public IReadOnlyDictionary<string, ClientActor> Clients { get; }
    public ScenarioReport Report { get; }
    /// <summary>The run's new output directory: reports, per-boot logs, command records and kept client logs.</summary>
    public string Output { get; }
    /// <summary>Cancelled by Ctrl+C or SIGTERM, and when <see cref="StartAsync"/> fails.</summary>
    public CancellationToken Cancellation => _token;
    /// <summary>Every actor's kept logs, the server's (or host's) first, for the teardown scan once the processes stopped.</summary>
    public IReadOnlyList<RunLog> Logs
    {
        get
        {
            List<ClientActor> opened; List<RunLog> added;
            lock (_opened) { opened = [.. _opened]; added = [.. _added]; }
            return [.. Server?.Logs ?? [], .. Host?.Logs ?? [], .. _clients.SelectMany(client => client.Logs), .. opened.SelectMany(client => client.Logs), .. added];
        }
    }

    /// <summary>Adds a log the teardown scan reads besides the actors' own, for example one a scenario's own tool wrote.</summary>
    public void AddLog(RunLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (_opened) _added.Add(log);
    }

    /// <summary>
    /// Opens a client of <paramref name="plan"/> through a new <see cref="ClientActor"/> and returns its session; its logs join
    /// <see cref="Logs"/>, and one the scenario leaves open is closed at teardown before the server stops. In a campaign,
    /// <paramref name="name"/> names the campaign's client (left out when it declares one), which starts on its assigned host on
    /// its leased Steam identity; otherwise the client opens on this machine. A campaign's client, and any client opened by name,
    /// writes its evidence into its own folder (<see cref="ActorOutput"/>). With <paramref name="directory"/>, its evidence goes
    /// to that subdirectory of <see cref="Output"/> instead, so a second client never overwrites the first one's. Until the
    /// scenarios declare their clients, this is how a scenario opens one. A client opened into its own
    /// <paramref name="directory"/> and no name is named by its directory in the report.
    /// </summary>
    public ClientSession OpenClient(ClientRunPlan plan, string? name = null, string? directory = null) => OpenClient(plan, name, directory, Cancellation);

    private ClientSession OpenClient(ClientRunPlan plan, string? name, string? directory, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (actorName, placement) = ResolveClient(plan, name);
        if (name == null && directory != null) actorName = directory;
        string output = Output;
        if (directory != null)
        {
            output = Path.GetFullPath(Path.Combine(Output, directory));
            if (!output.StartsWith(Path.GetFullPath(Output) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Name a subdirectory of the session's output.", nameof(directory));
        }
        // A campaign's client (by name, or the one it declares) and any client opened by name: its own folder.
        else if (name != null || CampaignClients.Contains(actorName)) output = ActorFolder(Output, actorName);
        var actor = new ClientActor(actorName, plan, output, placement, cancellation);
        lock (_opened)
        {
            // Under the lock teardown takes its list with: an actor added here is one teardown closes.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _opened.Add(actor);
        }
        Directory.CreateDirectory(output); // Once the open is accepted: an open refused at teardown leaves no empty folder.
        var session = actor.Start();
        Report.RecordPlugins(actorName, plan.Pins);
        RecordInstall(actorName, plan);
        return session;
    }

    // Which install an owned client on this machine ran: a disposable copy (the default) or the install itself (inPlace).
    private void RecordInstall(string actor, ClientRunPlan plan)
    {
        string? used = !plan.Owned ? null : plan.CopiedFrom != null ? $"disposable copy of {plan.CopiedFrom} ({plan.Install})" : plan.InPlace ? $"in place: {plan.Install}" : null;
        if (used != null) lock (Report.Provenance) Report.Provenance[actor == "client" ? "clientInstall" : actor + "Install"] = used;
    }
    private readonly List<ClientActor> _opened = [];
    private readonly List<RunLog> _added = [];

    /// <summary>
    /// The one evidence-folder rule (#445, #446): a named actor of a session (each client of a campaign, with or without a dedicated
    /// server, its hosting client, and any client opened by name) writes its evidence (command and process records, kept logs) into
    /// its own folder <c>&lt;output&gt;/&lt;actor&gt;</c>, created here. Actors start at once, and each launch writes files of the
    /// same names, so no two may share a folder. A run's one unnamed client on this machine, and a host run's one host, write into
    /// the output itself. <see cref="OpenClient"/> applies it to the clients it opens; a session with declared clients or a
    /// campaign's refuses, before anything starts, a declared actor built into another folder.
    /// </summary>
    internal static string ActorOutput(string output, string actor) => Directory.CreateDirectory(ActorFolder(output, actor)).FullName;

    // The folder's path. Its name is the actor's, as the run journal names it (RunJournal.SafeName): one folder name on any host.
    private static string ActorFolder(string output, string actor)
    {
        ArgumentException.ThrowIfNullOrEmpty(output);
        if (!RunJournal.SafeName(actor ?? ""))
            throw new ArgumentException($"Actor name '{actor}' is not letters, digits, '.', '_' and '-', so it cannot name the actor's evidence folder.", nameof(actor));
        return Path.GetFullPath(Path.Combine(output, actor!));
    }

    // The rule, checked before anything starts: in a session with declared clients or a campaign's, every declared actor (the
    // hosting client included) writes into its own folder, so a session built without it fails loudly rather than racing.
    private void RequireOwnFolders()
    {
        if (_clients.Count == 0 && CampaignClients.Count == 0) return;
        var actors = _clients.Select(client => (client.Name, client.Output)).Concat(Host is { } host ? new[] { (host.Name, host.Output) } : []).ToList();
        foreach (var (name, output) in actors)
            if (!string.Equals(Path.GetFullPath(output), ActorFolder(Output, name), StringComparison.Ordinal))
                throw new InvalidOperationException($"Actor {name} writes its evidence to {output}; in this session each actor writes into its own folder {ActorFolder(Output, name)} (GameSession.ActorOutput).");
        // Names that differ only in case are one folder on Windows and macOS.
        if (actors.GroupBy(actor => actor.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(same => same.Count() > 1) is { } shared)
            throw new InvalidOperationException($"Actors {string.Join(" and ", shared.Select(actor => actor.Name))} would share one evidence folder; name them apart.");
    }

    /// <summary>
    /// Opens the named campaign clients at once (<see cref="OpenClient"/> for each), each on its own host, Steam lease, install and
    /// character; all have opened before this returns. If one fails, every client that opened is closed and the first failure is
    /// rethrown (not the cancellation it caused in the others).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ClientSession>> OpenClientsAsync(IReadOnlyDictionary<string, ClientRunPlan> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        if (clients.Count == 0) throw new ArgumentException("Name at least one client.", nameof(clients));
        var opened = new System.Collections.Concurrent.ConcurrentDictionary<string, ClientSession>(StringComparer.Ordinal);
        // The first failure ends the other opens' waits at once, rather than after their whole start deadlines. It is the one
        // rethrown: the siblings it cancelled fail with OperationCanceledException, and the aggregate Task.WhenAll throws from
        // did not reliably name the failure first (#448). Only a cancellation of the session itself is rethrown as one.
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        Exception? first = null;
        var opens = clients.Select(pair => Task.Run(() =>
        {
            siblings.Token.ThrowIfCancellationRequested();
            try { opened[pair.Key] = OpenClient(pair.Value, pair.Key, null, siblings.Token); }
            catch (Exception error)
            {
                if (error is not OperationCanceledException) Interlocked.CompareExchange(ref first, error, null);
                siblings.Cancel();
                throw;
            }
        })).ToArray();
        try { await Task.WhenAll(opens).ConfigureAwait(false); }
        catch
        {
            foreach (var session in opened.Values) try { session.Dispose(); } catch (Exception error) { Console.Error.WriteLine("Teardown: " + error.Message); }
            if (Volatile.Read(ref first) is not { } cause) throw;
            // Another open that failed on its own, not by the cancellation, is named too: it would be the next run's refusal.
            foreach (var other in opens.Select(open => open.Exception?.InnerException).OfType<Exception>())
                if (other != cause && other is not OperationCanceledException) Console.Error.WriteLine("Another client's open failed too: " + other.Message);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cause);
        }
        return new Dictionary<string, ClientSession>(opened, StringComparer.Ordinal);
    }

    /// <summary>
    /// An owned client's live BepInEx log on this machine, for a scenario's in-run log reads; null for a campaign's clients (on
    /// their hosts) and an attached client (its logs are its operator's).
    /// </summary>
    public string? ClientLog(ClientRunPlan client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return LiveClientLog != null ? LiveClientLog(client)
            : CampaignClients.Count == 0 && client.Owned ? Path.Combine(client.Install, "BepInEx", "LogOutput.log") : null;
    }
    internal Func<ClientRunPlan, string?>? LiveClientLog { get; init; }

    /// <summary>How often a scenario's observation waits re-read: one second; scenario tests over scripted worlds shorten it.</summary>
    public TimeSpan Interval { get; internal init; } = TimeSpan.FromSeconds(1);

    /// <summary>A campaign's client names (<see cref="PinnedServerRun.RunCampaignAsync{TPlan}"/>), each opened on its assigned host by <see cref="OpenClient"/>; empty when clients open on this machine.</summary>
    public IReadOnlyList<string> CampaignClients { get; internal init; } = [];

    /// <summary>Where <see cref="OpenClient"/> places a client: the runner's campaign placement, or this machine by default.</summary>
    internal Func<ClientRunPlan, string?, (string Name, IClientPlacement Placement)> ResolveClient { get => _resolveClient ?? ThisMachine; init => _resolveClient = value; }
    private readonly Func<ClientRunPlan, string?, (string Name, IClientPlacement Placement)>? _resolveClient;
    // This machine: one placement for the session, so a client that leaves and rejoins between rounds reruns its one disposable
    // copy, which the session's teardown removes once the client is closed.
    private readonly LocalClientPlacement _local = new();
    internal (string Name, IClientPlacement Placement) ThisMachine(ClientRunPlan plan, string? name) =>
        name == null ? ("client", _local)
        : throw new ArgumentException("A named client opens only in a campaign that declares clients (PinnedServerRun.RunCampaignAsync).", nameof(name));

    /// <summary>The host a campaign client runs on, for reading its files or capturing there.</summary>
    public IGameHost ClientHost(string campaignClient) => FindClientHost?.Invoke(campaignClient)
        ?? throw new ArgumentException("This run is not a campaign with named clients.", nameof(campaignClient));
    internal Func<string, IGameHost>? FindClientHost { get; init; }

    /// <summary>
    /// Starts every actor at once: the server's boot ("start and verify owned dedicated fixture") and each client to its menu
    /// ("client X at its menu, plugins pinned"), each a Setup step. When one fails, the session's token is cancelled so the
    /// others end their waits, every start settles, the session is disposed (clients closed, then the server stopped, each a
    /// Cleanup step), and the first failure is rethrown.
    /// </summary>
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("The session has started already; it starts once.");
        RequireOwnFolders();
        _started = true;
        Cancellation.ThrowIfCancellationRequested();
        var starts = new List<Task>();
        Mod?.Validate(); // Before anything starts.
        if (Host is { } prepared)
            try
            {
                // The host's preflight, save directory and placed fixture world, before any actor starts: a wrong fixture or install
                // stops the session before a client is launched.
                await Task.Run(() => prepared.Prepare(Report), Cancellation).ConfigureAwait(false);
            }
            catch
            {
                await _cancellation.CancelAsync().ConfigureAwait(false);
                if (DisposeOnFailedStart) await DisposeAsync().ConfigureAwait(false);
                throw;
            }
        if (Server is { } server)
            starts.Add(Task.Run(() =>
            {
                Report.Step(StepPhase.Setup, "start and verify owned dedicated fixture", () => server.Start());
                if (server.Plan is { } plan) Report.RecordPlugins("server", plan.Pins);
                // Runtime-ready: the mod is loaded and patched in, before any scenario step; a failure cancels the clients' starts.
                if (Mod is { ChecksPatches: true } mod && ServerPinsMod)
                    Report.Step(StepPhase.Setup, "server: the mod's Harmony patches are applied", () => mod.RequirePatchesApplied(server.Game));
            }));
        if (Host is { } host)
            starts.Add(Task.Run(() =>
            {
                // Prepared above: the client to its menu, then its world opens.
                Report.Step(StepPhase.Setup, $"hosting client {host.Name} at its menu, plugins pinned", () => host.Start());
                Report.RecordPlugins(host.Name, host.Plan.Pins);
                RecordInstall(host.Name, host.Plan);
                Report.Step(StepPhase.Setup, $"hosting client {host.Name}'s ValheimCLI offers the hosted-world session commands", () => CliCapabilities.Require(host.Game, CliCapabilities.HostedRounds));
                if (Mod is { ChecksPatches: true } mod && ServerPinsMod)
                    Report.Step(StepPhase.Setup, "host: the mod's Harmony patches are applied", () => mod.RequirePatchesApplied(host.Game));
                Report.Step(StepPhase.Setup, "host the fixture world with the disposable character" + (host.ProtectPlayer ? ", protected" : ""), () => host.StartWorld(host.Game));
                if (host.Plan.Owned) Report.Step(StepPhase.Setup, "establish test access on the owned host", () => HostedWorldLifecycle.EstablishTestAccess(host.Game));
            }));
        foreach (var client in _clients)
            starts.Add(Task.Run(() =>
            {
                Report.Step(StepPhase.Setup, $"client {client.Name} at its menu, plugins pinned", () => client.Start());
                Report.RecordPlugins(client.Name, client.Plan.Pins);
                RecordInstall(client.Name, client.Plan);
            }));
        // The first failure cancels the rest at once, rather than after every sibling has run out its own deadline.
        var pending = new List<Task>(starts);
        Exception? first = null;
        while (pending.Count != 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (done.Exception is { } failed && first == null)
            {
                first = failed.InnerException ?? failed;
                await _cancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        if (first == null) return;
        // Every start settled: the teardown (clients before their server, each a Cleanup step), then the first failure. A runner
        // with its own bounded cleanup (PinnedServerRun) disposes the session there instead.
        if (DisposeOnFailedStart) await DisposeAsync().ConfigureAwait(false);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }

    /// <summary>The mod's declaration, whose Harmony check <see cref="StartAsync"/> runs on the server (or host) when <see cref="ServerPinsMod"/>.</summary>
    internal ModDeclaration? Mod { get; init; }
    /// <summary>Whether the server (or host) loads the mod: its plugin pinned to a build, or an unpinned plan.</summary>
    internal bool ServerPinsMod { get; init; }


    /// <summary>Whether a failed <see cref="StartAsync"/> disposes the session before rethrowing; false where the caller's own cleanup does it.</summary>
    internal bool DisposeOnFailedStart { get; init; } = true;

    /// <summary>
    /// The barrier "the server accepts game connections", within the server's startup deadline; with a hosting client, "the host's
    /// world accepts game connections" (<see cref="HostingClientActor.WaitUntilJoinable"/>), within its plan's join time.
    /// </summary>
    public async Task ServerJoinable()
    {
        var (joinable, game, what) = RequireJoinable();
        await Task.Run(() => Report.Step(StepPhase.Setup, what + " accepts game connections", () => joinable.WaitUntilJoinable(game())), Cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The barrier "client X in world &lt;uid&gt;": the server accepts connections, then <paramref name="client"/> joins it once
    /// (<see cref="SessionControl.JoinWorld"/>: by address, or the crossplay <paramref name="lobby"/>; the world pinned and awaited;
    /// <paramref name="protect"/> enables god, ghost and debug protection), within the client plan's join time. With
    /// <paramref name="arrival"/>, the player then stands there (<see cref="PlayerPlacement.Arrive"/>) within its arrival time, and
    /// the arrival (its support reading and teleport timing) is returned for the scenario's evidence; otherwise null.
    /// </summary>
    public async Task<PlayerPlacement.TeleportArrival?> Join(string client, HeightExpectation? arrival = null, bool protect = true, CrossplayLobby? lobby = null)
    {
        var actor = RequireClient(client);
        var (joinable, game, what) = RequireJoinable();
        string world = _worldUid ?? throw new InvalidOperationException("The session's server pins no world UID, so a join has no world to verify.");
        var host = Host;
        // A host's peer joins it by its identity, a dedicated server's client by its address or lobby: each plan says which it is.
        if (host != null && !actor.Plan.JoinsHost) throw new ArgumentException($"Client {client} joins a dedicated server; this session's world is hosted by {host.Name}: set joinsHost on its plan.", nameof(client));
        if (host == null && actor.Plan.JoinsHost) throw new ArgumentException($"Client {client} is a host's peer (joinsHost), but this session has a dedicated server.", nameof(client));
        if (host != null && lobby != null) throw new ArgumentException("A host's peer joins it by its identity, not a lobby.", nameof(lobby));
        if (actor.Plan.Crossplay && lobby == null) throw new ArgumentException($"Client {client} joins by crossplay: pass the server's lobby.", nameof(lobby));
        return await Task.Run(() =>
        {
            Report.Step(StepPhase.Setup, $"{what} accepts game connections for client {client}", () => joinable.WaitUntilJoinable(game()));
            if (host != null)
                Report.Step(StepPhase.Setup, $"client {client} in host {host.Name}'s world {world}" + (protect ? ", protected" : ""),
                    () => new SessionControl(actor.Game).JoinHost(actor.Plan, host.Game, world, protect, Cancellation));
            else
                Report.Step(StepPhase.Setup, $"client {client} in world {world}" + (protect ? ", protected" : ""),
                    () => new SessionControl(actor.Game).JoinWorld(actor.Plan, world, lobby, protect, Cancellation));
            PlayerPlacement.TeleportArrival? arrived = null;
            if (arrival is { } point)
                Report.Step(StepPhase.Setup, $"client {client} at the arrival point",
                    () => arrived = PlayerPlacement.Arrive(game(), actor.Game, point, TimeSpan.FromSeconds(actor.Plan.ArrivalSeconds), Cancellation));
            return arrived;
        }, Cancellation).ConfigureAwait(false);
    }

    /// <summary>Every client joins, one after another in name order (<see cref="Join"/>, protected, no arrival point; a crossplay server's <paramref name="lobby"/>; a host's peers by its identity).</summary>
    public async Task JoinAll(CrossplayLobby? lobby = null)
    {
        foreach (var name in Clients.Keys.Order(StringComparer.Ordinal)) await Join(name, lobby: lobby).ConfigureAwait(false);
    }

    /// <summary>
    /// The barrier "client X back at its menu", then <see cref="Join"/> again: the client leaves the world (pins verified at the
    /// menu again) and rejoins the server's current boot, or the host's world. It first waits, within the client's join time, for
    /// the client to settle where it is (<see cref="SessionControl"/>: idle at its menu, or in the world with its player): a client the
    /// world already dropped (a host's restart disconnects its peers) is only re-pinned at its menu and recorded in
    /// <c>rejoinedFromMenu</c>; a leave is issued only from a loaded world.
    /// </summary>
    public async Task<PlayerPlacement.TeleportArrival?> Rejoin(string client, HeightExpectation? arrival = null, bool protect = true, CrossplayLobby? lobby = null)
    {
        var actor = RequireClient(client);
        string? world = _worldUid;
        await Task.Run(() => Report.Step(StepPhase.Setup, $"client {client} back at its menu", () =>
        {
            var game = actor.Game;
            var control = new SessionControl(game);
            // Where the client is now, once it settles (a host's restart drops its peers to their menus, and they may still be leaving).
            if (!control.SettleAtMenuOrInWorld(actor.Plan, world, TimeSpan.FromSeconds(actor.Plan.JoinSeconds), Cancellation))
            {
                // Already at its menu (the world dropped it): nothing to leave.
                var rejoined = Report.Provenance.TryGetValue("rejoinedFromMenu", out var earlier) ? earlier.Split(',').ToList() : [];
                if (!rejoined.Contains(client)) rejoined.Add(client);
                Report.Provenance["rejoinedFromMenu"] = string.Join(",", rejoined);
                return;
            }
            control.Leave();
            game.VerifyEnvironment(actor.Plan.MenuExpectations); // A transition always needs fresh pins.
        }), Cancellation).ConfigureAwait(false);
        return await Join(client, arrival, protect, lobby).ConfigureAwait(false);
    }

    /// <summary>
    /// A scenario's own readiness as a named Setup step: <paramref name="wait"/> gets a token that ends after
    /// <paramref name="within"/> (or on the session's cancellation); running out is a <see cref="WaitTimeoutException"/> that names
    /// <paramref name="name"/>. Wait on events or read-only observations; never issue a mutation more than once.
    /// </summary>
    public Task Barrier(string name, Func<CancellationToken, Task> wait, TimeSpan within)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(wait);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (within <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(within), "A barrier waits a bounded, positive time.");
        return Report.StepAsync(StepPhase.Setup, name, async () =>
        {
            var clock = Stopwatch.StartNew();
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            bound.CancelAfter(within);
            try { await wait(bound.Token).WaitAsync(bound.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (bound.IsCancellationRequested && !Cancellation.IsCancellationRequested)
            {
                throw new WaitTimeoutException(name + " within " + WaitText.Seconds(within), clock.Elapsed, null);
            }
        });
    }

    /// <summary>
    /// Closes the clients in reverse order, then stops the server, each a Cleanup step; a failed close is recorded and the rest
    /// still run. The session's token is cancelled first, so a barrier the scenario left running ends now. A hosting client is
    /// closed after its peers: when everything so far passed, it first leaves its world to its menu (the game saves it); an owned
    /// one is then stopped, an operator's detached. Its placed world is moved into the evidence only once no client can still host
    /// it (it left, or its owned process stopped); otherwise it is left in place and named (<c>hostWorldLeftInPlace</c>).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_opened)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { await _cancellation.CancelAsync().ConfigureAwait(false); } catch (AggregateException error) { Console.Error.WriteLine("Teardown: " + error.Message); }
        List<ClientActor> opened;
        lock (_opened) opened = [.. _opened];
        foreach (var client in Enumerable.Reverse(_clients.Concat(opened).ToList()))
            if (client.Session is { } open)
                try { Report.Step(StepPhase.Cleanup, open.Owned ? $"stop only the owned client {client.Name}" : $"detach from the operator's client {client.Name}", client.Dispose); }
                catch (Exception error) { TeardownFailures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
            else client.Dispose();
        if (Host is { } host) CloseHost(host);
        // This machine's disposable client copies, once their clients are closed (a client that could not be stopped keeps its copy).
        if (_local.HasCopies)
            try { Report.Step(StepPhase.Cleanup, "remove the disposable client copy", () => _local.RetireAsync().GetAwaiter().GetResult()); }
            catch (Exception error) { TeardownFailures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        if (Server is { } server)
            try { Report.Step(StepPhase.Cleanup, "stop only owned server", server.Dispose); }
            catch (Exception error) { ServerStopped = false; TeardownFailures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        // A boot that failed to start and could not be stopped may still run too.
        if (Server is { MayStillRun: true }) ServerStopped = false;
        _cancellation.Dispose();
    }

    /// <summary>After disposing: false when the server's stop failed or a boot may still run, so its runtime and host lock are kept.</summary>
    internal bool ServerStopped { get; private set; } = true;
    /// <summary>After disposing: each failed close or stop, for the runner to classify (an unknown host outcome is not a failure).</summary>
    internal List<Exception> TeardownFailures { get; } = [];

    // The hosting client's teardown, after its peers: leave (a passing run), close, then the world once no client can host it.
    private void CloseHost(HostingClientActor host)
    {
        void Cleanup(string name, Action action)
        {
            try { Report.Step(StepPhase.Cleanup, name, action); }
            catch (Exception error) { TeardownFailures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        }
        if (host.Session is { } open)
        {
            // An operator's client after a failure is left as it is, its world in place for inspection.
            if (host.Hosting && Report.Passed) Cleanup("the host leaves its world to its menu", () => host.LeaveWorld(host.Game));
            Cleanup(open.Owned ? $"stop only the owned hosting client {host.Name}" : $"detach from the operator's hosting client {host.Name}", host.CloseClient);
        }
        else
            try { host.CloseClient(); } // Closed while it opened, or never opened.
            catch (Exception error) { TeardownFailures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        try { host.ReleaseWorld(Report); }
        catch (Exception error) { TeardownFailures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); } // Recorded as its failed step.
    }

    // The session's joinable: its dedicated server or its hosting client, with its current in-game handle and its name in steps.
    private (IOwnedServer Joinable, Func<GameActor> Game, string What) RequireJoinable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Host is { } host) return (host, () => host.Game, "the host's world");
        var server = Server ?? throw new InvalidOperationException("This session has no owned server or hosting client.");
        return (server, () => server.Game, "the server");
    }
    private ClientActor RequireClient(string name) => _disposed ? throw new ObjectDisposedException(nameof(GameSession)) : Clients.TryGetValue(name, out var actor) ? actor
        : throw new ArgumentException($"No client {name} in this session; it has {(Clients.Count == 0 ? "none" : string.Join(", ", Clients.Keys.Order(StringComparer.Ordinal)))}.", nameof(name));
}
