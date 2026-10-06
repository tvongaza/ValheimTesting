using System.Diagnostics;

namespace Valheim.Testing.Game;

/// <summary>
/// One run's game processes in their roles (#258, decided on #289): an owned dedicated server (<see cref="Server"/>) and named
/// clients (<see cref="Clients"/>), each with its in-game handle <c>.Game</c>. A scenario is a function of the session and its
/// plan, <c>(GameSession session, TPlan plan) =&gt; Task</c>, and coordinates the actors only through the barriers, each one
/// named Setup step with a bounded wait that names itself when it times out:
/// <list type="bullet">
/// <item><see cref="StartAsync"/>: the server boots and every client reaches its menu at the same time; one failure cancels the
/// others, lets them settle, closes the clients and then the server, and is rethrown.</item>
/// <item><see cref="ServerJoinable"/>: the server accepts game connections.</item>
/// <item><see cref="Join"/>, <see cref="JoinAll"/>: a client (every client) in the server's world, by the one join
/// (<see cref="SessionControl.JoinWorld"/>), optionally standing at an arrival point (<see cref="PlayerPlacement.Arrive"/>).</item>
/// <item><see cref="Rejoin"/>: a client leaves to its menu and joins again.</item>
/// <item><see cref="Barrier"/>: a scenario's own readiness, waited on within a bound.</item>
/// </list>
/// Disposing it closes the clients in reverse order, then stops the server, each a Cleanup step: clients before their host.
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
        IEnumerable<(string Name, Func<CancellationToken, ClientActor> Build)> clients, CancellationToken cancellation)
    {
        Report = report ?? throw new ArgumentNullException(nameof(report));
        ArgumentException.ThrowIfNullOrEmpty(output);
        Output = output; _worldUid = worldUid;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _token = _cancellation.Token; // Still readable once the session is disposed.
        _clients = [];
        var byName = new Dictionary<string, ClientActor>(StringComparer.Ordinal);
        try
        {
            Server = server?.Invoke(_token);
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
    /// <summary>The session's clients by name, for example <c>client</c>, or a campaign's <c>client-a</c> and <c>client-b</c>.</summary>
    public IReadOnlyDictionary<string, ClientActor> Clients { get; }
    public ScenarioReport Report { get; }
    /// <summary>The run's new output directory: reports, per-boot logs, command records and kept client logs.</summary>
    public string Output { get; }
    /// <summary>Cancelled by Ctrl+C or SIGTERM, and when <see cref="StartAsync"/> fails.</summary>
    public CancellationToken Cancellation => _token;
    /// <summary>Every actor's kept logs, the server's first, for the teardown scan once the processes stopped.</summary>
    public IReadOnlyList<RunLog> Logs => [.. Server?.Logs ?? [], .. _clients.SelectMany(client => client.Logs)];

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
        _started = true;
        Cancellation.ThrowIfCancellationRequested();
        var starts = new List<Task>();
        if (Server is { } server)
            starts.Add(Task.Run(() => Report.Step(StepPhase.Setup, "start and verify owned dedicated fixture", () => server.Start())));
        foreach (var client in _clients)
            starts.Add(Task.Run(() => Report.Step(StepPhase.Setup, $"client {client.Name} at its menu, plugins pinned", () => client.Start())));
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
        // Every start settled: the teardown (clients before their server, each a Cleanup step), then the first failure.
        await DisposeAsync().ConfigureAwait(false);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }

    /// <summary>The barrier "the server accepts game connections", within the server's startup deadline.</summary>
    public async Task ServerJoinable()
    {
        var server = RequireServer();
        await Task.Run(() => Report.Step(StepPhase.Setup, "the server accepts game connections", () => server.WaitUntilJoinable(server.Game)), Cancellation).ConfigureAwait(false);
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
        var server = RequireServer();
        string world = _worldUid ?? throw new InvalidOperationException("The session's server pins no world UID, so a join has no world to verify.");
        if (actor.Plan.Crossplay && lobby == null) throw new ArgumentException($"Client {client} joins by crossplay: pass the server's lobby.", nameof(lobby));
        return await Task.Run(() =>
        {
            Report.Step(StepPhase.Setup, $"the server accepts game connections for client {client}", () => server.WaitUntilJoinable(server.Game));
            Report.Step(StepPhase.Setup, $"client {client} in world {world}" + (protect ? ", protected" : ""),
                () => new SessionControl(actor.Game).JoinWorld(actor.Plan, world, lobby, protect, Cancellation));
            PlayerPlacement.TeleportArrival? arrived = null;
            if (arrival is { } point)
                Report.Step(StepPhase.Setup, $"client {client} at the arrival point",
                    () => arrived = PlayerPlacement.Arrive(server.Game, actor.Game, point, TimeSpan.FromSeconds(actor.Plan.ArrivalSeconds), Cancellation));
            return arrived;
        }, Cancellation).ConfigureAwait(false);
    }

    /// <summary>Every client joins, one after another in name order (<see cref="Join"/>, protected, no arrival point; a crossplay server's <paramref name="lobby"/>).</summary>
    public async Task JoinAll(CrossplayLobby? lobby = null)
    {
        foreach (var name in Clients.Keys.Order(StringComparer.Ordinal)) await Join(name, lobby: lobby).ConfigureAwait(false);
    }

    /// <summary>
    /// The barrier "client X back at its menu", then <see cref="Join"/> again: the client leaves the world (pins verified at the
    /// menu again) and rejoins the server's current boot.
    /// </summary>
    public async Task<PlayerPlacement.TeleportArrival?> Rejoin(string client, HeightExpectation? arrival = null, bool protect = true, CrossplayLobby? lobby = null)
    {
        var actor = RequireClient(client);
        await Task.Run(() => Report.Step(StepPhase.Setup, $"client {client} back at its menu", () =>
        {
            new SessionControl(actor.Game).Leave();
            actor.Game.VerifyEnvironment(actor.Plan.MenuExpectations); // A transition always needs fresh pins.
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
    /// still run. The session's token is cancelled first, so a barrier the scenario left running ends now.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _cancellation.CancelAsync().ConfigureAwait(false); } catch (AggregateException error) { Console.Error.WriteLine("Teardown: " + error.Message); }
        foreach (var client in Enumerable.Reverse(_clients))
            if (client.Session is { } open)
                try { Report.Step(StepPhase.Cleanup, open.Owned ? $"stop only the owned client {client.Name}" : $"detach from the operator's client {client.Name}", client.Dispose); }
                catch (Exception error) { Console.Error.WriteLine("Teardown: " + error.Message); }
            else client.Dispose();
        if (Server is { } server)
            try { Report.Step(StepPhase.Cleanup, "stop only owned server", server.Dispose); }
            catch (Exception error) { ServerStopped = false; Console.Error.WriteLine("Teardown: " + error.Message); }
        // A boot that failed to start and could not be stopped may still run too.
        if (Server is { MayStillRun: true }) ServerStopped = false;
        _cancellation.Dispose();
    }

    /// <summary>After disposing: false when the server's stop failed or a boot may still run, so its runtime and host lock are kept.</summary>
    internal bool ServerStopped { get; private set; } = true;

    private ServerActor RequireServer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Server ?? throw new InvalidOperationException("This session has no owned server.");
    }
    private ClientActor RequireClient(string name) => _disposed ? throw new ObjectDisposedException(nameof(GameSession)) : Clients.TryGetValue(name, out var actor) ? actor
        : throw new ArgumentException($"No client {name} in this session; it has {(Clients.Count == 0 ? "none" : string.Join(", ", Clients.Keys.Order(StringComparer.Ordinal)))}.", nameof(name));
}
