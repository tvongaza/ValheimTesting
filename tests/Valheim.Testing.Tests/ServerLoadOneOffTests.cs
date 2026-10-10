using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// server-load as the one-off: the actors come from the inventory (this machine with no file), the derived campaign is
// written into the output, the host preflight runs before anything is copied, and nothing has to be written by hand.
public sealed class ServerLoadOneOffTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    private readonly IDisposable _preflight = LocalHostPreflight.ReplaceDefaultProbesForTest(new(
        Processes: (_, _, _, _) => Task.CompletedTask,
        Port: (_, _, _, _) => Task.CompletedTask,
        Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
        Desktop: _ => Task.CompletedTask, MacDesktop: () => { }, SteamRunning: () => true,
        Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null));
    public void Dispose() { _preflight.Dispose(); _rig.Dispose(); }

    private static readonly CampaignPreflightReport Ready = new([]);
    [Fact]
    public async Task ModOwnedScenarioUsesThePreparedCampaignWithoutAnotherCopy()
    {
        string output = Path.Combine(_rig.Root, "mod-scenario");
        PinnedServerRunOptions<ServerRunPlan>? given = null;
        int result = await ServerLoad.RunAsync(Arguments(output, "--server-only", "--server-startup-seconds", "900",
            "--scenario", typeof(SampleOneShotServerScenario).Assembly.Location),
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
                Campaign: (_, _, _, _, options) => { given = options; return Task.FromResult(0); }));
        Assert.Equal(0, result);
        Assert.NotNull(given);
        Assert.True(File.Exists(Path.Combine(output, "scenario", "source.json")));
        Assert.True(File.Exists(Path.Combine(output, "campaign.json")));
        Assert.Equal(900, JsonSerializer.Deserialize<ServerRunPlan>(File.ReadAllText(Path.Combine(output, "plan.json")))!.StartupSeconds);
        Assert.Equal("sample-one-shot", OneShotScenario.Load(typeof(SampleOneShotServerScenario).Assembly.Location,
            Path.Combine(_rig.Root, "separate-selection")).Runner.Name);
    }

    [Fact]
    public void HostedAndDedicatedScenariosSelectTheirOwnContract()
    {
        string file = typeof(SampleOneShotServerScenario).Assembly.Location;
        Assert.Equal("sample-one-shot", OneShotScenario.Load(file,
            Path.Combine(_rig.Root, "dedicated-selection")).Runner.Name);
        Assert.Equal("sample-hosted-one-shot", OneShotScenario.LoadHosted(file,
            Path.Combine(_rig.Root, "hosted-selection")).Runner.Name);
        Assert.True(StartArguments.TryRead(["--project", "mod.csproj", "--scenario-project", "test.csproj",
            "--dependency", "Jotunn=Jotunn.dll"], out _, out _, out _, out _, out var dependencies, out string error,
            allowImplicitMod: true), error);
        Assert.Equal(["Jotunn=Jotunn.dll"], dependencies);
        Assert.True(ServerLoad.TryRead(["--project", "mod.csproj", "--scenario-project", "test.csproj",
            "--adapter-project", "adapter.csproj", "--adapter-property", "RoadsDll={mod}",
            "--session-capability", "roads.testing/session", "--session-token-variable", "ROADS_TEST_SESSION_TOKEN"], out var server, out error,
            allowImplicitMod: true), error);
        Assert.Equal(["RoadsDll={mod}"], server!.AdapterProperties);
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--adapter-project", "adapter.csproj"], out _, out error));
        Assert.Contains("--session-capability", error);
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--adapter", "adapter.dll",
            "--adapter-project", "adapter.csproj"], out _, out error));
        Assert.Contains("Choose --adapter", error);
    }

    [Fact]
    public async Task ScenarioWithTheWrongClientShapeRefusesBeforeCampaign()
    {
        string output = Path.Combine(_rig.Root, "wrong-client-shape");
        bool launched = false;
        using (EnvironmentInventory.UseMachine(WithValheim(out _)))
        {
            int result = await ServerLoad.RunAsync(Arguments(output, "--scenario", typeof(SampleOneShotServerScenario).Assembly.Location),
                new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
                    Campaign: (_, _, _, _, _) => { launched = true; return Task.FromResult(0); },
                    ClientArchitecture: (_, _, _, _) => { }));
            Assert.Equal(3, result);
        }
        Assert.False(launched);
        Assert.Contains("--server-only", File.ReadAllText(Path.Combine(output, "REFUSED.txt")));
    }
    [Fact]
    public void JoinedClientArchitectureOverridesTheInventoryAndServerOnlyRejectsIt()
    {
        var recipe = new EnvironmentRecipe { Host = "local", Architecture = "x64" };
        var inventory = new EnvironmentInventory { Hosts = new() { ["local"] = new HostProfile { Kind = "local", Platform = "macos" } } };
        Assert.Equal("arm64", SmokeInputResolver.SelectClientArchitecture("arm64", recipe, inventory));
        Assert.Equal("x64", SmokeInputResolver.SelectClientArchitecture(null, recipe, inventory));
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--client-architecture", "arm64"], out var parsed, out _));
        Assert.Equal("arm64", parsed!.Options["--client-architecture"]);
        Assert.False(ServerLoad.TryRead(["--mod", "a.dll", "--server-only", "--client-architecture", "arm64"], out _, out _));
        Assert.False(ServerLoad.TryRead(["--mod", "a.dll", "--client-architecture", "native"], out _, out _));
    }

    [Fact]
    public void Arm64SelectionRefusesANonMacClientBeforeAnyLoaderCheck()
    {
        var recipe = new EnvironmentRecipe { Host = "local", Architecture = "x64" };
        var inventory = new EnvironmentInventory { Hosts = new() { ["local"] = new HostProfile { Kind = "local", Platform = "linux" } } };
        Assert.Contains("macOS", Assert.Throws<ArgumentException>(() =>
            SmokeInputResolver.SelectClientArchitecture("arm64", recipe, inventory)).Message);
    }
    private string Adapter() => _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
        RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(SmokeSessionContract.SessionAdapterPluginGuid)));

    // The rig's game plays the dedicated server install: its managed assemblies and BepInEx core resolve the mod.
    private string[] Arguments(string output, params string[] more)
    {
        _rig.Write("game/" + ServerRunPlan.ExecutableFor(HostProfile.CurrentPlatform switch
        {
            "windows" => ServerPlatform.Windows, "macos" => ServerPlatform.MacOS, _ => ServerPlatform.Linux,
        }), Encoding.UTF8.GetBytes("fake dedicated executable"));
        return ["--server", _rig.Game, "--mod", _rig.Parent, "--cli-manifest", _rig.CliManifest(save: true, full: true), "--cli-files", Path.Combine(_rig.Root, "cli"),
            "--search-root", Path.Combine(_rig.Root, "deps"), "--adapter", Adapter(), "--output", output, .. more];
    }

    // This machine with Valheim installed (a fake one, of this OS), so the default client is found.
    private static FakeMachine WithValheim(out string game)
    {
        var machine = new FakeMachine(HostProfile.CurrentPlatform);
        string steam = machine.Platform switch
        {
            "windows" => @"C:\Program Files (x86)\Steam", "macos" => "/Users/tester/Library/Application Support/Steam", _ => "/home/tester/.local/share/Steam",
        };
        machine.Directories.Add(steam);
        machine.Directories.Add(HostPath.Join(steam, "userdata"));
        game = machine.App(steam, "892970", "Valheim", machine.Platform switch
            { "windows" => GameLaunch.ClientWindowsExecutable, "macos" => "Valheim.app/Contents/MacOS/Valheim", _ => GameLaunch.ClientLinuxExecutable });
        return machine;
    }

    [Fact]
    public async Task NamedInventoryChoiceIsFrozenForTheDerivedCampaign()
    {
        var machine = WithValheim(out string clientInstall);
        string inventoryFile = Path.Combine(_rig.Root, "chosen-inventory.json");
        File.WriteAllText(inventoryFile, JsonSerializer.Serialize(new
        {
            environments = new object[]
            {
                new { name = "server-choice", roles = new[] { "server" }, install = _rig.Game },
                new { name = "first-client", roles = new[] { "client" }, install = clientInstall },
                new { name = "chosen-client", roles = new[] { "client" }, install = clientInstall },
            },
        }));
        string output = Path.Combine(_rig.Root, "frozen-choice");
        var args = Arguments(output).Skip(2).Concat(["--inventory", inventoryFile,
            "--client-env", "chosen-client", "--preflight-only"]).ToArray();
        using (EnvironmentInventory.UseMachine(machine))
            Assert.Equal(0, await ServerLoad.RunAsync(args, new ServerLoad.Seams(
                Inspect: _ => Task.FromResult(Ready),
                ClientArchitecture: (_, _, _, _) => { })));

        string frozenFile = Path.Combine(output, "environments.json");
        var campaign = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "campaign.json"))).RootElement;
        Assert.Equal(frozenFile, campaign.GetProperty("inventory").GetString());
        Assert.Equal(["server-choice", "chosen-client"],
            EnvironmentInventory.Read(frozenFile, machine).Environments.Select(recipe => recipe.Name));
        File.WriteAllText(inventoryFile, "{ broken source inventory");
        Assert.Equal(["server-choice", "chosen-client"],
            EnvironmentInventory.Read(frozenFile, machine).Environments.Select(recipe => recipe.Name));
    }

    [Fact]
    public async Task HoldNeedsAnActualRunRatherThanPreflightOnly()
    {
        string output = Path.Combine(_rig.Root, "hold-refused");
        Assert.Equal(2, await ServerLoad.RunAsync(Arguments(output, "--preflight-only", "--hold")));
        Assert.False(Path.Exists(output));
        Assert.True(ServerLoad.TryRead(Arguments(Path.Combine(_rig.Root, "hold-accepted"), "--server-only", "--hold"),
            out var parsed, out string error), error);
        Assert.Contains("--hold", parsed!.Switches);
    }

    [Fact]
    public async Task ServerOnlyCanPrepareACustomPinnedWorldWithoutChangingItsSource()
    {
        string source = Path.Combine(_rig.Root, "chosen-world");
        DefaultSmokeWorld.PrepareServerSaveRoot(source);
        var hashes = WorldFixture.Manifest(Path.Combine(source, "worlds_local"));
        string output = Path.Combine(_rig.Root, "custom-world-run");
        Assert.Equal(0, await ServerLoad.RunAsync(Arguments(output, "--server-only", "--world-fixture", source,
            "--preflight-only"), new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready))));
        var campaign = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "campaign.json"))).RootElement;
        Assert.Equal(DefaultSmokeWorld.Uid, campaign.GetProperty("worldUid").GetString());
        Assert.Equal(Path.Combine(output, "world-source", "worlds_local"), campaign.GetProperty("world").GetString());
        WorldFixture.Verify(Path.Combine(source, "worlds_local"), hashes);
        WorldFixture.Verify(Path.Combine(output, "world-source", "worlds_local"), hashes);
    }

    [Fact]
    public async Task JoinedClientCanPrepareACustomPinnedWorldWithAnUnvisitedDisposableCharacter()
    {
        string source = Path.Combine(_rig.Root, "joined-world");
        DefaultSmokeWorld.PrepareServerSaveRoot(source);
        var hashes = WorldFixture.Manifest(Path.Combine(source, "worlds_local"));
        string output = Path.Combine(_rig.Root, "joined-custom-world-run");
        using (EnvironmentInventory.UseMachine(WithValheim(out _)))
            Assert.Equal(0, await ServerLoad.RunAsync(Arguments(output, "--world-fixture", source,
                "--server-startup-seconds", "3600", "--preflight-only"),
                new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
                    ClientArchitecture: (_, _, _, _) => { })));
        var campaign = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "campaign.json"))).RootElement;
        Assert.Equal(DefaultSmokeWorld.Uid, campaign.GetProperty("worldUid").GetString());
        Assert.Equal(DefaultSmokeCharacter.Name,
            campaign.GetProperty("clients").GetProperty("client").GetProperty("character").GetProperty("registeredName").GetString());
        Assert.Equal(3600, JsonSerializer.Deserialize<ServerRunPlan>(File.ReadAllText(Path.Combine(output, "plan.json")))!.StartupSeconds);
        WorldFixture.Verify(Path.Combine(source, "worlds_local"), hashes);
        WorldFixture.Verify(Path.Combine(output, "world-source", "worlds_local"), hashes);
    }

    [Fact]
    public async Task BakeRefusesAnExistingTargetBeforeItPreparesAnything()
    {
        string output = Path.Combine(_rig.Root, "bake-refusal-run");
        string baked = Path.Combine(_rig.Root, "existing-bake");
        Directory.CreateDirectory(baked);
        Assert.Equal(3, await ServerLoad.RunAsync(Arguments(output, "--server-only", "--bake-fixture", baked,
            "--assert-command", "cli_world", "--assert-line", "WORLD"),
            new ServerLoad.Seams(Inspect: _ => throw new Exception("preflight must not run"))));
        Assert.False(Path.Exists(output));
        Assert.True(Directory.Exists(baked));
    }

    [Fact]
    public void BakeRequiresAnExplicitGeneratedStateAssertion()
    {
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--server-only", "--bake-fixture", "new-fixture"], out _, out string error));
        Assert.Contains("--assert-command", error);
        Assert.True(ServerLoad.TryRead(["--mod", "mod.dll", "--server-only", "--bake-fixture", "new-fixture",
            "--assert-command", "cli_zdos_at 1 2 8", "--assert-line", "ZDO wood_pole2"], out _, out error), error);
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--server-only", "--bake-fixture", "new-fixture",
            "--assert-command", "cli_zdos_at 1 2 8", "--assert-line", " "], out _, out error));
        Assert.Contains("Option needs a value: --assert-line", error);
    }

    [Fact]
    public async Task BakeAllowsTheFinalWorldSaveToFinishAtQuit()
    {
        string output = Path.Combine(_rig.Root, "bake-quit-budget");
        string[] args = Arguments(output, "--server-only", "--bake-fixture", Path.Combine(_rig.Root, "baked"),
            "--assert-command", "cli_zdos_at 1 2 8", "--assert-line", "ZDO");
        Assert.Equal(1, await ServerLoad.RunAsync(args, new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
            Campaign: (_, plan, _, _, options) =>
            {
                Assert.Equal(300, plan.QuitSeconds);
                Assert.Equal(TimeSpan.FromMinutes(10), options.CleanupBudget);
                return Task.FromResult(1);
            })));
        Assert.Equal(300, ServerRunPlan.Read<ServerRunPlan>(Path.Combine(output, "plan.json")).QuitSeconds);
    }

    [Fact]
    public async Task PostRunExportExceptionIsAFailedRunWithEvidenceNotAPrelaunchRefusal()
    {
        string output = Path.Combine(_rig.Root, "bake-export-failure");
        string[] args = Arguments(output, "--server-only", "--bake-fixture", Path.Combine(_rig.Root, "baked"),
            "--assert-command", "cli_zdos_at 1 2 8", "--assert-line", "ZDO");
        int result = await ServerLoad.RunAsync(args, new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
            Campaign: (_, _, _, evidence, _) =>
            {
                string world = Path.Combine(evidence, "host-world");
                DefaultSmokeWorld.PrepareServerSaveRoot(world);
                string save = Path.Combine(world, "worlds_local", DefaultSmokeWorld.Name);
                File.Copy(Path.Combine(save, "_main.1.fwl2"), Path.Combine(save, "_main.2.fwl2"));
                // The missing runId used to throw KeyNotFoundException after the game had finished.
                File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true, CleanupVerified = true,
                    Provenance = new Dictionary<string, string> { ["bakeSaveNumber"] = "2", ["serverStopsClean"] = "true" },
                }));
                return Task.FromResult(0);
            }));
        Assert.Equal(1, result);
        Assert.True(File.Exists(Path.Combine(output, "evidence", "fixture-export-failed.txt")));
        Assert.False(File.Exists(Path.Combine(output, "REFUSED.txt")));
        Assert.False(Directory.Exists(Path.Combine(_rig.Root, "baked")));
    }

    [Fact]
    public async Task ModOwnedScriptedCommandStagesTheWholePinnedBundle()
    {
        string output = Path.Combine(_rig.Root, "mod-command");
        bool ran = false;
        int result = await ServerLoad.RunAsync(Arguments(output, "--server-only",
            "--assert-command", "mymod_status", "--assert-line", "READY"),
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
                Campaign: (_, _, _, _, _) => { ran = true; return Task.FromResult(1); }));
        Assert.Equal(1, result);
        Assert.True(ran);
        Assert.False(File.Exists(Path.Combine(output, "REFUSED.txt")));
        var locked = NativeDependencyLock.ReadReady(Path.Combine(output, "dependencies.lock.json"));
        Assert.Equal(locked.CliManifest.Files.Select(file => file.File), locked.CliFiles.Select(file => Path.GetFileName(file.File)));
    }

    [Fact]
    public async Task ARefusalAfterCampaignStartHasTheSameEvidenceMarkerAsOtherSmokeCommands()
    {
        string output = Path.Combine(_rig.Root, "post-start-refusal");
        int result = await ServerLoad.RunAsync(Arguments(output, "--server-only"),
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
                Campaign: (_, _, _, _, _) => throw new InvalidDataException("campaign setup failed after launch")));
        Assert.Equal(3, result);
        string refusal = File.ReadAllText(Path.Combine(output, "REFUSED.txt"));
        Assert.Contains("campaign setup failed after launch", refusal);
        Assert.Contains("no passing result was established", refusal);
    }

    [Fact]
    public async Task BadWorldFixtureIsRefusedBeforeBuildingTheAdapter()
    {
        string output = Path.Combine(_rig.Root, "bad-world");
        var args = Arguments(output, "--server-only", "--world-fixture", Path.Combine(_rig.Root, "missing-world")).ToList();
        args.RemoveAt(args.IndexOf("--adapter") + 1);
        args.Remove("--adapter");
        Assert.Equal(3, await ServerLoad.RunAsync(args.ToArray(), new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready))));
        Assert.False(Directory.Exists(Path.Combine(output, "adapter")));
    }

    [Fact]
    public async Task MissingPinnedObservationPackRefusesBeforeAnyCommandOrLaunch()
    {
        string output = Path.Combine(_rig.Root, "missing-observation-pack");
        bool launched = false;
        string[] args = Arguments(output, "--server-only", "--assert-command", "mymod_new_alias", "--assert-line", "READY");
        string manifestFile = args[Array.IndexOf(args, "--cli-manifest") + 1];
        var manifest = CliCapabilityManifest.Read(manifestFile);
        manifest.Files.RemoveAll(file => file.File == "Valheim.Cli.Observe.dll");
        manifest.Write(manifestFile);
        int result = await ServerLoad.RunAsync(args,
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
                Campaign: (_, _, _, _, _) => { launched = true; return Task.FromResult(0); }));
        Assert.Equal(3, result);
        Assert.False(launched);
        string refusal = File.ReadAllText(Path.Combine(output, "REFUSED.txt"));
        Assert.Contains("valheim.observe/zones", refusal);
        Assert.Contains("Observe", refusal);
    }

    [Fact]
    public async Task ChangedUnusedPackRefusesBeforeLaunchWithItsName()
    {
        string output = Path.Combine(_rig.Root, "changed-unused-pack");
        string[] args = Arguments(output, "--server-only", "--assert-command", "mymod_new_alias", "--assert-line", "READY");
        File.AppendAllText(Path.Combine(_rig.Root, "cli", "Valheim.Cli.Reflection.dll"), "changed");
        bool launched = false;
        int result = await ServerLoad.RunAsync(args, new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
            Campaign: (_, _, _, _, _) => { launched = true; return Task.FromResult(0); }));
        Assert.Equal(3, result);
        Assert.False(launched);
        string refusal = File.ReadAllText(Path.Combine(output, "REFUSED.txt"));
        Assert.Contains("Valheim.Cli.Reflection.dll", refusal);
        Assert.Contains("another build", refusal);
    }

    [Fact] public async Task TheDefaultIsAServerAndOneCleanClientAsADerivedCampaign()
    {
        string output = Path.Combine(_rig.Root, "one-off");
        string? campaignFile = null; ServerRunPlan? plan = null; IReadOnlyDictionary<string, ClientRunPlan>? clients = null;
        EnvironmentRecipe? architectureClient = null; string? selectedArchitecture = null; string? selectedLoader = null;
        int result;
        using (EnvironmentInventory.UseMachine(WithValheim(out _)))
            result = await ServerLoad.RunAsync(Arguments(output), new ServerLoad.Seams(
                Inspect: _ => Task.FromResult(Ready),
                Campaign: (file, runPlan, bind, evidence, options) =>
                {
                    campaignFile = file; plan = runPlan; clients = bind(runPlan);
                    Assert.Equal(Path.Combine(output, "evidence"), evidence);
                    return Task.FromResult(0);
                }, ClientArchitecture: (_, recipe, architecture, loader) =>
                { architectureClient = recipe; selectedArchitecture = architecture; selectedLoader = loader; }));
        Assert.Equal(0, result);
        Assert.NotNull(architectureClient);
        Assert.Equal("local-client", architectureClient.Name);
        Assert.Equal(architectureClient.Architecture, selectedArchitecture);
        Assert.Null(selectedLoader);
        Assert.Equal(Path.Combine(output, "campaign.json"), campaignFile);
        var campaign = JsonDocument.Parse(File.ReadAllText(campaignFile!)).RootElement;
        // --server is written as this machine's override; the client is this machine's detected Valheim.
        Assert.Equal(Path.Combine(output, "environments.json"), campaign.GetProperty("inventory").GetString());
        var inventory = EnvironmentInventory.Read(Path.Combine(output, "environments.json"), WithValheim(out _));
        Assert.Equal(["local-server", "local-client"], inventory.Environments.Select(recipe => recipe.Name));
        Assert.Equal(inventory.Environments.Single(recipe => recipe.Name == "local-client").Install, architectureClient.Install);
        Assert.Equal(["local-server"], campaign.GetProperty("server").GetProperty("environmentCandidates").EnumerateArray().Select(name => name.GetString()));
        var client = campaign.GetProperty("clients").GetProperty("client");
        Assert.Equal(["local-client"], client.GetProperty("environmentCandidates").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(DefaultSmokeCharacter.Name, client.GetProperty("character").GetProperty("registeredName").GetString());
        Assert.Matches("^vt[0-9a-f]{8}$", client.GetProperty("character").GetProperty("fileName").GetString());
        Assert.Equal("127.0.0.1:2486", campaign.GetProperty("join").GetString());
        Assert.Equal(DefaultSmokeWorld.Uid, campaign.GetProperty("worldUid").GetString());
        // The client's lock holds ValheimCLI only; the server's holds the mod, and its own files the adapter.
        var clientLock = NativeDependencyLock.ReadReady(client.GetProperty("dependencyLock").GetString()!);
        Assert.Empty(clientLock.Mods);
        Assert.Contains(campaign.GetProperty("server").GetProperty("files").EnumerateArray(),
            file => file.GetProperty("relativePath").GetString() == "BepInEx/plugins/NativeSmoke.SessionAdapter.dll");
        Assert.Equal(5688, plan!.Port);
        Assert.Equal("2486", plan.Arguments[plan.Arguments.IndexOf("-port") + 1]);
        var clientPlan = Assert.Single(clients!).Value;
        Assert.All(clientPlan.Pins, pin => Assert.Equal("absent", pin.Value)); // the clean client loads none of the server's plugins
        Assert.NotEmpty(clientPlan.Pins);
        Assert.False(Directory.Exists(Path.Combine(output, "consumer")));
        // The unbound plans beside it, for an editable consumer to run the same campaign again.
        Assert.Equal("2486", ServerRunPlan.Read<ServerRunPlan>(Path.Combine(output, "plan.json")).Arguments.SkipWhile(argument => argument != "-port").ElementAt(1));
        Assert.True(File.Exists(Path.Combine(output, "client-plan.json")));
        // The campaign is the shared one: its static preflight reads it as written.
        using (EnvironmentInventory.UseMachine(WithValheim(out _)))
            Assert.DoesNotContain(HostedCampaignPreparation.Inspect(campaignFile!).Problems, problem => problem.Input is "inventory" or "manifest");
    }

    // No client install: refused with the reason and --server-only, never run without its client.
    [Fact] public async Task AClientThatCannotRunIsRefusedNeverDropped()
    {
        bool ran = false;
        string output = Path.Combine(_rig.Root, "no-client");
        int result = await ServerLoad.RunAsync(Arguments(output), new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
            Campaign: (_, _, _, _, _) => { ran = true; return Task.FromResult(0); }));
        Assert.Equal(3, result);
        Assert.False(ran);
        Assert.True(ServerLoad.TryRead(Arguments(Path.Combine(_rig.Root, "unused")), out var parsed, out _));
        var refused = Assert.Throws<ArgumentException>(() => ServerLoad.Choose(parsed!, Path.Combine(_rig.Root, "choice")));
        Assert.Contains("--server-only", refused.Message);
        Assert.Contains("892970", refused.Message); // what was looked for

        // A host check that refuses the client (here: no signed-in Steam, and a mod manager's Doorstop proxy) stops before
        // anything is copied or launched, names the reviewed-loader fix, and marks the folder as a refused run.
        string checkedOutput = Path.Combine(_rig.Root, "client-refused");
        using (EnvironmentInventory.UseMachine(WithValheim(out _)))
            result = await ServerLoad.RunAsync(Arguments(checkedOutput), new ServerLoad.Seams(
                Inspect: _ => Task.FromResult(new CampaignPreflightReport([
                    new("client", "Steam identity", "No Steam account is signed in on local."),
                    new("client", "game and loader", "The source install on local's winhttp.dll is Doorstop 4 (file version 4.4.0), which reads only [General] in doorstop_config.ini, but that file is written for Doorstop 3 ([UnityDoorstop])."),
                ])),
                Campaign: (_, _, _, _, _) => { ran = true; return Task.FromResult(0); },
                ClientArchitecture: (_, _, _, _) => { }));
        Assert.Equal(3, result);
        Assert.False(ran);
        string refusal = File.ReadAllText(Path.Combine(checkedOutput, "REFUSED.txt"));
        Assert.Contains("no passing result", refusal);
        Assert.Contains("--client-loader-package FILE", refusal);
        Assert.Contains("No Steam account is signed in", refusal);
        Assert.False(File.Exists(Path.Combine(checkedOutput, "plan.json"))); // the password is written only once the preflight passed

        // With a reviewed package already given, a loader refusal is the package's own (here: another platform's loader);
        // the hint would name the option that caused it, so there is none.
        string package = Path.Combine(_rig.Root, "client-loader.json");
        BepInExLoaderPackage.Capture(_rig.Game, "reviewed", "1").Write(package);
        string packagedOutput = Path.Combine(_rig.Root, "client-package-refused");
        string? checkedArchitecture = null; string? checkedLoader = null;
        string requestedArchitecture = HostProfile.CurrentPlatform == "macos" ? "arm64" : "x64";
        using (EnvironmentInventory.UseMachine(WithValheim(out _)))
            result = await ServerLoad.RunAsync(Arguments(packagedOutput, "--client-loader-package", package, "--client-architecture", requestedArchitecture), new ServerLoad.Seams(
                Inspect: _ => Task.FromResult(new CampaignPreflightReport([new("client", "game and loader", "The reviewed loader package does not match the host platform.")])),
                Campaign: (_, _, _, _, _) => { ran = true; return Task.FromResult(0); },
                ClientArchitecture: (_, recipe, architecture, loader) =>
                { Assert.Equal("local-client", recipe.Name); checkedArchitecture = architecture; checkedLoader = loader; }));
        Assert.Equal(3, result);
        Assert.Equal(requestedArchitecture, checkedArchitecture);
        Assert.Equal(package, checkedLoader);
        Assert.False(ran);
        refusal = File.ReadAllText(Path.Combine(packagedOutput, "REFUSED.txt"));
        Assert.Contains("does not match the host platform", refusal);
        Assert.DoesNotContain("--client-loader-package FILE", refusal);
    }

    [Fact] public async Task ServerOnlySkipsTheClientAndPreflightOnlyStopsBeforeTheRun()
    {
        bool ran = false;
        string output = Path.Combine(_rig.Root, "server-only");
        int result = await ServerLoad.RunAsync(Arguments(output, "--server-only", "--preflight-only"), new ServerLoad.Seams(
            Inspect: _ => Task.FromResult(Ready), Campaign: (_, _, _, _, _) => { ran = true; return Task.FromResult(0); }));
        Assert.Equal(0, result);
        Assert.False(ran);
        var campaign = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "campaign.json"))).RootElement;
        Assert.Empty(campaign.GetProperty("clients").EnumerateObject());
        Assert.True(File.Exists(Path.Combine(output, "plan.json"))); // the consumer's input, written with the campaign
        Assert.False(File.Exists(Path.Combine(output, "REFUSED.txt")));
        Assert.Single(EnvironmentInventory.Read(Path.Combine(output, "environments.json"), new FakeMachine(HostProfile.CurrentPlatform)).Environments);
    }

    // An install whose own Doorstop pair does not match takes the shipped BepInExPack (ShippedLoader): the campaign's role
    // names that package, and an install that needs none is left to its own loader.
    [Fact] public async Task AShippedLoaderChosenForAnInstallBecomesItsRolesLoaderPackage()
    {
        string package = Path.Combine(_rig.Root, "shipped-loader.json");
        BepInExLoaderPackage.Capture(_rig.Game, "BepInExPack_Valheim", "5.4.2351").Write(package);
        var asked = new List<string>();
        string output = Path.Combine(_rig.Root, "shipped-server");
        int result = await ServerLoad.RunAsync(Arguments(output, "--server-only", "--preflight-only"), new ServerLoad.Seams(
            Inspect: _ => Task.FromResult(Ready), Campaign: (_, _, _, _, _) => Task.FromResult(0),
            Loader: (actor, install) => { asked.Add(actor); return new ShippedLoader.Choice(package, install + " mismatched"); }));
        Assert.Equal(0, result);
        Assert.Equal(new[] { "server" }, asked); // --server-only: no client to ask about
        var campaign = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "campaign.json"))).RootElement;
        Assert.Equal(package, campaign.GetProperty("server").GetProperty("loaderPackage").GetString());

        // An explicit package is the actor's own choice: the shipped one is not considered.
        asked.Clear();
        result = await ServerLoad.RunAsync(Arguments(Path.Combine(_rig.Root, "explicit"), "--server-only", "--preflight-only", "--loader-package", package),
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready), Campaign: (_, _, _, _, _) => Task.FromResult(0),
                Loader: (actor, _) => { asked.Add(actor); return null; }));
        Assert.Equal(0, result);
        Assert.Empty(asked);
    }

    [Fact] public async Task FrozenABServerLoaderRetainsTheAutomaticReplacementReason()
    {
        string package = Path.Combine(_rig.Root, "frozen-loader.json");
        BepInExLoaderPackage.Capture(_rig.Game, "BepInExPack_Valheim", "5.4.2351").Write(package);
        const string reason = "the source Doorstop pair was incoherent";
        var recorded = new Dictionary<string, string>();
        int result = await ServerLoad.RunAsync(Arguments(Path.Combine(_rig.Root, "frozen-server"),
            "--server-only", "--loader-package", package), new ServerLoad.Seams(
            Inspect: _ => Task.FromResult(Ready),
            Campaign: (_, plan, _, _, options) =>
            {
                options.Provenance!(plan, recorded);
                return Task.FromResult(0);
            },
            Loader: (_, _) => throw new InvalidOperationException("An explicit arm package must not choose another loader."),
            FrozenServerLoader: new ShippedLoader.Choice(package, reason)));

        Assert.Equal(0, result);
        Assert.Equal(reason, recorded["serverLoaderShipped"]);
        Assert.Contains("BepInExPack_Valheim", recorded["serverLoaderPackage"]);
    }

    // The server must be on this machine; a client elsewhere needs --join unless it can be inferred.
    [Fact] public void ARemoteServerIsRefusedAndARemoteClientNeedsAJoinAddress()
    {
        string file = Path.Combine(_rig.Root, "lab.json");
        bool windows = OperatingSystem.IsWindows();
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            hosts = new { lab = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@lab", @lock = "/vt/lock" } },
            environments = new object[]
            {
                new { name = "lab-server", host = "lab", roles = new[] { "server" }, install = "/opt/server", runtime = "/vt/runs", cliPort = 5700, gamePort = 2496 },
                new { name = "lab-client", host = "lab", roles = new[] { "client" }, install = "/opt/valheim", runtime = "/vt/runs", cliPort = 5701 },
                new { name = "local-server", roles = new[] { "server" }, install = windows ? @"C:\Server" : "/srv/server" },
            },
            leaseHost = "lab", leaseDirectory = "/vt/leases",
        }));
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", file], out var remote, out _));
        Assert.Contains("PinnedServerRun --inventory", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(remote!, _rig.Root)).Message);
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", file, "--server-env", "local-server"], out var local, out _));
        Assert.Contains("--join HOST:2486", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(local!, _rig.Root)).Message);
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", file, "--server-env", "local-server", "--join", "pc.lan:2456"], out var wrongPort, out _));
        Assert.Contains("game port 2486", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(wrongPort!, _rig.Root)).Message);
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", file, "--server-env", "local-server", "--join", "pc.lan:2486"], out var joined, out _));
        var choice = ServerLoad.Choose(joined!, _rig.Root);
        Assert.Equal(("local-server", "lab-client", "pc.lan:2486"), (choice.Server.Name, choice.Client!.Name, choice.Join));
    }

    [Fact] public void ConflictingOptionsAreRefusedBeforeAnything()
    {
        foreach (string[] args in new string[][]
        {
            ["--server", "a"], // no --mod
            ["--mod", "a.dll", "--server-only", "--client", "c"],
            ["--mod", "a.dll", "--server-only", "--join", "h:1"],
            ["--mod", "a.dll", "--port", "1"], // the port flags are gone: the inventory chooses
            ["--mod", "a.dll", "--server-only", "--steam-userdata", "u"],
            ["--mod", "a.dll", "--server-only", "--server-only"],
        })
            Assert.False(ServerLoad.TryRead(args, out _, out _), string.Join(" ", args));
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", "f.json", "--server", "s"], out var both, out _));
        Assert.Contains("--inventory", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(both!, _rig.Root)).Message);
        // An output inside a named install is refused before anything is written there.
        string server = Path.Combine(_rig.Root, "server-install");
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--server", server, "--server-only"], out var inside, out _));
        Assert.Contains("outside the prepared install", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(inside!, Path.Combine(server, "run"))).Message);
        Assert.False(Directory.Exists(server));

        // An override must not write its temporary inventory inside a different,
        // detected source install before the selected client is checked.
        using (EnvironmentInventory.UseMachine(WithValheim(out string clientGame)))
        {
            Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--server", server], out var selected, out _));
            string unsafeOutput = Path.Combine(clientGame, "run");
            ServerLoad.Choose(selected!, unsafeOutput);
            Assert.False(Directory.Exists(unsafeOutput));
        }
    }

    // Options from the removed Mac-only staged path are refused before anything is copied.
    [Fact] public async Task RouteSpecificOptionsAreRefused()
    {
        string output = Path.Combine(_rig.Root, "route");
        Assert.Equal(2, await ServerLoad.RunAsync(Arguments(output + "-userdata", "--steam-userdata", _rig.Root)));
        Assert.Equal(3, await ServerLoadComparison.RunAsync(["--mod", "a.dll", "--mod", "b.dll", "--remove-mod", "b.dll", "--output", output + "-ab", "--preflight-only"],
            _ => throw new InvalidOperationException("no arm runs")));
    }

    // A server install without BepInEx is named as such, with the two ways to supply a loader, before resolution.
    [Fact] public async Task AServerWithoutBepInExIsRefusedPlainly()
    {
        string output = Path.Combine(_rig.Root, "no-bepinex");
        var args = Arguments(output, "--server-only");
        Directory.Delete(Path.Combine(_rig.Game, "BepInEx", "core"), recursive: true);
        Assert.Equal(3, await ServerLoad.RunAsync(args, new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
            Campaign: (_, _, _, _, _) => throw new InvalidOperationException("never run"))));
        Assert.False(File.Exists(Path.Combine(output, "dependencies.lock.json")));
    }

    [Fact] public async Task ALocalHostRefusalStopsBeforeAnyAdapterOrFixtureIsWritten()
    {
        string output = Path.Combine(_rig.Root, "local-preflight-refused");
        bool campaignInspected = false;
        var args = Arguments(output, "--server-only", "--preflight-only");
        using var refused = LocalHostPreflight.ReplaceDefaultProbesForTest(new(
            Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
            Processes: (_, _, _, _) => Task.CompletedTask,
            Port: (_, _, _, _) => throw new InvalidOperationException("port already in use"),
            Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null));
        int result = await ServerLoad.RunAsync(args, new ServerLoad.Seams(
            Inspect: _ => { campaignInspected = true; return Task.FromResult(Ready); }));
        Assert.Equal(3, result);
        Assert.False(campaignInspected);
        Assert.False(Path.Exists(output));
    }

    // A Mac now writes and runs the same campaign as Windows and Linux, with the inventory's ports.
    [Fact] public async Task AMacUsesTheHostedCampaign()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string output = Path.Combine(_rig.Root, "mac");
        string? campaign = null;
        int result = await ServerLoad.RunAsync(Arguments(output, "--server-only"), new ServerLoad.Seams(
            Inspect: _ => Task.FromResult(Ready),
            Campaign: (file, plan, _, _, _) =>
            {
                campaign = file;
                Assert.Equal(GameLaunch.ServerMacExecutable, plan.Executable);
                Assert.Equal(5688, plan.Port);
                return Task.FromResult(0);
            }));
        Assert.Equal(0, result);
        Assert.Equal(Path.Combine(output, "campaign.json"), campaign);
        Assert.True(File.Exists(campaign));
        Assert.False(Directory.Exists(Path.Combine(output, "staged-runtime")));
    }

    // start's client is the inventory's: this machine's Valheim, --game and --loader-package as its override, or a file's
    // (--client-env picks one). It must be on this machine, and the chosen one is recorded beside the run's inputs.
    [Fact] public void StartTakesItsClientFromTheInventoryAndRecordsIt()
    {
        string output = Path.Combine(_rig.Root, "start");
        string game;
        bool checkedGui = false;
        using (EnvironmentInventory.UseMachine(WithValheim(out game)))
        {
            var (_, client, _) = SmokeInputs.Client(new Dictionary<string, string>(), output, requireMacGui: () => checkedGui = true);
            Assert.Equal(("local-client", game), (client.Name, client.Install));
            Assert.EndsWith("userdata", SmokeInputs.SteamUserdata(new Dictionary<string, string>()));
        }
        Assert.True(checkedGui);
        Assert.Equal(game, EnvironmentInventory.Read(Path.Combine(output, "environments.json"), new FakeMachine(HostProfile.CurrentPlatform)).Environments.Single().Install);
        var (_, overridden, _) = SmokeInputs.Client(new Dictionary<string, string> { ["--game"] = _rig.Game }, Path.Combine(_rig.Root, "start-game"), requireMacGui: () => { });
        Assert.Equal(_rig.Game, overridden.Install);

        // A file with two clients here and one elsewhere: --client-env picks, and only that one is recorded.
        string file = Path.Combine(_rig.Root, "lab.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            hosts = new { lab = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@lab", @lock = "/vt/lock" } },
            environments = new object[]
            {
                new { name = "lab-client", host = "lab", roles = new[] { "client" }, install = "/opt/valheim", runtime = "/vt/runs", cliPort = 5700 },
                new { name = "first", roles = new[] { "client" }, install = _rig.Game, runtime = Path.Combine(_rig.Root, "runs-first"), cliPort = 5701 },
                new { name = "alt", roles = new[] { "client" }, install = _rig.Game, runtime = Path.Combine(_rig.Root, "runs-alt"), cliPort = 5702 },
            },
            leaseHost = "lab", leaseDirectory = "/vt/leases",
        }));
        Assert.Contains("not this machine", Assert.Throws<ArgumentException>(() =>
            SmokeInputs.Client(new Dictionary<string, string> { ["--inventory"] = file }, Path.Combine(_rig.Root, "start-remote"))).Message);
        string chosen = Path.Combine(_rig.Root, "start-alt");
        var (_, alt, _) = SmokeInputs.Client(new Dictionary<string, string> { ["--inventory"] = file, ["--client-env"] = "alt" }, chosen, requireMacGui: () => { });
        Assert.Equal(5702, alt.CliPort);
        string explicitLoader = Path.Combine(_rig.Root, "client-loader.json");
        var (_, withLoader, _) = SmokeInputs.Client(new Dictionary<string, string>
        {
            ["--inventory"] = file, ["--client-env"] = "alt", ["--client-loader-package"] = explicitLoader,
        }, Path.Combine(_rig.Root, "start-alt-loader"), requireMacGui: () => { });
        Assert.Equal(explicitLoader, withLoader.LoaderPackage);
        var recorded = EnvironmentInventory.Read(Path.Combine(chosen, "environments.json"), new FakeMachine(HostProfile.CurrentPlatform));
        Assert.Equal(("alt", 5702), (recorded.Environments.Single().Name, recorded.Environments.Single().CliPort));

        Assert.Contains("Give --game", Assert.Throws<ArgumentException>(() => SmokeInputs.Client(new Dictionary<string, string>(), Path.Combine(_rig.Root, "none"))).Message);
        Assert.Contains("--inventory", Assert.Throws<ArgumentException>(() => SmokeInputs.Client(
            new Dictionary<string, string> { ["--game"] = _rig.Game, ["--inventory"] = "f.json" }, Path.Combine(_rig.Root, "both"))).Message);
        Assert.Contains("--steam-userdata", Assert.Throws<DirectoryNotFoundException>(() => SmokeInputs.SteamUserdata(new Dictionary<string, string>())).Message);
    }

    [Fact] public void StartRefusesAnUnavailableGuiBeforeRecordingOrInspectingTheLoader()
    {
        string output = Path.Combine(_rig.Root, "locked-mac-start");
        bool inspectedLoader = false;
        var failure = Assert.Throws<InvalidOperationException>(() => SmokeInputs.Client(
            new Dictionary<string, string> { ["--game"] = _rig.Game }, output,
            shippedLoader: (_, _) => { inspectedLoader = true; return null; },
            requireMacGui: () => throw new InvalidOperationException("The macOS console session is locked or unavailable")));
        Assert.Contains("macOS console session is locked", failure.Message);
        Assert.False(inspectedLoader);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public void StartCanDeferRecordingUntilOutputPassesSourceProtection()
    {
        string output = Path.Combine(_rig.Game, "accidental-output");
        string inventoryFile = Path.Combine(_rig.Root, "client-inventory.json");
        File.WriteAllText(inventoryFile, JsonSerializer.Serialize(new
        {
            environments = new[] { new { name = "local-client", roles = new[] { "client" },
                install = _rig.Game, runtime = Path.Combine(_rig.Root, "runs"), cliPort = 5701 } },
        }));
        var (inventory, client, _) = SmokeInputs.Client(
            new Dictionary<string, string> { ["--inventory"] = inventoryFile }, output,
            requireMacGui: () => { }, recordSelection: false);
        Assert.False(Directory.Exists(output));
        Assert.Throws<ArgumentException>(() => SmokeOutput.RefuseInside(output, _rig.Game));
        Assert.False(Directory.Exists(output));

        string safe = Path.Combine(_rig.Root, "safe-output");
        SmokeOutput.RefuseInside(safe, _rig.Game);
        SmokeInputs.RecordClient(inventory, client, safe);
        Assert.True(File.Exists(Path.Combine(safe, "environments.json")));
    }
}

public sealed class SampleOneShotServerScenario : IOneShotServerScenario
{
    public string Name => "sample-one-shot";
    public bool RequiresClient => false;
    public Task RunAsync(GameSession session, OneShotServerContext context)
    {
        session.Report.Step("sample fixture UID is pinned", () =>
        {
            if (string.IsNullOrWhiteSpace(context.WorldUid)) throw new InvalidDataException("Missing fixture world UID.");
        });
        return Task.CompletedTask;
    }
}

public sealed class SampleOneShotHostedScenario : IOneShotHostedScenario
{
    public string Name => "sample-hosted-one-shot";
    public void Run(ClientRound round) => round.Step("hosted fixture UID is pinned", () =>
    {
        if (string.IsNullOrWhiteSpace(round.WorldUid)) throw new InvalidDataException("Missing fixture world UID.");
    });
}
