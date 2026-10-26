using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

// The whole runner lifecycle on temporary fixtures, a standalone run without --inventory: its server's profile is staged on
// this machine's host (LocalServerMachine: a fake Linux host over a mirror, on any OS), and launching modes start the fake owned
// server through the internal session seam (no game).
public sealed class PinnedServerRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pinned-run-" + Guid.NewGuid().ToString("N"));
    private readonly LocalServerMachine _machine;
    public PinnedServerRunTests() => _machine = new LocalServerMachine(Path.Combine(_root, "local-host"));
    // The install the plan's runtime names, in the local host's mirror.
    private string Runtime => _machine.InstallDirectory;
    private string World => Path.Combine(_root, "world");
    private string Output => Path.Combine(_root, "out");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static async Task<int> RunHere<TPlan>(string[] args, PinnedServerRunOptions<TPlan> options) where TPlan : ServerRunPlan
    {
        using var machine = LocalServerMachine.Use();
        return await PinnedServerRun.MainAsync(args, options);
    }
    // A path the run reports on this machine's host, in the mirror.
    private string OnHost(string path) => _machine.Host.Local(path);

    // runtimePins: the pins to write (default: the runtime's own).
    private string WritePlan(Dictionary<string, object>? logScan = null, InstallPins? runtimePins = null,
        object? client = null)
    {
        FakeInstalls.World(Path.Combine(World, "worlds_local"), "Test", 1);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var plan = new Dictionary<string, object>
        {
            ["scenario"] = "smoke",
            ["runtime"] = new { source = LocalServerMachine.Install, sha256 = WorldFixture.Manifest(Runtime) },
            ["world"] = new { source = World, sha256 = WorldFixture.Manifest(World) },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}" },
            ["pins"] = new Dictionary<string, string> { ["worlduid"] = "1" },
            ["port"] = port < 1024 ? 5577 : port,
            ["logScan"] = logScan ?? [],
        };
        plan["runtimePins"] = runtimePins ?? InstallPins.Of(Runtime);
        if (client != null) plan["client"] = client;
        string path = Path.Combine(_root, "plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan));
        return path;
    }
    private PinnedServerRunOptions<ServerRunPlan> Options(Func<TestRun<ServerRunPlan>, Task>? scenario = null,
        FakeOwnedServer? server = null, Action<ServerRunPlan>? checkPlan = null) => new()
    {
        Hooks = _machine.Hooks(),
        Name = "toolkit-smoke",
        ReadPlan = path => { var plan = ServerRunPlan.Read<ServerRunPlan>(path); plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return plan; },
        Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
        CheckPlan = checkPlan,
        Scenario = TestRun.Scenario(scenario ?? (_ => Task.CompletedTask)),
        SessionOverride = server == null ? null : _ => server.Session(TimeSpan.FromSeconds(60)),
    };
    private JsonElement Result() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement;
    // A mod plan with a client section, read and validated the way a mod runner's ReadPlan does.
    private PinnedServerRunOptions<CrossplayPlanTests.ClientPlan> ClientOptions(FakeOwnedServer? server = null) => new()
    {
        Hooks = _machine.Hooks(),
        Name = "toolkit-smoke",
        ReadPlan = path =>
        {
            var plan = ServerRunPlan.Read<CrossplayPlanTests.ClientPlan>(path);
            plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); plan.Client?.Validate();
            return plan;
        },
        Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
        ClientPlans = plan => plan.Client == null ? [] : [plan.Client],
        Scenario = (_, _) => Task.CompletedTask,
        SessionOverride = server == null ? null : _ => server.Session(TimeSpan.FromSeconds(60)),
    };
    private static object MacClient(string install, string architecture)
    {
        FakeInstalls.MacClient(install);
        return new
        {
            mode = "owned", install, architecture, inPlace = true, port = 5556, join = "127.0.0.1:2456", character = "Tester",
            installPins = InstallPins.Of(install), pins = new Dictionary<string, string> { [CliCapabilityManifest.PluginGuid] = new string('a', 32) },
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CleanupBudgetMustBePositiveBeforeARunStarts(int ticks)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new PinnedServerRunOptions<ServerRunPlan>
        {
            Name = "toolkit-smoke", Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
            ReadPlan = _ => throw new Exception("a plan must not be read"),
            Scenario = (_, _) => Task.CompletedTask,
            CleanupBudget = TimeSpan.FromTicks(ticks),
        });
        Assert.Equal("CleanupBudget", error.ParamName);
    }

    // #730: without --inventory the server runs on this machine, from a profile of the install the plan names: the same profile
    // staging, checks and retirement as on any host, never a copy of the install.
    [Fact] public async Task ValidateStagesTheServersProfileOnThisMachineWithoutLaunching()
    {
        string plan = WritePlan();
        var before = WorldFixture.Manifest(Runtime);
        Assert.Equal(0, await RunHere(["validate", plan, Output], Options()));
        var result = Result();
        Assert.True(result.GetProperty("Passed").GetBoolean());
        // A Windows host first shows it can start a server task.
        // #612: a Linux server's profile is linked too, so its storage is checked first.
        string[] storage = [LocalServerMachine.Windows ? "check the Windows profile storage" : "check the Linux profile storage"];
        string[] task = LocalServerMachine.Windows ? ["the server host can start a server task"] : [];
        Assert.Equal([ "the world fixture is one complete world", "admit the fixture against the actual hosting install", "enough free disk space for the copies", .. storage, "copy and verify pinned world", .. task, "take the server host's lock",
                "check the server host's install against the pinned runtime and stage its profile",
                "ship and verify the world copy on the server host", "server runtime has the plan's server executable",
                "server runtime is the pinned game build, loader and patchers", "prepared only; no game launched",
                "verify server's source install after the run (size and modified time)",
                "remove the server host's runtime copy, keeping what the run changed", "remove the fetched world copy on the server host",
                "fetch and remove the server host's run folder", "release the server host's lock" ],
            result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
        // Validate prepares and cleans up; it runs no scenario, so the scenario state is not reported as passed.
        Assert.Equal((true, true, false, true), (result.GetProperty("PreflightPassed").GetBoolean(), result.GetProperty("RuntimeReady").GetBoolean(),
            result.GetProperty("ScenarioPassed").GetBoolean(), result.GetProperty("CleanupVerified").GetBoolean()));
        Assert.Equal([ "Preflight", "Preflight", "Preflight", .. storage.Select(_ => "Preflight"), .. task.Select(_ => "Setup"), "Setup", "Setup", "Setup", "Setup", "Setup", "Setup", "Setup", "Cleanup", "Cleanup", "Cleanup", "Cleanup", "Cleanup" ],
            result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Phase").GetString()));
        var provenance = result.GetProperty("Provenance");
        Assert.Equal(("this machine", EnvironmentInventory.LocalHost, EnvironmentInventory.LocalHost + ":" + LocalServerMachine.Install),
            (provenance.GetProperty("inventory").GetString(), provenance.GetProperty("serverHost").GetString(), provenance.GetProperty("runtimeSource").GetString()));
        Assert.StartsWith(EnvironmentInventory.LocalServer + ": ", provenance.GetProperty("serverEnvironment").GetString());
        Assert.DoesNotContain("copy", _machine.Host.Scripts);
        Assert.Contains("profile-seed", _machine.Host.Scripts);
        Assert.Equal(before, WorldFixture.Manifest(Runtime)); // the install itself never changes
        Assert.Equal("validate", result.GetProperty("Provenance").GetProperty("mode").GetString());
        Assert.Equal(InstallPins.Of(Runtime).Game, result.GetProperty("Provenance").GetProperty("runtimeGameSha256").GetString());
        Assert.True(File.Exists(Path.Combine(Output, "input-hashes.json"))); Assert.True(File.Exists(Path.Combine(Output, "junit.xml")));
    }
    // An in-place launch's architecture check runs when the plan is read: validate refuses it, and run refuses it before the server starts.
    [Fact] public async Task AnArm64ClientWithoutTheNativeCoreIsRefusedAtValidateAndBeforeTheServerStarts()
    {
        using var legacy = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.LegacyDetour);
        string plan = WritePlan(client: MacClient(legacy.Root, "arm64"));
        Assert.Contains("MonoMod before 25", Assert.Throws<ArgumentException>(() => ClientOptions().ReadPlan(plan)).Message);
        Assert.Equal(1, await RunHere(["validate", plan, Output], ClientOptions()));
        Assert.False(Directory.Exists(Output)); // Refused before anything was copied or written.
        var server = new FakeOwnedServer("test.mod");
        Assert.Equal(1, await RunHere(["run", plan, Output], ClientOptions(server)));
        Assert.Empty(server.Events); Assert.False(Directory.Exists(Output));
        using var native = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.NativeDetour);
        string nativePlan = WritePlan(client: MacClient(native.Root, "arm64"));
        Assert.Equal(ClientArchitecture.Arm64, ClientOptions().ReadPlan(nativePlan).Client!.LaunchArchitecture);
        Assert.Equal(0, await RunHere(["validate", nativePlan, Output], ClientOptions()));
    }

    // #860: a standalone run decides its installs' versions as a campaign does. Its declared client is read in place before the
    // server's install is listed, and a server the client cannot join is refused there, before its profile is staged.
    [Theory]
    [InlineData("range")]
    [InlineData("unknown")]
    public async Task TheSelectedHostsWorldSupportRefusesBeforeProfileShipOrLaunch(string kind)
    {
        string assembly = Path.Combine(Runtime, "valheim_server_Data", "Managed", InstallPins.GameAssemblyName);
        File.WriteAllBytes(assembly, FakeInstalls.GameAssembly(maximum: 40, shape: kind == "unknown" ? "missing" : "interval"));
        string plan = WritePlan();
        var before = WorldFixture.Manifest(Runtime);
        Assert.Equal(1, await RunHere(["validate", plan, Output], Options()));
        Assert.DoesNotContain(_machine.Host.Scripts, script => script is "profile-seed" or "apply-stage" or "ship" or "server-start" or "copy");
        WorldFixture.Verify(Runtime, before);
        var failed = Assert.Single(Result().GetProperty("Steps").EnumerateArray(), step => step.GetProperty("Name").GetString() == "admit the fixture against the actual hosting install");
        Assert.Contains(kind == "unknown" ? "Cannot establish world-format" : "world version 41", failed.GetProperty("Error").GetString());
    }

    [Theory]
    [InlineData("worlduid", "version", false)]
    [InlineData("world", "version", false)]
    [InlineData("any", "version", false)]
    [InlineData("none", "version", false)]
    [InlineData("worlduid", "uid", false)]
    [InlineData("world", "broken", false)]
    [InlineData("any", "bytes", false)]
    [InlineData("none", "name", false)]
    [InlineData("worlduid", "bytes", true)]
    [InlineData("world", "bytes", true)]
    [InlineData("any", "bytes", true)]
    [InlineData("none", "bytes", true)]
    public async Task AnInputChangedDuringHostInspectionIsRefusedBeforeAnyRuntimeProfile(string pin, string change, bool manifest)
    {
        string path = WritePlan();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        node["pins"] = pin switch
        {
            "worlduid" => System.Text.Json.Nodes.JsonNode.Parse("{\"worlduid\":\"1\"}"),
            "world" => System.Text.Json.Nodes.JsonNode.Parse("{\"world\":\"Test\"}"),
            "any" => System.Text.Json.Nodes.JsonNode.Parse("{\"world\":\"any\"}"),
            _ => new System.Text.Json.Nodes.JsonObject(),
        };
        if (!manifest) node["world"]!["sha256"] = new System.Text.Json.Nodes.JsonObject();
        File.WriteAllText(path, node.ToJsonString());
        bool changed = false;
        _machine.Host.BeforeScript = script =>
        {
            if (script != "preflight-read" || changed) return;
            changed = true;
            string world = Path.Combine(World, "worlds_local", "Test");
            switch (change)
            {
                case "broken": File.Delete(Path.Combine(world, "_main.1.db2")); break;
                case "bytes": File.AppendAllText(Path.Combine(world, "_main.1.db2"), "drift"); break;
                default: File.WriteAllBytes(Path.Combine(world, "_main.1.fwl2"), FakeInstalls.Metadata(change == "name" ? "Other" : "Test", change == "uid" ? 2 : 1, version: change == "version" ? 42 : 41)); break;
            }
        };
        Assert.Equal(1, await RunHere(["validate", path, Output], Options()));
        Assert.True(changed);
        Assert.DoesNotContain(_machine.Host.Scripts, script => script is "profile-seed" or "apply-stage" or "ship" or "server-start" or "copy");
    }

    [Fact] public async Task AServerOnAnotherNetworkVersionThanTheDeclaredClientIsRefusedBeforeItsProfileIsStaged()
    {
        using var native = ClientLaunchTests.Install.Mac(universalDoorstop: true, core: ClientLaunchTests.NativeDetour);
        string install = native.Root;
        var client = MacClient(install, "arm64");
        // On one network version (both 1.0.17): the declared client is read and the run goes on.
        string matching = WritePlan(client: client);
        Assert.Equal(0, await RunHere(["validate", matching, Output + "-matching"], ClientOptions()));
        File.WriteAllBytes(Path.Combine(install, GameLaunch.ClientMacBundle, "Contents", "Resources", "Data", "Managed", InstallPins.GameAssemblyName),
            FakeInstalls.GameAssembly("1.0.20", 41));
        string plan = WritePlan(client: client);
        int scriptsBefore = _machine.Host.Scripts.Count;
        Assert.Equal(1, await RunHere(["validate", plan, Output], ClientOptions()));
        var steps = Result().GetProperty("Steps").EnumerateArray().ToList();
        Assert.Contains(steps, step => step.GetProperty("Name").GetString() == "read the declared clients' game versions" && step.GetProperty("Passed").GetBoolean());
        var refused = Assert.Single(steps, step => step.GetProperty("Name").GetString() == "admit the fixture against the actual hosting install");
        Assert.Contains("declared client's install " + install + " on this machine is Valheim 1.0.20 (network version 41), but server's install", refused.GetProperty("Error").GetString());
        Assert.Contains("is Valheim 1.0.17 (network version 40). Valheim refuses a join across network versions", refused.GetProperty("Error").GetString());
        Assert.DoesNotContain(steps, step => step.GetProperty("Name").GetString() == "copy and verify pinned world");
        Assert.DoesNotContain(_machine.Host.Scripts.Skip(scriptsBefore), script => script is "profile-seed" or "apply-stage" or "copy");
    }

    [Fact] public async Task AMissingClientJoinPasswordIsRefusedBeforeTheServerOrAnyCopyStarts()
    {
        using var install = PreflightInstall.Create();
        const string variable = "VT_EARLY_JOIN_PASSWORD_TEST";
        string? previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            string plan = WritePlan(client: new
            {
                mode = "owned", install = install.Root, architecture = "x64", port = 5556,
                join = "127.0.0.1:2456", character = "Tester", passwordVariable = variable,
                installPins = InstallPins.Of(install.Root), pins = new Dictionary<string, string> { [CliCapabilityManifest.PluginGuid] = FileHash.Md5(Path.Combine(install.Root, "BepInEx", "plugins", "valheimCLI.dll")) },
            });
            var server = new FakeOwnedServer("test.mod");
            Assert.Equal(1, await RunHere(["run", plan, Output], ClientOptions(server)));
            Assert.Empty(server.Events);
            Assert.False(Directory.Exists(Output));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }
    // #256 amendment C: a restarted server process has neither cheat gate, so the session establishes test access on every
    // boot it starts, the scenario's restarts included; a server that never acknowledges fails its start, asked once.
    [Fact] public async Task TheDefaultRunEstablishesTestAccessOnEveryBootAndAServerThatNeverAcknowledgesFails()
    {
        string plan = WritePlan();
        var server = new FakeOwnedServer("test.mod");
        TestAccessState? afterRestart = null;
        Assert.Equal(0, await RunHere(["run", plan, Output], Options(server: server, scenario: run =>
        {
            afterRestart = TestAccess.Read(run.Session.Restart());
            return Task.CompletedTask;
        })));
        Assert.Equal(new[] { "devcommands1", "confirmcheats1", "devcommands2", "confirmcheats2" },
            server.Events.Where(e => e.StartsWith("devcommands") || e.StartsWith("confirmcheats")));
        Assert.True(afterRestart is { Devcommands: true, CheatsAcknowledged: true });
        Directory.Delete(Output, true);
        var stubborn = new FakeOwnedServer("test.mod") { IgnoreConfirmCheats = true };
        Assert.Equal(1, await RunHere(["run", plan, Output], Options(server: stubborn)));
        var start = Result().GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == "start and verify owned dedicated fixture");
        Assert.False(start.GetProperty("Passed").GetBoolean());
        Assert.Contains("The owned server started, but its test access was not established", start.GetProperty("Error").GetString());
        Assert.Single(stubborn.Events.Where(e => e.StartsWith("confirmcheats"))); // once, never retried
    }
    [Fact] public async Task ExistingEvidenceIsNeverOverwrittenAndBadUsageIsRefused()
    {
        string plan = WritePlan();
        Directory.CreateDirectory(Output); File.WriteAllText(Path.Combine(Output, "keep.txt"), "evidence");
        Assert.Equal(1, await RunHere(["validate", plan, Output], Options()));
        Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(Output).Select(Path.GetFileName));
        Assert.Equal(2, await RunHere(["launch", plan, Output + "2"], Options()));
        Assert.Equal(2, await RunHere(["validate", plan], Options()));
    }
    [Fact] public async Task AdmittedServerCopyRetainsPinnedFilesBesideWorldsLocal()
    {
        string path = WritePlan(), parentFile = Path.Combine(World, "server.cfg");
        File.WriteAllText(parentFile, "whole save root pin");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        node["world"]!["sha256"] = JsonSerializer.SerializeToNode(WorldFixture.Manifest(World));
        File.WriteAllText(path, node.ToJsonString());
        bool sawParent = false;
        _machine.Host.AfterShip = shipped =>
        {
            if (!Directory.Exists(Path.Combine(shipped, "worlds_local"))) return;
            Assert.Equal("whole save root pin", File.ReadAllText(Path.Combine(shipped, "server.cfg")));
            sawParent = true;
        };
        Assert.Equal(0, await RunHere(["validate", path, Output], Options()));
        Assert.True(sawParent); Assert.Equal("whole save root pin", File.ReadAllText(parentFile));
    }

    [Fact] public async Task AChangedFixtureFailsBeforeAnythingLaunches()
    {
        string plan = WritePlan();
        File.WriteAllText(Path.Combine(World, "worlds_local", "Test", "_main.1.db2"), "edited after pins recorded");
        Assert.Equal(1, await RunHere(["validate", plan, Output], Options()));
        Assert.False(Result().GetProperty("Passed").GetBoolean());
    }
    [Fact] public async Task RunStartsTheOwnedServerRunsTheScenarioAndStopsOnlyThatServer()
    {
        string plan = WritePlan();
        var server = new FakeOwnedServer("test.mod"); string? seen = null;
        int code = await RunHere(["run", plan, Output], Options(run => { seen = run.Server.GetType().Name; return Task.CompletedTask; }, server));
        Assert.Equal(0, code); Assert.Equal("GameActor", seen);
        Assert.Contains("stop1", server.Events);
        var result = Result();
        Assert.Contains("stop only owned server", result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
        Assert.Equal("1", result.GetProperty("Provenance").GetProperty("ownedPids").GetString());
    }
    [Fact] public async Task AFailingScenarioFailsTheRunButStillStopsTheServer()
    {
        string plan = WritePlan();
        var server = new FakeOwnedServer("test.mod");
        int code = await RunHere(["run", plan, Output], Options(_ => throw new InvalidOperationException("fixture refused"), server));
        Assert.Equal(1, code); Assert.Contains("stop1", server.Events);
        Assert.Contains(Result().GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "runner failed" && s.GetProperty("Error").GetString() == "fixture refused");
    }
    [Fact] public async Task AFailedTeardownFailsAPassingRunAndKeepsItsEvidence()
    {
        string plan = WritePlan();
        var server = new FakeOwnedServer("test.mod") { RefuseStop = true };
        int code = await RunHere(["run", plan, Output], Options(server: server));
        Assert.Equal(1, code); Assert.Contains("stop1", server.Events);
        var result = Result();
        Assert.False(result.GetProperty("Passed").GetBoolean());
        var stop = Assert.Single(result.GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "stop only owned server");
        Assert.False(stop.GetProperty("Passed").GetBoolean()); Assert.Equal("Fake server refused to stop.", stop.GetProperty("Error").GetString());
        var junit = System.Xml.Linq.XDocument.Load(Path.Combine(Output, "junit.xml")).Root!;
        // The stop, the unproven source-integrity verdict, and the host's lock, which a server that may still run keeps
        // (env recover releases it once it has stopped).
        Assert.Equal("3", junit.Attribute("failures")!.Value);
        Assert.Contains(result.GetProperty("Steps").EnumerateArray(), s =>
            s.GetProperty("Name").GetString()!.Contains("source", StringComparison.OrdinalIgnoreCase)
            && !s.GetProperty("Passed").GetBoolean()
            && s.GetProperty("Error").GetString()!.Contains("not proven stopped", StringComparison.Ordinal));
        Assert.Contains("may still run", Assert.Single(result.GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "release the server host's lock").GetProperty("Error").GetString());
        // The failed stop is in the Cleanup suite; the runtime was ready, and cleanup is not verified.
        var cleanup = junit.Elements("testsuite").Single(s => s.Attribute("name")!.Value.EndsWith(" / cleanup", StringComparison.Ordinal));
        Assert.Contains(cleanup.Elements("testcase"), c => c.Attribute("name")!.Value == "stop only owned server" && c.Element("failure") != null);
        Assert.Equal((true, false), (result.GetProperty("RuntimeReady").GetBoolean(), result.GetProperty("CleanupVerified").GetBoolean()));
        Assert.True(File.Exists(Path.Combine(Output, "input-hashes.json")));
        server.RefuseStop = false;
    }
    // #194: after a clean stop the server's profile goes, keeping what the run wrote in it; the world copy and the evidence stay.
    private string Copy(JsonElement result, string name) => result.GetProperty("Provenance").GetProperty(name).GetString()!;
    [Fact] public async Task ACleanRunKeepsWhatItWroteInTheProfileAndRemovesIt()
    {
        string plan = WritePlan();
        var server = new FakeOwnedServer("test.mod");
        int code = await RunHere(["run", plan, Output], Options(run =>
        {
            Directory.CreateDirectory(Path.Combine(OnHost(run.RuntimeDirectory), "BepInEx", "cache"));
            File.WriteAllText(Path.Combine(OnHost(run.RuntimeDirectory), "BepInEx", "cache", "audit.txt"), "written during the run");
            return Task.CompletedTask;
        }, server));
        Assert.Equal(0, code);
        var result = Result();
        Assert.False(Directory.Exists(OnHost(Copy(result, "runtime"))));
        Assert.True(Directory.Exists(Copy(result, "world")));
        Assert.Equal("written during the run", File.ReadAllText(Path.Combine(Output, "runtime-changes", "BepInEx", "cache", "audit.txt")));
        Assert.True(File.Exists(Path.Combine(Output, "runtime-changes", "changes.json")));
        Assert.StartsWith("removed " + EnvironmentInventory.LocalHost + ":" + Copy(result, "runtime"), Copy(result, "runtimeCopy"));
        Assert.True(long.Parse(Copy(result, "outputBytes")) > 0);
        Assert.True(long.Parse(Copy(result, "freeBytesBeforeCopies")) > 0);
    }
    [Fact] public async Task ValidateAndAFailedScenarioRemoveTheProfileToo()
    {
        string plan = WritePlan();
        Assert.Equal(0, await RunHere(["validate", plan, Output], Options()));
        Assert.False(Directory.Exists(OnHost(Copy(Result(), "runtime"))));
        Assert.EndsWith("nothing was launched", Copy(Result(), "runtimeCopy"));
        Assert.False(Directory.Exists(Path.Combine(Output, "runtime-changes")));
        Directory.Delete(Output, true);
        var server = new FakeOwnedServer("test.mod");
        Assert.Equal(1, await RunHere(["run", plan, Output], Options(_ => throw new InvalidOperationException("fixture refused"), server)));
        var result = Result();
        Assert.False(Directory.Exists(OnHost(Copy(result, "runtime")))); Assert.True(Directory.Exists(Copy(result, "world")));
    }
    [Fact] public async Task ARuntimeIsKeptWhenAskedOrWhenItsServerMayStillRun()
    {
        string plan = WritePlan();
        RunRetirement.KeepOverride.Value = true; // VALHEIM_TESTING_KEEP_RUNTIME=1, in this test's flow only
        try { Assert.Equal(0, await RunHere(["run", plan, Output], Options(server: new FakeOwnedServer("test.mod")))); }
        finally { RunRetirement.KeepOverride.Value = null; }
        var kept = Result();
        Assert.True(Directory.Exists(OnHost(Copy(kept, "runtime")))); Assert.Contains("kept on request", Copy(kept, "runtimeCopy"));
        Assert.False(Directory.Exists(Path.Combine(Output, "runtime-changes")));

        string refused = Output + "-refused";
        var stubborn = new FakeOwnedServer("test.mod") { RefuseStop = true };
        Assert.Equal(1, await RunHere(["run", plan, refused], Options(server: stubborn)));
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(refused, "result.json"))).RootElement;
        Assert.True(Directory.Exists(OnHost(Copy(result, "runtime"))));
        Assert.Contains("may still run", Copy(result, "runtimeCopy"));
        stubborn.RefuseStop = false;
    }
    // #869: a standalone run reads its fixture as one complete world before the host is touched, the profile staged or the world copied.
    [Theory]
    [InlineData("incomplete")]
    [InlineData("pre-1.0")]
    [InlineData("unwrapped")]
    public async Task AWorldFixtureThatIsNotOneCompleteWorldIsRefusedBeforeAnythingIsStaged(string shape)
    {
        string plan = WritePlan();
        string worlds = Path.Combine(World, "worlds_local");
        if (shape == "incomplete") File.Delete(Path.Combine(worlds, "Test", "_main.1.ok"));
        else if (shape == "pre-1.0") { File.WriteAllText(Path.Combine(worlds, "Test.fwl"), "world"); File.WriteAllText(Path.Combine(worlds, "Test.db"), "world"); }
        else Directory.Move(worlds, Path.Combine(World, "moved")); // the world folder not in worlds_local/
        Assert.Equal(1, await RunHere(["validate", plan, Output], Options()));
        Assert.False(Directory.Exists(Output)); // no world copy, no evidence
        Assert.Empty(_machine.Host.Scripts); // the host was never touched: no lock, no profile
    }

    [Fact] public async Task AFullDriveIsRefusedBeforeAnythingIsCopied()
    {
        string plan = WritePlan();
        DiskSpace.AvailableOverride = _ => 1024;
        try { Assert.Equal(1, await RunHere(["validate", plan, Output], Options())); }
        finally { DiskSpace.AvailableOverride = null; }
        var step = Result().GetProperty("Steps").EnumerateArray().First(step => !step.GetProperty("Passed").GetBoolean());
        Assert.Equal("enough free disk space for the copies", step.GetProperty("Name").GetString());
        Assert.False(step.GetProperty("Passed").GetBoolean());
        Assert.Contains("Not enough free disk space for this run's world copy", step.GetProperty("Error").GetString());
        Assert.Empty(Directory.GetDirectories(Output, "valheim-test-*"));
    }
    [Fact] public async Task APlanTheModRefusesIsRefusedBeforeCopying()
    {
        string plan = WritePlan();
        int code = await RunHere(["validate", plan, Output], Options(checkPlan: _ => throw new ArgumentException("smoke plans run only")));
        Assert.Equal(1, code); Assert.False(Directory.Exists(Output));
    }
    [Fact] public void ADedicatedServerWaitsOnThisBootsLogAndTheLoadedWorldPush()
    {
        string runtime = Path.Combine(Path.GetTempPath(), "runtime");
        var events = new ServerRunPlan { Port = 5591 }.DedicatedStartupEvents(runtime);
        Assert.Equal(Path.Combine(runtime, "BepInEx", "LogOutput.log"), events.CliLog);
        Assert.Equal(new[] { StateWait.InWorldNoPlayer }, events.ReadyStates);
        Assert.NotNull(events.States);
        Assert.Matches(events.Listening, "[Info   :valheimCLI] Command server listening on 127.0.0.1:5591");
        Assert.Same(StartupEvents.StartupFailures, events.Failures);
    }
    [Fact] public async Task ALeftoverPatcherFailsValidationByThePatchersPin()
    {
        WritePlan(); var pinned = InstallPins.Of(Runtime); // The runtime pinned clean, with no patchers.
        Directory.CreateDirectory(Path.Combine(Runtime, "BepInEx", "patchers"));
        File.WriteAllText(Path.Combine(Runtime, "BepInEx", "patchers", "RemovedMod.Preloader.dll"), "patcher");
        // Its manifest is regenerated with the leftover in it, so only the pin can tell.
        Assert.Equal(1, await RunHere(["validate", WritePlan(runtimePins: pinned), Output], Options()));
        // The install is checked against the plan before its profile is staged.
        var step = Assert.Single(Result().GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "check the server host's install against the pinned runtime and stage its profile");
        Assert.DoesNotContain("profile-seed", _machine.Host.Scripts);
        Assert.False(step.GetProperty("Passed").GetBoolean()); Assert.Contains("holding RemovedMod.Preloader.dll", step.GetProperty("Error").GetString());
    }
    // A scenario-registered log (an owned client's) is scanned with the run's at teardown: a patch on a method that does
    // not exist fails a passing scenario, unless the plan reclassifies it with a reason.
    private const string BrokenPatchLog = "[Message:   BepInEx] BepInEx 5.4.23.2\n[Error  :   BepInEx] Error loading [Broken Mod 1.0.0] : Exception has been thrown by the target of an invocation.\n" +
        "System.ArgumentException: Undefined target method for patch method static System.Void BrokenMod.Patches::Postfix()\n";
    private Func<TestRun<ServerRunPlan>, Task> RegistersClientLog(string text) => run =>
    {
        string path = Path.Combine(run.Output, "client-boot.game-0.log"); File.WriteAllText(path, text);
        run.GameSession.AddLog(new RunLog("client BepInEx log", path, Required: true));
        return Task.CompletedTask;
    };
    [Fact] public async Task TheTeardownScanFailsAPassingScenarioOnABrokenPatch()
    {
        string plan = WritePlan();
        var server = new FakeOwnedServer("test.mod");
        Assert.Equal(1, await RunHere(["run", plan, Output], Options(RegistersClientLog(BrokenPatchLog), server)));
        Assert.Contains("stop1", server.Events);
        var result = Result();
        var scan = Assert.Single(result.GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "scan run logs");
        Assert.False(scan.GetProperty("Passed").GetBoolean());
        Assert.Contains("client BepInEx log: harmony-undefined-target x1, first at line 3", scan.GetProperty("Error").GetString());
        Assert.Equal("client BepInEx log", Assert.Single(result.GetProperty("Logs").EnumerateArray()).GetProperty("Role").GetString());
    }
    [Fact] public async Task ACleanLogOrAReclassifiedPatternPassesTheScan()
    {
        string plan = WritePlan();
        Assert.Equal(0, await RunHere(["run", plan, Output], Options(RegistersClientLog("[Message:   BepInEx] BepInEx 5.4.23.2\n"), new FakeOwnedServer("test.mod"))));
        Assert.Contains(Result().GetProperty("Steps").EnumerateArray(), s => s.GetProperty("Name").GetString() == "scan run logs" && s.GetProperty("Passed").GetBoolean());
        plan = WritePlan(logScan: new() { ["harmony-undefined-target"] = new { severity = "Warning", reason = "The broken patch is the mod's known, reported issue." } });
        string output = Output + "-reclassified";
        Assert.Equal(0, await RunHere(["run", plan, output], Options(RegistersClientLog(BrokenPatchLog), new FakeOwnedServer("test.mod"))));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json")));
        var count = result.RootElement.GetProperty("Logs")[0].GetProperty("Counts").EnumerateArray().Single(c => c.GetProperty("Pattern").GetString() == "harmony-undefined-target");
        Assert.Equal(1, count.GetProperty("Count").GetInt32()); Assert.Equal("Warning", count.GetProperty("Severity").GetString());
        Assert.Equal("The broken patch is the mod's known, reported issue.", count.GetProperty("Reason").GetString());
    }
    [Fact] public async Task AnUnknownLogScanNameIsRefusedBeforeCopying()
    {
        string plan = WritePlan(logScan: new() { ["no-such-pattern"] = new { severity = "Warning", reason = "x" } });
        Assert.Equal(1, await RunHere(["validate", plan, Output], Options()));
        Assert.False(Directory.Exists(Output));
    }
    [Fact] public void TheGenericPlanRulesHold()
    {
        var plan = new ServerRunPlan
        {
            Runtime = new() { Source = Path.GetTempPath(), Sha256 = new() { ["a"] = new string('a', 64) } },
            World = new() { Source = Path.GetTempPath(), Sha256 = new() { ["b"] = new string('b', 64) } },
            Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"], Pins = new() { ["worlduid"] = "1", ["my.mod"] = new string('1', 32) },
            RuntimePins = new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) },
        };
        plan.ValidateServerPlan(["my.mod"], "TOKEN");
        plan.ValidateServerPlan(["other.mod"], "TOKEN"); // Omitted plugin pins are bound from the staged profile.
        plan.Environment["TOKEN"] = "x"; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")); plan.Environment.Clear();
        plan.Environment["doorstop_enabled"] = "0"; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")); plan.Environment.Clear();
        plan.Arguments = ["-batchmode", "-savedir", "{world}"]; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN"));
        plan.Arguments = ["-batchmode", "-nographics", "-savedir", "{world}", "-savedir", "{world}"]; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN"));
        plan.Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"];
        plan.Pins["worldfiles"] = "x"; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")); plan.Pins.Remove("worldfiles");
        plan.Pins["loose.mod"] = "any"; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN"));
    }
}

