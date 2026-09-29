using System.Diagnostics;
using System.Text.Json;
using Valheim.Testing.Game;
using valheim_cli.Testing;
using Xunit;

public class SessionTests
{
    private static JsonElement Reply(string token, int pid, string path, bool complete = true, bool dedicated = true, string extension = "roads.testing") =>
        JsonSerializer.SerializeToElement(new { schemaVersion = 1, ok = true, extension, data = new { source = "owned-test-session", token, pid, saveRoot = path, complete, dedicated } });
    [Fact] public void AnotherModCanUseTheSameOwnedLifecycle()
    {
        var fake = new Host { Owner = "other.mod.tests" }; using var session = fake.Session();
        session.Start(); session.Restart(); Assert.Equal(2, fake.Tokens.Count);
    }
    [Fact] public void WrongAdapterCannotSatisfySessionIdentity() =>
        Assert.Throws<InvalidOperationException>(() => OwnedServerSession.CheckIdentity(Reply("ours", 7, Path.GetTempPath()), "ours", 7, Path.GetTempPath(), "other.mod.tests"));
    [Theory] [InlineData("no-slash")] [InlineData("owner/session argument")] [InlineData("owner/session/extra")]
    public void SessionCapabilityCannotContainCommandArguments(string capability)
    {
        var fake = new Host();
        Assert.Throws<ArgumentException>(() => new OwnedServerSession(fake.Launch, fake.Connect, Path.GetTempPath(), "cli_expect worlduid=1", capability, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Empty(fake.Tokens);
    }
    [Theory]
    [InlineData("token")][InlineData("pid")][InlineData("path")][InlineData("client")]
    public void WrongSessionNeverBecomesAnActor(string wrong)
    {
        var data = Reply(wrong == "token" ? "other" : "ours", wrong == "pid" ? 8 : 7,
            wrong == "path" ? Path.GetTempPath() : Path.Combine(Path.GetTempPath(), "copy"), dedicated: wrong != "client");
        Assert.Throws<InvalidOperationException>(() => OwnedServerSession.CheckIdentity(data, "ours", 7, Path.Combine(Path.GetTempPath(), "copy"), "roads.testing"));
    }
    [Fact] public void ConsoleStartupRefusalIsTransientButOtherFailuresAreNot()
    {
        Assert.True(OwnedServerSession.StartupUnavailable(new() { Ok = false, Output = ["Error: Console not available (game not fully loaded)"] }));
        Assert.False(OwnedServerSession.StartupUnavailable(new() { Ok = false, ErrorCode = "command_failed", Output = ["ERROR: strict pin mismatch"] }));
        Assert.False(OwnedServerSession.StartupUnavailable(new() { Ok = false, Output = ["some log mentions no_extension_command"] }));
    }
    [Fact] public void MissingCapabilityUsesStructuredCodeOnly()
    {
        Assert.True(OwnedServerSession.StartupUnavailable(new() { Ok = false, Output = ["EXTENSION_RESULT {\"code\":\"no_extension_command\"}"] }));
        Assert.False(OwnedServerSession.StartupUnavailable(new() { Ok = false, Output = ["EXTENSION_RESULT {broken"] }));
    }
    [Fact] public void IncompleteOwnedSessionIsNotReady() => Assert.False(OwnedServerSession.CheckIdentity(Reply("a", 1, Path.GetTempPath(), false), "a", 1, Path.GetTempPath(), "roads.testing"));
    [Fact] public void StartupPinsThenRestartStopsOldBeforeLaunchingAndRepins()
    {
        var fake = new Host(); using var session = fake.Session();
        session.Start(); session.Restart();
        Assert.Equal(new[] { "launch1", "probe1", "pins1", "disconnect1", "stop1", "dispose1", "launch2", "probe2", "pins2" }, fake.Events);
        Assert.NotEqual(fake.Tokens[0], fake.Tokens[1]);
    }
    [Fact] public void FailedStopNeverLaunchesReplacement()
    {
        var fake = new Host { RefuseStop = true }; var session = fake.Session(); session.Start();
        Assert.Throws<TimeoutException>(() => session.Restart()); Assert.Single(fake.Tokens);
        fake.RefuseStop = false; session.Dispose();
    }
    [Fact] public void IncorrectEnvironmentPinsDoNotRetryOrReturnActor()
    {
        var fake = new Host { BadPins = true }; using var session = fake.Session();
        Assert.Throws<InvalidOperationException>(() => session.Start());
        Assert.Equal(1, fake.Events.Count(x => x.StartsWith("pins")));
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("mutation"));
    }
    [Fact] public void WrongPidFailsImmediatelyWithoutPins()
    {
        var fake = new Host { BadPid = true }; using var session = fake.Session();
        Assert.Throws<InvalidOperationException>(() => session.Start()); Assert.DoesNotContain(fake.Events, x => x.StartsWith("pins"));
    }
    [Fact] public void ExitedProcessCannotBecomeReady()
    {
        var fake = new Host { ExitOnLaunch = true }; using var session = fake.Session();
        var error = Assert.Throws<WaitFailedException>(() => session.Start()); Assert.Contains("code 1", error.Reason);
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("probe"));
    }
    [Fact] public void CancelledSessionDoesNotLaunch()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var fake = new Host();
        using var session = new OwnedServerSession(fake.Launch, fake.Connect, Path.GetTempPath(), "cli_expect worlduid=1", "roads.testing/session", Generous, TimeSpan.FromSeconds(1), cancellation: cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => session.Start()); Assert.Empty(fake.Tokens);
    }
    [Fact] public void ConnectionLossHasBoundedStartupAndOwnedCleanup()
    {
        var fake = new Host { NoConnection = true }; var session = fake.Session(TimeSpan.FromMilliseconds(20));
        Assert.Throws<WaitTimeoutException>(() => session.Start()); session.Dispose();
        Assert.Contains("stop1", fake.Events);
    }
    [Fact] public void NoWorldReadinessCannotPassOnAListeningSocket()
    {
        var fake = new Host { Ready = false }; using var session = fake.Session(TimeSpan.FromMilliseconds(20));
        Assert.Throws<WaitTimeoutException>(() => session.Start()); Assert.DoesNotContain(fake.Events, x => x.StartsWith("pins"));
    }
    [Fact] public void RecordingIncludesFailedRepliesAndClosesTransport()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var fake = new Host { BadPins = true }; fake.Launch("t");
            using (var recording = new RecordingTransport(fake.Connect(), file)) recording.Execute("cli_expect worlduid=1", TimeSpan.FromSeconds(1));
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Assert.False(doc.RootElement.GetProperty("reply").GetProperty("Ok").GetBoolean()); Assert.Contains("disconnect1", fake.Events);
        }
        finally { File.Delete(file); }
    }
    [Fact] public void DirectProcessStopDoesNotStopAnotherProcess()
    {
        if (OperatingSystem.IsWindows()) return; // Fast Unix process test; Windows is the station gate.
        using var unrelated = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        string dir = Path.Combine(Path.GetTempPath(), "roads-process-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string log = Path.Combine(dir, "game.log"); File.WriteAllText(log, "boot one evidence");
            using var owned = new DirectServerProcess(new ProcessStartInfo("/bin/sleep", "30"), Path.Combine(dir, "owned"), log, Path.Combine(dir, "missing.log"));
            owned.Stop(TimeSpan.FromSeconds(3)); Assert.True(owned.HasExited); Assert.False(unrelated.HasExited);
            File.WriteAllText(log, "boot two overwrites live log");
            Assert.Equal("boot one evidence", File.ReadAllText(Path.Combine(dir, "owned.game-0.log")));
            Assert.True(File.Exists(Path.Combine(dir, "owned.game-1.log.absent")));
        }
        finally { unrelated.Kill(); unrelated.WaitForExit(); Directory.Delete(dir, true); }
    }
    private const string Listening = "[Info   :valheimCLI] Command server listening on 127.0.0.1:5555\n";
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
    // BepInEx 5's own chainloader messages, as its BepInEx.dll carries them.
    [Theory]
    [InlineData("[Error  :   BepInEx] Could not load [ProceduralRoads 1.9.0] because it has missing dependencies: com.jotunn.jotunn")]
    [InlineData("[Error  :   BepInEx] Could not load [ProceduralRoads 1.9.0] because it is incompatible with: other.plugin")]
    [InlineData("[Error  :   BepInEx] Error loading [ProceduralRoads 1.9.0] : Exception has been thrown by the target of an invocation.")]
    [InlineData("[Warning:   BepInEx] Could not load [testing.adapter 1.0.0] because it has missing dependencies: valheimCLI.valheimCLI")]
    public void BepInExPluginLoadFailuresMatch(string line) => Assert.Contains(StartupEvents.BepInExPluginLoadFailures, failure => failure.IsMatch(line));
    [Theory]
    [InlineData("[Info   :   BepInEx] Loading [ProceduralRoads 1.9.0]")]
    [InlineData("[Warning:   BepInEx] Skipping [ProceduralRoads 1.8.0] because a newer version exists (ProceduralRoads 1.9.0)")]
    [InlineData("[Error  :ProceduralRoads] Could not load [bridge piece] prefab")]
    [InlineData("[Info   :valheimCLI] Command server listening on 127.0.0.1:5577")]
    public void OrdinaryLinesAreNotPluginLoadFailures(string line) => Assert.DoesNotContain(StartupEvents.BepInExPluginLoadFailures, failure => failure.IsMatch(line));
    [Fact] public void NoConnectionIsTriedBeforeThisBootAnnouncesItsListener()
    {
        using var log = new TempLog(); log.Append("previous boot\n" + Listening);
        var fake = new Host(); using var session = fake.Session(TimeSpan.FromMilliseconds(300), new StartupEvents { CliLog = log.Path });
        var error = Assert.Throws<WaitTimeoutException>(() => session.Start());
        Assert.Contains("listening", error.Target); Assert.Equal(0, fake.Connects);
    }
    [Fact] public void TheListeningLineLeadsToOneProbeWithoutRetries()
    {
        using var log = new TempLog();
        var fake = new Host { OnLaunch = _ => log.Append("BepInEx loading\n" + Listening) };
        using var session = fake.Session(Generous, new StartupEvents { CliLog = log.Path });
        session.Start();
        Assert.Equal(new[] { "launch1", "probe1", "pins1" }, fake.Events); Assert.Equal(1, fake.Connects);
    }
    [Fact] public void AFailureLineEndsStartupBeforeAnyConnection()
    {
        using var log = new TempLog();
        var fake = new Host { OnLaunch = _ => log.Append("[Error  : BepInEx] Could not load [roads.testing]\n") };
        using var session = fake.Session(Generous, new StartupEvents { CliLog = log.Path, Failures = [LogWait.Literal("[Error  : BepInEx]")] });
        var error = Assert.Throws<WaitFailedException>(() => session.Start());
        Assert.Equal("[Error  : BepInEx] Could not load [roads.testing]", error.LastSeen); Assert.Equal(0, fake.Connects);
    }
    [Fact] public void AnEarlyExitEndsStartupAtOnceWithItsCodeAndLastLogLine()
    {
        using var log = new TempLog();
        var fake = new Host { OnLaunch = process => { log.Append("Fatal: port in use\n"); process.Exit(3); } };
        using var session = fake.Session(Generous, new StartupEvents { CliLog = log.Path });
        var error = Assert.Throws<WaitFailedException>(() => session.Start());
        Assert.Contains("code 3", error.Reason); Assert.Equal("Fatal: port in use", error.LastSeen);
        Assert.True(error.Elapsed < Generous); Assert.Equal(0, fake.Connects);
    }
    [Fact] public async Task StartupWaitsForTheWorldStateBeforeProbing()
    {
        using var log = new TempLog(); using var server = new FakeCliServer(StateWait.Loading);
        var fake = new Host { OnLaunch = _ => log.Append(Listening) };
        using var session = fake.Session(Generous, new StartupEvents { CliLog = log.Path, States = () => new StateWait(server.Connect()) });
        var start = Task.Run(session.Start);
        await server.Subscribed.WaitAsync(Generous);
        // Loading, and only a push can change that here: no probe may have run.
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("probe"));
        server.Push(StateWait.InWorldNoPlayer);
        await start.WaitAsync(Generous);
        Assert.Equal(new[] { "launch1", "probe1", "pins1" }, fake.Events);
    }
    [Fact] public async Task AnExitWhileTheWorldLoadsEndsStartupAtOnce()
    {
        using var log = new TempLog(); using var server = new FakeCliServer(StateWait.Loading);
        Host.FakeProcess? launched = null;
        var fake = new Host { OnLaunch = process => { launched = process; log.Append(Listening); } };
        using var session = fake.Session(Generous, new StartupEvents { CliLog = log.Path, States = () => StateWait.Connect("127.0.0.1", server.Port) });
        var start = Task.Run(session.Start);
        await server.Subscribed.WaitAsync(Generous);
        log.Append("Out of memory\n"); launched!.Exit(5);
        var error = await Assert.ThrowsAsync<WaitFailedException>(() => start);
        Assert.Contains("code 5", error.Reason); Assert.Equal("Out of memory", error.LastSeen);
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("probe"));
    }
    // The startup deadline bounds blocking calls too: the fakes below block until released, ignoring their timeouts.
    [Fact] public void AStalledConnectionCannotOutliveTheStartupDeadline()
    {
        using var gate = new ManualResetEventSlim();
        var fake = new Host { ConnectGate = gate }; using var session = fake.Session(TimeSpan.FromSeconds(1));
        var clock = Stopwatch.StartNew();
        Assert.Throws<WaitTimeoutException>(() => session.Start());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref fake.Connects)); // The deadline expired in the stalled connection, the stage under test.
        // The connection that completes afterwards is closed, not leaked.
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref fake.Disconnects) == 1, TimeSpan.FromSeconds(10)));
    }
    [Fact] public void AnExitWhileConnectingEndsStartupAtOnce()
    {
        using var gate = new ManualResetEventSlim();
        var fake = new Host { ConnectGate = gate };
        // The exit comes once the connection is under way, the stage under test.
        fake.OnLaunch = process => _ = Task.Run(() => { fake.ConnectEntered.Wait(TimeSpan.FromSeconds(30)); process.Exit(4); });
        using var session = fake.Session(Generous);
        var clock = Stopwatch.StartNew();
        var error = Assert.Throws<WaitFailedException>(() => session.Start());
        Assert.Contains("code 4", error.Reason); Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        gate.Set();
    }
    [Fact] public void VerificationGetsOnlyTheTimeLeftAndTheActorKeepsItsCommandTimeout()
    {
        var fake = new Host(); using var session = fake.Session(TimeSpan.FromSeconds(30), command: TimeSpan.FromSeconds(60));
        var actor = session.Start();
        Assert.Single(fake.PinTimeouts); Assert.True(fake.PinTimeouts[0] <= TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(60), actor.CommandTimeout);
    }
    [Fact] public void AStalledVerificationCannotOutliveTheStartupDeadline()
    {
        using var gate = new ManualResetEventSlim();
        var fake = new Host { PinGate = gate }; using var session = fake.Session(TimeSpan.FromSeconds(2), command: TimeSpan.FromSeconds(60));
        var clock = Stopwatch.StartNew();
        Assert.Throws<WaitTimeoutException>(() => session.Start());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains("pins1", fake.Snapshot()); // The deadline expired in verification, the stage under test.
        // The abandoned verification's transport was closed at once; the actor is disposed when the command returns.
        Assert.Equal(1, Volatile.Read(ref fake.Disconnects));
        gate.Set();
    }
    [Fact] public void AnExitDuringVerificationEndsStartupAtOnce()
    {
        using var gate = new ManualResetEventSlim();
        var fake = new Host { PinGate = gate };
        // The exit comes once verification is under way, the stage under test.
        fake.OnLaunch = process => _ = Task.Run(() => { fake.PinEntered.Wait(TimeSpan.FromSeconds(30)); process.Exit(5); });
        using var session = fake.Session(Generous, command: TimeSpan.FromSeconds(60));
        var error = Assert.Throws<WaitFailedException>(() => session.Start());
        Assert.Contains("code 5", error.Reason); Assert.Contains("pins1", fake.Snapshot()); Assert.True(error.Elapsed < TimeSpan.FromSeconds(10));
        gate.Set();
    }
    [Fact] public void NoVerificationStartsOnceTheStartupTimeIsSpent()
    {
        var fake = new Host { ProbeDelay = TimeSpan.FromSeconds(3) }; using var session = fake.Session(TimeSpan.FromSeconds(1), command: TimeSpan.FromSeconds(60));
        Assert.Throws<WaitTimeoutException>(() => session.Start());
        Assert.Contains("probe1", fake.Snapshot()); // The time ran out during the readiness probe, before verification.
        Assert.DoesNotContain("pins1", fake.Snapshot());
    }

    private sealed class Host
    {
        public string Owner = "roads.testing";
        public bool RefuseStop, BadPins, BadPid, ExitOnLaunch, NoConnection, Ready = true;
        public List<string> Events = [], Tokens = [];
        public int Connects, Disconnects;
        public ManualResetEventSlim? ConnectGate, PinGate;
        public readonly ManualResetEventSlim ConnectEntered = new(), PinEntered = new();
        public TimeSpan ProbeDelay;
        public List<TimeSpan> PinTimeouts = [];
        public string[] Snapshot() { lock (Events) return Events.ToArray(); }
        public Action<FakeProcess>? OnLaunch;
        private FakeProcess? _current;
        public IServerProcess Launch(string token)
        {
            Tokens.Add(token); int id = Tokens.Count; Events.Add("launch" + id);
            _current = new FakeProcess(this, id);
            if (ExitOnLaunch) _current.Exit(1);
            OnLaunch?.Invoke(_current);
            return _current;
        }
        public IGameTransport Connect()
        {
            Interlocked.Increment(ref Connects); if (NoConnection) throw new IOException("not yet");
            var transport = new Transport(this, _current!.Id, Tokens[^1]);
            ConnectEntered.Set();
            ConnectGate?.Wait(TimeSpan.FromSeconds(30));
            return transport;
        }
        public OwnedServerSession Session(TimeSpan? timeout = null, StartupEvents? events = null, TimeSpan? command = null) => new(Launch, Connect, Path.GetTempPath(), "cli_expect worlduid=1", Owner + "/session", timeout ?? Generous, command ?? TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1)) { Events = events };
        public sealed class FakeProcess(Host host, int id) : IServerProcess
        {
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Id => id; public bool HasExited => _exit.Task.IsCompleted;
            public void Exit(int code) => _exit.TrySetResult(code);
            public Task<int> WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);
            public void Stop(TimeSpan timeout) { host.Events.Add("stop" + id); if (host.RefuseStop) throw new TimeoutException(); Exit(-1); }
            public void Dispose() => host.Events.Add("dispose" + id);
        }
        private sealed class Transport(Host host, int pid, string token) : IGameTransport
        {
            public CommandResult Execute(string command, TimeSpan timeout)
            {
                if (command.StartsWith("cli_expect"))
                {
                    lock (host.Events) { host.Events.Add("pins" + pid); host.PinTimeouts.Add(timeout); }
                    host.PinEntered.Set();
                    host.PinGate?.Wait(TimeSpan.FromSeconds(30));
                    return new() { Ok = !host.BadPins, Output = [host.BadPins ? "ERROR: pins" : "OK: EXPECT"] };
                }
                if (command != "cli_extension " + host.Owner + "/session") throw new InvalidOperationException("Wrong session capability requested.");
                lock (host.Events) host.Events.Add("probe" + pid);
                if (host.ProbeDelay > TimeSpan.Zero) Thread.Sleep(host.ProbeDelay);
                return new() { Ok = true, Output = ["EXTENSION_RESULT " + Reply(token, host.BadPid ? 99 : pid, Path.GetTempPath(), host.Ready, extension: host.Owner).GetRawText()] };
            }
            public void Dispose() { lock (host.Events) host.Events.Add("disconnect" + pid); Interlocked.Increment(ref host.Disconnects); }
        }
    }
}
