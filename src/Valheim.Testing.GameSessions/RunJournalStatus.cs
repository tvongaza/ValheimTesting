using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>Where a journalled run stands, as <c>valheim-test env status</c> reports it.</summary>
internal enum JournalRunState
{
    /// <summary>Its runner still runs, on this machine or on a Windows host of the inventory that is its machine: the run is going, nothing of it is touched.</summary>
    Live,
    /// <summary>It never journalled its end and its runner cannot be checked from here (a machine that is not a reachable Windows host of the inventory, or not journalled).</summary>
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
    /// <summary>Every field its entries journalled (later entries over earlier ones): what a recovery acts on.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
    /// <summary>Kept on purpose by a run that ended.</summary>
    public bool Kept { get; init; }
    /// <summary>Cannot be proven gone or the run's own.</summary>
    public bool Unrecoverable { get; init; }
}

internal sealed record JournalRunStatus(string Run, JournalRunState State, string Reason, DateTime FirstUtc, DateTime LastUtc,
    IReadOnlyList<string> Hosts, string? Runner, IReadOnlyList<JournalItem> Items)
{
    /// <summary>Launches never journalled as started whose pid file proves their process gone: a recovery journals them settled.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<JournalItem> SettledLaunches { get; init; } = [];
}

/// <summary>One host's journal as read: where, how many runs, and why it (or some of it) could not be read.</summary>
internal sealed record JournalHostStatus(string Name, string Journal, int Runs, string? Error);

internal sealed record JournalStatusReport(IReadOnlyList<JournalHostStatus> Hosts, IReadOnlyList<JournalRunStatus> Runs)
{
    /// <summary>Copies on this machine that no journal names (made before runs journalled them): removed only by name.</summary>
    public IReadOnlyList<OwnedCopy> Unjournalled { get; init; } = [];
    /// <summary>Every host's journal was read whole, every run left nothing and no unjournalled copy is left.</summary>
    public bool Clean => Hosts.All(host => host.Error == null) && Runs.All(run => run.State == JournalRunState.Ended) && Unjournalled.Count == 0;
}

