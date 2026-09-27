using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace Valheim.Testing.Game;
public sealed record StepResult(string Name, bool Passed, double Seconds, string Error);
public sealed class ScenarioReport
{
    public string Name { get; }
    public Dictionary<string, string> Provenance { get; } = new();
    public List<StepResult> Steps { get; } = new();
    public bool Passed => Steps.Count > 0 && Steps.All(x => x.Passed);
    public ScenarioReport(string name) => Name = name;
    public void Step(string name, Action action)
    {
        var clock = Stopwatch.StartNew();
        try { action(); Steps.Add(new(name, true, clock.Elapsed.TotalSeconds, "")); }
        catch (Exception error) { Steps.Add(new(name, false, clock.Elapsed.TotalSeconds, error.Message)); throw; }
    }
    public void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        var suite = new XElement("testsuite", new XAttribute("name", Name), new XAttribute("tests", Steps.Count), new XAttribute("failures", Steps.Count(x => !x.Passed)));
        foreach (var step in Steps) suite.Add(new XElement("testcase", new XAttribute("name", step.Name), new XAttribute("time", step.Seconds), step.Passed ? null : new XElement("failure", step.Error)));
        new XDocument(suite).Save(Path.Combine(directory, "junit.xml"));
    }
}
