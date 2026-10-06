using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Valheim.Testing.Game;
/// <summary>
/// Which of the four reported states a step belongs to. <see cref="Preflight"/>: checks before anything is copied.
/// <see cref="Setup"/>: copying, launching, joining and the barriers until the runtime is ready (a runner's <c>validate</c>
/// mode runs Preflight and the copying Setup steps, and launches nothing). <see cref="Scenario"/>: the mod's exercise and assertions, the default. <see cref="Cleanup"/>:
/// stopping, retiring, restoring and the log scan.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepPhase>))]
[ResultShape]
public enum StepPhase { Preflight, Setup, Scenario, Cleanup }
[ResultShape]
public sealed record StepResult(string Name, bool Passed, double Seconds, string Error, StepPhase Phase = StepPhase.Scenario);
/// <summary>
/// The result of one run: its steps, each in a <see cref="StepPhase"/>, the four states derived from them, provenance, log
/// scans and linked evidence. <see cref="Write"/> writes <c>result.json</c> (schema <see cref="Schema"/>) and <c>junit.xml</c>
/// (one test suite per phase).
/// </summary>
public sealed class ScenarioReport
{
    /// <summary>The <c>result.json</c> schema: 2 since steps carry a phase and the four states are reported.</summary>
    public int Schema => 2;
    private readonly object _stepGate = new();
    public string Name { get; }
    public Dictionary<string, string> Provenance { get; } = new();
    public List<StepResult> Steps { get; } = new();
    /// <summary>The teardown log scans (<see cref="ScanLogs"/>), one per log.</summary>
    public List<LogFileScan> Logs { get; } = new();
    /// <summary>
    /// Every piece of evidence attached with <see cref="Attach(IEvidence)"/> or <see cref="Attach(EvidenceReference)"/>, in
    /// attachment order, filled in by <see cref="Write"/>: snapshots, review stills and clips, each with its SHA-256.
    /// </summary>
    public List<EvidenceReference> Evidence { get; } = new();
    // IEvidence to serialize, or an EvidenceReference already written, with the phase of the step it was attached in.
    private readonly List<(object Item, StepPhase Phase)> _attached = new();
    private StepPhase CurrentPhase => _current.Value ?? StepPhase.Scenario;
    private static readonly Regex EvidenceKind = new("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant);
    internal static bool ValidKind(string? kind) => EvidenceKind.IsMatch(kind ?? "");
    /// <summary>Every step passed, in every phase. A run whose cleanup failed has not passed.</summary>
    public bool Passed => Steps.Count > 0 && Steps.All(x => x.Passed);
    /// <summary>Every <see cref="StepPhase.Preflight"/> step passed (true when there were none).</summary>
    public bool PreflightPassed => PhasePassed(StepPhase.Preflight);
    /// <summary><see cref="PreflightPassed"/> and every <see cref="StepPhase.Setup"/> step passed: the runtime was ready.</summary>
    public bool RuntimeReady => PreflightPassed && PhasePassed(StepPhase.Setup);
    /// <summary><see cref="RuntimeReady"/>, at least one <see cref="StepPhase.Scenario"/> step, and every one passed.</summary>
    public bool ScenarioPassed => RuntimeReady && PhaseRan(StepPhase.Scenario) && PhasePassed(StepPhase.Scenario);
    /// <summary>At least one <see cref="StepPhase.Cleanup"/> step, and every one passed.</summary>
    public bool CleanupVerified => PhaseRan(StepPhase.Cleanup) && PhasePassed(StepPhase.Cleanup);
    private bool PhaseRan(StepPhase phase) { lock (_stepGate) return Steps.Any(x => x.Phase == phase); }
    private bool PhasePassed(StepPhase phase) { lock (_stepGate) return Steps.Where(x => x.Phase == phase).All(x => x.Passed); }
    // The phase of the step running on this logical thread, so evidence attached inside it records that phase.
    private readonly AsyncLocal<StepPhase?> _current = new();
    /// <summary><c>strict</c>, or <c>none</c> once <see cref="MarkNotPinned"/> recorded an explicit opt-out.</summary>
    public string Pinning { get; private set; } = EnvironmentPinning.Strict;
    public ScenarioReport(string name) => Name = name;
    /// <summary>
    /// Records that this run's environment is not pinned, and why: <see cref="Pinning"/> <c>none</c>, the provenance entry
    /// <c>environment</c> ("environment not pinned: ..."), and in <c>junit.xml</c> a suite property and a skipped test case
    /// of that name, so the marker shows wherever the result is read. A pass stays a pass.
    /// </summary>
    public void MarkNotPinned(string why)
    {
        Pinning = EnvironmentPinning.None;
        Provenance["environment"] = EnvironmentPinning.NotPinned + ": " + why;
    }
    /// <summary>Records a <see cref="StepPhase.Scenario"/> step: runs <paramref name="action"/>, records its outcome and time, rethrows its failure.</summary>
    public void Step(string name, Action action) => Step(StepPhase.Scenario, name, action);
    /// <summary>Records a step in <paramref name="phase"/>: runs <paramref name="action"/>, records its outcome and time, rethrows its failure.</summary>
    public void Step(StepPhase phase, string name, Action action)
    {
        var clock = Stopwatch.StartNew();
        var outer = _current.Value;
        _current.Value = phase;
        try { action(); lock (_stepGate) Steps.Add(new(name, true, clock.Elapsed.TotalSeconds, "", phase)); }
        catch (Exception error) { lock (_stepGate) Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, error.Message, phase)); throw; }
        finally { _current.Value = outer; }
    }
    /// <summary>
    /// Attach evidence for the next <see cref="Write"/> to serialize to <c>evidence/&lt;kind&gt;-NNN.json</c> and link from
    /// <c>result.json</c>, such as a <see cref="TerrainSiteSnapshot"/> or <see cref="AreaObjectSnapshot"/> with its exact replies.
    /// </summary>
    public void Attach(IEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!ValidKind(evidence.Kind)) throw new ArgumentException("Evidence kind must be 1-40 lower-case letters, digits or hyphens.", nameof(evidence));
        lock (_stepGate) _attached.Add((evidence, CurrentPhase));
    }
    /// <summary>
    /// Link evidence already written elsewhere, such as <see cref="ReviewCaptureReceipt.Evidence"/> or
    /// <see cref="ReviewClipReceipt.Evidence"/>. <see cref="Write"/> stores its path relative to the report directory when it
    /// lies inside it.
    /// </summary>
    public void Attach(EvidenceReference published)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (!ValidKind(published.Kind) || string.IsNullOrWhiteSpace(published.File) ||
            !Regex.IsMatch(published.Sha256 ?? "", "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Name the evidence kind, its file and the file's lower-case SHA-256.", nameof(published));
        lock (_stepGate) _attached.Add((published, CurrentPhase));
    }
    /// <summary>
    /// Run an assertion, capturing evidence only if it fails. A failed capture is recorded as its own failed step; the
    /// original assertion failure remains the thrown exception.
    /// </summary>
    public void StepWithEvidenceOnFailure(string name, Action assertion, Func<IEvidence> capture)
    {
        try { Step(name, assertion); }
        catch
        {
            var outer = _current.Value;
            _current.Value = StepPhase.Scenario;
            try { Attach(capture()); }
            catch (Exception error) { RecordFailure("capture evidence after " + name, error); }
            finally { _current.Value = outer; }
            throw;
        }
    }
    /// <summary>
    /// The async twin of <see cref="Step"/>: records the awaited action's outcome and time and rethrows its failure. With
    /// <paramref name="failure"/>, the failure is rethrown as an <see cref="InvalidOperationException"/> with that message
    /// and the original as its inner exception.
    /// </summary>
    public Task StepAsync(string name, Func<Task> action, string? failure = null) => StepAsync(StepPhase.Scenario, name, action, failure);
    /// <summary>The async twin of <see cref="Step(StepPhase, string, Action)"/>.</summary>
    public async Task StepAsync(StepPhase phase, string name, Func<Task> action, string? failure = null)
    {
        var clock = Stopwatch.StartNew();
        _current.Value = phase; // An async method's own AsyncLocal changes do not flow back to its caller.
        try { await action().ConfigureAwait(false); lock (_stepGate) Steps.Add(new(name, true, clock.Elapsed.TotalSeconds, "", phase)); }
        catch (Exception error)
        {
            if (failure == null || error is OperationCanceledException) { lock (_stepGate) Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, error.Message, phase)); throw; }
            var wrapped = new InvalidOperationException(failure, error);
            lock (_stepGate) Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, failure + " " + error.Message, phase));
            throw wrapped;
        }
    }
    /// <summary>
    /// Records a failure outside any step (for example in the runner around the scenario), so the report cannot pass. It
    /// goes in the phase of the last recorded step, where the run had got to (<see cref="StepPhase.Preflight"/> before any).
    /// </summary>
    public void RecordFailure(string name, Exception error)
    {
        StepPhase phase;
        lock (_stepGate) phase = Steps.Count == 0 ? StepPhase.Preflight : Steps[^1].Phase;
        RecordFailure(phase, name, error);
    }
    /// <summary>Records a failure in <paramref name="phase"/> outside any step, so the report cannot pass.</summary>
    public void RecordFailure(StepPhase phase, string name, Exception error) { lock (_stepGate) Steps.Add(new(name, false, 0, error.Message, phase)); }
    /// <summary>
    /// Scans a run's logs after its processes stopped (<see cref="LogScanner"/>) and records a <c>scan run logs</c> step:
    /// it fails when a failure pattern matched or a required log is missing, and names each with its first occurrence.
    /// Warnings are counted in <see cref="Logs"/> only. <paramref name="classifications"/> changes a pattern's severity for
    /// this run, with a written reason. It never throws, because it runs at teardown; returns whether the step passed.
    /// </summary>
    public bool ScanLogs(IEnumerable<RunLog> logs, IReadOnlyDictionary<string, LogClassification>? classifications = null)
    {
        try
        {
            Step(StepPhase.Cleanup, "scan run logs", () =>
            {
                var scans = logs.Select(log => LogScanner.Scan(log, classifications)).ToList();
                Logs.AddRange(scans);
                var failures = new List<string>();
                foreach (var scan in scans)
                {
                    if (scan.Problem != null) failures.Add(scan.Problem);
                    foreach (var count in scan.Counts.Where(count => count.Severity == LogSeverity.Failure && count.Count > 0))
                        failures.Add($"{scan.Role}: {count.Pattern} x{count.Count}, first at line {count.FirstLine}: {count.First}" + (count.FirstFrame is { } frame ? $" [{frame}]" : ""));
                }
                if (failures.Count != 0) throw new InvalidOperationException("The run's logs hold failures. " + string.Join(" | ", failures));
            });
            return true;
        }
        catch (Exception) { return false; }
    }
    public void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        Evidence.Clear();
        (object Item, StepPhase Phase)[] attached;
        lock (_stepGate) attached = _attached.ToArray();
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (item, phase) in attached)
        {
            if (item is EvidenceReference published)
            {
                string full = Path.GetFullPath(published.File);
                Evidence.Add(published with { File = full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    ? Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/') : full, Phase = phase });
                continue;
            }
            var evidence = (IEvidence)item;
            int number = numbers[evidence.Kind] = numbers.GetValueOrDefault(evidence.Kind) + 1;
            string file = $"evidence/{evidence.Kind}-{number:D3}.json";
            string path = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, evidence.GetType(), new JsonSerializerOptions { WriteIndented = true }));
            Evidence.Add(new(evidence.Kind, evidence.Site, evidence.WorldUid, file, FileHash.Sha256(path)) { Phase = phase });
        }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        bool unpinned = Pinning != EnvironmentPinning.Strict;
        StepResult[] steps;
        lock (_stepGate) steps = Steps.ToArray();
        // One suite per phase, each named "<report> / <phase>", so a reader sees which state failed without reading names.
        var suites = new XElement("testsuites", new XAttribute("name", Name), new XAttribute("tests", steps.Length + (unpinned ? 1 : 0)),
            new XAttribute("failures", steps.Count(x => !x.Passed)));
        foreach (var phase in Enum.GetValues<StepPhase>())
        {
            var inPhase = steps.Where(x => x.Phase == phase).ToArray();
            bool marker = unpinned && phase == StepPhase.Preflight;
            var suite = new XElement("testsuite", new XAttribute("name", Name + " / " + phase.ToString().ToLowerInvariant()),
                new XAttribute("tests", inPhase.Length + (marker ? 1 : 0)), new XAttribute("failures", inPhase.Count(x => !x.Passed)));
            if (marker)
            {
                string text = Provenance.GetValueOrDefault("environment", EnvironmentPinning.NotPinned);
                suite.Add(new XAttribute("skipped", 1), new XElement("properties", new XElement("property", new XAttribute("name", "environment"), new XAttribute("value", text))),
                    new XElement("testcase", new XAttribute("name", EnvironmentPinning.NotPinned), new XAttribute("time", 0), new XElement("skipped", new XAttribute("message", text))));
            }
            foreach (var step in inPhase) suite.Add(new XElement("testcase", new XAttribute("name", step.Name), new XAttribute("time", step.Seconds), step.Passed ? null : new XElement("failure", step.Error)));
            suites.Add(suite);
        }
        new XDocument(suites).Save(Path.Combine(directory, "junit.xml"));
    }
}
