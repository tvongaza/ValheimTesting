using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace ExampleMod.Tests;

/// <summary>A local hosting client, rather than a dedicated server and joined client, tests the same marker rule.</summary>
public sealed class HostedMarkerPlan
{
    public const string ScenarioName = "hosted-dry-site-lifecycle";
    public string Scenario { get; set; } = "";
    public ClientRunPlan Client { get; set; } = new();
    public Site DrySite { get; set; } = new();
    public Site WetSite { get; set; } = new();
    public Site Arrival { get; set; } = new();

    public static HostedMarkerPlan ReadValidated(string path) => Validated(JsonSerializer.Deserialize<HostedMarkerPlan>(
        File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        }) ?? throw new ArgumentException("The hosted plan is empty."));

    public static HostedMarkerPlan Validated(HostedMarkerPlan plan)
    {
        if (plan.Scenario != ScenarioName) throw new ArgumentException("Use the hosted-dry-site-lifecycle scenario.");
        if (plan.Client.HostWorld == null) throw new ArgumentException("The client needs hostWorld for its disposable fixture.");
        if (!plan.Client.Owned) throw new ArgumentException("The hosted example needs an owned client so its character and world can be retired after the run.");
        plan.Client.Validate();
        foreach (string plugin in new[] { MarkerPlan.ModPlugin, MarkerPlan.AdapterPlugin })
            if (!plan.Client.Pins.TryGetValue(plugin, out string? pin) || pin == "absent")
                throw new ArgumentException($"The hosting client must pin {plugin}.");
        MarkerPlan.CheckSites(plan.DrySite, plan.WetSite, plan.Arrival);
        return plan;
    }
}

/// <summary>Copyable local-host run: the toolkit places and journals the host world through <see cref="HostingClientActor"/>.</summary>
public static class HostedMarkerScenario
{
    public static HostedRunOptions<HostedMarkerPlan> RunnerOptions => new()
    {
        Name = "examplemod-hosted-test",
        ReadPlan = HostedMarkerPlan.ReadValidated,
        CheckPlan = plan => HostedMarkerPlan.Validated(plan),
        Host = plan => plan.Client,
        Mod = MarkerPlan.Mod,
        Scenario = Run,
    };

    public static Task Run(GameSession session, HostedMarkerPlan plan)
    {
        var owner = session.Host ?? throw new ArgumentException("This scenario needs the local hosting-client actor.");
        var report = session.Report;
        var host = owner.Game; // The same actor is client and server of this world.
        report.Step("host: no marker at either site", () =>
        {
            MarkerScenario.RequireServerMarkers(host, plan.DrySite, 0);
            MarkerScenario.RequireServerMarkers(host, plan.WetSite, 0);
        });
        report.Step("host: mark dry site", () => host.Execute(MarkerScenario.Mark(plan.DrySite)).RequireLine("OK: marked ", "the host did not mark the dry site"));
        report.Step("host: refuse wet site", () => host.Execute(MarkerScenario.Mark(plan.WetSite)).RequireLine("REFUSED: ", "the host did not refuse the wet site"));
        report.Step("host: arrive beside the dry marker", () => PlayerPlacement.Arrive(null, host,
            new HeightExpectation(plan.Arrival.X, plan.Arrival.Z, plan.Arrival.Ground),
            TimeSpan.FromSeconds(plan.Client.ArrivalSeconds), session.Cancellation, loadedGround: true));
        report.Step("host: marker visible before save", () => MarkerScenario.RequireClientMarkers(host, plan.DrySite, 1));
        report.Step("host: confirmed save", () => new SessionControl(host).Save(owner.WorldUid,
            TimeSpan.FromSeconds(plan.Client.HostWorld!.SaveSeconds)));
        report.Step("host: leave and rehost the same fixture", () => host = owner.Restart());
        report.Step("host: return beside the dry marker", () => PlayerPlacement.Arrive(null, host,
            new HeightExpectation(plan.Arrival.X, plan.Arrival.Z, plan.Arrival.Ground),
            TimeSpan.FromSeconds(plan.Client.ArrivalSeconds), session.Cancellation, loadedGround: true));
        report.Step("host: saved marker visible after rehost", () =>
        {
            MarkerScenario.RequireServerMarkers(host, plan.DrySite, 1);
            MarkerScenario.RequireServerMarkers(host, plan.WetSite, 0);
            MarkerScenario.RequireClientMarkers(host, plan.DrySite, 1);
        });
        return Task.CompletedTask;
    }
}
