using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>One server and two clients, all owned by the pinned runner. Setup smoke with explicit join and rejoin checkpoints.</summary>
public static class ThreeActorSmokeScenario
{
    public static void Run(GameSession session, AcceptancePlan plan)
    {
        session.Report.Step("server accepts game connections", () => session.Server!.WaitUntilJoinable(session.Server!.Game));
        IReadOnlyDictionary<string, ClientSession>? sessions = null;
        session.Report.Step("both clients start in parallel on their owned hosts", () =>
            sessions = session.OpenClientsAsync(new Dictionary<string, ClientRunPlan>
            {
                ["client-a"] = plan.Client!, ["client-b"] = plan.SecondClient!,
            }).GetAwaiter().GetResult());
        using var a = sessions!["client-a"];
        using var b = sessions["client-b"];
        session.Report.Step("both owned clients are at their pinned menus", () =>
        {
            RequireMenu(a.Actor, plan.Client!, "client A");
            RequireMenu(b.Actor, plan.SecondClient!, "client B");
        });
        Join(a.Actor, plan.Client!, plan.WorldUid, session, "client A joins");
        Join(b.Actor, plan.SecondClient!, plan.WorldUid, session, "client B joins");
        session.Report.Step("both clients joined: server sees two peers", () => CampaignSteps.RequirePeers(session.Server!.Game, 2));
        session.Report.Step("client B leaves and returns to its pinned menu", () =>
        {
            new SessionControl(b.Actor).Leave();
            RequireMenu(b.Actor, plan.SecondClient!, "client B");
        });
        session.Report.Step("client A remains joined while B is away", () => RequireWorld(a.Actor, plan.Client!, plan.WorldUid, "client A"));
        Join(b.Actor, plan.SecondClient!, plan.WorldUid, session, "client B rejoins");
        session.Report.Step("both clients rejoined: server sees two peers", () => CampaignSteps.RequirePeers(session.Server!.Game, 2));
    }

    private static void RequireMenu(GameActor actor, ClientRunPlan plan, string name)
    {
        actor.VerifyEnvironment(plan.MenuExpectations);
        var state = new SessionControl(actor).Read();
        if (state.Phase != "menu" || state.WorldPresent)
            throw new InvalidOperationException(name + " is not at its pinned main menu.");
    }

    private static void RequireWorld(GameActor actor, ClientRunPlan plan, string worldUid, string name)
    {
        actor.VerifyEnvironment(plan.WorldExpectations(worldUid));
        var state = new SessionControl(actor).Read();
        if (!state.WorldReady || state.WorldUid != worldUid || !state.LocalPlayer)
            throw new InvalidOperationException(name + " did not enter the expected fixture world.");
    }

    // The step names who and how: "client A joins", "client B rejoins".
    private static void Join(GameActor actor, ClientRunPlan client, string worldUid, GameSession session, string who) =>
        session.Report.Step(who + " the pinned world with its mod and adapter", () =>
        {
            new SessionControl(actor).JoinWorld(client, worldUid, cancellation: session.Cancellation); // The toolkit's one join: pins, world, protection, test access.
            _ = actor.RequireCapability(Capabilities.Markers);
        });
}
