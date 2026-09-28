using System.Diagnostics;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

// Waits for a game state that ValheimCLI pushes (SUBSCRIBE_STATE, then STATE_CHANGED:<state> lines). It owns its
// connection and uses it for nothing else: a command on the same connection would be mixed into the pushes.
// Nothing polls: after one STATE question per wait, the wait is a pending read that completes when a push arrives.
// States are ValheimCLI's names; a dedicated server's loaded world is InWorldNoPlayer. A state is a world-scene fact,
// not mod readiness or permission to act: keep the session's own readiness check after it. One wait at a time.
public sealed class StateWait : IDisposable
{
    public const string Unknown = "Unknown", MainMenu = "MainMenu", Loading = "Loading", InWorld = "InWorld", InWorldNoPlayer = "InWorldNoPlayer";
    /// <summary>A world is loaded, on a dedicated server (no player) or a client.</summary>
    public static readonly IReadOnlyList<string> WorldLoaded = [InWorldNoPlayer, InWorld];
    private readonly ValheimClient _client;
    private readonly object _sync = new();
    private readonly List<string> _seen = [];
    private bool _subscribed, _disposed;
    private string? _broken;
    private int _waiting;
    /// <summary>The latest state received, or null before the first answer.</summary>
    public string? LastState { get { lock (_sync) return _seen.Count == 0 ? null : _seen[^1]; } }

    /// <summary>Takes ownership of a connected client that nothing else uses.</summary>
    public StateWait(ValheimClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.OnStateChanged += Record;
    }
    public static StateWait Connect(string host, int port)
    {
        var client = new ValheimClient(host, port);
        if (!client.Connect()) { client.Dispose(); throw new IOException("CLI state connection failed."); }
        return new StateWait(client);
    }

    /// <summary>
    /// Waits until the game reaches one of <paramref name="targets"/> and returns it. Subscribes on first use, asks the
    /// current state once, then awaits pushes. Every state seen during the wait counts, in order, including one pushed
    /// while the question was being answered. A state in <paramref name="failures"/>, a closed connection or a protocol
    /// error ends the wait at once with <see cref="WaitFailedException"/>; expiry throws <see cref="WaitTimeoutException"/>.
    /// Names compare case-insensitively, as ValheimCLI's own wait does. Expiry or cancellation closes the connection
    /// (an abandoned read leaves the stream's position unknown), so later waits on this instance fail: connect a new one.
    /// </summary>
    public async Task<string> WaitAsync(IReadOnlyCollection<string> targets, TimeSpan timeout, IReadOnlyCollection<string>? failures = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0) throw new ArgumentException("Name at least one target state.", nameof(targets));
        WaitText.RequireTimeout(timeout);
        failures ??= [];
        string target = "ValheimCLI state " + string.Join(" or ", targets);
        var clock = Stopwatch.StartNew();
        int next;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_broken != null) throw new WaitFailedException(target, "the state connection was closed by " + _broken + "; connect a new StateWait", TimeSpan.Zero, Seen());
            next = _seen.Count;
        }
        if (Interlocked.Exchange(ref _waiting, 1) == 1) throw new InvalidOperationException("A StateWait serves one wait at a time.");
        Task? asking = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try
        {
            // Pushes report changes only, so the current state is asked once, after subscribing. A change from then on is
            // either raised during the question (through OnStateChanged, ahead of its answer) or waits in the stream for
            // the reads below. Both calls block on the socket, so they run off this thread, bounded by the deadline.
            asking = Task.Run(Ask, CancellationToken.None);
            await asking.WaitAsync(deadline.Token).ConfigureAwait(false);
            while (true)
            {
                lock (_sync)
                    for (; next < _seen.Count; next++)
                    {
                        string state = _seen[next];
                        if (failures.Contains(state, StringComparer.OrdinalIgnoreCase))
                            throw new WaitFailedException(target, "the game reached failure state " + state, clock.Elapsed, "state " + state);
                        if (targets.Contains(state, StringComparer.OrdinalIgnoreCase)) return state;
                    }
                // Completes when the next push arrives and raises OnStateChanged, which records it; null means closed.
                if (await _client.ReadStateChangeAsync(deadline.Token).ConfigureAwait(false) == null)
                    throw Broken(new WaitFailedException(target, "ValheimCLI closed the state connection", clock.Elapsed, SeenLocked()), "the server");
            }
        }
        catch (IOException error)
        { throw Broken(new WaitFailedException(target, "the state connection failed: " + error.Message, clock.Elapsed, SeenLocked()), "a connection failure"); }
        catch (InvalidDataException error)
        { throw Broken(new WaitFailedException(target, "protocol error: " + error.Message, clock.Elapsed, SeenLocked()), "a protocol error"); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw Broken(new WaitTimeoutException(target, clock.Elapsed, SeenLocked()), "an expired wait"); }
        catch (OperationCanceledException)
        {
            Close("a cancelled wait");
            throw;
        }
        finally
        {
            // An unfinished question is still blocked on the socket; the failure closed it, which ends that read with an error nobody awaits.
            if (asking is { IsCompleted: false }) _ = asking.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            Volatile.Write(ref _waiting, 0);
        }
    }
    private void Ask()
    {
        if (!_client.IsConnected) throw new IOException("not connected");
        if (!_subscribed)
        {
            if (!_client.SubscribeToStateChanges()) throw new InvalidDataException("ValheimCLI did not confirm the state subscription");
            _subscribed = true;
        }
        string state = _client.GetState();
        // GetState reports a closed or reset connection as Unknown and disconnects.
        if (!_client.IsConnected) throw new IOException("closed before the current state was known");
        Record(state);
    }
    // After a failure the stream's position is unknown: close it so a pending read ends and no later wait trusts it.
    private T Broken<T>(T error, string cause) where T : Exception { Close(cause); return error; }
    private void Close(string cause)
    {
        lock (_sync) _broken ??= cause;
        _client.Disconnect();
    }
    private string SeenLocked() { lock (_sync) return Seen(); }
    private string Seen() => _seen.Count == 0 ? "no state received" : "state " + _seen[^1];
    private void Record(string state) { lock (_sync) _seen.Add(state); }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        _client.OnStateChanged -= Record;
        _client.Dispose();
    }
}
