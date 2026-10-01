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

/// <summary>
/// A run's own classification of a known pattern, with the written reason it differs from the default: another severity,
/// named lines the run expects, or both.
/// </summary>
public sealed class LogClassification
{
    /// <summary><c>Failure</c> or <c>Warning</c>; absent keeps the pattern's default.</summary>
    public LogSeverity? Severity { get; set; }
    /// <summary>
    /// Texts that name lines this run expects, such as a lookup a mod makes on purpose: a line of the pattern containing
    /// one of them (ordinal) is counted as <see cref="LogPatternCount.Expected"/> and never fails the scan. Every other
    /// line of the pattern still counts, so naming the expected lines keeps the pattern's check for the rest.
    /// </summary>
    public List<string> Expected { get; set; } = [];
    public string Reason { get; set; } = "";
}

/// <summary>
/// A log file a run's teardown scan reads. <paramref name="Required"/>: its absence is a failure (a BepInEx log);
/// otherwise it is recorded as absent (a Unity log that was not redirected there).
/// </summary>
public sealed record RunLog(string Role, string Path, bool Required = false);

/// <summary>
/// How often a pattern matched in one log and where first; <see cref="Reason"/> is set when the run reclassified it.
/// Lines the run named as expected are counted apart in <see cref="Expected"/> (the first in <see cref="FirstExpected"/>)
/// and never fail. <see cref="FirstFrame"/> is the most useful stack frame under the first counted line, for
/// <see cref="LogScanner.UnityException"/>: the first frame outside the runtime's own System, Mono and wrapper frames.
/// </summary>
public sealed record LogPatternCount(string Pattern, LogSeverity Severity, string? Reason, int Count, int? FirstLine, string? First, int Expected = 0, string? FirstExpected = null, string? FirstFrame = null);

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
/// RPCs without a handler, objects whose prefab is not registered, missing scripts and shaders a GPU cannot run. Each known pattern is counted per log with its
/// first occurrence and has a default severity a run may change with a written reason. BepInEx warning and error lines
/// that match no pattern are counted as <see cref="UnknownWarning"/> and <see cref="UnknownError"/>, never ignored. Unity's
/// Player.log has no levels; there an exception Unity printed (a line that starts with the exception's type name, outside
/// a BepInEx warning or error record) that no known pattern names is counted as <see cref="UnityException"/>, which fails.
/// The same Unity message may appear in both logs.
/// Owned processes are asked to quit at teardown and killed only if they do not (<see cref="IServerProcess.StopCleanly"/>):
/// after a clean stop the logs include what the game and its mods logged while shutting down, for example an UnpatchAll a
/// mod calls when the game quits; after a kill they do not.
/// </summary>
public static class LogScanner
{
    public const string UnknownWarning = "unknown-warning", UnknownError = "unknown-error", UnityException = "unity-exception";
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private const int FrameLines = 20, TextLimit = 500;
    // BepInEx's disk log line: "[Level  :Source] message". Continuation lines (stack traces) have no header.
    private static readonly Regex Header = new(@"^\[(Info|Message|Warning|Error|Fatal|Debug) *:[^\]]*\]", Options);
    // How Unity prints an exception it caught: the type name at the start of the line, then ": message" or nothing, for
    // example "NullReferenceException: Object reference not set to an instance of an object". Its frames follow, indented.
    private static readonly Regex ExceptionLine = new(@"^(?:[A-Za-z_]\w*\.)*\w*Exception(?::|$)", Options);
    // Frames that say where the runtime was, not which code threw: skipped when choosing FirstFrame.
    private static readonly Regex RuntimeFrame = new(@"^\s*(?:at\s+)?(?:\(wrapper |System\.|Mono\.)", Options);

