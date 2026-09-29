using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
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
}
