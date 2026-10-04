using System.Diagnostics;
using System.Globalization;

namespace Valheim.Testing.Game;

// Waits block on an event (a log line, a process exit, a pushed game state) or re-observe a source (ObservedWait) under an explicit deadline.
// Expiry and early failure both say what was awaited, how long it ran and the last thing seen, so a failed
// startup explains itself without being rerun.
public sealed class WaitTimeoutException(string target, TimeSpan elapsed, string? lastSeen)
    : TimeoutException($"Timed out after {WaitText.Seconds(elapsed)} waiting for {target}; last seen: {lastSeen ?? "nothing"}.")
{
    public string Target { get; } = target;
    public TimeSpan Elapsed { get; } = elapsed;
    public string? LastSeen { get; } = lastSeen;
}
/// <param name="context">Lines seen just before <paramref name="lastSeen"/>, oldest first, for example the log lines leading to a failure line.</param>
public sealed class WaitFailedException(string target, string reason, TimeSpan elapsed, string? lastSeen, IReadOnlyList<string>? context = null)
    : InvalidOperationException($"Stopped waiting for {target} after {WaitText.Seconds(elapsed)}: {reason}; last seen: {lastSeen ?? "nothing"}." + WaitText.Context(context))
{
    public string Target { get; } = target;
    public string Reason { get; } = reason;
    public TimeSpan Elapsed { get; } = elapsed;
    public string? LastSeen { get; } = lastSeen;
    /// <summary>Lines seen just before <see cref="LastSeen"/>, oldest first; empty when the wait kept none.</summary>
    public IReadOnlyList<string> Context { get; } = context ?? [];
}
internal static class WaitText
{
    // No default and no infinite wait: every caller states how long the event may take.
    public static void RequireTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "A wait needs an explicit, positive timeout.");
    }
    public static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
    public static string Context(IReadOnlyList<string>? lines) =>
        lines is not { Count: > 0 } ? "" : " Lines before it:" + string.Concat(lines.Select(line => Environment.NewLine + "  " + line));
}

/// <summary>How <see cref="ExitRace"/> ended: the work finished first, the process exited first, or the deadline passed (or the caller cancelled: check the token).</summary>
internal enum RaceEnd { Completed, Exited, Expired }

/// <summary>
/// The one race of a piece of work against an owned process's exit and a deadline: an owned server's startup stages, an
/// owned client's launch, a local process run, an ssh forward's readiness. A process that is gone ends the race at once
/// instead of at the deadline. The loser is not cancelled here: the caller abandons it, because only it knows what the
/// work is blocked on.
/// </summary>
internal static class ExitRace
{
    /// <summary>
    /// Waits for whichever of <paramref name="work"/>, <paramref name="exited"/> and the deadline comes first. The deadline
    /// is <paramref name="deadline"/> on <paramref name="clock"/>. A cancelled <paramref name="cancellation"/> ends the race
    /// as <see cref="RaceEnd.Expired"/>.
    /// </summary>
    public static async Task<RaceEnd> RunAsync(Task work, Task exited, Stopwatch clock, TimeSpan deadline, CancellationToken cancellation)
    {
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var first = await Task.WhenAny(work, exited, Elapsed(clock, deadline, expiry.Token)).ConfigureAwait(false);
        expiry.Cancel();
        return first == work ? RaceEnd.Completed : first == exited ? RaceEnd.Exited : RaceEnd.Expired;
    }

    /// <summary>An <see cref="ObservedWait"/> pause that ends early when the process exits, so the next observation sees the exit at once.</summary>
    public static Func<TimeSpan, CancellationToken, Task> Pause(Task exited) => (wait, cancellation) => Task.WhenAny(Task.Delay(wait, cancellation), exited);

    /// <summary>
    /// Completes once <paramref name="clock"/> has passed <paramref name="deadline"/>. Timers can fire slightly before a
    /// Stopwatch agrees (Windows' coarse timer: a 1 s delay measured 0.9999 s), so an early wake-up waits out the rest
    /// instead of reporting a timeout before the deadline. No polling: at most one more short delay.
    /// </summary>
    private static async Task Elapsed(Stopwatch clock, TimeSpan deadline, CancellationToken cancellation)
    {
        for (var left = deadline - clock.Elapsed; left > TimeSpan.Zero; left = deadline - clock.Elapsed)
            await Task.Delay(left < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : left, cancellation).ConfigureAwait(false);
    }
}
