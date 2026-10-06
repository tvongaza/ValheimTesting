using System.Runtime.ExceptionServices;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>
/// Ghost protection against AI another peer simulates (#261). A creature's AI runs only in the process that owns its ZDO,
/// and the game's ghost mode was a local field, so a protected player could be seen and attacked by a creature another
/// client owned. Two owned clients join one dedicated server:
/// <list type="number">
/// <item>B joins, is protected and arrives; B spawns one hostile beside itself, so B's process owns and simulates it.</item>
/// <item>A joins, is protected (god and ghost, read back on A) and arrives within the hostile's view.</item>
/// <item>For a bounded window, read in B's process: the hostile, still owned by B, never targets A, and B reads A as a
/// ghost. Local readback on A alone is never the result.</item>
/// <item>Control: A's ghost mode goes off (as the game's <c>ghost off</c>); the same hostile must then target A, which
/// shows the window could have seen an attack.</item>
/// <item>Rejoin: A leaves and joins again, protected by the join as every join is; the same hostile again never targets A.</item>
/// </list>
/// The hostile is removed by its owner at the end; both clients stop in every outcome.
/// </summary>
public static class GhostProtectionScenario
{
    public const string Hostile = "Skeleton";
    public const int WatchSeconds = 20;
    /// <summary>How far from B's arrival the hostile is spawned, and the radius the watches read.</summary>
    public const float SpawnOffset = 2f, WatchRadius = 30f;

