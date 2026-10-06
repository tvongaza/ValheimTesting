using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>A failure fails the run's log scan; a warning is counted and reported only.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogSeverity>))]
[ResultShape]
public enum LogSeverity { Failure, Warning }

/// <summary>When a pattern applies: the teardown scan of a run's logs, an owned process's startup, or both.</summary>
[Flags]
[ResultShape]
public enum LogPhase { Teardown = 1, Startup = 2 }

/// <summary>
/// A known log problem. <see cref="Line"/> matches one line; with <see cref="Frame"/>, the line counts only when a line of
/// its stack trace (up to a blank line, the next log message or the next exception) matches the frame. A
/// <see cref="LogPhase.Startup"/> pattern also ends an owned startup at once (<see cref="StartupEvents.RuntimeLoadFailures"/>);
/// startup matching reads its <see cref="Line"/> only, so a startup pattern has no <see cref="Frame"/>.
/// </summary>
[ResultShape]
public sealed record LogPattern(string Name, LogSeverity Severity, Regex Line, Regex? Frame = null, LogPhase Phase = LogPhase.Teardown);

/// <summary>
/// A run's own classification of a known pattern, with the written reason it differs from the default: another severity,
/// named lines the run expects, or both. Under a name that is not built in, it is a pattern of the run's own: its
/// <see cref="Line"/> (and optional <see cref="Frame"/>) regex and its <see cref="Severity"/>, for a mod's own known-bad line
/// or another mod's known noise, counted like the built-in patterns at teardown.
/// </summary>
[ResultShape]
public sealed class LogClassification
{
    /// <summary>
    /// For a pattern of the run's own (a name that is not built in): the regex a line must match (.NET syntax,
    /// culture-invariant, ordinal). A built-in pattern's regex cannot be replaced.
    /// </summary>
    public string? Line { get; set; }
    /// <summary>For a pattern of the run's own: a regex one of the matching line's stack frames must match, as <see cref="LogPattern.Frame"/>.</summary>
    public string? Frame { get; set; }
    /// <summary><c>Failure</c> or <c>Warning</c>; absent keeps the pattern's default.</summary>
    public LogSeverity? Severity { get; set; }
    /// <summary>
    /// Texts that name lines this run expects, such as a lookup a mod makes on purpose: a line of the pattern containing
    /// one of them (ordinal) is counted as <see cref="LogPatternCount.Expected"/> and never fails the scan. For
    /// <see cref="LogScanner.UnknownError"/>, each text must be the entire BepInEx Error or Fatal header line and is
    /// matched exactly. Every other line of the pattern still counts.
    /// </summary>
    public List<string> Expected { get; set; } = [];
    public string Reason { get; set; } = "";
}

/// <summary>
/// A log file a run's teardown scan reads. <paramref name="Required"/>: its absence is a failure (a BepInEx log);
/// otherwise it is recorded as absent (a Unity log that was not redirected there).
/// </summary>
[ResultShape]
public sealed record RunLog(string Role, string Path, bool Required = false);

/// <summary>
/// How often a pattern matched in one log and where first; <see cref="Reason"/> is set when the run reclassified it.
/// Lines the run named as expected are counted apart in <see cref="Expected"/> (the first in <see cref="FirstExpected"/>)
/// and never fail. <see cref="FirstFrame"/> is the most useful stack frame under the first counted line, for
/// <see cref="LogScanner.UnityException"/>: the first frame outside the runtime's own System, Mono and wrapper frames.
/// </summary>
[ResultShape]
public sealed record LogPatternCount(string Pattern, LogSeverity Severity, string? Reason, int Count, int? FirstLine, string? First, int Expected = 0, string? FirstExpected = null, string? FirstFrame = null);

/// <summary>
/// One log's scan. An absent log has no counts (absent is not zero); <see cref="Problem"/> says why a required one is
/// missing. <see cref="Failed"/> when a required log is missing or a failure pattern matched.
/// </summary>
[ResultShape]
public sealed record LogFileScan(string Role, string Path, bool Present, string? Problem, IReadOnlyList<LogPatternCount> Counts)
{
    public bool Failed => Problem != null || Counts.Any(count => count.Severity == LogSeverity.Failure && count.Count > 0);
}

