using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed class ClientSessionTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("client-session-").FullName;
    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact] public void FailedStartupCapturesEvidenceBeforeStoppingItsOwnedProcess()
    {
        var process = new FakeOwnedProcess(99);
        bool capturedWhileRunning = false;
        Assert.Throws<WaitTimeoutException>(() => ClientSession.Launch(Plan(), _output, () => process,
            () => new ScriptedTransport(), (_, _) => throw new WaitTimeoutException("menu", TimeSpan.Zero, null),
            default, null, null, failureEvidence: (_, _, _) => capturedWhileRunning = process.Stops == 0));
        Assert.True(capturedWhileRunning);
        Assert.Equal(1, process.Stops);
    }

    [Fact] public void FailedPinVerificationCannotSendADiagnosticGameCommand()
    {
        var process = new FakeOwnedProcess(99);
        var transport = new ScriptedTransport { PinsHold = false };
        bool capturedWhileRunning = false;
        Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(Plan(), _output, () => process,
            () => transport, (_, _) => Task.CompletedTask, default, null, null,
            failureEvidence: (_, actor, _) =>
            {
                capturedWhileRunning = process.Stops == 0;
                Assert.Null(actor); // Capture process facts and log tails only; no screenshot or other game command.
            }));
        Assert.True(capturedWhileRunning);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("cli_screenshot ", StringComparison.Ordinal));
        Assert.Equal(1, process.Stops);
    }

    [Fact] public void AnExplicitlyUnpinnedClientCannotSendADiagnosticGameCommand()
    {
        var plan = Owned(Path.GetFullPath("client-install"));
        plan.Capabilities = ["test/absent"]; // Failure after the unpinned environment check.
        var transport = new ScriptedTransport();
        GameActor? diagnosticActor = null;
        Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _output,
            () => new FakeOwnedProcess(99), () => transport, (_, _) => Task.CompletedTask,
            default, null, null, failureEvidence: (_, actor, _) => diagnosticActor = actor));
        Assert.Null(diagnosticActor);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("cli_screenshot ", StringComparison.Ordinal));
    }

    [Fact] public void AFailedWorldRoundCapturesEvidenceOnlyOnceBeforeStopping()
    {
        var process = new FakeOwnedProcess(99);
        int captures = 0;
        using (var session = ClientSession.Launch(Plan(), _output, () => process, () => new ScriptedTransport(),
            (_, _) => Task.CompletedTask, default, null, null,
            failureEvidence: (_, _, _) => { Assert.Equal(0, process.Stops); captures++; }))
        {
            session.CaptureFailure();
            session.CaptureFailure();
        }
        Assert.Equal(1, captures);
        Assert.Equal(1, process.Stops);
    }

    [Fact] public void AWorldRoundThatWasUnpinnedKeepsProcessEvidenceWithoutAGameCommand()
    {
        var process = new FakeOwnedProcess(99);
        var transport = new ScriptedTransport();
        GameActor? diagnosticActor = null;
        using (var session = ClientSession.Launch(Plan(), _output, () => process, () => transport,
            (_, _) => Task.CompletedTask, default, null, null,
            failureEvidence: (_, actor, _) => diagnosticActor = actor))
        {
            session.Actor.VerifyEnvironment(EnvironmentPinning.None);
            session.CaptureFailure();
        }
        Assert.Null(diagnosticActor);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("cli_screenshot ", StringComparison.Ordinal));
        Assert.Equal(1, process.Stops);
    }

    [Fact] public void AFailureSnapshotRequiresTheExactOwnedProcessAndKeepsLogTails()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var plan = Plan(); plan.Install = _output; plan.Port = 1; // No unrelated CLI service is probed by this local-process fixture.
        string log = Path.Combine(_output, "BepInEx", "LogOutput.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, new string('x', 512 * 1024));
        File.AppendAllLines(log, Enumerable.Range(1, 100).Select(n => "log " + n));
        string evidence = Path.Combine(_output, "failure");
        string start = current.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture);
        ClientFailureEvidence.CaptureLocal(new ExactProcess(current.Id, start), null, plan, evidence);
        string[] tail = File.ReadAllLines(Path.Combine(evidence, "client-failure-bepinex-tail.log"));
        Assert.Equal(80, tail.Length);
        Assert.Equal("log 21", tail[0]);
        Assert.Equal("log 100", tail[^1]);
        Assert.Contains("same owned process", File.ReadAllText(Path.Combine(evidence, "client-failure-diagnostic.json")));

        string mismatch = Path.Combine(_output, "mismatch");
        ClientFailureEvidence.CaptureLocal(new ExactProcess(current.Id, "1"), null, plan, mismatch);
        Assert.Contains("different launch", File.ReadAllText(Path.Combine(mismatch, "client-failure-diagnostic.json")));

        var transport = new ScriptedTransport().OnPrefix("cli_screenshot ", _ => ScriptedTransport.Ok("That command is a cheat"));
        using var actor = transport.Actor();
        string refused = Path.Combine(_output, "cheat-refused");
        ClientFailureEvidence.CaptureLocal(new ExactProcess(current.Id, start), actor, plan, refused);
        Assert.Contains("CLI refused", File.ReadAllText(Path.Combine(refused, "client-failure-diagnostic.json")));
        Assert.Single(transport.Commands.Where(command => command.StartsWith("cli_screenshot ", StringComparison.Ordinal)));

        actor.VerifyEnvironment(EnvironmentPinning.None);
        string unpinned = Path.Combine(_output, "unpinned");
        ClientFailureEvidence.CaptureLocal(new ExactProcess(current.Id, start), actor, plan, unpinned);
        Assert.Single(transport.Commands.Where(command => command.StartsWith("cli_screenshot ", StringComparison.Ordinal)));
        Assert.Contains("not attempted", File.ReadAllText(Path.Combine(unpinned, "client-failure-diagnostic.json")));
    }

    private sealed class ExactProcess(int id, string start) : IOwnedProcess, IClientProcessIdentity
    {
        public int Id => id;
        public string StartFileTimeUtc => start;
        public bool HasExited => false;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => Task.FromResult(0);
        public void Stop(TimeSpan timeout) { }
        public void Dispose() { }
    }

    private sealed class RunningIdentifiedProcess(int id, string start) : IOwnedProcess, IClientProcessIdentity
    {
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => id;
        public string StartFileTimeUtc => start;
        public bool HasExited { get; private set; }
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => _exited.Task;
        public void Stop(TimeSpan timeout) { HasExited = true; _exited.TrySetResult(0); }
        public void Dispose() { }
    }

    [Fact] public void LocalWindowsOwnedClientRecordsItsExactProcessAndStrictPinsForAPassthrough()
    {
        if (!OperatingSystem.IsWindows()) return;
        var process = new RunningIdentifiedProcess(99, "123456789");
        var plan = Plan();
        plan.HostWorld = new HostWorldPlan { WorldUid = "fixture-uid" };
        using (ClientSession.Launch(plan, _output, () => process, () => new ScriptedTransport(),
            (_, _) => Task.CompletedTask))
        {
            Assert.True(File.Exists(Path.Combine(_output, OwnedClientCommandLease.FileName)));
            Assert.Contains("\"Pid\": 99", File.ReadAllText(Path.Combine(_output, OwnedClientCommandLease.FileName)));
            Assert.Contains("my.mod=absent", File.ReadAllText(Path.Combine(_output, OwnedClientCommandLease.MenuPins)));
            Assert.Contains("worlduid=fixture-uid", File.ReadAllText(Path.Combine(_output, OwnedClientCommandLease.WorldPins)));
        }
        Assert.True(process.HasExited);
    }

    [Fact] public void AnOwnedClientReceivesItsDeclaredProcessVariable()
    {
        using var install = ClientLaunchTests.Install.For(ClientPlatform.Windows);
        var plan = Plan(); plan.Install = install.Root;
        plan.Environment["PROCEDURALROADS_GENERATE_ROADS_ON_LOAD"] = "0";
        var launch = ClientSession.StartInfo(plan, ClientPlatform.Windows);
        Assert.Equal("0", launch.Environment["PROCEDURALROADS_GENERATE_ROADS_ON_LOAD"]);
    }

    [Fact] public void ANullClientEnvironmentIsRefusedAsAPlanError()
    {
        var plan = Plan(); plan.Environment = null!;
        Assert.Contains("client.environment", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
    }

    private static ClientRunPlan Plan(string mode = "owned") => new()
    {
        Mode = mode, Install = mode == "owned" ? Path.GetFullPath("client-install") : "", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
        InstallPins = mode == "owned" ? new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) } : null,
    };

    // With an environment profile the client runs on another machine: a Windows install is validated from macOS or Linux too.
    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Valheim", true)]
    [InlineData(@"\\fileserver\games\Valheim", true)]
    [InlineData("/home/tester/.steam/steam/steamapps/common/Valheim", true)]
    [InlineData("Valheim", false)]
    [InlineData(@"steamapps\common\Valheim", false)]
    [InlineData("", false)]
    public void AnOwnedClientsInstallIsAFullPathInAnyHostsStyle(string install, bool accepted)
    {
        var plan = Plan(); plan.Install = install;
        if (accepted) plan.Validate("my.mod"); else Assert.Throws<ArgumentException>(() => plan.Validate("my.mod"));
    }

    [Fact] public void AnOwnedClientAtItsMenuIsPinnedAndDisposingStopsOnlyItsProcess()
    {
        var process = new FakeOwnedProcess(99); var transport = new ScriptedTransport();
        var session = ClientSession.Launch(Plan(), _output, () => process, () => transport, (_, _) => Task.CompletedTask);
        Assert.True(session.Owned); Assert.Equal(99, session.ProcessId);
        Assert.Contains(transport.Commands, c => c.StartsWith("cli_expect", StringComparison.Ordinal) && c.Contains("my.mod=absent", StringComparison.Ordinal));
        Assert.Equal(0, process.Stops);
        session.Dispose(); session.Dispose();
        Assert.Equal(1, process.Stops); Assert.Equal(1, process.Disposals); Assert.True(transport.Disposed);
        Assert.Contains("\"pid\":99", File.ReadAllText(Path.Combine(_output, "client-process.json")));
    }

    // The session reports how its client ended: a client that quits when asked is Clean, one that ignores the request is killed.
    [Theory] [InlineData(false, StopOutcome.Clean)] [InlineData(true, StopOutcome.Killed)]
    public void DisposingRecordsWhetherTheClientQuitOrWasKilled(bool ignoreQuit, StopOutcome outcome)
    {
        var process = new FakeOwnedProcess(99) { IgnoreQuit = ignoreQuit };
        var session = ClientSession.Launch(Plan(), _output, () => process, () => new ScriptedTransport(), (_, _) => Task.CompletedTask);
        session.Dispose();
        Assert.Equal(outcome, session.Stopped!.Outcome); Assert.True(process.HasExited);
    }

    // An unpinned owned plan for a synthetic install, so only the architecture decides what the launch does.
    private static ClientRunPlan Owned(string install, string? architecture = null) => new()
    {
        Mode = "owned", Install = install, Port = 5556, Join = "127.0.0.1:2456", Character = "Tester", Pinning = "none", Architecture = architecture ?? "",
    };

    [Theory] [InlineData("x64", "-x86_64", false)] [InlineData("arm64", "-arm64", true)]
    public void ThePlansArchitectureChoosesTheMacSliceAndItsDoorstop(string? architecture, string slice, bool native)
    {
        using var install = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.NativeDetour);
        var plan = Owned(install.Root, architecture);
        plan.Validate();
        var arguments = ClientSession.StartInfo(plan, ClientPlatform.MacOS).ArgumentList.ToList();
        Assert.Equal(slice, arguments[0]);
        string doorstop = native ? Path.Combine(install.Root, "libdoorstop.dylib") : Path.Combine(install.Root, "doorstop_libs", "libdoorstop_x64.dylib");
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + doorstop }, ClientLaunchTests.ExportPair(arguments, "DYLD_INSERT_LIBRARIES"));
    }

    [Fact] public void APreparedMacProfileRunsTheSourceGameWithItsOwnDoorstop()
    {
        using var game = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.NativeDetour);
        using var profile = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.NativeDetour);
        var plan = Owned(game.Root, "arm64");
        plan.Prepared = true;
        plan.PreparedLoaderRoot = profile.Root;
        var launch = GameLaunch.LocalClient(game.Root, [], null, ClientArchitecture.Arm64, true, ClientPlatform.MacOS, profile.Root);
        Assert.Contains("DYLD_INSERT_LIBRARIES", launch.Unset);
        Assert.Contains("DYLD_LIBRARY_PATH", launch.Unset);
        var start = ClientSession.StartInfo(plan, ClientPlatform.MacOS);
        Assert.Equal("/usr/bin/arch", start.FileName);
        Assert.Contains(game.Executable, start.ArgumentList);
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + Path.Combine(profile.Root, "libdoorstop.dylib") },
            ClientLaunchTests.ExportPair(start.ArgumentList.ToList(), "DYLD_INSERT_LIBRARIES"));
    }

    [Fact] public void AnOmittedArchitectureUsesTheCurrentHostsDefaultMacSlice()
    {
        using var install = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.NativeDetour);
        var plan = Owned(install.Root);
        bool native = OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        Assert.Equal(native ? ClientArchitecture.Arm64 : ClientArchitecture.X64, plan.LaunchArchitecture);
        plan.Validate();
        var arguments = ClientSession.StartInfo(plan, ClientPlatform.MacOS).ArgumentList.ToList();
        Assert.Equal(native ? "-arm64" : "-x86_64", arguments[0]);
        string doorstop = native ? Path.Combine(install.Root, "libdoorstop.dylib") : Path.Combine(install.Root, "doorstop_libs", "libdoorstop_x64.dylib");
        Assert.Equal(new[] { "-e", "DYLD_INSERT_LIBRARIES=" + doorstop }, ClientLaunchTests.ExportPair(arguments, "DYLD_INSERT_LIBRARIES"));
    }

    // Never Rosetta instead: an arm64 plan for the pack's x64-only loader, or for the pack's legacy MonoMod core, is refused.
    [Fact] public void AnArm64PlanForAnInstallThatCannotRunNativelyIsRefusedBeforeLaunch()
    {
        using var pack = ClientLaunchTests.Install.Mac();
        var error = Assert.Throws<InvalidOperationException>(() => ClientSession.StartInfo(Owned(pack.Root, "arm64"), ClientPlatform.MacOS));
        Assert.Contains("No Doorstop library in the install has an arm64 slice", error.Message);
        using var legacy = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.LegacyDetour);
        error = Assert.Throws<InvalidOperationException>(() => ClientSession.StartInfo(Owned(legacy.Root, "arm64"), ClientPlatform.MacOS));
        Assert.Contains("MonoMod before 25", error.Message);
        // The public launch refuses the same plan before it reserves the port, checks Steam or starts anything: on a Mac for
        // the loader, elsewhere because a macOS client runs only on a Mac.
        Assert.ThrowsAny<Exception>(() => ClientSession.Launch(Owned(pack.Root, "arm64"), _output));
        Assert.False(File.Exists(Path.Combine(_output, "client-process.json")));
    }

    [Theory] [InlineData(ClientPlatform.Windows)] [InlineData(ClientPlatform.Linux)]
    public void AnArm64PlanForAWindowsOrLinuxClientIsRefusedAndX64IsUnchanged(ClientPlatform platform)
    {
        using var install = ClientLaunchTests.Install.For(platform);
        var error = Assert.Throws<ArgumentException>(() => Owned(install.Root, "arm64").Validate());
        Assert.Contains($"this {platform} client is x64 only", error.Message);
        Assert.Throws<ArgumentException>(() => ClientSession.StartInfo(Owned(install.Root, "arm64"), platform));
        foreach (string? x64 in new[] { null, "x64" })
        {
            var plan = Owned(install.Root, x64); plan.Validate();
            Assert.Equal(install.Executable, ClientSession.StartInfo(plan, platform).FileName);
        }
    }

    // In-place validation checks the selected loader's slice before launch. An unprepared owned profile can replace
    // the source loader with a reviewed package, so its source loader must not decide whether that profile can run.
    [Fact] public void PlanValidationRefusesAMacInstallThePlansArchitectureCannotLaunch()
    {
        using var pack = ClientLaunchTests.Install.Mac();
        var armPlan = Owned(pack.Root, "arm64");
        armPlan.InPlace = true;
        var error = Assert.Throws<ArgumentException>(() => armPlan.Validate());
        Assert.Contains("The client install cannot launch as arm64: No Doorstop library in the install has an arm64 slice", error.Message);
        Owned(pack.Root, "x64").Validate(); // The pack's own route remains available when explicitly requested.
        if (OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
        {
            var defaultPlan = Owned(pack.Root); defaultPlan.InPlace = true;
            Assert.Contains("cannot launch as arm64", Assert.Throws<ArgumentException>(() => defaultPlan.Validate()).Message);
        }
        using var legacy = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.LegacyDetour);
        var legacyPlan = Owned(legacy.Root, "arm64"); legacyPlan.InPlace = true;
        Assert.Contains("MonoMod before 25", Assert.Throws<ArgumentException>(() => legacyPlan.Validate()).Message);
        using var nativeOnly = ClientLaunchTests.Install.Mac(packDoorstop: false, arm64Doorstop: true, core: ClientLaunchTests.NativeDetour);
        Owned(nativeOnly.Root, "arm64").Validate();
        var x64Plan = Owned(nativeOnly.Root, "x64"); x64Plan.InPlace = true;
        Assert.Contains("cannot launch as x64: No Doorstop library in the install has an x86_64 slice", Assert.Throws<ArgumentException>(() => x64Plan.Validate()).Message);
        if (OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
            Owned(nativeOnly.Root).Validate();
        using var noGame = ClientLaunchTests.Install.Mac(core: ClientLaunchTests.NativeDetour);
        File.Delete(noGame.Executable);
        var noGamePlan = Owned(noGame.Root); noGamePlan.InPlace = true;
        Assert.Contains("has no executable", Assert.Throws<ArgumentException>(() => noGamePlan.Validate()).Message);
    }

    // The process started, so its kept logs travel with the failure for the scan; a start that never happened keeps none.
    [Fact] public void AFailedStartupCarriesTheLogsItKept()
    {
        RunLog[] logs = [new("client BepInEx log", Path.Combine(_output, "client-boot.game-0.log"), Required: true), new("client Player.log", Path.Combine(_output, "client-boot.game-1.log"))];
        var failed = Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => FakeOwnedProcess.Exited(3, 99), () => new ScriptedTransport(),
            (_, _) => Task.CompletedTask, default, null, logs));
        Assert.Equal(logs, ClientSession.KeptLogs(failed));
        var refused = Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(Plan(), _output, () => new FakeOwnedProcess(99), () => new ScriptedTransport { PinsHold = false },
            (_, _) => Task.CompletedTask, default, null, logs));
        Assert.Equal(logs, ClientSession.KeptLogs(refused));
        var neverStarted = Assert.Throws<IOException>(() => ClientSession.Launch(Plan(), _output, () => throw new IOException("no such file"), () => new ScriptedTransport(),
            (_, _) => Task.CompletedTask, default, null, logs));
        Assert.Empty(ClientSession.KeptLogs(neverStarted));
        Assert.Empty(ClientSession.KeptLogs(new InvalidOperationException("unrelated")));
    }

    [Fact] public void AnArm64PlanForAWindowsInstallPathIsRefusedFromAnyHost()
    {
        var error = Assert.Throws<ArgumentException>(() => Owned(@"C:\Games\Valheim-vt-" + Guid.NewGuid().ToString("N"), "arm64").Validate());
        Assert.Contains("this Windows client is x64 only", error.Message);
        // A POSIX path that is not on this machine is decided by the launch where it runs.
        Owned("/Users/tester/valheim-vt-" + Guid.NewGuid().ToString("N"), "arm64").Validate();
    }

    [Fact] public void TheArchitectureFieldReadsFromAPlanAndRefusesOtherValues()
    {
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        var plan = System.Text.Json.JsonSerializer.Deserialize<ClientRunPlan>("""{ "mode": "owned", "architecture": "arm64" }""", options)!;
        Assert.Equal(ClientArchitecture.Arm64, plan.LaunchArchitecture);
        var defaultArchitecture = OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? ClientArchitecture.Arm64 : ClientArchitecture.X64;
        Assert.Equal(defaultArchitecture, System.Text.Json.JsonSerializer.Deserialize<ClientRunPlan>("""{ "mode": "owned" }""", options)!.LaunchArchitecture);
        foreach (string? value in new[] { "arm", "ARM64", "x86_64", "universal", null })
        {
            var refused = Owned("/Users/tester/valheim"); refused.Architecture = value!;
            Assert.Contains("neither x64 nor arm64", Assert.Throws<ArgumentException>(() => refused.Validate()).Message);
        }
        var attached = Plan("attach"); attached.Architecture = "x64";
        Assert.Contains("leave out install, installPins, launch arguments, architecture and inPlace", Assert.Throws<ArgumentException>(() => attached.Validate("my.mod")).Message);
    }

    [Fact] public void TheLaunchedArchitectureIsRecordedAndAnAttachedClientHasNone()
    {
        var plan = Plan(); plan.Architecture = "arm64";
        using (var session = ClientSession.Launch(plan, _output, () => new FakeOwnedProcess(99), () => new ScriptedTransport(), (_, _) => Task.CompletedTask))
            Assert.Equal(ClientArchitecture.Arm64, session.Architecture);
        Assert.Contains("\"architecture\":\"arm64\"", File.ReadAllText(Path.Combine(_output, "client-process.json")));
        var defaultArchitecture = OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? ClientArchitecture.Arm64 : ClientArchitecture.X64;
        using (var session = ClientSession.Launch(Plan(), _output, () => new FakeOwnedProcess(99), () => new ScriptedTransport(), (_, _) => Task.CompletedTask))
            Assert.Equal(defaultArchitecture, session.Architecture);
        Assert.Contains("\"architecture\":\"" + (defaultArchitecture == ClientArchitecture.Arm64 ? "arm64" : "x64") + "\"", File.ReadAllText(Path.Combine(_output, "client-process.json")));
        using (var session = ClientSession.Attach(Plan("attach"), _output, new ScriptedTransport())) Assert.Null(session.Architecture);
    }

    [Fact] public void AnExitDuringStartupFailsWithItsCodeAndNeverConnects()
    {
        var process = FakeOwnedProcess.Exited(5, 99); bool connected = false;
        var error = Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => process, () => { connected = true; return new ScriptedTransport(); }, (_, _) => Task.CompletedTask));
        Assert.Contains("exited with code 5", error.Message);
        Assert.False(connected); Assert.Equal(1, process.Stops);
    }

    [Fact] public void AClientThatNeverReachesItsMenuTimesOutAndIsStopped()
    {
        var plan = Plan(); plan.StartSeconds = 1;
        var process = new FakeOwnedProcess(99);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<WaitTimeoutException>(() => ClientSession.Launch(plan, _output, () => process, () => new ScriptedTransport(), (_, token) => Task.Delay(Timeout.Infinite, token)));
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        Assert.Equal(1, process.Stops);
    }

    [Fact] public void AReadinessFailureEndsStartupAndStopsTheProcess()
    {
        var process = new FakeOwnedProcess(99);
        Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => process, () => new ScriptedTransport(),
            (_, _) => Task.FromException(new WaitFailedException("CLI listening", "a plugin failed to load", TimeSpan.Zero, "[Error  : BepInEx] Could not load [x]"))));
        Assert.Equal(1, process.Stops);
    }

    [Fact] public void PinsThatDoNotHoldAtTheMenuStopTheOwnedProcess()
    {
        var process = new FakeOwnedProcess(99); var transport = new ScriptedTransport { PinsHold = false };
        Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(Plan(), _output, () => process, () => transport, (_, _) => Task.CompletedTask));
        Assert.Equal(1, process.Stops); Assert.True(transport.Disposed);
    }

    [Fact] public void AMissingPasswordVariableRefusesBeforeLaunching()
    {
        var plan = Plan(); plan.PasswordVariable = "VT_TEST_UNSET_" + Guid.NewGuid().ToString("N");
        bool started = false;
        Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _output, () => { started = true; return new FakeOwnedProcess(99); }, () => new ScriptedTransport(), (_, _) => Task.CompletedTask));
        Assert.False(started);
    }

    [Fact] public void JoinPasswordComesFromTheRunnerEnvironmentWithoutEnteringPlanData()
    {
        string variable = "VT_JOIN_PASSWORD_" + Guid.NewGuid().ToString("N");
        const string secret = "private-test-password";
        try
        {
            Environment.SetEnvironmentVariable(variable, secret);
            var plan = Plan(); plan.PasswordVariable = variable;
            using var install = PreflightInstall.Create();
            plan.Install = install.Root;
            plan.Architecture = "x64";
            plan.RequirePasswordSource();
            var launch = ClientSession.StartInfo(plan, GameLaunch.CurrentClientHost);
            Assert.Equal(secret, launch.Environment[variable]);
            Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(plan));

            plan.Environment[variable] = secret;
            var error = Assert.Throws<InvalidOperationException>(plan.RequirePasswordSource);
            Assert.Contains("serialized plan data", error.Message);
            Assert.DoesNotContain(secret, error.Message);

            plan.Environment.Clear();
            plan.Environment[variable.ToLowerInvariant()] = secret;
            error = Assert.Throws<InvalidOperationException>(plan.RequirePasswordSource);
            Assert.Contains("serialized plan data", error.Message);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact] public void AnExitBeforeBepInExWroteItsLogSaysWhereToLook()
    {
        var error = Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => FakeOwnedProcess.Exited(1, 99), () => new ScriptedTransport(),
            (_, _) => Task.CompletedTask, default, () => " before BepInEx wrote its log", null));
        Assert.Contains("exited with code 1 before BepInEx wrote its log", error.Reason);
        var plain = Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => FakeOwnedProcess.Exited(1, 99), () => new ScriptedTransport(),
            (_, _) => Task.CompletedTask, default, () => null, null));
        Assert.EndsWith("exited with code 1", plain.Reason);
    }

    [Fact] public void AnOwnedClientListsTheLogsItKeeps()
    {
        RunLog[] logs = [new("client BepInEx log", Path.Combine(_output, "client-boot.game-0.log"), Required: true), new("client Player.log", Path.Combine(_output, "client-boot.game-1.log"))];
        using var session = ClientSession.Launch(Plan(), _output, () => new FakeOwnedProcess(99), () => new ScriptedTransport(), (_, _) => Task.CompletedTask, default, null, logs);
        Assert.Equal(logs, session.Logs);
    }

    [Theory]
    [InlineData(ClientPlatform.Windows, "AppData", "LocalLow", "IronGate", "Valheim", "Player.log")]
    [InlineData(ClientPlatform.Linux, ".config", "unity3d", "IronGate", "Valheim", "Player.log")]
    [InlineData(ClientPlatform.MacOS, "Library", "Logs", "IronGate", "Valheim", "Player.log")]
    public void UnityWritesTheClientsPlayerLogUnderTheUsersProfile(ClientPlatform platform, params string[] under) =>
        Assert.Equal(Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }.Concat(under).ToArray()), ClientSession.PlayerLog(platform));

    [Fact] public void ALeftoverPatcherInTheInstallRefusesTheLaunch()
    {
        // The install pinned clean; a removed mod then left its patcher behind. The patchers pin refuses it, naming it.
        string install = Path.Combine(_output, "install");
        FakeInstalls.Client(install);
        var plan = Plan(); plan.Install = install; plan.InstallPins = InstallPins.Of(install); plan.InPlace = true; // Launch runs only a bound copy or the install in place.
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "patchers"));
        File.WriteAllText(Path.Combine(install, "BepInEx", "patchers", "RemovedMod.Preloader.dll"), "patcher");
        var error = Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _output));
        Assert.Contains("holding RemovedMod.Preloader.dll", error.Message);
        Assert.False(File.Exists(Path.Combine(_output, "client-process.json")));
    }

    [Fact] public void AnAttachedClientIsPinnedAndNeverStopped()
    {
        var transport = new ScriptedTransport();
        using (var session = ClientSession.Attach(Plan("attach"), _output, transport)) { Assert.False(session.Owned); Assert.Null(session.ProcessId); Assert.Empty(session.Logs); }
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
            case "no-cli-pin": plan.InPlace = true; plan.Pins.Remove("valheimCLI.valheimCLI"); break; // A disposable copy's pin is its staged set's.
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
        var pinned = new PinnedFile { Source = file, Sha256 = FileHash.Sha256(file) };
        pinned.Validate("plan"); Assert.Equal(file, pinned.Verified());
        File.WriteAllText(file, "{ }");
        Assert.Throws<InvalidOperationException>(pinned.Verified);
        Assert.Throws<ArgumentException>(() => new PinnedFile { Source = "plan.json", Sha256 = pinned.Sha256 }.Validate("plan"));
    }
}
