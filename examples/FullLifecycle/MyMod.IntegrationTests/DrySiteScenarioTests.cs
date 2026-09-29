using System.Text.Json;
using MyMod.SystemTests;
using Valheim.Testing.Game;
using Xunit;

namespace MyMod.IntegrationTests;

/// <summary>
/// The scenario's own logic against scripted game replies: what passes, what must fail, and that the client is always
/// cleaned up. Nothing here starts Valheim, Steam or a network connection.
/// </summary>
public sealed class DrySiteScenarioTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("mymod-integration-").FullName;
    private readonly TestWorld _world = new();
    private FakeClientProcess? _process;
    public void Dispose() => Directory.Delete(_output, recursive: true);

    private ScenarioReport Run(LifecyclePlan plan, FakeClientProcess? process = null)
    {
        _process = process ?? new FakeClientProcess();
        var report = new ScenarioReport("mymod-system-test");
        var server = _world.Server();
        Func<ClientSession> open = plan.Client!.Owned
            ? () => ClientSession.Launch(plan.Client, _output, () => _process, () => _world.Client(plan), (_, _) => Task.CompletedTask)
            : () => ClientSession.Attach(plan.Client, _output, _world.Client(plan));
        try { DrySiteScenario.Run(plan, server, _world.Restart, open, Joinable, report, _output, settleFor: TimeSpan.Zero); }
        catch (Exception) { Assert.False(report.Passed); }
        return report;
    }
    private static void Joinable(GameActor server) => OwnedServerSession.WaitUntilJoinable(server, "mymod.testing/session", TimeSpan.FromSeconds(5));
    private static string[] Failed(ScenarioReport report) => report.Steps.Where(s => !s.Passed).Select(s => s.Name).ToArray();

    [Fact] public void TheWholeLifecyclePassesAndStopsTheOwnedClientOnce()
    {
        var report = Run(_world.Plan());
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Equal(2, _world.MarkCommands); // One per site, never repeated.
        Assert.Equal(1, _world.Restarts);
        Assert.Equal(1, _process!.Stops);
        Assert.Equal(2, _world.ClientTransport!.Count("cli_extension valheim.session/join"));
        Assert.Contains(report.Steps, s => s.Name == "after-restart: the client sees the marker at the dry site" && s.Passed);
    }

    [Fact] public void EveryJoinProtectsThePlayerBeforeArrivalWithoutAnExplicitStep()
    {
        var report = Run(_world.Plan());
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        var commands = _world.ClientTransport!.Commands.ToList();
        Assert.Equal(2, commands.Count(c => c == "cli_set_player_safety true")); // Once per round, never repeated.
        // Each round: join, then protection, then the arrival's intro skip.
        int join = commands.FindIndex(c => c.StartsWith("cli_extension valheim.session/join", StringComparison.Ordinal));
        int protect = commands.IndexOf("cli_set_player_safety true");
        int arrive = commands.FindIndex(c => c.StartsWith("cli_skip_intro", StringComparison.Ordinal));
        Assert.True(join < protect && protect < arrive, $"join {join}, protection {protect}, arrival {arrive}");
        Assert.DoesNotContain(commands, c => c.StartsWith("cli_fly", StringComparison.Ordinal));
    }

    [Fact] public void AnUnconfirmedProtectionFailsTheJoinAndThePlayerIsNeverMoved()
    {
        _world.ConfirmProtection = false;
        var report = Run(_world.Plan());
        Assert.Equal("first: join the owned server with the disposable character, protected", Failed(report).First());
        Assert.Contains("ghost=False", report.Steps.First(s => !s.Passed).Error);
        Assert.Equal(1, _world.ClientTransport!.Count("cli_set_player_safety")); // Not retried or toggled.
        Assert.Equal(0, _world.ClientTransport!.Count("cli_skip_intro"));
        Assert.All(_world.Servers, server => Assert.Equal(0, server.Count("cli_teleport_peer")));
        Assert.Equal(1, _process!.Stops);
    }

    [Fact] public void AMarkWhoseReplyIsLostFailsAndIsNotRetried()
    {
        _world.LoseMarkReply = true;
        var report = Run(_world.Plan());
        Assert.Equal(new[] { "the mod marks the dry site" }, Failed(report));
        Assert.Equal(1, _world.MarkCommands);
        Assert.Null(_world.ClientTransport); // Nothing after the failure ran, not even the client launch.
    }

    [Fact] public void AnIncompleteServerObservationIsAFailureNotAnEmptyResult()
    {
        _world.OmitServerSummary = true;
        var report = Run(_world.Plan());
        Assert.Equal(new[] { "no marker at either site before the mod acts" }, Failed(report));
        Assert.Equal(0, _world.MarkCommands);
    }

    [Fact] public void AnUnconfirmedSaveStopsBeforeTheRestartAndStillStopsTheClient()
    {
        _world.ConfirmSaves = false;
        var report = Run(_world.Plan());
        Assert.Equal(new[] { "confirmed world save" }, Failed(report).Take(1));
        Assert.Equal(0, _world.Restarts);
        Assert.Equal(1, _process!.Stops);
        Assert.Contains(report.Steps, s => s.Name == "stop only the owned client" && s.Passed);
    }

    [Fact] public void AMarkerTheClientCannotSeeFailsAndTheOwnedClientIsStopped()
    {
        _world.ClientSeesMarkers = false;
        var report = Run(_world.Plan());
        Assert.Equal("first: the client sees the marker at the dry site", Failed(report).First());
        Assert.Equal(0, _world.Restarts);
        Assert.Equal(1, _process!.Stops);
    }

    [Fact] public void AnOwnedClientThatExitsDuringStartupFailsFastAndIsCleanedUp()
    {
        var report = Run(_world.Plan(), new FakeClientProcess(exitDuringStartup: 3));
        Assert.Equal(new[] { "launch the owned client to its menu, plugins pinned" }, Failed(report));
        Assert.Contains("exited with code 3", report.Steps.Single(s => !s.Passed).Error);
        Assert.Equal(1, _process!.Stops);
        Assert.Null(_world.ClientTransport); // Never connected to a client that did not start.
        Assert.True(File.Exists(Path.Combine(_output, "client-process.json")));
    }

    [Fact] public void CancellingAWaitForTheMenuStopsTheOwnedClientAtOnce()
    {
        var plan = _world.Plan(); plan.Client!.StartSeconds = 10;
        var process = new FakeClientProcess();
        var report = new ScenarioReport("mymod-system-test");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => DrySiteScenario.Run(plan, _world.Server(), _world.Restart,
            () => ClientSession.Launch(plan.Client, _output, () => process, () => _world.Client(plan), (_, _) => Task.Delay(Timeout.Infinite), cancel.Token),
            Joinable, report, _output, settleFor: TimeSpan.Zero, cancellation: cancel.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(9), "cancellation, not the start deadline, ended the wait");
        Assert.Equal(1, process.Stops);
    }

    [Fact] public void TheClientIsReadyBeforeTheServerAcceptsConnectionsAndTheJoinWaits()
    {
        _world.ClosedReadings = 3; // A first boot: the socket opens after the client is already at its menu.
        var report = Run(_world.Plan());
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        var names = report.Steps.Select(s => s.Name).ToList();
        Assert.True(names.IndexOf("launch the owned client to its menu, plugins pinned") < names.IndexOf("first: the server accepts game connections"));
        Assert.True(names.IndexOf("first: the server accepts game connections") < names.IndexOf("first: join the owned server with the disposable character, protected"));
        Assert.True(_world.SessionReadings > 3);
    }

    [Fact] public void AnAttachedClientIsDetachedButNeverStopped()
    {
        var report = Run(_world.Plan("attach"));
        Assert.True(report.Passed);
        Assert.Contains(report.Steps, s => s.Name == "detach from the operator's client");
        Assert.Equal(0, _process!.Stops);
        Assert.True(_world.ClientTransport!.Disposed);
    }

    [Fact] public void AHumanVerdictIsRecordedButNeverScored()
    {
        var plan = _world.Plan(); plan.Review = new() { Enabled = true, Seconds = 10 };
        File.WriteAllText(Path.Combine(_output, "review.json"), JsonSerializer.Serialize(new { verdict = "fail", notes = "marker floats" }));
        var report = Run(plan);
        Assert.True(report.Passed); // The automated checks decide the result.
        Assert.Equal("fail", report.Provenance["humanReview"]);
        Assert.Equal("marker floats", report.Provenance["humanReviewNotes"]);
        Assert.DoesNotContain(report.Steps, s => s.Name.Contains("review", StringComparison.OrdinalIgnoreCase));
    }
}
