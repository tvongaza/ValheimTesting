using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>One server and two clients, all owned by the pinned runner. Setup smoke with explicit join and rejoin checkpoints.</summary>
public static class ThreeActorSmokeScenario
{
    public static void Run(CampaignRun run)
    {
        run.Report.Step("server accepts game connections", () => run.WaitUntilJoinable(run.Server));
        IReadOnlyDictionary<string, ClientSession>? sessions = null;
        run.Report.Step("both clients start in parallel on their owned hosts", () =>
            sessions = run.OpenProfileClientsParallel(new Dictionary<string, ClientRunPlan>
            {
                ["client-a"] = run.Plan.Client!, ["client-b"] = run.Plan.SecondClient!,
            }));
        using var a = sessions!["client-a"];
        using var b = sessions["client-b"];
        run.Report.Step("both owned clients are at their pinned menus", () =>
        {
            RequireMenu(a.Actor, run.Plan.Client!, "client A");
            RequireMenu(b.Actor, run.Plan.SecondClient!, "client B");
        });
        Join(a.Actor, run.Plan.Client!, run.Plan.WorldUid, run.Report, "client A");
        Join(b.Actor, run.Plan.SecondClient!, run.Plan.WorldUid, run.Report, "client B");
        run.Report.Step("both clients joined: server sees two peers", () => RequirePeers(run.Server, 2));
        run.Report.Step("client B leaves and returns to its pinned menu", () =>
        {
            new SessionControl(b.Actor).Leave();
            RequireMenu(b.Actor, run.Plan.SecondClient!, "client B");
        });
        run.Report.Step("client A remains joined while B is away", () => RequireWorld(a.Actor, run.Plan.Client!, run.Plan.WorldUid, "client A"));
        Join(b.Actor, run.Plan.SecondClient!, run.Plan.WorldUid, run.Report, "client B rejoins");
        run.Report.Step("both clients rejoined: server sees two peers", () => RequirePeers(run.Server, 2));
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

    private static void RequirePeers(GameActor server, int count)
    {
        var reply = server.Execute("cli_peers");
        if (!reply.Output.Contains($"OK: {count} peer(s)") || reply.Output.Count(line => line.StartsWith("PEER ", StringComparison.Ordinal)) != count)
            throw new InvalidOperationException("The server did not see " + count + " joined clients: " + string.Join(" | ", reply.Output));
    }

    private static void Join(GameActor actor, ClientRunPlan plan, string worldUid, ScenarioReport report, string name) =>
        report.Step(name + " joins the pinned world with its mod and adapter", () =>
        {
            new SessionControl(actor).Join(plan.Join, plan.Character, plan.PasswordVariable);
            RequireWorld(actor, plan, worldUid, name);
            TestAccess.Ensure(actor, TestActorRole.ClientInWorld, clientMutations: true); // joined outside ClientRounds
            PlayerPlacement.Protect(actor);
            _ = actor.RequireCapability(Capabilities.Markers);
        });
}
