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

    private ScenarioReport RunPair(MarkerPlan plan, bool aliasActors = false)
    {
        var report = new ScenarioReport("examplemod-two-clients");
        try
        {
            MarkerScenario.RunPair(plan, _world.Server(), () =>
            {
                var first = _world.Client(plan).Actor("A");
                return new Dictionary<string, GameActor>
                {
                    [MarkerPlan.FirstClientName] = first,
                    [MarkerPlan.SecondClientName] = aliasActors ? first : _world.Client(plan, second: true).Actor("B"),
                };
            }, () => { }, report, _output);
        }
        catch (Exception) { Assert.False(report.Passed); }
        return report;
    }

    [Fact] public void BothClientsSeeTheMarkerAndBRejoinsWhileAStays()
    {
        var report = RunPair(_world.PairPlan());
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Equal(2, _world.MarkCommands);
        Assert.Equal(0, _world.Restarts); // This mode proves visibility, not persistence.
        Assert.Equal(1, _world.ClientTransport!.Count("cli_extension valheim.session/join"));
        Assert.Equal(2, _world.SecondClientTransport!.Count("cli_extension valheim.session/join"));
        Assert.Equal(1, _world.ClientTransport.Count("cli_extension valheim.session/leave"));
        Assert.Equal(2, _world.SecondClientTransport.Count("cli_extension valheim.session/leave"));
        Assert.Contains(report.Steps, s => s.Name == "A remains pinned in the world while B is away" && s.Passed);
        Assert.Contains(report.Steps, s => s.Name == "B sees exactly one marker after rejoin" && s.Passed);
    }

    [Theory]
    [InlineData(false, true, "lists 0")]
    [InlineData(true, false, "Incomplete client observation")]
    public void BMustIndependentlyObserveACompleteMarkerCensus(bool visible, bool complete, string error)
    {
        _world.SecondClientSeesMarkers = visible;
        _world.SecondObservationComplete = complete;
        var report = RunPair(_world.PairPlan());
        Assert.Contains(report.Steps, s => s.Name == "A sees exactly one marker" && s.Passed);
        var failure = Assert.Single(report.Steps.Where(s => !s.Passed));
        Assert.Equal("B independently sees exactly one marker", failure.Name);
        Assert.Contains(error, failure.Error);
        Assert.Equal(1, _world.SecondClientTransport!.Count("cli_extension valheim.session/join"));
    }

    [Fact] public void TwoNamesCannotStandForTheSameClientActor()
    {
        var report = RunPair(_world.PairPlan(), aliasActors: true);
        Assert.Equal(new[] { "open both pinned clients on their assigned hosts" }, Failed(report));
        Assert.Equal(0, _world.ClientTransport!.Count("cli_extension valheim.session/join"));
    }

    [Fact] public void TheNamedMapRetainsBothOriginalPlanInstancesAndRejectsAnAlias()
    {
        var plan = _world.PairPlan();
        var clients = ExampleSession.Clients(plan);
        Assert.Same(plan.Client, clients[MarkerPlan.FirstClientName]);
        Assert.Same(plan.SecondClient, clients[MarkerPlan.SecondClientName]);
        Assert.Equal(2, clients.Count);
        plan.SecondClient = plan.Client;
        Assert.Contains("separate plan objects", Assert.Throws<ArgumentException>(() => MarkerPlan.Clients(plan)).Message);
    }

    [Theory]
    [InlineData("lifecycle-pair")]
    [InlineData("visibility-single")]
    [InlineData("missing-arrival")]
    [InlineData("missing-client")]
    public void ScenarioModeAndSecondClientPairMustAgreeBeforeTheModActs(string invalid)
    {
        var plan = _world.PairPlan();
        if (invalid == "lifecycle-pair") plan.Scenario = MarkerPlan.ScenarioName;
        if (invalid == "visibility-single") { plan.SecondClient = null; plan.SecondArrival = null; }
        if (invalid == "missing-arrival") plan.SecondArrival = null;
        if (invalid == "missing-client") plan.SecondClient = null;
        Assert.Throws<ArgumentException>(() => MarkerScenario.RunPair(plan, _world.Server(),
            () => throw new InvalidOperationException("must not open"), () => { }, new ScenarioReport("invalid"), _output));
        Assert.Equal(0, _world.MarkCommands);
    }

    [Theory]
    [InlineData("near-A")]
    [InlineData("far-marker")]
    [InlineData("wet")]
    [InlineData("nonfinite")]
    [InlineData("same-character")]
    [InlineData("different-server")]
    [InlineData("mod-present")]
    [InlineData("attached")]
    public void SecondClientInputsMustMatchTheObservationContract(string invalid)
    {
        var plan = _world.PairPlan();
        if (invalid == "near-A") plan.SecondArrival!.X = plan.Arrival.X + 1;
        if (invalid == "far-marker") plan.SecondArrival!.X = plan.DrySite.X + 21;
        if (invalid == "wet") plan.SecondArrival!.Ground = 30;
        if (invalid == "nonfinite") plan.SecondArrival!.X = float.NaN;
        if (invalid == "same-character") plan.SecondClient!.Character = plan.Client!.Character;
        if (invalid == "different-server") plan.SecondClient!.Join = "different:2456";
        if (invalid == "mod-present") plan.SecondClient!.Pins[MarkerPlan.ModPlugin] = new string('a', 32);
        if (invalid == "attached") plan.SecondClient!.Mode = "attach";
        Assert.Throws<ArgumentException>(plan.CheckSites);
        Assert.Equal(0, _world.MarkCommands);
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

    [Fact] public void AnOptionalReviewStillRequiresItsClientHostBeforeTheModActs()
    {
        var plan = _world.Plan();
        plan.ReviewStill = new ReviewStillSettings { HostDirectory = OperatingSystem.IsWindows() ? @"C:\review-example" : "/review-example", GameBuild = "1.0.17" };
        plan.ReviewStill.Validate();
        var report = new ScenarioReport("examplemod-system-test");
        Assert.Contains("client's IGameHost", Assert.Throws<ArgumentException>(() => MarkerScenario.Run(
            plan, _world.Server(), _world, () => throw new InvalidOperationException("the client must not open"),
            report, _output)).Message);
        Assert.Equal(0, _world.MarkCommands);
        Assert.Empty(report.Steps);
    }

    [Fact] public void TheHostedSampleKeepsTheArrivalBesideItsMarker()
    {
        // The sample has placeholders for private install hashes, so check only its public site contract here.
        var plan = JsonSerializer.Deserialize<HostedMarkerPlan>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "sample-hosted-plan.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        MarkerPlan.CheckSites(plan.DrySite, plan.WetSite, plan.Arrival);
        plan.Arrival.X = plan.DrySite.X;
        plan.Arrival.Z = plan.DrySite.Z;
        Assert.Contains("beside the marker", Assert.Throws<ArgumentException>(() => MarkerPlan.CheckSites(
            plan.DrySite, plan.WetSite, plan.Arrival)).Message);
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
