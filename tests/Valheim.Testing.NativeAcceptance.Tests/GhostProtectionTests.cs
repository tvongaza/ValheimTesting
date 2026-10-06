using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

namespace Valheim.Testing.NativeAcceptance.Tests;

/// <summary>
/// The ghost-protection scenario (#261) against scripted replies: what the protected window and its control conclude from
/// B's readings, and that a failed window still removes the hostile and stops both clients. No game.
/// </summary>
public sealed class GhostProtectionTests
{
    private const string Hostile = "5:6", A = "Alice";

    private static JsonElement Watch(bool remoteGhost, bool targetedA, bool ownedHere = true, bool seen = true) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        source = "acceptancemod-ai-watch", complete = true, self = "202",
        players = new object[] { new { name = A, local = false, ghost = remoteGhost }, new { name = "Bob", local = true, ghost = true } },
        creatures = seen ? new object[] { new { zdo = Hostile, prefab = "Skeleton", owner = "202", ownedHere, targets = targetedA ? new[] { "player:" + A } : Array.Empty<string>() } } : [],
    })).RootElement.Clone();

    [Fact] public void TheWindowIsReadOnlyWhereTheCreatureRan()
    {
        GhostProtectionScenario.RequireOwnedHere(Watch(true, false), Hostile);
        Assert.Contains("ownership moved away", Assert.Throws<InvalidOperationException>(() => GhostProtectionScenario.RequireOwnedHere(Watch(true, false, ownedHere: false), Hostile)).Message);
        Assert.Contains("did not see creature", Assert.Throws<InvalidOperationException>(() => GhostProtectionScenario.RequireOwnedHere(Watch(true, false, seen: false), Hostile)).Message);
    }

    [Fact] public void ARemoteGhostAndATargetAreReadFromTheOwnersProcess()
    {
        GhostProtectionScenario.RequireRemoteGhost(Watch(true, false), A, expected: true);
        Assert.Throws<InvalidOperationException>(() => GhostProtectionScenario.RequireRemoteGhost(Watch(false, false), A, expected: true));
        Assert.Contains("does not know player", Assert.Throws<InvalidOperationException>(() => GhostProtectionScenario.RequireRemoteGhost(Watch(true, false), "Carol", expected: true)).Message);
        GhostProtectionScenario.RequireTargeted(Watch(true, false), Hostile, A, expected: false);
        Assert.Contains("targeted protected", Assert.Throws<InvalidOperationException>(() => GhostProtectionScenario.RequireTargeted(Watch(true, true), Hostile, A, expected: false)).Message);
        Assert.Contains("control did not show", Assert.Throws<InvalidOperationException>(() => GhostProtectionScenario.RequireTargeted(Watch(false, false), Hostile, A, expected: true)).Message);
    }

    // replicated: B's process reads A's ghost mode (the fix); otherwise B reads A as visible and its hostile targets A (#261's bug).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProtectedPlayerIsInvisibleToAHostileAnotherClientOwnsAndTheControlSeesIt(bool replicated)
    {
        using var world = new CampaignWorld();
        var plan = world.Plan(AcceptancePlan.GhostProtectionScenarioName);
        plan.SecondClient = CampaignWorld.ClientPlan(port: 5557);
        plan.SecondArrival = new Site { X = 100, Z = -32, Ground = 42.4f };
        var report = new ScenarioReport("ghost-protection");
        // A is a ghost after each protection (every join protects) until the control turns ghost mode off.
        ScriptedTransport? a = null;
        int offAfter = -1;
        bool AGhost() => offAfter < 0 || a!.Count("cli_set_player_safety true") > offAfter;
        a = OwnershipHandoffTests.ReadyClient(world, plan)
            .Extension("acceptancemod.testing", "ai-watch", _ => new
            {
                source = "acceptancemod-ai-watch", complete = true, self = "101",
                players = new object[] { new { name = A, local = true, ghost = AGhost() } }, creatures = Array.Empty<object>(),
            })
            .Extension("acceptancemod.testing", "ghost-mode", _ => { offAfter = a!.Count("cli_set_player_safety true"); return new { source = "acceptancemod-ai-watch", complete = true, ghost = false }; }, readOnly: false);
        var b = OwnershipHandoffTests.ReadySecond(plan, wrongOwner: false)
            .Extension("acceptancemod.testing", "creature-spawn", _ => new { source = "acceptancemod-ai-watch", complete = true, zdo = Hostile, prefab = "Skeleton", owner = "202", ownedHere = true }, readOnly: false)
            .Extension("acceptancemod.testing", "ai-watch", _ => Watch(remoteGhost: replicated && AGhost(), targetedA: !(replicated && AGhost())))
            .Extension("acceptancemod.testing", "creature-remove", _ => new { source = "acceptancemod-ai-watch", complete = true, zdo = Hostile, removed = true }, readOnly: false);
        var run = world.Run(plan, report, campaignClient: (_, name) =>
            ClientSession.Attach(CampaignWorld.ClientPlan(port: name == "client-a" ? 5556 : 5557), world.Output, name == "client-a" ? a : b));

        if (replicated)
        {
            GhostProtectionScenario.Run(run, plan);
            Assert.True(report.Passed, string.Join("; ", report.Steps.Where(step => !step.Passed).Select(step => step.Name + ": " + step.Error)));
            Assert.Equal(1, a.Count("cli_extension acceptancemod.testing/ghost-mode")); // The control, once.
            Assert.Contains("ghost-watch-control", report.Provenance.Keys);
            Assert.Contains("ghost-watch-rejoin", report.Provenance.Keys);
            Assert.Equal(2, a.Count("cli_extension valheim.session/join")); // Once, and once more for the rejoin.
            Assert.Equal(2, a.Count("cli_set_player_safety true")); // Each join protects.
        }
        else
        {
            Assert.Contains("ghost=False", Assert.Throws<InvalidOperationException>(() => GhostProtectionScenario.Run(run, plan)).Message);
            Assert.Equal("B reads A as a ghost", report.Steps.First(step => !step.Passed).Name);
            Assert.Contains("player:" + A, report.Provenance["ghost-watch-protected"]); // The evidence keeps the attack.
            Assert.Equal(0, a.Count("cli_extension acceptancemod.testing/ghost-mode"));
        }
        // Each watch names the point, the radius and the window, in that order.
        Assert.Contains(b.Commands, c => c == "cli_extension acceptancemod.testing/ai-watch 100 -32 30 20");
        Assert.Contains(a.Commands, c => c.StartsWith("cli_extension acceptancemod.testing/ai-watch " + CampaignSteps.Number(plan.Arrival.X) + " ", StringComparison.Ordinal));
        // B spawned the hostile once and removed it once; both clients stopped in every outcome.
        Assert.Equal(1, b.Count("cli_extension acceptancemod.testing/creature-spawn"));
        Assert.Equal(1, b.Count("cli_extension acceptancemod.testing/creature-remove"));
        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
        Assert.Equal(1, b.Count("cli_extension valheim.session/join"));
    }
}
