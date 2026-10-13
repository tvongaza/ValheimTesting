using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>How a run ended, in the one vocabulary its journal's <c>run-ended</c> entry uses (#667).</summary>
internal enum RunOutcome
{
    /// <summary>Its result passed.</summary>
    Passed,
    /// <summary>Its result failed, a refusal or preparation failure after it journalled anything included.</summary>
    Failed,
    /// <summary>A host operation's outcome could not be established: neither a pass nor a failure.</summary>
    Unknown,
    /// <summary>Recorded by <c>env recover</c> for a run whose runner is proven gone and that left nothing.</summary>
    Recovered,
    /// <summary>Recorded by <c>env teardown --machine-gone</c> for a run whose machine is gone for good.</summary>
    MachineGone,
}

/// <summary>
/// What a run's caller reports at its end: the outcome, whether its own cleanup was proven, and, when the user gave up on that
/// cleanup, why. Facts only; <see cref="RunFinish"/> composes the entry.
/// </summary>
internal sealed record RunEnd(RunOutcome Outcome, bool CleanupProven, string? Abandoned = null)
{
    /// <summary>A runner's end from its result: passed, unknown (an outcome no host could establish) or failed, and its Cleanup phase.</summary>
    public static RunEnd Of(ScenarioReport report, bool unknownOutcome, string? abandoned) =>
        new(report.Passed ? RunOutcome.Passed : unknownOutcome ? RunOutcome.Unknown : RunOutcome.Failed, report.CleanupVerified, abandoned);
}

/// <summary>
/// The one owner of a run's last journal entry (#667): every command and library path that ends a run (start, server-load and
/// its A/B and phases, sessions and campaigns, a failed preparation, recovery and a gone machine) reports a <see cref="RunEnd"/>
/// here and never builds the entry itself. A run's own flow ends it in every journal that flow wrote for it
/// (<see cref="RunJournal.RunScope"/>, so no caller keeps a flag), exactly once; a run that journalled nothing gets no entry.
/// A cleanup the user abandoned is recorded as <c>cleanup-abandoned</c> instead, which leaves the run for <c>env recover</c>:
/// the runner never journals an end it did not reach.
/// </summary>
internal static class RunFinish
{
    // The user asked to get out: an abandoned cleanup's last entry waits no longer than this per journal.
    private static readonly TimeSpan AbandonedWrite = TimeSpan.FromSeconds(15);
    // A journal on this machine is written in process; its place carries no timeout of its own.
    private static readonly TimeSpan DefaultWrite = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Ends <paramref name="run"/> in every journal its flow wrote it to, once (a second call writes nothing). Each write is best
    /// effort: a journal that cannot take it is named on stderr, and the result is false. True when every entry was written,
    /// including when the run journalled nothing.
    /// </summary>
    public static async Task<bool> EndAsync(RunJournal.RunScope run, RunEnd end)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(end);
        if (!run.TryEnd()) return true;
        var entry = Entry(end);
        bool whole = true;
        foreach (var place in run.Places)
        {
            var timeout = place.Timeout > TimeSpan.Zero ? place.Timeout : DefaultWrite;
            if (end.Abandoned != null && timeout > AbandonedWrite) timeout = AbandonedWrite;
            try { await place.Write("run", entry, timeout).ConfigureAwait(false); }
            catch (Exception error)
            {
                whole = false;
                Console.Error.WriteLine($"Warning: could not journal run {run.RunId}'s {entry.Kind} in {place.Key}: {error.Message}");
            }
        }
        return whole;
    }

    /// <summary>
    /// Records the end of a run whose runner is gone (<see cref="RunOutcome.Recovered"/>, <see cref="RunOutcome.MachineGone"/>) on
    /// <paramref name="host"/>, as <see cref="RunRecovery.Actor"/>, so the recovering process never reads as the run's runner.
    /// Throws when the host does not confirm the write.
    /// </summary>
    public static Task RecordForGoneRunnerAsync(RunJournal journal, IGameHost host, string directory, RunEnd end, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (end.Outcome is not (RunOutcome.Recovered or RunOutcome.MachineGone))
            throw new ArgumentException("Only a recovery or a gone machine records another runner's end; a run's own flow ends it with EndAsync.", nameof(end));
        return journal.AppendAsync(host, directory, RunRecovery.Actor, Entry(end), timeout, cancellation);
    }

    // The one composition of a run's last entry: its state word and the one cleanup rule (proven, and not abandoned).
    private static JournalEntry Entry(RunEnd end) => end.Abandoned is { } why
        ? JournalEntry.Of(JournalEntry.CleanupAbandoned, ("reason", why))
        : JournalEntry.Of(JournalEntry.RunEnded, ("state", State(end.Outcome)), ("cleanupVerified", end.CleanupProven ? "true" : "false"));

    internal static string State(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Passed => "passed",
        RunOutcome.Failed => "failed",
        RunOutcome.Unknown => "unknown",
        RunOutcome.Recovered => "recovered: runner gone",
        RunOutcome.MachineGone => "abandoned: its machine is gone",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}
