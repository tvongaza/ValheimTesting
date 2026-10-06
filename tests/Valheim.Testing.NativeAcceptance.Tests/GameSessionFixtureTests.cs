using MyMod.IntegrationTests;
using MyMod.SystemTests;
using Valheim.Testing.NativeAcceptance;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance.Tests;

// GameSessionFixture (the FullLifecycle example's copyable source, compiled here too) over a scripted world (#258 step 7): the
// fixture hands a started session to the tests, its disposal lets the run tear down, and a run that never started or ended
// badly fails the test class with its exit code. No game.
public sealed class GameSessionFixtureTests : IDisposable
{
    private readonly CampaignWorld _world = new();
    public void Dispose() => _world.Dispose();

    // A runner like the toolkit's: start, the scenario, then the teardown and the run's verdict as its exit code.
    private sealed class ScriptedFixture(CampaignWorld world, bool startFails = false, bool cleanupFails = false) : GameSessionFixture<AcceptancePlan>
    {
        public ScenarioReport Report { get; } = new("fixture");
        public bool Unavailable { get; init; }
        public CancellationToken Cancellation { get; init; }
        protected override bool Available => !Unavailable;
        protected override async Task<int> RunAsync(Func<GameSession, AcceptancePlan, Task> scenario)
        {
            if (startFails) return 1;
            var plan = world.Plan(AcceptancePlan.SyncedConfigScenario);
            var session = world.Run(plan, Report, cancellation: Cancellation);
            try { await scenario(session, plan); }
            catch (Exception error) { Report.RecordFailure("scenario", error); }
            finally
            {
                await session.DisposeAsync();
                if (cleanupFails) Report.RecordFailure("cleanup", new IOException("the runtime copy could not be removed"));
            }
            return Report.Passed ? 0 : 1;
        }
    }

    [Fact] public async Task ATestDrivesTheStartedSessionAndDisposalTearsItDown()
    {
        var fixture = new ScriptedFixture(_world);
        await fixture.InitializeAsync();
        Assert.Equal(AcceptancePlan.SyncedConfigScenario, fixture.Plan.Scenario);
        fixture.Session.Report.Step("the test's own step", () => Assert.NotNull(fixture.Session.Server!.Game));
        Assert.Null(fixture.ExitCode); // The run waits for the test class.
        await fixture.DisposeAsync();
        Assert.Equal(0, fixture.ExitCode);
        Assert.Equal(new[] { "start and verify owned dedicated fixture", "the test's own step", "stop only owned server" }, fixture.Report.Steps.Select(step => step.Name));
    }

    [Fact] public async Task AFailedTestFailsTheRunsReportAndTheFixturesDisposal()
    {
        var fixture = new ScriptedFixture(_world);
        await fixture.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Test("the test", () => throw new InvalidOperationException("two peers expected")));
        Assert.Contains(fixture.Report.Steps, step => step.Name == "the test" && !step.Passed);
        Assert.Contains("exit code 1", (await Assert.ThrowsAsync<InvalidOperationException>(fixture.DisposeAsync)).Message);
    }

    [Fact] public async Task ACancelledRunEndsItsScenarioWithoutTheTestClass()
    {
        using var cancel = new CancellationTokenSource();
        var fixture = new ScriptedFixture(_world) { Cancellation = cancel.Token };
        await fixture.InitializeAsync();
        await cancel.CancelAsync(); // Ctrl+C: the run's scenario returns at once and the run tears down.
        Assert.True(SpinWait.SpinUntil(() => fixture.Report.Steps.Any(step => step.Name == "stop only owned server"), TimeSpan.FromSeconds(20)));
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.DisposeAsync); // The run ended failed (cancelled).
    }

    [Fact] public async Task ASessionThatNeverStartedFailsWithItsExitCodeAndRunsNothing()
    {
        var fixture = new ScriptedFixture(_world, startFails: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.InitializeAsync);
        Assert.Contains("exit code 1", error.Message);
        Assert.Null(fixture.Session);
        await fixture.DisposeAsync(); // Nothing more to end.
        Assert.Equal(1, fixture.ExitCode);
    }

    [Fact] public async Task ARunThatFailsAfterTheTestsFailsTheFixturesDisposal()
    {
        var fixture = new ScriptedFixture(_world, cleanupFails: true);
        await fixture.InitializeAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.DisposeAsync);
        Assert.Contains("exit code 1", error.Message);
        Assert.False(fixture.Report.Passed);
    }

    [Fact] public async Task TwoTestClassesNeverShareASession()
    {
        using var other = new CampaignWorld();
        var first = new ScriptedFixture(_world);
        var second = new ScriptedFixture(other);
        await first.InitializeAsync(); await second.InitializeAsync();
        Assert.NotSame(first.Session, second.Session);
        await first.DisposeAsync(); await second.DisposeAsync();
        Assert.Equal((0, 0), (first.ExitCode, second.ExitCode));
    }

    [Fact] public async Task AFixtureThatCannotRunHereStartsNothing()
    {
        var fixture = new ScriptedFixture(_world, startFails: true) { Unavailable = true };
        await fixture.InitializeAsync();
        await fixture.DisposeAsync();
        Assert.Null(fixture.ExitCode);
        Assert.Empty(fixture.Report.Steps);
    }

    // The suite's native sessions are never skipped (#258 Q7): without session.json beside the tests, one fails at its start
    // with that reason, before anything is read or launched.
    [Fact] public async Task AnAcceptanceSessionWithoutItsEnvironmentFailsAtItsStart()
    {
        // No machine has this scenario's plan; without session.json the manifest is named instead.
        var session = new NoPlanSession();
        string expected = File.Exists(AcceptanceSession.Manifest) ? $"No {NoPlanSession.Scenario}.plan.json" : "No session.json";
        Assert.Contains(expected, (await Assert.ThrowsAsync<InvalidOperationException>(session.InitializeAsync)).Message);
        await session.DisposeAsync(); // Nothing started, nothing to end.
        Assert.Null(session.ExitCode);
    }

    private sealed class NoPlanSession() : AcceptanceSession(Scenario) { public const string Scenario = "no-such-scenario"; }
}
