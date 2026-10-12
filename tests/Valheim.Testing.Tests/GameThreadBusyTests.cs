using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

public class GameThreadBusyTests
{
    /// <summary>A clock that moves only when a test says so, so a deadline's arithmetic never depends on how fast the host runs.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
    }

    /// <summary>
    /// Lists capabilities only after <paramref name="listingTime"/> of the world-entry deadline has gone, as a slow game would,
    /// by moving the actor's clock rather than sleeping. Nothing else moves that clock, so each read's time left is exact.
    /// </summary>
    private sealed class LaggedTransport(ScriptedTransport inner, ManualClock clock, TimeSpan listingTime, TimeSpan pinTime, int pinsSlowAfterReads) : IGameTransport, IGameThreadStatusTransport
    {
        private readonly List<TimeSpan> _sessionReads = [];
        private bool _listed;
        /// <summary>The time each session-state read was given to start and finish.</summary>
        public IReadOnlyList<TimeSpan> SessionReadTimeouts => _sessionReads;
        public CommandResult Execute(string command, TimeSpan timeout)
        {
            if (command == "cli_extensions") { clock.Advance(listingTime); _listed = true; }
            // A slow pin check (its round trip is on the same clock) once the listing and the given number of reads are done.
            if (command.StartsWith("cli_expect", StringComparison.Ordinal) && _listed && _sessionReads.Count >= pinsSlowAfterReads) clock.Advance(pinTime);
            if (command == "cli_extension valheim.session/state")
            {
                _sessionReads.Add(timeout);
                // A game thread answers a read given under 25 ms as not started: too little time to start it.
                if (timeout <= TimeSpan.FromMilliseconds(25)) return Unstarted();
            }
            return inner.Execute(command, timeout);
        }
        public IReadOnlyDictionary<string, string> ReadStatus() => new Dictionary<string, string> { ["mainThreadIdleMs"] = "10" };
        public void Dispose() => inner.Dispose();
    }

    private static GameActor LoadingServer(int listingMs, out LaggedTransport transport, int pinMs = 0, int pinsSlowAfterReads = 0)
    {
        var inner = new ScriptedTransport().Extension("valheim.session", "state", _ => new
        {
            source = "session-state", complete = true, phase = "loading", worldUid = "7",
            worldPresent = true, worldReady = false, server = true, dedicated = true,
            localPlayer = false, playerReady = false, saving = false, loadError = false, connectionStatus = "Connecting"
        });
        var clock = new ManualClock();
        transport = new LaggedTransport(inner, clock, TimeSpan.FromMilliseconds(listingMs), TimeSpan.FromMilliseconds(pinMs), pinsSlowAfterReads);
        var actor = new GameActor("server", transport) { Clock = clock };
        actor.VerifyEnvironment("cli_expect worlduid=7");
        return actor;
    }

    // The world wait's 350 ms deadline starts before the capability listing. The listing's time is on the actor's manual
    // clock, so what is left for the first session read is exact: 350 ms minus the listing, on any host at any speed.
    // With at most 200 ms left, the wait's own minimum read time ends it after that one read whatever the real time.
    [Fact]
    public void DelayedCapabilityListingDoesNotRestartTheWorldWaitDeadline()
    {
        using var actor = LoadingServer(150, out var transport);
        var error = Assert.Throws<WaitTimeoutException>(() =>
            new SessionControl(actor).WaitForWorld("7", TimeSpan.FromMilliseconds(350)));
        Assert.Contains("phase loading", error.Message);
        Assert.DoesNotContain("game thread to resume", error.Message);
        // A restarted deadline would give this read the full 350 ms.
        Assert.Equal(TimeSpan.FromMilliseconds(200), Assert.Single(transport.SessionReadTimeouts));
    }

    [Theory]
    [InlineData(324, 26)]
    [InlineData(200, 150)]
    public void ReadsStartedWithTheDeadlineNearlyGoneStillReportTheLastSessionState(int listingMs, int leftMs)
    {
        using var actor = LoadingServer(listingMs, out var transport);
        var error = Assert.Throws<WaitTimeoutException>(() =>
            new SessionControl(actor).WaitForWorld("7", TimeSpan.FromMilliseconds(350)));
        Assert.Contains("phase loading", error.Message);
        Assert.DoesNotContain("game thread to resume", error.Message);
        Assert.Equal(TimeSpan.FromMilliseconds(leftMs), Assert.Single(transport.SessionReadTimeouts));
    }

    // Under 25 ms left, no read is started: the wait ends as a timeout that says the deadline passed, never as a stalled
    // game thread. A slow host used to land here by chance from the 150 ms sleep in the test above.
    [Theory]
    [InlineData(325)]
    [InlineData(350)]
    [InlineData(1000)]
    public void AListingThatUsesUpTheDeadlineEndsTheWaitBeforeAnyReadStarts(int listingMs)
    {
        using var actor = LoadingServer(listingMs, out var transport);
        var error = Assert.Throws<WaitTimeoutException>(() =>
            new SessionControl(actor).WaitForWorld("7", TimeSpan.FromMilliseconds(350)));
        Assert.Contains("deadline passed before another session read could start", error.Message);
        Assert.DoesNotContain("game thread to resume", error.Message);
        Assert.Empty(transport.SessionReadTimeouts);
    }

    // The guard lets a read start with 26 to 35 ms left, but the read's own pin check comes first. When that round trip uses
    // the difference, the read would be sent with under 25 ms, expire before starting and end the wait as a stalled game
    // thread or a harness fault. The deadline ends it instead, as the guard would have, once the pin check has taken its time.
    [Theory]
    [InlineData(316, 10)]
    [InlineData(316, 9)]
    [InlineData(300, 40)]
    public void APinCheckThatUsesUpTheLastOfTheDeadlineEndsTheWaitAsADeadlineTimeout(int listingMs, int pinMs)
    {
        using var actor = LoadingServer(listingMs, out var transport, pinMs);
        var error = Assert.Throws<WaitTimeoutException>(() =>
            new SessionControl(actor).WaitForWorld("7", TimeSpan.FromMilliseconds(350)));
        Assert.Contains("deadline passed before another session read could start", error.Message);
        Assert.DoesNotContain("game thread to resume", error.Message);
        Assert.Empty(transport.SessionReadTimeouts);
    }

    // A read after a successful one: the pin check uses the rest of the deadline, and the wait reports the state it last saw.
    [Fact]
    public void APinCheckThatUsesUpTheDeadlineAfterAReadStillReportsTheLastSessionState()
    {
        using var actor = LoadingServer(0, out var transport, pinMs: 2000, pinsSlowAfterReads: 1);
        var error = Assert.Throws<WaitTimeoutException>(() =>
            new SessionControl(actor).WaitForWorld("7", TimeSpan.FromMilliseconds(1000)));
        Assert.Contains("phase loading", error.Message);
        Assert.DoesNotContain("game thread to resume", error.Message);
        Assert.DoesNotContain("Harness fault", error.Message);
        Assert.Single(transport.SessionReadTimeouts);
    }

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
