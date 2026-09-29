using System.Diagnostics;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

public class SessionTests
{
    private static JsonElement Reply(string token, int pid, string path, bool complete = true, bool dedicated = true, string extension = "roads.testing") =>
        FakeOwnedServer.SessionReply(extension, token, pid, path, complete, dedicated);
    // Correctness tests get a generous deadline; timeout tests pass their short one.
    private static OwnedServerSession Session(FakeOwnedServer server, TimeSpan? timeout = null, StartupEvents? events = null, TimeSpan? command = null) =>
        server.Session(timeout ?? Generous, command ?? TimeSpan.FromMilliseconds(20), events: events);
    [Fact] public void AnotherModCanUseTheSameOwnedLifecycle()
    {
        var fake = new FakeOwnedServer("other.mod.tests"); using var session = Session(fake);
        session.Start(); session.Restart(); Assert.Equal(2, fake.Tokens.Count);
    }
    [Fact] public void WrongAdapterCannotSatisfySessionIdentity() =>
        Assert.Throws<InvalidOperationException>(() => OwnedServerSession.CheckIdentity(Reply("ours", 7, Path.GetTempPath()), "ours", 7, Path.GetTempPath(), "other.mod.tests"));
    [Theory] [InlineData("no-slash")] [InlineData("owner/session argument")] [InlineData("owner/session/extra")]
    public void SessionCapabilityCannotContainCommandArguments(string capability)
    {
        var fake = new FakeOwnedServer("roads.testing");
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
        var fake = new FakeOwnedServer("roads.testing"); using var session = Session(fake);
        session.Start(); session.Restart();
        Assert.Equal(new[] { "launch1", "probe1", "pins1", "disconnect1", "stop1", "dispose1", "launch2", "probe2", "pins2" }, fake.Events);
        Assert.NotEqual(fake.Tokens[0], fake.Tokens[1]);
    }
    [Fact] public void FailedStopNeverLaunchesReplacement()
    {
        var fake = new FakeOwnedServer("roads.testing") { RefuseStop = true }; var session = Session(fake); session.Start();
        Assert.Throws<TimeoutException>(() => session.Restart()); Assert.Single(fake.Tokens);
        fake.RefuseStop = false; session.Dispose();
    }
    [Fact] public void IncorrectEnvironmentPinsDoNotRetryOrReturnActor()
    {
        var fake = new FakeOwnedServer("roads.testing") { RefusePins = true }; using var session = Session(fake);
        Assert.Throws<InvalidOperationException>(() => session.Start());
        Assert.Equal(1, fake.Events.Count(x => x.StartsWith("pins")));
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("mutation"));
    }
    [Fact] public void WrongPidFailsImmediatelyWithoutPins()
    {
        var fake = new FakeOwnedServer("roads.testing") { ReportWrongPid = true }; using var session = Session(fake);
        Assert.Throws<InvalidOperationException>(() => session.Start()); Assert.DoesNotContain(fake.Events, x => x.StartsWith("pins"));
    }
    [Fact] public void ExitedProcessCannotBecomeReady()
    {
        var fake = new FakeOwnedServer("roads.testing") { ExitOnLaunch = true }; using var session = Session(fake);
        var error = Assert.Throws<WaitFailedException>(() => session.Start()); Assert.Contains("code 1", error.Reason);
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("probe"));
    }
    [Fact] public void CancelledSessionDoesNotLaunch()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var fake = new FakeOwnedServer("roads.testing");
        using var session = new OwnedServerSession(fake.Launch, fake.Connect, Path.GetTempPath(), "cli_expect worlduid=1", "roads.testing/session", Generous, TimeSpan.FromSeconds(1), cancellation: cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => session.Start()); Assert.Empty(fake.Tokens);
    }
    [Fact] public void ConnectionLossHasBoundedStartupAndOwnedCleanup()
    {
        var fake = new FakeOwnedServer("roads.testing") { RefuseConnections = true }; var session = Session(fake, TimeSpan.FromMilliseconds(20));
        Assert.Throws<WaitTimeoutException>(() => session.Start()); session.Dispose();
        Assert.Contains("stop1", fake.Events);
    }
    [Fact] public void NoWorldReadinessCannotPassOnAListeningSocket()
    {
        var fake = new FakeOwnedServer("roads.testing") { Ready = false }; using var session = Session(fake, TimeSpan.FromMilliseconds(20));
        Assert.Throws<WaitTimeoutException>(() => session.Start()); Assert.DoesNotContain(fake.Events, x => x.StartsWith("pins"));
    }
    [Fact] public void RecordingIncludesFailedRepliesAndClosesTransport()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var fake = new FakeOwnedServer("roads.testing") { RefusePins = true }; fake.Launch("t");
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
        var fake = new FakeOwnedServer("roads.testing"); using var session = Session(fake, TimeSpan.FromMilliseconds(300), new StartupEvents { CliLog = log.Path });
        var error = Assert.Throws<WaitTimeoutException>(() => session.Start());
        Assert.Contains("listening", error.Target); Assert.Equal(0, fake.Connects);
    }
    [Fact] public void TheListeningLineLeadsToOneProbeWithoutRetries()
    {
        using var log = new TempLog();
        var fake = new FakeOwnedServer("roads.testing") { OnLaunch = _ => log.Append("BepInEx loading\n" + Listening) };
        using var session = Session(fake, Generous, new StartupEvents { CliLog = log.Path });
        session.Start();
        Assert.Equal(new[] { "launch1", "probe1", "pins1" }, fake.Events); Assert.Equal(1, fake.Connects);
    }
    [Fact] public void AFailureLineEndsStartupBeforeAnyConnection()
    {
        using var log = new TempLog();
        var fake = new FakeOwnedServer("roads.testing") { OnLaunch = _ => log.Append("[Error  : BepInEx] Could not load [roads.testing]\n") };
        using var session = Session(fake, Generous, new StartupEvents { CliLog = log.Path, Failures = [LogWait.Literal("[Error  : BepInEx]")] });
        var error = Assert.Throws<WaitFailedException>(() => session.Start());
        Assert.Equal("[Error  : BepInEx] Could not load [roads.testing]", error.LastSeen); Assert.Equal(0, fake.Connects);
    }
    [Fact] public void AnEarlyExitEndsStartupAtOnceWithItsCodeAndLastLogLine()
    {
        using var log = new TempLog();
        var fake = new FakeOwnedServer("roads.testing") { OnLaunch = process => { log.Append("Fatal: port in use\n"); process.Exit(3); } };
        using var session = Session(fake, Generous, new StartupEvents { CliLog = log.Path });
        var error = Assert.Throws<WaitFailedException>(() => session.Start());
        Assert.Contains("code 3", error.Reason); Assert.Equal("Fatal: port in use", error.LastSeen);
        Assert.True(error.Elapsed < Generous); Assert.Equal(0, fake.Connects);
    }
    [Fact] public async Task StartupWaitsForTheWorldStateBeforeProbing()
    {
        using var log = new TempLog(); using var server = new FakeCliServer(StateWait.Loading);
        var fake = new FakeOwnedServer("roads.testing") { OnLaunch = _ => log.Append(Listening) };
        using var session = Session(fake, Generous, new StartupEvents { CliLog = log.Path, States = () => new StateWait(server.Connect()) });
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
        FakeServerProcess? launched = null;
        var fake = new FakeOwnedServer("roads.testing") { OnLaunch = process => { launched = process; log.Append(Listening); } };
        using var session = Session(fake, Generous, new StartupEvents { CliLog = log.Path, States = () => StateWait.Connect("127.0.0.1", server.Port) });
        var start = Task.Run(session.Start);
        await server.Subscribed.WaitAsync(Generous);
        log.Append("Out of memory\n"); launched!.Exit(5);
        var error = await Assert.ThrowsAsync<WaitFailedException>(() => start);
        Assert.Contains("code 5", error.Reason); Assert.Equal("Out of memory", error.LastSeen);
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("probe"));
    }
    // A restart composes the lifecycle with the log wait: BepInEx rewrites LogOutput.log from the start at every boot, and
    // the dedicated-server events (the log half; the state push needs a live ValheimCLI) must follow each boot's own log.
    [Fact] public void ARestartWaitsForTheNewBootsLogWhichBepInExRewrites()
    {
        using var runtime = new TempRuntime();
        var fake = new FakeOwnedServer("roads.testing") { OnLaunch = process => runtime.Replace($"[Message:   BepInEx] BepInEx 5.4.23 - boot {process.Id}\n{Listening}") };
        using var session = Session(fake, Generous, DedicatedLogEvents(runtime));
        session.Start(); session.Restart();
        Assert.Equal(new[] { "launch1", "probe1", "pins1", "stop1", "dispose1", "launch2", "probe2", "pins2" }, fake.Events.Where(x => !x.StartsWith("disconnect")));
        Assert.Equal(2, fake.Connects); Assert.NotEqual(fake.Tokens[0], fake.Tokens[1]);
    }
    [Fact] public void ARestartedBootIsNotReadyOnThePreviousBootsLine()
    {
        using var runtime = new TempRuntime();
        // Boot two rewrites the log but has not announced its listener yet; boot one's line must not count for it.
        var fake = new FakeOwnedServer("roads.testing") { OnLaunch = process => runtime.Replace(process.Id == 1 ? "boot 1\n" + Listening : "boot 2, still loading\n") };
        using var session = Session(fake, TimeSpan.FromSeconds(2), DedicatedLogEvents(runtime));
        session.Start();
        Assert.Throws<WaitTimeoutException>(() => session.Restart());
        Assert.Equal(1, fake.Connects); Assert.Equal(1, fake.Events.Count(x => x.StartsWith("pins")));
    }
    private static StartupEvents DedicatedLogEvents(TempRuntime runtime)
    {
        var events = new ServerRunPlan { Port = 5555 }.DedicatedStartupEvents(runtime.DirectoryPath);
        return new StartupEvents { CliLog = events.CliLog, Listening = events.Listening, Failures = events.Failures };
    }
    // The startup deadline bounds blocking calls too: the fakes below block until released, ignoring their timeouts.
    [Fact] public void AStalledConnectionCannotOutliveTheStartupDeadline()
    {
        using var gate = new ManualResetEventSlim();
        var fake = new FakeOwnedServer("roads.testing") { ConnectGate = gate }; using var session = Session(fake, TimeSpan.FromSeconds(1));
        var clock = Stopwatch.StartNew();
        Assert.Throws<WaitTimeoutException>(() => session.Start());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(1, fake.Connects); // The deadline expired in the stalled connection, the stage under test.
        // The connection that completes afterwards is closed, not leaked.
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => fake.Disconnects == 1, TimeSpan.FromSeconds(10)));
    }
    [Fact] public void AnExitWhileConnectingEndsStartupAtOnce()
    {
        using var gate = new ManualResetEventSlim();
        var fake = new FakeOwnedServer("roads.testing") { ConnectGate = gate };
        // The exit comes once the connection is under way, the stage under test.
        fake.OnLaunch = process => _ = Task.Run(() => { fake.ConnectEntered.Wait(TimeSpan.FromSeconds(30)); process.Exit(4); });
        using var session = Session(fake, Generous);
        var clock = Stopwatch.StartNew();
        var error = Assert.Throws<WaitFailedException>(() => session.Start());
        Assert.Contains("code 4", error.Reason); Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        gate.Set();
    }
    [Fact] public void VerificationGetsOnlyTheTimeLeftAndTheActorKeepsItsCommandTimeout()
    {
        var fake = new FakeOwnedServer("roads.testing"); using var session = Session(fake, TimeSpan.FromSeconds(30), command: TimeSpan.FromSeconds(60));
        var actor = session.Start();
        Assert.Single(fake.PinTimeouts); Assert.True(fake.PinTimeouts[0] <= TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(60), actor.CommandTimeout);
    }
    [Fact] public void AStalledVerificationCannotOutliveTheStartupDeadline()
    {
        using var gate = new ManualResetEventSlim();
        var fake = new FakeOwnedServer("roads.testing") { PinGate = gate }; using var session = Session(fake, TimeSpan.FromSeconds(2), command: TimeSpan.FromSeconds(60));
        var clock = Stopwatch.StartNew();
        Assert.Throws<WaitTimeoutException>(() => session.Start());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains("pins1", fake.Events); // The deadline expired in verification, the stage under test.
        // The abandoned verification's transport was closed at once; the actor is disposed when the command returns.
        Assert.Equal(1, fake.Disconnects);
        gate.Set();
    }
    [Fact] public void AnExitDuringVerificationEndsStartupAtOnce()
    {
        using var gate = new ManualResetEventSlim();
        var fake = new FakeOwnedServer("roads.testing") { PinGate = gate };
        // The exit comes once verification is under way, the stage under test.
        fake.OnLaunch = process => _ = Task.Run(() => { fake.PinEntered.Wait(TimeSpan.FromSeconds(30)); process.Exit(5); });
        using var session = Session(fake, Generous, command: TimeSpan.FromSeconds(60));
        var error = Assert.Throws<WaitFailedException>(() => session.Start());
        Assert.Contains("code 5", error.Reason); Assert.Contains("pins1", fake.Events); Assert.True(error.Elapsed < TimeSpan.FromSeconds(10));
        gate.Set();
    }
    [Fact] public void NoVerificationStartsOnceTheStartupTimeIsSpent()
    {
        var fake = new FakeOwnedServer("roads.testing") { ProbeDelay = TimeSpan.FromSeconds(3) }; using var session = Session(fake, TimeSpan.FromSeconds(1), command: TimeSpan.FromSeconds(60));
        Assert.Throws<WaitTimeoutException>(() => session.Start());
        Assert.Contains("probe1", fake.Events); // The time ran out during the readiness probe, before verification.
        Assert.DoesNotContain("pins1", fake.Events);
    }
}