    /// <summary>The known patterns and their default severities.</summary>
    public static IReadOnlyList<LogPattern> Patterns { get; } =
    [
        // HarmonyX's warning when a mod calls UnpatchAll() without an id: every mod's patches are removed. Not its
        // "Legacy UnpatchAll has been called AND DisallowLegacyGlobalUnpatchAll=true. Skipping execution", which removes nothing.
        new("harmony-unpatch-all", LogSeverity.Failure, new(@"UnpatchAll has been called - This will remove ALL", Options)),
        // HarmonyX's error for a patch class whose target method does not exist (renamed or removed by a game update).
        new("harmony-undefined-target", LogSeverity.Failure, new(@"Undefined target method for (?:reverse )?patch method", Options)),
        // HarmonyX's AccessTools lookups that found nothing. On the Valheim 1.0.16 Windows dedicated server (BepInEx 5.4.23.5,
        // HarmonyX 2.9.0) a [HarmonyPatch] on a method that no longer exists logs this warning in BepInEx's log; PatchAll then
        // throws "Undefined target method", which only Unity's own log (-logFile) records. This warning is the missing
        // target's trace in the one log every run keeps, so it fails. A mod that probes optional members this way names
        // those lines as expected in its plan (LogClassification.Expected), which keeps the check for every other line.
        new("accesstools-not-found", LogSeverity.Failure, new(@"AccessTools\.\w+: Could not find ", Options)),
        new("missing-method", LogSeverity.Failure, new(@"\bMissingMethodException\b", Options)),
        new("missing-field", LogSeverity.Failure, new(@"\bMissingFieldException\b", Options)),
        new("type-load", LogSeverity.Failure, new(@"\bTypeLoadException\b", Options)),
        // A mod destroying networked objects the scene still tracks.
        new("nre-remove-objects", LogSeverity.Failure, new(@"\bNullReferenceException\b", Options), new(@"\bZNetScene\.RemoveObjects\b", Options)),
        // The game's warning for a per-object RPC that no component registered (a mod missing on one side, or a typo).
        new("rpc-method-missing", LogSeverity.Warning, new(@"Failed to find rpc method", Options)),
        // The game's warning when a saved object's prefab hash is not registered here (a prefab only the server's mods add),
        // logged again each time the scene tries to create the object; the object is simply not there on this side.
        new("missing-prefab-hash", LogSeverity.Warning, new(@"Missing prefab hash: -?\d+", Options)),
        // Unity's own warning: a prefab from an asset bundle references a script that is not loaded.
        new("missing-script", LogSeverity.Warning, new(@"The referenced script\b.*\bis missing", Options)),
        // Unity: a bundle's shader was not built for this graphics API (magenta objects on Vulkan or OpenGL clients). The
        // wording is as the Valheim-Modding wiki's Valheim-Unity-Project-Guide quotes it.
        new("shader-unsupported", LogSeverity.Warning, new(@"not supported on this GPU|Shader Unsupported\b|Desired shader compiler platform \d+ is not available in shader blob", Options)),
        // The macOS client's own Apple plugins (GameKitWrapper, AppleCoreNativeMac) failing to load at startup: Player.log on
        // Valheim 1.0.16 had these DllNotFoundExceptions in every macOS client run (native arm64 and Rosetta, with or without
        // mods, 30 Sep 2026). The game continues; they are the game's own, so they do not count as UnityException.
        new("macos-apple-plugin-missing", LogSeverity.Warning, new(@"\bDllNotFoundException\b", Options), new(@"\bApple\.(?:GameKit|Core)\.", Options)),
    ];

    /// <summary>Every name a classification may use: the patterns, <see cref="UnityException"/>, <see cref="UnknownWarning"/> and <see cref="UnknownError"/>.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Patterns.Select(pattern => pattern.Name), UnityException, UnknownWarning, UnknownError];

