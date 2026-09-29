using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed class ClientSessionTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("client-session-").FullName;
    public void Dispose() => Directory.Delete(_output, recursive: true);

    private static ClientRunPlan Plan(string mode = "owned") => new()
    {
        Mode = mode, Install = mode == "owned" ? Path.GetFullPath("client-install") : "", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
    };

    private sealed class Process(int? exitCode = null) : IServerProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops, Disposals;
        public int Id => 99;
        public bool HasExited => _exit.Task.IsCompleted;
        public Task<int> WaitForExitAsync(CancellationToken cancellation)
        {
            if (exitCode is int code) _exit.TrySetResult(code);
            return _exit.Task.WaitAsync(cancellation);
        }
        public void Stop(TimeSpan timeout) { Stops++; _exit.TrySetResult(-1); }
        public void Dispose() => Disposals++;
    }

    [Fact] public void AnOwnedClientAtItsMenuIsPinnedAndDisposingStopsOnlyItsProcess()
    {
        var process = new Process(); var transport = new ScriptedTransport();
        var session = ClientSession.Launch(Plan(), _output, () => process, () => transport, (_, _) => Task.CompletedTask);
        Assert.True(session.Owned); Assert.Equal(99, session.ProcessId);
        Assert.Contains(transport.Commands, c => c.StartsWith("cli_expect", StringComparison.Ordinal) && c.Contains("my.mod=absent", StringComparison.Ordinal));
        Assert.Equal(0, process.Stops);
        session.Dispose(); session.Dispose();
        Assert.Equal(1, process.Stops); Assert.Equal(1, process.Disposals); Assert.True(transport.Disposed);
        Assert.Contains("\"pid\":99", File.ReadAllText(Path.Combine(_output, "client-process.json")));
    }

    [Fact] public void AnExitDuringStartupFailsWithItsCodeAndNeverConnects()
    {
        var process = new Process(exitCode: 5); bool connected = false;
        var error = Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => process, () => { connected = true; return new ScriptedTransport(); }, (_, _) => Task.CompletedTask));
        Assert.Contains("exited with code 5", error.Message);
        Assert.False(connected); Assert.Equal(1, process.Stops);
    }

    [Fact] public void AClientThatNeverReachesItsMenuTimesOutAndIsStopped()
    {
        var plan = Plan(); plan.StartSeconds = 1;
        var process = new Process();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<WaitTimeoutException>(() => ClientSession.Launch(plan, _output, () => process, () => new ScriptedTransport(), (_, token) => Task.Delay(Timeout.Infinite, token)));
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        Assert.Equal(1, process.Stops);
    }

    [Fact] public void AReadinessFailureEndsStartupAndStopsTheProcess()
    {
        var process = new Process();
        Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => process, () => new ScriptedTransport(),
            (_, _) => Task.FromException(new WaitFailedException("CLI listening", "a plugin failed to load", TimeSpan.Zero, "[Error  : BepInEx] Could not load [x]"))));
        Assert.Equal(1, process.Stops);
    }

    [Fact] public void PinsThatDoNotHoldAtTheMenuStopTheOwnedProcess()
    {
        var process = new Process(); var transport = new ScriptedTransport { PinsHold = false };
        Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(Plan(), _output, () => process, () => transport, (_, _) => Task.CompletedTask));
        Assert.Equal(1, process.Stops); Assert.True(transport.Disposed);
    }

    [Fact] public void AMissingPasswordVariableRefusesBeforeLaunching()
    {
        var plan = Plan(); plan.PasswordVariable = "VT_TEST_UNSET_" + Guid.NewGuid().ToString("N");
        bool started = false;
        Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _output, () => { started = true; return new Process(); }, () => new ScriptedTransport(), (_, _) => Task.CompletedTask));
        Assert.False(started);
    }

    [Fact] public void AnAttachedClientIsPinnedAndNeverStopped()
    {
        var transport = new ScriptedTransport();
        using (var session = ClientSession.Attach(Plan("attach"), _output, transport)) { Assert.False(session.Owned); Assert.Null(session.ProcessId); }
        Assert.True(transport.Disposed);
        Assert.Throws<ArgumentException>(() => ClientSession.Attach(Plan(), _output, new ScriptedTransport()));
        var refused = new ScriptedTransport { PinsHold = false };
        Assert.Throws<InvalidOperationException>(() => ClientSession.Attach(Plan("attach"), _output, refused));
        Assert.True(refused.Disposed);
        // Each session kept its own record.
        Assert.True(File.Exists(Path.Combine(_output, "client-commands.jsonl")));
        Assert.True(File.Exists(Path.Combine(_output, "client-commands-2.jsonl")));
    }

    [Theory]
    [InlineData("mode", "Client mode")]
    [InlineData("relative-install", "full path")]
    [InlineData("attach-with-install", "leave out install")]
    [InlineData("owned-remote-host", "127.0.0.1")]
    [InlineData("world-pin", "Leave the world out")]
    [InlineData("no-cli-pin", "ValheimCLI MD5")]
    [InlineData("mod-present", "my.mod=absent")]
    [InlineData("loose-pin", "exact MD5 or absent")]
    [InlineData("spaced-character", "single tokens")]
    [InlineData("join-seconds", "timeouts")]
    public void PlanRulesRefuseBeforeAnythingStarts(string defect, string message)
    {
        var plan = Plan();
        switch (defect)
        {
            case "mode": plan.Mode = "both"; break;
            case "relative-install": plan.Install = "client"; break;
            case "attach-with-install": plan.Mode = "attach"; break;
            case "owned-remote-host": plan.Host = "game-host"; break;
            case "world-pin": plan.Pins["worlduid"] = "1"; break;
            case "no-cli-pin": plan.Pins.Remove("valheimCLI.valheimCLI"); break;
            case "mod-present": plan.Pins["my.mod"] = new string('b', 32); break;
            case "loose-pin": plan.Pins["other.mod"] = "any"; break;
            case "spaced-character": plan.Character = "Test Er"; break;
            case "join-seconds": plan.JoinSeconds = 5; break;
        }
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => plan.Validate("my.mod")).Message);
    }

    [Fact] public void ExpectationsAddTheServersWorldOnlyOnceJoined()
    {
        var plan = Plan(); plan.Validate("my.mod");
        Assert.DoesNotContain("worlduid", plan.MenuExpectations);
        Assert.Contains("worlduid=77", plan.WorldExpectations("77"));
    }

    [Fact] public void APinnedFileMustStillMatch()
    {
        string file = Path.Combine(_output, "plan.json"); File.WriteAllText(file, "{}");
        var pinned = new PinnedFile { Source = file, Sha256 = WorldFixture.Hash(file) };
        pinned.Validate("plan"); Assert.Equal(file, pinned.Verified());
        File.WriteAllText(file, "{ }");
        Assert.Throws<InvalidOperationException>(pinned.Verified);
        Assert.Throws<ArgumentException>(() => new PinnedFile { Source = "plan.json", Sha256 = pinned.Sha256 }.Validate("plan"));
    }
}
