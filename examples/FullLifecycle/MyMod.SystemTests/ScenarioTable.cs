using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// Every scenario of this example by its plan name, in one place (#258 Q5): how it runs on the runner's
/// <see cref="GameSession"/>, whether it marks the plan's sites, and, for the two that run as a campaign, which of the plan's
/// client sections are the campaign's named clients. <see cref="Run"/> is the runner's scenario.
/// </summary>
public static class ScenarioTable
{
    /// <param name="CampaignClients">The plan's client sections by the campaign's client names, for a scenario that runs only as a campaign.</param>
    public sealed record Entry(string Name, Action<GameSession, LifecyclePlan> Run, bool MarksSites = false,
        Func<LifecyclePlan, IReadOnlyDictionary<string, ClientRunPlan>>? CampaignClients = null);

    private static readonly Entry[] Entries =
    [
        new(LifecyclePlan.LifecycleScenario, (session, plan) =>
            // Its logs are scanned with the server's at teardown, after the scenario stops the client, and also after a failed startup.
            DrySiteScenario.Run(plan, session.Server!.Game, session.Server!, () => session.OpenClient(plan.Client!), session.Report, session.Output, session.Cancellation),
            MarksSites: true),
        new(LifecyclePlan.ServerScenario, ServerScenario, MarksSites: true),
        new(LifecyclePlan.WorldScenario, LifecycleWorldScenario.Run, MarksSites: true),
        new(LifecyclePlan.VanillaClientScenario, VanillaClientScenario.Run, MarksSites: true),
        new(LifecyclePlan.SyncedConfigScenario, SyncedConfigScenario.Run),
        new(LifecyclePlan.RefusedJoinScenario, RefusedJoinScenario.Run),
        // The dry-site lifecycle, joined through each boot's crossplay lobby instead of the server's address.
        new(LifecyclePlan.CrossplayScenario, (session, plan) =>
            DrySiteScenario.Run(plan, session.Server!.Game, session.Server!, () => session.OpenClient(plan.Client!), session.Report, session.Output,
                session.Cancellation, session.Server!.Lobby), MarksSites: true),
        new(LifecyclePlan.ContentCensusScenario, ContentCensusScenario.Run),
        new(LifecyclePlan.ReviewCaptureScenarioName, ReviewCaptureScenario.Run),
        new(LifecyclePlan.AreaObjectsScenarioName, AreaObjectsScenario.Run),
        new(LifecyclePlan.OwnershipHandoffScenario, OwnershipHandoffScenario.Run, MarksSites: true, CampaignClients: TwoClients),
        new(LifecyclePlan.ThreeActorScenario, ThreeActorSmokeScenario.Run, CampaignClients: TwoClients),
    ];

    /// <summary>Every scenario's name, in table order.</summary>
    public static IReadOnlyList<string> Names => [.. Entries.Select(entry => entry.Name)];
    /// <summary>
    /// The runner's options for MyMod: its plan rules, its declaration (<see cref="LifecyclePlan.Mod"/>) and provenance, with
    /// <paramref name="scenario"/> as the scenario (<see cref="Run"/> by default). The console runner and the xUnit session
    /// fixture (MyMod.IntegrationTests' <c>MyModSession</c>) use the same options.
    /// </summary>
    public static PinnedServerRunOptions<LifecyclePlan> RunnerOptions(Func<GameSession, LifecyclePlan, Task>? scenario = null) => new()
    {
        Name = "mymod-system-test",
        ReadPlan = path =>
        {
            var plan = LifecyclePlan.ReadValidated(path);
            // Two simultaneous clients are a campaign's actors (an inventory assigns their hosts and Steam identities).
            if (plan.Scenario is LifecyclePlan.ThreeActorScenario or LifecyclePlan.OwnershipHandoffScenario)
                throw new ArgumentException($"The {plan.Scenario} scenario runs as a campaign: campaign run <campaign.json> <plan.json> <new-output-directory>.");
            return plan;
        },
        // The session capability and token variable MyMod's test adapter serves, and its Harmony patches, which the session
        // checks on the server before any scenario step.
        Mod = LifecyclePlan.Mod,
        CheckPlan = plan =>
        {
            if (plan.Client == null && !plan.ServerOnly)
                throw new ArgumentException($"A run looks from a client: add the client section, or use the {LifecyclePlan.ServerScenario} scenario for the server half alone.");
            // A campaign template is bound to its prepared actors in memory; its own rules apply to the bound plan.
            if (plan.Scenario is LifecyclePlan.ThreeActorScenario or LifecyclePlan.OwnershipHandoffScenario) LifecyclePlan.Validated(plan);
        },
        Provenance = (plan, provenance) =>
        {
            provenance["clientMode"] = plan.Client?.Mode ?? "none";
            provenance["humanReview"] = plan.Review.Enabled ? "requested" : "not requested";
            provenance["expectFailure"] = plan.ExpectFailure ?? "none";
        },
        Scenario = scenario ?? Run,
    };

    /// <summary>The campaign's named clients of <paramref name="plan"/>'s scenario; refuses a scenario that does not run as a campaign.</summary>
    public static IReadOnlyDictionary<string, ClientRunPlan> CampaignClientsFor(LifecyclePlan plan) =>
        (Find(plan.Scenario)?.CampaignClients ?? throw new ArgumentException($"This example campaign runs {LifecyclePlan.ThreeActorScenario} or {LifecyclePlan.OwnershipHandoffScenario}."))(plan);

    /// <summary>The scenario named <paramref name="name"/>, or null.</summary>
    public static Entry? Find(string? name) => Entries.FirstOrDefault(entry => entry.Name == name);

    /// <summary>The runner's scenario: the plan's entry on the session. A control run that failed its check as expected ends here, passing.</summary>
    public static Task Run(GameSession session, LifecyclePlan plan)
    {
        var entry = Find(plan.Scenario) ?? throw new ArgumentException($"{plan.Scenario} is not one of this example's scenarios.");
        try { entry.Run(session, plan); }
        catch (ControlConcluded concluded) when (concluded.Control == plan.Control)
        {
            session.Report.Provenance["control"] = concluded.Control.Name + ": failed its check as expected";
        }
        return Task.CompletedTask;
    }

    private static void ServerScenario(GameSession session, LifecyclePlan plan)
    {
        var owned = session.Server!;
        var server = DrySiteServerScenario.Run(plan, owned.Game, owned.Restart, session.Report);
        if (plan.PatchReload == null) return;
        // It writes into the runtime copy's scripts folder, which must be on this machine.
        if (owned.Host != null) throw new ArgumentException("patchReload runs only with the server on this machine (no --inventory).");
        PatchReloadScenario.Run(plan, server, owned.RuntimeDirectory, session.Output, session.Report, session.Cancellation);
    }

    // The example's two client sections, by the campaign's client names.
    private static IReadOnlyDictionary<string, ClientRunPlan> TwoClients(LifecyclePlan plan) => new Dictionary<string, ClientRunPlan>
    {
        ["client-a"] = plan.Client ?? throw new ArgumentException("The campaign template needs a client section."),
        ["client-b"] = plan.SecondClient ?? throw new ArgumentException("The campaign template needs a secondClient section."),
    };
}
