using Valheim.Testing.Game;
namespace Valheim.Testing.GameSessions;

/// <summary>
/// One named game client of a run, from its open to its close. It is the one client open path (#258): <see cref="Start"/>
/// opens the client to its strictly pinned menu through its placement, once, and that open's kept logs, a failed startup's
/// included, are the actor's (<see cref="Logs"/>) for the teardown scan. Where the client runs is its placement: this machine
/// (<see cref="OnThisMachine"/>: launched, or attached to an operator's client, as the plan's mode says), or the host a
/// campaign assigned it, in that host's desktop session or, for a local macOS host, this runner's GUI session, on its leased
/// Steam identity (<see cref="GameSession.OpenClient"/>). <see cref="Game"/> is the open client's in-game
/// handle. Disposing it closes the client: it stops only a process it started, and detaches from an operator's.
/// </summary>
public sealed class ClientActor : IDisposable
{
    private readonly IClientPlacement _placement;
    private readonly string _output;
    private readonly CancellationToken _cancellation;
    private readonly List<RunLog> _logs = [];
    private readonly object _state = new();
    private ClientSession? _session;
    private bool _started, _disposed;

    internal ClientActor(string name, ClientRunPlan plan, string output, IClientPlacement placement, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrEmpty(name); ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(output); ArgumentNullException.ThrowIfNull(placement);
        Name = name; Plan = plan; _output = output; _placement = placement; _cancellation = cancellation;
    }

    /// <summary>
    /// A client on this machine, its evidence (command record, process record, kept logs) written to <paramref name="output"/>:
    /// launched (<see cref="ClientSession.Launch(ClientRunPlan, string, CancellationToken)"/>) from a disposable copy of the plan's
    /// install, which the actor makes when it opens and removes when it is disposed, or from the install itself with
    /// <see cref="ClientRunPlan.InPlace"/>; or attached to the operator's client
    /// (<see cref="ClientSession.Attach(ClientRunPlan, string, IGameTransport)"/>), as its mode says.
    /// </summary>
    public static ClientActor OnThisMachine(string name, ClientRunPlan plan, string output, CancellationToken cancellation = default) =>
        new(name, plan, output, new LocalClientPlacement(), cancellation) { RetiresPlacement = true };

    /// <summary>Whether disposing the actor retires its placement's disposable copy: an actor on this machine made outside a session.</summary>
    internal bool RetiresPlacement { get; init; }
    internal IClientPlacement Placement => _placement;

    /// <summary>The client's name in the run, for example <c>client</c> or a campaign's <c>client-a</c>.</summary>
    public string Name { get; }
    /// <summary>The folder its evidence goes to (<see cref="GameSession.ActorOutput"/>).</summary>
    internal string Output => _output;
    public ClientRunPlan Plan { get; }
    /// <summary>The open client's session, or null before <see cref="Start"/> and once it is closed.</summary>
    public ClientSession? Session { get { lock (_state) return _session is { Closed: false } open ? open : null; } }
    /// <summary>The open client's in-game handle, strictly pinned at its menu when it opened.</summary>
    public GameActor Game => Session?.Actor ?? throw new InvalidOperationException($"Client {Name} is not open.");
    /// <summary>Every open's kept logs (its BepInEx log and Player.log), a startup that failed after its process started included, for the teardown scan once the client is closed.</summary>
    public IReadOnlyList<RunLog> Logs { get { lock (_logs) return _logs.ToArray(); } }

    /// <summary>
    /// Opens the client to its menu, plugins pinned, and returns its session; closing the session (or this actor) closes it.
    /// An actor opens its client once: its evidence (command and process records, kept logs) is that one open's.
    /// </summary>
    public ClientSession Start()
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException($"Client {Name} was opened already; an actor opens its client once.");
            _started = true;
        }
        ClientSession session;
        try { session = _placement.Open(Name, Plan, _output, _cancellation); }
        catch (Exception error)
        {
            // A startup that failed after the client started kept its logs: they are scanned and listed like an opened client's.
            lock (_logs) _logs.AddRange(ClientSession.KeptLogs(error));
            throw;
        }
        lock (_logs) _logs.AddRange(session.Logs);
        bool disposed;
        lock (_state) { disposed = _disposed; if (!disposed) _session = session; }
        if (disposed)
        {
            // Closed while it was opening (teardown, cancellation): nobody else will close it.
            session.Dispose();
            throw new ObjectDisposedException(nameof(ClientActor), $"Client {Name} was closed while it opened.");
        }
        return session;
    }

    /// <summary>
    /// Closes the client, if open: it stops only a process it started, and detaches from an operator's. A client still opening is
    /// closed when its open returns. An actor made with <see cref="OnThisMachine"/> then removes its disposable copy (a client
    /// whose stop failed keeps it, for <c>valheim-test env recover</c>).
    /// </summary>
    public void Dispose()
    {
        ClientSession? open;
        lock (_state) { _disposed = true; open = _session; _session = null; }
        open?.Dispose(); // Throws when the owned client could not be stopped: its copy is then kept, for env recover.
        RetireCopy();
    }

    /// <summary>An actor on this machine made outside a session: removes its disposable copy, once its client was stopped.</summary>
    internal void RetireCopy()
    {
        if (RetiresPlacement && _placement is LocalClientPlacement local) local.RetireAsync().GetAwaiter().GetResult();
    }
}

