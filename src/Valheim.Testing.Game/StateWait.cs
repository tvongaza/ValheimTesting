using System.Diagnostics;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

// Waits for a game state that ValheimCLI pushes (SUBSCRIBE_STATE, then STATE_CHANGED:<state> lines). It owns its
// connection and uses it for nothing else: a command on the same connection would consume the pushes itself.
// States are ValheimCLI's names; a dedicated server's loaded world is InWorldNoPlayer. A state is a world-scene fact,
// not mod readiness or permission to act: keep the session's own readiness check after it.
public sealed class StateWait : IDisposable
{
    public const string Unknown = "Unknown", MainMenu = "MainMenu", Loading = "Loading", InWorld = "InWorld", InWorldNoPlayer = "InWorldNoPlayer";
    /// <summary>A world is loaded, on a dedicated server (no player) or a client.</summary>
    public static readonly IReadOnlyList<string> WorldLoaded = [InWorldNoPlayer, InWorld];
    // ValheimClient raises OnStateChanged only while a caller reads its socket, and offers no awaitable read. The feed
    // thread therefore checks the bytes already received every PumpInterval; that check is local and sends nothing.
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);
    private readonly ValheimClient _client;
    private readonly object _sync = new();
    private readonly List<string> _seen = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _safety = TimeSpan.FromSeconds(2);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _feed;
    private string? _lost;
    private bool _disposed;
    /// <summary>
    /// Interval of a STATE query next to the pushes. A push can be lost (the transport's STATE and SUBSCRIBE_STATE reads
    /// can consume one) and a closed connection sends nothing; this query notices both. It bounds their delay only.
    /// </summary>
    public TimeSpan SafetyInterval
    {
        get => _safety;
        init => _safety = value > TimeSpan.Zero || value == Timeout.InfiniteTimeSpan ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
    /// <summary>The latest state received, or null before the first answer.</summary>
    public string? LastState { get { lock (_sync) return _seen.Count == 0 ? null : _seen[^1]; } }

    /// <summary>Takes ownership of a connected client that nothing else uses.</summary>
    public StateWait(ValheimClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.OnStateChanged += Observe;
    }
    public static StateWait Connect(string host, int port)
    {
        var client = new ValheimClient(host, port);
        if (!client.Connect()) { client.Dispose(); throw new IOException("CLI state connection failed."); }
        return new StateWait(client);
    }

    /// <summary>
    /// Waits until the current state, or a later pushed one, is in <paramref name="targets"/> and returns it. A state in
    /// <paramref name="failures"/> or a lost connection ends the wait at once with <see cref="WaitFailedException"/>;
    /// expiry throws <see cref="WaitTimeoutException"/>. Names compare case-insensitively, as ValheimCLI's own wait does.
    /// </summary>
    public async Task<string> WaitAsync(IReadOnlyCollection<string> targets, TimeSpan timeout, IReadOnlyCollection<string>? failures = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0) throw new ArgumentException("Name at least one target state.", nameof(targets));
        WaitText.RequireTimeout(timeout);
        failures ??= [];
        string target = "ValheimCLI state " + string.Join(" or ", targets);
        var clock = Stopwatch.StartNew();
        int next = -1;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _feed ??= Task.Factory.StartNew(Feed, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            Task changed;
            lock (_sync)
            {
                // The current state counts, then every later one in order, so a brief failure state is not skipped.
                if (next < 0) next = Math.Max(0, _seen.Count - 1);
                for (; next < _seen.Count; next++)
                {
                    string state = _seen[next];
                    if (failures.Contains(state, StringComparer.OrdinalIgnoreCase))
                        throw new WaitFailedException(target, "the game reached failure state " + state, clock.Elapsed, "state " + state);
                    if (targets.Contains(state, StringComparer.OrdinalIgnoreCase)) return state;
                }
                if (_lost != null) throw new WaitFailedException(target, _lost, clock.Elapsed, Seen());
                changed = _changed.Task;
            }
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) { lock (_sync) throw new WaitTimeoutException(target, clock.Elapsed, Seen()); }
            try { await changed.WaitAsync(remaining, cancellation).ConfigureAwait(false); }
            catch (TimeoutException) { /* Reported with the last state on the next pass. */ }
        }
    }
    private string Seen() => _seen.Count == 0 ? "no state received" : "state " + _seen[^1];

    private void Feed()
    {
        try
        {
            // Asked before subscribing, so no push can interleave with the answer; a push reports changes only.
            // A change between this answer and the subscription is caught by the safety query.
            Observe(_client.GetState());
            if (!_client.IsConnected) throw new IOException("the CLI state connection closed");
            if (!_client.SubscribeToStateChanges()) throw new InvalidOperationException("ValheimCLI did not confirm the state subscription");
            var check = Stopwatch.StartNew();
            while (!_stop.IsCancellationRequested)
            {
                _client.PollStateChanges();
                if (_safety != Timeout.InfiniteTimeSpan && check.Elapsed >= _safety)
                {
                    string state = _client.GetState();
                    if (!_client.IsConnected) throw new IOException("the CLI state connection closed");
                    // With a subscription, Unknown can be a swallowed push rather than an answer; a real state change is pushed.
                    if (state != Unknown) Observe(state);
                    check.Restart();
                }
                _stop.Token.WaitHandle.WaitOne(PumpInterval);
            }
        }
        catch (Exception error)
        {
            if (_stop.IsCancellationRequested) return; // Dispose closed the connection under the feed.
            lock (_sync) { _lost = error.Message; Pulse(); }
        }
    }
    private void Observe(string state)
    {
        lock (_sync)
        {
            if (_seen.Count > 0 && string.Equals(_seen[^1], state, StringComparison.OrdinalIgnoreCase)) return;
            _seen.Add(state); Pulse();
        }
    }
    private void Pulse()
    {
        var old = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }
    public void Dispose()
    {
        Task? feed;
        lock (_sync) { if (_disposed) return; _disposed = true; feed = _feed; }
        _stop.Cancel();
        // The feed leaves within one pump interval unless a read blocks on a silent server; closing the client ends that read.
        try { feed?.Wait(PumpInterval * 5); } catch (AggregateException) { }
        _client.OnStateChanged -= Observe;
        _client.Dispose();
    }
}