/// <summary>
/// Scans a whole run's logs after its processes stopped, for problems that only warn or appear long after startup: a
/// Harmony patch whose target is gone, a global unpatch, missing members after a game update, errors while objects unload,
/// RPCs without a handler, objects whose prefab is not registered, missing scripts and shaders a GPU cannot run; a dedicated
/// server's own graphics errors are known too. Each known pattern is counted per log with its
/// first occurrence and has a default severity a run may change with a written reason. BepInEx warning and error lines
/// that match no pattern are counted as <see cref="UnknownWarning"/> and <see cref="UnknownError"/>, never ignored.
/// Unknown errors fail by default; a run can name an exact expected header line with a written reason. Unity's
/// Player.log has no levels; there an exception Unity printed (a line that starts with the exception's type name, outside
/// a BepInEx warning or error record) that no known pattern names is counted as <see cref="UnityException"/>, which fails.
/// The same Unity message may appear in both logs.
/// Owned processes are asked to quit at teardown and killed only if they do not (<see cref="IOwnedProcess.StopCleanly"/>):
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
    // A timestamped Unity line in a mixed console log starts a new message even without a blank separator.
    private static readonly Regex UnityTimestamp = new(@"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2}:", Options);
    // How Unity prints an exception it caught: the type name at the start of the line, then ": message" or nothing, for
    // example "NullReferenceException: Object reference not set to an instance of an object". Its frames follow, indented.
    private static readonly Regex ExceptionLine = new(@"^(?:[A-Za-z_]\w*\.)*\w*Exception(?::|$)", Options);
    // Frames that say where the runtime was, not which code threw: skipped when choosing FirstFrame.
    private static readonly Regex RuntimeFrame = new(@"^\s*(?:at\s+)?(?:\(wrapper |System\.|Mono\.)", Options);

    private static readonly LogPattern[] Problems =
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
        // The runtime's assemblies do not fit the game: a leftover preloader patcher or a mod built for another game version.
        // At startup they also end an owned process's start at once (StartupEvents.RuntimeLoadFailures uses these regexes).
        new("missing-method", LogSeverity.Failure, new(@"\bMissingMethodException\b", Options), Phase: LogPhase.Teardown | LogPhase.Startup),
        new("missing-field", LogSeverity.Failure, new(@"\bMissingFieldException\b", Options), Phase: LogPhase.Teardown | LogPhase.Startup),
        new("type-load", LogSeverity.Failure, new(@"\bTypeLoadException\b", Options), Phase: LogPhase.Teardown | LogPhase.Startup),
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
    ];

    /// <summary>
    /// The game's own lines on a given platform that are not a mod's problem, each measured on the game version its comment
    /// names. A run can allow a newer one from its plan without a toolkit release: a <see cref="LogClassification"/> with a
    /// new name, a <see cref="LogClassification.Line"/> and <see cref="LogSeverity.Warning"/>.
    /// </summary>
    public static IReadOnlyList<LogPattern> KnownGameNoise { get; } =
    [        // The macOS game's own Apple plugins (GameKitWrapper, AppleCoreNativeMac) failing to load at startup: these
        // DllNotFoundExceptions were in the Unity log of every macOS client and dedicated server run kept from 18 to 30 Sep
        // 2026 (Valheim 1.0.16, native arm64 and Rosetta, with or without mods). The game continues; they are the game's own,
        // so they do not count as UnityException.
        new("macos-apple-plugin-missing", LogSeverity.Warning, new(@"\bDllNotFoundException\b", Options), new(@"\bApple\.(?:GameKit|Core)\.", Options)),
        // Unity's and the game's errors on a dedicated server's null graphics device ("GPU Device: 0000:0000"): no video
        // shaders for the intro cinematic and no GPU for asset uploads. Every kept dedicated server boot from 26 Sep to 2 Oct
        // 2026 (Valheim 1.0.16, Unity 6000.0.75f1; Windows, Linux and macOS, with or without mods) logged 12 to 14 of them, and
        // no client log had one. Linux and macOS servers write them to the BepInEx log as Unity Log errors, which fail as
        // unknown-error otherwise; the Windows server writes them only to its Unity log. Matched by their whole text, so
        // another error from the same systems still counts. Only an unprefixed Unity line or a Unity Log error record
        // may match; a mod's own BepInEx error ending in the same words remains unknown-error.
        new("headless-server-graphics", LogSeverity.Warning, new(@"^(?:\[Error\s+:\s*Unity Log\]\s*)?(?:AsyncResourceUpload failed\.|This custom render path shader needs to have at least 1 passes\."
            + @"|Could not find material Hidden/Video(?:Decode|Composite)\. Make sure the Video shaders are included in your build, in the Built-in Shader Settings section of the Graphics Settings\."
            + @"|Could not find video decode shader pass \w+ in shader <not found>|\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2}: Failed to play intro cinematic)$", Options)),
    ];

    /// <summary>The known patterns and their default severities: the problems, then <see cref="KnownGameNoise"/>.</summary>
    public static IReadOnlyList<LogPattern> Patterns { get; } = [.. Problems, .. KnownGameNoise];

    /// <summary>
    /// The built-in names a classification may use: the patterns, <see cref="UnityException"/>, <see cref="UnknownWarning"/>
    /// and <see cref="UnknownError"/>. Any other name defines a pattern of the run's own (<see cref="LogClassification.Line"/>).
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = [.. Patterns.Select(pattern => pattern.Name), UnityException, UnknownWarning, UnknownError];

    /// <summary>
    /// Refuses a classification without a written reason, a built-in pattern given a regex, and a pattern of the run's own
    /// without a line regex or a severity, or whose regex does not parse.
    /// </summary>
    public static void CheckClassifications(IReadOnlyDictionary<string, LogClassification>? classifications) => RunPatterns(classifications);

    // The run's own patterns, after checking every classification; a user regex gets a match timeout.
    private static List<LogPattern> RunPatterns(IReadOnlyDictionary<string, LogClassification>? classifications)
    {
        var own = new List<LogPattern>();
        foreach (var entry in classifications ?? new Dictionary<string, LogClassification>())
        {
            var classification = entry.Value;
            if (string.IsNullOrWhiteSpace(entry.Key)) throw new ArgumentException("Log scan: a pattern needs a name.");
            if (Names.Contains(entry.Key))
            {
                if (classification?.Line != null || classification?.Frame != null)
                    throw new ArgumentException($"Log scan: {entry.Key} is built in; its regex cannot be replaced. Give a pattern of your own a new name.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(classification?.Line) || classification.Severity == null || string.IsNullOrWhiteSpace(classification.Reason))
                    throw new ArgumentException($"Log scan: {entry.Key} is not a built-in pattern ({string.Join(", ", Names)}); a pattern of your own needs a line regex, a severity and a written reason.");
                own.Add(new(entry.Key, classification.Severity.Value, UserRegex(entry.Key, "line", classification.Line),
                    classification.Frame == null ? null : UserRegex(entry.Key, "frame", classification.Frame)));
            }
            if (classification == null || (classification.Severity is { } severity && !Enum.IsDefined(severity)) || string.IsNullOrWhiteSpace(classification.Reason)
                || (classification.Severity == null && (classification.Expected == null || classification.Expected.Count == 0)))
                throw new ArgumentException($"Log scan: classify {entry.Key} as Failure or Warning, or name the lines it expects, with a written reason.");
            if (classification.Expected != null && classification.Expected.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Log scan: an expected line of {entry.Key} is named by non-empty text.");
            if (entry.Key == UnknownError && classification.Severity == LogSeverity.Warning)
                throw new ArgumentException("Log scan: unknown-error cannot be downgraded as a category; name each exact expected Error or Fatal header line with a written reason.");
            if (entry.Key == UnknownError && classification.Expected != null && classification.Expected.Any(line =>
                    Header.Match(line) is not { Success: true } match || match.Groups[1].Value is not ("Error" or "Fatal")))
                throw new ArgumentException("Log scan: unknown-error needs the exact expected BepInEx Error or Fatal header line.");
        }
        return own;
    }

    private static Regex UserRegex(string name, string field, string text)
    {
        try { return new Regex(text, Options, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException e) { throw new ArgumentException($"Log scan: {name}'s {field} regex does not parse: {e.Message}"); }
    }

    /// <summary>Counts every pattern in one log. A missing log has no counts; a missing required log is a <see cref="LogFileScan.Problem"/>.</summary>
    public static LogFileScan Scan(RunLog log, IReadOnlyDictionary<string, LogClassification>? classifications = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        var patterns = Patterns.Where(pattern => pattern.Phase.HasFlag(LogPhase.Teardown)).Concat(RunPatterns(classifications)).ToList();
        if (!File.Exists(log.Path)) return new(log.Role, log.Path, false, log.Required ? Missing(log) : null, []);
        List<string> lines;
        using (var reader = new StreamReader(new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            lines = [];
            for (string? line; (line = reader.ReadLine()) != null;) lines.Add(line);
        }
        var names = patterns.Select(pattern => pattern.Name).Concat([UnityException, UnknownWarning, UnknownError]).ToList();
        var counts = new int[names.Count];
        var first = new (int Line, string Text)?[names.Count];
        var expectedCounts = new int[names.Count];
        var firstExpected = new string?[names.Count];
        var firstFrame = new string?[names.Count];
        int unityException = patterns.Count, unknownWarning = names.Count - 2, unknownError = names.Count - 1;
        var expected = names.Select(name => classifications != null && classifications.TryGetValue(name, out var chosen) ? chosen.Expected ?? [] : []).ToArray();
        string Text(int line) => lines[line].Length > TextLimit ? lines[line][..TextLimit] + " [truncated]" : lines[line];
        // A UnityException is expected when the named text is in its first line or in one of its frames, because the first
        // line ("NullReferenceException: Object reference not set ...") rarely says which code threw.
        void Hit(int index, int line)
        {
            bool Named(string text) => (index == unknownError ? lines[line].Equals(text, StringComparison.Ordinal) : lines[line].Contains(text, StringComparison.Ordinal))
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
            else if (UnityTimestamp.IsMatch(line)) EndRecord();
            else if (line.Trim().Length == 0) { EndRecord(); continue; }
            bool named = false;
            for (int p = 0; p < patterns.Count; p++)
            {
                var pattern = patterns[p];
                try { if (!pattern.Line.IsMatch(line) || (pattern.Frame != null && !HasFrame(lines, i, pattern.Frame))) continue; }
                catch (RegexMatchTimeoutException) { throw new InvalidOperationException($"Log scan: {pattern.Name}'s regex took over {pattern.Line.MatchTimeout.TotalSeconds:0.#} s on line {i + 1} of {log.Role}; simplify it."); }
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
            var severity = n < patterns.Count ? patterns[n].Severity : n == unityException || n == unknownError ? LogSeverity.Failure : LogSeverity.Warning;
            string? reason = null;
            if (classifications != null && classifications.TryGetValue(names[n], out var chosen)) { severity = chosen.Severity ?? severity; reason = chosen.Reason; }
            result.Add(new(names[n], severity, reason, counts[n], first[n]?.Line, first[n]?.Text, expectedCounts[n], firstExpected[n], firstFrame[n]));
        }
        return new(log.Role, log.Path, true, null, result);
    }

    // The stack trace under line `at`: stop before another log message or exception, even without a blank separator.
    private static IEnumerable<string> Frames(List<string> lines, int at)
    {
        for (int i = at + 1; i < lines.Count && i <= at + FrameLines; i++)
        {
            if (lines[i].Trim().Length == 0 || Header.IsMatch(lines[i]) || UnityTimestamp.IsMatch(lines[i]) || ExceptionLine.IsMatch(lines[i])) yield break;
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
