using ExampleMod.Tests;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance.Tests;

/// <summary>
/// The suite's native session for a test class (#258 Q7): <c>session.json</c> beside this test project is the session manifest
/// <c>valheim-test session check</c> reads (read in place, so its relative paths resolve as they do for <c>campaign run</c>), and
/// <c>&lt;scenario&gt;.plan.json</c> beside it is the scenario's plan template. Unlike the FullLifecycle example's session, which
/// skips without them, a native acceptance test FAILS without them: a native gate that skips silently would approve itself.
/// Validation leaves these tests out (trait <c>Category=Native</c>); run them with <c>--filter Category=Native</c>. The session
/// classes share one collection, so two sessions never run at once. Each run writes a new evidence directory under
/// <c>session-runs/</c> in the test output.
/// </summary>
public abstract class AcceptanceSession(string scenario) : GameSessionFixture<AcceptancePlan>
{
    public const string ManifestFile = "session.json";
    /// <summary>The test project's directory, where the private environment files sit (recorded at build time).</summary>
    public static string Directory => typeof(AcceptanceSession).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "SessionDirectory").Value!;
    public static string Manifest => Path.Combine(Directory, ManifestFile);
    public static string PlanFile(string scenario) => Path.Combine(Directory, scenario + ".plan.json");
    /// <summary>Why a session for <paramref name="scenario"/> cannot run here, or null.</summary>
    public static string? Missing(string scenario) =>
        !File.Exists(Manifest) ? $"No {ManifestFile} beside the test project: a native acceptance session needs its session manifest (see the suite's README)."
        : !File.Exists(PlanFile(scenario)) ? $"No {scenario}.plan.json beside {ManifestFile}." : null;

    // Always available: a missing environment fails the session's start, and with it every test of the class.
    protected override Task<int> RunAsync(Func<GameSession, AcceptancePlan, Task> run)
    {
        if (Missing(scenario) is { } why) throw new InvalidOperationException(why);
        var plan = ServerRunPlan.Read<AcceptancePlan>(PlanFile(scenario));
        if (plan.Scenario != scenario) throw new ArgumentException($"{scenario}.plan.json is a {plan.Scenario} plan.");
        _ = ScenarioTable.CampaignClientsFor(plan); // Refuses a scenario that does not run as a campaign, before anything starts.
        string output = Path.Combine(AppContext.BaseDirectory, "session-runs", $"{scenario}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
        return PinnedServerRun.RunCampaignAsync(Manifest, plan, ScenarioTable.CampaignClientsFor, output, ScenarioTable.RunnerOptions(run));
    }
}

public sealed class OwnershipHandoffSession() : AcceptanceSession(AcceptancePlan.OwnershipHandoffScenario);
public sealed class ThreeActorSmokeSession() : AcceptanceSession(AcceptancePlan.ThreeActorScenario);

/// <summary>The native session classes: one at a time, since they share the inventory's hosts and Steam identities.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeSessions { public const string Name = "native sessions"; }

/// <summary>The two stateful campaign scenarios as async tests on a native session; the console's <c>campaign run</c> calls the same methods.</summary>
[Collection(NativeSessions.Name), Trait("Category", "Native")]
public sealed class OwnershipHandoffSessionTests(OwnershipHandoffSession fixture) : IClassFixture<OwnershipHandoffSession>
{
    [Fact]
    public Task TwoClientsHandTheMarkerOver() => fixture.Test("ownership handoff", () => Task.Run(() => OwnershipHandoffScenario.Run(fixture.Session, fixture.Plan)));
}

[Collection(NativeSessions.Name), Trait("Category", "Native")]
public sealed class ThreeActorSmokeSessionTests(ThreeActorSmokeSession fixture) : IClassFixture<ThreeActorSmokeSession>
{
    [Fact]
    public Task AServerAndTwoClientsJoinAndRejoin() => fixture.Test("three-actor smoke", () => Task.Run(() => ThreeActorSmokeScenario.Run(fixture.Session, fixture.Plan)));
}
