using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// <c>vanilla-client</c> (#33): MyMod claims clients do not need it. A client with BepInEx, ValheimCLI and MyMod's test
/// adapter but not MyMod (pinned <c>absent</c>) joins after the mod has marked the dry site, arrives beside the marker and
/// must see nothing it cannot handle: <see cref="VanillaClientCheck"/> waits for a complete census of the objects within
/// 64 m and fails on any prefab hash the client cannot resolve, then scans the owned client's live log for missing
/// prefabs, missing RPC handlers and errors while objects unload. The same after a restart. With
/// <c>"expectFailure": "server-only-prefab"</c> the server also spawns the control's own object beside the dry site, and
/// the census must name its hash.
/// </summary>
public static class VanillaClientScenario
{
    public static void Run(CampaignRun run)
    {
        var plan = run.Plan; var report = run.Report; var client = plan.Client!; var control = plan.Control;
        CampaignSteps.MarkSites(plan, run.Server, report);
        if (control != null)
            report.Step($"control {control.Name}: the server spawns its server-only object beside the dry site", () =>
            {
                run.Server.Execute($"mymodcontrol_spawn {CampaignSteps.Number(plan.DrySite.X + 3)} {CampaignSteps.Number(plan.DrySite.Z)}")
                    .RequireLine("OK: spawned " + ControlPlugins.ServerOnlyPrefabName, "The control did not spawn its object");
            });

        string? log = run.ClientLog(client);
        report.Provenance["vanillaClientLogScan"] = log == null ? "not run: an attached client's logs are its operator's" : "the owned client's live BepInEx log";
        var check = new VanillaClientCheck
        {
            Capability = Capabilities.UnresolvedPrefabs, Radius = 64, KnownPrefabs = [ControlPlugins.ServerOnlyPrefabName],
            ClientLogs = log == null ? null : () => new[] { new RunLog("client BepInEx log (live)", log, Required: true) },
            ArrivalTimeout = TimeSpan.FromSeconds(client.ArrivalSeconds), CensusTimeout = TimeSpan.FromSeconds(client.ArrivalSeconds),
            CensusInterval = run.Interval, Cancellation = run.Cancellation,
        };
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = report, Output = run.Output, OwnedServer = run.OwnedServer, Arrival = CampaignSteps.At(plan.Arrival), ArriveStep = "arrive beside the marker",
            Cancellation = run.Cancellation,
        }.Run(run.Server, () => run.OpenClient(client, null),
            measure: round =>
            {
                round.Step("the client sees the marker at the dry site", () => DrySiteScenario.RequireClientMarkers(round.Client, plan.DrySite, 1));
                if (control == null) { check.Measure(round); return; }
                ControlPlugins.ExpectFailure(report, control, () =>
                {
                    var scan = UnresolvedPrefabs.WaitForComplete(round.Client, Capabilities.UnresolvedPrefabs, check.Radius, check.CensusTimeout, check.CensusInterval, run.Cancellation)
                        .GetAwaiter().GetResult();
                    round.Write("vanilla-client-1", scan);
                    scan.RequireNone(check.KnownPrefabs);
                }, round.Name + ": ");
                throw new ControlConcluded(control);
            },
            afterRestart: round => round.Step("the server still has one marker at the dry site, none at the wet site",
                () => CampaignSteps.RequireMarkers(round.Server, plan, dry: 1)));
    }
}
