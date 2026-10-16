using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

// The hosted runner: the dedicated server on its environment's server host, against a fake host (no shell, no game).
public sealed partial class HostedServerRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hosted-run-").FullName;
    private const string Install = "/opt/valheim/server", Runs = "/srv/vt/runs", RunId = "run-test";
    private const string RunDirectory = Runs + "/" + RunId;
    private string Mirror => Path.Combine(_root, "host");
    private string Output => Path.Combine(_root, "out");
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private FakeServerHost NewHost(FakeOwnedServer? server = null) => new("linux-box", Mirror, server);
    private FakeOwnedServer NewServer() => new("test.mod", saveRoot: RunDirectory + "/world");

    private (FakeOwnedServer Server, FakeServerHost Host, string Plan, ResolvedEnvironment Profile) LocalFixture()
    {
        bool windows = LocalServerMachine.Windows;
        string saveRoot = windows ? @"C:\vt\runs\run-test\world" : RunDirectory + "/world";
        var server = new FakeOwnedServer("test.mod", saveRoot);
        var host = new FakeServerHost("linux-box", Mirror, server, windows: windows);
        var (planPath, profilePath) = Write(host, hostPlatform: windows ? "windows" : "linux", hostShell: windows ? "pwsh" : "bash");
        var profile = TestEnvironment.Read(profilePath);
        profile.Hosts["linux-box"].Kind = "local";
        return (server, host, planPath, profile);
    }

    private static string ServerCapability(string output, string name) => Path.Combine(output, name);

    [Fact]
    public async Task ALocalServerPublishesThroughRealStartAndRestartAndRetiresAtTeardown()
    {
        var fixture = LocalFixture();
        using var machine = LocalServerMachine.Use();
        string leasePath = ServerCapability(Output, OwnedClientCommandLease.ServerFileName);
        string menuPins = ServerCapability(Output, OwnedClientCommandLease.ServerMenuPins);
        string worldPins = ServerCapability(Output, OwnedClientCommandLease.ServerWorldPins);

        int code = await PinnedServerRun.MainAsync(fixture.Profile, ["run", fixture.Plan, Output], Options(fixture.Host, fixture.Server, run =>
        {
            var first = JsonSerializer.Deserialize<OwnedClientCommandLease>(File.ReadAllText(leasePath));
            Assert.NotNull(first);
            Assert.Equal((OwnedClientCommandLease.ServerRole, 1, "9001", 5577, run.RuntimeDirectory),
                (first.Role, first.Pid, first.StartFileTimeUtc, first.Port, first.Install));
            Assert.DoesNotContain("worlduid", File.ReadAllText(menuPins));
            Assert.Contains("worlduid=1", File.ReadAllText(worldPins));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(OwnedClientCommandLease.PrivateMode, File.GetUnixFileMode(leasePath));
                Assert.Equal(OwnedClientCommandLease.PrivateMode, File.GetUnixFileMode(menuPins));
                Assert.Equal(OwnedClientCommandLease.PrivateMode, File.GetUnixFileMode(worldPins));
            }

            run.Session.Restart();
            var second = JsonSerializer.Deserialize<OwnedClientCommandLease>(File.ReadAllText(leasePath));
            Assert.NotNull(second);
            Assert.Equal((2, "9002"), (second.Pid, second.StartFileTimeUtc));
            Assert.Contains("worlduid=1", File.ReadAllText(worldPins));
            return Task.CompletedTask;
        }));

        Assert.Equal(0, code);
        Assert.False(File.Exists(leasePath));
        Assert.False(File.Exists(menuPins));
        Assert.False(File.Exists(worldPins));
    }

    // The lease is published only after Start returned: a separate cli during the read-only bootstrap and strict
    // verification finds no lease and sends nothing, and a boot that fails verification never publishes one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACommandBeforeStrictVerificationIsRefusedBecauseNoServerLeaseExistsYet(bool verificationFails)
    {
        var fixture = LocalFixture();
        fixture.Server.RefusePins = verificationFails;
        fixture.Server.PinGate = new ManualResetEventSlim(false);
        using var machine = LocalServerMachine.Use();
        bool scenarioRan = false;
        Task<int> running = PinnedServerRun.MainAsync(fixture.Profile, ["run", fixture.Plan, Output], Options(fixture.Host, fixture.Server, _ =>
        {
            scenarioRan = true;
            Assert.True(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
            Assert.True(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerWorldPins)));
            return Task.CompletedTask;
        }));
        try
        {
            Assert.True(fixture.Server.PinEntered.Wait(TimeSpan.FromSeconds(10)), "the fake server never reached strict startup verification");
            foreach (string name in new[] { OwnedClientCommandLease.ServerFileName, OwnedClientCommandLease.ServerMenuPins, OwnedClientCommandLease.ServerWorldPins })
                Assert.False(File.Exists(ServerCapability(Output, name)), name + " was published before strict verification passed");
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(3, OwnedCliCommand.Run(["--evidence", Output, "--role", "server", "--phase", "menu", "--command", "cli_manifest"], output, error));
            Assert.Empty(output.ToString());
            Assert.Contains(OwnedClientCommandLease.ServerFileName, error.ToString()); // Not found: each OS words it differently.
            Assert.Empty(Directory.GetFiles(Output, "owned-cli-command-*.jsonl"));
        }
        finally { fixture.Server.PinGate.Set(); }

        Assert.Equal(verificationFails ? 1 : 0, await running);
        Assert.Equal(!verificationFails, scenarioRan);
        foreach (string name in new[] { OwnedClientCommandLease.ServerFileName, OwnedClientCommandLease.ServerMenuPins, OwnedClientCommandLease.ServerWorldPins })
            Assert.False(File.Exists(ServerCapability(Output, name)));
    }

    // The lease is optional: a lease or world-pin file that cannot be written or retired warns and publishes less, and
    // never fails a healthy boot. A directory in the file's place makes every write and delete of it fail.
    [Theory]
    [InlineData(OwnedClientCommandLease.ServerFileName)]
    [InlineData(OwnedClientCommandLease.ServerWorldPins)]
    public async Task AnUnwritableServerCapabilityWarnsAndNeverFailsTheBoot(string blocked)
    {
        var fixture = LocalFixture();
        string blocker = ServerCapability(Output, blocked);
        fixture.Server.OnLaunch = _ => Directory.CreateDirectory(blocker);
        using var machine = LocalServerMachine.Use();
        bool scenarioRan = false;
        int code = await PinnedServerRun.MainAsync(fixture.Profile, ["run", fixture.Plan, Output], Options(fixture.Host, fixture.Server, _ =>
        {
            scenarioRan = true;
            Assert.True(Directory.Exists(blocker));
            // Nothing is published: a lease that cannot be written, or a new boot's lease beside old world pins that cannot
            // be removed. A separate cli therefore refuses.
            Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
            Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerMenuPins)));
            Directory.Delete(blocker); // Teardown's own retirement is not under test here.
            return Task.CompletedTask;
        }));

        Assert.True(scenarioRan);
        Assert.Equal(0, code);
    }

    // World pins that cannot be written after the lease was published warn and leave world-phase commands refused.
    [Fact]
    public void UnwritableWorldPinsAfterAPublishedLeaseWarnAndDoNotThrow()
    {
        var fixture = LocalFixture();
        using var machine = LocalServerMachine.Use();
        Directory.CreateDirectory(Output);
        var plan = ServerRunPlan.Read<ServerRunPlan>(fixture.Plan);
        IServerPlacement hosted = HostedServerRun.Create(fixture.Profile, plan, "test", new FakeRunHooks { Host = _ => fixture.Host, RunId = RunId });
        OwnedClientCommandLease.WriteServer(Output, 41, "100", 5577, Output, plan);
        string blocker = ServerCapability(Output, OwnedClientCommandLease.ServerWorldPins);
        Directory.CreateDirectory(blocker);

        hosted.PublishVerifiedWorld("1", Output);

        Assert.True(Directory.Exists(blocker));
        Assert.True(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
    }

    [Fact]
    public async Task CancellationRetiresTheLocalServersPublishedCapabilities()
    {
        var fixture = LocalFixture();
        using var cancellation = new RunCancellation();
        using var machine = LocalServerMachine.Use();
        await PinnedServerRun.MainAsync(fixture.Profile, ["run", fixture.Plan, Output], Options(fixture.Host, fixture.Server, _ =>
        {
            Assert.True(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
            Assert.True(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerWorldPins)));
            cancellation.SignalCancel();
            return Task.CompletedTask;
        }, cancellation: cancellation));

        Assert.True(cancellation.Token.IsCancellationRequested);
        Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
        Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerMenuPins)));
        Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerWorldPins)));
    }

    [Fact]
    public void AReusedOrGoneLaunchIdentityPublishesNoServerLease()
    {
        var fixture = LocalFixture();
        using var machine = LocalServerMachine.Use();
        Directory.CreateDirectory(Output);
        var plan = ServerRunPlan.Read<ServerRunPlan>(fixture.Plan);
        var hosted = HostedServerRun.Create(fixture.Profile, plan, "test",
            new FakeRunHooks { Host = _ => fixture.Host, RunId = RunId });
        fixture.Host.Running(41, "200");

        foreach (string name in new[] { OwnedClientCommandLease.ServerFileName, OwnedClientCommandLease.ServerMenuPins, OwnedClientCommandLease.ServerWorldPins })
            File.WriteAllText(ServerCapability(Output, name), "stale capability");
        hosted.WriteServerCommandLease(Output, 41, "100", new(41, ProbedState.Reused, "200", null));
        Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
        Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerMenuPins)));
        Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerWorldPins)));
        hosted.WriteServerCommandLease(Output, 42, "100", new(42, ProbedState.Gone, null, null));
        Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
    }

    [Theory]
    [InlineData((int)ProbedState.Starting)]
    [InlineData((int)ProbedState.Unreadable)]
    public void AnInconclusivePublicationProbeDoesNotFailTheHealthyBootOrLeaveAnOldLease(int stateValue)
    {
        var state = (ProbedState)stateValue;
        var fixture = LocalFixture();
        using var machine = LocalServerMachine.Use();
        Directory.CreateDirectory(Output);
        var hosted = HostedServerRun.Create(fixture.Profile, ServerRunPlan.Read<ServerRunPlan>(fixture.Plan), "test",
            new FakeRunHooks { Host = _ => fixture.Host, RunId = RunId });
        File.WriteAllText(ServerCapability(Output, OwnedClientCommandLease.ServerFileName), "stale capability");
        File.WriteAllText(ServerCapability(Output, OwnedClientCommandLease.ServerMenuPins), "stale pins");

        hosted.WriteServerCommandLease(Output, 41, "100", new(41, state, "100", null));

        Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
        Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerMenuPins)));
    }

    [Fact] public void RuntimeExplicitIsSerializedWithAResolvedRole()
    {
        var assumed = new GameRole { Runtime = "/runs", RuntimeExplicit = false };
        var explicitRole = new GameRole { Runtime = "/runs", RuntimeExplicit = true };

        Assert.False(JsonSerializer.Deserialize<GameRole>(JsonSerializer.Serialize(assumed))!.RuntimeExplicit);
        Assert.True(JsonSerializer.Deserialize<GameRole>(JsonSerializer.Serialize(explicitRole))!.RuntimeExplicit);
    }

    // These direct hosted-client tests stand in for campaign preparation: bind the staged CLI and install pins that preparation supplies.
    private ClientRunPlan PreparedClient(FakeServerHost host, int startSeconds = 30, int bepInExSeconds = 60, bool connect = true)
    {
        string install = host.Local("/Users/tester/valheim");
        string cli = Path.Combine(install, "BepInEx", "plugins", "valheimCLI.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.Copy(Path.Combine(TestCliSet.Source.Files, "valheimCLI.dll"), cli, overwrite: true);
        return new ClientRunPlan
        {
            Mode = "owned", Architecture = "x64", Install = _root, Port = 5578, StartSeconds = startSeconds,
            BepInExSeconds = bepInExSeconds, LaunchArguments = connect ? ["+connect", "linux-box:2456"] : [],
            InstallPins = InstallPins.Of(install), Pins = new() { [CliCapabilityManifest.PluginGuid] = TestCliSet.CorePin },
        };
    }
    [Fact] public void AWindowsPwshDedicatedServerProfileIsAcceptedBeforeAnyHostOperation()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host, hostPlatform: "windows", hostShell: "pwsh");
        var profile = TestEnvironment.Read(profilePath);
        var hosted = HostedServerRun.Create(profile, ServerRunPlan.Read<ServerRunPlan>(planPath), "test", new FakeRunHooks { Host = _ => host, RunId = RunId });
        Assert.Equal("windows", hosted.HostProfile.Platform);
        Assert.Empty(host.Runs);
    }

    [Fact] public void RemoteMacServerRefusesAStandaloneProfileBeforeTouchingItsHost()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host);
        var plan = ServerRunPlan.Read<ServerRunPlan>(planPath);
        plan.Executable = GameLaunch.ServerMacExecutable;
        plan.Runtime.Sha256.Clear();
        var profile = TestEnvironment.Read(profilePath);
        var serverHost = profile.Hosts[profile.Server!.Host];
        serverHost.Platform = "macos";
        serverHost.Kind = "ssh";
        var error = Assert.Throws<InvalidOperationException>(() => HostedServerRun.Create(profile, plan, "test",
            new FakeRunHooks { Host = _ => host, RunId = RunId }));
        Assert.Contains("prepared profile campaign", error.Message);
        Assert.Empty(host.Runs);
    }

    // #700: whether the runtime lies outside the install is decided on the host (links resolved there), before the run writes
    // anything under it; the inventory no longer compares the spelling. A standalone profile inside the install is refused.
    [Fact] public async Task AStandaloneRunRefusesARuntimeInsideTheInstallBeforeStaging()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host);
        var plan = ServerRunPlan.Read<ServerRunPlan>(planPath);
        var profile = TestEnvironment.Read(profilePath);
        profile.Server!.Runtime = Install + "/runs";
        var hosted = HostedServerRun.Create(profile, plan, "test", new FakeRunHooks { Host = _ => host, RunId = RunId });
        var error = await Assert.ThrowsAsync<ArgumentException>(() => hosted.LockAndPrepareRuntimeAsync(new ScenarioReport("inside"), plan, CancellationToken.None));
        Assert.Contains("overlaps the install", error.Message);
        Assert.Contains("apart", host.Scripts);
        Assert.DoesNotContain("profile-seed", host.Scripts);
        Assert.False(Directory.Exists(host.Local(Install + "/runs")));
    }

    // #257: a campaign's prepared profile is the server's runtime, verified where it is and never staged again (the campaign
    // test proves the passing path); one that changed between preparation and the run is refused before anything launches.
    [Fact] public async Task APreparedRuntimeThatChangedSincePreparationIsRefusedAndNotStagedAgain()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host);
        var plan = ServerRunPlan.Read<ServerRunPlan>(planPath);
        var environment = TestEnvironment.Read(profilePath);
        const string owned = Runs + "/vt-prep-changed/runtime";
        FakeServerHost.CopyDirectory(host.Local(Install), host.Local(owned));
        environment.Server!.Install = owned;
        environment.Server.PreparedGameRoot = Install;
        environment.Server.PreparedLoaderRoot = owned;
        environment.Server.PreparedSourceRoot = Install;
        var preparedListing = await ProfileSourceCheck.InspectAsync(host, Install, TimeSpan.FromSeconds(30));
        var sourceCheck = ProfileSourceCheck.Prepare(preparedListing, [GameLaunch.ServerLinuxExecutable]);
        sourceCheck.RecordPreparedProfile(preparedListing,
            await HostInstall.ListAsync(host, owned, TimeSpan.FromSeconds(30), cancellation: CancellationToken.None), []);
        var hosted = HostedServerRun.Create(environment, plan, "test", new FakeRunHooks { Host = _ => host, RunId = RunId }, prepared: true,
            sourceChecks: new Dictionary<string, ProfileSourceCheck> { ["server"] = sourceCheck });
        Assert.True(hosted.Prepared);
        Assert.Equal(owned, hosted.RuntimeDirectory);
        Assert.Equal(RunDirectory, hosted.RunDirectory); // world and boot evidence stay in the run's own directory
        var report = new ScenarioReport("prepared runtime");
        await hosted.LockAndPrepareRuntimeAsync(report, plan, CancellationToken.None);
        File.AppendAllText(Path.Combine(host.Local(Install), GameLaunch.ServerLinuxExecutable), " changed after preparation");
        var launch = GameLaunch.ForServerWithLoader(Install, owned, ["-batchmode"], null, ServerPlatform.Linux);
        var error = Assert.ThrowsAny<Exception>(() => ((IServerPlacement)hosted).Start(1, launch, Output,
            TimeSpan.FromSeconds(120), CancellationToken.None));
        Assert.Contains(GameLaunch.ServerLinuxExecutable, error.Message);
        Assert.DoesNotContain("profile-seed", host.Scripts);
        Assert.DoesNotContain("start", host.Scripts);
    }

    [Fact] public async Task APreparedUnixProfileRefusesASteamGameUpdateImmediatelyBeforeBoot()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host);
        var plan = ServerRunPlan.Read<ServerRunPlan>(planPath);
        var environment = TestEnvironment.Read(profilePath);
        const string owned = Runs + "/vt-prep-profile/runtime";
        FakeServerHost.CopyDirectory(host.Local(Install), host.Local(owned)); // a stand-in for the campaign's staged profile
        environment.Server!.Install = owned; // the campaign retires only this owned profile
        environment.Server.PreparedGameRoot = Install;
        environment.Server.PreparedLoaderRoot = owned;
        environment.Server.PreparedSourceRoot = Install;
        var sourceCheck = ProfileSourceCheck.Prepare(await ProfileSourceCheck.InspectAsync(host, Install, TimeSpan.FromSeconds(30)), []);
        sourceCheck.RecordPreparedProfile(sourceCheck.Source,
            await HostInstall.ListAsync(host, owned, TimeSpan.FromSeconds(30), cancellation: CancellationToken.None), []);
        var hosted = HostedServerRun.Create(environment, plan, "test", new FakeRunHooks { Host = _ => host, RunId = RunId }, prepared: true,
            sourceChecks: new Dictionary<string, ProfileSourceCheck> { ["server"] = sourceCheck });
        Assert.Equal(Install, hosted.GameDirectory);
        Assert.Equal(owned, hosted.LoaderDirectory);
        await hosted.LockAndPrepareRuntimeAsync(new ScenarioReport("prepared profile"), plan, CancellationToken.None);
        File.AppendAllText(Directory.EnumerateFiles(host.Local(Install), "assembly_valheim.dll", SearchOption.AllDirectories).Single(), "steam update");
        var launch = GameLaunch.ForServerWithLoader(Install, owned, ["-batchmode"], null, ServerPlatform.Linux);
        var error = Assert.ThrowsAny<Exception>(() => ((IServerPlacement)hosted).Start(1, launch, Output, TimeSpan.FromSeconds(120), CancellationToken.None));
        Assert.Contains("game", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start", host.Scripts);
        Assert.False(Directory.Exists(Path.Combine(Output, "boot-1")));
    }

    [Fact] public async Task APreparedWindowsProfileRefusesASwappedLinkedNativeDllBeforeBoot()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        var (planPath, profilePath) = Write(host, hostPlatform: "windows", hostShell: "pwsh");
        var plan = ServerRunPlan.Read<ServerRunPlan>(planPath);
        var environment = TestEnvironment.Read(profilePath);
        const string source = @"C:\valheim\server", owned = @"C:\vt\runs\vt-prep-profile\runtime";
        FakeServerHost.CopyDirectory(host.Local(source), host.Local(owned)); // a stand-in for the campaign's staged launch folder
        environment.Server!.Install = owned;
        environment.Server.PreparedGameRoot = owned;
        environment.Server.PreparedLoaderRoot = owned;
        environment.Server.PreparedSourceRoot = source;
        var sourceCheck = ProfileSourceCheck.Prepare(await ProfileSourceCheck.InspectAsync(host, source, TimeSpan.FromSeconds(30)), ["UnityPlayer.dll"]);
        var ownedListing = await HostInstall.ListAsync(host, owned, TimeSpan.FromSeconds(30), cancellation: CancellationToken.None);
        sourceCheck.RecordPreparedProfile(ownedListing, ownedListing, []);
        var hosted = HostedServerRun.Create(environment, plan, "test", new FakeRunHooks { Host = _ => host, RunId = RunId }, prepared: true,
            sourceChecks: new Dictionary<string, ProfileSourceCheck> { ["server"] = sourceCheck });
        await hosted.LockAndPrepareRuntimeAsync(new ScenarioReport("prepared profile"), plan, CancellationToken.None);
        File.WriteAllText(Path.Combine(host.Local(source), "UnityPlayer.dll"), "swapped native player");

        var launch = GameLaunch.ForServerWithLoader(owned, owned, ["-batchmode"], null, ServerPlatform.Windows);
        var error = Assert.ThrowsAny<Exception>(() => ((IServerPlacement)hosted).Start(1, launch, Output, TimeSpan.FromSeconds(120), CancellationToken.None));
        Assert.Contains("UnityPlayer.dll", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start", host.Scripts);
    }

    [Fact] public async Task AWindowsPwshProfileRunsTheWholeServerLifecycleAndKeepsOnlyItsEvidence()
    {
        const string windowsRuns = @"C:\vt\runs";
        var server = new FakeOwnedServer("test.mod", saveRoot: windowsRuns + @"\run-test\world");
        var host = new FakeServerHost("windows-server", Mirror, server, windows: true);
        var (plan, profile) = Write(host, hostPlatform: "windows", hostShell: "pwsh");
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.Equal(host.Claims, host.Releases);
        Assert.Contains("start", host.Scripts);
        Assert.Contains("stop", host.Scripts);
        Assert.Contains("keep", host.Scripts);
        Assert.Contains("fetch", host.Scripts);
        Assert.False(Directory.Exists(host.Local(windowsRuns + @"\run-test\runtime")));
        Assert.Contains("fake boot", File.ReadAllText(Path.Combine(Output, "boot-1", "game-0.log")));
        var roles = Result().GetProperty("Logs").EnumerateArray().Select(log => log.GetProperty("Role").GetString()).ToList();
        Assert.Contains("boot-1 Unity log", roles);
        Assert.Contains("boot-1 stdout", roles);
        Assert.Contains("boot-1 stderr", roles);
        Assert.Equal("server stdout\n", File.ReadAllText(Path.Combine(Output, "boot-1", "stdout.log")));
        Assert.True(File.Exists(Path.Combine(Output, "boot-1", "stderr.log")));
        var prebootProfile = Assert.Single(host.Runs, run => run.Script == "list" &&
            run.Variables["root"] == windowsRuns + @"\run-test\runtime" &&
            run.Variables["dirs"].Split('\n').Contains("UnityPlayer.dll"));
        Assert.Contains("BepInEx/core", prebootProfile.Variables["dirs"].Split('\n'));
        Assert.Equal("1", prebootProfile.Variables["content"]);
    }

    // #295: a standalone Windows server whose install's proxy (Doorstop 4) and doorstop_config.ini (Doorstop 3) are from different
    // versions is refused when its install is inspected, before a profile is staged or the server starts (no loader package
    // replaces the install's loader here; a pinned one would, and the profile would never use the install's pair).
    [Fact] public async Task AHostedWindowsServerRunRefusesAMixedDoorstopBeforeItsProfile()
    {
        var server = new FakeOwnedServer("test.mod", saveRoot: @"C:\vt\runs\run-test\world");
        var host = new FakeServerHost("windows-server", Mirror, server, windows: true);
        var (plan, profile) = Write(host, hostPlatform: "windows", hostShell: "pwsh", doorstopConfig: DoorstopMixPathsTests.Doorstop3Config);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.DoesNotContain("start", host.Scripts);
        Assert.DoesNotContain("profile-seed", host.Scripts);
        var step = Step(StageStep);
        Assert.False(step.GetProperty("Passed").GetBoolean());
        DoorstopMixPathsTests.AssertRefusal(new InvalidOperationException(step.GetProperty("Error").GetString()), "source install on windows-server",
            "is Doorstop 4, which reads only [General] in doorstop_config.ini, but that file is written for Doorstop 3 ([UnityDoorstop])");
    }

    // The host's install (the runtime the plan pins) and a local world; returns the plan and the profile.
    private (string Plan, string Profile) Write(FakeServerHost host, int planPort = 5577, string hostPlatform = "linux", string hostShell = "bash", bool withClient = false,
        bool crossplay = false, string portOption = "-port", string gamePort = "2456", object? steamAccounts = null, string? steamAccount = null,
        string doorstopConfig = DoorstopMixPathsTests.Doorstop4Config, string clientPlatform = "macos")
    {
        bool windows = hostPlatform == "windows";
        string hostInstall = windows ? @"C:\valheim\server" : Install;
        string install = host.Local(hostInstall);
        Directory.CreateDirectory(install);
        FakeInstalls.Server(install);
        if (windows)
        {
            File.Delete(Path.Combine(install, GameLaunch.ServerLinuxExecutable));
            File.WriteAllText(Path.Combine(install, GameLaunch.ServerWindowsExecutable), "server");
            File.WriteAllText(Path.Combine(install, "UnityPlayer.dll"), "native player");
            File.WriteAllText(Path.Combine(install, "winhttp.dll"), "MZ target_assembly");
            File.WriteAllText(Path.Combine(install, "doorstop_config.ini"), doorstopConfig);
        }
        else
        {
            File.WriteAllText(Path.Combine(install, GameLaunch.ServerLinuxExecutable), "server");
            FakeInstalls.LinuxLoader(install);
        }
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.Preloader.dll"), "preloader");
        string world = Path.Combine(_root, "world");
        Directory.CreateDirectory(Path.Combine(world, "worlds_local"));
        File.WriteAllText(Path.Combine(world, "worlds_local", "Test.db"), "world");
        var plan = new Dictionary<string, object>
        {
            ["scenario"] = "smoke",
            ["runtime"] = new { source = install, sha256 = WorldFixture.Manifest(install) },
            ["world"] = new { source = world, sha256 = WorldFixture.Manifest(world) },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}", portOption, gamePort, "-logFile", "{runtime}/toolkit-unity.log" },
            ["pins"] = new Dictionary<string, string> { ["worlduid"] = "1" },
            ["port"] = planPort,
        };
        if (crossplay) plan["crossplay"] = true;
        plan["runtimePins"] = InstallPins.Of(install);
        string planPath = Path.Combine(_root, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var hosts = new Dictionary<string, object>
        {
            ["linux-box"] = new { kind = "ssh", platform = hostPlatform, shell = hostShell, destination = "tester@linux-box.example", @lock = hostPlatform == "windows" ? @"C:\vt\lock" : "/var/tmp/vt/lock" },
        };
        var clients = new Dictionary<string, object>();
        if (withClient)
        {
            hosts["mac-gpu"] = new { kind = "ssh", platform = clientPlatform, shell = "bash", destination = "tester@mac-gpu.example", @lock = "/Users/tester/lock" };
            var player = new Dictionary<string, object> { ["host"] = "mac-gpu", ["install"] = "/Users/tester/valheim", ["runtime"] = "/Users/tester/runs", ["cliPort"] = 5578 };
            if (steamAccount != null) player["steamAccount"] = steamAccount;
            clients["player"] = player;
        }
        if (steamAccounts != null) hosts[LeaseBox.Name] = LeaseBox.Profile;
        var server = hostPlatform == "windows"
            ? new { host = "linux-box", install = @"C:\valheim\server", runtime = @"C:\vt\runs", cliPort = 5577, gamePort = 2456 }
            : new { host = "linux-box", install = Install, runtime = Runs, cliPort = 5577, gamePort = 2456 };
        string profilePath = Path.Combine(_root, "environment.json");
        File.WriteAllText(profilePath, JsonSerializer.Serialize(steamAccounts == null ? new { hosts, server, clients } : (object)new { hosts, server, clients, steamAccounts }));
        return (planPath, profilePath);
    }

    // A standalone source with one real mod assembly and a readable world identity. The three shapes exercise the
    // pin-as-found contract without a game: no expected pins, every expected pin, and a mixed input.
    private (string Plan, string Profile, string Mod) PinPlan(FakeServerHost host, string shape, bool scriptPlugin = false)
    {
        var (planPath, profilePath) = Write(host);
        string mod = Path.Combine(host.Local(Install), "BepInEx", scriptPlugin ? "scripts" : "plugins", "TestMod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(mod)!);
        File.WriteAllBytes(mod, RegressionRig.Assembly("TestMod", new("test.mod")));
        string? scriptEngine = null;
        if (scriptPlugin)
        {
            scriptEngine = Path.Combine(host.Local(Install), "BepInEx", "plugins", "ScriptEngine.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(scriptEngine)!);
            File.WriteAllBytes(scriptEngine, RegressionRig.Assembly("ScriptEngine", new(OwnedClientPreflight.ScriptEngine)));
            string config = Path.Combine(host.Local(Install), "BepInEx", "config", OwnedClientPreflight.ScriptEngineConfig);
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "[General]\nLoadOnStart = true\n");
        }
        string world = Path.Combine(_root, "world");
        Directory.Delete(world, recursive: true);
        DefaultSmokeWorld.PrepareServerSaveRoot(world);
        var plan = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(planPath))!;
        var runtime = plan["runtime"]!;
        var fixture = plan["world"]!;
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        if (shape == "full")
        {
            string sourceCli = Path.Combine(host.Local(Install), "BepInEx", "plugins", "valheimCLI.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceCli)!);
            File.Copy(Path.Combine(TestCliSet.Source.Files, "valheimCLI.dll"), sourceCli, overwrite: true);
            runtime["sha256"] = JsonSerializer.SerializeToNode(WorldFixture.Manifest(host.Local(Install)));
            fixture["sha256"] = JsonSerializer.SerializeToNode(WorldFixture.Manifest(world));
            pins["worlduid"] = DefaultSmokeWorld.Uid;
            pins["test.mod"] = FileHash.Md5(mod);
            if (scriptEngine != null) pins[OwnedClientPreflight.ScriptEngine] = FileHash.Md5(scriptEngine);
            pins["valheimCLI.valheimCLI"] = TestCliSet.CorePin;
            plan["runtimePins"] = JsonSerializer.SerializeToNode(InstallPins.Of(host.Local(Install)));
        }
        else
        {
            runtime["sha256"] = JsonSerializer.SerializeToNode(new Dictionary<string, string>());
            fixture["sha256"] = JsonSerializer.SerializeToNode(new Dictionary<string, string>());
            plan.AsObject().Remove("runtimePins");
            if (shape == "mixed")
            {
                pins["worlduid"] = DefaultSmokeWorld.Uid;
                plan["runtimePins"] = JsonSerializer.SerializeToNode(InstallPins.Of(host.Local(Install)));
            }
        }
        plan["pins"] = JsonSerializer.SerializeToNode(pins);
        File.WriteAllText(planPath, plan.ToJsonString());
        return (planPath, profilePath, mod);
    }

    [Fact] public async Task AStandalonePlanWithNoPinsBindsAndRecordsEverythingItStages()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile, mod) = PinPlan(host, "observed");
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server));
        Assert.True(code == 0, File.ReadAllText(Path.Combine(Output, "result.json")));

        var result = Result();
        Assert.Equal("pins observed, not reviewed", result.GetProperty("Provenance").GetProperty("pins").GetString());
        var origins = result.GetProperty("PinOrigins");
        Assert.All(origins.EnumerateObject(), origin => Assert.Equal("observed", origin.Value.GetString()));
        Assert.Equal("observed", origins.GetProperty("plugins.test.mod").GetString());
        Assert.Equal("observed", origins.GetProperty("worldUid").GetString());
        // A Linux server's recorder keeps its stdout, scanned with the rest.
        Assert.Contains(result.GetProperty("Logs").EnumerateArray(), log => log.GetProperty("Role").GetString() == "boot-1 stdout");
        Assert.Contains(server.Events, item => item.StartsWith("pins", StringComparison.Ordinal));
        Assert.Equal(FileHash.Md5(mod), result.GetProperty("Plugins").GetProperty("server").GetProperty("test.mod").GetString());
        Assert.False(result.GetProperty("Provenance").TryGetProperty("environment", out _));

        var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "input-hashes.json"))).RootElement;
        Assert.Equal(DefaultSmokeWorld.Uid, inputs.GetProperty("worldUid").GetString());
        Assert.Equal(FileHash.Md5(mod), inputs.GetProperty("plugins").GetProperty("test.mod").GetString());
        Assert.Equal("observed", inputs.GetProperty("pinOrigins").GetProperty("runtimePins").GetString());
    }

    [Fact] public async Task AStandalonePlanWithEveryPinRecordsTheCliReplacementAsObserved()
    {
        var host = NewHost();
        var (plan, profile, _) = PinPlan(host, "full");
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["validate", plan, Output], Options(host, null)));
        var origins = Result().GetProperty("PinOrigins");
        Assert.NotEmpty(origins.EnumerateObject());
        Assert.All(origins.EnumerateObject().Where(origin => origin.Name != "plugins." + CliCapabilityManifest.PluginGuid),
            origin => Assert.Equal("plan", origin.Value.GetString()));
        Assert.Equal("observed", origins.GetProperty("plugins." + CliCapabilityManifest.PluginGuid).GetString());
        Assert.Equal(TestCliSet.CorePin, JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "input-hashes.json"))).RootElement
            .GetProperty("sourceValheimCliPins").GetProperty(CliCapabilityManifest.PluginGuid).GetString());
        Assert.Equal("pins observed, not reviewed", Result().GetProperty("Provenance").GetProperty("pins").GetString());
    }

    [Fact] public async Task AStandaloneMixedPlanChecksWhatItGivesAndObservesTheRest()
    {
        var host = NewHost();
        var (plan, profile, _) = PinPlan(host, "mixed");
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["validate", plan, Output], Options(host, null)));
        var origins = Result().GetProperty("PinOrigins");
        Assert.Equal("plan", origins.GetProperty("runtimePins").GetString());
        Assert.Equal("plan", origins.GetProperty("worldUid").GetString());
        Assert.Equal("observed", origins.GetProperty("runtimeManifest").GetString());
        Assert.Equal("observed", origins.GetProperty("worldManifest").GetString());
        Assert.Equal("observed", origins.GetProperty("plugins.test.mod").GetString());
    }

    [Fact] public async Task AStandaloneObservedScriptPluginIsPinnedFromTheStagedProfile()
    {
        var host = NewHost();
        var (plan, profile, mod) = PinPlan(host, "observed", scriptPlugin: true);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["validate", plan, Output], Options(host, null)));
        var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "input-hashes.json"))).RootElement;
        Assert.Equal(FileHash.Md5(mod), inputs.GetProperty("plugins").GetProperty("test.mod").GetString());
        Assert.Equal("observed", Result().GetProperty("PinOrigins").GetProperty("plugins.test.mod").GetString());
        Assert.Equal("observed", Result().GetProperty("PinOrigins").GetProperty("plugins." + OwnedClientPreflight.ScriptEngine).GetString());
    }

    [Fact] public async Task AStandaloneSuppliedScriptPluginPinIsCheckedAndKeptAsAPlanOrigin()
    {
        var host = NewHost();
        var (plan, profile, mod) = PinPlan(host, "full", scriptPlugin: true);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["validate", plan, Output], Options(host, null)));
        var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "input-hashes.json"))).RootElement;
        Assert.Equal(FileHash.Md5(mod), inputs.GetProperty("plugins").GetProperty("test.mod").GetString());
        Assert.Equal("plan", Result().GetProperty("PinOrigins").GetProperty("plugins.test.mod").GetString());
        Assert.Equal("plan", Result().GetProperty("PinOrigins").GetProperty("plugins." + OwnedClientPreflight.ScriptEngine).GetString());
    }

    [Fact] public async Task AStandaloneSuppliedInstallCliPinIsCheckedBeforeTheToolkitReplacesIt()
    {
        var host = NewHost();
        var (planPath, profilePath, _) = PinPlan(host, "full");
        string installCli = Path.Combine(host.Local(Install), "BepInEx", "plugins", "valheimCLI.dll");
        File.WriteAllBytes(installCli, RegressionRig.Assembly("valheimCLI-source", new("valheimCLI.valheimCLI")));
        var plan = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(planPath))!;
        plan["runtime"]!["sha256"] = JsonSerializer.SerializeToNode(WorldFixture.Manifest(host.Local(Install)));
        plan["pins"]!["valheimCLI.valheimCLI"] = FileHash.Md5(installCli);
        File.WriteAllText(planPath, plan.ToJsonString());

        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profilePath), ["validate", planPath, Output], Options(host, null)));
        var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "input-hashes.json"))).RootElement;
        Assert.Equal(TestCliSet.CorePin, inputs.GetProperty("plugins").GetProperty("valheimCLI.valheimCLI").GetString());
        Assert.Equal(FileHash.Md5(installCli), inputs.GetProperty("sourceValheimCliPins").GetProperty("valheimCLI.valheimCLI").GetString());
        Assert.Equal("BepInEx/plugins/valheimCLI.dll", Assert.Single(inputs.GetProperty("replacedValheimCliPaths").EnumerateArray()).GetString());
        Assert.Equal("observed", Result().GetProperty("PinOrigins").GetProperty("plugins.valheimCLI.valheimCLI").GetString());
        var provenance = Result().GetProperty("Provenance");
        Assert.Equal("valheimCLI.valheimCLI=" + FileHash.Md5(installCli), provenance.GetProperty("sourceValheimCliPins").GetString());
        Assert.Equal("BepInEx/plugins/valheimCLI.dll", provenance.GetProperty("replacedValheimCliPaths").GetString());
    }

    [Fact] public async Task AStandaloneStagedCliMatchDoesNotSkipTheSuppliedSourcePinCheck()
    {
        var host = NewHost();
        var (planPath, profilePath, _) = PinPlan(host, "full");
        string installCli = Path.Combine(host.Local(Install), "BepInEx", "plugins", "valheimCLI.dll");
        File.WriteAllBytes(installCli, RegressionRig.Assembly("valheimCLI-drifted-source", new("valheimCLI.valheimCLI")));
        var plan = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(planPath))!;
        plan["runtime"]!["sha256"] = JsonSerializer.SerializeToNode(WorldFixture.Manifest(host.Local(Install)));
        File.WriteAllText(planPath, plan.ToJsonString());

        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profilePath), ["validate", planPath, Output], Options(host, null)));
        var step = Assert.Single(Result().GetProperty("Steps").EnumerateArray(), item =>
            item.GetProperty("Name").GetString() == "check the server host's install against the pinned runtime and stage its profile");
        Assert.Contains("install build it replaces", step.GetProperty("Error").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceCliCopiesDoNotInspectNonLoadingOrDuplicateReplacements(bool alsoInPlugins)
    {
        var host = NewHost();
        var (plan, profile, _) = PinPlan(host, "observed");
        string scripts = Path.Combine(host.Local(Install), "BepInEx", "scripts", "old-valheimCLI.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(scripts)!);
        File.WriteAllBytes(scripts, RegressionRig.Assembly("old-cli-script", new("valheimCLI.valheimCLI")));
        if (alsoInPlugins)
        {
            string plugins = Path.Combine(host.Local(Install), "BepInEx", "plugins", "old-valheimCLI.dll");
            File.WriteAllBytes(plugins, RegressionRig.Assembly("old-cli-plugin", new("valheimCLI.valheimCLI")));
        }

        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["validate", plan, Output], Options(host, null)));
        Assert.Equal(TestCliSet.CorePin, JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "input-hashes.json"))).RootElement
            .GetProperty("plugins").GetProperty("valheimCLI.valheimCLI").GetString());
    }

    [Fact] public async Task ARequiredPluginMissingFromTheStagedPinsIsRefused()
    {
        var host = NewHost();
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["validate", plan, Output],
            Options(host, null, requiredPlugins: ["test.mod"])));
        var stage = Step(StageStep);
        Assert.False(stage.GetProperty("Passed").GetBoolean());
        Assert.Contains("missing required plugin test.mod", stage.GetProperty("Error").GetString());
    }

    [Fact] public async Task AnEmptyNewWorldFixtureWithWorldAnyStagesAndRuns()
    {
        var server = NewServer(); var host = NewHost(server);
        var (planPath, profilePath, _) = PinPlan(host, "observed");
        string fixture = Path.Combine(_root, "new-world");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "NEW-WORLD.txt"), "The game creates this world on first boot.\n");
        var plan = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(planPath))!;
        plan["world"]!["source"] = fixture;
        plan["world"]!["sha256"] = JsonSerializer.SerializeToNode(WorldFixture.Manifest(fixture));
        plan["pins"] = JsonSerializer.SerializeToNode(new Dictionary<string, string> { ["world"] = "any" });
        File.WriteAllText(planPath, plan.ToJsonString());

        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profilePath), ["run", planPath, Output], Options(host, server)));
        Assert.Equal("plan", Result().GetProperty("PinOrigins").GetProperty("worldUid").GetString());
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "input-hashes.json"))).RootElement
            .GetProperty("worldUid").ValueKind);
        Assert.True(File.Exists(Path.Combine(Output, "host-world", "NEW-WORLD.txt")));
    }

    [Fact] public async Task AnObservedPluginChangedAfterStagingIsRefusedBeforeBoot()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile, _) = PinPlan(host, "observed");
        bool changed = false;
        host.BeforeScript = script =>
        {
            if (script != "port" || changed) return;
            changed = true;
            File.WriteAllText(Path.Combine(host.Local(RunDirectory + "/runtime"), "BepInEx", "plugins", "TestMod.dll"), "swapped after staging");
        };
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.True(changed);
        Assert.DoesNotContain("start", host.Scripts);
        var start = Step("start and verify owned dedicated fixture");
        Assert.False(start.GetProperty("Passed").GetBoolean());
        Assert.Contains("prepared server profile before launch plugins", start.GetProperty("Error").GetString());
        Assert.Contains("different hash: BepInEx/plugins/TestMod.dll", start.GetProperty("Error").GetString());
    }

    private static PinnedServerRunOptions<ServerRunPlan> Options(FakeServerHost host, FakeOwnedServer? server, Func<TestRun<ServerRunPlan>, Task>? scenario = null,
        FakeServerHost? clientHost = null, IGameTransport? clientTransport = null, IGameHost? leaseHost = null, string name = "toolkit-smoke",
        RunCancellation? cancellation = null, IReadOnlyList<string>? requiredPlugins = null, bool checkpointFixture = false, Func<IGameTransport>? connect = null) => new()
    {
        Name = name, CheckpointFixture = checkpointFixture,
        ReadPlan = path => { var plan = ServerRunPlan.Read<ServerRunPlan>(path); plan.ValidateServerPlan(requiredPlugins ?? [], "TEST_SESSION_TOKEN"); return plan; },
        Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
        Scenario = TestRun.Scenario(scenario ?? (_ => Task.CompletedTask)),
        Hooks = new FakeRunHooks
        {
            Host = hostName => hostName == "linux-box" ? host : hostName == LeaseBox.Name && leaseHost != null ? leaseHost : clientHost ?? throw new InvalidOperationException("No fake host " + hostName),
            Connect = port => port == 15578 ? clientTransport! : connect?.Invoke() ?? server!.Connect(),
            StateWaits = false, RunId = RunId, Cancellation = cancellation,
        },
    };
    // The step that checks a standalone server's install against the plan and stages its profile.
    private const string StageStep = "check the server host's install against the pinned runtime and stage its profile";
    // Where in a fake host's script log the first journal append of that kind ran.
    private static int JournalIndex(FakeServerHost host, string kind) => host.Runs.ToList().FindIndex(run => run.Script == "journal" &&
        Encoding.UTF8.GetString(Convert.FromBase64String(run.Variables["line"])).Contains($"\"kind\":\"{kind}\"", StringComparison.Ordinal));
    private JsonElement Result() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement;
    private IReadOnlyList<string?> StepNames() => Result().GetProperty("Steps").EnumerateArray().Select(step => step.GetProperty("Name").GetString()).ToList();
    private JsonElement Step(string name) => Result().GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == name);

    // #194: what the run wrote in the server's profile is kept; a profile kept on request, or whose server may still run, stays on the host.
    [Fact] public async Task TheServerProfileKeepsWhatTheRunWroteAndStaysWhenItMustOrIsAskedTo()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            string cache = Path.Combine(host.Local(run.RuntimeDirectory), "BepInEx", "cache");
            Directory.CreateDirectory(cache); File.WriteAllText(Path.Combine(cache, "audit.txt"), "written on the host");
            return Task.CompletedTask;
        })));
        Assert.Equal("written on the host", File.ReadAllText(Path.Combine(Output, "runtime-changes", "BepInEx", "cache", "audit.txt")));
        var changes = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "runtime-changes", "changes.json"))).RootElement;
        Assert.Contains("BepInEx/cache/audit.txt", changes.GetProperty("Added").EnumerateArray().Select(e => e.GetString()));
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime-changes"))); // fetched, so not kept twice

        // A cleanup the host could not finish fails the Cleanup step: the run fails (exit 1), its scenario still passed, and
        // the host's lock is released.
        Directory.Delete(Output, true); if (Directory.Exists(host.Local(RunDirectory))) Directory.Delete(host.Local(RunDirectory), true);
        host.Failures["retire"] = new HostResult(HostOutcome.Exited, 3, "", "rm: cannot remove", TimeSpan.Zero, false);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.StartsWith("cleanup failed", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
        // This scenario records no step of its own, so the runtime-ready state is the one that says the run got that far.
        Assert.Equal((false, true, false), (Result().GetProperty("Passed").GetBoolean(), Result().GetProperty("RuntimeReady").GetBoolean(), Result().GetProperty("CleanupVerified").GetBoolean()));
        var failed = Assert.Single(Result().GetProperty("Steps").EnumerateArray(), s => !s.GetProperty("Passed").GetBoolean());
        Assert.Equal(("remove the server host's runtime copy, keeping what the run changed", "Cleanup"), (failed.GetProperty("Name").GetString(), failed.GetProperty("Phase").GetString()));
        Assert.Equal(host.Claims.Count, host.Releases.Count);
        host.Failures.Remove("retire");

        // Kept on request: no retire script, and the report names the copy.
        Directory.Delete(Output, true); if (Directory.Exists(host.Local(RunDirectory))) Directory.Delete(host.Local(RunDirectory), true);
        RunRetirement.KeepOverride.Value = true; // VALHEIM_TESTING_KEEP_RUNTIME=1, in this test's flow only
        try { Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server))); }
        finally { RunRetirement.KeepOverride.Value = null; }
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.Contains("kept on request", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
    }
    [Fact] public async Task ARemoteRunStagesAProfileOnTheHostReachesTheCliThroughTheTunnelAndStopsOnlyItsServer()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        IGameHost? seenHost = null; string? seenRuntime = null;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            seenHost = run.ServerHost; seenRuntime = run.RuntimeDirectory;
            Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
            Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerMenuPins)));
            Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerWorldPins)));
            run.Session.Restart(); // A second boot: its own boot directory and log, and a stop of only the first.
            Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerFileName)));
            Assert.False(File.Exists(ServerCapability(Output, OwnedClientCommandLease.ServerWorldPins)));
            return Task.CompletedTask;
        }));
        Assert.Equal(0, code);
        // The run journal on the server host (#257): the lock once held, each boot journalled before its start and its process after,
        // the lock's release and the run's end. Each boot's intent precedes its start script.
        var journal = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5));
        var processes = journal.Where(record => record.Actor == "server" && record.Entry.Kind.StartsWith("process-", StringComparison.Ordinal)).ToList();
        Assert.Equal([JournalEntry.ProcessIntended, JournalEntry.ProcessStarted, JournalEntry.ProcessIntended, JournalEntry.ProcessStarted], processes.Select(record => record.Entry.Kind));
        Assert.Equal(RunDirectory + "/boot-2", processes.Last().Entry.Fields["bootDirectory"]);
        // Each boot journals the quit budget its stop gives it, which env recover gives it too (#665).
        string quit = host.Runs.First(run => run.Script == "stop").Variables["quit"];
        Assert.All(processes, record => Assert.Equal(quit, record.Entry.Fields["quitSeconds"]));
        // Each started process carries its command line's hash, read on the host right after the start (#257 Q2's third fact).
        Assert.All(journal.Where(record => record.Entry.Kind == JournalEntry.ProcessStarted), record =>
            Assert.Equal(FakeServerHost.CommandLineSha256(record.Entry.Fields["pid"]), record.Entry.Fields["commandLineSha256"]));
        Assert.Equal([JournalEntry.LockIntended, JournalEntry.LockHeld, JournalEntry.LockReleased, JournalEntry.RunEnded],
            journal.Where(record => record.Actor == "run").Select(record => record.Entry.Kind));
        Assert.Equal("passed", journal.Last(record => record.Actor == "run").Entry.Fields["state"]);
        Assert.True(JournalIndex(host, JournalEntry.ProcessIntended) < host.Runs.ToList().FindIndex(run => run.Script == "start"));
        // Every owned server boot gets its test access, the restart's included; no runner option turns it off (#257 Q5).
        Assert.Equal(["devcommands", "confirmcheats", "devcommands", "confirmcheats"],
            server.Events.Where(e => e.StartsWith("devcommands") || e.StartsWith("confirmcheats")).Select(e => e.TrimEnd("0123456789".ToCharArray())));
        Assert.Same(host, seenHost); Assert.Equal(RunDirectory + "/runtime", seenRuntime);
        Assert.Equal(new[] { "enough free disk space for the copies", "take the server host's lock", StageStep, "copy and verify pinned world", "ship and verify the world copy on the server host",
                "server runtime has the plan's server executable", "server runtime is the pinned game build, loader and patchers",
                "CLI port is free on the server host", "open the loopback CLI tunnel to the server host", "start and verify owned dedicated fixture", "stop only owned server",
                "fetch the server host's world copy", "verify server's source install after the run (size and modified time)",
                "remove the server host's runtime copy, keeping what the run changed", "remove the fetched world copy on the server host",
                "remove the fetched boot files on the server host", "remove the fetched boot files on the server host", "close the CLI tunnel", "release the server host's lock", "scan run logs" }, StepNames());

        // #730: nothing of the install is copied. Its profile is staged in this run's own directory (the loader seeded from the install,
        // never its game files on Linux), and journalled before it is made and once made; the world copy is shipped beside it.
        Assert.DoesNotContain("copy", host.Scripts);
        var seed = Assert.Single(host.Runs, run => run.Script == "profile-seed");
        Assert.Equal(Install, seed.Variables["source"]); Assert.Equal(RunDirectory + "/runtime", seed.Variables["runtime"]);
        Assert.Equal(new[] { RunDirectory + "/staging", RunDirectory + "/world" }, host.Runs.Where(run => run.Script == "ship").Select(run => run.Variables["dest"]));
        var profileEntries = journal.Where(record => record.Actor == "server" && record.Entry.Fields.GetValueOrDefault("runtime") == RunDirectory + "/runtime").ToList();
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.CopyRetired], profileEntries.Select(record => record.Entry.Kind));
        Assert.Equal((RunDirectory + "/staging", RunDirectory, "server", Install), (profileEntries[0].Entry.Fields["stage"], profileEntries[0].Entry.Fields["parent"],
            profileEntries[0].Entry.Fields["runtimeKind"], profileEntries[0].Entry.Fields["launchRoot"]));
        Assert.True(JournalIndex(host, JournalEntry.CopyIntended) < host.Runs.FindIndex(run => run.Script == "profile-seed"));
        // Each boot starts with the plan's arguments on the host's paths, its own boot directory and the logs moved aside.
        var starts = host.Runs.Where(run => run.Script == "start").ToList();
        Assert.Equal(new[] { RunDirectory + "/boot-1", RunDirectory + "/boot-2" }, starts.Select(start => start.Variables["dir"]));
        var prebootSource = host.Runs.Where(run => run.Script == "list" && run.Variables["root"] == Install && run.Variables["dirs"].Length != 0).ToList();
        Assert.Equal(2, prebootSource.Count); // initial boot and restart, neither a full source re-hash
        Assert.All(prebootSource, run =>
        {
            Assert.Equal(string.Join('\n', HostInstall.ListingPatterns(InstallPins.ManagedFolders
                .Concat([HostInstall.LiteralPathPattern(GameLaunch.ServerLinuxExecutable)]))), run.Variables["dirs"]);
            Assert.Equal("1", run.Variables["content"]);
            Assert.Equal("0", run.Variables["metadata"]);
        });
        var prebootLoader = host.Runs.Where(run => run.Script == "list" && run.Variables["root"] == RunDirectory + "/runtime" &&
            run.Variables["dirs"] == string.Join('\n', HostInstall.ListingPatterns(HostInstall.ProfilePinPaths))).ToList();
        Assert.Equal(2, prebootLoader.Count);
        Assert.All(prebootLoader, run => Assert.Equal(("1", "0"), (run.Variables["content"], run.Variables["metadata"])));
        Assert.Equal("BepInEx/LogOutput.log\ntoolkit-unity.log", starts[0].Variables["logs"]);
        var spec = FakeServerHost.Spec(starts[0].Variables["spec"]);
        Assert.Equal(new[] { "-batchmode", "-nographics", "-savedir", RunDirectory + "/world", "-port", "2456", "-logFile", RunDirectory + "/runtime/toolkit-unity.log" },
            spec.Where(line => line.Kind == "arg").Select(line => line.Text));
        Assert.Contains(("env", "DOORSTOP_TARGET_ASSEMBLY=" + RunDirectory + "/runtime/BepInEx/core/BepInEx.Preloader.dll"), spec);
        // The game runs from its install, never changed; BepInEx from the profile.
        Assert.Equal((Install, RunDirectory + "/runtime"), (starts[0].Variables["runtime"], starts[0].Variables["logroot"]));
        Assert.Contains(("prepend", "LD_LIBRARY_PATH=" + Install + "/linux64:" + RunDirectory + "/runtime/doorstop_libs"), spec);
        // The listening line is awaited in this boot's log on the host, from its start.
        Assert.All(host.Runs.Where(run => run.Script == "follow"), run => { Assert.Equal(RunDirectory + "/runtime/BepInEx/LogOutput.log", run.Variables["log"]); Assert.Equal("0", run.Variables["offset"]); });
        // Only the two processes this run started were stopped, each by its own identity, and each boot's evidence came back.
        Assert.Equal(new[] { ("1", "9001"), ("2", "9002") }, host.Stops);
        Assert.Equal(new[] { "launch1", "stop1", "launch2", "stop2" }, server.Events.Where(e => e.StartsWith("launch") || e.StartsWith("stop")));
        Assert.Contains("fake boot 1", File.ReadAllText(Path.Combine(Output, "boot-1", "game-0.log")));
        Assert.Contains("fake boot 2", File.ReadAllText(Path.Combine(Output, "boot-2", "game-0.log")));
        Assert.True(File.Exists(Path.Combine(Output, "host-world", "worlds_local", "Test.db")));
        // #194: the host's runtime copy went at teardown, under the lock; what the run changed in it came back.
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.False(Directory.Exists(host.Local(RunDirectory))); // fetched world and every boot retired under the same lock
        Assert.True(File.Exists(Path.Combine(Output, "runtime-changes", "changes.json")));
        Assert.StartsWith("removed linux-box:" + RunDirectory + "/runtime", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
        int retire = host.Runs.FindIndex(run => run.Script == "retire");
        Assert.True(retire > host.Runs.FindLastIndex(run => run.Script == "stop") && retire < host.Runs.Count);
        var process = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "boot-1.process.json"))).RootElement;
        Assert.Equal(1, process.GetProperty("pid").GetInt32()); Assert.Equal("9001", process.GetProperty("startIdentity").GetString());
        Assert.Equal(RunDirectory + "/boot-1", process.GetProperty("bootDirectory").GetString());
        // The tunnel closed, the lock was taken and released once, by the same claimant.
        Assert.True(Assert.Single(host.Tunnels).Stopped);
        Assert.Equal(host.Claims, host.Releases); Assert.StartsWith("toolkit-smoke run-test", Assert.Single(host.Claims));
        var provenance = Result().GetProperty("Provenance");
        Assert.Equal("linux-box", provenance.GetProperty("serverHost").GetString());
        Assert.Equal(RunDirectory, provenance.GetProperty("hostRunDirectory").GetString());
        Assert.Equal("linux-box:" + Install, provenance.GetProperty("runtimeSource").GetString());
        Assert.Equal(InstallPins.Of(host.Local(Install)).Game, provenance.GetProperty("runtimeGameSha256").GetString());
        Assert.Equal("1,2", provenance.GetProperty("ownedPids").GetString());
        Assert.False(provenance.TryGetProperty("outcome", out _));
    }

    [Theory]
    [InlineData("run-write.txt")]
    [InlineData("logs/steam-runtime.txt")]
    public async Task ASourceWriteDuringTheFakeRunFailsTheRecordedPostRunVerdictAndNamesTheFile(string relative)
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, _ =>
        {
            string path = Path.Combine(host.Local(Install), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "written while the fake server ran");
            return Task.CompletedTask;
        }));

        Assert.Equal(1, code);
        var result = Result();
        var step = result.GetProperty("Steps").EnumerateArray().Single(item =>
            item.GetProperty("Name").GetString()!.StartsWith("verify server's source install after the run", StringComparison.Ordinal));
        Assert.False(step.GetProperty("Passed").GetBoolean());
        Assert.Contains(relative, step.GetProperty("Error").GetString());
        Assert.True(File.Exists(Path.Combine(host.Local(Install), relative))); // integrity findings are never repaired
    }

    // #257: one journal run per run. This machine's world copy is journalled under the run's own id, beside its server's
    // entries, not under a run id of its own; and the check of the server host's install leaves out its logs/ (Steam's runtime
    // output), which the pinned manifest may list or not, while the install keeps it and the profile never takes it.
    [Fact] public async Task AHostedRunJournalsItsLocalCopiesUnderItsOwnIdAndItsInstallCheckLeavesOutSteamsLogs()
    {
        var server = NewServer(); var host = NewHost(server);
        string logs = Path.Combine(host.Local(Install), "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "connection_log_2456.txt"), "Steam's own, from another day");
        var (plan, profile) = Write(host); // its manifest lists logs/connection_log_2456.txt
        string local = Path.Combine(_root, "local-journal");
        using var journal = RunJournal.UseLocalDirectory(local);

        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));

        Assert.Equal([RunId], Directory.GetDirectories(local).Select(Path.GetFileName));
        var copies = File.ReadAllLines(Path.Combine(local, RunId, WorldFixture.Actor + ".jsonl"));
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.CopyRetired],
            copies.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()));
        Assert.All(copies, line => Assert.Equal(RunId, JsonDocument.Parse(line).RootElement.GetProperty("run").GetString()));
        // After the run, the process's own journal run is the process's again.
        Assert.NotEqual(RunId, RunJournal.ThisProcess.RunId);

        Assert.True(File.Exists(Path.Combine(logs, "connection_log_2456.txt")));
        Assert.DoesNotContain("logs/", File.ReadAllText(Path.Combine(Output, "runtime-changes", "changes.json")));

        // Steam writes more there between runs: the install still matches the plan, which never counts logs/.
        File.WriteAllText(Path.Combine(logs, "stats_log.txt"), "Steam's own, from today");
        if (Directory.Exists(host.Local(RunDirectory))) Directory.Delete(host.Local(RunDirectory), true); // the same fixed run id again
        string again = Path.Combine(_root, "again");
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, again], Options(host, server)));
    }

    // The remote launch is the plan's launch arguments: a crossplay plan's server starts with -crossplay, any other without it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACrossplayPlansServerStartsOnTheHostWithCrossplay(bool crossplay)
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host, crossplay: crossplay);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        var arguments = FakeServerHost.Spec(Assert.Single(host.Runs, run => run.Script == "start").Variables["spec"])
            .Where(line => line.Kind == "arg").Select(line => line.Text).ToList();
        if (crossplay) Assert.Equal("-crossplay", arguments.Last()); else Assert.DoesNotContain("-crossplay", arguments);
        Assert.Equal(crossplay ? "true" : "false", Result().GetProperty("Provenance").GetProperty("crossplay").GetString());
    }

    [Fact] public async Task ACrossplayRunWhoseServerHadToBeKilledFailsAndSaysWhy()
    {
        var server = NewServer(); var host = NewHost(server);
        host.IgnoreQuit = true;
        var (plan, profile) = Write(host, crossplay: true);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        var step = Step("every boot quit cleanly and retired its crossplay lobby");
        Assert.False(step.GetProperty("Passed").GetBoolean());
        Assert.Contains("boot-1 was killed", step.GetProperty("Error").GetString());
        Assert.Contains("not a clean crossplay run", step.GetProperty("Error").GetString());
        Assert.StartsWith("boot-1 killed", Result().GetProperty("Provenance").GetProperty("serverStops").GetString());
        Assert.Equal("120", Assert.Single(host.Runs, run => run.Script == "stop").Variables["quit"]);
    }

    [Fact] public async Task ACleanCrossplayRunRecordsEachBootsRetiredLobby()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host, crossplay: true);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.True(Step("every boot quit cleanly and retired its crossplay lobby").GetProperty("Passed").GetBoolean());
        var provenance = Result().GetProperty("Provenance");
        Assert.StartsWith("boot-1 clean", provenance.GetProperty("serverStops").GetString());
        Assert.Contains("lobby lobby-1; retired yes; PlayFab confirmation logged", provenance.GetProperty("crossplayLobbies").GetString());
    }

    // A crossplay plan checks that the host's runtime copy can load libparty.so before anything starts, in validate too; a host
    // missing libpulse fails there with the packages to install, and no server starts. A plan without crossplay is not checked.
    [Theory]
    [InlineData("run")]
    [InlineData("validate")]
    public async Task ACrossplayHostMissingLibrariesIsRefusedBeforeAnythingStarts(string mode)
    {
        var server = NewServer(); var host = NewHost(server);
        host.PartyReply = "VT-LDD \tlibpulse.so.0 => not found\nVT-LDD \tlibc.so.6 => /lib/libc.so.6 (0x1)\nVT-PARTY checked valheim_server_Data/Plugins/libparty.so 1\n";
        var (plan, profile) = Write(host, crossplay: true);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), [mode, plan, Output], Options(host, server)));
        var step = Step("the server host can load crossplay's libraries");
        Assert.False(step.GetProperty("Passed").GetBoolean());
        Assert.Contains("libpulse.so.0 (package libpulse0) is missing", step.GetProperty("Error").GetString());
        // libparty.so is the game's: read where the profile's game runs from, the install itself on Linux.
        Assert.Equal(Install, Assert.Single(host.Runs, run => run.Script == "party").Variables["runtime"]);
        Assert.DoesNotContain(host.Runs, run => run.Script == "start");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyACrossplayPlanChecksTheLibrariesAndRecordsWhichLoaded(bool crossplay)
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host, crossplay: crossplay);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        var provenance = Result().GetProperty("Provenance");
        if (crossplay)
        {
            Assert.True(Step("the server host can load crossplay's libraries").GetProperty("Passed").GetBoolean());
            Assert.Equal("valheim_server_Data/Plugins/libparty.so loads on linux-box", provenance.GetProperty("crossplayLibraries").GetString());
            Assert.Equal("1", Assert.Single(host.Runs, run => run.Script == "start").Variables["crossplay"]);
        }
        else
        {
            Assert.DoesNotContain("the server host can load crossplay's libraries", StepNames());
            Assert.False(provenance.TryGetProperty("crossplayLibraries", out _));
            Assert.DoesNotContain(host.Runs, run => run.Script == "party");
        }
    }

    // The game reads its arguments lowercased: -Port names the game port, and one that differs from the profile's is refused.
    [Fact] public async Task AGamePortInAnyCaseMustBeTheProfilesGamePort()
    {
        var host = NewHost();
        var (plan, profile) = Write(host, portOption: "-Port", gamePort: "2457");
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, null)));
        Assert.False(Directory.Exists(Output)); Assert.Empty(host.Runs);
    }

    [Fact] public async Task ValidateOnAHostStagesTheProfileThereAndStartsNothing()
    {
        var host = NewHost();
        var (plan, profile) = Write(host);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["validate", plan, Output], Options(host, null)));
        Assert.Contains("prepared only; no game launched", StepNames());
        Assert.DoesNotContain(host.Runs, run => run.Script is "port" or "tunnel" or "start" or "fetch");
        Assert.Equal(host.Claims, host.Releases); Assert.Single(host.Claims);
        // Nothing was launched, so the profile just goes (#194), with nothing to keep or fetch.
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.EndsWith("nothing was launched", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
    }

    // #730: the plan is checked against the install it names before anything is staged: a file that differs from the runtime
    // manifest, or (with a manifest made from the changed install) a loader that is not the plan's runtimePins, is refused there.
    // Nothing is journalled or staged, and the lock is released.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInstallThatIsNotThePinnedRuntimeIsRefusedBeforeItsProfileIsStaged(bool manifestFollows)
    {
        var host = NewHost(NewServer());
        var (plan, profile) = Write(host);
        File.WriteAllText(Path.Combine(host.Local(Install), "BepInEx", "core", "BepInEx.dll"), "another bepinex");
        if (manifestFollows)
        {
            // The manifest regenerated after the change accepts it; runtimePins, reviewed separately, do not.
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(plan))!;
            document["runtime"]!["sha256"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(WorldFixture.Manifest(host.Local(Install))));
            File.WriteAllText(plan, document.ToJsonString());
        }
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, NewServer())));
        var stage = Step(StageStep);
        Assert.False(stage.GetProperty("Passed").GetBoolean());
        Assert.Contains(manifestFollows ? "loader" : "different hash: BepInEx/core/BepInEx.dll", stage.GetProperty("Error").GetString());
        Assert.DoesNotContain(host.Runs, run => run.Script is "profile-seed" or "apply-stage" or "ship" or "start" or "tunnel");
        Assert.Equal(-1, JournalIndex(host, JournalEntry.CopyIntended));
        Assert.Equal(host.Claims, host.Releases);
    }

    [Fact] public async Task AnotherRunsLockRefusesAfterOnlyTheExactClaimIntentAndAtomicClaimAttempt()
    {
        var host = NewHost(NewServer()); host.HeldBy = "other-runner run-x [0123]";
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, NewServer())));
        var step = Step("take the server host's lock");
        Assert.False(step.GetProperty("Passed").GetBoolean()); Assert.Contains("other-runner run-x [0123]", step.GetProperty("Error").GetString());
        Assert.Equal(["journal", "lock", "journal"], host.Scripts);
        var journal = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.Equal([JournalEntry.LockIntended, JournalEntry.RunEnded], journal.Where(record => record.Actor == "run").Select(record => record.Entry.Kind));
        Assert.DoesNotContain(journal, record => record.Entry.Kind == JournalEntry.LockHeld);
        Assert.Empty(host.Releases);
        Assert.DoesNotContain("release the server host's lock", StepNames());
    }

    [Fact]
    public async Task CancellationAfterTheAtomicClaimStillJournalsTheHeldLockAndReturnsItsHandle()
    {
        var host = NewHost();
        using var cancellation = new CancellationTokenSource();
        host.BeforeScript = script => { if (script == "lock") cancellation.Cancel(); };
        var journal = new RunJournal("run-cancelled-lock-note");

        var held = await JournaledHostLock.AcquireAsync(host, "/var/tmp/vt/lock", "test owner", journal,
            "/var/tmp/vt/journal", "run", TimeSpan.FromSeconds(5), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        host.BeforeScript = null;
        await held.ReleaseAsync(CancellationToken.None);

        var records = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", journal.RunId, TimeSpan.FromSeconds(5));
        Assert.Equal([JournalEntry.LockIntended, JournalEntry.LockHeld, JournalEntry.LockReleased],
            records.Select(record => record.Entry.Kind));
        Assert.Equal(host.Claims, host.Releases);
    }

    [Fact]
    public async Task AFailedReleaseNoteWarnsAfterTheLockIsAlreadyFree()
    {
        var host = NewHost();
        var journal = new RunJournal("run-release-note-failure");
        var held = await JournaledHostLock.AcquireAsync(host, "/var/tmp/vt/lock", "test owner", journal,
            "/var/tmp/vt/journal", "run", TimeSpan.FromSeconds(5));
        host.JournalFailsFor = JournalEntry.LockReleased;

        var released = await held.ReleaseAsync();

        Assert.Equal(HostLockState.Released, released.State);
        Assert.Equal(host.Claims, host.Releases);
        var records = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", journal.RunId, TimeSpan.FromSeconds(5));
        Assert.Equal([JournalEntry.LockIntended, JournalEntry.LockHeld], records.Select(record => record.Entry.Kind));
    }

    [Fact]
    public async Task DisposeRetriesAJournalledLockWhoseFirstReleaseWasNotProven()
    {
        var host = NewHost();
        var journal = new RunJournal("run-release-retry");
        var held = await JournaledHostLock.AcquireAsync(host, "/var/tmp/vt/lock", "test owner", journal,
            "/var/tmp/vt/journal", "run", TimeSpan.FromSeconds(5));
        host.ReleaseLockResults.Enqueue(new HostLockResult(HostLockState.Unknown, null, "release reply was lost"));

        var error = await Assert.ThrowsAsync<HostLockException>(() => held.ReleaseAsync());
        Assert.Equal(HostLockState.Unknown, error.State);
        await held.DisposeAsync();

        Assert.Equal([held.Owner], host.Releases);
        var records = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", journal.RunId, TimeSpan.FromSeconds(5));
        Assert.Equal([JournalEntry.LockIntended, JournalEntry.LockHeld, JournalEntry.LockReleased],
            records.Select(record => record.Entry.Kind));
    }

    // #257 step 5: cleanup never runs on the cancelled token. A Ctrl+C during the scenario still lets the copy be retired and
    // the run's end be journalled; a second Ctrl+C while a retire hangs abandons the cleanup: exit 3, journalled, recoverable.
    [Fact] public async Task CleanupRunsOnItsOwnTokenAndASecondInterruptAbandonsIt()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        using (var once = new RunCancellation())
        {
            await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server,
                _ => { once.SignalCancel(); return Task.CompletedTask; }, cancellation: once));
            Assert.True(once.Token.IsCancellationRequested);
            Assert.Null(once.Abandoned);
            Assert.Contains("retire", host.Scripts);
            Assert.Contains(await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5)), record => record.Entry.Kind == JournalEntry.RunEnded);
        }

        var server2 = NewServer(); var host2 = new FakeServerHost("linux-box", Path.Combine(_root, "mirror-2"), server2);
        var (plan2, profile2) = Write(host2);
        using var twice = new RunCancellation();
        host2.Hang.Add("retire");
        host2.BeforeScript = name => { if (name == "retire") { twice.SignalCancel(); twice.SignalCancel(); } };
        string output2 = Path.Combine(_root, "output-2");
        Assert.Equal(3, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile2), ["run", plan2, output2], Options(host2, server2, cancellation: twice)));
        Assert.Contains("second interrupt", twice.Abandoned);
        var journal = await RunJournalOnHost.ReadAsync(host2, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.Contains("second interrupt", Assert.Single(journal, record => record.Entry.Kind == JournalEntry.CleanupAbandoned).Entry.Fields["reason"]);
        Assert.DoesNotContain(journal, record => record.Entry.Kind == JournalEntry.RunEnded);
        // env status names it (this test is the runner, so the run reads as going until the process ends, then recoverable).
        var status = Assert.Single((await RunJournalStatus.InspectAsync(new Dictionary<string, HostProfile> { ["linux-box"] = new() { Kind = "ssh", Lock = "/var/tmp/vt/lock" } },
            _ => host2, TimeSpan.FromSeconds(5))).Runs, run => run.Run == RunId);
        Assert.Contains("its cleanup was abandoned (a second interrupt during cleanup)", status.Reason);
        Assert.NotEqual(JournalRunState.Ended, status.State);
    }

    // A boot whose journal entry cannot be written is never started: no process runs that the journal does not name.
    [Fact] public async Task AServerBootThatCannotBeJournalledIsNotStarted()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        host.JournalFailsFor = JournalEntry.ProcessIntended;
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.DoesNotContain("start", host.Scripts);
        Assert.Contains("Journalling process-intended", Step("start and verify owned dedicated fixture").GetProperty("Error").GetString());
        Assert.Equal(host.Claims.Count, host.Releases.Count);
    }

    [Fact] public async Task ALostStartReplyIsAnUnknownOutcomeAndKeepsTheLock()
    {
        var server = NewServer(); var host = NewHost(server);
        host.Failures["start"] = FakeServerHost.TransportFailure;
        var (plan, profile) = Write(host);
        Assert.Equal(3, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        var result = Result();
        Assert.False(result.GetProperty("Passed").GetBoolean());
        Assert.StartsWith("unknown: ", result.GetProperty("Provenance").GetProperty("outcome").GetString());
        // A server may be running there that no session knows: nothing is stopped by guesswork, and the host stays locked.
        Assert.Empty(host.Stops);
        Assert.Empty(host.Releases);
        Assert.Contains("may still run", Step("release the server host's lock").GetProperty("Error").GetString());
        Assert.True(Assert.Single(host.Tunnels).Stopped);
        // ...and the copy it may run from stays, named, never removed (#257).
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.DoesNotContain("retire", host.Scripts);
        Assert.Contains("may still run", result.GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
    }

    [Fact] public async Task AnUnprovenStopIsAnUnknownOutcomeAndKeepsTheLock()
    {
        var server = NewServer(); var host = NewHost(server);
        host.Failures["stop"] = new HostResult(HostOutcome.Unknown, null, "", "", TimeSpan.FromSeconds(45), true);
        var (plan, profile) = Write(host);
        Assert.Equal(3, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.False(Step("stop only owned server").GetProperty("Passed").GetBoolean());
        Assert.Empty(host.Releases);
        Assert.DoesNotContain("fetch the server host's world copy", StepNames());
        Assert.StartsWith("unknown: ", Result().GetProperty("Provenance").GetProperty("outcome").GetString());
    }

    [Fact] public async Task AFailingScenarioIsAFailureEvenWhenTeardownIsUnknown()
    {
        var server = NewServer(); var host = NewHost(server);
        host.Failures["stop"] = FakeServerHost.TransportFailure;
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, _ => throw new InvalidOperationException("marker missing"))));
        Assert.False(Result().GetProperty("Provenance").TryGetProperty("outcome", out _));
    }

    [Fact] public async Task ATunnelThatCannotOpenFailsTheRunBeforeTheServerStarts()
    {
        var server = NewServer(); var host = NewHost(server);
        host.TunnelFailure = new WaitFailedException("the forward to listen on 127.0.0.1:15577", "ssh exited with code 255", TimeSpan.FromSeconds(1), "bind [127.0.0.1]:15577: Address already in use");
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.False(Step("open the loopback CLI tunnel to the server host").GetProperty("Passed").GetBoolean());
        Assert.DoesNotContain(host.Runs, run => run.Script == "start");
        Assert.Empty(server.Events);
        Assert.Equal(host.Claims, host.Releases);
    }

    [Fact] public async Task ABusyCliPortOnTheHostIsRefusedBeforeTheTunnel()
    {
        var host = NewHost(NewServer()); host.PortBusy = true;
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, NewServer())));
        Assert.Contains("already listens on port 5577", Step("CLI port is free on the server host").GetProperty("Error").GetString());
        Assert.DoesNotContain(host.Runs, run => run.Script is "tunnel" or "start");
    }

    [Fact] public async Task AProfileThatDoesNotFitThePlanIsRefusedBeforeAnythingIsWritten()
    {
        var host = NewHost();
        var (plan, profile) = Write(host, planPort: 5590);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, null)));
        Assert.False(Directory.Exists(Output)); Assert.Empty(host.Runs);
    }

    // --inventory: a standalone run places its one actor, the dedicated server, on the first server environment in inventory
    // order that can run the plan, records why, and refuses before anything is written when none can.
    [Fact] public async Task AnInventoryPlacesTheServerOnTheFirstEnvironmentThatFitsThePlan()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, _) = Write(host);
        string inventory = Path.Combine(_root, "environments.json");
        object Environment(string name, int cliPort) => new { name, host = "linux-box", roles = new[] { "server" }, install = Install, runtime = Runs, cliPort, gamePort = 2456 };
        void Inventory(params object[] environments) => File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object> { ["linux-box"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-box.example", @lock = "/var/tmp/vt/lock" } },
            environments,
        }));
        // Skipped in order: a Windows host for this Linux runtime, a local host of another platform than this machine's, and a CLI
        // port that is not the plan's.
        string other = HostProfile.CurrentPlatform == "linux" ? "windows" : "linux";
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object>
            {
                ["linux-box"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-box.example", @lock = "/var/tmp/vt/lock" },
                ["windows-box"] = new { kind = "ssh", platform = "windows", shell = "pwsh", destination = "tester@windows-box.example", @lock = @"C:\vt\lock" },
                ["elsewhere"] = new { kind = "local", platform = other, shell = other == "windows" ? "pwsh" : "bash", @lock = other == "windows" ? @"C:\vt\lock" : "/var/tmp/vt/lock" },
            },
            environments = new object[]
            {
                new { name = "windows", host = "windows-box", roles = new[] { "server" }, install = @"C:\valheim\server", runtime = @"C:\vt\runs", cliPort = 5577, gamePort = 2456 },
                new { name = "local-other", host = "elsewhere", roles = new[] { "server" }, install = other == "windows" ? @"C:\valheim\server" : Install,
                      runtime = other == "windows" ? @"C:\vt\runs" : Runs, cliPort = 5577, gamePort = 2456 },
                Environment("other-port", 5590), Environment("fits", 5577),
            },
        }));
        Assert.Equal(0, await PinnedServerRun.MainAsync(["--inventory", inventory, "run", plan, Output], Options(host, server)));
        var provenance = Result().GetProperty("Provenance");
        string placed = provenance.GetProperty("serverEnvironment").GetString()!;
        Assert.StartsWith("fits: first server recipe that can run the plan after windows: the plan's runtime is a linux server, but the host is windows.", placed);
        Assert.Contains($"local-other: it is a local {other} host, but this machine is {HostProfile.CurrentPlatform}.", placed);
        Assert.Contains("other-port: the plan's ValheimCLI port 5577 is not its cliPort 5590", placed);
        Assert.Equal(FileHash.Sha256(inventory), provenance.GetProperty("inventorySha256").GetString());
        Assert.Contains("start", host.Scripts);

        Inventory(Environment("other-port", 5590));
        string refused = Path.Combine(_root, "refused");
        int scripts = host.Runs.Count;
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--inventory", inventory, "run", plan, refused], Options(host, server)));
        Assert.False(Directory.Exists(refused)); Assert.Equal(scripts, host.Runs.Count);
    }

    [Fact]
    public void PlacementSkipsAnExplicitWindowsRuntimeOnAnotherVolumeForTheNextEnvironment()
    {
        string inventoryPath = Path.Combine(_root, "windows-placement.json");
        File.WriteAllText(inventoryPath, JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object>
            {
                ["windows-box"] = new { kind = "ssh", platform = "windows", shell = "pwsh",
                    destination = "tester@windows-box.example", @lock = @"C:\vt\lock" },
            },
            environments = new object[]
            {
                new { name = "srv-a", host = "windows-box", roles = new[] { "server" }, install = @"C:\Steam\Valheim server",
                    runtime = @"D:\vt\runs", cliPort = 5577, gamePort = 2456 },
                new { name = "srv-b", host = "windows-box", roles = new[] { "server" }, install = @"C:\Steam\Valheim server",
                    runtime = @"C:\vt\runs", cliPort = 5577, gamePort = 2456 },
            },
        }));
        var inventory = EnvironmentInventory.Read(inventoryPath, new FakeMachine("windows"));
        var plan = new ServerRunPlan
        {
            Executable = GameLaunch.ServerWindowsExecutable,
            Port = 5577,
            Arguments = ["-port", "2456"],
        };

        var (_, assignment, _) = inventory.PlaceServer(plan);

        Assert.Equal("srv-b", assignment.Environment);
        Assert.Contains(@"srv-a: the explicit inventory runtime folder D:\vt\runs is on D:\, not the source install's volume C:\",
            assignment.Reason);
    }

    // A Linux loader package (the pinned pack's shape) beside its manifest; returns the manifest's path.
    private string LinuxLoaderPackage(string name)
    {
        string root = Path.Combine(_root, name);
        foreach (string relative in new[] { "BepInEx/core/BepInEx.dll", "BepInEx/core/BepInEx.Preloader.dll", "doorstop_libs/libdoorstop_x64.so" })
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name + " " + relative);
        }
        string manifest = Path.Combine(_root, name + ".json");
        BepInExLoaderPackage.Capture(root, "BepInExPack_Valheim", "5.4.2333").Write(manifest);
        return manifest;
    }

    // #730: a standalone server's plan is checked against its install and staged plugins, then bound to
    // the profile staged from it: the runtime manifest to the profile's game listing, runtimePins to the profile's (on the pinned
    // loader), the ValheimCLI pin to the staged set's. The install is never changed; its own ValheimCLI build is left out.
    [Fact] public async Task AStandalonePlanIsCheckedAgainstItsInstallThenBoundToItsProfile()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host);
        Directory.CreateDirectory(Path.Combine(host.Local(Install), "BepInEx", "plugins"));
        string installCli = Path.Combine(host.Local(Install), "BepInEx", "plugins", "valheimCLI.dll");
        File.WriteAllText(installCli, "the install's own ValheimCLI");
        string mod = Path.Combine(host.Local(Install), "BepInEx", "plugins", "Mod.dll");
        File.WriteAllBytes(mod, RegressionRig.Assembly("ExampleMod", new("example.mod")));
        var plan = ServerRunPlan.Read<ServerRunPlan>(planPath);
        plan.Runtime.Sha256 = new(WorldFixture.Manifest(host.Local(Install)));
        var installPins = plan.RuntimePins = InstallPins.Of(host.Local(Install));
        string sourceCliPin = plan.Pins["valheimCLI.valheimCLI"] = FileHash.Md5(installCli);
        plan.Pins["example.mod"] = FileHash.Md5(mod);
        var before = WorldFixture.Manifest(host.Local(Install));
        string package = LinuxLoaderPackage("pinned-pack");
        var hosted = HostedServerRun.Create(TestEnvironment.Read(profilePath), plan, "test",
            new FakeRunHooks { Host = _ => host, RunId = RunId, PinnedLoader = (_, _, platform) => platform == "linux" ? new ShippedLoader.Choice(package, "the pinned test pack") : null }, prepared: false);
        var report = new ScenarioReport("bind");
        await hosted.LockAndPrepareRuntimeAsync(report, plan, CancellationToken.None);

        Assert.Equal((Install, RunDirectory + "/runtime"), (hosted.GameDirectory, hosted.LoaderDirectory));
        string profile = host.Local(RunDirectory + "/runtime");
        Assert.Equal(BepInExLoaderPackage.Read(package).Loader, plan.RuntimePins.Loader);
        Assert.NotEqual(installPins.Loader, plan.RuntimePins.Loader);
        Assert.Equal(installPins.Game, plan.RuntimePins.Game);
        Assert.Equal((Install, before.Count), (plan.Runtime.Source, plan.Runtime.Sha256.Count)); // the profile's game is the install, on Linux
        Assert.Equal(TestCliSet.CorePin, plan.Pins["valheimCLI.valheimCLI"]);
        Assert.Equal(sourceCliPin, hosted.SourceCliPins["valheimCLI.valheimCLI"]);
        Assert.Equal("observed", hosted.PinOrigins["plugins.valheimCLI.valheimCLI"]);
        Assert.Equal(FileHash.Md5(mod), plan.Pins["example.mod"]);
        Assert.Equal(File.ReadAllBytes(Path.Combine(TestCliSet.Source.Files, "valheimCLI.dll")), File.ReadAllBytes(Path.Combine(profile, "BepInEx", "plugins", "valheimCLI.dll")));
        Assert.Equal(File.ReadAllBytes(mod), File.ReadAllBytes(Path.Combine(profile, "BepInEx", "plugins", "Mod.dll")));
        Assert.Equal("pinned-pack doorstop_libs/libdoorstop_x64.so", File.ReadAllText(Path.Combine(profile, "doorstop_libs", "libdoorstop_x64.so")));
        Assert.Equal("the pinned test pack", report.Provenance["serverLoaderShipped"]);
        Assert.Equal(before, WorldFixture.Manifest(host.Local(Install)));
        hosted.CheckRuntime(report, plan);
        Assert.All(report.Steps, step => Assert.True(step.Passed, step.Name + ": " + step.Error));
    }

    // #730: an inventory recipe's reviewed loader package is the standalone server's loader (a campaign applies it the same way),
    // never skipped as before; the run records it and the profile's loader pin is the package's.
    [Fact] public async Task AnInventoryRecipesLoaderPackageIsTheStandaloneServersLoader()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, _) = Write(host);
        string package = LinuxLoaderPackage("recipe-pack");
        string inventory = Path.Combine(_root, "environments.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object> { ["linux-box"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-box.example", @lock = "/var/tmp/vt/lock" } },
            environments = new object[] { new { name = "packaged", host = "linux-box", roles = new[] { "server" }, install = Install, runtime = Runs, cliPort = 5577, gamePort = 2456, loaderPackage = package } },
        }));
        Assert.Equal(0, await PinnedServerRun.MainAsync(["--inventory", inventory, "run", plan, Output], Options(host, server)));
        var provenance = Result().GetProperty("Provenance");
        Assert.StartsWith("packaged: first server recipe", provenance.GetProperty("serverEnvironment").GetString());
        Assert.Equal(BepInExLoaderPackage.Read(package).Identity, provenance.GetProperty("serverLoaderPackage").GetString());
        Assert.Equal(BepInExLoaderPackage.Read(package).Loader, provenance.GetProperty("runtimeLoaderSha256").GetString());
        Assert.Equal("observed", Result().GetProperty("PinOrigins").GetProperty("runtimePins").GetString());
        Assert.Equal("pins observed, not reviewed", provenance.GetProperty("pins").GetString());
    }

    [Fact] public async Task AHostedClientsFailedRestoreIntentAppendCannotChangeItsRecentFiles()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("mac-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        FakeInstalls.MacClient(clientHost.Local("/Users/tester/valheim"));
        string recent = clientHost.Local("/Users/tester/" + HostedWorld.MacSaveRelative + "/serverlist_local/recent");
        Directory.CreateDirectory(Path.GetDirectoryName(recent)!); File.WriteAllText(recent, "personal before");
        var (plan, profile) = Write(host, withClient: true);
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            using var session = run.OpenClient(PreparedClient(clientHost));
            clientHost.JournalFailsFor = JournalEntry.UserFilesRestoreIntended;
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport()));
        Assert.Equal(1, code);
        Assert.Equal("127.0.0.1:2456 joined by client 77\n", File.ReadAllText(recent));
        Assert.Equal("personal before", File.ReadAllText(clientHost.Local("/Users/tester/runs/" + RunId + "/" +
            UserSaveFiles.BackupName(RunId, "client-1") + "/serverlist_local/recent")));
        Assert.DoesNotContain(clientHost.Runs, step => step.Script == "user-files" && step.Variables["mode"] == "restore");
        var journal = await RunJournalOnHost.ReadAsync(clientHost, "/Users/tester/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(journal, record => record.Entry.Kind == JournalEntry.UserFilesRestored);
    }

    [Fact] public async Task AMacServersFailedRestoreIntentAppendCannotChangeItsAccessLists()
    {
        var host = NewHost(); var (planPath, profilePath) = Write(host);
        var plan = ServerRunPlan.Read<ServerRunPlan>(planPath);
        plan.Executable = GameLaunch.ServerMacExecutable; plan.Runtime.Sha256.Clear();
        var profile = TestEnvironment.Read(profilePath); profile.Hosts[profile.Server!.Host].Platform = "macos";
        string root = "/Users/tester/" + HostedWorld.MacSaveRelative;
        string admin = host.Local(root + "/adminlist.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(admin)!); File.WriteAllText(admin, "personal admins");
        var hosted = HostedServerRun.Create(profile, plan, "test", new FakeRunHooks { Host = _ => host, RunId = RunId }, prepared: true);
        var report = new ScenarioReport("mac-access-lists");
        // Deliberately stop preparation after capture/isolation: this fixture has no prepared source-check owner.
        await Assert.ThrowsAnyAsync<Exception>(() => hosted.LockAndPrepareRuntimeAsync(report, plan, CancellationToken.None));
        Assert.False(File.Exists(admin));
        File.WriteAllText(admin, "run admins"); host.JournalFailsFor = JournalEntry.UserFilesRestoreIntended;
        var failures = await hosted.TeardownAsync(report, Output, launched: false, serverStopped: true);
        Assert.NotEmpty(failures);
        Assert.Equal("run admins", File.ReadAllText(admin));
        Assert.DoesNotContain(host.Runs, step => step.Script == "user-files" && step.Variables["mode"] == "restore");
        var journal = await RunJournalOnHost.ReadAsync(host, RunJournal.DirectoryFor(profile.Hosts[profile.Server.Host]), RunId, TimeSpan.FromSeconds(5));
        Assert.Contains(journal, record => record.Entry.Kind == JournalEntry.UserFilesCaptured);
        Assert.DoesNotContain(journal, record => record.Entry.Kind == JournalEntry.UserFilesRestored);
    }

    [Fact] public async Task AProfileClientStartsInItsHostsDesktopSessionAndStopsOnlyThatClient()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("mac-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/Users/tester/valheim");
        FakeInstalls.MacClient(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, "BepInEx", "LogOutput.log"), "an earlier run's log\n");
        // The host user's own recent-servers list, which the client adds to whatever the launch asks (#822).
        string recent = clientHost.Local("/Users/tester/" + HostedWorld.MacSaveRelative + "/serverlist_local/recent");
        Directory.CreateDirectory(Path.GetDirectoryName(recent)!);
        File.WriteAllText(recent, "the user's servers\n");
        var (plan, profile) = Write(host, withClient: true);
        var clientTransport = new ScriptedTransport();
        var client = PreparedClient(clientHost);
        int? pid = null;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            // A scenario reaches a campaign client's host by its name.
            Assert.Equal(["player"], run.CampaignClients);
            Assert.Same(clientHost, run.GameSession.ClientHost("player"));
            using (var session = run.OpenClient(client)) pid = session.ProcessId;
            return Task.CompletedTask;
        }, clientHost, clientTransport));
        Assert.Equal(0, code);
        Assert.Equal(77, pid);
        // The user's list is as the run found it; the client's version is kept in the run's folder on its host.
        Assert.Equal("the user's servers\n", File.ReadAllText(recent));
        Assert.Equal("127.0.0.1:2456 joined by client 77\n", File.ReadAllText(clientHost.Local("/Users/tester/runs/" + RunId + "/" +
            UserSaveFiles.BackupName(RunId, "client-1") + "/after/serverlist_local/recent")));
        // The launch used the profile's install and a launch directory in this run's directory on the client's host.
        var start = Assert.Single(clientHost.Runs, run => run.Script == "client-start");
        Assert.Equal("/Users/tester/valheim", start.Variables["install"]);
        Assert.Equal("/Users/tester/runs/" + RunId + "/client-1", start.Variables["dir"]);
        Assert.Contains(FakeServerHost.Spec(start.Variables["spec"]), line => line.Kind == "arg" && line.Text == "+connect");
        // The client host's journal (#257): its lock held and released, the client journalled before its start, then its process.
        var clientJournal = await RunJournalOnHost.ReadAsync(clientHost, "/Users/tester/journal", RunId, TimeSpan.FromSeconds(5));
        // A Mac client's GUI agent is journalled around its process: intended before the start, retired once the client is stopped.
        // The host user's recent-servers list is captured before the start and restored once the client stopped (#822).
        Assert.Equal([JournalEntry.UserFilesCaptureIntended, JournalEntry.UserFilesCaptured, JournalEntry.ProcessIntended, JournalEntry.MacAgentIntended, JournalEntry.ProcessStarted,
            JournalEntry.MacAgentRetired, JournalEntry.UserFilesRestoreIntended, JournalEntry.UserFilesRestored],
            clientJournal.Where(record => record.Actor == "player").Select(record => record.Entry.Kind));
        Assert.Equal("77", clientJournal.Single(record => record.Entry.Kind == JournalEntry.ProcessStarted).Entry.Fields["pid"]);
        Assert.Equal(FakeServerHost.CommandLineSha256("77"), clientJournal.Single(record => record.Entry.Kind == JournalEntry.ProcessStarted).Entry.Fields["commandLineSha256"]);
        // The run ends in every journal it wrote (#667): the client host's too, not only the server's.
        Assert.Equal([JournalEntry.LockIntended, JournalEntry.LockHeld, JournalEntry.LockReleased, JournalEntry.RunEnded],
            clientJournal.Where(record => record.Actor == "run").Select(record => record.Entry.Kind));
        Assert.Equal("passed", clientJournal.Single(record => record.Entry.Kind == JournalEntry.RunEnded).Entry.Fields["state"]);
        Assert.True(JournalIndex(clientHost, JournalEntry.ProcessIntended) < clientHost.Runs.ToList().FindIndex(run => run.Script == "client-start"));
        // The earlier log moved aside first, the new one was awaited from its start; only that client was stopped.
        Assert.Equal("/Users/tester/runs/" + RunId + "/client-1.previous-LogOutput.log", Assert.Single(clientHost.Runs, run => run.Script == "move-aside").Variables["to"]);
        Assert.Equal(2, clientHost.Runs.Count(run => run.Script == "follow")); // fresh BepInEx line, then ValheimCLI listening
        Assert.All(clientHost.Runs.Where(run => run.Script == "follow"), run => Assert.Equal("0", run.Variables["offset"]));
        Assert.Equal(new[] { ("77", "555") }, clientHost.Stops);
        Assert.Contains("listening on 127.0.0.1:5578", File.ReadAllText(Path.Combine(Output, "player", "client-1", "game-0.log")));
        Assert.True(Assert.Single(clientHost.Tunnels).Stopped);
        // An inventory client runs in place. It gets the required narrow install-pin check, but no preparation or post-run
        // source listing: the game writes into this install by design, so the report records that verdict as not applicable.
        var sourceLists = clientHost.Runs.Where(run => run.Script == "list" && run.Variables["root"] == "/Users/tester/valheim").ToList();
        Assert.NotEmpty(sourceLists);
        Assert.All(sourceLists, run => Assert.NotEmpty(run.Variables["dirs"]));
        Assert.DoesNotContain("profile-seed", clientHost.Scripts);
        Assert.Equal("not applicable: runs in place", Result().GetProperty("Provenance").GetProperty("sourceIntegrity.player").GetString());
        Assert.Contains("verify player's source install after the run (not applicable: runs in place)", StepNames());
        // The client's host was locked for the run and released at teardown; its logs were scanned with the server's.
        Assert.Equal(clientHost.Claims, clientHost.Releases); Assert.Single(clientHost.Claims);
        Assert.Contains("release client host mac-gpu's lock", StepNames());
        Assert.Contains(Result().GetProperty("Logs").EnumerateArray(), log => log.GetProperty("Role").GetString() == "client-1 BepInEx log");
    }

    [Fact] public async Task RemoteClientPinsAreCheckedBeforeStartAndItsInPlaceSourceVerdictIsNotApplicable()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("mac-gpu", Path.Combine(_root, "gpu-integrity"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/Users/tester/valheim");
        FakeInstalls.MacClient(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        var strict = new ClientRunPlan
        {
            Mode = "owned", Architecture = "x64", Install = _root, Port = 5578, StartSeconds = 30,
            LaunchArguments = ["+connect", "linux-box:2456"], InstallPins = InstallPins.Of(clientInstall),
        };
        strict.InstallPins.Game = new string('0', 64);

        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            using var _ = run.OpenClient(strict);
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport())));
        Assert.DoesNotContain(clientHost.Runs, run => run.Script == "client-start");

        Directory.Delete(Output, recursive: true);
        if (Directory.Exists(host.Local(RunDirectory))) Directory.Delete(host.Local(RunDirectory), recursive: true);
        var inPlace = PreparedClient(clientHost);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            using var session = run.OpenClient(inPlace);
            File.WriteAllText(Path.Combine(clientInstall, "written-during-client.txt"), "source mutation");
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport())));
        Assert.Equal("not applicable: runs in place", Result().GetProperty("Provenance").GetProperty("sourceIntegrity.player").GetString());
        Assert.Contains(clientHost.Runs, run => run.Script == "client-start" &&
            FakeServerHost.Spec(run.Variables["spec"]).Any(line => line.Kind == "env" &&
                line.Text.Contains("/Users/tester/valheim/BepInEx/core", StringComparison.Ordinal)));
    }

    // #257, part 2: the client host's Steam logs "Logged In Elsewhere" as the client registers (steamdup2's lines): the start fails at
    // once with the decided message, not as a later exit or timeout. The same evening's earlier sign-out (steamdup1), already in the
    // log before the launch, is not this launch's: that client starts.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClientSteamSignsOutAsItStartsFailsAtOnceNamingTheOtherComputer(bool signedOut)
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("mac-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578) { SteamLog = "/Users/tester/Library/Application Support/Steam/logs/connection_log.txt" };
        string clientInstall = clientHost.Local("/Users/tester/valheim");
        FakeInstalls.MacClient(clientInstall);
        string steamLog = clientHost.Local(clientHost.SteamLog);
        Directory.CreateDirectory(Path.GetDirectoryName(steamLog)!);
        File.WriteAllText(steamLog, SteamAccountInUseTests.Healthy + SteamAccountInUseTests.SteamDup1 + SteamAccountInUseTests.Healthy);
        long before = new FileInfo(steamLog).Length;
        clientHost.BeforeScript = name => { if (name == "client-start") File.AppendAllText(steamLog, signedOut ? SteamAccountInUseTests.SteamDup2 : SteamAccountInUseTests.Healthy); };
        var (plan, profile) = Write(host, withClient: true);
        var client = PreparedClient(clientHost);
        Exception? failed = null;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            failed = Record.Exception(() => { using var session = run.OpenClient(client); });
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport()));
        Assert.Equal(0, code);
        // The log was marked before the start and followed from its length then.
        var runs = clientHost.Runs.ToList();
        Assert.True(runs.FindIndex(run => run.Script == "steam-log") is var mark and >= 0 && mark < runs.FindIndex(run => run.Script == "client-start"));
        Assert.Contains(runs, run => run.Script == "follow" && run.Variables["log"] == clientHost.SteamLog && run.Variables["offset"] == before.ToString());
        if (!signedOut) { Assert.Null(failed); return; }
        var error = Assert.IsType<SteamLoggedInElsewhereException>(failed);
        Assert.Equal(SteamSessionLog.Message(null, "mac-gpu"), error.Message);
        Assert.DoesNotContain("12345678", error.Message);
        Assert.Equal(new[] { ("77", "555") }, clientHost.Stops); // the started client was stopped
    }

    [Fact] public async Task ARemoteWindowsClientRefusesMixedLoaderAndInheritedStandingPinsBeforeLaunch()
    {
        var host = new FakeServerHost("windows-client", Path.Combine(_root, "remote-client"));
        const string install = "/client/valheim";
        string local = host.Local(install);
        Directory.CreateDirectory(Path.Combine(local, "BepInEx", "config"));
        FakeInstalls.Client(local);
        File.WriteAllText(Path.Combine(local, "winhttp.dll"), "MZ fake"); // a proxy that shows no Doorstop version
        File.WriteAllText(Path.Combine(local, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var unrecognised = await Assert.ThrowsAsync<DoorstopPairingException>(() => HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows,
            new ClientRunPlan { Mode = "owned" }, TimeSpan.FromSeconds(5), default));
        Assert.Contains("not a Doorstop proxy this check recognises", unrecognised.Message);
        File.WriteAllText(Path.Combine(local, "winhttp.dll"), "MZ target_assembly"); // Doorstop 4 signature
        File.WriteAllText(Path.Combine(local, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var plan = new ClientRunPlan { Mode = "owned", Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) } };
        var mismatch = await Assert.ThrowsAsync<DoorstopPairingException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(5), default));
        DoorstopMixPathsTests.AssertRefusal(mismatch, "client install on windows-client",
            "is Doorstop 4, which reads only [General] in doorstop_config.ini, but that file is written for Doorstop 3 ([UnityDoorstop])");
        Assert.DoesNotContain(host.Runs, run => run.Script == "client-start");

        File.WriteAllText(Path.Combine(local, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        string config = Path.Combine(local, "BepInEx", "config", OwnedClientPreflight.CliConfig);
        File.WriteAllText(config, "[Expectations]\nFile = C:\\Users\\Public\\station\\expect-client.txt\nStrict = true\n");
        var global = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(5), default));
        Assert.Contains("host-global standing file", global.Message);

        File.WriteAllText(config, "[Expectations]\nFile = expect.txt\nStrict = true\n");
        string standing = Path.Combine(local, "BepInEx", "config", "expect.txt");
        File.WriteAllText(standing, $"valheimCLI.valheimCLI={new string('a', 32)}\ncom.bepis.bepinex.scriptengine=any\nworld=any\n");
        var unrelated = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(5), default));
        Assert.Contains("plugin the plan does not pin", unrelated.Message);
        File.WriteAllText(standing, $"valheimCLI.valheimCLI={new string('a', 32)}\nworld=any\n");
        await HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(5), default);
    }

    [Fact] public async Task WindowsPwshReadsTheRemoteLoaderFilesForPreflight()
    {
        if (!OperatingSystem.IsWindows()) return;
        string install = Path.Combine(_root, "windows-client");
        Directory.CreateDirectory(install);
        FakeInstalls.Client(install); // The preloader and core, found by the real existence script.
        File.WriteAllText(Path.Combine(install, "winhttp.dll"), "MZ target_assembly");
        string config = Path.Combine(install, "doorstop_config.ini");
        File.WriteAllText(config, "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var host = new LocalGameHost("windows-client", HostShell.Pwsh);
        var plan = new ClientRunPlan { Mode = "owned" };
        await HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(30), default);
        File.WriteAllText(config, "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var error = await Assert.ThrowsAsync<DoorstopPairingException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(30), default));
        Assert.Contains("Doorstop 4", error.Message);
    }

    // A client that started but never reached its menu (here its pins do not hold) is stopped; its fetched logs are still scanned.
    [Fact] public async Task AProfileClientWhoseStartupFailsStillHasItsLogsScannedAndListed()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("mac-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/Users/tester/valheim");
        FakeInstalls.MacClient(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        // Strict, so the menu pins are checked; the scripted client does not hold them.
        var client = new ClientRunPlan
        {
            Mode = "owned", Architecture = "x64", Install = _root, Port = 5578, StartSeconds = 30, Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) }, InstallPins = InstallPins.Of(clientInstall),
        };
        Exception? failed = null;
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            failed = Record.Exception(() => run.OpenClient(client));
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport { PinsHold = false })));
        Assert.IsType<InvalidOperationException>(failed);
        Assert.Single(clientHost.Stops);
        Assert.Contains("listening on 127.0.0.1:5578", File.ReadAllText(Path.Combine(Output, "player", "client-1", "game-0.log")));
        // Unity's log is the launch's own -logFile in the run's launch folder, kept from there, never the host user's Player.log (#822).
        Assert.StartsWith("Unity player log of client ", File.ReadAllText(Path.Combine(Output, "player", "client-1", "game-1.log")));
        var keep = Assert.Single(clientHost.Runs, run => run.Script == "client-keep").Variables;
        Assert.Equal(HostPath.Join(keep["dir"], GameLaunch.UnityLogName), keep["unitylog"]);
        var roles = Result().GetProperty("Logs").EnumerateArray().Select(log => log.GetProperty("Role").GetString()).ToList();
        Assert.Contains("client-1 BepInEx log", roles);
        Assert.Contains("client-1 Player.log", roles);
    }

    [Fact] public async Task AProfileClientWithoutAFreshBepInExLogFailsAtTheLoaderDeadline()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("mac-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578) { ClientWritesBepInExLog = false };
        string clientInstall = clientHost.Local("/Users/tester/valheim");
        FakeInstalls.MacClient(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        var client = PreparedClient(clientHost, startSeconds: 300, bepInExSeconds: 30, connect: false);
        Exception? failure = null;
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            failure = Record.Exception(() => run.OpenClient(client));
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport())));
        Assert.Contains("BepInEx wrote no fresh log line", failure?.ToString());
        Assert.Contains("within 30s", failure?.ToString());
        Assert.Contains("check winhttp.dll, doorstop_config.ini and BepInEx/core", failure?.ToString()); // no preloader log: the guess stays
        Assert.Single(clientHost.Stops);
        Assert.DoesNotContain(clientHost.Runs, run => run.Script == "follow" && run.Variables["offset"] != "0");
    }

    // #254: a preloader crash log the launch wrote explains the missing BepInEx log; an older one is named as not this launch's.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APreloaderCrashLogExplainsAClientThatNeverLoggedAndAnOlderOneIsNamedAsStale(bool fresh)
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("mac-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578) { ClientWritesBepInExLog = false };
        string clientInstall = clientHost.Local("/Users/tester/valheim");
        FakeInstalls.MacClient(clientInstall);
        static string B(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        clientHost.PreloaderReply = (fresh ? $"VT-PRELOADER fresh {B("preloader_20261005_190000.log")} {B("[Fatal  :   BepInEx] Could not find BepInEx.Preloader.Core")}\n" : "") +
            $"VT-PRELOADER stale {B("preloader_20260101_000000.log")} -\nVT-PRELOADER-END\n";
        var (plan, profile) = Write(host, withClient: true);
        var client = PreparedClient(clientHost, startSeconds: 300, bepInExSeconds: 30, connect: false);
        Exception? failure = null;
        await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            failure = Record.Exception(() => run.OpenClient(client));
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport()));
        string message = failure?.ToString() ?? "";
        Assert.Contains("Older preloader logs beside the game (preloader_20260101_000000.log) predate this launch and are not its.", message);
        if (fresh)
        {
            Assert.Contains("BepInEx's preloader failed: [Fatal  :   BepInEx] Could not find BepInEx.Preloader.Core (from preloader_20261005_190000.log", message);
            Assert.DoesNotContain("check winhttp.dll", message);
        }
        else Assert.Contains("check winhttp.dll, doorstop_config.ini and BepInEx/core", message);
    }

    // Linux hosts run the dedicated server only (#708): a client the profile puts on one is refused when the profile is read (#740).
    [Fact] public void AProfileClientOnALinuxHostIsRefusedWhenTheProfileIsRead()
    {
        var (_, profile) = Write(NewHost(NewServer()), withClient: true, clientPlatform: "linux");
        Assert.Contains("The client player: Linux hosts run the dedicated server only; start the client on Windows or macOS.",
            Assert.Throws<ArgumentException>(() => TestEnvironment.Read(profile)).Message);
    }
}
