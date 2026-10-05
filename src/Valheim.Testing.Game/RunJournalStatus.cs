using System.Globalization;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>Where a journalled run stands, as <c>valheim-test env status</c> reports it.</summary>
internal enum JournalRunState
{
    /// <summary>Its runner still runs on this machine: the run is going, nothing of it is touched.</summary>
    Live,
    /// <summary>It never journalled its end and its runner cannot be checked from here (another machine, or not journalled).</summary>
    Unknown,
    /// <summary>Something it left cannot be proven gone or its own (a process that matches only in part, a host that cannot tell).</summary>
    Unrecoverable,
    /// <summary>It left copies, characters, processes, leases or locks that are provably its own.</summary>
    Recoverable,
    /// <summary>It ended and kept copies or leases on purpose (<c>VALHEIM_TESTING_KEEP_RUNTIME</c>, a server that may still run).</summary>
    Kept,
    /// <summary>It left nothing.</summary>
    Ended,
}

/// <summary>Something a run journalled and never journalled as gone: a copy, character, process, lease or lock on one host.</summary>
internal sealed record JournalItem(string Kind, string Host, string Actor, string What, string Status, DateTime SinceUtc)
{
    /// <summary>Kept on purpose by a run that ended.</summary>
    public bool Kept { get; init; }
    /// <summary>Cannot be proven gone or the run's own.</summary>
    public bool Unrecoverable { get; init; }
}

internal sealed record JournalRunStatus(string Run, JournalRunState State, string Reason, DateTime FirstUtc, DateTime LastUtc,
    IReadOnlyList<string> Hosts, string? Runner, IReadOnlyList<JournalItem> Items);

/// <summary>One host's journal as read: where, how many runs, and why it (or some of it) could not be read.</summary>
internal sealed record JournalHostStatus(string Name, string Journal, int Runs, string? Error);

internal sealed record JournalStatusReport(IReadOnlyList<JournalHostStatus> Hosts, IReadOnlyList<JournalRunStatus> Runs)
{
    /// <summary>Every host's journal was read whole and every run left nothing.</summary>
    public bool Clean => Hosts.All(host => host.Error == null) && Runs.All(run => run.State == JournalRunState.Ended);
}

/// <summary>
/// Reads every host's run journal (#257 step 4) and says, per run, whether it is going, over with nothing left, or left
/// something: each copy, character, process, Steam lease and host lock it journalled and never journalled as gone, checked
/// against the host where that can be read (a process by <see cref="HostProcessProbe"/>, a lock by its claimant). Changes
/// nothing.
/// </summary>
internal static class RunJournalStatus
{
    public static async Task<JournalStatusReport> InspectAsync(IReadOnlyDictionary<string, HostProfile> hosts, Func<string, IGameHost> hostFactory,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        // Every host at once: one that cannot be reached does not hold up the others.
        var reads = await Task.WhenAll(hosts.OrderBy(host => host.Key, StringComparer.Ordinal).Select(async pair =>
        {
            var (name, profile) = pair;
            string journal = "";
            try
            {
                journal = RunJournal.DirectoryFor(profile);
                var host = hostFactory(name);
                var (records, unreadable) = await RunJournal.ReadAllAsync(host, journal, timeout, cancellation).ConfigureAwait(false);
                var status = new JournalHostStatus(name, journal, records.Select(record => record.Run).Distinct(StringComparer.Ordinal).Count(),
                    unreadable == 0 ? null : $"{unreadable} journal line{(unreadable == 1 ? "" : "s")} could not be read, so what they record is unknown.");
                return (Status: status, Read: ((string Host, IGameHost Connection, IReadOnlyList<JournalRecord> Records)?)(name, host, records));
            }
            catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or ArgumentException or PlatformNotSupportedException
                                              or InvalidOperationException)
            {
                return (Status: new JournalHostStatus(name, journal, 0, error.Message), Read: null);
            }
        })).ConfigureAwait(false);
        var hostStatus = reads.Select(entry => entry.Status).ToList();
        var read = reads.Where(entry => entry.Read != null).Select(entry => entry.Read!.Value).ToList();

