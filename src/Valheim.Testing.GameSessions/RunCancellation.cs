using System.Runtime.InteropServices;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// Cancels a console-owned setup or run on Ctrl+C or SIGTERM. Dispose after cleanup so another run in the same
/// process does not inherit the handler. Cleanup never runs on the cancelled token: <see cref="BeginCleanup"/> gives it a
/// fresh one, bounded by a budget, which only a further signal during cleanup (the escape from a cleanup that hangs) or the
/// budget itself cancels; <see cref="Abandoned"/> then says why, and the run is left for <c>valheim-test env recover</c>.
/// Nothing is ever killed by the escape itself.
/// </summary>
public sealed class RunCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _cleanup;
    private int _signals;
    private string? _abandoned;

    /// <summary>How long cleanup may take once it begins, unless another signal ends it sooner.</summary>
    public static readonly TimeSpan CleanupBudget = TimeSpan.FromMinutes(5);
    private readonly ConsoleCancelEventHandler _onCancel;
    private readonly PosixSignalRegistration? _sigterm;

    public RunCancellation()
    {
        _onCancel = (_, eventArgs) => { eventArgs.Cancel = true; SignalCancel(); };
        Console.CancelKeyPress += _onCancel;
        try
        {
            if (!OperatingSystem.IsWindows())
                _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                    context => { context.Cancel = true; SignalCancel(); });
        }
        catch
        {
            Console.CancelKeyPress -= _onCancel;
            _source.Dispose();
            throw;
        }
    }

    /// <summary>The token to pass through preparation, launch and event waits.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Cancels the run from code, as Ctrl+C would.</summary>
    public void Cancel() => _source.Cancel();

    /// <summary>
    /// The token cleanup runs on, from now: never the run's (cancelled) token. A signal after cleanup began, when at least one
    /// came before it (a second Ctrl+C), or the end of <paramref name="budget"/> (<see cref="CleanupBudget"/> by default)
    /// cancels it. Calling it again returns the same token.
    /// </summary>
    public CancellationToken BeginCleanup(TimeSpan? budget = null)
    {
        lock (_sync)
        {
            if (_cleanup == null)
            {
                var limit = budget ?? CleanupBudget;
                _cleanup = new CancellationTokenSource(limit);
                _cleanup.Token.Register(() => { lock (_sync) _abandoned ??= $"cleanup did not finish within its budget of {WaitText.Seconds(limit)}"; });
            }
            return _cleanup.Token;
        }
    }

    /// <summary>Why cleanup was abandoned (a second signal, or its budget), or null while it was not.</summary>
    public string? Abandoned { get { lock (_sync) return _abandoned; } }

    // Each Ctrl+C or SIGTERM: the first cancels the run; one during cleanup, after an earlier one, abandons the cleanup.
    internal void SignalCancel()
    {
        CancellationTokenSource? abandon = null;
        lock (_sync)
        {
            _signals++;
            if (_cleanup != null && _signals >= 2 && !_cleanup.IsCancellationRequested)
            {
                _abandoned ??= "a second interrupt during cleanup";
                abandon = _cleanup;
            }
        }
        try { _source.Cancel(); abandon?.Cancel(); }
        catch (ObjectDisposedException) { /* A queued signal raced disposal after its handler was removed. */ }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= _onCancel;
        _sigterm?.Dispose();
        _source.Dispose();
        lock (_sync) _cleanup?.Dispose();
    }
}
