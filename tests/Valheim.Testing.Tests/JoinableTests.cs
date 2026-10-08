using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

public class JoinableTests
{
    private static GameActor Server(Func<int, object> reading, out Func<int> reads)
    {
        int n = 0; reads = () => n;
        return new ScriptedTransport().Extension("my.mod", "session", _ => reading(++n)).Actor();
    }

    [Fact] public void ReturnsOnceTheServerReportsItsSocketOpen()
    {
        using var server = Server(n => new { source = "owned-test-session", complete = true, acceptingConnections = n >= 3 }, out var reads);
        OwnedServerSession.WaitUntilJoinable(server, "my.mod/session", TimeSpan.FromSeconds(10));
        Assert.Equal(3, reads());
    }

    [Fact] public void TimesOutWithTheLastReadingAndNeverLonger()
    {
        using var server = Server(_ => new { source = "owned-test-session", complete = true, acceptingConnections = false }, out _);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = Assert.Throws<WaitTimeoutException>(() => OwnedServerSession.WaitUntilJoinable(server, "my.mod/session", TimeSpan.FromSeconds(1)));
        Assert.Contains("acceptingConnections", error.LastSeen);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
    }

    [Fact] public void AnAdapterThatDoesNotReportTheSocketIsRefusedAtOnce()
    {
        using var server = Server(_ => new { source = "owned-test-session", complete = true }, out var reads);
        Assert.Contains("acceptingConnections", Assert.Throws<InvalidOperationException>(() => OwnedServerSession.WaitUntilJoinable(server, "my.mod/session", TimeSpan.FromSeconds(10))).Message);
        Assert.Equal(1, reads());
    }

    [Fact] public void CancellationEndsTheWait()
    {
        using var server = Server(_ => new { source = "owned-test-session", complete = true, acceptingConnections = false }, out _);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.ThrowsAny<OperationCanceledException>(() => OwnedServerSession.WaitUntilJoinable(server, "my.mod/session", TimeSpan.FromSeconds(30), cancel.Token));
    }

    [Fact] public void AReadinessPinCommandThatNeverStartedIsRetriedWithFreshPins()
    {
        int checks = 0;
        int statuses = 0;
        using var transport = new ScriptedTransport()
            .Extension("my.mod", "session", _ => new { source = "owned-test-session", complete = true, acceptingConnections = true })
            .OnStatus(() => new Dictionary<string, string> { ["mainThreadIdleMs"] = ++statuses == 1 ? "3100" : "50" })
            .OnPrefix("cli_expect", _ => ++checks == 2
                ? new CommandResult { Ok = false, ErrorCode = "command_failed", Message = "ERROR: code=command_timeout message=Command #2 had not started and will not run." }
                : ScriptedTransport.Ok("OK: EXPECT"));
        using var server = transport.Actor();
        OwnedServerSession.WaitUntilJoinable(server, "my.mod/session", TimeSpan.FromSeconds(5));
        Assert.Equal(4, checks);
        Assert.Equal(1, transport.Count("cli_extension my.mod/session"));
    }

    [Fact] public void AReadinessObservationThatNeverStartedIsRetried()
    {
        int reads = 0;
        int statuses = 0;
        using var transport = new ScriptedTransport().Extension("my.mod", "session", _ =>
            new { source = "owned-test-session", complete = true, acceptingConnections = true })
            .OnStatus(() => new Dictionary<string, string> { ["mainThreadIdleMs"] = ++statuses == 1 ? "3100" : "50" })
            .OnPrefix("cli_extension my.mod/session", _ => ++reads == 1
                ? new CommandResult { Ok = false, ErrorCode = "command_failed", Message = "ERROR: code=command_timeout message=Command #4 had not started and will not run." }
                : ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("my.mod", new { source = "owned-test-session", complete = true, acceptingConnections = true })));
        using var server = transport.Actor();
        OwnedServerSession.WaitUntilJoinable(server, "my.mod/session", TimeSpan.FromSeconds(5));
        Assert.Equal(2, reads);
    }

    [Fact] public void AFailedReadinessPinIsNotTreatedAsWorldLoading()
    {
        int checks = 0;
        using var transport = new ScriptedTransport()
            .Extension("my.mod", "session", _ => new { source = "owned-test-session", complete = true, acceptingConnections = true })
            .OnPrefix("cli_expect", _ => ++checks == 2
                ? new CommandResult { Ok = false, ErrorCode = "command_failed", Message = "ERROR: code=expectation_mismatch mismatches=1" }
                : ScriptedTransport.Ok("OK: EXPECT"));
        using var server = transport.Actor();
        Assert.Contains("expectation_mismatch", Assert.Throws<InvalidOperationException>(() =>
            OwnedServerSession.WaitUntilJoinable(server, "my.mod/session", TimeSpan.FromSeconds(5))).Message);
        Assert.Equal(2, checks);
    }

    [Fact] public void AnOwnedSessionWaitsOnItsOwnSessionCapabilityWithinItsStartupDeadline()
    {
        using var server = Server(_ => new { source = "owned-test-session", complete = true, acceptingConnections = false }, out var reads);
        using var session = new FakeOwnedServer("my.mod").Session(TimeSpan.FromSeconds(1));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = Assert.Throws<WaitTimeoutException>(() => session.WaitUntilJoinable(server));
        Assert.Contains("acceptingConnections", error.LastSeen);
        Assert.True(reads() >= 1);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
        // Negative control: a session for another adapter reads its own capability, which this server does not offer.
        using var other = new FakeOwnedServer("other.mod").Session(TimeSpan.FromSeconds(1));
        Assert.Contains("other.mod/session", Assert.Throws<InvalidOperationException>(() => other.WaitUntilJoinable(server)).Message);
    }
}