        // What each run left, host by host, from its own entries in the order written.
        var pending = new Dictionary<string, List<Pending>>(StringComparer.Ordinal);
        var ended = new Dictionary<string, (string State, bool Cleaned)>(StringComparer.Ordinal);
        foreach (var (hostName, connection, records) in read)
            foreach (var run in records.GroupBy(record => record.Run, StringComparer.Ordinal))
            {
                var left = new Dictionary<string, Pending>(StringComparer.Ordinal);
                foreach (var record in run.OrderBy(record => record.Utc))
                {
                    var fields = record.Entry.Fields;
                    string Field(string key) => fields.TryGetValue(key, out string? value) ? value : "";
                    void Open(string key, string kind, string what, string status, bool kept = false) =>
                        left[key] = new(kind, hostName, connection, record.Actor, what, status, kept, left.TryGetValue(key, out var was) ? was.SinceUtc : record.Utc, fields);
                    switch (record.Entry.Kind)
                    {
                        case JournalEntry.CopyIntended: Open("copy " + Field("runtime"), "copy", Field("runtime"), "copy started, never finished"); break;
                        case JournalEntry.CopyDone: Open("copy " + Field("runtime"), "copy", Field("runtime"), "copied, not retired"); break;
                        case JournalEntry.CopyKept: Open("copy " + Field("runtime"), "copy", Field("runtime"), "kept: " + Field("why"), kept: true); break;
                        case JournalEntry.CopyRetired: left.Remove("copy " + Field("runtime")); break;
                        case JournalEntry.CharacterIntended:
                            Open($"character {record.Actor} {Field("fileName")}", "character", $"{Field("fileName")} in {Field("characters")}", "staging started, never finished"); break;
                        case JournalEntry.CharacterDone:
                            if (left.TryGetValue($"character {record.Actor} {Field("fileName")}", out var staged))
                                left[$"character {record.Actor} {Field("fileName")}"] = staged with { Status = "staged, not retired" };
                            break;
                        case JournalEntry.CharacterRetired: left.Remove($"character {record.Actor} {Field("fileName")}"); break;
                        case JournalEntry.ProcessIntended:
                        {
                            string directory = Field("bootDirectory") is { Length: > 0 } boot ? boot : Field("launchDirectory");
                            Open($"launch {record.Actor} {directory}", "launch", directory, "launch started before its process was journalled; see its pid file");
                            break;
                        }
                        case JournalEntry.ProcessStarted:
                        {
                            string directory = Field("bootDirectory") is { Length: > 0 } boot ? boot : Field("launchDirectory");
                            left.Remove($"launch {record.Actor} {directory}");
                            Open($"process {Field("pid")} {Field("startIdentity")}", "process", $"{Field("pid")} (started {Field("startIdentity")})", "");
                            break;
                        }
                        case JournalEntry.LeaseHeld: Open($"lease {Field("account")} {Field("owner")}", "lease", $"Steam account {Field("account")} ({Field("pool")})", "held"); break;
                        case JournalEntry.LeaseKept: Open($"lease {Field("account")} {Field("owner")}", "lease", $"Steam account {Field("account")} ({Field("pool")})", "kept: its client may still run", kept: true); break;
                        case JournalEntry.LeaseReleased: left.Remove($"lease {Field("account")} {Field("owner")}"); break;
                        case JournalEntry.LockHeld: Open($"lock {Field("lock")} {Field("claimant")}", "lock", Field("lock"), "held"); break;
                        case JournalEntry.LockReleased: left.Remove($"lock {Field("lock")} {Field("claimant")}"); break;
                        case JournalEntry.RunEnded: ended[run.Key] = (Field("state"), Field("cleanupVerified") == "true"); break;
                    }
                }
                if (!pending.TryGetValue(run.Key, out var list)) pending[run.Key] = list = [];
                list.AddRange(left.Values);
            }

