using System.Diagnostics;
using System.Globalization;
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

    public static void Run(CampaignRun run)
    {
        var plan = run.Plan;
        var first = plan.Client!;
        var second = plan.SecondClient!;
        var report = run.Report;
        ClientSession? a = null, b = null;
        Exception? failure = null;
        CampaignSteps.MarkSites(plan, run.Server, report);
        try
        {
            report.Step("the dedicated server accepts both clients", () => run.WaitUntilJoinable(run.Server));
            report.Step("open pinned client A on its leased account", () => a = run.OpenProfileClient(first, "client-a"));
            JoinAndArrive(run, a!.Actor, first, plan.Arrival, "A");
            report.Step("A sees one labelled marker", () => CampaignSteps.RequireLabelledMarker(a.Actor, plan.DrySite));
            string aId = "";
            report.Step("A explicitly claims the example marker once", () => aId = Claim(a.Actor, plan.DrySite));
            report.Step("server observes A's owner-change notification", () => WaitForOwner(run.Server, plan.DrySite, aId, report, "server-owner-a"));

            report.Step("open pinned client B on its separate leased account", () => b = run.OpenProfileClient(second, "client-b"));
            JoinAndArrive(run, b!.Actor, second, plan.SecondArrival!, "B");
            report.Step("two clients remain connected at once", () => RequirePeers(run.Server, 2));
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
            report.Step("server observes B's owner-change notification", () => WaitForOwner(run.Server, plan.DrySite, bId, report, "server-owner-b"));
            report.Step("B still sees the marker and is its only owner", () =>
            {
                CampaignSteps.RequireLabelledMarker(b.Actor, plan.DrySite);
                RequireOwner(b.Actor, plan.DrySite, bId, ownedHere: true);
                RequireOwner(run.Server, plan.DrySite, bId, ownedHere: false, requireInstance: false);
                RequirePeers(run.Server, 1);
            });
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // Dispose in reverse order even if joining B, an account lease, or an observation failed. Keep an
            // uncertain stop visible to the runner; it must not report a failed observation as a clean teardown.
            var cleanup = new List<Exception>();
            if (b != null && !b.Closed)
                try { report.Step("stop only owned client B before lease teardown", b.Dispose); }
                catch (Exception error) { cleanup.Add(error); }
            if (a != null && !a.Closed)
                try { report.Step("stop only owned client A before lease teardown", a.Dispose); }
                catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count > 0)
                failure = failure == null ? new AggregateException("Client teardown was not established.", cleanup)
                    : new AggregateException("The scenario and client teardown both failed.", new[] { failure }.Concat(cleanup));
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void JoinAndArrive(CampaignRun run, GameActor actor, ClientRunPlan plan, Site site, string name)
    {
        run.Report.Step($"{name} joins the pinned world once and is protected", () =>
        {
            var session = new SessionControl(actor);
            // valheim.session/join completes only after the game's player-ready transition.
            session.Join(plan.Join, plan.Character, plan.PasswordVariable);
            actor.VerifyEnvironment(plan.WorldExpectations(run.Plan.WorldUid));
            var joined = session.Read(); // One identity/readiness check, not an external polling loop.
            if (!joined.WorldReady || joined.WorldUid != run.Plan.WorldUid || !joined.LocalPlayer)
                throw new InvalidOperationException($"{name} joined without a ready player in world {run.Plan.WorldUid}: {joined}.");
            PlayerPlacement.Protect(actor);
            CampaignSteps.AcknowledgeLocalCheats(actor); // The plan uses disposable local characters.
        });
        run.Report.Step($"{name} arrives on dry ground by game-side signals", () =>
            ArriveSelf(actor, CampaignSteps.At(site), TimeSpan.FromSeconds(plan.ArrivalSeconds), run.Cancellation));
    }

    private static void ArriveSelf(GameActor actor, HeightExpectation point, TimeSpan timeout, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        PlayerPlacement.SkipIntro(actor, TimeSpan.FromSeconds(Math.Min(60, timeout.TotalSeconds)));
        string left = SecondsLeft(clock, timeout);
        WithDeadline(actor, timeout - clock.Elapsed, () => RequireLine(actor.Execute($"cli_wait_teleportable {left} 0 true"), "OK: TELEPORTABLE "));
        cancellation.ThrowIfCancellationRequested();
        string armed = RequireLine(actor.Execute("cli_teleport_trace_arm"), "OK: TELEPORT_TRACE_ARM id=");
        string id = armed["OK: TELEPORT_TRACE_ARM id=".Length..];
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
            throw new InvalidOperationException("Invalid teleport trace ID: " + armed);
        string at = string.Join(" ", new[] { point.X, point.Height + .5f, point.Z }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        RequireLine(actor.Execute("cli_teleport " + at), "OK: Teleported to "); // Own player, never an ambiguous peer index.
        cancellation.ThrowIfCancellationRequested();
        string trace = "";
        WithDeadline(actor, timeout - clock.Elapsed, () => trace = RequireLine(actor.Execute($"cli_teleport_trace_wait {id} {SecondsLeft(clock, timeout)}"), "OK: TELEPORT_TRACE "));
        if (!trace.Contains("floorAtDone=True", StringComparison.Ordinal)) throw new InvalidOperationException("Teleport ended without a ready floor: " + trace);
        Observation support = null!;
        WithDeadline(actor, timeout - clock.Elapsed, () => support = actor.Observe(actor.RequireCapability("valheim.world/player-support-wait"),
            point.X.ToString("R", CultureInfo.InvariantCulture), point.Height.ToString("R", CultureInfo.InvariantCulture),
            point.Z.ToString("R", CultureInfo.InvariantCulture), SecondsLeft(clock, timeout)));
        if (!SurfaceProbe.Supported(support, point)) throw new InvalidOperationException("Unsupported arrival: " + support.Data.GetRawText());
    }

    private static string SecondsLeft(Stopwatch clock, TimeSpan timeout)
    {
        double left = (timeout - clock.Elapsed).TotalSeconds;
        if (left <= 0) throw new TimeoutException("The client's one-hop deadline expired; no action was repeated.");
        return Math.Min(120, left).ToString("R", CultureInfo.InvariantCulture);
    }

    private static void WithDeadline(GameActor actor, TimeSpan remaining, Action action)
    {
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("The client's one-hop deadline expired; no action was repeated.");
        var old = actor.CommandTimeout;
        try { actor.CommandTimeout = remaining + TimeSpan.FromSeconds(10); action(); }
        finally { actor.CommandTimeout = old; }
    }

    private static string RequireLine(valheim_cli.Testing.CommandResult reply, string prefix) =>
        reply.Output.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal)) ??
        throw new InvalidOperationException("The game did not confirm " + prefix + ": " + string.Join(" | ", reply.Output));

    private static string Claim(GameActor actor, Site site)
    {
        var data = actor.Invoke(actor.RequireCapability(Capabilities.MarkerOwnerClaim), CampaignSteps.Number(site.X), CampaignSteps.Number(site.Z));
        if (!data.GetProperty("ownedHere").GetBoolean() || !data.GetProperty("instance").GetBoolean())
            throw new InvalidOperationException("The claim did not make the local loaded marker owned here: " + data.GetRawText());
        return data.GetProperty("self").GetString()!;
    }

    private static void WaitForOwner(GameActor actor, Site site, string expected, ScenarioReport report, string evidence)
    {
        JsonElement data = default;
        WithDeadline(actor, TimeSpan.FromSeconds(45), () => data = actor.ObserveComplete(actor.RequireCapability(Capabilities.MarkerOwnerWait),
            OwnerSource, CampaignSteps.Number(site.X), CampaignSteps.Number(site.Z), expected, "40").Data.Clone());
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

    private static void RequirePeers(GameActor server, int expected)
    {
        var reply = server.Execute("cli_peers");
        string line = RequireLine(reply, "OK: ");
        if (line != $"OK: {expected} peer(s)" || reply.Output.Count(x => x.StartsWith("PEER ", StringComparison.Ordinal)) != expected)
            throw new InvalidOperationException($"Expected {expected} connected peer(s): " + string.Join(" | ", reply.Output));
    }
}