    public static void Run(GameSession session, AcceptancePlan plan)
    {
        var report = session.Report;
        ClientSession? a = null, b = null;
        Exception? failure = null;
        string? hostile = null;
        try
        {
            report.Step(StepPhase.Setup, "the dedicated server accepts both clients", () => session.Server!.WaitUntilJoinable(session.Server!.Game));
            report.Step(StepPhase.Setup, "open pinned client B (the hostile's owner) on its leased account", () => b = session.OpenClient(plan.SecondClient!, "client-b"));
            OwnershipHandoffScenario.JoinAndArrive(session, plan.WorldUid, b!.Actor, plan.SecondClient!, plan.SecondArrival!, "B");
            report.Step($"B spawns one {Hostile} beside itself and owns it", () =>
            {
                var spawned = b.Actor.Invoke(b.Actor.RequireCapability(Capabilities.CreatureSpawn), Hostile,
                    CampaignSteps.Number(plan.SecondArrival!.X + SpawnOffset), CampaignSteps.Number(plan.SecondArrival!.Z));
                report.Provenance["ghost-hostile"] = spawned.GetRawText();
                if (!spawned.GetProperty("ownedHere").GetBoolean()) throw new InvalidOperationException("B does not own the creature it spawned: " + spawned.GetRawText());
                hostile = spawned.GetProperty("zdo").GetString();
            });

            report.Step(StepPhase.Setup, "open pinned client A (the protected player) on its separate leased account", () => a = session.OpenClient(plan.Client!, "client-a"));
            OwnershipHandoffScenario.JoinAndArrive(session, plan.WorldUid, a!.Actor, plan.Client!, plan.Arrival, "A");
            string aName = "";
            report.Step("A reads itself as protected", () =>
            {
                var self = Watch(a.Actor, plan.Arrival, seconds: 0);
                var local = Players(self).SingleOrDefault(player => player.GetProperty("local").GetBoolean());
                if (local.ValueKind != JsonValueKind.Object || !local.GetProperty("ghost").GetBoolean())
                    throw new InvalidOperationException("A's local player is not in ghost mode: " + self.GetRawText());
                aName = local.GetProperty("name").GetString()!;
            });

            JsonElement protectedWindow = default;
            report.Step($"B watches its {Hostile} for {WatchSeconds} s with A protected beside it", () =>
                protectedWindow = Watch(b.Actor, plan.SecondArrival!, WatchSeconds, report, "ghost-watch-protected"));
            report.Step($"the {Hostile} stayed owned by B: the window read the AI that ran", () => RequireOwnedHere(protectedWindow, hostile!));
            report.Step("B reads A as a ghost", () => RequireRemoteGhost(protectedWindow, aName, expected: true));
            report.Step($"B's {Hostile} never targeted protected A", () => RequireTargeted(protectedWindow, hostile!, aName, expected: false));

            report.Step("control: A's ghost mode goes off", () =>
            {
                var off = a.Actor.Invoke(a.Actor.RequireCapability(Capabilities.GhostMode), "off");
                if (off.GetProperty("ghost").GetBoolean()) throw new InvalidOperationException("A's ghost mode did not turn off: " + off.GetRawText());
            });
            JsonElement controlWindow = default;
            report.Step($"control: B watches its {Hostile} for {WatchSeconds} s with A visible", () =>
                controlWindow = Watch(b.Actor, plan.SecondArrival!, WatchSeconds, report, "ghost-watch-control"));
            report.Step($"control: the {Hostile} stayed owned by B", () => RequireOwnedHere(controlWindow, hostile!));
            report.Step("control: B reads A as visible", () => RequireRemoteGhost(controlWindow, aName, expected: false));
            report.Step($"control: B's {Hostile} targeted A once A was visible", () => RequireTargeted(controlWindow, hostile!, aName, expected: true));

            // Rejoin: A's player is a new object with a new ZDO, so its ghost mode is set and replicated again by the join.
            report.Step("A leaves the world once and is back at its pinned menu", () =>
            {
                new SessionControl(a.Actor).Leave();
                a.Actor.VerifyEnvironment(plan.Client!.MenuExpectations); // A transition always needs fresh pins.
                if (new SessionControl(a.Actor).Read() is { Phase: not "menu" } or { WorldPresent: true }) throw new InvalidOperationException("A is not at its pinned main menu.");
            });
            OwnershipHandoffScenario.JoinAndArrive(session, plan.WorldUid, a.Actor, plan.Client!, plan.Arrival, "A again");
            JsonElement rejoinWindow = default;
            report.Step($"after A's rejoin: B watches its {Hostile} for {WatchSeconds} s", () =>
                rejoinWindow = Watch(b.Actor, plan.SecondArrival!, WatchSeconds, report, "ghost-watch-rejoin"));
            report.Step($"after A's rejoin: the {Hostile} stayed owned by B", () => RequireOwnedHere(rejoinWindow, hostile!));
            report.Step("after A's rejoin: B reads A as a ghost", () => RequireRemoteGhost(rejoinWindow, aName, expected: true));
            report.Step($"after A's rejoin: B's {Hostile} never targeted A", () => RequireTargeted(rejoinWindow, hostile!, aName, expected: false));
        }
        catch (Exception error) { failure = error; }
        finally
        {
            var cleanup = new List<Exception>();
            if (hostile != null && b != null && !b.Closed)
                try { report.Step(StepPhase.Cleanup, $"B removes its {Hostile}", () => b.Actor.Invoke(b.Actor.RequireCapability(Capabilities.CreatureRemove), hostile)); }
                catch (Exception error) { cleanup.Add(error); }
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

    /// <summary>One adapter watch (<c>ai-watch</c>), complete or refused; with <paramref name="evidence"/> it is kept in the provenance.</summary>
    public static JsonElement Watch(GameActor actor, Site at, int seconds, ScenarioReport? report = null, string? evidence = null)
    {
        JsonElement data;
        var previous = actor.CommandTimeout;
        try
        {
            actor.CommandTimeout = TimeSpan.FromSeconds(seconds + 30);
            data = actor.ObserveComplete(actor.RequireCapability(Capabilities.AiWatch), "acceptancemod-ai-watch", CampaignSteps.Number(at.X), CampaignSteps.Number(at.Z),
                CampaignSteps.Number(WatchRadius), seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).Data.Clone();
        }
        finally { actor.CommandTimeout = previous; }
        if (!data.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array ||
            !data.TryGetProperty("creatures", out var creatures) || creatures.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Incomplete ai-watch observation: " + data.GetRawText());
        if (report != null && evidence != null) report.Provenance[evidence] = data.GetRawText();
        return data;
    }

    private static IEnumerable<JsonElement> Players(JsonElement watch) => watch.GetProperty("players").EnumerateArray();

    private static JsonElement Creature(JsonElement watch, string zdo) =>
        watch.GetProperty("creatures").EnumerateArray().SingleOrDefault(creature => creature.GetProperty("zdo").GetString() == zdo) is { ValueKind: JsonValueKind.Object } found
            ? found : throw new InvalidOperationException($"The watch did not see creature {zdo} within {WatchRadius} m: " + watch.GetRawText());

    /// <summary>The creature was owned, and so simulated, by the watching process at every reading: otherwise some of its targets were not readable there.</summary>
    public static void RequireOwnedHere(JsonElement watch, string zdo)
    {
        var creature = Creature(watch, zdo);
        if (!creature.GetProperty("ownedHere").GetBoolean())
            throw new InvalidOperationException("The creature's ownership moved away from the watching client, so its targets are unknown: " + creature.GetRawText());
    }

    /// <summary>The watching process reads <paramref name="name"/> (another peer's player) as a ghost, or not.</summary>
    public static void RequireRemoteGhost(JsonElement watch, string name, bool expected)
    {
        var player = Players(watch).SingleOrDefault(p => p.GetProperty("name").GetString() == name && !p.GetProperty("local").GetBoolean());
        if (player.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"The watching client does not know player {name}: " + watch.GetRawText());
        if (player.GetProperty("ghost").GetBoolean() != expected)
            throw new InvalidOperationException($"The watching client reads {name} with ghost={player.GetProperty("ghost").GetBoolean()}; expected {expected}.");
    }

    /// <summary>Whether the creature chose <paramref name="name"/> as a target during the window.</summary>
    public static void RequireTargeted(JsonElement watch, string zdo, string name, bool expected)
    {
        var targets = Creature(watch, zdo).GetProperty("targets").EnumerateArray().Select(target => target.GetString()).ToArray();
        if (targets.Contains("player:" + name) != expected)
            throw new InvalidOperationException(expected
                ? $"The creature never targeted {name}: targets [{string.Join(", ", targets)}]. The control did not show the window could see an attack."
                : $"The creature targeted protected {name}: targets [{string.Join(", ", targets)}].");
    }
}
