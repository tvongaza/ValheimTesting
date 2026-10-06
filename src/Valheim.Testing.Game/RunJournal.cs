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
    public const string CharacterIntended = "character-intended", CharacterDone = "character-done", CharacterRetired = "character-retired";
    public const string LockHeld = "lock-held", LockReleased = "lock-released";
    public const string ProcessIntended = "process-intended", ProcessStarted = "process-started", ProcessStopped = "process-stopped";
    /// <summary>A recovery's word that a launch never journalled as started is over: its pid file's process is stopped or proven gone.</summary>
    public const string LaunchSettled = "launch-settled";
    public const string LeaseHeld = "lease-held", LeaseReleased = "lease-released", LeaseKept = "lease-kept";
    public const string RunEnded = "run-ended", RunRecovered = "run-recovered", CleanupAbandoned = "cleanup-abandoned";

    public static JournalEntry Of(string kind, params (string Key, string Value)[] fields) =>
        new(kind, fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal));
}

/// <summary>A journal line as read back: its time, actor and entry, the run it belongs to and the runner that wrote it (null on lines written before runners were recorded).</summary>
internal sealed record JournalRecord(DateTime Utc, string Actor, JournalEntry Entry)
{
    public string Run { get; init; } = "";
    public JournalRunner? Runner { get; init; }
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
        return HostInstall.Join(lockPath[..at], "journal");
    }

    /// <summary>Appends <paramref name="entry"/> for <paramref name="actor"/> on <paramref name="host"/>; throws unless the host confirms the write.</summary>
    public async Task AppendAsync(IGameHost host, string journal, string actor, JournalEntry entry, TimeSpan timeout, CancellationToken cancellation = default)
    {
        // Base64 keeps the line one shell word whatever the paths hold; the host decodes it before appending.
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Line(actor, entry)));
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsAppend : BashAppend, new Dictionary<string, string>
        {
            ["journal"] = journal, ["run"] = RunId, ["actor"] = actor, ["line"] = encoded,
        }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Journalling {entry.Kind} for {actor} on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-JOURNALED") == null)
            throw new HostOperationException($"The run journal on {host.Name} did not confirm {entry.Kind} for {actor}", result);
    }

    /// <summary>
    /// This machine's journal (<c>journal</c> beside its default lock, under ValheimTesting's data folder), where copies made
    /// on this machine (<see cref="WorldFixture"/>) are journalled in process, without a shell.
    /// </summary>
    internal static string LocalDirectory => s_localFlow.Value
        ?? (LocalSteamLocator.SimulatedDataRoot.Value is { } fake ? Path.Combine(fake.Directory, "journal") : null)
        ?? LocalDirectoryDefault ?? Path.Combine(new LocalSteamLocator().DataRoot, "journal");

    // Test seams: this machine's journal is the real machine's, never a simulated machine's (EnvironmentInventory.ThisMachine
    // may describe another platform's folders). Tests point it at a temporary folder, for all tests or for one test's flow;
    // a no-game test outside this assembly uses a Fakes.FakeDataRoot scope.
    internal static string? LocalDirectoryDefault { get; set; }
    private static readonly AsyncLocal<string?> s_localFlow = new();
    internal static IDisposable UseLocalDirectory(string directory)
    {
        string? previous = s_localFlow.Value;
        s_localFlow.Value = directory;
        return new LocalReset(previous);
    }
    private sealed class LocalReset(string? previous) : IDisposable { public void Dispose() => s_localFlow.Value = previous; }

    private static readonly Lazy<RunJournal> s_process = new(() => new RunJournal(NewRunId()));
    private static readonly AsyncLocal<RunJournal?> s_run = new();
    /// <summary>
    /// The run this process's local copies are journalled under: the run going in this flow (<see cref="UseRun"/>), so a run's
    /// copies join its own journal run, else one run for the whole process.
    /// </summary>
    internal static RunJournal ThisProcess => s_run.Value ?? s_process.Value;

    /// <summary>Journals this flow's local copies under <paramref name="runId"/> (a run's own id) until disposed.</summary>
    internal static IDisposable UseRun(string runId)
    {
        var previous = s_run.Value;
        s_run.Value = new RunJournal(runId);
        return new RunReset(previous);
    }
    private sealed class RunReset(RunJournal? previous) : IDisposable { public void Dispose() => s_run.Value = previous; }

    /// <summary>
    /// Copies this machine's journal says a still-running process holds: made (or being made) by a runner on this machine that
    /// still runs, and not yet retired or kept. Path (as journalled) to the holder's process ID. Reads only.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> LocalHolders()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var open = new Dictionary<string, JournalRunner?>(comparer);
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
        return open.Where(entry => entry.Value is { OnThisMachine: true } runner && runner.StillRuns())
            .ToDictionary(entry => entry.Key, entry => entry.Value!.Pid, comparer);
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
        string directory = Path.Combine(journal ?? LocalDirectory, RunId);
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
                    return;
                }
                catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
        }
    }

    // One journal line: when, which run and actor, what, and the runner that wrote it.
    private string Line(string actor, JournalEntry entry)
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

    /// <summary>Every entry of run <paramref name="runId"/> on <paramref name="host"/>, in the order written per actor.</summary>
    public static async Task<IReadOnlyList<JournalRecord>> ReadAsync(IGameHost host, string journal, string runId, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (!SafeName(runId)) throw new ArgumentException("A run id is letters, digits, '.', '_' and '-'.", nameof(runId));
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsRead : BashRead,
            new Dictionary<string, string> { ["journal"] = journal, ["run"] = runId }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Reading run {runId}'s journal on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-JOURNAL-END") == null)
            throw new HostOperationException($"Run {runId}'s journal on {host.Name} was not read to its end", result);
        return Parse(result.Stdout);
    }

    /// <summary>
    /// Every entry of every run journalled on <paramref name="host"/>, in the order written per run and actor, and how many
    /// lines could not be read (a line cut short by a full disk or a killed write): those are skipped and counted, never guessed.
    /// Changes nothing.
    /// </summary>
    public static async Task<(IReadOnlyList<JournalRecord> Records, int Unreadable)> ReadAllAsync(IGameHost host, string journal, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsReadAll : BashReadAll,
            new Dictionary<string, string> { ["journal"] = journal }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Reading the run journal on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-JOURNAL-END") == null)
            throw new HostOperationException($"The run journal on {host.Name} was not read to its end", result);
        var records = new List<JournalRecord>();
        int unreadable = 0;
        // One base64 word per file (one encoder per file, not per line), each file's lines in order.
        foreach (string raw in result.Stdout.Split('\n'))
        {
            if (!raw.StartsWith("VT-JOURNAL-FILE ", StringComparison.Ordinal)) continue;
            string text;
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(raw["VT-JOURNAL-FILE ".Length..].Trim())); }
            catch (FormatException) { unreadable++; continue; }
            foreach (string line in text.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                try { records.Add(ParseLine(line)); }
                catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { unreadable++; }
            }
        }
        return (records, unreadable);
    }

    private static List<JournalRecord> Parse(string stdout) =>
        stdout.Split('\n').Where(raw => raw.StartsWith("VT-JOURNAL ", StringComparison.Ordinal))
            .Select(raw => ParseLine(Encoding.UTF8.GetString(Convert.FromBase64String(raw["VT-JOURNAL ".Length..].Trim())))).ToList();

    private static JournalRecord ParseLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var fields = root.GetProperty("fields").EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString() ?? "", StringComparer.Ordinal);
        JournalRunner? runner = null;
        if (root.TryGetProperty("runner", out var by) && by.ValueKind == JsonValueKind.Object)
            runner = new(by.GetProperty("machine").GetString()!, by.GetProperty("pid").GetInt32(),
                DateTime.Parse(by.GetProperty("startedUtc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
        return new(DateTime.Parse(root.GetProperty("utc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
            root.GetProperty("actor").GetString()!, new JournalEntry(root.GetProperty("kind").GetString()!, fields))
        { Run = root.GetProperty("run").GetString()!, Runner = runner };
    }

    private static bool SafeName(string name) =>
        name.Length is > 0 and <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && name.Trim('.').Length != 0;

    // Variables: journal, run, actor, line (base64 of one JSON object). Appends the decoded line and a newline in one write.
    internal static readonly string BashAppend = """
        dir="$journal/$run"
        mkdir -p -- "$dir" || exit 4
        decoded=$(printf '%s' "$line" | base64 -d 2>/dev/null || printf '%s' "$line" | base64 -D) || exit 4
        printf '%s\n' "$decoded" >> "$dir/$actor.jsonl" || exit 4
        echo VT-JOURNALED
        """;
    internal static readonly string WindowsAppend = """
        $dir = Join-Path $journal $run
        [void][IO.Directory]::CreateDirectory($dir)
        $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line)) + "`n"
        [IO.File]::AppendAllText((Join-Path $dir ($actor + '.jsonl')), $text, (New-Object Text.UTF8Encoding $false))
        'VT-JOURNALED'
        """;
    // Variables: journal, run. Each line goes back base64-encoded, one per output line; a missing run reads as empty.
    internal static readonly string BashRead = """
        dir="$journal/$run"
        if [ -d "$dir" ]; then
          for file in "$dir"/*.jsonl; do
            [ -f "$file" ] || continue
            while IFS= read -r entry || [ -n "$entry" ]; do
              [ -n "$entry" ] && printf 'VT-JOURNAL %s\n' "$(printf '%s' "$entry" | base64 | tr -d '\n')"
            done < "$file"
          done
        fi
        echo VT-JOURNAL-END
        """;
    internal static readonly string WindowsRead = """
        $dir = Join-Path $journal $run
        if ([IO.Directory]::Exists($dir)) {
            foreach ($file in [IO.Directory]::GetFiles($dir, '*.jsonl')) {
                # Shared for writing, so a run appending at this moment is not refused.
                $reader = New-Object IO.StreamReader([IO.File]::Open($file, 'Open', 'Read', 'ReadWrite, Delete'), [Text.Encoding]::UTF8)
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                foreach ($entry in ($text -split "`n")) {
                    $entry = $entry.TrimEnd("`r")
                    if ($entry) { 'VT-JOURNAL ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($entry)) }
                }
            }
        }
        'VT-JOURNAL-END'
        """;
    // Variables: journal. Every run's files, each sent whole as one base64 word; a missing journal reads as empty.
    internal static readonly string BashReadAll = """
        if [ -d "$journal" ]; then
          for file in "$journal"/*/*.jsonl; do
            [ -f "$file" ] || continue
            printf 'VT-JOURNAL-FILE %s\n' "$(base64 < "$file" | tr -d '\n')"
          done
        fi
        echo VT-JOURNAL-END
        """;
    internal static readonly string WindowsReadAll = """
        if ([IO.Directory]::Exists($journal)) {
            foreach ($dir in [IO.Directory]::GetDirectories($journal)) {
                foreach ($file in [IO.Directory]::GetFiles($dir, '*.jsonl')) {
                    # Shared for writing, so a run appending at this moment is not refused.
                    $stream = [IO.File]::Open($file, 'Open', 'Read', 'ReadWrite, Delete')
                    try { $bytes = New-Object byte[] $stream.Length; $read = 0; while ($read -lt $bytes.Length) { $n = $stream.Read($bytes, $read, $bytes.Length - $read); if ($n -le 0) { break }; $read += $n } }
                    finally { $stream.Dispose() }
                    'VT-JOURNAL-FILE ' + [Convert]::ToBase64String($bytes, 0, $read)
                }
            }
        }
        'VT-JOURNAL-END'
        """;
}
