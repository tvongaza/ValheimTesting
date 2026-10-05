using System.Text.Json;
using System.Xml.Linq;
using Valheim.Testing.Game;
using Xunit;

// The four reported states (#303): each step carries the phase it ran in, and the states are derived from them.
public sealed class ScenarioReportPhaseTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("vt-phases-").FullName;
    public void Dispose() => Directory.Delete(_output, true);

    private static ScenarioReport Run(bool setup = true, bool scenario = true, bool cleanup = true)
    {
        var report = new ScenarioReport("phases");
        report.Step(StepPhase.Preflight, "plan and fixtures", () => { });
        Try(() => report.Step(StepPhase.Setup, "start the server", () => { if (!setup) throw new InvalidOperationException("no server"); }));
        Try(() => report.Step("the mod marks the site", () => { if (!scenario) throw new InvalidOperationException("no marker"); }));
        Try(() => report.Step(StepPhase.Cleanup, "stop only owned server", () => { if (!cleanup) throw new InvalidOperationException("still running"); }));
        return report;
        static void Try(Action action) { try { action(); } catch (InvalidOperationException) { } }
    }

    [Fact] public void AScenarioThatPassedWithAFailedCleanupSaysSoAndDoesNotPass()
    {
        var report = Run(cleanup: false);
        Assert.Equal((true, true, true, false, false), (report.PreflightPassed, report.RuntimeReady, report.ScenarioPassed, report.CleanupVerified, report.Passed));
        report.Write(_output);
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "result.json"))).RootElement;
        Assert.Equal(2, result.GetProperty("Schema").GetInt32());
        Assert.Equal((true, true, true, false, false), (result.GetProperty("PreflightPassed").GetBoolean(), result.GetProperty("RuntimeReady").GetBoolean(),
            result.GetProperty("ScenarioPassed").GetBoolean(), result.GetProperty("CleanupVerified").GetBoolean(), result.GetProperty("Passed").GetBoolean()));
        Assert.Equal(new[] { "Preflight", "Setup", "Scenario", "Cleanup" }, result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Phase").GetString()));
    }

    [Fact] public void AFailedSetupIsNotAScenarioResultEvenIfLaterStepsPassed()
    {
        var report = Run(setup: false);
        Assert.Equal((true, false, false, true), (report.PreflightPassed, report.RuntimeReady, report.ScenarioPassed, report.CleanupVerified));
    }

    [Fact] public void NoScenarioStepIsNoScenarioPassedAndNoCleanupStepIsNoCleanupVerified()
    {
        var report = new ScenarioReport("validate");
        report.Step(StepPhase.Preflight, "plan", () => { });
        Assert.True(report.Passed);
        Assert.Equal((true, true, false, false), (report.PreflightPassed, report.RuntimeReady, report.ScenarioPassed, report.CleanupVerified));
    }

    [Fact] public void JUnitHasOneSuitePerPhaseInPhaseOrder()
    {
        var report = Run(scenario: false);
        report.MarkNotPinned("trying it out");
        report.Write(_output);
        var root = XDocument.Load(Path.Combine(_output, "junit.xml")).Root!;
        Assert.Equal("testsuites", root.Name.LocalName);
        Assert.Equal(("5", "1"), (root.Attribute("tests")!.Value, root.Attribute("failures")!.Value));
        var suites = root.Elements("testsuite").ToList();
        Assert.Equal(new[] { "phases / preflight", "phases / setup", "phases / scenario", "phases / cleanup" }, suites.Select(s => s.Attribute("name")!.Value));
        Assert.Equal(new[] { "0", "0", "1", "0" }, suites.Select(s => s.Attribute("failures")!.Value));
        Assert.NotNull(suites[2].Element("testcase")!.Element("failure"));
        // The unpinned marker sits in the Preflight suite only.
        Assert.Equal("1", suites[0].Attribute("skipped")!.Value);
        Assert.All(suites.Skip(1), s => Assert.Null(s.Attribute("skipped")));
    }

    [Fact] public async Task EvidenceRecordsThePhaseOfTheStepItWasAttachedIn()
    {
        var report = new ScenarioReport("evidence");
        string file = Path.Combine(_output, "x.json"); File.WriteAllText(file, "{}");
        EvidenceReference Ref(string kind) => new(kind, "site", "1", file, FileHash.Sha256(file));
        report.Step(StepPhase.Setup, "arrive", () => report.Attach(Ref("arrival")));
        report.Attach(Ref("outside"));
        await report.StepAsync(StepPhase.Cleanup, "collect", () => { report.Attach(Ref("collected")); return Task.CompletedTask; });
        report.Step("measure", () => report.Attach(Ref("reading")));
        report.Write(_output);
        Assert.Equal(new[] { ("arrival", StepPhase.Setup), ("outside", StepPhase.Scenario), ("collected", StepPhase.Cleanup), ("reading", StepPhase.Scenario) },
            report.Evidence.Select(e => (e.Kind, e.Phase)));
        Assert.Equal("Setup", JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "result.json"))).RootElement.GetProperty("Evidence")[0].GetProperty("Phase").GetString());
    }

    [Fact] public void AFailureOutsideAStepGoesWhereTheRunHadGot()
    {
        var early = new ScenarioReport("early");
        early.RecordFailure("plan unreadable", new ArgumentException("bad plan"));
        Assert.Equal(StepPhase.Preflight, Assert.Single(early.Steps).Phase);
        var report = new ScenarioReport("runner");
        report.Step(StepPhase.Setup, "copy the runtime", () => { });
        report.RecordFailure("runner failed", new IOException("session would not start"));
        Assert.Equal(StepPhase.Setup, report.Steps[^1].Phase);
        Assert.Equal((false, false), (report.RuntimeReady, report.ScenarioPassed));
    }

    [Fact] public void AFailureOutsideAStepIsRecordedInItsPhase()
    {
        var report = new ScenarioReport("runner");
        report.RecordFailure(StepPhase.Setup, "runner failed", new IOException("disk full"));
        Assert.Equal(StepPhase.Setup, Assert.Single(report.Steps).Phase);
        Assert.False(report.RuntimeReady);
        Assert.True(report.PreflightPassed);
    }
}
