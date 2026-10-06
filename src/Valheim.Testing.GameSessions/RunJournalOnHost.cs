using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// A run's journal on a host other than through this process (#257): appended and read through the host's own shell, so a
/// remote host's journal and this machine's read the same way. <see cref="RunJournal"/> holds the line format and this
/// machine's in-process journal.
/// </summary>
internal static class RunJournalOnHost
{
    /// <summary>Appends <paramref name="entry"/> for <paramref name="actor"/> on <paramref name="host"/>; throws unless the host confirms the write.</summary>
    public static async Task AppendAsync(this RunJournal run, IGameHost host, string journal, string actor, JournalEntry entry, TimeSpan timeout, CancellationToken cancellation = default)
    {
        // Base64 keeps the line one shell word whatever the paths hold; the host decodes it before appending.
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(run.Line(actor, entry)));
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsAppend : BashAppend, new Dictionary<string, string>
        {
            ["journal"] = journal, ["run"] = run.RunId, ["actor"] = actor, ["line"] = encoded,
        }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Journalling {entry.Kind} for {actor} on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-JOURNALED") == null)
            throw new HostOperationException($"The run journal on {host.Name} did not confirm {entry.Kind} for {actor}", result);
    }

    /// <summary>Every entry of run <paramref name="runId"/> on <paramref name="host"/>, in the order written per actor.</summary>
    public static async Task<IReadOnlyList<JournalRecord>> ReadAsync(IGameHost host, string journal, string runId, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (!RunJournal.SafeName(runId)) throw new ArgumentException("A run id is letters, digits, '.', '_' and '-'.", nameof(runId));
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
                try { records.Add(RunJournal.ParseLine(line)); }
                catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { unreadable++; }
            }
        }
        return (records, unreadable);
    }

    private static List<JournalRecord> Parse(string stdout) =>
        stdout.Split('\n').Where(raw => raw.StartsWith("VT-JOURNAL ", StringComparison.Ordinal))
            .Select(raw => RunJournal.ParseLine(Encoding.UTF8.GetString(Convert.FromBase64String(raw["VT-JOURNAL ".Length..].Trim())))).ToList();

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
