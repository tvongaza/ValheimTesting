using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>
/// One entry of a run's journal: what the run is about to do on a host (<c>*-intended</c>), did (<c>*-done</c>) or left
/// (<c>*-retired</c>, <c>*-kept</c>), with the paths it concerns. Written before each effect, so an interrupted run leaves a
/// record of every path it may own.
/// </summary>
internal sealed record JournalEntry(string Kind, IReadOnlyDictionary<string, string> Fields)
{
    public const string CopyIntended = "copy-intended", CopyDone = "copy-done", CopyRetired = "copy-retired", CopyKept = "copy-kept";
    public const string MacListsCaptured = "mac-lists-captured", MacListsRestored = "mac-lists-restored";
    public const string CharacterIntended = "character-intended", CharacterDone = "character-done", CharacterRetired = "character-retired";
    public const string LockIntended = "lock-intended", LockHeld = "lock-held", LockReleased = "lock-released";
    public const string ProcessIntended = "process-intended", ProcessStarted = "process-started", ProcessStopped = "process-stopped";
    public const string MacAgentIntended = "mac-agent-intended", MacAgentRetired = "mac-agent-retired";
    public const string ScheduledTaskIntended = "scheduled-task-intended", ScheduledTaskStarted = "scheduled-task-started",
        ScheduledTaskRetired = "scheduled-task-retired";
    /// <summary>A recovery's word that a launch never journalled as started is over: its pid file's process is stopped or proven gone.</summary>
    public const string LaunchSettled = "launch-settled";
    public const string LeaseHeld = "lease-held", LeaseReleased = "lease-released", LeaseKept = "lease-kept";
    public const string RunEnded = "run-ended", RunRecovered = "run-recovered", CleanupAbandoned = "cleanup-abandoned";

    public static JournalEntry Of(string kind, params (string Key, string Value)[] fields) =>
        new(kind, fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal));
}

/// <summary>A journal line as read back: its time, actor and entry, the run it belongs to and the runner that wrote it.</summary>
internal sealed record JournalRecord(DateTime Utc, string Actor, JournalEntry Entry, JournalRunner Runner)
{
    public string Run { get; init; } = "";
}

/// <summary>
/// The <c>valheim-test</c> process that wrote a journal line: its machine, process ID and start time. A run whose runner is
/// still that process on that machine is going; one whose runner is proven gone and that never journalled its end was interrupted.
/// </summary>
internal sealed record JournalRunner(string Machine, int Pid, DateTime StartedUtc)
{
    private static readonly Lazy<JournalRunner> s_current = new(() =>
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return new(Environment.MachineName, self.Id, self.StartTime.ToUniversalTime());
    });
    /// <summary>This process.</summary>
    public static JournalRunner Current => s_current.Value;

    /// <summary>Whether the runner ran on this machine, where its process can be checked.</summary>
    public bool OnThisMachine => string.Equals(Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// On this machine: whether the runner still runs (the same process ID with the same start time, so a reused ID does not
    /// count). A process whose start time cannot be read counts as running. Never call for a runner on another machine.
    /// </summary>
    public bool StillRuns()
    {
        if (!OnThisMachine) throw new InvalidOperationException($"The runner ran on {Machine}, not on this machine.");
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(Pid);
            try { if (Math.Abs((process.StartTime.ToUniversalTime() - StartedUtc).TotalSeconds) > 2) return false; }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; } // no such process
    }

    public override string ToString() => $"process {Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)} on {Machine} (started {StartedUtc:yyyy-MM-dd HH:mm:ss}Z)";
}

