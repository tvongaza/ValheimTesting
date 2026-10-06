using MyMod.SystemTests;
using Valheim.Testing.Game;
using Xunit;

namespace MyMod.IntegrationTests;

/// <summary>
/// MyMod's native session for a test class (#258 Q7): <c>session.json</c> beside the test project is the campaign manifest
/// <c>valheim-test session check</c> reads (read in place, so its relative paths resolve as they do for <c>campaign run</c>), and
/// <c>&lt;scenario&gt;.plan.json</c> beside it is the scenario's plan template. Without them the class's tests are skipped with
/// that reason (<see cref="SessionFactAttribute"/>). The session classes share one collection, so two campaigns never run at
/// once. Each run writes a new evidence directory under <c>session-runs/</c> in the test output.
/// </summary>
public abstract class MyModSession(string scenario) : GameSessionFixture<LifecyclePlan>
{
    public const string ManifestFile = "session.json";
    /// <summary>The test project's directory, where the private environment files sit (recorded at build time).</summary>
    public static string Directory => typeof(MyModSession).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "SessionDirectory").Value!;
    public static string Manifest => Path.Combine(Directory, ManifestFile);
    public static string PlanFile(string scenario) => Path.Combine(Directory, scenario + ".plan.json");
    /// <summary>Why a session for <paramref name="scenario"/> cannot run here, or null.</summary>
    public static string? Missing(string scenario) =>
        !File.Exists(Manifest) ? $"No {ManifestFile} beside the test project: a native session needs its campaign manifest (see the FullLifecycle README)."
        : !File.Exists(PlanFile(scenario)) ? $"No {scenario}.plan.json beside {ManifestFile}." : null;

    protected override bool Available => Missing(scenario) == null;

    protected override Task<int> RunAsync(Func<GameSession, LifecyclePlan, Task> run)
    {
        var plan = ServerRunPlan.Read<LifecyclePlan>(PlanFile(scenario));
        if (plan.Scenario != scenario) throw new ArgumentException($"{scenario}.plan.json is a {plan.Scenario} plan.");
        _ = ScenarioTable.CampaignClientsFor(plan); // Refuses a scenario that does not run as a campaign, before anything starts.
        string output = Path.Combine(AppContext.BaseDirectory, "session-runs", $"{scenario}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
        return PinnedServerRun.RunCampaignAsync(Manifest, plan, ScenarioTable.CampaignClientsFor, output, ScenarioTable.RunnerOptions(run));
    }
}

/// <summary>A test that needs a native session: skipped, with the reason, when <c>session.json</c> or the scenario's plan is absent.</summary>
public sealed class SessionFactAttribute : FactAttribute
{
    public SessionFactAttribute(string scenario) { if (MyModSession.Missing(scenario) is { } why) Skip = why; }
}

public sealed class OwnershipHandoffSession() : MyModSession(LifecyclePlan.OwnershipHandoffScenario);
public sealed class ThreeActorSmokeSession() : MyModSession(LifecyclePlan.ThreeActorScenario);

/// <summary>The native session classes: one at a time, since they share the inventory's hosts and Steam identities.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeSessions { public const string Name = "native sessions"; }

/// <summary>The two stateful campaign scenarios as async tests on a native session; the console's <c>campaign run</c> calls the same methods.</summary>
[Collection(NativeSessions.Name)]
public sealed class OwnershipHandoffSessionTests(OwnershipHandoffSession fixture) : IClassFixture<OwnershipHandoffSession>
{
    [SessionFact(LifecyclePlan.OwnershipHandoffScenario)]
    public Task TwoClientsHandTheMarkerOver() => fixture.Test("ownership handoff", () => Task.Run(() => OwnershipHandoffScenario.Run(fixture.Session, fixture.Plan)));
}

[Collection(NativeSessions.Name)]
public sealed class ThreeActorSmokeSessionTests(ThreeActorSmokeSession fixture) : IClassFixture<ThreeActorSmokeSession>
{
    [SessionFact(LifecyclePlan.ThreeActorScenario)]
    public Task AServerAndTwoClientsJoinAndRejoin() => fixture.Test("three-actor smoke", () => Task.Run(() => ThreeActorSmokeScenario.Run(fixture.Session, fixture.Plan)));
}
