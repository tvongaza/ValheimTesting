using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace Valheim.Testing.Game;
public sealed record StepResult(string Name, bool Passed, double Seconds, string Error);
public sealed record TerrainSnapshotReference(string Site, string WorldUid, string File, string Sha256);
public sealed class ScenarioReport
{
    public string Name { get; }
    public Dictionary<string, string> Provenance { get; } = new();
    public List<StepResult> Steps { get; } = new();
    /// <summary>The teardown log scans (<see cref="ScanLogs"/>), one per log.</summary>
    public List<LogFileScan> Logs { get; } = new();
    /// <summary>Bounded read-only terrain evidence written beside this report.</summary>
    public List<TerrainSnapshotReference> TerrainSnapshots { get; } = new();
    private readonly List<TerrainSiteSnapshot> _terrainCaptures = new();
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
        try { action(); Steps.Add(new(name, true, clock.Elapsed.TotalSeconds, "")); }
        catch (Exception error) { Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, error.Message)); throw; }
    }
    /// <summary>Attach a site capture to the next <see cref="Write"/>. The capture retains exact ValheimCLI replies.</summary>
    public void AttachTerrainSnapshot(TerrainSiteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _terrainCaptures.Add(snapshot);
    }
    /// <summary>Run an assertion, optionally capturing the site if it fails. A failed capture is recorded separately;
    /// the original assertion failure remains the thrown exception.</summary>
    public void StepWithTerrainOnFailure(string name, Action assertion, Func<TerrainSiteSnapshot> capture)
    {
        try { Step(name, assertion); }
        catch
        {
            try { AttachTerrainSnapshot(capture()); }
            catch (Exception error) { RecordFailure("capture terrain after " + name, error); }
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
        try { await action().ConfigureAwait(false); Steps.Add(new(name, true, clock.Elapsed.TotalSeconds, "")); }
        catch (Exception error)
        {
            if (failure == null || error is OperationCanceledException) { Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, error.Message)); throw; }
            var wrapped = new InvalidOperationException(failure, error);
            Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, failure + " " + error.Message));
            throw wrapped;
        }
    }
    /// <summary>Records a failure outside any step (for example in the runner around the scenario), so the report cannot pass.</summary>
    public void RecordFailure(string name, Exception error) => Steps.Add(new(name, false, 0, error.Message));
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
        TerrainSnapshots.Clear();
        if (_terrainCaptures.Count > 0)
        {
            string captures = Path.Combine(directory, "terrain-snapshots");
            Directory.CreateDirectory(captures);
            for (int i = 0; i < _terrainCaptures.Count; i++)
            {
                string file = $"terrain-snapshots/site-{i + 1:D3}.json";
                string path = Path.Combine(directory, file);
                string content = JsonSerializer.Serialize(_terrainCaptures[i], new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, content);
                TerrainSnapshots.Add(new(_terrainCaptures[i].Site, _terrainCaptures[i].WorldUid, file,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()));
            }
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
