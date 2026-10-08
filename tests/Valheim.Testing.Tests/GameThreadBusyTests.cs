using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

public class GameThreadBusyTests
{
    private static CommandResult Unstarted() => new()
    {
        Ok = false, ErrorCode = "command_failed",
        Message = "ERROR: code=command_timeout message=Command #30 did not complete in time; it had not started and will not run."
    };

    private static ScriptedTransport Session(out Func<int> attempts, int unstartedReads = 1)
    {
        int count = 0;
        var transport = new ScriptedTransport()
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = "world-present", worldUid = "7",
                worldPresent = true, worldReady = true, server = true, dedicated = true,
                localPlayer = false, playerReady = false, saving = false, loadError = false, connectionStatus = "None"
            });
        transport.On("cli_extension valheim.session/state", command =>
            ++count <= unstartedReads ? Unstarted() : ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("valheim.session", new
            {
                source = "session-state", complete = true, phase = "world-present", worldUid = "7",
                worldPresent = true, worldReady = true, server = true, dedicated = true,
                localPlayer = false, playerReady = false, saving = false, loadError = false, connectionStatus = "None"
            })));
        attempts = () => count;
        return transport;
    }

    [Fact]
    public void BusyThreadRetriesOnlyTheUnstartedReadWithinTheWorldDeadline()
    {
        var transport = Session(out var attempts);
        int checks = 0;
        transport.OnStatus(() => new Dictionary<string, string>
        {
            ["mainThreadIdleMs"] = ++checks == 1 ? "3100" : "50", ["busy"] = "first%20generation"
        });
        using var actor = transport.Actor(expectations: "cli_expect worlduid=7");
        Assert.True(new SessionControl(actor).WaitForWorld("7", TimeSpan.FromSeconds(2)).WorldReady);
        Assert.Equal(2, attempts());
        Assert.True(actor.LongestMainThreadIdleMs >= 3100);
        Assert.Equal("first generation", actor.BusyNote);
        Assert.DoesNotContain(transport.Commands, command => command.EndsWith("/join", StringComparison.Ordinal));
    }

    [Fact]
    public void AStatusConnectionThatDoesNotAnswerFailsInsteadOfRetrying()
    {
        var transport = Session(out var attempts).OnStatus(() => throw new IOException("socket closed"));
        using var actor = transport.Actor(expectations: "cli_expect worlduid=7");
        Assert.Contains("stopped answering STATUS", Assert.Throws<InvalidOperationException>(() =>
            new SessionControl(actor).WaitForWorld("7", TimeSpan.FromSeconds(2))).Message);
        Assert.Equal(1, attempts());
    }

    [Fact]
    public void AStallThatEndsBeforeTheFirstStatusReadingRetriesTheReadOnce()
    {
        var transport = Session(out var attempts).OnStatus(() => new Dictionary<string, string> { ["mainThreadIdleMs"] = "15" });
        using var actor = transport.Actor(expectations: "cli_expect worlduid=7");
        Assert.True(new SessionControl(actor).WaitForWorld("7", TimeSpan.FromSeconds(2)).WorldReady);
        Assert.Equal(2, attempts());
    }

    [Fact]
    public void AResponsiveHeartbeatMakesAnUnstartedTimeoutAHarnessFault()
    {
        var transport = Session(out var attempts, unstartedReads: 2).OnStatus(() => new Dictionary<string, string> { ["mainThreadIdleMs"] = "15" });
        using var actor = transport.Actor(expectations: "cli_expect worlduid=7");
        Assert.Contains("Harness fault", Assert.Throws<InvalidOperationException>(() =>
            new SessionControl(actor).WaitForWorld("7", TimeSpan.FromSeconds(2))).Message);
        Assert.Equal(2, attempts());
    }

    [Fact]
    public void CancellationStopsABusyHeartbeatWaitWithoutRetrying()
    {
        using var stop = new CancellationTokenSource();
        var transport = Session(out var attempts).OnStatus(() =>
        {
            stop.Cancel();
            return new Dictionary<string, string> { ["mainThreadIdleMs"] = "3100" };
        });
        using var actor = transport.Actor(expectations: "cli_expect worlduid=7");
        Assert.Throws<OperationCanceledException>(() =>
            new SessionControl(actor).WaitForWorld("7", TimeSpan.FromSeconds(2), stop.Token));
        Assert.Equal(1, attempts());
    }

    [Fact]
    public void AQueuedMutationIsNeverRetried()
    {
        var transport = new ScriptedTransport().On("cli_extension valheim.session/join", _ => Unstarted());
        using var actor = transport.Actor();
        using (actor.BusyReadRetries(TimeSpan.FromSeconds(2)))
            Assert.Throws<InvalidOperationException>(() => actor.Execute("cli_extension valheim.session/join"));
        Assert.Equal(1, transport.Count("cli_extension valheim.session/join"));
    }
}
