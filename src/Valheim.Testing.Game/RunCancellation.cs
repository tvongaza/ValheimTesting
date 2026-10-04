using System.Runtime.InteropServices;

namespace Valheim.Testing.Game;

/// <summary>
/// Cancels a console-owned setup or run on Ctrl+C or SIGTERM. Dispose after cleanup so another run in the same
/// process does not inherit the handler. Cancellation never replaces the uncancelled, bounded cleanup path.
/// </summary>
public sealed class RunCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();
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

    /// <summary>Cancel when another owned resource, such as an account lease, is lost.</summary>
    public void Cancel() => _source.Cancel();

    private void SignalCancel()
    {
        try { _source.Cancel(); }
        catch (ObjectDisposedException) { /* A queued signal raced disposal after its handler was removed. */ }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= _onCancel;
        _sigterm?.Dispose();
        _source.Dispose();
    }
}
