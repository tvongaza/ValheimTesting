namespace Valheim.Testing.Game;

/// <summary>
/// The toolkit's one polling wait: re-observes a read-only source until it matches, for state that announces its change
/// nowhere the runner can wait on (an adapter's readiness flag, a client's global keys, a synced config value). When the
/// change announces itself, wait for that instead: <see cref="LogWait"/> for a log line, <see cref="StateWait"/> for a
/// ValheimCLI game state.
/// <para>
/// Each round observes once, then fails at once when <c>fails</c> names a reason (<see cref="WaitFailedException"/>),
/// returns when <c>matches</c> holds, and otherwise waits at most <c>interval</c> (less when the deadline is nearer) before
/// observing again. The timeout is required and finite; expiry throws <see cref="WaitTimeoutException"/> with
/// <c>describe</c> of the last observation. The cancellation token ends the wait at once, including during the pause.
/// <c>observe</c> must be read-only: never put a gameplay action in it.
/// </para>
/// </summary>
public static class ObservedWait
{
    /// <summary>
    /// Waits, without blocking a thread, until <paramref name="observe"/> matches; see <see cref="ObservedWait"/>. With
    /// <paramref name="changed"/>, each pause ends early when the event it awaits completes, for example a log line the
    /// change produces, through a <see cref="LogWait"/> opened before the change. It gets the pause's length and a token
    /// cancelled at its end; its expiry or failure only ends the pause, so a missed or absent event costs one interval.
    /// </summary>
    public static async Task<T> UntilAsync<T>(string target, Func<T> observe, Func<T, bool> matches, TimeSpan timeout, TimeSpan interval,
        CancellationToken cancellation = default, Func<T, string?>? fails = null, Func<T, string>? describe = null,
        Func<TimeSpan, CancellationToken, Task>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(observe);
        return (await Run(target, (_, _) => new ValueTask<T>(observe()), matches, timeout, interval, cancellation, fails, describe,
            changed == null ? Delay : EndsEarlyOn(changed)).ConfigureAwait(false)).Value;
    }

    /// <summary>Blocks until <paramref name="observe"/> matches, observing on the calling thread; see <see cref="ObservedWait"/>.</summary>
    public static T Until<T>(string target, Func<T> observe, Func<T, bool> matches, TimeSpan timeout, TimeSpan interval,
        CancellationToken cancellation = default, Func<T, string?>? fails = null, Func<T, string>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(observe);
        return RunBlocking(target, _ => observe(), matches, timeout, interval, cancellation, fails, describe).Value;
    }

    /// <summary>The value that matched and how long the wait took.</summary>
    internal readonly record struct Observed<T>(T Value, TimeSpan Elapsed);

    /// <summary>
    /// <see cref="Until"/> for callers in this package that need the elapsed time, the time left when they observe, or a
    /// blocking change signal. <paramref name="changed"/> must itself return within the time it gets, or when the token
    /// is cancelled (as <see cref="SemaphoreSlim.Wait(TimeSpan, CancellationToken)"/> does).
    /// </summary>
    internal static Observed<T> RunBlocking<T>(string target, Func<TimeSpan, T> observe, Func<T, bool> matches, TimeSpan timeout, TimeSpan interval,
        CancellationToken cancellation, Func<T, string?>? fails = null, Func<T, string>? describe = null, Action<TimeSpan, CancellationToken>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(observe);
        // Every step completes synchronously here (the observation is synchronous and the pause blocks), so the shared
        // loop finishes before Run returns, on this thread.
        var run = Run(target, (left, _) => new ValueTask<T>(observe(left)), matches, timeout, interval, cancellation, fails, describe, (wait, token) =>
        {
            if (changed != null) changed(wait, token);
            else token.WaitHandle.WaitOne(wait);
            return Task.CompletedTask;
        });
        return run.GetAwaiter().GetResult();
    }

    /// <summary>
    /// The one loop. <paramref name="observe"/> gets the time left before the deadline and the caller's token.
    /// <paramref name="pause"/> gets the pause's length and the caller's token, and returns within that length or on
    /// cancellation (throwing <see cref="OperationCanceledException"/> then is fine).
    /// </summary>
    internal static async Task<Observed<T>> Run<T>(string target, Func<TimeSpan, CancellationToken, ValueTask<T>> observe, Func<T, bool> matches,
        TimeSpan timeout, TimeSpan interval, CancellationToken cancellation, Func<T, string?>? fails, Func<T, string>? describe,
        Func<TimeSpan, CancellationToken, Task> pause)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(matches);
        WaitText.RequireTimeout(timeout);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "Give a positive interval.");
        describe ??= value => value?.ToString() ?? "nothing";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            // A value observed is judged even if cancellation arrives meanwhile: a match may own a resource (a connected actor).
            T value = await observe(timeout - clock.Elapsed, cancellation).ConfigureAwait(false);
            if (fails?.Invoke(value) is string reason) throw new WaitFailedException(target, reason, clock.Elapsed, describe(value));
            if (matches(value)) return new(value, clock.Elapsed);
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new WaitTimeoutException(target, clock.Elapsed, describe(value));
            await pause(remaining < interval ? remaining : interval, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>A pause that ends early when <paramref name="changed"/> completes; its expiry or failure only ends the pause.</summary>
    internal static Func<TimeSpan, CancellationToken, Task> EndsEarlyOn(Func<TimeSpan, CancellationToken, Task> changed) => async (wait, cancellation) =>
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bounded.CancelAfter(wait);
        try { await changed(wait, bounded.Token).WaitAsync(wait, cancellation).ConfigureAwait(false); }
        catch (Exception error) when (!cancellation.IsCancellationRequested && error is TimeoutException or OperationCanceledException) { }
    };

    internal static Task Delay(TimeSpan wait, CancellationToken cancellation) => Task.Delay(wait, cancellation);
}
