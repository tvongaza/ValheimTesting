using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// Plan rules for crossplay servers and clients and for hosting clients, the runner's handling of the crossplay option,
// and ClientRounds joining a crossplay server's lobby. No game.
public sealed class CrossplayPlanTests : IDisposable
{
    private const string Token = "TEST_SESSION_TOKEN";
    private readonly string _output = Directory.CreateTempSubdirectory("crossplay-rounds-").FullName;
    public void Dispose() => Directory.Delete(_output, recursive: true);

    private static ServerRunPlan Server(params string[] extra) => new()
    {
        Runtime = new() { Source = Path.GetTempPath(), Sha256 = new() { ["a"] = new string('a', 64) } },
        World = new() { Source = Path.GetTempPath(), Sha256 = new() { ["b"] = new string('b', 64) } },
        Arguments = ["-batchmode", "-nographics", "-savedir", "{world}", .. extra], Pins = new() { ["worlduid"] = "1" },
        RuntimePins = new() { Game = new string('c', 64), BepInExCore = new string('d', 64), Patchers = new string('e', 64) },
    };

    [Theory] [InlineData("-crossplay")] [InlineData("-Crossplay")] [InlineData("-CROSSPLAY")]
    public void CrossplayComesOnlyFromThePlanOption(string argument)
    {
        foreach (bool crossplay in new[] { false, true })
        {
            var plan = Server(argument, "-port", "2466"); plan.Crossplay = crossplay;
            var error = Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], Token));
            Assert.Contains("\"crossplay\": true", error.Message);
        }
    }

    [Fact] public void ACrossplayPlanNamesExactlyOneGamePort()
    {
        var plan = Server("-port", "2466"); plan.Crossplay = true;
        plan.ValidateServerPlan([], Token); // The control: a named port passes.
        foreach (var ports in new string[][] { [], ["-port"], ["-port", "{port}"], ["-port", "2466", "-port", "2467"], ["-port", "80"], ["-port", "+2466"], ["-port", "70000"] })
        {
            plan.Arguments = ["-batchmode", "-nographics", "-savedir", "{world}", .. ports];
            var error = Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], Token));
            Assert.Contains("public IP", error.Message);
        }
        // A Steam server keeps the game's default port.
        plan.Crossplay = false; plan.Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"];
        plan.ValidateServerPlan([], Token);
    }

    [Fact] public void TheLaunchAddsCrossplayOnlyForACrossplayPlan()
    {
        var plan = Server("-port", "2466", "-logFile", "{runtime}/toolkit-unity.log");
        string[] expanded = ["-batchmode", "-nographics", "-savedir", "/w", "-port", "2466", "-logFile", "/r/toolkit-unity.log"];
        Assert.Equal(expanded, plan.LaunchArguments("/r", "/w"));
        plan.Crossplay = true;
        Assert.Equal(expanded.Append("-crossplay").ToArray(), plan.LaunchArguments("/r", "/w"));
    }

    [Fact] public async Task TheRunnerRefusesCrossplayInTheArgumentsOfEveryPlanAndRecordsTheOption()
    {
        string root = Directory.CreateTempSubdirectory("crossplay-plan-").FullName;
        try
        {
            string runtime = Path.Combine(root, "runtime"), world = Path.Combine(root, "world");
            Directory.CreateDirectory(runtime); Directory.CreateDirectory(world);
            File.WriteAllText(Path.Combine(runtime, ServerLaunch.LinuxExecutable), "server"); File.WriteAllText(Path.Combine(world, "Test.db"), "world");
            FakeInstalls.Server(runtime);
            string Plan(string name, bool crossplay, params string[] extra)
            {
                string path = Path.Combine(root, name);
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    scenario = "smoke", runtime = new { source = runtime, sha256 = WorldFixture.Manifest(runtime) }, world = new { source = world, sha256 = WorldFixture.Manifest(world) },
                    arguments = new[] { "-batchmode", "-nographics", "-savedir", "{world}" }.Concat(extra).ToArray(), pins = new Dictionary<string, string> { ["worlduid"] = "1" },
                    runtimePins = InstallPins.Of(runtime), crossplay,
                }));
                return path;
            }
            // A mod whose ReadPlan forgets ValidateServerPlan still cannot pass -crossplay around the option.
            var options = new PinnedServerRunOptions<ServerRunPlan>
            {
                Name = "crossplay", SessionCapability = "test.mod/session", SessionTokenVariable = Token,
                ReadPlan = ServerRunPlan.Read<ServerRunPlan>, Scenario = _ => Task.CompletedTask,
            };
            string refused = Path.Combine(root, "out-refused");
            Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", Plan("raw.json", false, "-crossplay"), refused], options));
            Assert.False(Directory.Exists(refused)); // Refused before anything was copied.
            foreach (bool crossplay in new[] { true, false })
            {
                string output = Path.Combine(root, "out-" + crossplay);
                Assert.Equal(0, await PinnedServerRun.MainAsync(["validate", Plan(crossplay + ".json", crossplay, "-port", "2466"), output], options));
                var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement;
                Assert.Equal(crossplay ? "true" : "false", result.GetProperty("Provenance").GetProperty("crossplay").GetString());
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static ClientRunPlan Client() => new()
    {
        Mode = "attach", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
    };

    [Fact] public void ACrossplayClientJoinsNoAddressAndPassesNoPassword()
    {
        var plan = Client(); plan.Crossplay = true;
        Assert.Throws<ArgumentException>(() => plan.Validate()); // It still names an address.
        plan.Join = "";
        plan.Validate();
        plan.PasswordVariable = "SERVER_PASSWORD";
        Assert.Contains("password", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
        // The control: a client that joins by address still needs one.
        plan.PasswordVariable = null; plan.Crossplay = false;
        Assert.Throws<ArgumentException>(() => plan.Validate());
    }

    private static HostWorldPlan Host() => new()
    {
        World = new() { Source = Path.GetFullPath("host-fixture"), Sha256 = new() { ["HostFixture.fwl"] = new string('a', 64), ["HostFixture.db"] = new string('b', 64) } },
        WorldUid = "4242",
    };
    private static ClientRunPlan HostingClient(Action<ClientRunPlan>? change = null)
    {
        var plan = Client(); plan.Join = ""; plan.HostWorld = Host();
        change?.Invoke(plan);
        return plan;
    }

    [Fact] public void AHostingClientPlanIsValidatedLikeTheRest()
    {
        HostingClient().Validate(); // The control.
        foreach (var change in new Action<ClientRunPlan>[]
        {
            p => p.Join = "127.0.0.1:2456", p => p.PasswordVariable = "SERVER_PASSWORD", p => p.Crossplay = true, p => p.Host = "client.example",
            p => p.HostWorld!.WorldUid = "", p => p.HostWorld!.WorldUid = "fixture", p => p.HostWorld!.World.Sha256.Clear(),
            p => p.HostWorld!.World.Sha256["Other.fwl"] = new string('c', 64), p => p.HostWorld!.World.Source = "host-fixture",
            p => p.HostWorld!.SaveDirectory = "client-data", p => p.HostWorld!.SaveSeconds = 0, p => p.HostWorld!.SaveSeconds = 601,
            p => p.Pins.Remove("valheimCLI.valheimCLI"),
        })
            Assert.Throws<ArgumentException>(() => HostingClient(change).Validate());
        // Explicitly unpinned: no pins and no fixture hashes, but still the exact world UID.
        var unpinned = HostingClient(p => { p.Pinning = "none"; p.Pins.Clear(); p.HostWorld!.World.Sha256.Clear(); });
        unpinned.Validate();
        unpinned.HostWorld!.WorldUid = "";
        Assert.Throws<ArgumentException>(() => unpinned.Validate());
    }

    public sealed class ClientPlan : ServerRunPlan { public ClientRunPlan? Client { get; set; } }

    [Theory]
    [InlineData("{\"client\":{\"mode\":\"attach\",\"hostWorld\":{\"worldUid\":\"1\",\"wolrd\":{}}}}")]
    [InlineData("{\"client\":{\"mode\":\"attach\",\"crosplay\":true}}")]
    public void UnknownFieldsInTheNewSectionsAreRefused(string json)
    {
        string file = Path.GetTempFileName();
        try { File.WriteAllText(file, json); Assert.Throws<JsonException>(() => ServerRunPlan.Read<ClientPlan>(file)); }
        finally { File.Delete(file); }
    }

    [Fact] public void TheNewSectionsRead()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{\"crossplay\":true,\"client\":{\"mode\":\"attach\",\"crossplay\":true,\"hostWorld\":{\"worldUid\":\"1\",\"crossplay\":true,\"saveSeconds\":30,\"world\":{\"source\":\"/x\"}}}}");
            var plan = ServerRunPlan.Read<ClientPlan>(file);
            Assert.True(plan.Crossplay); Assert.True(plan.Client!.Crossplay);
            Assert.Equal("1", plan.Client.HostWorld!.WorldUid); Assert.True(plan.Client.HostWorld.Crossplay); Assert.Equal(30, plan.Client.HostWorld.SaveSeconds);
        }
        finally { File.Delete(file); }
    }

    // ---- ClientRounds with a crossplay client ----

    private const string WorldUid = "4242";
    private static GameActor RoundServer() => new ScriptedTransport().Saves()
        .Extension("test.mod", "session", _ => new { source = "owned-test-session", complete = true, acceptingConnections = true })
        .Actor("server", "cli_expect worlduid=" + WorldUid);

    private static ScriptedTransport CrossplayClient()
    {
        bool devcommands = false, joined = false;
        return new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Tester' (tester, Local)"))
            .OnPrefix("cli_connect_playfab_user ", command => { joined = true; return ScriptedTransport.Ok($"OK: PlayFab user join started for {command.Split(' ')[1]} using character 'Tester' (tester, Local)"); })
            .Extension("valheim.session", "leave", _ => { joined = false; return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = joined ? "world-present" : "menu", worldUid = joined ? WorldUid : null, worldPresent = joined,
                worldReady = joined, server = false, dedicated = false, localPlayer = joined, playerReady = joined, saving = false, loadError = false,
                connectionStatus = joined ? "Connected" : "None",
            })
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"));
    }

    private ClientRounds Rounds(ScenarioReport report, ClientRunPlan plan, Func<GameActor, CrossplayLobby>? lobby) => new()
    {
        Client = plan, WorldUid = WorldUid, Report = report, Output = _output, Arrival = null, Lobby = lobby,
        WaitUntilJoinable = server => OwnedServerSession.WaitUntilJoinable(server, "test.mod/session", TimeSpan.FromSeconds(5)),
        RestartServer = RoundServer,
    };

    [Fact] public void CrossplayRoundsJoinEachBootsLobbyAndRecordTheJoin()
    {
        var plan = Client(); plan.Join = ""; plan.Crossplay = true;
        var client = CrossplayClient(); var report = new ScenarioReport("crossplay");
        var servers = new List<GameActor>();
        Rounds(report, plan, server => { servers.Add(server); return new CrossplayLobby("ENTITY" + servers.Count, "LOBBY" + servers.Count); })
            .Run(RoundServer(), () => ClientSession.Attach(plan, _output, client), _ => { });

        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Contains("first: the server's crossplay lobby is open", report.Steps.Select(s => s.Name));
        Assert.Contains("after-restart: join the owned server's crossplay lobby with the disposable character, protected", report.Steps.Select(s => s.Name));
        Assert.Equal(2, servers.Distinct().Count()); // Each boot's own lobby.
        Assert.Contains("cli_connect_playfab_user ENTITY1", client.Commands);
        Assert.Contains("cli_connect_playfab_user ENTITY2", client.Commands);
        Assert.Equal(0, client.Count("cli_extension valheim.session/join"));
        Assert.Equal(2, client.Count("cli_set_player_safety"));
        Assert.Equal("crossplay", report.Provenance["clientJoin"]);
    }

    [Fact] public void ACrossplayClientWithoutALobbyOrAHostingClientIsRefusedBeforeTheClientOpens()
    {
        int opens = 0;
        var crossplay = Client(); crossplay.Join = ""; crossplay.Crossplay = true;
        foreach (var plan in new[] { crossplay, HostingClient() })
        {
            var error = Assert.Throws<ArgumentException>(() => Rounds(new ScenarioReport("refused"), plan, lobby: null)
                .Run(RoundServer(), () => { opens++; throw new InvalidOperationException("opened"); }, _ => { }));
            Assert.Contains(plan.Crossplay ? "Lobby" : "HostRounds", error.Message);
        }
        Assert.Equal(0, opens);
    }
}
