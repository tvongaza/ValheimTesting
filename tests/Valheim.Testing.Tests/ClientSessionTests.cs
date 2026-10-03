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
        InstallPins = mode == "owned" ? new() { Game = new string('c', 64), BepInExCore = new string('d', 64), Patchers = new string('e', 64) } : null,
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
        var process = new Process(); var transport = new ScriptedTransport();
        var session = ClientSession.Launch(Plan(), _output, () => process, () => transport, (_, _) => Task.CompletedTask);
        Assert.True(session.Owned); Assert.Equal(99, session.ProcessId);
        Assert.Contains(transport.Commands, c => c.StartsWith("cli_expect", StringComparison.Ordinal) && c.Contains("my.mod=absent", StringComparison.Ordinal));
        Assert.Equal(0, process.Stops);
        session.Dispose(); session.Dispose();
        Assert.Equal(1, process.Stops); Assert.Equal(1, process.Disposals); Assert.True(transport.Disposed);
        Assert.Contains("\"pid\":99", File.ReadAllText(Path.Combine(_output, "client-process.json")));
    }

    [Fact] public void ADirectStartPinsTheWorldBeforeReturningInsteadOfRequiringTheMenu()
    {
        var plan = Plan(); plan.DirectStart = true; plan.DirectStartWorldUid = "77";
        var transport = new ScriptedTransport().Extension("valheim.session", "direct-start", _ => new { source = "direct-start", complete = true, supported = true, claimed = true });
        using var session = ClientSession.Launch(plan, _output, () => new Process(), () => transport, (_, _) => Task.CompletedTask);
        Assert.Contains(transport.Commands, command => command.StartsWith("cli_expect ", StringComparison.Ordinal) &&
            command.Contains("worlduid=77", StringComparison.Ordinal));
        CliCapabilities.RequireDirectStartClaimed(session.Actor);
    }

    [Fact] public void AWorldWithoutAConsumedDirectStartRequestIsRefused()
    {
        var transport = new ScriptedTransport().Extension("valheim.session", "direct-start", _ =>
            new { source = "direct-start", complete = true, supported = true, claimed = false });
        using var actor = transport.Actor();
        Assert.Contains("did not confirm consuming", Assert.Throws<InvalidOperationException>(() => CliCapabilities.RequireDirectStartClaimed(actor)).Message);
    }

    // An unpinned owned plan for a synthetic install, so only the architecture decides what the launch does.
    private static ClientRunPlan Owned(string install, string? architecture = null) => new()
    {
        Mode = "owned", Install = install, Port = 5556, Join = "127.0.0.1:2456", Character = "Tester", Pinning = "none", Architecture = architecture ?? "",
    };

    [Theory] [InlineData(null, "-x86_64", false)] [InlineData("x64", "-x86_64", false)] [InlineData("arm64", "-arm64", true)]
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

    // Validation runs the launch's own slice and core check on an install on this machine, so a runner's validate mode, and its
    // run mode before it starts a server, refuse what the launch would.
    [Fact] public void PlanValidationRefusesAMacInstallThePlansArchitectureCannotLaunch()
    {
        using var pack = ClientLaunchTests.Install.Mac();
        var error = Assert.Throws<ArgumentException>(() => Owned(pack.Root, "arm64").Validate());
        Assert.Contains("The client install cannot launch as arm64: No Doorstop library in the install has an arm64 slice", error.Message);
        Owned(pack.Root).Validate(); // The pack's own route, x64, is fine.
        using var legacy = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.LegacyDetour);
        Assert.Contains("MonoMod before 25", Assert.Throws<ArgumentException>(() => Owned(legacy.Root, "arm64").Validate()).Message);
        using var nativeOnly = ClientLaunchTests.Install.Mac(packDoorstop: false, arm64Doorstop: true, core: ClientLaunchTests.NativeDetour);
        Owned(nativeOnly.Root, "arm64").Validate();
        Assert.Contains("cannot launch as x64: No Doorstop library in the install has an x86_64 slice", Assert.Throws<ArgumentException>(() => Owned(nativeOnly.Root).Validate()).Message);
        using var noGame = ClientLaunchTests.Install.Mac(core: ClientLaunchTests.NativeDetour);
        File.Delete(noGame.Executable);
        Assert.Contains("has no executable", Assert.Throws<ArgumentException>(() => Owned(noGame.Root).Validate()).Message);
    }

    // The process started, so its kept logs travel with the failure for the scan; a start that never happened keeps none.
    [Fact] public void AFailedStartupCarriesTheLogsItKept()
    {
        RunLog[] logs = [new("client BepInEx log", Path.Combine(_output, "client-boot.game-0.log"), Required: true), new("client Player.log", Path.Combine(_output, "client-boot.game-1.log"))];
        var failed = Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => new Process(exitCode: 3), () => new ScriptedTransport(),
            (_, _) => Task.CompletedTask, default, null, logs));
        Assert.Equal(logs, ClientSession.KeptLogs(failed));
        var refused = Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(Plan(), _output, () => new Process(), () => new ScriptedTransport { PinsHold = false },
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
        Assert.Equal(ClientArchitecture.X64, System.Text.Json.JsonSerializer.Deserialize<ClientRunPlan>("""{ "mode": "owned" }""", options)!.LaunchArchitecture);
        foreach (string? value in new[] { "arm", "ARM64", "x86_64", "universal", null })
        {
            var refused = Owned("/Users/tester/valheim"); refused.Architecture = value!;
            Assert.Contains("neither x64 nor arm64", Assert.Throws<ArgumentException>(() => refused.Validate()).Message);
        }
        var attached = Plan("attach"); attached.Architecture = "x64";
        Assert.Contains("leave out install, installPins, launch arguments, patchers and architecture", Assert.Throws<ArgumentException>(() => attached.Validate("my.mod")).Message);
    }

    [Fact] public void TheLaunchedArchitectureIsRecordedAndAnAttachedClientHasNone()
    {
        var plan = Plan(); plan.Architecture = "arm64";
        using (var session = ClientSession.Launch(plan, _output, () => new Process(), () => new ScriptedTransport(), (_, _) => Task.CompletedTask))
            Assert.Equal(ClientArchitecture.Arm64, session.Architecture);
        Assert.Contains("\"architecture\":\"arm64\"", File.ReadAllText(Path.Combine(_output, "client-process.json")));
        using (var session = ClientSession.Launch(Plan(), _output, () => new Process(), () => new ScriptedTransport(), (_, _) => Task.CompletedTask))
            Assert.Equal(ClientArchitecture.X64, session.Architecture);
        Assert.Contains("\"architecture\":\"x64\"", File.ReadAllText(Path.Combine(_output, "client-process.json")));
        using (var session = ClientSession.Attach(Plan("attach"), _output, new ScriptedTransport())) Assert.Null(session.Architecture);
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

    [Fact] public void AnExitBeforeBepInExWroteItsLogSaysWhereToLook()
    {
        var error = Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => new Process(exitCode: 1), () => new ScriptedTransport(),
            (_, _) => Task.CompletedTask, default, () => " before BepInEx wrote its log", null));
        Assert.Contains("exited with code 1 before BepInEx wrote its log", error.Reason);
        var plain = Assert.Throws<WaitFailedException>(() => ClientSession.Launch(Plan(), _output, () => new Process(exitCode: 1), () => new ScriptedTransport(),
            (_, _) => Task.CompletedTask, default, () => null, null));
        Assert.EndsWith("exited with code 1", plain.Reason);
    }

    [Fact] public void AnOwnedClientListsTheLogsItKeeps()
    {
        RunLog[] logs = [new("client BepInEx log", Path.Combine(_output, "client-boot.game-0.log"), Required: true), new("client Player.log", Path.Combine(_output, "client-boot.game-1.log"))];
        using var session = ClientSession.Launch(Plan(), _output, () => new Process(), () => new ScriptedTransport(), (_, _) => Task.CompletedTask, default, null, logs);
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
        string install = Path.Combine(_output, "install");
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "patchers"));
        File.WriteAllText(Path.Combine(install, "BepInEx", "patchers", "RemovedMod.Preloader.dll"), "patcher");
        var plan = Plan(); plan.Install = install;
        var error = Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _output));
        Assert.Contains("RemovedMod.Preloader.dll", error.Message);
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
    [InlineData("attach-with-patchers", "leave out install")]
    [InlineData("patcher-path", "not paths")]
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
            case "attach-with-patchers": plan.Mode = "attach"; plan.Install = ""; plan.Patchers = ["HookGenPatcher"]; break;
            case "patcher-path": plan.Patchers = ["../HookGenPatcher"]; break;
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
