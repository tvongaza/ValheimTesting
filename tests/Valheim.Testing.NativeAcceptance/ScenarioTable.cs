using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>
/// Every scenario of the native acceptance suite by its plan name, in one place (#258 Q5): how it runs on the runner's
/// <see cref="GameSession"/>, whether it marks the plan's sites, and, for the two that run as a campaign, which of the plan's
/// client sections are the campaign's named clients. <see cref="Run"/> is the runner's scenario.
/// </summary>
public static class ScenarioTable
{
    /// <param name="CampaignClients">The plan's client sections by the campaign's client names, for a scenario that runs only as a campaign.</param>
    public sealed record Entry(string Name, Action<GameSession, AcceptancePlan> Run, bool MarksSites = false,
        Func<AcceptancePlan, IReadOnlyDictionary<string, ClientRunPlan>>? CampaignClients = null);

    private static readonly Entry[] Entries =
    [
        new(AcceptancePlan.LifecycleScenario, (session, plan) =>
            // Its logs are scanned with the server's at teardown, after the scenario stops the client, and also after a failed startup.
            DrySiteScenario.Run(plan, session.Server!.Game, session.Server!, () => session.OpenClient(plan.Client!), session.Report, session.Output, session.Cancellation),
            MarksSites: true),
        new(AcceptancePlan.ServerScenario, ServerScenario, MarksSites: true),
        new(AcceptancePlan.WorldScenario, LifecycleWorldScenario.Run, MarksSites: true),
        new(AcceptancePlan.VanillaClientScenario, VanillaClientScenario.Run, MarksSites: true),
        new(AcceptancePlan.SyncedConfigScenario, SyncedConfigScenario.Run),
        new(AcceptancePlan.RefusedJoinScenario, RefusedJoinScenario.Run),
        // The dry-site lifecycle, joined through each boot's crossplay lobby instead of the server's address.
        new(AcceptancePlan.CrossplayScenario, (session, plan) =>
            DrySiteScenario.Run(plan, session.Server!.Game, session.Server!, () => session.OpenClient(plan.Client!), session.Report, session.Output,
                session.Cancellation, session.Server!.Lobby), MarksSites: true),
        new(AcceptancePlan.ContentCensusScenario, ContentCensusScenario.Run),
        new(AcceptancePlan.ReviewCaptureScenarioName, ReviewCaptureScenario.Run),
        new(AcceptancePlan.AreaObjectsScenarioName, AreaObjectsScenario.Run),
        new(AcceptancePlan.OwnershipHandoffScenario, OwnershipHandoffScenario.Run, MarksSites: true, CampaignClients: TwoClients),
        new(AcceptancePlan.ThreeActorScenario, ThreeActorSmokeScenario.Run, CampaignClients: TwoClients),
    ];

    /// <summary>Every scenario's name, in table order.</summary>
    public static IReadOnlyList<string> Names => [.. Entries.Select(entry => entry.Name)];
    /// <summary>
    /// The runner's options for the suite: its plan rules, AcceptanceMod's declaration (<see cref="LifecyclePlan.Mod"/>) and provenance,
    /// with <paramref name="scenario"/> as the scenario (<see cref="Run"/> by default). The console runner and the xUnit session
    /// fixture (Valheim.Testing.NativeAcceptance.Tests' <c>AcceptanceSession</c>) use the same options.
    /// </summary>
    public static PinnedServerRunOptions<AcceptancePlan> RunnerOptions(Func<GameSession, AcceptancePlan, Task>? scenario = null) => new()
    {
        Name = "acceptancemod-system-test",
        ReadPlan = path =>
        {
            var plan = AcceptancePlan.ReadValidated(path);
            // Two simultaneous clients are a campaign's actors (an inventory assigns their hosts and Steam identities).
            if (plan.Scenario is AcceptancePlan.ThreeActorScenario or AcceptancePlan.OwnershipHandoffScenario)
                throw new ArgumentException($"The {plan.Scenario} scenario runs as a campaign: campaign run <campaign.json> <plan.json> <new-output-directory>.");
            return plan;
        },
        // The session capability and token variable AcceptanceMod's test adapter serves, and its Harmony patches, which the session
        // checks on the server before any scenario step.
        Mod = AcceptancePlan.Mod,
        CheckPlan = plan =>
        {
            if (plan.Client == null && !plan.ServerOnly)
                throw new ArgumentException($"A run looks from a client: add the client section, or use the {AcceptancePlan.ServerScenario} scenario for the server half alone.");
            // A campaign template is bound to its prepared actors in memory; its own rules apply to the bound plan.
            if (plan.Scenario is AcceptancePlan.ThreeActorScenario or AcceptancePlan.OwnershipHandoffScenario) AcceptancePlan.Validated(plan);
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
    public static IReadOnlyDictionary<string, ClientRunPlan> CampaignClientsFor(AcceptancePlan plan) =>
        (Find(plan.Scenario)?.CampaignClients ?? throw new ArgumentException($"This suite's campaign runs {AcceptancePlan.ThreeActorScenario} or {AcceptancePlan.OwnershipHandoffScenario}."))(plan);

    /// <summary>The scenario named <paramref name="name"/>, or null.</summary>
    public static Entry? Find(string? name) => Entries.FirstOrDefault(entry => entry.Name == name);

    /// <summary>The runner's scenario: the plan's entry on the session. A control run that failed its check as expected ends here, passing.</summary>
    public static Task Run(GameSession session, AcceptancePlan plan)
    {
        var entry = Find(plan.Scenario) ?? throw new ArgumentException($"{plan.Scenario} is not one of this suite's scenarios.");
        try { entry.Run(session, plan); }
        catch (ControlConcluded concluded) when (concluded.Control == plan.Control)
        {
            session.Report.Provenance["control"] = concluded.Control.Name + ": failed its check as expected";
        }
        return Task.CompletedTask;
    }

    private static void ServerScenario(GameSession session, AcceptancePlan plan)
    {
        var owned = session.Server!;
        var server = DrySiteServerScenario.Run(plan, owned.Game, owned.Restart, session.Report);
        if (plan.PatchReload == null) return;
        // It writes into the runtime copy's scripts folder, which must be on this machine.
        if (owned.Host != null) throw new ArgumentException("patchReload runs only with the server on this machine (no --inventory).");
        PatchReloadScenario.Run(plan, server, owned.RuntimeDirectory, session.Output, session.Report, session.Cancellation);
    }

    // The plan's two client sections, by the campaign's client names.
    private static IReadOnlyDictionary<string, ClientRunPlan> TwoClients(AcceptancePlan plan) => new Dictionary<string, ClientRunPlan>
    {
        ["client-a"] = plan.Client ?? throw new ArgumentException("The campaign template needs a client section."),
        ["client-b"] = plan.SecondClient ?? throw new ArgumentException("The campaign template needs a secondClient section."),
    };
}
