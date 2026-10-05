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
    public const string RunEnded = "run-ended";

    public static JournalEntry Of(string kind, params (string Key, string Value)[] fields) =>
        new(kind, fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal));
}

/// <summary>A journal line as read back: its time, actor and entry.</summary>
internal sealed record JournalRecord(DateTime Utc, string Actor, JournalEntry Entry);

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
        if (!SafeName(actor)) throw new ArgumentException("An actor name is letters, digits, '.', '_' and '-'.", nameof(actor));
        var line = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["utc"] = DateTime.UtcNow.ToString("O"), ["run"] = RunId, ["actor"] = actor, ["kind"] = entry.Kind, ["fields"] = entry.Fields,
        };
        // Base64 keeps the line one shell word whatever the paths hold; the host decodes it before appending.
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json)));
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsAppend : BashAppend, new Dictionary<string, string>
        {
            ["journal"] = journal, ["run"] = RunId, ["actor"] = actor, ["line"] = encoded,
        }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Journalling {entry.Kind} for {actor} on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-JOURNALED") == null)
            throw new HostOperationException($"The run journal on {host.Name} did not confirm {entry.Kind} for {actor}", result);
    }

    /// <summary>Every entry of run <paramref name="runId"/> on <paramref name="host"/>, in the order written per actor.</summary>
    public static async Task<IReadOnlyList<JournalRecord>> ReadAsync(IGameHost host, string journal, string runId, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (!SafeName(runId)) throw new ArgumentException("A run id is letters, digits, '.', '_' and '-'.", nameof(runId));
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsRead : BashRead,
            new Dictionary<string, string> { ["journal"] = journal, ["run"] = runId }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Reading run {runId}'s journal on {host.Name}");
        var records = new List<JournalRecord>();
        foreach (string raw in result.Stdout.Split('\n'))
        {
            if (!raw.StartsWith("VT-JOURNAL ", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(raw["VT-JOURNAL ".Length..].Trim())));
            var root = document.RootElement;
            var fields = root.GetProperty("fields").EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString() ?? "", StringComparer.Ordinal);
            records.Add(new(DateTime.Parse(root.GetProperty("utc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
                root.GetProperty("actor").GetString()!, new JournalEntry(root.GetProperty("kind").GetString()!, fields)));
        }
        if (InteractiveClient.Line(result.Stdout, "VT-JOURNAL-END") == null)
            throw new HostOperationException($"Run {runId}'s journal on {host.Name} was not read to its end", result);
        return records;
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
                foreach ($entry in [IO.File]::ReadAllLines($file, [Text.Encoding]::UTF8)) {
                    if ($entry) { 'VT-JOURNAL ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($entry)) }
                }
            }
        }
        'VT-JOURNAL-END'
        """;
}