/// <summary>
/// A run's durable journal on each host it touches (#257): <c>&lt;journal&gt;/&lt;runId&gt;/&lt;actor&gt;.jsonl</c>, one JSON object
/// per line, appended through the host's own shell before the effect it names. The journal directory sits beside the host's
/// lock (<see cref="DirectoryFor"/>), so it needs no inventory field. An entry that cannot be written stops the run before the
/// effect: a path the journal does not name is one a later recovery could not find.
/// </summary>
internal sealed class RunJournal
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public RunJournal(string runId)
    {
        if (!SafeName(runId)) throw new ArgumentException("A run id is letters, digits, '.', '_' and '-'.", nameof(runId));
        RunId = runId;
    }

    public string RunId { get; }

    /// <summary>A new run id: <c>run-&lt;UTC time&gt;-&lt;8 hex&gt;</c>.</summary>
    public static string NewRunId() =>
        "run-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A host's journal directory: <c>journal</c> beside its lock (the lock's parent directory).</summary>
    public static string DirectoryFor(HostProfile host)
    {
        string lockPath = host.Lock.TrimEnd('/', '\\');
        int at = lockPath.LastIndexOfAny(['/', '\\']);
        if (at <= 0) throw new ArgumentException($"The host lock {host.Lock} has no parent directory for the run journal.");
        return HostPath.Join(lockPath[..at], "journal");
    }

    /// <summary>
    /// This machine's journal (<c>journal</c> beside its default lock, under ValheimTesting's data folder), where copies made
    /// on this machine (<see cref="WorldFixture"/>) are journalled in process, without a shell.
    /// </summary>
    internal static string LocalDirectory => s_localFlow.Value ?? Path.Combine(new LocalSteamLocator().DataRoot, "journal");

    // Test seam: this machine's journal is the real machine's, never a simulated machine's (EnvironmentInventory.ThisMachine
    // may describe another platform's folders). It follows the data root (a FakeDataRoot scope, or the test default of
    // LocalSteamLocator.DataRootDefault); one test's flow may point it at a temporary folder of its own.
    private static readonly AsyncLocal<string?> s_localFlow = new();
    internal static IDisposable UseLocalDirectory(string directory)
    {
        string? previous = s_localFlow.Value;
        s_localFlow.Value = directory;
        return new LocalReset(previous);
    }
    private sealed class LocalReset(string? previous) : IDisposable { public void Dispose() => s_localFlow.Value = previous; }

    private static readonly Lazy<RunJournal> s_process = new(() => new RunJournal(NewRunId()));
    private static readonly AsyncLocal<RunScope?> s_run = new();
    /// <summary>
    /// The run this process's local copies are journalled under: the run going in this flow (<see cref="UseRun"/>), so a run's
    /// copies join its own journal run, else one run for the whole process.
    /// </summary>
    internal static RunJournal ThisProcess => s_run.Value?.Journal ?? s_process.Value;

    /// <summary>
    /// Journals this flow's local copies under <paramref name="runId"/> (a run's own id) until disposed. The scope records every
    /// journal this flow writes for that run, here or on a host, so the run's end goes to each of them and nowhere else
    /// (<c>RunFinish</c>, #667). A scope opened inside another keeps the outer one: an entry joins the nearest scope of its run.
    /// </summary>
    internal static RunScope UseRun(string runId)
    {
        var scope = new RunScope(new RunJournal(runId), s_run.Value);
        s_run.Value = scope;
        return scope;
    }

    /// <summary>
    /// Notes, before an entry is written, that this flow journals run <paramref name="runId"/> at <paramref name="place"/>
    /// (one key per journal file set: a host and its journal directory). <paramref name="write"/> writes another entry there.
    /// Outside a scope of that run nothing is noted: only the run's own flow ends it.
    /// </summary>
    internal static void Journalled(string runId, string place, Func<string, JournalEntry, TimeSpan, Task> write, TimeSpan timeout)
    {
        for (var scope = s_run.Value; scope != null; scope = scope.Parent)
            if (scope.Journal.RunId == runId) { scope.Note(place, write, timeout); return; }
    }

    /// <summary>
    /// One run in one flow (<see cref="UseRun"/>): where the flow journalled it, and whether its end was written. The journal's
    /// record of "did this process journal anything for the run", so no caller keeps a flag of its own.
    /// </summary>
    internal sealed class RunScope : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Place> _places = new(StringComparer.Ordinal);
        private int _ended;
        internal RunScope(RunJournal journal, RunScope? parent) { Journal = journal; Parent = parent; }
        public RunJournal Journal { get; }
        public string RunId => Journal.RunId;
        internal RunScope? Parent { get; }
        /// <summary>Where this flow journalled the run, in the order first written.</summary>
        internal IReadOnlyList<Place> Places => _places.Values.OrderBy(place => place.Order).ToList();
        private int _order;
        internal void Note(string key, Func<string, JournalEntry, TimeSpan, Task> write, TimeSpan timeout) =>
            _places.TryAdd(key, new Place(key, write, timeout, Interlocked.Increment(ref _order)));
        /// <summary>True once: the caller that gets it writes the run's end.</summary>
        internal bool TryEnd() => Interlocked.Exchange(ref _ended, 1) == 0;
        public void Dispose() => s_run.Value = Parent;
    }

    /// <summary>A journal a flow wrote a run to: its key, how to write one more entry there and the timeout its entries used.</summary>
    internal sealed record Place(string Key, Func<string, JournalEntry, TimeSpan, Task> Write, TimeSpan Timeout, int Order);

    /// <summary>
    /// Copies this machine's journal says a still-running process holds: made (or being made) by a runner on this machine that
    /// still runs, and not yet retired or kept. Path (as journalled) to the holder's process ID. Reads only.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> LocalHolders()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var open = new Dictionary<string, JournalRunner>(comparer);
        if (!Directory.Exists(LocalDirectory)) return new Dictionary<string, int>(comparer);
        foreach (string file in Directory.EnumerateFiles(LocalDirectory, WorldFixture.Actor + ".jsonl", SearchOption.AllDirectories))
        {
            string[] lines;
            try { lines = ReadShared(file).Split('\n'); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (string line in lines.Where(line => line.Trim().Length != 0))
            {
                JournalRecord record;
                try { record = ParseLine(line); }
                catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { continue; }
                if (record.Entry.Fields.GetValueOrDefault("runtime") is not { } path) continue;
                if (record.Entry.Kind is JournalEntry.CopyIntended or JournalEntry.CopyDone) open[path] = record.Runner;
                else if (record.Entry.Kind is JournalEntry.CopyRetired or JournalEntry.CopyKept) open.Remove(path);
            }
        }
        return open.Where(entry => entry.Value.OnThisMachine && entry.Value.StillRuns())
            .ToDictionary(entry => entry.Key, entry => entry.Value.Pid, comparer);
    }

    // A journal is read while runs append to it: readers share it for writing, so an append never fails because of a read
    // (Windows refuses a write to a file another handle opened read-only-shared).
    private static string ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static readonly object s_localWrite = new();
    /// <summary>
    /// Appends <paramref name="entry"/> to this machine's journal (<paramref name="journal"/>, default <see cref="LocalDirectory"/>)
    /// in this process; throws when it cannot.
    /// </summary>
    internal void AppendLocal(string actor, JournalEntry entry, string? journal = null)
    {
        string root = Path.GetFullPath(journal ?? LocalDirectory);
        string directory = Path.Combine(root, RunId);
        string line = Line(actor, entry) + "\n";
        lock (s_localWrite)
        {
            Directory.CreateDirectory(directory);
            // Another process may hold the file for a moment (a reader from before readers shared it, an indexer): retry briefly.
            for (int attempt = 1; ; attempt++)
                try
                {
                    using var stream = new FileStream(Path.Combine(directory, actor + ".jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    byte[] bytes = new UTF8Encoding(false).GetBytes(line);
                    stream.Write(bytes);
                    break;
                }
                catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
        }
        // Noted once written: a write in process that threw left nothing here, so the run's end need not come here either.
        Journalled(RunId, LocalPlace(root), (again, last, _) => { AppendLocal(again, last, root); return Task.CompletedTask; }, TimeSpan.Zero);
    }

    /// <summary>The place key of a journal on this machine, however it is written (in process or through the local shell).
    /// Windows and macOS file systems ignore case, so one directory spelled two ways is one place.</summary>
    internal static string LocalPlace(string journal)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(journal));
        return "this machine|" + (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? path.ToUpperInvariant() : path);
    }

    // One journal line: when, which run and actor, what, and the runner that wrote it.
    internal string Line(string actor, JournalEntry entry)
    {
        if (!SafeName(actor)) throw new ArgumentException("An actor name is letters, digits, '.', '_' and '-'.", nameof(actor));
        var line = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["utc"] = DateTime.UtcNow.ToString("O"), ["run"] = RunId, ["actor"] = actor, ["kind"] = entry.Kind, ["fields"] = entry.Fields,
            ["runner"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["machine"] = JournalRunner.Current.Machine, ["pid"] = JournalRunner.Current.Pid, ["startedUtc"] = JournalRunner.Current.StartedUtc.ToString("O"),
            },
        };
        return JsonSerializer.Serialize(line, Json);
    }

    internal static JournalRecord ParseLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var fields = root.GetProperty("fields").EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString() ?? "", StringComparer.Ordinal);
        // Every line names its runner; one without is unreadable (KeyNotFoundException), never a run that cannot be judged.
        var by = root.GetProperty("runner");
        var runner = new JournalRunner(by.GetProperty("machine").GetString()!, by.GetProperty("pid").GetInt32(),
            DateTime.Parse(by.GetProperty("startedUtc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
        return new(DateTime.Parse(root.GetProperty("utc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
            root.GetProperty("actor").GetString()!, new JournalEntry(root.GetProperty("kind").GetString()!, fields), runner)
        { Run = root.GetProperty("run").GetString()! };
    }

    internal static bool SafeName(string name) =>
        name.Length is > 0 and <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && name.Trim('.').Length != 0;
}
