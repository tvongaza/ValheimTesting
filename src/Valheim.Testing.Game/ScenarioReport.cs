using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Valheim.Testing.Game;
public sealed record StepResult(string Name, bool Passed, double Seconds, string Error);
public sealed class ScenarioReport
{
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
    private readonly List<object> _attached = new(); // IEvidence to serialize, or an EvidenceReference already written
    private static readonly Regex EvidenceKind = new("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant);
    internal static bool ValidKind(string? kind) => EvidenceKind.IsMatch(kind ?? "");
    public bool Passed => Steps.Count > 0 && Steps.All(x => x.Passed);
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
    public void Step(string name, Action action)
    {
        var clock = Stopwatch.StartNew();
        try { action(); lock (_stepGate) Steps.Add(new(name, true, clock.Elapsed.TotalSeconds, "")); }
        catch (Exception error) { lock (_stepGate) Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, error.Message)); throw; }
    }
    /// <summary>
    /// Attach evidence for the next <see cref="Write"/> to serialize to <c>evidence/&lt;kind&gt;-NNN.json</c> and link from
    /// <c>result.json</c>, such as a <see cref="TerrainSiteSnapshot"/> or <see cref="AreaObjectSnapshot"/> with its exact replies.
    /// </summary>
    public void Attach(IEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!ValidKind(evidence.Kind)) throw new ArgumentException("Evidence kind must be 1-40 lower-case letters, digits or hyphens.", nameof(evidence));
        lock (_stepGate) _attached.Add(evidence);
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
        lock (_stepGate) _attached.Add(published);
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
            try { Attach(capture()); }
            catch (Exception error) { RecordFailure("capture evidence after " + name, error); }
            throw;
        }
    }
    /// <summary>
    /// The async twin of <see cref="Step"/>: records the awaited action's outcome and time and rethrows its failure. With
    /// <paramref name="failure"/>, the failure is rethrown as an <see cref="InvalidOperationException"/> with that message
    /// and the original as its inner exception.
    /// </summary>
    public async Task StepAsync(string name, Func<Task> action, string? failure = null)
    {
        var clock = Stopwatch.StartNew();
        try { await action().ConfigureAwait(false); lock (_stepGate) Steps.Add(new(name, true, clock.Elapsed.TotalSeconds, "")); }
        catch (Exception error)
        {
            if (failure == null || error is OperationCanceledException) { lock (_stepGate) Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, error.Message)); throw; }
            var wrapped = new InvalidOperationException(failure, error);
            lock (_stepGate) Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, failure + " " + error.Message));
            throw wrapped;
        }
    }
    /// <summary>Records a failure outside any step (for example in the runner around the scenario), so the report cannot pass.</summary>
    public void RecordFailure(string name, Exception error) { lock (_stepGate) Steps.Add(new(name, false, 0, error.Message)); }
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
            Step("scan run logs", () =>
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
        object[] attached;
        lock (_stepGate) attached = _attached.ToArray();
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var item in attached)
        {
            if (item is EvidenceReference published)
            {
                string full = Path.GetFullPath(published.File);
                Evidence.Add(published with { File = full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    ? Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/') : full });
                continue;
            }
            var evidence = (IEvidence)item;
            int number = numbers[evidence.Kind] = numbers.GetValueOrDefault(evidence.Kind) + 1;
            string file = $"evidence/{evidence.Kind}-{number:D3}.json";
            string path = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, evidence.GetType(), new JsonSerializerOptions { WriteIndented = true }));
            Evidence.Add(new(evidence.Kind, evidence.Site, evidence.WorldUid, file, WorldFixture.Hash(path)));
        }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        bool unpinned = Pinning != EnvironmentPinning.Strict;
        var suite = new XElement("testsuite", new XAttribute("name", Name), new XAttribute("tests", Steps.Count + (unpinned ? 1 : 0)), new XAttribute("failures", Steps.Count(x => !x.Passed)));
        if (unpinned)
        {
            string marker = Provenance.GetValueOrDefault("environment", EnvironmentPinning.NotPinned);
            suite.Add(new XAttribute("skipped", 1), new XElement("properties", new XElement("property", new XAttribute("name", "environment"), new XAttribute("value", marker))),
                new XElement("testcase", new XAttribute("name", EnvironmentPinning.NotPinned), new XAttribute("time", 0), new XElement("skipped", new XAttribute("message", marker))));
        }
        foreach (var step in Steps) suite.Add(new XElement("testcase", new XAttribute("name", step.Name), new XAttribute("time", step.Seconds), step.Passed ? null : new XElement("failure", step.Error)));
        new XDocument(suite).Save(Path.Combine(directory, "junit.xml"));
    }
}
