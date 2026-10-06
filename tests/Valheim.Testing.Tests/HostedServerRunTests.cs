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
    [Fact] public void WindowsPowerShellDedicatedServerProfileIsAcceptedBeforeAnyHostOperation()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host, hostPlatform: "windows", hostShell: "powershell");
        var profile = TestEnvironment.Read(profilePath);
        var hosted = HostedServerRun.Create(profile, ServerRunPlan.Read<ServerRunPlan>(planPath), "test", new FakeRunHooks { Host = _ => host, RunId = RunId });
        Assert.Equal("windows", hosted.HostProfile.Platform);
        Assert.Empty(host.Runs);
    }

    // #257: a campaign's prepared install is the server's runtime, verified where it is and never copied again (the campaign
    // test proves the passing path); one that changed between preparation and the run is refused before anything launches.
    [Fact] public async Task APreparedRuntimeThatChangedSincePreparationIsRefusedAndNotCopied()
    {
        var host = NewHost();
        var (planPath, profilePath) = Write(host);
        var plan = ServerRunPlan.Read<ServerRunPlan>(planPath);
        var hosted = HostedServerRun.Create(TestEnvironment.Read(profilePath), plan, "test", new FakeRunHooks { Host = _ => host, RunId = RunId }, prepared: true);
        Assert.True(hosted.Prepared);
        Assert.Equal(Install, hosted.RuntimeDirectory);
        Assert.Equal(RunDirectory, hosted.RunDirectory); // world and boot evidence stay in the run's own directory
        File.AppendAllText(Path.Combine(host.Local(Install), GameLaunch.ServerLinuxExecutable), " changed after preparation");
        var report = new ScenarioReport("prepared runtime");
        await Assert.ThrowsAnyAsync<Exception>(() => hosted.LockAndCopyRuntimeAsync(report, plan, pinned: true, CancellationToken.None));
        var step = Assert.Single(report.Steps, step => step.Name == "verify the prepared runtime on the server host");
        Assert.False(step.Passed);
        Assert.Contains(GameLaunch.ServerLinuxExecutable, step.Error);
        Assert.DoesNotContain("copy", host.Scripts);
        Assert.DoesNotContain("start", host.Scripts);
    }

    [Fact] public async Task WindowsPowerShellProfileRunsTheWholeServerLifecycleAndKeepsOnlyItsEvidence()
    {
        const string windowsRuns = @"C:\vt\runs";
        var server = new FakeOwnedServer("test.mod", saveRoot: windowsRuns + @"\run-test\world");
        var host = new FakeServerHost("windows-server", Mirror, server, windows: true);
        var (plan, profile) = Write(host, hostPlatform: "windows", hostShell: "powershell");
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.Equal(host.Claims, host.Releases);
        Assert.Contains("start", host.Scripts);
        Assert.Contains("stop", host.Scripts);
        Assert.Contains("keep", host.Scripts);
        Assert.Contains("fetch", host.Scripts);
        Assert.False(Directory.Exists(host.Local(windowsRuns + @"\run-test\runtime")));
        Assert.Contains("fake boot", File.ReadAllText(Path.Combine(Output, "boot-1", "game-0.log")));
    }

    // #295: the hosted server run's own loader step refuses a copied Windows runtime whose proxy (Doorstop 4) and
    // doorstop_config.ini (Doorstop 3) are from different versions, before the server starts.
    [Fact] public async Task AHostedWindowsServerRunRefusesAMixedDoorstopBeforeItStarts()
    {
        var server = new FakeOwnedServer("test.mod", saveRoot: @"C:\vt\runs\run-test\world");
        var host = new FakeServerHost("windows-server", Mirror, server, windows: true);
        var (plan, profile) = Write(host, hostPlatform: "windows", hostShell: "powershell", doorstopConfig: DoorstopMixPathsTests.Doorstop3Config);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.DoesNotContain("start", host.Scripts);
        var step = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement.GetProperty("Steps").EnumerateArray()
            .Single(s => s.GetProperty("Name").GetString() == "copied Windows runtime has a coherent Doorstop loader");
        Assert.False(step.GetProperty("Passed").GetBoolean());
        DoorstopMixPathsTests.AssertRefusal(new InvalidOperationException(step.GetProperty("Error").GetString()), "server runtime",
            "is Doorstop 4, which reads only [General] in doorstop_config.ini, but that file is written for Doorstop 3 ([UnityDoorstop])");
    }

    // The host's install (the runtime the plan pins) and a local world; returns the plan and the profile.
    private (string Plan, string Profile) Write(FakeServerHost host, int planPort = 5577, string hostPlatform = "linux", string hostShell = "bash", bool withClient = false, bool unpinned = false,
        bool crossplay = false, string portOption = "-port", string gamePort = "2456", object? steamAccounts = null, string? steamAccount = null,
        string doorstopConfig = DoorstopMixPathsTests.Doorstop4Config)
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
            File.WriteAllText(Path.Combine(install, "winhttp.dll"), "MZ target_assembly");
            File.WriteAllText(Path.Combine(install, "doorstop_config.ini"), doorstopConfig);
        }
        else File.WriteAllText(Path.Combine(install, GameLaunch.ServerLinuxExecutable), "server");
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.Preloader.dll"), "preloader");
        string world = Path.Combine(_root, "world");
        Directory.CreateDirectory(Path.Combine(world, "worlds_local"));
        File.WriteAllText(Path.Combine(world, "worlds_local", "Test.db"), "world");
        var plan = new Dictionary<string, object>
        {
            ["scenario"] = "smoke",
            ["runtime"] = new { source = install, sha256 = unpinned ? new Dictionary<string, string>() : WorldFixture.Manifest(install) },
            ["world"] = new { source = world, sha256 = WorldFixture.Manifest(world) },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}", portOption, gamePort, "-logFile", "{runtime}/toolkit-unity.log" },
            ["pins"] = unpinned ? new Dictionary<string, string>() : new Dictionary<string, string> { ["worlduid"] = "1" },
            ["port"] = planPort,
        };
        if (crossplay) plan["crossplay"] = true;
        if (unpinned) plan["pinning"] = "none"; else plan["runtimePins"] = InstallPins.Of(install);
        string planPath = Path.Combine(_root, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var hosts = new Dictionary<string, object>
        {
            ["linux-box"] = new { kind = "ssh", platform = hostPlatform, shell = hostShell, destination = "tester@linux-box.example", @lock = hostPlatform == "windows" ? @"C:\vt\lock" : "/var/tmp/vt/lock" },
        };
        var clients = new Dictionary<string, object>();
        if (withClient)
        {
            hosts["linux-gpu"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-gpu.example", @lock = "/home/tester/lock" };
            var player = new Dictionary<string, object> { ["host"] = "linux-gpu", ["install"] = "/home/tester/valheim", ["runtime"] = "/home/tester/runs", ["cliPort"] = 5578 };
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

    private static PinnedServerRunOptions<ServerRunPlan> Options(FakeServerHost host, FakeOwnedServer? server, Func<TestRun<ServerRunPlan>, Task>? scenario = null,
        FakeServerHost? clientHost = null, IGameTransport? clientTransport = null, IGameHost? leaseHost = null, string name = "toolkit-smoke",
        RunCancellation? cancellation = null) => new()
    {
        Name = name,
        ReadPlan = path => { var plan = ServerRunPlan.Read<ServerRunPlan>(path); plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return plan; },
        Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
        Scenario = TestRun.Scenario(scenario ?? (_ => Task.CompletedTask)),
        Hooks = new FakeRunHooks
        {
            Host = hostName => hostName == "linux-box" ? host : hostName == LeaseBox.Name && leaseHost != null ? leaseHost : clientHost ?? throw new InvalidOperationException("No fake host " + hostName),
            Connect = port => port == 15578 ? clientTransport! : server!.Connect(),
            StateWaits = false, RunId = RunId, Cancellation = cancellation,
        },
    };
    // Where in a fake host's script log the first journal append of that kind ran.
    private static int JournalIndex(FakeServerHost host, string kind) => host.Runs.ToList().FindIndex(run => run.Script == "journal" &&
        Encoding.UTF8.GetString(Convert.FromBase64String(run.Variables["line"])).Contains($"\"kind\":\"{kind}\"", StringComparison.Ordinal));
    private JsonElement Result() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement;
    private IReadOnlyList<string?> StepNames() => Result().GetProperty("Steps").EnumerateArray().Select(step => step.GetProperty("Name").GetString()).ToList();
    private JsonElement Step(string name) => Result().GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == name);

    // #194: what the run wrote in the host's runtime copy is kept; a copy kept on request, or whose server may still run, stays on the host.
    [Fact] public async Task TheHostRuntimeCopyKeepsWhatTheRunWroteAndStaysWhenItMustOrIsAskedTo()
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
        Directory.Delete(Output, true); Directory.Delete(host.Local(RunDirectory), true);
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
        Directory.Delete(Output, true); Directory.Delete(host.Local(RunDirectory), true);
        RunRetirement.KeepOverride.Value = true; // VALHEIM_TESTING_KEEP_RUNTIME=1, in this test's flow only
        try { Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server))); }
        finally { RunRetirement.KeepOverride.Value = null; }
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.Contains("kept on request", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
    }
    [Fact] public async Task AStagedRuntimeCannotStandInForAHostCopy()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        using var staged = WorldFixture.Copy(host.Local(Install), Path.Combine(_root, "staged"), WorldFixture.Manifest(host.Local(Install)));
        var options = Options(host, server);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], new PinnedServerRunOptions<ServerRunPlan>
        {
            Name = options.Name, ReadPlan = options.ReadPlan, Mod = options.Mod,
            Scenario = options.Scenario, Hooks = options.Hooks, StagedRuntime = staged,
        }));
        Assert.DoesNotContain(host.Runs, run => run.Script == "copy");
    }
    [Fact] public async Task ARemoteRunCopiesAndVerifiesOnTheHostReachesTheCliThroughTheTunnelAndStopsOnlyItsServer()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        IGameHost? seenHost = null; string? seenRuntime = null;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            seenHost = run.ServerHost; seenRuntime = run.RuntimeDirectory;
            run.Session.Restart(); // A second boot: its own boot directory and log, and a stop of only the first.
            return Task.CompletedTask;
        }));
        Assert.Equal(0, code);
        // The run journal on the server host (#257): the lock once held, each boot journalled before its start and its process after,
        // the lock's release and the run's end. Each boot's intent precedes its start script.
        var journal = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5));
        var processes = journal.Where(record => record.Actor == "server" && record.Entry.Kind.StartsWith("process-", StringComparison.Ordinal)).ToList();
        Assert.Equal([JournalEntry.ProcessIntended, JournalEntry.ProcessStarted, JournalEntry.ProcessIntended, JournalEntry.ProcessStarted], processes.Select(record => record.Entry.Kind));
        Assert.Equal(RunDirectory + "/boot-2", processes.Last().Entry.Fields["bootDirectory"]);
        // Each started process carries its command line's hash, read on the host right after the start (#257 Q2's third fact).
        Assert.All(journal.Where(record => record.Entry.Kind == JournalEntry.ProcessStarted), record =>
            Assert.Equal(FakeServerHost.CommandLineSha256(record.Entry.Fields["pid"]), record.Entry.Fields["commandLineSha256"]));
        Assert.Equal([JournalEntry.LockHeld, JournalEntry.LockReleased, JournalEntry.RunEnded], journal.Where(record => record.Actor == "run").Select(record => record.Entry.Kind));
        Assert.Equal("passed", journal.Last(record => record.Actor == "run").Entry.Fields["state"]);
        Assert.True(JournalIndex(host, JournalEntry.ProcessIntended) < host.Runs.ToList().FindIndex(run => run.Script == "start"));
        // Every owned server boot gets its test access, the restart's included; no runner option turns it off (#257 Q5).
        Assert.Equal(["devcommands", "confirmcheats", "devcommands", "confirmcheats"],
            server.Events.Where(e => e.StartsWith("devcommands") || e.StartsWith("confirmcheats")).Select(e => e.TrimEnd("0123456789".ToCharArray())));
        Assert.Same(host, seenHost); Assert.Equal(RunDirectory + "/runtime", seenRuntime);
        Assert.Equal(new[] { "enough free disk space for the copies", "take the server host's lock", "copy and verify pinned runtime on the server host", "copy and verify pinned world", "ship and verify the world copy on the server host",
                "copied runtime has the plan's server executable", "copied runtime is the pinned game build, loader and patchers",
                "CLI port is free on the server host", "open the loopback CLI tunnel to the server host", "start and verify owned dedicated fixture", "stop only owned server",
                "fetch the server host's world copy", "remove the server host's runtime copy, keeping what the run changed", "close the CLI tunnel", "release the server host's lock", "scan run logs" }, StepNames());

        // The copy is the host's install, into this run's own directory; the world copy is shipped there.
        var copy = Assert.Single(host.Runs, run => run.Script == "copy");
        Assert.Equal(Install, copy.Variables["source"]); Assert.Equal(RunDirectory + "/runtime", copy.Variables["dest"]);
        Assert.Equal(RunDirectory + "/world", Assert.Single(host.Runs, run => run.Script == "ship").Variables["dest"]);
        // Each boot starts with the plan's arguments on the host's paths, its own boot directory and the logs moved aside.
        var starts = host.Runs.Where(run => run.Script == "start").ToList();
        Assert.Equal(new[] { RunDirectory + "/boot-1", RunDirectory + "/boot-2" }, starts.Select(start => start.Variables["dir"]));
        Assert.Equal("BepInEx/LogOutput.log\ntoolkit-unity.log", starts[0].Variables["logs"]);
        var spec = FakeServerHost.Spec(starts[0].Variables["spec"]);
        Assert.Equal(new[] { "-batchmode", "-nographics", "-savedir", RunDirectory + "/world", "-port", "2456", "-logFile", RunDirectory + "/runtime/toolkit-unity.log" },
            spec.Where(line => line.Kind == "arg").Select(line => line.Text));
        Assert.Contains(("env", "DOORSTOP_TARGET_ASSEMBLY=" + RunDirectory + "/runtime/BepInEx/core/BepInEx.Preloader.dll"), spec);
        Assert.Contains(("prepend", "LD_LIBRARY_PATH=" + RunDirectory + "/runtime/linux64:" + RunDirectory + "/runtime/doorstop_libs"), spec);
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
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/world"))); Assert.True(Directory.Exists(host.Local(RunDirectory + "/boot-1")));
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

    // #257: one journal run per run. This machine's world copy is journalled under the run's own id, beside its server's
    // entries, not under a run id of its own; and the server host's runtime copy leaves out the install's logs/ (Steam's runtime
    // output), which the pinned manifest may list but the copy never counts, while the install keeps it.
    [Fact] public async Task AHostedRunJournalsItsLocalCopiesUnderItsOwnIdAndItsRuntimeCopyLeavesOutSteamsLogs()
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

        Assert.Equal("logs", Assert.Single(host.Runs, run => run.Script == "copy").Variables["skip"]);
        Assert.True(File.Exists(Path.Combine(logs, "connection_log_2456.txt")));
        Assert.DoesNotContain("logs/", File.ReadAllText(Path.Combine(Output, "runtime-changes", "changes.json")));
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
        Assert.Equal(Runs + "/" + RunId + "/runtime", Assert.Single(host.Runs, run => run.Script == "party").Variables["runtime"]);
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

    [Fact] public async Task ValidateOnAHostCopiesAndVerifiesThereAndStartsNothing()
    {
        var host = NewHost();
        var (plan, profile) = Write(host);
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["validate", plan, Output], Options(host, null)));
        Assert.Contains("prepared only; no game launched", StepNames());
        Assert.DoesNotContain(host.Runs, run => run.Script is "port" or "tunnel" or "start" or "fetch");
        Assert.Equal(host.Claims, host.Releases); Assert.Single(host.Claims);
        // Nothing was launched, so the host copy just goes (#194), with nothing to keep or fetch.
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/runtime")));
        Assert.EndsWith("nothing was launched", Result().GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
    }

    [Fact] public async Task ARuntimeCopyThatDiffersOnTheHostFailsBeforeAnythingStarts()
    {
        var host = NewHost(NewServer());
        var (plan, profile) = Write(host);
        host.AfterCopy = runtime => File.WriteAllText(Path.Combine(runtime, "BepInEx", "core", "BepInEx.dll"), "another bepinex");
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, NewServer())));
        var copy = Step("copy and verify pinned runtime on the server host");
        Assert.False(copy.GetProperty("Passed").GetBoolean());
        Assert.Contains("different hash: BepInEx/core/BepInEx.dll", copy.GetProperty("Error").GetString());
        Assert.DoesNotContain(host.Runs, run => run.Script is "start" or "tunnel");
        Assert.Equal(host.Claims, host.Releases);
    }

    [Fact] public async Task AnotherRunsLockRefusesTheRunBeforeTheHostIsTouched()
    {
        var host = NewHost(NewServer()); host.HeldBy = "other-runner run-x [0123]";
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, NewServer())));
        var step = Step("take the server host's lock");
        Assert.False(step.GetProperty("Passed").GetBoolean()); Assert.Contains("other-runner run-x [0123]", step.GetProperty("Error").GetString());
        Assert.Empty(host.Runs);
        Assert.Empty(host.Releases);
        Assert.DoesNotContain("release the server host's lock", StepNames());
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
    // order that can run the plan, records why, and refuses before anything is written when none can. --profile is gone.
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
        // Skipped in order: a Windows host for this Linux runtime, a local host of another platform than this machine's, an
        // environment whose loader package only a campaign applies, and a CLI port that is not the plan's.
        string other = HostProfile.CurrentPlatform == "linux" ? "windows" : "linux";
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object>
            {
                ["linux-box"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-box.example", @lock = "/var/tmp/vt/lock" },
                ["windows-box"] = new { kind = "ssh", platform = "windows", shell = "powershell", destination = "tester@windows-box.example", @lock = @"C:\vt\lock" },
                ["elsewhere"] = new { kind = "local", platform = other, shell = other == "windows" ? "powershell" : "bash", @lock = other == "windows" ? @"C:\vt\lock" : "/var/tmp/vt/lock" },
            },
            environments = new object[]
            {
                new { name = "windows", host = "windows-box", roles = new[] { "server" }, install = @"C:\valheim\server", runtime = @"C:\vt\runs", cliPort = 5577, gamePort = 2456 },
                new { name = "local-other", host = "elsewhere", roles = new[] { "server" }, install = other == "windows" ? @"C:\valheim\server" : Install,
                      runtime = other == "windows" ? @"C:\vt\runs" : Runs, cliPort = 5577, gamePort = 2456 },
                new { name = "packaged", host = "linux-box", roles = new[] { "server" }, install = Install, runtime = Runs, cliPort = 5577, gamePort = 2456, loaderPackage = "/srv/loader.json" },
                Environment("other-port", 5590), Environment("fits", 5577),
            },
        }));
        Assert.Equal(0, await PinnedServerRun.MainAsync(["--inventory", inventory, "run", plan, Output], Options(host, server)));
        var provenance = Result().GetProperty("Provenance");
        string placed = provenance.GetProperty("serverEnvironment").GetString()!;
        Assert.StartsWith("fits: first server recipe that can run the plan after windows: the plan's runtime is a linux server, but the host is windows.", placed);
        Assert.Contains($"local-other: it is a local {other} host, but this machine is {HostProfile.CurrentPlatform}.", placed);
        Assert.Contains("packaged: it names a loaderPackage, which only a campaign's preparation applies", placed);
        Assert.Contains("other-port: the plan's ValheimCLI port 5577 is not its cliPort 5590", placed);
        Assert.Equal(FileHash.Sha256(inventory), provenance.GetProperty("inventorySha256").GetString());
        Assert.Contains("start", host.Scripts);

        Inventory(Environment("other-port", 5590));
        string refused = Path.Combine(_root, "refused");
        int scripts = host.Runs.Count;
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--inventory", inventory, "run", plan, refused], Options(host, server)));
        Assert.False(Directory.Exists(refused)); Assert.Equal(scripts, host.Runs.Count);
        // The removed option is refused as bad usage, never read as a file.
        Assert.Equal(2, await PinnedServerRun.MainAsync(["--profile", inventory, "run", plan, refused], Options(host, server)));
        Assert.False(Directory.Exists(refused));
    }

    [Fact] public async Task AProfileClientStartsInItsHostsDesktopSessionAndStopsOnlyThatClient()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, GameLaunch.ClientLinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, "BepInEx", "LogOutput.log"), "an earlier run's log\n");
        var (plan, profile) = Write(host, withClient: true);
        var clientTransport = new ScriptedTransport();
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, LaunchArguments = ["+connect", "linux-box:2456"] };
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
        // The launch used the profile's install and a launch directory in this run's directory on the client's host.
        var start = Assert.Single(clientHost.Runs, run => run.Script == "client-start");
        Assert.Equal("/home/tester/valheim", start.Variables["install"]);
        Assert.Equal("/home/tester/runs/" + RunId + "/client-1", start.Variables["dir"]);
        Assert.Contains(FakeServerHost.Spec(start.Variables["spec"]), line => line.Kind == "arg" && line.Text == "+connect");
        // The client host's journal (#257): its lock held and released, the client journalled before its start, then its process.
        var clientJournal = await RunJournalOnHost.ReadAsync(clientHost, "/home/tester/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.Equal([JournalEntry.ProcessIntended, JournalEntry.ProcessStarted], clientJournal.Where(record => record.Actor == "player").Select(record => record.Entry.Kind));
        Assert.Equal("77", clientJournal.Single(record => record.Entry.Kind == JournalEntry.ProcessStarted).Entry.Fields["pid"]);
        Assert.Equal(FakeServerHost.CommandLineSha256("77"), clientJournal.Single(record => record.Entry.Kind == JournalEntry.ProcessStarted).Entry.Fields["commandLineSha256"]);
        Assert.Equal([JournalEntry.LockHeld, JournalEntry.LockReleased], clientJournal.Where(record => record.Actor == "run").Select(record => record.Entry.Kind));
        Assert.True(JournalIndex(clientHost, JournalEntry.ProcessIntended) < clientHost.Runs.ToList().FindIndex(run => run.Script == "client-start"));
        // The earlier log moved aside first, the new one was awaited from its start; only that client was stopped.
        Assert.Equal("/home/tester/runs/" + RunId + "/client-1.previous-LogOutput.log", Assert.Single(clientHost.Runs, run => run.Script == "move-aside").Variables["to"]);
        Assert.Equal(2, clientHost.Runs.Count(run => run.Script == "follow")); // fresh BepInEx line, then ValheimCLI listening
        Assert.All(clientHost.Runs.Where(run => run.Script == "follow"), run => Assert.Equal("0", run.Variables["offset"]));
        Assert.Equal(new[] { ("77", "555") }, clientHost.Stops);
        Assert.Contains("listening on 127.0.0.1:5578", File.ReadAllText(Path.Combine(Output, "player", "client-1", "game-0.log")));
        Assert.True(Assert.Single(clientHost.Tunnels).Stopped);
        // The client's host was locked for the run and released at teardown; its logs were scanned with the server's.
        Assert.Equal(clientHost.Claims, clientHost.Releases); Assert.Single(clientHost.Claims);
        Assert.Contains("release client host linux-gpu's lock", StepNames());
        Assert.Contains(Result().GetProperty("Logs").EnumerateArray(), log => log.GetProperty("Role").GetString() == "client-1 BepInEx log");
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
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578) { SteamLog = "/home/tester/.local/share/Steam/logs/connection_log.txt" };
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, GameLaunch.ClientLinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        string steamLog = clientHost.Local(clientHost.SteamLog);
        Directory.CreateDirectory(Path.GetDirectoryName(steamLog)!);
        File.WriteAllText(steamLog, SteamAccountInUseTests.Healthy + SteamAccountInUseTests.SteamDup1 + SteamAccountInUseTests.Healthy);
        long before = new FileInfo(steamLog).Length;
        clientHost.BeforeScript = name => { if (name == "client-start") File.AppendAllText(steamLog, signedOut ? SteamAccountInUseTests.SteamDup2 : SteamAccountInUseTests.Healthy); };
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, LaunchArguments = ["+connect", "linux-box:2456"] };
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
        Assert.Equal(SteamSessionLog.Message(null, "linux-gpu"), error.Message);
        Assert.DoesNotContain("12345678", error.Message);
        Assert.Equal(new[] { ("77", "555") }, clientHost.Stops); // the started client was stopped
    }

    // #248's remaining half: a remote Linux client's loader is checked before launch, as a Windows client's is. Through the run:
    // the client's host is never asked to start a game without its Doorstop library.
    [Fact] public async Task ARemoteLinuxClientWithoutItsDoorstopLibraryIsRefusedBeforeLaunch()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/home/tester/valheim");
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, GameLaunch.ClientLinuxExecutable), "client");
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, LaunchArguments = ["+connect", "linux-box:2456"] };
        Exception? refused = null;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            refused = Record.Exception(() => run.OpenClient(client));
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport()));
        Assert.Contains("doorstop_libs/libdoorstop_x64.so", Assert.IsType<FileNotFoundException>(refused).Message);
        Assert.DoesNotContain(clientHost.Runs, run => run.Script == "client-start");
        Assert.Equal(0, code); // The scenario recorded the refusal; nothing else in the run failed.
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
            new ClientRunPlan { Mode = "owned", Pinning = "none" }, TimeSpan.FromSeconds(5), default));
        Assert.Contains("not a Doorstop proxy this check recognises", unrecognised.Message);
        File.WriteAllText(Path.Combine(local, "winhttp.dll"), "MZ target_assembly"); // Doorstop 4 signature
        File.WriteAllText(Path.Combine(local, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var plan = new ClientRunPlan { Mode = "owned", Pinning = "none", Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) } };
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

    [Fact] public async Task WindowsPowerShellReadsTheRemoteLoaderFilesForPreflight()
    {
        if (!OperatingSystem.IsWindows()) return;
        string install = Path.Combine(_root, "windows-client");
        Directory.CreateDirectory(install);
        FakeInstalls.Client(install); // The preloader and core, found by the real existence script.
        File.WriteAllText(Path.Combine(install, "winhttp.dll"), "MZ target_assembly");
        string config = Path.Combine(install, "doorstop_config.ini");
        File.WriteAllText(config, "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var host = new LocalGameHost("windows-client", HostShell.WindowsPowerShell);
        var plan = new ClientRunPlan { Mode = "owned", Pinning = "none" };
        await HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(30), default);
        File.WriteAllText(config, "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var error = await Assert.ThrowsAsync<DoorstopPairingException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Windows, plan, TimeSpan.FromSeconds(30), default));
        Assert.Contains("Doorstop 4", error.Message);
    }

    // The bash existence script for real: a Linux client's loader files are found, and a missing Doorstop library is named.
    [Fact] public async Task BashChecksTheRemoteLoaderFilesForPreflight()
    {
        if (OperatingSystem.IsWindows()) return;
        string install = Path.Combine(_root, "linux client"); // A space in the path, as a host install may have.
        FakeInstalls.Client(install);
        var host = new LocalGameHost("linux-client", HostShell.Bash);
        var plan = new ClientRunPlan { Mode = "owned", Pinning = "none" };
        Assert.Contains("doorstop_libs/libdoorstop_x64.so", (await Assert.ThrowsAsync<FileNotFoundException>(() =>
            HostClientPreflight.CheckAsync(host, install, ClientPlatform.Linux, plan, TimeSpan.FromSeconds(30), default))).Message);
        FakeInstalls.LinuxLoader(install);
        await HostClientPreflight.CheckAsync(host, install, ClientPlatform.Linux, plan, TimeSpan.FromSeconds(30), default);
    }

    // A client that started but never reached its menu (here its pins do not hold) is stopped; its fetched logs are still scanned.
    [Fact] public async Task AProfileClientWhoseStartupFailsStillHasItsLogsScannedAndListed()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, GameLaunch.ClientLinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        // Strict, so the menu pins are checked; the scripted client does not hold them.
        var client = new ClientRunPlan
        {
            Mode = "owned", Install = _root, Port = 5578, StartSeconds = 30, Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) }, InstallPins = InstallPins.Of(clientInstall),
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
        var roles = Result().GetProperty("Logs").EnumerateArray().Select(log => log.GetProperty("Role").GetString()).ToList();
        Assert.Contains("client-1 BepInEx log", roles);
        Assert.Contains("client-1 Player.log", roles);
    }

    [Fact] public async Task AProfileClientWithoutAFreshBepInExLogFailsAtTheLoaderDeadline()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578) { ClientWritesBepInExLog = false };
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, GameLaunch.ClientLinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 300, BepInExSeconds = 30 };
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
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578) { ClientWritesBepInExLog = false };
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, GameLaunch.ClientLinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        static string B(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        clientHost.PreloaderReply = (fresh ? $"VT-PRELOADER fresh {B("preloader_20261005_190000.log")} {B("[Fatal  :   BepInEx] Could not find BepInEx.Preloader.Core")}\n" : "") +
            $"VT-PRELOADER stale {B("preloader_20260101_000000.log")} -\nVT-PRELOADER-END\n";
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 300, BepInExSeconds = 30 };
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

    [Fact] public async Task AnArm64ProfileClientIsRefusedBeforeItsHostIsLockedOrAnythingStarts()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, Architecture = "arm64" };
        Exception? refused = null;
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            refused = Record.Exception(() => run.OpenClient(client));
            return Task.CompletedTask;
        }, clientHost, new ScriptedTransport())));
        Assert.Contains("architecture arm64 is for a macOS client launched locally in this runner's GUI session", Assert.IsType<ArgumentException>(refused).Message);
        Assert.Empty(clientHost.Claims); Assert.Empty(clientHost.Runs); Assert.Empty(clientHost.Tunnels);
    }
}
