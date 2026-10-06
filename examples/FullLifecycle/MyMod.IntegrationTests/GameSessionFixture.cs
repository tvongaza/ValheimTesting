using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

namespace MyMod.IntegrationTests;

/// <summary>
/// The xUnit adapter for a <see cref="GameSession"/> (#258 Q6: copy this file into your test project; it may move into a
/// package once the API settles). <see cref="InitializeAsync"/> runs the session to its scenario: <see cref="RunAsync"/> (the
/// toolkit's runner) prepares and starts every actor, and the "scenario" it is given only hands over the started session and
/// waits for the test class to finish. The tests then drive <see cref="Session"/> with <see cref="Plan"/>. <see cref="DisposeAsync"/>
/// lets the run go on to its teardown, report and scan, and fails when the run did (cleanup included). One session per test
/// class (<c>IClassFixture</c>); a session that never started fails every test with the run's exit code.
/// </summary>
public abstract class GameSessionFixture<TPlan> : IAsyncLifetime where TPlan : ServerRunPlan
{
    private readonly TaskCompletionSource<(GameSession, TPlan)> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<int>? _run;

    public GameSession Session { get; private set; } = null!;
    public TPlan Plan { get; private set; } = null!;
    /// <summary>The run's exit code once it ended: 0 passed, 1 failed, 3 unknown (see <see cref="PinnedServerRun.MainAsync{TPlan}(string[], PinnedServerRunOptions{TPlan})"/>).</summary>
    public int? ExitCode { get; private set; }
    /// <summary>Whether this fixture can run here (for example, its environment file exists); when false nothing starts.</summary>
    protected virtual bool Available => true;

    /// <summary>Runs the session with <paramref name="scenario"/> as its scenario and returns the run's exit code.</summary>
    protected abstract Task<int> RunAsync(Func<GameSession, TPlan, Task> scenario);

    public async Task InitializeAsync()
    {
        if (!Available) return;
        // The run's scenario: hand the started session over, then wait for the class to finish (or for Ctrl+C).
        _run = RunAsync((session, plan) => { _started.TrySetResult((session, plan)); return _finished.Task.WaitAsync(session.Cancellation); });
        if (await Task.WhenAny(_started.Task, _run) == _run)
        {
            ExitCode = await _run;
            throw new InvalidOperationException($"The session did not start (exit code {ExitCode}); its result.json says why. Nothing ran.");
        }
        (Session, Plan) = await _started.Task;
    }

    /// <summary>Runs one test on the session; its failure is also recorded in the session's report, so the run's result.json fails too.</summary>
    public async Task Test(string name, Func<Task> test)
    {
        try { await test(); }
        catch (Exception error) { Session.Report.RecordFailure(name, error); throw; }
    }

    public async Task DisposeAsync()
    {
        _finished.TrySetResult();
        if (_run == null || ExitCode != null) return;
        if ((ExitCode = await _run) != 0)
            throw new InvalidOperationException($"The session ended with exit code {ExitCode}: a step or its cleanup failed; its result.json says which.");
    }
}
