using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

namespace ExampleMod.Tests;

/// <summary>
/// The scenario's own logic against scripted game replies (<see cref="TestWorld"/>): what passes, what must fail, and that
/// the client is always cleaned up. Nothing here starts Valheim, Steam or a network connection.
/// </summary>
public sealed class MarkerScenarioTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("examplemod-tests-").FullName;
    private readonly TestWorld _world = new();
    private readonly FakeOwnedProcess _process = new(7331);
    public void Dispose() => Directory.Delete(_output, recursive: true);

    private ScenarioReport Run(MarkerPlan plan)
    {
        var report = new ScenarioReport("examplemod-system-test");
        try { MarkerScenario.Run(plan, _world.Server(), _world, () => ClientSession.Launch(plan.Client!, _output, () => _process, () => _world.Client(plan), (_, _) => Task.CompletedTask), report, _output); }
        catch (Exception) { Assert.False(report.Passed); }
        return report;
    }
    private static string[] Failed(ScenarioReport report) => report.Steps.Where(s => !s.Passed).Select(s => s.Name).ToArray();

    [Fact] public void TheWholeLifecyclePassesAndStopsTheOwnedClientOnce()
    {
        var report = Run(_world.Plan());
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Equal(2, _world.MarkCommands); // One per site, never repeated.
        Assert.Equal(1, _world.Restarts);
        Assert.Equal(1, _process.Stops);
        Assert.Contains(report.Steps, s => s.Name == "after-restart: the client sees the marker at the dry site" && s.Passed);
    }

    [Fact] public void AMarkWhoseReplyIsLostFailsAndIsNotRetried()
    {
        _world.LoseMarkReply = true;
        var report = Run(_world.Plan());
        Assert.Equal(new[] { "the mod marks the dry site" }, Failed(report));
        Assert.Equal(1, _world.MarkCommands);
        Assert.Null(_world.ClientTransport); // Nothing after the failure ran, not even the client launch.
    }

    [Fact] public void AnUnconfirmedSaveStopsBeforeTheRestartAndStillStopsTheClient()
    {
        _world.ConfirmSaves = false;
        var report = Run(_world.Plan());
        Assert.Equal(new[] { "confirmed world save" }, Failed(report).Take(1));
        Assert.Equal(0, _world.Restarts);
        Assert.Equal(1, _process.Stops);
    }

    [Fact] public void AMarkerTheClientCannotSeeFailsAndTheOwnedClientIsStopped()
    {
        _world.ClientSeesMarkers = false;
        var report = Run(_world.Plan());
        Assert.Equal("first: the client sees the marker at the dry site", Failed(report).First());
        Assert.Equal(1, _process.Stops);
    }

    // The declaration the session checks at runtime-ready is met by the census the adapter reports; with the patch gone the
    // same check fails, naming it.
    [Fact] public void TheModsDeclarationIsMetByItsAdaptersCensusAndAMissingPatchFails()
    {
        var mod = MarkerPlan.Mod;
        mod.Validate();
        HarmonyCensus.Parse(JsonSerializer.SerializeToElement(TestWorld.ModCensus())).Check(mod.Owner!, mod.Patches).RequireApplied();
        var empty = HarmonyCensus.Parse(JsonSerializer.SerializeToElement(new { source = "harmony-patches", complete = true, owner = mod.Owner, methods = Array.Empty<object>() }));
        Assert.Contains("RegisterCommands::Postfix on Terminal::InitTerminal", Assert.ThrowsAny<Exception>(() => empty.Check(mod.Owner!, mod.Patches).RequireApplied()).Message);
    }

    // The plan agrees with itself before any game starts: a "dry" site under the rule's threshold is refused.
    [Fact] public void APlanWhoseDrySiteIsWetIsRefused()
    {
        var plan = _world.Plan();
        plan.DrySite.Ground = 30f;
        Assert.Contains("dry site", Assert.Throws<ArgumentException>(plan.CheckSites).Message);
    }

    // The sample a mod author starts from is this plan's shape (unknown fields are refused when read) and its sites agree with the rule.
    [Fact] public void TheSamplePlanIsAMarkerPlanWithConsistentSites()
    {
        var plan = ServerRunPlan.Read<MarkerPlan>(Path.Combine(AppContext.BaseDirectory, "sample-plan.json"));
        Assert.Equal(MarkerPlan.ScenarioName, plan.Scenario);
        Assert.Equal("absent", plan.Client!.Pins[MarkerPlan.ModPlugin]);
        plan.CheckSites();
    }

    // The native session has one client, the plan's client section, and is skipped here without session.json.
    [Fact] public void TheNativeSessionsOneClientIsThePlansClientSection()
    {
        var plan = _world.Plan();
        Assert.Equal("client", Assert.Single(ExampleSession.Clients(plan)).Key);
        plan.Client = null;
        Assert.Contains("needs its client section", Assert.Throws<ArgumentException>(() => ExampleSession.Clients(plan)).Message);
        if (!File.Exists(ExampleSession.Manifest)) Assert.Contains("No session.json", ExampleSession.Missing);
    }
}