// VALHEIM_TESTING_KEEP_RUNTIME is process-wide: this test runs alone and restores it.
[CollectionDefinition(nameof(KeepRuntimeVariable), DisableParallelization = true)]
public sealed class KeepRuntimeVariable { }

[Collection(nameof(KeepRuntimeVariable))]
public sealed class KeepRuntimeVariableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keep-runtime-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact] public async Task TheVariableKeepsTheRuntimeForAnyRunner()
    {
        var machine = new LocalServerMachine(Path.Combine(_root, "local-host"));
        string runtime = machine.InstallDirectory, world = Path.Combine(_root, "world"), output = Path.Combine(_root, "out");
        FakeInstalls.World(Path.Combine(world, "worlds_local"), "Test", 1);
        string plan = Path.Combine(_root, "plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["scenario"] = "smoke", ["runtime"] = new { source = LocalServerMachine.Install, sha256 = WorldFixture.Manifest(runtime) },
            ["world"] = new { source = world, sha256 = WorldFixture.Manifest(world) },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}" },
            ["pins"] = new Dictionary<string, string> { ["worlduid"] = "1" }, ["port"] = 5577, ["runtimePins"] = InstallPins.Of(runtime),
        }));
        var options = new PinnedServerRunOptions<ServerRunPlan>
        {
            Name = "toolkit-smoke", Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
            ReadPlan = path => { var read = ServerRunPlan.Read<ServerRunPlan>(path); read.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return read; },
            Scenario = (_, _) => Task.CompletedTask, Hooks = machine.Hooks(),
        };
        string? previous = Environment.GetEnvironmentVariable(PinnedServerRun.KeepRuntimeVariable);
        Environment.SetEnvironmentVariable(PinnedServerRun.KeepRuntimeVariable, "1");
        try { using (LocalServerMachine.Use()) Assert.Equal(0, await PinnedServerRun.MainAsync(["validate", plan, output], options)); }
        finally { Environment.SetEnvironmentVariable(PinnedServerRun.KeepRuntimeVariable, previous); }
        var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement.GetProperty("Provenance");
        Assert.True(Directory.Exists(machine.Host.Local(provenance.GetProperty("runtime").GetString()!)));
        Assert.Contains("kept on request", provenance.GetProperty("runtimeCopy").GetString());
    }
}