/// <summary>Where a client opens: what differs between this machine and a campaign's assigned host.</summary>
internal interface IClientPlacement
{
    /// <summary>Opens <paramref name="name"/>'s client to its menu. A startup that fails after its process started carries its kept logs (<see cref="ClientSession.KeptLogs"/>).</summary>
    ClientSession Open(string name, ClientRunPlan plan, string output, CancellationToken cancellation);
}

/// <summary>
/// A client on this machine: launched, or attached to the operator's, as the plan's mode says. An owned client is launched
/// from its disposable copy (<see cref="LocalClientCopy"/>), made at its plan's first open and kept for the placement's later
/// opens of the same plan (a client that leaves and rejoins between rounds), or, with <see cref="ClientRunPlan.InPlace"/>, from
/// its install as it is. <see cref="RetireAsync"/> removes the copies once their clients are closed.
/// </summary>
internal sealed class LocalClientPlacement : IClientPlacement
{
    // Each copy with, per launch from it, whether that client is proven stopped; a copy is removed only once each one is.
    private readonly List<(LocalClientCopy Copy, ClientRunPlan Plan, List<Func<bool>> Stopped)> _copies = [];
    private bool _closed;
    // Test seam: the copy, in place of LocalClientCopy.PrepareAsync.
    internal Func<string, ClientRunPlan, CancellationToken, Task<LocalClientCopy>>? PrepareCopy { get; init; }

    public ClientSession Open(string name, ClientRunPlan plan, string output, CancellationToken cancellation)
    {
        if (plan.Owned && plan.InPlace)
            // Run in place: nothing is staged, so the only conflict to rule out first is a Valheim that already runs here.
            HostedRuntimeStage.RequireStoppedAsync(new LocalGameHost("this machine", OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash),
                LocalClientCopy.StepTimeout, cancellation, clientSession: true).GetAwaiter().GetResult();
        else if (plan.CopySource)
        {
            var made = (PrepareCopy ?? ((actor, client, token) => LocalClientCopy.PrepareAsync(actor, client, token)))(name, plan, cancellation).GetAwaiter().GetResult();
            bool closed;
            lock (_copies) { closed = _closed; if (!closed) _copies.Add((made, plan, [])); }
            if (closed)
            {
                // Closed while the copy was made (teardown, cancellation): nothing else would remove it.
                made.RetireAsync().GetAwaiter().GetResult();
                throw new ObjectDisposedException(nameof(LocalClientPlacement), $"Client {name} was closed while its copy was made.");
            }
        }
        var entry = Entry(plan);
        if (entry == null) return ClientSession.Open(plan, output, cancellation);
        // Until the launch returns, a game may run from the copy: an open that fails is cleared only once none does.
        int at;
        lock (_copies) { entry.Value.Stopped.Add(() => false); at = entry.Value.Stopped.Count - 1; }
        ClientSession session;
        try { session = ClientSession.Open(plan, output, cancellation); }
        catch
        {
            // A failed launch stops its process; where the system names a game by its path, that is confirmed and the copy can
            // go. Elsewhere (Linux), or if a game still runs from it, the copy is kept for env recover.
            if (!OperatingSystem.IsLinux())
                try
                {
                    HostedRuntimeStage.RequireStoppedAsync(new LocalGameHost("this machine", OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash),
                        LocalClientCopy.StepTimeout, CancellationToken.None, runtimes: [entry.Value.Copy.Runtime], clientSession: false).GetAwaiter().GetResult();
                    lock (_copies) entry.Value.Stopped[at] = () => true;
                }
                catch (Exception error) when (error is InvalidOperationException or HostOperationException or IOException) { }
            throw;
        }
        lock (_copies) entry.Value.Stopped[at] = () => session.Closed && (session.Stopped != null || !session.Owned);
        return session;
    }

    private (LocalClientCopy Copy, ClientRunPlan Plan, List<Func<bool>> Stopped)? Entry(ClientRunPlan plan)
    {
        lock (_copies)
            foreach (var entry in _copies)
                if (ReferenceEquals(entry.Plan, plan)) return entry;
        return null;
    }

    /// <summary>
    /// Retires every copy this placement made whose clients were all stopped, each once, and makes no more; a copy one of whose
    /// clients was not proven stopped is kept (<c>valheim-test env status</c> names it, <c>env recover</c> removes it). The first
    /// failure is rethrown after all were tried.
    /// </summary>
    internal async Task RetireAsync()
    {
        List<(LocalClientCopy Copy, ClientRunPlan Plan, List<Func<bool>> Stopped)> copies;
        lock (_copies) { _closed = true; copies = [.. _copies]; _copies.Clear(); }
        var failures = new List<Exception>();
        foreach (var (copy, _, launches) in copies)
        {
            bool stopped;
            lock (_copies) stopped = launches.All(proven => proven());
            if (!stopped)
            {
                failures.Add(new IOException($"The disposable client copy {copy.Runtime} is kept: a client launched from it was not proven stopped. valheim-test env status names it; env recover removes it."));
                continue;
            }
            try { await copy.RetireAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(new IOException($"The disposable client copy {copy.Runtime} was not removed ({error.Message}); valheim-test env status names it and env recover removes it.", error)); }
        }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1) throw new AggregateException("Disposable client copies were not removed.", failures);
    }

    /// <summary>Whether this placement made a copy it has not retired.</summary>
    internal bool HasCopies { get { lock (_copies) return _copies.Count != 0; } }
}
