using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>Example of issue #201: optional read-only object evidence alongside, not inside, terrain checks.</summary>
public static class AreaObjectsScenario
{
    public static void Run(CampaignRun run)
    {
        var plan = run.Plan;
        var client = plan.Client!;
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = run.Report, Output = run.Output,
            OwnedServer = run.OwnedServer,
            Arrival = CampaignSteps.At(plan.Arrival), Rounds = ["joined"],
            SettleFor = run.SettleFor, Cancellation = run.Cancellation,
        }.Run(run.Server, () => run.OpenClient(client, null), round =>
            round.Step("capture read-only saved and loaded objects at the arrival site", () =>
            {
                var snapshot = AreaObjectSnapshot.Capture(round.Server, round.Client, "arrival", plan.WorldUid,
                    (int)plan.Arrival.X, (int)plan.Arrival.Z, radius: 16, TimeSpan.FromSeconds(30),
                    includeContainers: true, includeSupport: true, run.Cancellation);
                run.Report.AttachAreaObjectSnapshot(snapshot);
            }));
    }
}
