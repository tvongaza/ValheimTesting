using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// The server half of <see cref="DrySiteScenario"/> alone, for a plan whose scenario is <c>dry-site-server</c>: no client
/// and no human review, so it can run unattended (the repository's scheduled CI check runs it).
/// <list type="number">
/// <item>Neither site has a marker yet (the fresh fixture copy).</item>
/// <item>The mod marks the dry site and refuses the wet one, each asked exactly once.</item>
/// <item>The server's saved objects show one marker at the dry site and none at the wet one.</item>
/// <item>Confirmed save, then only the owned server restarts; the server still has the marker and nothing at the wet site.</item>
/// </list>
/// It shows the mod and its adapter load on this server build and the marker persists. It cannot show what a client sees.
/// </summary>
public static class DrySiteServerScenario
{
    public static void Run(LifecyclePlan plan, GameActor server, Func<GameActor> restartOwnedServer, ScenarioReport report)
    {
        if (!plan.ServerOnly) throw new ArgumentException($"This scenario runs {LifecyclePlan.ServerScenario} plans.");
        report.Step("no marker at either site before the mod acts", () => RequireMarkers(server, plan, dry: 0));
        report.Step("the mod marks the dry site", () => DrySiteScenario.RequireReply(server.Execute(DrySiteScenario.Mark(plan.DrySite)), "OK: marked "));
        report.Step("the mod refuses the wet site", () => DrySiteScenario.RequireReply(server.Execute(DrySiteScenario.Mark(plan.WetSite)), "REFUSED: "));
        report.Step("server: one marker at the dry site, none at the wet site", () => RequireMarkers(server, plan, dry: 1));
        report.Step("confirmed world save", () => server.SaveConfirmed());
        report.Step("restart only the owned server", () => server = restartOwnedServer());
        report.Step("after restart: the server still has one marker at the dry site, none at the wet site", () => RequireMarkers(server, plan, dry: 1));
    }

    private static void RequireMarkers(GameActor server, LifecyclePlan plan, int dry)
    {
        DrySiteScenario.RequireServerMarkers(server, plan.DrySite, dry);
        DrySiteScenario.RequireServerMarkers(server, plan.WetSite, 0);
    }
}
