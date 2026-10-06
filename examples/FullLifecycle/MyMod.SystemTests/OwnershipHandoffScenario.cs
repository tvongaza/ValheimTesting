using System.Runtime.ExceptionServices;
using System.Text.Json;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// Two owned clients on different account hosts remain connected together. The example explicitly claims its marker
/// on A, observes that B sees the same owner, closes A, then explicitly claims on B. This tests orchestration,
/// identity and teardown; it does not assert that all vanilla ZDOs transfer automatically when a player walks away.
/// Each transition is requested once. The adapter waits for owner-change notifications inside the game.
/// </summary>
public static class OwnershipHandoffScenario
{
    private const string OwnerSource = "mymod-marker-owner";

    public static void Run(GameSession session, LifecyclePlan plan)
    {
        var first = plan.Client!;
        var second = plan.SecondClient!;
        var report = session.Report;
        ClientSession? a = null, b = null;
        Exception? failure = null;
        report.Step(StepPhase.Setup, "fixture arrival heights are dry and measured before either client starts", () =>
        {
            var samples = new[] { CampaignSteps.At(plan.Arrival), CampaignSteps.At(plan.SecondArrival!) };
            var measured = TerrainProbe.Compare(session.Server!.Game, "generator", "declared fixture arrival points", samples, 0.5f);
            report.Provenance["arrival-height-check"] = JsonSerializer.Serialize(measured);
            if (!measured.Passed || measured.Samples.Any(sample => sample.Actual < LifecyclePlan.WaterLevel + LifecyclePlan.Clearance))
                throw new InvalidOperationException("Fixture arrivals are not confirmed dry: " + JsonSerializer.Serialize(measured));
        });
        CampaignSteps.MarkSites(plan, session.Server!.Game, report);
        try
        {
            report.Step(StepPhase.Setup, "the dedicated server accepts both clients", () => session.Server!.WaitUntilJoinable(session.Server!.Game));
            report.Step(StepPhase.Setup, "open pinned client A on its leased account", () => a = session.OpenClient(first, "client-a"));
            JoinAndArrive(session, plan.WorldUid, a!.Actor, first, plan.Arrival, "A");
            report.Step("A sees one labelled marker", () => CampaignSteps.RequireLabelledMarker(a.Actor, plan.DrySite));
            string aId = "";
            report.Step("A explicitly claims the example marker once", () => aId = Claim(a.Actor, plan.DrySite));
            report.Step("server observes A's owner-change notification", () => WaitForOwner(session.Server!.Game, plan.DrySite, aId, report, "server-owner-a"));

            report.Step("open pinned client B on its separate leased account", () => b = session.OpenClient(second, "client-b"));
            JoinAndArrive(session, plan.WorldUid, b!.Actor, second, plan.SecondArrival!, "B");
            report.Step("two clients remain connected at once", () => CampaignSteps.RequirePeers(session.Server!.Game, 2));
            report.Step("B sees A as the marker's only owner", () =>
            {
                CampaignSteps.RequireLabelledMarker(b.Actor, plan.DrySite);
                WaitForOwner(b.Actor, plan.DrySite, aId, report, "client-b-owner-a");
                RequireOwner(b.Actor, plan.DrySite, aId, ownedHere: false);
            });

            report.Step("A leaves and its owned game process stops", () =>
            {
                new SessionControl(a.Actor).Leave();
                a.Dispose();
                if (a.Owned && a.Stopped == null) throw new InvalidOperationException("A's process stop was not established.");
            });
            string bId = "";
            report.Step("B explicitly claims the example marker once", () => bId = Claim(b.Actor, plan.DrySite));
            if (bId == aId) throw new InvalidOperationException("The two clients reported the same ZDO session ID.");
            report.Step("server observes B's owner-change notification", () => WaitForOwner(session.Server!.Game, plan.DrySite, bId, report, "server-owner-b"));
            report.Step("B still sees the marker and is its only owner", () =>
            {
                CampaignSteps.RequireLabelledMarker(b.Actor, plan.DrySite);
                RequireOwner(b.Actor, plan.DrySite, bId, ownedHere: true);
                RequireOwner(session.Server!.Game, plan.DrySite, bId, ownedHere: false, requireInstance: false);
                CampaignSteps.RequirePeers(session.Server!.Game, 1);
            });
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // Dispose in reverse order even if joining B, an account lease, or an observation failed. Keep an
            // uncertain stop visible to the runner; it must not report a failed observation as a clean teardown.
            var cleanup = new List<Exception>();
            if (b != null && !b.Closed)
                try { report.Step(StepPhase.Cleanup, "stop only owned client B before lease teardown", b.Dispose); }
                catch (Exception error) { cleanup.Add(error); }
            if (a != null && !a.Closed)
                try { report.Step(StepPhase.Cleanup, "stop only owned client A before lease teardown", a.Dispose); }
                catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count > 0)
                failure = failure == null ? new AggregateException("Client teardown was not established.", cleanup)
                    : new AggregateException("The scenario and client teardown both failed.", new[] { failure }.Concat(cleanup));
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void JoinAndArrive(GameSession session, string worldUid, GameActor actor, ClientRunPlan client, Site site, string name)
    {
        // The toolkit's one join: the join once, world pins, the world awaited and the player protected, test access.
        session.Report.Step(StepPhase.Setup, $"{name} joins the pinned world once and is protected",
            () => new SessionControl(actor).JoinWorld(client, worldUid, cancellation: session.Cancellation));
        session.Report.Step(StepPhase.Setup, $"{name} arrives on dry ground by game-side signals", () =>
        {
            var point = CampaignSteps.At(site);
            PlayerPlacement.TeleportArrival arrival;
            // With two players the server cannot name one, so the client teleports itself (no server). This scenario tests
            // ownership, not terrain generation: a location's levelling can move the ground from the generator's height, so
            // the landing is judged on the loaded ground the client measures once its floor is ready (loadedGround).
            try { arrival = PlayerPlacement.Arrive(null, actor, point, TimeSpan.FromSeconds(client.ArrivalSeconds), session.Cancellation, loadedGround: true); }
            catch
            {
                // Read-only diagnostics before teardown: never retry a teleport or replace its failure.
                if (!session.Cancellation.IsCancellationRequested)
                {
                    var previous = actor.CommandTimeout;
                    try
                    {
                        actor.CommandTimeout = TimeSpan.FromSeconds(5);
                        Capture("support", () => actor.Observe(actor.RequireCapability("valheim.world/player-support")).Data.GetRawText());
                        Capture("ground", () => JsonSerializer.Serialize(TerrainProbe.Compare(actor, "loaded-ground",
                            "declared fixture arrival", new[] { point }, .3f)));
                    }
                    finally { actor.CommandTimeout = previous; }
                }
                throw;
            }
            session.Report.Provenance[$"arrival-{name}-ground"] = JsonSerializer.Serialize(new { point.X, point.Z, generator = point.Height, loaded = arrival.Target.Height });
            if (arrival.Target.Height < LifecyclePlan.WaterLevel + LifecyclePlan.Clearance) throw new InvalidOperationException("Loaded arrival ground is not dry.");
            void Capture(string kind, Func<string> read)
            {
                string key = $"arrival-{name}-{kind}";
                try { session.Report.Provenance[key] = read(); }
                catch (Exception error) { session.Report.Provenance[key] = "Diagnostic unavailable: " + error.Message; }
            }
        });
    }

    private static string Claim(GameActor actor, Site site)
    {
        var data = actor.Invoke(actor.RequireCapability(Capabilities.MarkerOwnerClaim), CampaignSteps.Number(site.X), CampaignSteps.Number(site.Z));
        if (!data.GetProperty("ownedHere").GetBoolean() || !data.GetProperty("instance").GetBoolean())
            throw new InvalidOperationException("The claim did not make the local loaded marker owned here: " + data.GetRawText());
        return data.GetProperty("self").GetString()!;
    }

    private static void WaitForOwner(GameActor actor, Site site, string expected, ScenarioReport report, string evidence)
    {
        JsonElement data;
        var previous = actor.CommandTimeout;
        try
        {
            actor.CommandTimeout = TimeSpan.FromSeconds(55); // The adapter's own wait is 40 s.
            data = actor.ObserveComplete(actor.RequireCapability(Capabilities.MarkerOwnerWait),
                OwnerSource, CampaignSteps.Number(site.X), CampaignSteps.Number(site.Z), expected, "40").Data.Clone();
        }
        finally { actor.CommandTimeout = previous; }
        report.Provenance[evidence] = data.GetRawText();
        if (data.GetProperty("owner").GetString() != expected) throw new InvalidOperationException("The owner wait completed for the wrong session.");
    }

    private static void RequireOwner(GameActor actor, Site site, string expected, bool ownedHere, bool requireInstance = true)
    {
        var data = actor.ObserveComplete(actor.RequireCapability(Capabilities.MarkerOwner), OwnerSource,
            CampaignSteps.Number(site.X), CampaignSteps.Number(site.Z)).Data;
        RequireOwnerReading(data, expected, ownedHere, requireInstance);
    }

    /// <summary>Refuses an incomplete or contradictory owner reading; an absent field cannot count as zero or false.</summary>
    public static void RequireOwnerReading(JsonElement data, string expected, bool ownedHere, bool requireInstance = true)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("source", out var source) || source.GetString() != OwnerSource ||
            !data.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True ||
            !data.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.String || owner.GetString() != expected ||
            !data.TryGetProperty("ownedHere", out var local) || local.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            local.GetBoolean() != ownedHere || requireInstance &&
            (!data.TryGetProperty("instance", out var instance) || instance.ValueKind != JsonValueKind.True))
            throw new InvalidOperationException("Incomplete or conflicting marker owner observation: " + data.GetRawText());
    }
}
