using System.Globalization;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// <c>refused-join</c> (#34): MyMod's version handshake refuses a client that runs another MyMod build, and the server
/// stays up for a matching one.
/// <list type="number">
/// <item>The server's MyMod patches, the handshake's among them, are applied.</item>
/// <item>The refused client (<c>refusedClient</c>, a MyMod built with another net version) opens, in <c>refused-client/</c>;
/// once the server accepts connections it joins exactly once and must be refused with <c>expectedRefusal</c> (default
/// <c>ErrorVersion</c>, 3), read back at its menu (<see cref="SessionControl.JoinExpectingRefusal"/>). It is then closed.</item>
/// <item>The server still accepts connections, and the matching client (<c>client</c>, the server's MyMod build) joins in one
/// round, in <c>matching-client/</c>: the server keeps it connected as its one player.</item>
/// </list>
/// The two clients run one after the other, so one Steam account serves both; each owned one has its own install.
/// </summary>
public static class RefusedJoinScenario
{
    public const string RefusedDirectory = "refused-client", MatchingDirectory = "matching-client";

    public static void Run(CampaignRun run)
    {
        var plan = run.Plan; var report = run.Report; var refusedPlan = plan.RefusedClient!; var client = plan.Client!;
        var expected = plan.RefusalStatus;
        CampaignSteps.ModPatchesApplied(run.Server, report);

        ClientSession? refused = null;
        bool passed = false;
        try
        {
            report.Step(refusedPlan.Owned ? "launch the mismatched owned client to its menu, plugins pinned" : "attach to the operator's mismatched client at its menu, plugins pinned",
                () => refused = run.OpenClient(refusedPlan, RefusedDirectory));
            report.Step("the server accepts game connections", () => run.OwnedServer.WaitUntilJoinable(run.Server));
            report.Step($"the mismatched client is refused with {expected} ({(int)expected})", () =>
            {
                var refusal = new SessionControl(refused!.Actor).JoinExpectingRefusal(refusedPlan.Join, refusedPlan.Character, expected, refusedPlan.MenuExpectations,
                    TimeSpan.FromSeconds(refusedPlan.JoinSeconds), refusedPlan.PasswordVariable, cancellation: run.Cancellation);
                report.Provenance["refusal"] = string.Create(CultureInfo.InvariantCulture, $"{refusal.Status} ({refusal.Code}) after {refusal.Elapsed.TotalSeconds:0.#} s");
            });
            passed = true;
        }
        finally
        {
            if (refused != null)
                try { report.Step(refused.Owned ? "stop only the mismatched owned client" : "detach from the operator's mismatched client", refused.Dispose); }
                catch when (!passed) { } // Recorded as its own failed step; the earlier failure is the one to report.
        }

        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = report, Output = run.Output, OwnedServer = run.OwnedServer, Rounds = ["matching"], Cancellation = run.Cancellation,
            OpenStep = client.Owned ? "launch the matching owned client to its menu, plugins pinned" : "attach to the operator's matching client at its menu, plugins pinned",
        }.Run(run.Server, () => run.OpenClient(client, MatchingDirectory),
            round => round.Step("the server keeps the matching client connected as its one player", () => PlayerPlacement.OnlyPeer(round.Server)));
    }
}
