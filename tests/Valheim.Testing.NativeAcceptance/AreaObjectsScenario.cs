using MyMod.SystemTests;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>Example of issue #201: optional read-only object evidence alongside, not inside, terrain checks.</summary>
public static class AreaObjectsScenario
{
    public static void Run(GameSession session, AcceptancePlan plan)
    {
        var client = plan.Client!;
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = session.Report, Output = session.Output,
            OwnedServer = session.Server!,
            Arrival = CampaignSteps.At(plan.Arrival), Rounds = ["joined"],
            Cancellation = session.Cancellation,
        }.Run(session.Server!.Game, () => session.OpenClient(client), round =>
            round.Step("capture read-only saved and loaded objects at the arrival site", () =>
            {
                var snapshot = AreaObjectSnapshot.Capture(round.Server, round.Client, "arrival", plan.WorldUid,
                    (int)plan.Arrival.X, (int)plan.Arrival.Z, radius: 16, TimeSpan.FromSeconds(30),
                    includeContainers: true, includeSupport: true, session.Cancellation);
                session.Report.Attach(snapshot);
            }));
    }
}