/// <summary>
/// Reads every host's run journal (#257 step 4) and says, per run, whether it is going, over with nothing left, or left
/// something: each copy, character, process, Steam lease and host lock it journalled and never journalled as gone, checked
/// against the host where that can be read (a process by <see cref="HostProcessProbe"/>, a lock by its claimant). Changes
/// nothing.
/// </summary>
internal static class RunJournalStatus
{
    /// <param name="leaseHost">The inventory's lease host and <paramref name="leaseDirectory"/> its lease directory: where a lease journalled
    /// before leases named their directory is checked.</param>
    /// <param name="copyRoots">Directories on this machine searched for copies no journal names (<see cref="OwnedCopies.Find"/>).</param>
    public static async Task<JournalStatusReport> InspectAsync(IReadOnlyDictionary<string, HostProfile> hosts, Func<string, IGameHost> hostFactory,
        TimeSpan timeout, CancellationToken cancellation = default, string? leaseHost = null, string? leaseDirectory = null, IReadOnlyList<string>? copyRoots = null)
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
                var (records, unreadable) = await RunJournalOnHost.ReadAllAsync(host, journal, timeout, cancellation).ConfigureAwait(false);
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
        var recovered = new HashSet<string>(StringComparer.Ordinal);
        var abandoned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (hostName, connection, records) in read)
            foreach (var run in records.GroupBy(record => record.Run, StringComparer.Ordinal))
            {
                var left = new Dictionary<string, Pending>(StringComparer.Ordinal);
                foreach (var record in run.OrderBy(record => record.Utc))
                {
                    var fields = record.Entry.Fields;
                    string Field(string key) => fields.TryGetValue(key, out string? value) ? value : "";
                    void Open(string key, string kind, string what, string status, bool kept = false)
                    {
                        // Later entries add to what earlier ones journalled (copy-done names no staging; copy-intended does).
                        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (left.TryGetValue(key, out var was)) foreach (var (name, value) in was.Fields) merged[name] = value;
                        foreach (var (name, value) in fields) merged[name] = value;
                        left[key] = new(kind, hostName, connection, record.Actor, what, status, kept, was?.SinceUtc ?? record.Utc, merged);
                    }
                    switch (record.Entry.Kind)
                    {
                        case JournalEntry.CopyIntended: Open("copy " + Field("runtime"), "copy", Field("runtime"), "copy started, never finished"); break;
                        case JournalEntry.CopyDone: Open("copy " + Field("runtime"), "copy", Field("runtime"), "copied, not retired"); break;
                        case JournalEntry.CopyKept: Open("copy " + Field("runtime"), "copy", Field("runtime"), "kept: " + Field("why"), kept: true); break;
                        case JournalEntry.CopyRetired: left.Remove("copy " + Field("runtime")); break;
                        case JournalEntry.MacListsCaptured: Open("mac-lists " + Field("backup"), "mac-lists", Field("saveRoot"), "user access lists captured, not restored"); break;
                        case JournalEntry.MacListsRestored: left.Remove("mac-lists " + Field("backup")); break;
                        case JournalEntry.CharacterIntended:
                            Open($"character {Field("fileName")}", "character", $"{Field("fileName")} in {Field("characters")}", "staging started, never finished"); break;
                        case JournalEntry.CharacterDone:
                            if (left.TryGetValue($"character {Field("fileName")}", out var staged))
                            {
                                var characterFields = staged.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                                foreach (var (name, value) in fields) characterFields[name] = value;
                                characterFields["staged"] = "true";
                                left[$"character {Field("fileName")}"] = staged with { Status = "staged, not retired", Fields = characterFields };
                            }
                            break;
                        case JournalEntry.CharacterRetired: left.Remove($"character {Field("fileName")}"); break;
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
                        case JournalEntry.ProcessStopped: left.Remove($"process {Field("pid")} {Field("startIdentity")}"); break;
                        case JournalEntry.LaunchSettled: left.Remove($"launch {Field("actor")} {Field("directory")}"); break;
                        case JournalEntry.LeaseHeld: Open($"lease {Field("account")} {Field("owner")}", "lease", $"Steam account {Field("account")} ({Field("pool")})", "held"); break;
                        case JournalEntry.LeaseKept: Open($"lease {Field("account")} {Field("owner")}", "lease", $"Steam account {Field("account")} ({Field("pool")})", "kept: its client may still run", kept: true); break;
                        case JournalEntry.LeaseReleased: left.Remove($"lease {Field("account")} {Field("owner")}"); break;
                        case JournalEntry.LockHeld: Open($"lock {Field("lock")} {Field("claimant")}", "lock", Field("lock"), "held"); break;
                        case JournalEntry.LockReleased: left.Remove($"lock {Field("lock")} {Field("claimant")}"); break;
                        case JournalEntry.RunEnded: ended[run.Key] = (Field("state"), Field("cleanupVerified") == "true"); break;
                        case JournalEntry.RunRecovered: recovered.Add(run.Key); break;
                        case JournalEntry.CleanupAbandoned: abandoned[run.Key] = Field("reason"); break;
                    }
                }
                if (!pending.TryGetValue(run.Key, out var list)) pending[run.Key] = list = [];
                list.AddRange(left.Values);
            }

        // A launch left open (its process never journalled) by a run that did not end cleaned up: its pid file, read on its host,
        // names the process it started, if any. One read per host.
        // Every host at once, as the journals are read: one that cannot answer does not hold up the others.
        var pidFiles = new Dictionary<(string Host, string Directory), PidFile>();
        var pidReads = await Task.WhenAll(pending.Where(run => !(ended.TryGetValue(run.Key, out var end) && end.Cleaned)).SelectMany(run => run.Value)
            .Where(item => item.Kind == "launch" && item.What.Length != 0).GroupBy(item => item.Host, StringComparer.Ordinal).Select(async group =>
            {
                var directories = group.Select(item => item.What).Distinct(StringComparer.Ordinal).ToList();
                IReadOnlyList<PidFile> files;
                try { files = await ReadPidFilesAsync(group.First().Connection, directories, timeout, cancellation).ConfigureAwait(false); }
                catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or InvalidOperationException)
                {
                    files = directories.Select(_ => new PidFile(PidFileState.Unreadable, 0, null, error.Message)).ToList();
                }
                return (Host: group.Key, Directories: directories, Files: files);
            })).ConfigureAwait(false);
        foreach (var (host, directories, files) in pidReads)
            for (int i = 0; i < directories.Count; i++) pidFiles[(host, directories[i])] = files[i];

        // The host's word on each process and lock still open: one process check per host (the processes journalled, and those
        // pid files name), one lock check per lock.
        var probed = new Dictionary<(string Host, int Pid, string Start), ProbedProcess>();
        var probeFailed = new Dictionary<string, string>(StringComparer.Ordinal);
        var asked = pending.Values.SelectMany(list => list).Where(item => item.Kind == "process").Select(item => (item.Host, item.Connection,
                Pid: int.TryParse(item.Fields.GetValueOrDefault("pid"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ? pid : 0,
                Start: item.Fields.GetValueOrDefault("startIdentity") ?? ""))
            .Concat(pending.Values.SelectMany(list => list).Where(item => item.Kind == "launch")
                .Select(item => (item.Host, item.Connection, File: pidFiles.GetValueOrDefault((item.Host, item.What))))
                .Where(launch => launch.File is { State: PidFileState.Found })
                .Select(launch => (launch.Host, launch.Connection, Pid: launch.File!.Pid, Start: launch.File.StartIdentity ?? "")));
        foreach (var group in asked.GroupBy(process => process.Host, StringComparer.Ordinal))
        {
            var processes = group.Select(process => (process.Pid, process.Start))
                .Where(process => process.Pid > 0 && process.Start.All(char.IsAsciiDigit)).Distinct().ToList();
            try
            {
                foreach (var (question, process) in await HostProcessProbe.ProbeAsync(group.First().Connection, processes, timeout, cancellation).ConfigureAwait(false))
                    probed[(group.Key, question.Pid, question.StartIdentity)] = process;
            }
            catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or InvalidOperationException) { probeFailed[group.Key] = error.Message; }
        }

        // The run's own runner: a recovery journals as its own actor, and its process is not the run's.
        var runnerOf = read.SelectMany(host => host.Records).GroupBy(record => record.Run, StringComparer.Ordinal).ToDictionary(run => run.Key,
            run => run.OrderBy(record => record.Utc).LastOrDefault(record => record.Runner != null && record.Actor != RunRecovery.Actor)?.Runner, StringComparer.Ordinal);
        var remoteRunners = await RemoteRunnersAsync(read.Select(host => (host.Host, host.Connection)).ToList(),
            runnerOf.Where(run => !ended.ContainsKey(run.Key) && run.Value is { OnThisMachine: false })
                .ToDictionary(run => run.Key, run => run.Value!, StringComparer.Ordinal), timeout, cancellation).ConfigureAwait(false);

        var runs = new List<JournalRunStatus>();
        foreach (var run in read.SelectMany(host => host.Records.Select(record => (host.Host, Record: record))).GroupBy(entry => entry.Record.Run, StringComparer.Ordinal))
        {
            var ordered = run.OrderBy(entry => entry.Record.Utc).ToList();
            var runner = runnerOf[run.Key];
            bool isEnded = ended.TryGetValue(run.Key, out var end);
            var items = new List<JournalItem>();
            var settled = new List<JournalItem>();
            foreach (var item in pending.GetValueOrDefault(run.Key) ?? [])
            {
                var judged = await JudgeAsync(item, run.Key, isEnded && end.Cleaned, probed, probeFailed, pidFiles, timeout, cancellation,
                    item.Host == leaseHost && !string.IsNullOrEmpty(leaseDirectory) ? leaseDirectory : null).ConfigureAwait(false);
                if (judged != null) items.Add(judged);
                else if (item.Kind == "launch" && !(isEnded && end.Cleaned))
                    settled.Add(new JournalItem(item.Kind, item.Host, item.Actor, item.What, "its pid file's process is gone", item.SinceUtc) { Fields = item.Fields });
            }
            // TryGetValue, not GetValueOrDefault: a default tuple would read as "asked, and gone".
            var (state, reason) = Verdict(isEnded ? end : null, runner, items, remoteRunners.TryGetValue(run.Key, out var remote) ? remote : null);
            if (abandoned.TryGetValue(run.Key, out string? why)) reason += $"; its cleanup was abandoned ({why})";
            if (recovered.Contains(run.Key)) reason += "; recovered by env recover/teardown" + (items.Count == 0 ? "" : ", but not all of it");
            runs.Add(new(run.Key, state, reason, ordered[0].Record.Utc, ordered[^1].Record.Utc,
                run.Select(entry => entry.Host).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), runner?.ToString(),
                items.OrderBy(item => item.Host, StringComparer.Ordinal).ThenBy(item => item.SinceUtc).ToList()) { SettledLaunches = settled });
        }
        // Copies made before runs journalled them: every copy any journal names is the journal's to judge.
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var journalled = read.SelectMany(host => host.Records).Select(record => record.Entry.Fields.GetValueOrDefault("runtime")).OfType<string>()
            .Select(path => Path.TrimEndingDirectorySeparator(path)).ToHashSet(comparison);
        var roots = (copyRoots ?? []).Where(Directory.Exists).Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).Distinct(comparison).ToList();
        // A root inside another is already searched with it.
        roots = roots.Where(root => !roots.Any(other => other != root && root.StartsWith(other + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))).ToList();
        var unjournalled = roots
            .SelectMany(root => { try { return OwnedCopies.Find(root); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; } })
            .Where(copy => !journalled.Contains(Path.TrimEndingDirectorySeparator(copy.Path))).DistinctBy(copy => copy.Path, comparison).ToList();
        return new(hostStatus, runs.OrderBy(run => run.FirstUtc).ToList()) { Unjournalled = unjournalled };
    }

    private sealed record Pending(string Kind, string Host, IGameHost Connection, string Actor, string What, string Status, bool Kept, DateTime SinceUtc,
        IReadOnlyDictionary<string, string> Fields);

    // One open entry as the host now shows it: null when it is provably gone after all (a process that exited, a lock someone released).
    private static async Task<JournalItem?> JudgeAsync(Pending item, string run, bool endedClean, Dictionary<(string Host, int Pid, string Start), ProbedProcess> probed,
        Dictionary<string, string> probeFailed, Dictionary<(string Host, string Directory), PidFile> pidFiles, TimeSpan timeout, CancellationToken cancellation,
        string? inventoryLeaseDirectory)
    {
        var judged = new JournalItem(item.Kind, item.Host, item.Actor, item.What, item.Status, item.SinceUtc) { Kept = item.Kept, Fields = item.Fields };
        switch (item.Kind)
        {
            case "launch":
                // A launch the run itself saw fail and cleaned up after is over; one an interrupted run left may have started a game.
                return endedClean ? null : JudgeLaunch(item, judged, pidFiles, probed, probeFailed);
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
                    ProbedState.Starting => judged with { Status = "its launch wrapper still runs; the game command line is not yet available", Unrecoverable = true },
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
            case "lease":
            {
                // Checked on the lease host by its claim: held by this run's owner, or not (released, or another run's now). A lease
                // never lapses (#257), so one this run still holds stays until a recovery releases it.
                var fields = item.Fields;
                // A lease journalled before leases named their id and directory is looked up in the inventory's lease directory.
                bool withId = fields.ContainsKey("leaseId");
                string? directory = fields.GetValueOrDefault("directory") ?? inventoryLeaseDirectory;
                if (directory == null)
                    return judged with { Status = $"journalled without its lease directory, so it cannot be checked; check pool {fields.GetValueOrDefault("pool")} by hand",
                        Unrecoverable = true };
                try
                {
                    var pool = new SteamAccountPool { Pool = fields.GetValueOrDefault("pool") ?? "", LeaseDirectory = directory, Accounts = [new SteamPoolAccount { Name = fields.GetValueOrDefault("account") ?? "" }] };
                    var account = (await pool.ListAsync(item.Connection, timeout, cancellation).ConfigureAwait(false)).SingleOrDefault();
                    return account switch
                    {
                        { State: SteamAccountState.Held } when account.Holder == fields.GetValueOrDefault("owner") && !withId =>
                            judged with { Status = "held by this run, but journalled without its lease id, so env recover cannot release it; once its client is gone, " +
                                $"valheim-test env teardown --run {run} --machine-gone releases it", Unrecoverable = true },
                        { State: SteamAccountState.Held } when account.Holder == fields.GetValueOrDefault("owner") =>
                            judged with { Status = (item.Kept ? item.Status + "; " : "") + "held by this run until released" },
                        { State: SteamAccountState.Free } or { State: SteamAccountState.Held } => null,
                        _ => judged with { Status = "cannot be checked: " + (account?.Describe() ?? "the lease host named no such account"), Unrecoverable = true },
                    };
                }
                catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or ArgumentException or InvalidOperationException)
                {
                    return judged with { Status = "cannot be checked: " + error.Message, Unrecoverable = true };
                }
            }
            default: return judged;
        }
    }

    // A launch whose process was never journalled (#257): its pid file, which the launcher writes as the game starts, says which
    // process it started. Proven gone when no process has that ID and start identity now (an ID another process reused counts as
    // gone, and is never stopped); adopted as the run's process, which env recover stops by identity, only when that process
    // runs the command line the launch journalled. Anything else (no pid file, an unreadable one, a pid file without a start
    // identity naming a running process, a launch journalled without its command line, another command line) stays unrecoverable.
    private static JournalItem? JudgeLaunch(Pending item, JournalItem judged, Dictionary<(string Host, string Directory), PidFile> pidFiles,
        Dictionary<(string Host, int Pid, string Start), ProbedProcess> probed, Dictionary<string, string> probeFailed)
    {
        string where = $"its pid file in {item.What}";
        if (!pidFiles.TryGetValue((item.Host, item.What), out var file) || file.State == PidFileState.Unreadable)
            return judged with { Status = $"launch started before its process was journalled, and {where} cannot be read: {file?.Detail ?? "no directory was journalled"}", Unrecoverable = true };
        if (file.State == PidFileState.Missing)
            return judged with { Status = $"launch started before its process was journalled, and {where} does not exist, so a process it may have started cannot be named", Unrecoverable = true };
        if (file.State == PidFileState.Malformed)
            return judged with { Status = $"launch started before its process was journalled, and {where} names no process ID and start identity ({file.Detail})", Unrecoverable = true };
        string named = file.StartIdentity == null ? $"process {file.Pid}" : $"process {file.Pid} (started {file.StartIdentity})";
        if (probeFailed.TryGetValue(item.Host, out string? why)) return judged with { Status = $"{where} names {named}, which cannot be checked: {why}", Unrecoverable = true };
        if (!probed.TryGetValue((item.Host, file.Pid, file.StartIdentity ?? ""), out var process) || process.State == ProbedState.Unreadable)
            return judged with { Status = $"{where} names {named}, whose state cannot be read on {item.Host}", Unrecoverable = true };
        if (process.State is ProbedState.Gone or ProbedState.Reused) return null;
        if (process.State == ProbedState.Starting)
            return judged with { Status = $"{where} names {named}, whose launch wrapper still runs", Unrecoverable = true };
        if (file.StartIdentity == null)
            return judged with { Status = $"{where} names {named}, which runs, but no start identity, so it cannot be proven the launch's", Unrecoverable = true };
        string expected = item.Fields.GetValueOrDefault("expectedCommandLineSha256") ?? "";
        if (expected.Length == 0)
            return judged with { Status = $"{where} names {named}, which still runs; the launch journalled no command line, so only its ID and start time match", Unrecoverable = true };
        if (process.CommandLineSha256 == null)
            return judged with { Status = $"{where} names {named}, which still runs; its command line cannot be read, so only its ID and start time match", Unrecoverable = true };
        if (!string.Equals(process.CommandLineSha256, expected, StringComparison.OrdinalIgnoreCase))
            return judged with { Status = $"{where} names {named}, which runs another command line than the launch's", Unrecoverable = true };
        var fields = new Dictionary<string, string>(item.Fields, StringComparer.Ordinal)
        {
            ["pid"] = file.Pid.ToString(CultureInfo.InvariantCulture), ["startIdentity"] = file.StartIdentity, ["commandLineSha256"] = expected, ["adoptedFrom"] = item.What,
        };
        return judged with
        {
            Kind = "process", What = $"{file.Pid} (started {file.StartIdentity})", Fields = fields,
            Status = $"still runs; adopted from {where} (ID, start time and command line match)",
        };
    }

    internal enum PidFileState { Found, Missing, Unreadable, Malformed }

    /// <summary>A launch's pid file as its host read it: the process ID and, where the launcher records it (Windows), the start identity.</summary>
    internal sealed record PidFile(PidFileState State, int Pid, string? StartIdentity, string? Detail);

    /// <summary>Reads <c>&lt;directory&gt;/pid</c> for each directory on <paramref name="host"/>, in order. Changes nothing.</summary>
    internal static async Task<IReadOnlyList<PidFile>> ReadPidFilesAsync(IGameHost host, IReadOnlyList<string> directories, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsPidFiles : BashPidFiles,
            new Dictionary<string, string> { ["dirs"] = string.Join('\n', directories) }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Reading launch pid files on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-PIDFILE-END") == null)
            throw new HostOperationException($"The pid file check on {host.Name} did not finish", result);
        var found = new PidFile?[directories.Count];
        foreach (string line in result.Stdout.Split('\n'))
        {
            // VT-PIDFILE <index> missing|unreadable|file <base64>
            var parts = line.Trim().Split(' ');
            if (parts.Length < 3 || parts[0] != "VT-PIDFILE" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index >= directories.Count) continue;
            found[index] = parts[2] switch
            {
                "missing" => new(PidFileState.Missing, 0, null, null),
                "file" => Parse(parts.Length == 4 ? parts[3] : ""),
                _ => new(PidFileState.Unreadable, 0, null, "the host could not read it"),
            };
        }
        return found.Select(file => file ?? new PidFile(PidFileState.Unreadable, 0, null, "the host's reply did not name it")).ToList();

        static PidFile Parse(string encoded)
        {
            string text;
            try { text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim(); }
            catch (FormatException) { return new(PidFileState.Unreadable, 0, null, "the host's reply was not base64"); }
            var words = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length is 1 or 2 && int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0
                && (words.Length == 1 || (words[1].Length is > 0 and <= 20 && words[1].All(char.IsAsciiDigit))))
                return new(PidFileState.Found, pid, words.Length == 2 ? words[1] : null, null);
            return new(PidFileState.Malformed, 0, null, text.Length == 0 ? "it is empty" : $"it holds '{(text.Length > 40 ? text[..40] + "..." : text)}'");
        }
    }

    // Variables: dirs (one launch or boot directory per line). One line per directory, in order: VT-PIDFILE <index> missing,
    // unreadable, or file <base64 of its first 200 bytes>; then VT-PIDFILE-END. Reads only.
    internal static readonly string BashPidFiles = """
        i=0
        while IFS= read -r dir; do
          if [ ! -e "$dir/pid" ]; then echo "VT-PIDFILE $i missing"
          elif [ ! -f "$dir/pid" ] || [ ! -r "$dir/pid" ]; then echo "VT-PIDFILE $i unreadable"
          else echo "VT-PIDFILE $i file $(head -c 200 -- "$dir/pid" | base64 | tr -d '\n')"; fi
          i=$((i + 1))
        done <<< "$dirs"
        echo VT-PIDFILE-END
        """.ReplaceLineEndings("\n");
    // Shared for writing and deleting, so a launcher moving its file into place is never refused by this read.
    internal static readonly string WindowsPidFiles = """
        $i = 0
        foreach ($dir in ($dirs -split "`n")) {
            $file = Join-Path $dir 'pid'
            if (-not [IO.File]::Exists($file)) { 'VT-PIDFILE ' + $i + ' missing' }
            else {
                try {
                    $stream = [IO.File]::Open($file, 'Open', 'Read', 'ReadWrite, Delete')
                    try { $bytes = New-Object byte[] 200; $read = $stream.Read($bytes, 0, 200) } finally { $stream.Dispose() }
                    'VT-PIDFILE ' + $i + ' file ' + [Convert]::ToBase64String($bytes, 0, $read)
                } catch { 'VT-PIDFILE ' + $i + ' unreadable' }
            }
            $i++
        }
        'VT-PIDFILE-END'
        """.ReplaceLineEndings("\n");

    private static (JournalRunState State, string Reason) Verdict((string State, bool Cleaned)? end, JournalRunner? runner, IReadOnlyList<JournalItem> items,
        (string Host, bool StillRuns)? remote = null)
    {
        string ending = end is { } e ? $"ended {e.State}" + (e.Cleaned ? "" : ", cleanup not verified") : "never journalled its end";
        if (end == null)
        {
            if (runner == null) return (JournalRunState.Unknown, ending + "; its runner was not journalled, so it cannot be proven over");
            if (!runner.OnThisMachine)
            {
                if (remote is not { } asked) return (JournalRunState.Unknown, $"{ending}; its runner ran on {runner.Machine}: check it there");
                if (asked.StillRuns) return (JournalRunState.Live, $"its runner still runs, as host {asked.Host} reports: " + runner);
                ending += $"; its runner is gone, as host {asked.Host} reports";
            }
            else if (runner.StillRuns()) return (JournalRunState.Live, "its runner still runs: " + runner);
            else ending += "; its runner is gone";
        }
        if (items.Any(item => item.Unrecoverable)) return (JournalRunState.Unrecoverable, ending + "; something it left cannot be proven its own or gone");
        if (items.Any(item => !item.Kept)) return (JournalRunState.Recoverable, ending + "; it left what is provably its own");
        if (items.Count != 0) return (JournalRunState.Kept, ending + "; it kept copies or leases on purpose");
        return (JournalRunState.Ended, ending);
    }

    /// <summary>
    /// For each run whose runner ran on another machine that is one of the inventory's readable Windows hosts (a run on the
    /// station PC, its journal read from this Mac): whether that runner still runs, as that host reports by process ID and start
    /// time (a UTC file time, within the same 2 s as on this machine). The host is the runner's machine when its machine name
    /// (<c>[Environment]::MachineName</c>, which the runner journalled) is the runner's and no other host reports the same name;
    /// a runner on a third machine, on a name two hosts share, on a bash host, or on a host that cannot be asked is left out, and
    /// its run stays unknown. One machine-name read per Windows host and one process check per host asked, read only.
    /// </summary>
    private static async Task<Dictionary<string, (string Host, bool StillRuns)>> RemoteRunnersAsync(
        IReadOnlyList<(string Host, IGameHost Connection)> hosts, Dictionary<string, JournalRunner> runners, TimeSpan timeout, CancellationToken cancellation)
    {
        var verdicts = new Dictionary<string, (string Host, bool StillRuns)>(StringComparer.Ordinal);
        runners = runners.Where(run => run.Value.Pid > 0).ToDictionary(run => run.Key, run => run.Value, StringComparer.Ordinal);
        if (runners.Count == 0) return verdicts;
        var windows = hosts.Where(host => host.Connection.Shell.Kind == HostShellKind.PowerShell).ToList();
        var names = await Task.WhenAll(windows.Select(async host =>
        {
            try
            {
                var reply = (await host.Connection.RunAsync(WindowsMachineName, null, timeout, cancellation).ConfigureAwait(false))
                    .EnsureSuccess($"Reading {host.Host}'s machine name");
                return (host.Host, host.Connection, Machine: InteractiveClient.Line(reply.Stdout, "VT-MACHINE ")?.Trim());
            }
            catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or InvalidOperationException)
            {
                return (host.Host, host.Connection, Machine: (string?)null); // Cannot be asked: its runners stay unknown.
            }
        })).ConfigureAwait(false);
        // A runner's machine is a host only when exactly one host reports that name.
        var byMachine = names.Where(host => !string.IsNullOrEmpty(host.Machine)).GroupBy(host => host.Machine!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var asked = runners.Where(run => byMachine.ContainsKey(run.Value.Machine)).GroupBy(run => byMachine[run.Value.Machine].Host, StringComparer.Ordinal);
        var answers = await Task.WhenAll(asked.Select(async group =>
        {
            var host = byMachine[group.First().Value.Machine];
            try
            {
                var probed = await HostProcessProbe.ProbeAsync(host.Connection, group.Select(run => (run.Value.Pid, "")).Distinct().ToList(), timeout, cancellation).ConfigureAwait(false);
                var judged = new List<(string Run, bool StillRuns)>();
                foreach (var (run, runner) in group)
                {
                    var process = probed[(runner.Pid, "")];
                    // Asked without a start, the probe says gone, or same with the start it read: another start is another process.
                    if (process.State == ProbedState.Gone) judged.Add((run, false));
                    else if (process.State == ProbedState.Same && long.TryParse(process.StartIdentity, NumberStyles.None, CultureInfo.InvariantCulture, out long fileTime))
                        judged.Add((run, Math.Abs((DateTime.FromFileTimeUtc(fileTime) - runner.StartedUtc).TotalSeconds) <= 2));
                }
                return (host.Host, Runs: judged);
            }
            catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or InvalidOperationException)
            {
                return (host.Host, Runs: new List<(string Run, bool StillRuns)>());
            }
        })).ConfigureAwait(false);
        foreach (var (host, judged) in answers)
            foreach (var (run, stillRuns) in judged) verdicts[run] = (host, stillRuns);
        return verdicts;
    }

    // Variables: none. VT-MACHINE <name>: the machine name .NET reports there, as a runner journals it (Environment.MachineName).
    internal static readonly string WindowsMachineName = """
        'VT-MACHINE ' + [Environment]::MachineName
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Why <paramref name="run"/>, another process's, stops a new run from starting, as a preflight says it; null when it does
    /// not. <paramref name="here"/>: the run touched a host the new run uses (a run elsewhere that shares only the lease host
    /// is no conflict while it goes). A run that left something, provably its own or not, always stops it.
    /// </summary>
    internal static string? Problem(JournalRunStatus run, bool here)
    {
        string where = string.Join(", ", run.Hosts);
        return run.State switch
        {
            JournalRunState.Live when here => $"run {run.Run} is still going on {where} ({run.Reason}); wait for it to end",
            JournalRunState.Recoverable => $"run {run.Run} left {run.Items.Count} thing(s) on {where} ({run.Reason}); see valheim-test env status, then valheim-test env recover --run {run.Run}",
            JournalRunState.Unrecoverable => $"run {run.Run} left something on {where} that cannot be proven its own ({run.Reason}); see valheim-test env status and settle it by hand",
            JournalRunState.Unknown when run.Runner != null && here => $"run {run.Run} on {where} may still be going: {run.Reason}",
            _ => null,
        };
    }

    /// <summary>A preflight's refusal for a host whose journal could not be read.</summary>
    internal static string UnreadableProblem(string error) => $"could not be read, so whether another run is going there is unknown: {error}";

    /// <summary>
    /// A preflight's journal refusals from <paramref name="status"/>: each host whose journal could not be read, and each run of
    /// another process (<see cref="Problem"/>), <paramref name="here"/> saying whether that run touched a host the new run uses.
    /// The one wording the campaign's preflight and a bare <c>env preflight</c> share.
    /// </summary>
    internal static List<CampaignPreflightProblem> Problems(JournalStatusReport status, Func<JournalRunStatus, bool> here)
    {
        var problems = status.Hosts.Where(host => host.Error != null).Select(host => new CampaignPreflightProblem(host.Name, "run journal", UnreadableProblem(host.Error!))).ToList();
        string self = JournalRunner.Current.ToString();
        foreach (var run in status.Runs.Where(run => run.Runner != self))
            if (Problem(run, here(run)) is { } problem) problems.Add(new(run.Hosts[0], "run journal", problem));
        return problems;
    }

    /// <summary>The report as <c>valheim-test env status</c> prints it: each host, then each run that left something, with what it left.</summary>
    public static void Write(JournalStatusReport report, TextWriter output, bool json)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { report.Hosts, report.Runs, report.Unjournalled, report.Clean },
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
        foreach (var copy in report.Unjournalled)
            output.WriteLine($"UNJOURNALLED copy {copy.Path} ({copy.Kind}, {DiskSpace.Format(copy.Bytes)}, made {Time(copy.CreatedUtc)}" +
                (copy.InUse ? $", used by process {string.Join(", ", copy.InUseBy)}" : "") + "): made before runs journalled their copies; " +
                $"once its run is over, remove it with: valheim-test env teardown --copy \"{copy.Path}\"");
        output.WriteLine($"{Count(report.Runs.Count(run => run.State == JournalRunState.Ended), "run")} ended and left nothing. Nothing was changed.");
    }

    private static string Count(int count, string noun) => count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");
    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";
}