        // The host's word on each process and lock still open: one process check per host, one lock check per lock.
        var probed = new Dictionary<(string Host, int Pid, string Start), ProbedProcess>();
        var probeFailed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in pending.Values.SelectMany(list => list).Where(item => item.Kind == "process").GroupBy(item => item.Host, StringComparer.Ordinal))
        {
            var processes = group.Select(item => (Pid: int.TryParse(item.Fields.GetValueOrDefault("pid"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ? pid : 0,
                    Start: item.Fields.GetValueOrDefault("startIdentity") ?? ""))
                .Where(process => process.Pid > 0 && process.Start.All(char.IsAsciiDigit)).Distinct().ToList();
            try
            {
                foreach (var (asked, process) in await HostProcessProbe.ProbeAsync(group.First().Connection, processes, timeout, cancellation).ConfigureAwait(false))
                    probed[(group.Key, asked.Pid, asked.StartIdentity)] = process;
            }
            catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or InvalidOperationException) { probeFailed[group.Key] = error.Message; }
        }

        var runs = new List<JournalRunStatus>();
        foreach (var run in read.SelectMany(host => host.Records.Select(record => (host.Host, Record: record))).GroupBy(entry => entry.Record.Run, StringComparer.Ordinal))
        {
            var ordered = run.OrderBy(entry => entry.Record.Utc).ToList();
            var runner = ordered.LastOrDefault(entry => entry.Record.Runner != null).Record?.Runner;
            bool isEnded = ended.TryGetValue(run.Key, out var end);
            var items = new List<JournalItem>();
            foreach (var item in pending.GetValueOrDefault(run.Key) ?? [])
            {
                var judged = await JudgeAsync(item, isEnded && end.Cleaned, probed, probeFailed, timeout, cancellation).ConfigureAwait(false);
                if (judged != null) items.Add(judged);
            }
            var (state, reason) = Verdict(isEnded ? end : null, runner, items);
            runs.Add(new(run.Key, state, reason, ordered[0].Record.Utc, ordered[^1].Record.Utc,
                run.Select(entry => entry.Host).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), runner?.ToString(),
                items.OrderBy(item => item.Host, StringComparer.Ordinal).ThenBy(item => item.SinceUtc).ToList()));
        }
        return new(hostStatus, runs.OrderBy(run => run.FirstUtc).ToList());
    }

    private sealed record Pending(string Kind, string Host, IGameHost Connection, string Actor, string What, string Status, bool Kept, DateTime SinceUtc,
        IReadOnlyDictionary<string, string> Fields);

    // One open entry as the host now shows it: null when it is provably gone after all (a process that exited, a lock someone released).
    private static async Task<JournalItem?> JudgeAsync(Pending item, bool endedClean, Dictionary<(string Host, int Pid, string Start), ProbedProcess> probed,
        Dictionary<string, string> probeFailed, TimeSpan timeout, CancellationToken cancellation)
    {
        var judged = new JournalItem(item.Kind, item.Host, item.Actor, item.What, item.Status, item.SinceUtc) { Kept = item.Kept };
        switch (item.Kind)
        {
            case "launch":
                // A launch the run itself saw fail and cleaned up after is over; one an interrupted run left may have started a game.
                return endedClean ? null : judged with { Unrecoverable = true };
            case "process":
            {
                if (probeFailed.TryGetValue(item.Host, out string? why)) return judged with { Status = "cannot be checked: " + why, Unrecoverable = true };
                int.TryParse(item.Fields.GetValueOrDefault("pid"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid);
                if (!probed.TryGetValue((item.Host, pid, item.Fields.GetValueOrDefault("startIdentity") ?? ""), out var process)) return judged with { Status = "its journal entry names no readable process", Unrecoverable = true };
                string journalled = item.Fields.GetValueOrDefault("commandLineSha256") ?? "";
                return process.State switch
                {
                    ProbedState.Gone or ProbedState.Reused => null,
                    ProbedState.Unreadable => judged with { Status = "its state cannot be read on " + item.Host, Unrecoverable = true },
                    _ when journalled.Length == 0 => judged with { Status = "still runs; its command line was not journalled, so only its ID and start time match", Unrecoverable = true },
                    _ when process.CommandLineSha256 == null => judged with { Status = "still runs; its command line cannot be read, so only its ID and start time match", Unrecoverable = true },
                    _ when !string.Equals(process.CommandLineSha256, journalled, StringComparison.OrdinalIgnoreCase) =>
                        judged with { Status = "a process with its ID and start time runs another command line", Unrecoverable = true },
                    _ => judged with { Status = "still runs (ID, start time and command line match)" },
                };
            }
            case "lock":
            {
                string claimant = item.Fields.GetValueOrDefault("claimant") ?? "";
                try
                {
                    var held = await item.Connection.CheckLockAsync(item.What, claimant, timeout, cancellation).ConfigureAwait(false);
                    return held.State switch
                    {
                        HostLockState.Yours => judged with { Status = "held by this run (" + claimant + ")" },
                        HostLockState.Free or HostLockState.HeldByOther => null,
                        _ => judged with { Status = "cannot be checked: " + held.Detail, Unrecoverable = true },
                    };
                }
                catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or InvalidOperationException)
                {
                    return judged with { Status = "cannot be checked: " + error.Message, Unrecoverable = true };
                }
            }
            default: return judged;
        }
    }

    private static (JournalRunState State, string Reason) Verdict((string State, bool Cleaned)? end, JournalRunner? runner, IReadOnlyList<JournalItem> items)
    {
        string ending = end is { } e ? $"ended {e.State}" + (e.Cleaned ? "" : ", cleanup not verified") : "never journalled its end";
        if (end == null)
        {
            if (runner == null) return (JournalRunState.Unknown, ending + "; its runner was not journalled, so it cannot be proven over");
            if (!runner.OnThisMachine) return (JournalRunState.Unknown, $"{ending}; its runner ran on {runner.Machine}: check it there");
            if (runner.StillRuns()) return (JournalRunState.Live, "its runner still runs: " + runner);
            ending += "; its runner is gone";
        }
        if (items.Any(item => item.Unrecoverable)) return (JournalRunState.Unrecoverable, ending + "; something it left cannot be proven its own or gone");
        if (items.Any(item => !item.Kept)) return (JournalRunState.Recoverable, ending + "; it left what is provably its own");
        if (items.Count != 0) return (JournalRunState.Kept, ending + "; it kept copies or leases on purpose");
        return (JournalRunState.Ended, ending);
    }

    /// <summary>The report as <c>valheim-test env status</c> prints it: each host, then each run that left something, with what it left.</summary>
    public static void Write(JournalStatusReport report, TextWriter output, bool json)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { report.Hosts, report.Runs, report.Clean },
                new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            return;
        }
        foreach (var host in report.Hosts)
            output.WriteLine(host.Error == null ? $"host {host.Name}: journal {host.Journal}, {Count(host.Runs, "run")}"
                : host.Runs != 0 ? $"UNREADABLE lines on host {host.Name}: journal {host.Journal}, {Count(host.Runs, "run")}; {host.Error}"
                : $"UNREADABLE host {host.Name}: {host.Error} Its runs are unknown; none is reported as over.");
        foreach (var run in report.Runs.Where(run => run.State != JournalRunState.Ended))
        {
            output.WriteLine($"{run.State.ToString().ToUpperInvariant()} {run.Run} on {string.Join(", ", run.Hosts)}, {Time(run.FirstUtc)} to {Time(run.LastUtc)}: {run.Reason}");
            foreach (var item in run.Items)
                output.WriteLine($"  {item.Kind} {item.What} on {item.Host} ({item.Actor}, since {Time(item.SinceUtc)}): {(item.Unrecoverable ? "UNRECOVERABLE " : "")}{item.Status}");
        }
        output.WriteLine($"{Count(report.Runs.Count(run => run.State == JournalRunState.Ended), "run")} ended and left nothing. Nothing was changed.");
    }

    private static string Count(int count, string noun) => count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");
    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";
}
