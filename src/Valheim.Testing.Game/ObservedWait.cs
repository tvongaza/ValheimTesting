namespace Valheim.Testing.Game;

/// <summary>
/// Re-observes a read-only source until it matches, for state that announces its change nowhere the runner can wait on
/// directly (a client's global keys, a synced config value). Between observations it waits at most the interval, or less
/// when <c>changed</c> (given the time to wait) completes first: for example a log line the change produces, through a
/// <see cref="LogWait"/> opened before the change. A missed or absent event only costs the interval.
/// </summary>
internal static class ObservedWait
{
    public static async Task<T> Until<T>(Func<T> observe, Func<T, bool> matches, Func<T, string?> fails, Func<T, string> describe, string target,
        TimeSpan timeout, TimeSpan interval, Func<TimeSpan, CancellationToken, Task>? changed, CancellationToken cancellation)
    {
        WaitText.RequireTimeout(timeout);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "Give a positive interval.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            T value = observe();
            if (fails(value) is string reason) throw new WaitFailedException(target, reason, clock.Elapsed, describe(value));
            if (matches(value)) return value;
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new WaitTimeoutException(target, clock.Elapsed, describe(value));
            var wait = remaining < interval ? remaining : interval;
            if (changed == null) { await Task.Delay(wait, cancellation).ConfigureAwait(false); continue; }
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            bounded.CancelAfter(wait);
            // The event's expiry is only the end of this interval: observe again either way.
            try { await changed(wait, bounded.Token).WaitAsync(wait, cancellation).ConfigureAwait(false); }
            catch (Exception error) when (!cancellation.IsCancellationRequested && error is TimeoutException or OperationCanceledException) { }
        }
    }
}
