using MyMod.SystemTests;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

namespace MyMod.IntegrationTests;

/// <summary>
/// MyMod's native session for a test class (#258 Q7): <c>session.json</c> beside the test project is the session manifest
/// <c>valheim-test session check</c> reads (read in place, so its relative paths resolve from there), and
/// <c>dry-site-lifecycle.plan.json</c> beside it is the scenario's plan template. The session has a dedicated server and one
/// client, named <c>client</c>, the plan's client section. Without those two files the test is skipped with that reason
/// (<see cref="SessionFactAttribute"/>). It carries the trait <c>Category=Native</c>, which this repository's validation leaves
/// out, so validation never launches the game even where the files exist. Each run writes a new evidence directory under
/// <c>session-runs/</c> in the test output.
/// </summary>
public sealed class MyModSession : GameSessionFixture<LifecyclePlan>
{
    public const string ManifestFile = "session.json";
    /// <summary>The test project's directory, where the private environment files sit (recorded at build time).</summary>
    public static string Directory => typeof(MyModSession).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "SessionDirectory").Value!;
    public static string Manifest => Path.Combine(Directory, ManifestFile);
    public static string PlanFile => Path.Combine(Directory, LifecyclePlan.LifecycleScenario + ".plan.json");
    /// <summary>Why the session cannot run here, or null.</summary>
    public static string? Missing =>
        !File.Exists(Manifest) ? $"No {ManifestFile} beside the test project: a native session needs its session manifest (see the FullLifecycle README)."
        : !File.Exists(PlanFile) ? $"No {Path.GetFileName(PlanFile)} beside {ManifestFile}." : null;

    protected override bool Available => Missing == null;

    protected override Task<int> RunAsync(Func<GameSession, LifecyclePlan, Task> run)
    {
        var plan = ServerRunPlan.Read<LifecyclePlan>(PlanFile);
        if (plan.Scenario != LifecyclePlan.LifecycleScenario) throw new ArgumentException($"{Path.GetFileName(PlanFile)} is a {plan.Scenario} plan.");
        _ = Clients(plan); // A template without its client section is refused before anything starts.
        string output = Path.Combine(AppContext.BaseDirectory, "session-runs", $"{plan.Scenario}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
        return PinnedServerRun.RunCampaignAsync(Manifest, plan, Clients, output, DrySiteScenario.RunnerOptions(run));
    }

    /// <summary>The session's one client, <c>client</c>: the plan's client section.</summary>
    public static IReadOnlyDictionary<string, ClientRunPlan> Clients(LifecyclePlan plan) => new Dictionary<string, ClientRunPlan>
    {
        ["client"] = plan.Client ?? throw new ArgumentException("The session's plan needs its client section."),
    };
}

/// <summary>A test that needs the native session: skipped, with the reason, when <c>session.json</c> or the plan is absent.</summary>
public sealed class SessionFactAttribute : FactAttribute
{
    public SessionFactAttribute() { if (MyModSession.Missing is { } why) Skip = why; }
}

/// <summary>The dry-site scenario as an async test on the native session; the console's <c>run</c> calls the same method.</summary>
[Trait("Category", "Native")]
public sealed class DrySiteSessionTests(MyModSession fixture) : IClassFixture<MyModSession>
{
    [SessionFact]
    public Task TheMarkerSurvivesASaveAndARestart() => fixture.Test("dry-site lifecycle", () => Task.Run(() => DrySiteScenario.Run(fixture.Session, fixture.Plan)));
}