    /// <summary>Refuses a classification of an unknown name or without a written reason.</summary>
    public static void CheckClassifications(IReadOnlyDictionary<string, LogClassification>? classifications)
    {
        foreach (var entry in classifications ?? new Dictionary<string, LogClassification>())
        {
            if (!Names.Contains(entry.Key)) throw new ArgumentException($"Log scan: {entry.Key} is not a known pattern ({string.Join(", ", Names)}).");
            var classification = entry.Value;
            if (classification == null || (classification.Severity is { } severity && !Enum.IsDefined(severity)) || string.IsNullOrWhiteSpace(classification.Reason)
                || (classification.Severity == null && (classification.Expected == null || classification.Expected.Count == 0)))
                throw new ArgumentException($"Log scan: classify {entry.Key} as Failure or Warning, or name the lines it expects, with a written reason.");
            if (classification.Expected != null && classification.Expected.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Log scan: an expected line of {entry.Key} is named by non-empty text.");
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
        var expectedCounts = new int[names.Count];
        var firstExpected = new string?[names.Count];
        var firstFrame = new string?[names.Count];
        int unityException = Patterns.Count, unknownWarning = names.Count - 2, unknownError = names.Count - 1;
        var expected = names.Select(name => classifications != null && classifications.TryGetValue(name, out var chosen) ? chosen.Expected ?? [] : []).ToArray();
        string Text(int line) => lines[line].Length > TextLimit ? lines[line][..TextLimit] + " [truncated]" : lines[line];
        // A UnityException is expected when the named text is in its first line or in one of its frames, because the first
        // line ("NullReferenceException: Object reference not set ...") rarely says which code threw.
        void Hit(int index, int line)
        {
            bool Named(string text) => lines[line].Contains(text, StringComparison.Ordinal)
                || (index == unityException && Frames(lines, line).Any(frame => frame.Contains(text, StringComparison.Ordinal)));
            if (expected[index].Any(Named)) { expectedCounts[index]++; firstExpected[index] ??= Text(line); return; }
            counts[index]++;
            if (first[index] != null) return;
            first[index] = (line + 1, Text(line));
            if (index == unityException) firstFrame[index] = UsefulFrame(lines, line);
        }
        // The current BepInEx record: its header's level and line, and whether a known pattern matched in it.
        string? level = null; int headerLine = -1; bool known = false;
        void EndRecord()
        {
            if (!known && level is "Warning") Hit(unknownWarning, headerLine);
            if (!known && level is "Error" or "Fatal") Hit(unknownError, headerLine);
            level = null; known = false;
        }
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            var header = Header.Match(line);
            if (header.Success) { EndRecord(); level = header.Groups[1].Value; headerLine = i; }
            else if (line.Trim().Length == 0) { EndRecord(); continue; }
            bool named = false;
            for (int p = 0; p < Patterns.Count; p++)
            {
                var pattern = Patterns[p];
                if (!pattern.Line.IsMatch(line) || (pattern.Frame != null && !HasFrame(lines, i, pattern.Frame))) continue;
                Hit(p, i); known = true; named = true;
            }
            // Inside a BepInEx warning or error record the exception is already counted, by a pattern or as unknown. Info,
            // Message and Debug records do not hold exceptions; there an unheadered line is Unity's own, where Player.log
            // or a server's output mixes BepInEx's console lines with Unity's.
            if (!named && !header.Success && level is not ("Warning" or "Error" or "Fatal") && ExceptionLine.IsMatch(line)) Hit(unityException, i);
        }
        EndRecord();
        var result = new List<LogPatternCount>(names.Count);
        for (int n = 0; n < names.Count; n++)
        {
            var severity = n < Patterns.Count ? Patterns[n].Severity : n == unityException ? LogSeverity.Failure : LogSeverity.Warning;
            string? reason = null;
            if (classifications != null && classifications.TryGetValue(names[n], out var chosen)) { severity = chosen.Severity ?? severity; reason = chosen.Reason; }
            result.Add(new(names[n], severity, reason, counts[n], first[n]?.Line, first[n]?.Text, expectedCounts[n], firstExpected[n], firstFrame[n]));
        }
        return new(log.Role, log.Path, true, null, result);
    }

    // The stack trace under line `at`: the lines after it, up to a blank line or the next BepInEx header.
    private static IEnumerable<string> Frames(List<string> lines, int at)
    {
        for (int i = at + 1; i < lines.Count && i <= at + FrameLines; i++)
        {
            if (lines[i].Trim().Length == 0 || Header.IsMatch(lines[i])) yield break;
            yield return lines[i];
        }
    }
    private static bool HasFrame(List<string> lines, int at, Regex frame) => Frames(lines, at).Any(frame.IsMatch);

    // The first frame that names the code that threw, else the first frame; trimmed and cut to the text limit.
    private static string? UsefulFrame(List<string> lines, int at)
    {
        var frames = Frames(lines, at).Select(frame => frame.Trim()).Where(frame => frame.Length != 0).ToList();
        var frame = frames.FirstOrDefault(frame => !RuntimeFrame.IsMatch(frame)) ?? frames.FirstOrDefault();
        return frame == null || frame.Length <= TextLimit ? frame : frame[..TextLimit] + " [truncated]";
    }

    // DirectServerProcess leaves "<copy>.absent" when the game never created the log.
    private static string Missing(RunLog log) => File.Exists(log.Path + ".absent")
        ? $"{log.Role} was never written: the process ended before BepInEx started. Read its Unity player log (Player.log, the -logFile file or its stdout capture) for the reason;" +
          " security software that blocks or quarantines the game or BepInEx's Doorstop loader is a known cause."
        : $"{log.Role} was not kept at {log.Path}: its process was not stopped by its session, which copies the log when it stops.";
}
