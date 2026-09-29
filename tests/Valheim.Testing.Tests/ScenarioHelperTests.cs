using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public class ScenarioHelperTests
{
    [Fact] public async Task StepAsyncRecordsOutcomesAndWrapsFailuresWhenAsked()
    {
        var report = new ScenarioReport("steps");
        await report.StepAsync("waits", () => Task.Delay(1));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => report.StepAsync("drains", () => throw new TimeoutException("still 3"), "Queue did not drain."));
        Assert.Equal("Queue did not drain.", error.Message); Assert.IsType<TimeoutException>(error.InnerException);
        await Assert.ThrowsAsync<TimeoutException>(() => report.StepAsync("unwrapped", () => throw new TimeoutException("as is")));
        await Assert.ThrowsAsync<OperationCanceledException>(() => report.StepAsync("cancelled", () => throw new OperationCanceledException(), "never wraps cancellation"));
        Assert.Equal(new[] { true, false, false, false }, report.Steps.Select(s => s.Passed));
        Assert.Contains("still 3", report.Steps[1].Error);
    }
    [Fact] public void ARecordedFailureFailsTheReport()
    {
        var report = new ScenarioReport("runner"); report.Step("ok", () => { });
        report.RecordFailure("runner failed", new IOException("disk full"));
        Assert.False(report.Passed); Assert.Equal("disk full", report.Steps[^1].Error);
    }
    [Fact] public void ObserveCompleteRefusesAnIncompleteOrForeignObservation()
    {
        bool complete = false; string source = "memory";
        using var actor = new ScriptedTransport().Extension("my.mod", "state", _ => new { source, complete }).Actor();
        var capability = actor.RequireCapability("my.mod/state");
        Assert.ThrowsAny<Exception>(() => actor.ObserveComplete(capability, "memory"));
        complete = true; Assert.True(actor.ObserveComplete(capability, "memory").Complete);
        source = "cache"; Assert.ThrowsAny<Exception>(() => actor.ObserveComplete(capability, "memory"));
    }
    [Fact] public void SaveConfirmedNeedsValheimClisConfirmationLine()
    {
        using var saving = new ScriptedTransport().Saves().Actor();
        Assert.Equal("OK: SAVE saveNumber=2", saving.SaveConfirmed());
        using var silent = new ScriptedTransport().On("cli_save", _ => ScriptedTransport.Ok("OK")).Actor();
        Assert.Throws<InvalidOperationException>(() => silent.SaveConfirmed());
        using var refused = new ScriptedTransport().Saves(confirmed: false).Actor();
        Assert.Throws<InvalidOperationException>(() => refused.SaveConfirmed());
    }
    [Fact] public void CliBooleanRepliesMustHoldExactlyOneBoolean()
    {
        Assert.True(CliReply.Bool(["VALUE true"])); Assert.False(CliReply.Bool(["noise", "VALUE false"]));
        Assert.Throws<InvalidOperationException>(() => CliReply.Bool(["OK: true"]));
        Assert.Throws<InvalidOperationException>(() => CliReply.Bool(["VALUE true", "VALUE false"]));
        Assert.Throws<InvalidOperationException>(() => CliReply.Bool(["VALUE 1"]));
    }
    [Fact] public async Task ZonePreparationCreatesAMissingZoneOnceAndRecordsIt()
    {
        int reads = 0;
        var transport = new ScriptedTransport()
            .OnPrefix("cli_call ZoneSystem.instance.IsZoneGenerated ", _ => ScriptedTransport.Ok(reads++ == 0 ? "VALUE false" : "VALUE true"))
            .OnPrefix("cli_call ZoneSystem.instance.CreateGhostZones ", _ => ScriptedTransport.Ok("OK"));
        using var actor = transport.Actor(); var report = new ScenarioReport("prepare");
        await ZonePreparation.EnsureGeneratedAsync(actor, [(2, -3)], report, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, transport.Count("cli_call ZoneSystem.instance.CreateGhostZones"));
        Assert.Contains("cli_call ZoneSystem.instance.CreateGhostZones 128,0,-192", transport.Commands);
        Assert.Equal("prepared generated zone 2,-3", Assert.Single(report.Steps).Name); Assert.True(report.Passed);
    }
    [Fact] public async Task ZonePreparationGivesUpAtItsDeadline()
    {
        var transport = new ScriptedTransport()
            .OnPrefix("cli_call ZoneSystem.instance.IsZoneGenerated ", _ => ScriptedTransport.Ok("VALUE false"))
            .OnPrefix("cli_call ZoneSystem.instance.CreateGhostZones ", _ => ScriptedTransport.Ok("OK"));
        using var actor = transport.Actor(); var report = new ScenarioReport("prepare");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ZonePreparation.EnsureGeneratedAsync(actor, [(0, 0), (1, 0)], report, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5)));
        Assert.IsType<WaitTimeoutException>(error.InnerException);
        Assert.False(report.Passed); Assert.Single(report.Steps); // the second zone is never attempted
    }
}
