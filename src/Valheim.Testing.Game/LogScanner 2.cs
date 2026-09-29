using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>A failure fails the run's log scan; a warning is counted and reported only.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogSeverity>))]
public enum LogSeverity { Failure, Warning }

/// <summary>
/// A known log problem. <see cref="Line"/> matches one line; with <see cref="Frame"/>, the line counts only when a line of
/// its stack trace (the lines after it, up to a blank line or the next BepInEx log header) matches the frame.
/// </summary>
public sealed record LogPattern(string Name, LogSeverity Severity, Regex Line, Regex? Frame = null);

/// <summary>A run's own classification of a known pattern, with the written reason it differs from the default.</summary>
public sealed class LogClassification
{
    /// <summary>Required: <c>Failure</c> or <c>Warning</c>.</summary>
    public LogSeverity? Severity { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>
/// A log file a run's teardown scan reads. <paramref name="Required"/>: its absence is a failure (a BepInEx log);
/// otherwise it is recorded as absent (a Unity log that was not redirected there).
/// </summary>
public sealed record RunLog(string Role, string Path, bool Required = false);

/// <summary>How often a pattern matched in one log and where first; <see cref="Reason"/> is set when the run reclassified it.</summary>
public sealed record LogPatternCount(string Pattern, LogSeverity Severity, string? Reason, int Count, int? FirstLine, string? First);

/// <summary>
/// One log's scan. An absent log has no counts (absent is not zero); <see cref="Problem"/> says why a required one is
/// missing. <see cref="Failed"/> when a required log is missing or a failure pattern matched.
/// </summary>
public sealed record LogFileScan(string Role, string Path, bool Present, string? Problem, IReadOnlyList<LogPatternCount> Counts)
{
    public bool Failed => Problem != null || Counts.Any(count => count.Severity == LogSeverity.Failure && count.Count > 0);
}

/// <summary>
/// Scans a whole run's logs after its processes stopped, for problems that only warn or appear long after startup: a
/// Harmony patch whose target is gone, a global unpatch, missing members after a game update, errors while objects unload,
/// RPCs without a handler, missing scripts and shaders a GPU cannot run. Each known pattern is counted per log with its
/// first occurrence and has a default severity a run may change with a written reason. BepInEx warning and error lines
/// that match no pattern are counted as <see cref="UnknownWarning"/> and <see cref="UnknownError"/>, never ignored. Unity's
/// Player.log has no levels, so there only the known patterns count. The same Unity message may appear in both logs.
/// </summary>
public static class LogScanner
{
    public const string UnknownWarning = "unknown-warning", UnknownError = "unknown-error";
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private const int FrameLines = 20, TextLimit = 500;
    // BepInEx's disk log line: "[Level  :Source] message". Continuation lines (stack traces) have no header.
    private static readonly Regex Header = new(@"^\[(Info|Message|Warning|Error|Fatal|Debug) *:[^\]]*\]", Options);

    /// <summary>The known patterns and their default severities.</summary>
    public static IReadOnlyList<LogPattern> Patterns { get; } =
    [
        // HarmonyX's warning when a mod calls UnpatchAll() without an id: every mod's patches are removed.
        new("harmony-unpatch-all", LogSeverity.Failure, new(@"UnpatchAll has been called", Options)),
        // HarmonyX's error for a patch class whose target method does not exist (renamed or removed by a game update).
        new("harmony-undefined-target", LogSeverity.Failure, new(@"Undefined target method for (?:reverse )?patch method", Options)),
        // HarmonyX's AccessTools lookups that found nothing. Mods also probe optional members this way, so a warning.
        new("accesstools-not-found", LogSeverity.Warning, new(@"AccessTools\.\w+: Could not find ", Options)),
        new("missing-method", LogSeverity.Failure, new(@"\bMissingMethodException\b", Options)),
        new("missing-field", LogSeverity.Failure, new(@"\bMissingFieldException\b", Options)),
        new("type-load", LogSeverity.Failure, new(@"\bTypeLoadException\b", Options)),
        // A mod destroying networked objects the scene still tracks.
        new("nre-remove-objects", LogSeverity.Failure, new(@"\bNullReferenceException\b", Options), new(@"\bZNetScene\.RemoveObjects\b", Options)),
        // The game's warning for a per-object RPC that no component registered (a mod missing on one side, or a typo).
        new("rpc-method-missing", LogSeverity.Warning, new(@"Failed to find rpc method", Options)),
        // Unity: a prefab from an asset bundle references a script that is not loaded.
        new("missing-script", LogSeverity.Warning, new(@"The referenced script\b.*\bis missing", Options)),
        // Unity: a bundle's shader was not built for this graphics API (magenta objects on Vulkan or OpenGL clients).
        new("shader-unsupported", LogSeverity.Warning, new(@"not supported on this GPU|Shader Unsupported\b", Options)),
    ];

    /// <summary>Every name a classification may use: the patterns, <see cref="UnknownWarning"/> and <see cref="UnknownError"/>.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Patterns.Select(pattern => pattern.Name), UnknownWarning, UnknownError];

    /// <summary>Refuses a classification of an unknown name or without a written reason.</summary>
    public static void CheckClassifications(IReadOnlyDictionary<string, LogClassification>? classifications)
    {
        foreach (var entry in classifications ?? new Dictionary<string, LogClassification>())
        {
            if (!Names.Contains(entry.Key)) throw new ArgumentException($"Log scan: {entry.Key} is not a known pattern ({string.Join(", ", Names)}).");
            if (entry.Value?.Severity is not { } severity || !Enum.IsDefined(severity) || string.IsNullOrWhiteSpace(entry.Value.Reason))
                throw new ArgumentException($"Log scan: classify {entry.Key} as Failure or Warning with a written reason.");
        }
    }

    /// <summary>Counts every pattern in one log. A missing log has no counts; a missing required log is a <see cref="LogFileScan.Problem"/>.</summary>
    public static LogFileScan Scan(RunLog log, IReadOnlyDictionary<string, LogClassification>? classifications = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        CheckClassifications(classifications);
        if (!File.Exists(log.Path)) return new(log.Role, log.Path, false, log.Required ? Missing(log) : null, []);
        List<string> lines;
        using (var reader = new StreamReader(new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            lines = [];
            for (string? line; (line = reader.ReadLine()) != null;) lines.Add(line);
        }
        var names = Names;
        var counts = new int[names.Count];
        var first = new (int Line, string Text)?[names.Count];
        void Hit(int index, int line)
        {
            counts[index]++;
            first[index] ??= (line + 1, lines[line].Length > TextLimit ? lines[line][..TextLimit] + " [truncated]" : lines[line]);
        }
        // The current BepInEx record: its header's level and line, and whether a known pattern matched in it.
        string? level = null; int headerLine = -1; bool known = false;
        void EndRecord()
        {
            if (!known && level is "Warning") Hit(names.Count - 2, headerLine);
            if (!known && level is "Error" or "Fatal") Hit(names.Count - 1, headerLine);
            level = null; known = false;
        }
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            var header = Header.Match(line);
            if (header.Success) { EndRecord(); level = header.Groups[1].Value; headerLine = i; }
            else if (line.Trim().Length == 0) { EndRecord(); continue; }
            for (int p = 0; p < Patterns.Count; p++)
            {
                var pattern = Patterns[p];
                if (!pattern.Line.IsMatch(line) || (pattern.Frame != null && !HasFrame(lines, i, pattern.Frame))) continue;
                Hit(p, i); known = true;
            }
        }
        EndRecord();
        var result = new List<LogPatternCount>(names.Count);
        for (int n = 0; n < names.Count; n++)
        {
            var severity = n < Patterns.Count ? Patterns[n].Severity : LogSeverity.Warning;
            string? reason = null;
            if (classifications != null && classifications.TryGetValue(names[n], out var chosen)) { severity = chosen.Severity!.Value; reason = chosen.Reason; }
            result.Add(new(names[n], severity, reason, counts[n], first[n]?.Line, first[n]?.Text));
        }
        return new(log.Role, log.Path, true, null, result);
    }

    // The stack trace under line `at`: the lines after it, up to a blank line or the next BepInEx header.
    private static bool HasFrame(List<string> lines, int at, Regex frame)
    {
        for (int i = at + 1; i < lines.Count && i <= at + FrameLines; i++)
        {
            if (lines[i].Trim().Length == 0 || Header.IsMatch(lines[i])) return false;
            if (frame.IsMatch(lines[i])) return true;
        }
        return false;
    }

    // DirectServerProcess leaves "<copy>.absent" when the game never created the log.
    private static string Missing(RunLog log) => File.Exists(log.Path + ".absent")
        ? $"{log.Role} was never written: the process ended before BepInEx started. Read its Unity player log (Player.log, the -logFile file or its stdout capture) for the reason;" +
          " security software that blocks or quarantines the game or BepInEx's Doorstop loader is a known cause."
        : $"{log.Role} was not kept at {log.Path}: its process was not stopped by its session, which copies the log when it stops.";
}
