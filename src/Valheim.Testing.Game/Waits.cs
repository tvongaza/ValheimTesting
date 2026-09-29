using System.Diagnostics;
using System.Globalization;

namespace Valheim.Testing.Game;

// Waits block on an event (a log line, a process exit, a pushed game state) under an explicit deadline.
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

public static class ProcessWait
{
    /// <summary>
    /// Waits for the process to exit and returns its exit code. Expiry throws <see cref="WaitTimeoutException"/> and leaves
    /// the process running; stopping it is the owner's decision. <paramref name="lastSeen"/> supplies context for the report,
    /// for example a <see cref="LogWait"/>'s last line.
    /// </summary>
    public static async Task<int> ForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellation = default, Func<string?>? lastSeen = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        WaitText.RequireTimeout(timeout);
        string target = "exit of process " + process.Id.ToString(CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new WaitTimeoutException(target, clock.Elapsed, lastSeen?.Invoke()); }
        return process.ExitCode;
    }
}
