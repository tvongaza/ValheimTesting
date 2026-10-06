namespace Valheim.Testing.Game;

/// <summary>
/// One named game client of a run, from its open to its close. It is the one client open path (#258): <see cref="Start"/>
/// opens the client to its strictly pinned menu through its placement, once, and that open's kept logs, a failed startup's
/// included, are the actor's (<see cref="Logs"/>) for the teardown scan. Where the client runs is its placement: this machine
/// (<see cref="OnThisMachine"/>: launched, or attached to an operator's client, as the plan's mode says), or the host a
/// campaign assigned it, in that host's desktop session or, for a local macOS host, this runner's GUI session, on its leased
/// Steam identity (<see cref="PinnedServerRunContext{TPlan}.OpenClient"/>). <see cref="Game"/> is the open client's in-game
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
    /// launched from the plan's install (<see cref="ClientSession.Launch(ClientRunPlan, string, CancellationToken)"/>) or attached
    /// to the operator's client (<see cref="ClientSession.Attach(ClientRunPlan, string, IGameTransport)"/>), as its mode says.
    /// </summary>
    public static ClientActor OnThisMachine(string name, ClientRunPlan plan, string output, CancellationToken cancellation = default) =>
        new(name, plan, output, LocalClientPlacement.Instance, cancellation);

    /// <summary>The client's name in the run, for example <c>client</c> or a campaign's <c>client-a</c>.</summary>
    public string Name { get; }
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

    /// <summary>Closes the client, if open: it stops only a process it started, and detaches from an operator's. A client still opening is closed when its open returns.</summary>
    public void Dispose()
    {
        ClientSession? open;
        lock (_state) { _disposed = true; open = _session; _session = null; }
        open?.Dispose();
    }
}

/// <summary>Where a client opens: what differs between this machine and a campaign's assigned host.</summary>
internal interface IClientPlacement
{
    /// <summary>Opens <paramref name="name"/>'s client to its menu. A startup that fails after its process started carries its kept logs (<see cref="ClientSession.KeptLogs"/>).</summary>
    ClientSession Open(string name, ClientRunPlan plan, string output, CancellationToken cancellation);
}

/// <summary>A client on this machine: launched, or attached to the operator's, as the plan's mode says.</summary>
internal sealed class LocalClientPlacement : IClientPlacement
{
    public static readonly LocalClientPlacement Instance = new();
    public ClientSession Open(string name, ClientRunPlan plan, string output, CancellationToken cancellation) => ClientSession.Open(plan, output, cancellation);
}
