using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

public sealed class NativeDependencyResolverTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public void StandalonePluginNeedsNoDependencyDllAndRelativeRequestPathsAreResolved()
    {
        string mod = _rig.Write("standalone/Alone.dll", RegressionRig.Assembly("Alone", new("example.alone")));
        var request = Request(mod);
        request.SearchRoots = [];
        string directory = Path.Combine(_rig.Root, "request");
        Directory.CreateDirectory(directory);
        request.Mods = [Path.GetRelativePath(directory, mod)];
        request.GameManaged = Path.GetRelativePath(directory, request.GameManaged);
        request.BepInExCore = Path.GetRelativePath(directory, request.BepInExCore);
        request.CliManifest = Path.GetRelativePath(directory, request.CliManifest);
        request.CliFiles = Path.GetRelativePath(directory, request.CliFiles);
        string file = Path.Combine(directory, "setup.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(request));
        var resolved = NativeDependencyResolver.Resolve(NativeDependencyRequest.Read(file));
        Assert.True(resolved.Ready, string.Join("; ", resolved.Gaps.Select(gap => gap.Reason)));
        Assert.Empty(resolved.Plugins);
        Assert.Equal(mod, Assert.Single(resolved.Mods).File);
    }

    [Fact] public void HardPluginAndLibraryClosureArePinnedFromExplicitRoots()
    {
        var request = Request(_rig.Parent);
        var plan = NativeDependencyResolver.Resolve(request);
        Assert.True(plan.Ready, string.Join("; ", plan.Gaps.Select(gap => gap.Reason)));
        Assert.Equal(new[] { "Dependency.dll" }, plan.Plugins.Select(file => Path.GetFileName(file.File)));
        Assert.Equal(new[] { "valheimCLI.dll", "Valheim.Cli.Standard.dll" }, plan.CliFiles.Select(file => Path.GetFileName(file.File)));
        Assert.Contains("example.soft", plan.OptionalCandidates); // Soft plugins are reported, never auto-staged.
        Assert.Contains("hard [BepInDependency]", plan.Plugins[0].Reason);
        string lockFile = Path.Combine(_rig.Root, "dependency-lock.json");
        plan.Write(lockFile);
        Assert.True(NativeDependencyLock.ReadReady(lockFile).Ready);
        var environment = _rig.Manifest();
        environment.Plugins.Clear();
        plan.ApplyTo(environment, Path.Combine(_rig.Root, "selected-cli.json"));
        Assert.Single(environment.Plugins);
        Assert.Equal("Dependency.dll", Path.GetFileName(environment.Plugins[0].File));
        new TargetedRegression(environment).Stage("parent");
        File.AppendAllText(plan.Plugins[0].File, "changed");
        Assert.Contains("missing or changed", Assert.Throws<InvalidDataException>(() => NativeDependencyLock.ReadReady(lockFile)).Message);
    }

    [Fact] public void HostedRuntimeSelectionUsesTheReviewedDependencyClosure()
    {
        var plan = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        Assert.True(plan.Ready);
        string path = Path.Combine(_rig.Root, "host-lock.json");
        plan.Write(path);
        var staged = HostedRuntimeStage.FromDependencies(path);
        Assert.Equal(plan.Mods.Count + plan.Plugins.Count + plan.CliFiles.Count, staged.Count);
        Assert.All(staged, file => Assert.StartsWith("BepInEx/plugins/", file.RelativePath));
        Assert.Contains(staged, file => file.RelativePath == "BepInEx/plugins/valheimCLI.dll");
        File.AppendAllText(_rig.Parent, "changed");
        Assert.Throws<InvalidDataException>(() => HostedRuntimeStage.FromDependencies(path));
    }

    [Fact] public async Task HostedCampaignPreparesServerAndAnyNumberOfNamedClientsAndRetiresTheirCopies()
    {
        string parent = _rig.Write("campaign/Server.dll", RegressionRig.Assembly("Server", new("example.server")));
        string client = _rig.Write("campaign/Client.dll", RegressionRig.Assembly("Client", new("example.client")));
        string serverLock = Lock(parent, "server"), clientLock = Lock(client, "client");
        string[] stores = [Store("one", 101), Store("two", 202), Store("three", 303)];
        string world = Path.Combine(_rig.Root, "campaign-world");
        Directory.CreateDirectory(world);
        using (var payload = new MemoryStream())
        {
            using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
            { writer.Write(41); writer.Write("Campaign"); writer.Write("AbCdEf1234"); writer.Write(1234); writer.Write(4242L); }
            File.WriteAllBytes(Path.Combine(world, "Campaign.fwl"), [.. BitConverter.GetBytes((int)payload.Length), .. payload.ToArray()]);
        }
        File.WriteAllText(Path.Combine(world, "Campaign.db"), "fixture");
        var hosts = new Dictionary<string, FakeServerHost>(StringComparer.Ordinal);
        foreach (string name in new[] { "server", "client-a", "client-b", "client-c" })
        {
            var host = new FakeServerHost(name, Path.Combine(_rig.Root, "mirror-" + name), windows: true);
            hosts[name] = host;
            host.SteamUserReply = "VT-STEAMUSER account " + (name == "client-b" ? 202 : name == "client-c" ? 303 : 101) + "\n";
            string source = host.Local(@"C:\game\source");
            if (name == "server") FakeInstalls.Server(source); else FakeInstalls.Client(source);
            File.WriteAllText(Path.Combine(source, name == "server" ? ServerLaunch.WindowsExecutable : ClientLaunch.WindowsExecutable), "game");
            File.WriteAllText(Path.Combine(source, "winhttp.dll"), "unknown proxy version");
            File.WriteAllText(Path.Combine(source, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
            if (name != "server")
            {
                Directory.CreateDirectory(host.Local(@"C:\save\characters_local"));
                Directory.CreateDirectory(host.Local(@"C:\Steam\userdata"));
            }
        }
        // Each actor's environment on its own host; the clients' Steam identities are the ones signed in there.
        object Environment(string name, string host, string role, int port, string install = @"C:\game\source") => new
        {
            name, host, roles = new[] { role }, install, runtime = @"C:\runs", cliPort = port, localCliPort = port + 1000, gamePort = role == "server" ? 2456 : 0,
        };
        object Inventory(params object[] environments) => new
        {
            hosts = hosts.Keys.ToDictionary(name => name, name => new { kind = "ssh", platform = "windows", shell = "powershell",
                @lock = @"C:\locks\campaign.lock", destination = "test@" + name }),
            environments, leaseHost = "server", leaseDirectory = @"C:\leases",
        };
        string inventoryFile = Path.Combine(_rig.Root, "campaign-inventory.json");
        File.WriteAllText(inventoryFile, JsonSerializer.Serialize(Inventory(Environment("server", "server", "server", 5577),
            Environment("client-a", "client-a", "client", 5578), Environment("client-b", "client-b", "client", 5579), Environment("client-c", "client-c", "client", 5580))));
        string manifestFile = Path.Combine(_rig.Root, "campaign.json");
        File.WriteAllText(manifestFile, JsonSerializer.Serialize(new
        {
            inventory = inventoryFile,
            world, join = "test-server.example:2456",
            server = new { dependencyLock = serverLock },
            clients = new Dictionary<string, object> {
                ["client-a"] = new { dependencyLock = clientLock, character = Character(stores[0], "one", "vt-one") },
                ["client-b"] = new { dependencyLock = clientLock, character = Character(stores[1], "two", "vt-two") },
                ["client-c"] = new { dependencyLock = clientLock, character = Character(stores[2], "three", "vt-three") },
            },
        }));
        var duplicate = JsonNode.Parse(File.ReadAllText(manifestFile))!;
        duplicate["clients"]!["client-b"]!["character"]!["store"] = stores[0];
        duplicate["clients"]!["client-b"]!["character"]!["registeredName"] = "one";
        string duplicateFile = Path.Combine(_rig.Root, "duplicate-player.json");
        File.WriteAllText(duplicateFile, duplicate.ToJsonString());
        Assert.Contains("different registered character player IDs", Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.Check(duplicateFile)).Message);
        Assert.All(hosts.Values, host => Assert.Empty(host.Claims));
        HostedCampaignPreparation.Check(manifestFile);
        hosts["client-a"].SteamUserReply = "VT-STEAMUSER unreadable\n";
        hosts["client-b"].SteamUserReply = "VT-STEAMUSER account 303\n"; // client-c's identity: one account, two simultaneous clients.
        var hostReport = await HostedCampaignPreparation.InspectAsync(manifestFile, TimeSpan.FromSeconds(30), name => hosts[name]);
        Assert.Contains(hostReport.Problems, problem => problem.Actor == "client-a" && problem.Input == "Steam identity");
        Assert.Contains(hostReport.Problems, problem => problem.Actor == "clients" && problem.Input == "Steam identities" && problem.Message.Contains("client-b, client-c"));
        Assert.All(hosts.Values, host => Assert.DoesNotContain(host.Scripts, script => script is "ship" or "copy" or "start"));
        var identities = await Assert.ThrowsAsync<ArgumentException>(() => HostedCampaignPreparation.PrepareAsync(manifestFile,
            Path.Combine(_rig.Root, "bad-identities"), TimeSpan.FromSeconds(30), name => hosts[name]));
        Assert.Contains("client-a Steam identity", identities.Message);
        Assert.Contains("client-b, client-c use the same signed-in Steam account", identities.Message);
        Assert.All(hosts.Values, host => Assert.DoesNotContain("ship", host.Scripts));
        hosts["client-a"].SteamUserReply = "VT-STEAMUSER account 101\n";
        hosts["client-b"].SteamUserReply = "VT-STEAMUSER account 202\n";
        int active = 0, peak = 0;
        foreach (var host in hosts.Values)
            host.BeforeShip = async () =>
            {
                int now = Interlocked.Increment(ref active);
                int old;
                while ((old = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, old) != old) { }
                try { await Task.Delay(30); }
                finally { Interlocked.Decrement(ref active); }
            };
        string output = Path.Combine(_rig.Root, "prepared");
        string[] prepared;
        await using (var campaign = await HostedCampaignPreparation.PrepareAsync(manifestFile, output, TimeSpan.FromSeconds(30), name => hosts[name]))
        {
            var profile = campaign.Environment;
            prepared = [profile.Server!.Install, profile.Clients["client-a"].Install,
                profile.Clients["client-b"].Install, profile.Clients["client-c"].Install];
            Assert.Equal(4, campaign.Listings.Count);
            Assert.Contains("BepInEx/plugins/Server.dll", campaign.Listings["server"].Files.Keys);
            Assert.DoesNotContain("BepInEx/plugins/Server.dll", campaign.Listings["client-a"].Files.Keys);
            Assert.Contains("BepInEx/plugins/Client.dll", campaign.Listings["client-b"].Files.Keys);
            Assert.Contains("BepInEx/config/valheimCLI.valheimCLI.cfg", campaign.Listings["client-a"].Files.Keys);
            Assert.Contains("example.server", campaign.PluginPins("server").Keys);
            Assert.DoesNotContain("example.server", campaign.PluginPins("client-a").Keys);
            var clients = new Dictionary<string, ClientRunPlan>
            {
                ["client-a"] = new() { Pins = new() { ["example.server"] = "absent" } }, ["client-b"] = new(), ["client-c"] = new(),
            };
            var plan = new ServerRunPlan();
            campaign.ApplyTo(plan, HostedCampaignManifest.Read(manifestFile), clients, output);
            Assert.Equal("4242", plan.Pins["worlduid"]);
            Assert.All(plan.World.Sha256.Keys, path => Assert.StartsWith("worlds_local" + Path.DirectorySeparatorChar, path));
            Assert.True(File.Exists(Path.Combine(plan.World.Source, "worlds_local", "Campaign.fwl")));
            Assert.True(File.Exists(Path.Combine(plan.World.Source, "worlds_local", "Campaign.db")));
            WorldFixture.Verify(plan.World.Source, plan.World.Sha256);
            Assert.Equal(profile.Server.Install, plan.Runtime.Source);
            Assert.Equal("vt-one", clients["client-a"].Character);
            Assert.Equal("absent", clients["client-a"].Pins["example.server"]); // Binding keeps what a client pins absent.
            Assert.Contains("example.client", clients["client-a"].Pins.Keys);
            Assert.Equal("test-server.example:2456", clients["client-b"].Join);
            Assert.Equal("vt-three", clients["client-c"].Character);
            Assert.All(clients.Values, role => Assert.True(File.Exists(role.CliManifest)));
            Assert.True(File.Exists(hosts["client-a"].Local(@"C:\save\characters_local\vt-one.fch")));
            Assert.True(File.Exists(hosts["client-b"].Local(@"C:\save\characters_local\vt-two.fch")));
            Assert.True(File.Exists(hosts["client-c"].Local(@"C:\save\characters_local\vt-three.fch")));
            Assert.All(new[] { "server", "client-a", "client-b", "client-c" }, name => Assert.NotEmpty(hosts[name].Claims));
        }
        Assert.True(peak >= 2, "Independent actors should stage concurrently, not wait for each prior actor's copy.");
        foreach (string name in hosts.Keys)
        {
            Assert.True(File.Exists(Path.Combine(hosts[name].Local(@"C:\game\source"), "BepInEx", "core", "BepInEx.dll")));
            Assert.Equal(hosts[name].Claims.Count, hosts[name].Releases.Count);
        }
        Assert.False(Directory.Exists(hosts["server"].Local(prepared[0])));
        Assert.False(Directory.Exists(hosts["client-a"].Local(prepared[1])));
        Assert.False(Directory.Exists(hosts["client-b"].Local(prepared[2])));
        Assert.False(Directory.Exists(hosts["client-c"].Local(prepared[3])));
        Assert.False(File.Exists(hosts["client-a"].Local(@"C:\save\characters_local\vt-one.fch")));
        Assert.False(File.Exists(hosts["client-b"].Local(@"C:\save\characters_local\vt-two.fch")));
        Assert.False(File.Exists(hosts["client-c"].Local(@"C:\save\characters_local\vt-three.fch")));
        Assert.False(File.Exists(Path.Combine(output, "profile.json"))); // The prepared environment, with the observed Steam IDs, stays in memory.

        // A dedicated server and one client may share a machine. Their installs are separate, but setup should
        // still overlap under one host claim rather than serialising two full game copies.
        var sharedHost = hosts["server"];
        string sharedClientSource = sharedHost.Local(@"C:\game\client-source");
        FakeInstalls.Client(sharedClientSource);
        File.WriteAllText(Path.Combine(sharedClientSource, ClientLaunch.WindowsExecutable), "game");
        File.WriteAllText(Path.Combine(sharedClientSource, "winhttp.dll"), "unknown proxy version");
        File.WriteAllText(Path.Combine(sharedClientSource, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        Directory.CreateDirectory(sharedHost.Local(@"C:\save\characters_local"));
        Directory.CreateDirectory(sharedHost.Local(@"C:\Steam\userdata"));
        string sameHostInventoryFile = Path.Combine(_rig.Root, "shared-host-inventory.json");
        File.WriteAllText(sameHostInventoryFile, JsonSerializer.Serialize(Inventory(Environment("server", "server", "server", 5577),
            Environment("client-a", "server", "client", 5578, @"C:\game\client-source"), Environment("client-b", "client-b", "client", 5579),
            Environment("client-c", "client-c", "client", 5580))));
        var sameHostManifest = JsonNode.Parse(File.ReadAllText(manifestFile))!;
        sameHostManifest["inventory"] = sameHostInventoryFile;
        string sameHostManifestFile = Path.Combine(_rig.Root, "shared-host-campaign.json");
        File.WriteAllText(sameHostManifestFile, sameHostManifest.ToJsonString());
        int sharedActive = 0, sharedPeak = 0;
        sharedHost.BeforeShip = async () =>
        {
            int now = Interlocked.Increment(ref sharedActive);
            int old;
            while ((old = Volatile.Read(ref sharedPeak)) < now && Interlocked.CompareExchange(ref sharedPeak, now, old) != old) { }
            try { await Task.Delay(30); }
            finally { Interlocked.Decrement(ref sharedActive); }
        };
        int claimsBefore = sharedHost.Claims.Count;
        await using (var campaign = await HostedCampaignPreparation.PrepareAsync(sameHostManifestFile,
            Path.Combine(_rig.Root, "shared-host-prepared"), TimeSpan.FromSeconds(30), name => hosts[name]))
        {
            Assert.Equal(claimsBefore + 1, sharedHost.Claims.Count);
            Assert.True(sharedPeak >= 2, "Server and client setup on one host should overlap under its single claim.");
            Assert.Contains("BepInEx/plugins/Server.dll", campaign.Listings["server"].Files.Keys);
            Assert.DoesNotContain("BepInEx/plugins/Server.dll", campaign.Listings["client-a"].Files.Keys);
            Assert.True(File.Exists(sharedHost.Local(@"C:\save\characters_local\vt-one.fch")));
        }
        Assert.False(File.Exists(sharedHost.Local(@"C:\save\characters_local\vt-one.fch")));

        object Character(string store, string registeredName, string fileName) => new
        {
            store, registeredName, fileName,
            charactersLocalDirectory = @"C:\save\characters_local", steamUserDataDirectory = @"C:\Steam\userdata",
        };
        string Store(string name, long id)
        {
            string local = _rig.Write("characters_local/" + name + ".fch", CharacterSaveReaderTests.Profile(playerId: id).File);
            string store = Path.Combine(_rig.Root, "registered-" + name);
            DisposableCharacterStore.Create(store).Register(name, local);
            return store;
        }

        string Lock(string mod, string name)
        {
            var resolved = NativeDependencyResolver.Resolve(Request(mod));
            Assert.True(resolved.Ready, string.Join("; ", resolved.Gaps.Select(gap => gap.Reason)));
            string path = Path.Combine(_rig.Root, name + "-lock.json");
            resolved.Write(path);
            return path;
        }
    }

    // RunCampaignAsync: one report from the campaign's preflight through the run to retiring the prepared install, with the
    // prepared environment in memory (no profile.json, plan.json or campaign-times.json); a plan that disagrees with the
    // campaign is refused in Preflight before any host is contacted.
    [Fact] public async Task CampaignRunPreparesRunsAndRetiresInOneReportAndRefusesADisagreeingPlanFirst()
    {
        string serverDll = _rig.Write("run-campaign/Server.dll", RegressionRig.Assembly("Server", new("example.server")));
        var resolved = NativeDependencyResolver.Resolve(Request(serverDll));
        Assert.True(resolved.Ready, string.Join("; ", resolved.Gaps.Select(gap => gap.Reason)));
        string serverLock = Path.Combine(_rig.Root, "run-campaign-lock.json");
        resolved.Write(serverLock);
        string world = Path.Combine(_rig.Root, "run-campaign-world");
        Directory.CreateDirectory(world);
        using (var payload = new MemoryStream())
        {
            using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
            { writer.Write(41); writer.Write("Campaign"); writer.Write("AbCdEf1234"); writer.Write(1234); writer.Write(4242L); }
            File.WriteAllBytes(Path.Combine(world, "Campaign.fwl"), [.. BitConverter.GetBytes((int)payload.Length), .. payload.ToArray()]);
        }
        File.WriteAllText(Path.Combine(world, "Campaign.db"), "fixture");
        var server = new FakeOwnedServer("test.mod", saveRoot: @"C:\runs\run-test\world");
        var host = new FakeServerHost("pc", Path.Combine(_rig.Root, "run-campaign-pc"), server, windows: true);
        string source = host.Local(@"C:\game\source");
        FakeInstalls.Server(source);
        File.WriteAllText(Path.Combine(source, ServerLaunch.WindowsExecutable), "server");
        File.WriteAllText(Path.Combine(source, "winhttp.dll"), "MZ target_assembly");
        File.WriteAllText(Path.Combine(source, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        string inventory = Path.Combine(_rig.Root, "run-campaign-inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new { pc = new { kind = "ssh", platform = "windows", shell = "powershell", @lock = @"C:\locks\campaign.lock", destination = "test@pc" } },
            environments = new[] { new { name = "pc-server", host = "pc", roles = new[] { "server" }, install = @"C:\game\source", runtime = @"C:\runs", cliPort = 5577, localCliPort = 6577, gamePort = 2456 } },
        }));
        string manifest = Path.Combine(_rig.Root, "run-campaign.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            inventory, world, join = "test-server.example:2456",
            server = new { dependencyLock = serverLock }, clients = new Dictionary<string, object>(),
        }));
        SitePlan Plan(string pin, string password, string value = "<md5 of the server's plugin>") => new()
        {
            Scenario = "smoke", Port = 5577,
            Arguments = ["-batchmode", "-nographics", "-savedir", "{world}", "-port", "2456", "-password", password, "-logFile", "{runtime}/toolkit-unity.log"],
            Pins = new() { [pin] = value },
        };
        bool scenarioRan = false;
        var options = new PinnedServerRunOptions<SitePlan>
        {
            Name = "campaign-smoke", ReadPlan = _ => throw new InvalidOperationException("A campaign plan is in memory."),
            SessionCapability = "test.mod/session", SessionTokenVariable = "TEST_SESSION_TOKEN", TestAccess = false,
            Scenario = run => { scenarioRan = true; Assert.NotNull(run.ServerHost); return Task.CompletedTask; },
            HostSeams = new HostedSeams { Host = _ => host, Connect = _ => server.Connect(), StateWaits = false, RunId = "run-test" },
        };
        IReadOnlyDictionary<string, ClientRunPlan> NoClients(SitePlan _) => new Dictionary<string, ClientRunPlan>();

        // A legacy fixed-profile manifest is named as such; a manifest without world or join is refused before any host.
        string legacy = Path.Combine(_rig.Root, "run-campaign-legacy.json");
        File.WriteAllText(legacy, "{\"profile\": \"environment.json\", \"server\": {\"dependencyLock\": \"lock.json\"}, \"clients\": {}}");
        Assert.Contains(HostedCampaignPreparation.Inspect(legacy).Problems, problem => problem.Message.Contains("replace profile with inventory"));
        string noJoin = Path.Combine(_rig.Root, "run-campaign-nojoin.json");
        File.WriteAllText(noJoin, JsonSerializer.Serialize(new { inventory, world, server = new { dependencyLock = serverLock }, clients = new Dictionary<string, object>() }));
        HostedCampaignPreparation.Check(noJoin);
        Assert.Contains("Set world (the fixture) and join", Assert.Throws<ArgumentException>(() =>
            HostedCampaignPreparation.CheckPlan(noJoin, Plan("example.server", "secret"), new Dictionary<string, ClientRunPlan>())).Message);
        // A plugin pinned absent (a client's way to say "not loaded") that the lock selects contradicts the campaign too.
        Assert.Contains("pins plugin example.server absent", Assert.Throws<ArgumentException>(() =>
            HostedCampaignPreparation.CheckPlan(manifest, Plan("example.server", "secret", "absent"), new Dictionary<string, ClientRunPlan>())).Message);

        // A plugin the lock does not select and a placeholder argument: both named, in Preflight, before any host script.
        string refusedOutput = Path.Combine(_rig.Root, "run-campaign-refused");
        Assert.Equal(1, await PinnedServerRun.RunCampaignAsync(manifest, Plan("example.missing", "<fixture password>"), NoClients, refusedOutput, options));
        var refused = JsonDocument.Parse(File.ReadAllText(Path.Combine(refusedOutput, "result.json"))).RootElement;
        var agreement = refused.GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == "the plan agrees with the campaign");
        Assert.Equal("Preflight", agreement.GetProperty("Phase").GetString());
        Assert.Contains("example.missing", agreement.GetProperty("Error").GetString());
        Assert.Contains("<fixture password>", agreement.GetProperty("Error").GetString());
        Assert.False(refused.GetProperty("PreflightPassed").GetBoolean());
        Assert.Empty(host.Scripts); Assert.Empty(host.Claims); Assert.False(scenarioRan);

        string output = Path.Combine(_rig.Root, "run-campaign-out");
        Assert.Equal(0, await PinnedServerRun.RunCampaignAsync(manifest, Plan("example.server", "secret"), NoClients, output, options));
        Assert.True(scenarioRan);
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement;
        Assert.True(result.GetProperty("CleanupVerified").GetBoolean());
        var steps = result.GetProperty("Steps").EnumerateArray().ToDictionary(step => step.GetProperty("Name").GetString()!, step => step.GetProperty("Phase").GetString());
        Assert.Equal("Setup", steps["check the hosts and prepare every actor's disposable install"]);
        Assert.Equal("Cleanup", steps["retire the campaign's prepared installs and characters"]);
        Assert.True(File.Exists(Path.Combine(output, "prepared", "environment-assignments.json")));
        Assert.False(File.Exists(Path.Combine(output, "prepared", "profile.json")));
        Assert.False(File.Exists(Path.Combine(output, "campaign-times.json")));
        // The bound plan is evidence (NaN sites included), hashed as the run's plan; the campaign and inventory are hashed too.
        string bound = Path.Combine(output, "prepared", "plan.json");
        Assert.Equal(WorldFixture.Hash(bound), result.GetProperty("Provenance").GetProperty("planSha256").GetString());
        Assert.Contains("NaN", File.ReadAllText(bound));
        Assert.Equal(WorldFixture.Hash(manifest), result.GetProperty("Provenance").GetProperty("campaignSha256").GetString());
        Assert.Equal(WorldFixture.Hash(inventory), result.GetProperty("Provenance").GetProperty("inventorySha256").GetString());
        Assert.Equal(host.Claims.Count, host.Releases.Count);
        Assert.Empty(Directory.GetDirectories(host.Local(@"C:\runs"), "vt-prep-*")); // The prepared install was retired.
    }

    private sealed class SitePlan : ServerRunPlan { public float Ground { get; set; } = float.NaN; }

    [Fact] public async Task PreparingOrRetiringACharacterRefusesAnActiveGameProcess()
    {
        var host = new FakeServerHost("client", Path.Combine(_rig.Root, "active-game"), windows: true) { GameActive = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostedRuntimeStage.RequireStoppedAsync(host, TimeSpan.FromSeconds(2)));
        Assert.Contains(host.Scripts, script => script == "game-process");
        host.GameActive = false;
        await HostedRuntimeStage.RequireStoppedAsync(host, TimeSpan.FromSeconds(2));
    }

    [Fact] public async Task LostCharacterInstallReplyNamesThePossibleSaveWithoutDeletingIt()
    {
        string original = _rig.Write("characters_local/tester.fch", CharacterSaveReaderTests.Profile(playerId: 919).File);
        string store = Path.Combine(_rig.Root, "ambiguous-store");
        DisposableCharacterStore.Create(store).Register("tester", original);
        var host = new FakeServerHost("client", Path.Combine(_rig.Root, "ambiguous-host"), windows: true);
        Directory.CreateDirectory(host.Local(@"C:\save\characters_local"));
        Directory.CreateDirectory(host.Local(@"C:\Steam\userdata"));
        host.Failures["character-install"] = FakeServerHost.TransportFailure;
        var chosen = HostedCharacterStage.Select(new HostedCampaignCharacter
        {
            Store = store, RegisteredName = "tester", FileName = "vt-ambiguous",
            CharactersLocalDirectory = @"C:\save\characters_local", SteamUserDataDirectory = @"C:\Steam\userdata",
        }, _rig.Root);
        var error = await Assert.ThrowsAsync<IOException>(() => HostedCharacterStage.StageAsync(host, chosen,
            @"C:\runs\vt-prep-test\character-stage", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains("not proven", error.Message);
        Assert.Contains("vt-ambiguous", error.Message);
        Assert.DoesNotContain("character-retire", host.Scripts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialCharacterUploadReportsWhetherStagingCleanupWasProven(bool cleanupFails)
    {
        string original = _rig.Write("characters_local/stage-seed.fch", CharacterSaveReaderTests.Profile(playerId: 920).File);
        string store = Path.Combine(_rig.Root, "stage-store");
        DisposableCharacterStore.Create(store).Register("stage-seed", original);
        var host = new FakeServerHost("client", Path.Combine(_rig.Root, "stage-host"), windows: true);
        Directory.CreateDirectory(host.Local(@"C:\save\characters_local"));
        Directory.CreateDirectory(host.Local(@"C:\Steam\userdata"));
        string stage = @"C:\runs\vt-prep-test\character-stage";
        host.AfterShip = _ => throw new IOException("upload reply lost after files arrived");
        if (cleanupFails) host.Failures["character-drop"] = FakeServerHost.TransportFailure;
        var chosen = HostedCharacterStage.Select(new HostedCampaignCharacter
        {
            Store = store, RegisteredName = "stage-seed", FileName = "vt-stage-seed",
            CharactersLocalDirectory = @"C:\save\characters_local", SteamUserDataDirectory = @"C:\Steam\userdata",
        }, _rig.Root);

        if (cleanupFails)
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => HostedCharacterStage.StageAsync(host, chosen,
                stage, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Contains("upload reply lost", error.InnerExceptions[0].Message);
            Assert.Contains("Removing character staging", error.InnerExceptions[1].Message);
            Assert.True(Directory.Exists(host.Local(stage)));
        }
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(() => HostedCharacterStage.StageAsync(host, chosen,
                stage, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Contains("upload reply lost", error.Message);
            Assert.False(Directory.Exists(host.Local(stage)));
        }
        Assert.Contains("character-drop", host.Scripts);
        Assert.False(File.Exists(Path.Combine(host.Local(@"C:\save\characters_local"), "vt-stage-seed.fch")));
    }

    [Fact] public void AnEditedLockCannotStageAnExtraCliPackOutsideTheSelectedManifest()
    {
        var plan = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        Assert.True(plan.Ready);
        plan.CliFiles.Add(plan.CliFiles[0]);
        string path = Path.Combine(_rig.Root, "edited-lock.json");
        plan.Write(path);
        Assert.Contains("exactly the ValheimCLI core and packs", Assert.Throws<InvalidDataException>(() => NativeDependencyLock.ReadReady(path)).Message);
        Assert.Throws<InvalidDataException>(() => plan.ApplyTo(_rig.Manifest(), Path.Combine(_rig.Root, "should-not-be-written.json")));
        Assert.False(File.Exists(Path.Combine(_rig.Root, "should-not-be-written.json")));
    }

    [Fact] public void AnEditedLockWithoutTheCliCoreIsNotReady()
    {
        var plan = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        var core = Assert.Single(plan.CliManifest.Files.Where(file => file.Plugins.Contains("valheimCLI.valheimCLI")));
        core.Plugins = ["a.different.plugin"];
        string path = Path.Combine(_rig.Root, "no-core-lock.json");
        plan.Write(path);
        Assert.Contains("exactly one ValheimCLI core", Assert.Throws<InvalidDataException>(() => NativeDependencyLock.ReadReady(path)).Message);
    }

    [Fact] public void UniqueReferencedLibraryIsIncludedAndAmbiguityIsLeftForAnExplicitChoice()
    {
        string mod = _rig.Write("uses/Uses.dll", RegressionRig.Assembly("Uses", new("example.uses"), reference: typeof(FactAttribute)));
        string library = _rig.Write("library-one/xunit.core.dll", RegressionRig.Assembly("xunit.core", null));
        var request = Request(mod);
        request.SearchRoots = [Path.Combine(_rig.Root, "library-one")];
        var unique = NativeDependencyResolver.Resolve(request);
        Assert.True(unique.Ready, string.Join("; ", unique.Gaps.Select(gap => gap.Reason)));
        Assert.Equal(library, Assert.Single(unique.Plugins).File);
        Assert.Contains("assembly reference xunit.core", unique.Plugins[0].Reason);

        _rig.Write("library-two/xunit.core.dll", RegressionRig.Assembly("xunit.core", null, marker: "Other"));
        request.SearchRoots.Add(Path.Combine(_rig.Root, "library-two"));
        var ambiguous = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(ambiguous.Gaps);
        Assert.Equal("assembly", gap.Kind);
        Assert.Equal("xunit.core", gap.Name);
        Assert.Equal(2, gap.Candidates.Count);
        Assert.Empty(ambiguous.Plugins);
    }

    [Fact] public void IdenticalHardPluginCopiesAcrossModFolderAndSearchRootAreOneChoice()
    {
        string besideMod = Path.Combine(_rig.Root, "parent", "Dependency.dll");
        string searchRoot = Path.Combine(_rig.Root, "deps", "Dependency.dll");
        File.Copy(searchRoot, besideMod);

        var plan = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        Assert.True(plan.Ready, string.Join("; ", plan.Gaps.Select(gap => gap.Reason)));
        var dependency = Assert.Single(plan.Plugins);
        Assert.Equal(new[] { besideMod, searchRoot }.Order(StringComparer.Ordinal), dependency.SourcePaths);
        Assert.Equal(dependency.SourcePaths[0], dependency.File);
        string lockFile = Path.Combine(_rig.Root, "equivalent-lock.json");
        plan.Write(lockFile);
        Assert.Equal(dependency.SourcePaths, Assert.Single(NativeDependencyLock.ReadReady(lockFile).Plugins).SourcePaths);

        _rig.Write("parent/Dependency.dll", RegressionRig.Assembly("Dependency", new("example.dependency", "1.4.0")));
        var different = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        var gap = Assert.Single(different.Gaps);
        Assert.Equal("plugin", gap.Kind);
        Assert.Equal(2, gap.Candidates.Count);
    }

    [Fact] public void IdenticalReferencedLibraryWithAnotherFilenameIsOneChoice()
    {
        string mod = _rig.Write("uses/Uses.dll", RegressionRig.Assembly("Uses", new("example.uses"), reference: typeof(FactAttribute)));
        string original = _rig.Write("library-one/xunit.core.dll", RegressionRig.Assembly("xunit.core", null));
        string renamed = Path.Combine(_rig.Root, "library-two", "Renamed.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(renamed)!);
        File.Copy(original, renamed);
        var request = Request(mod);
        request.SearchRoots = [Path.GetDirectoryName(original)!, Path.GetDirectoryName(renamed)!];

        var plan = NativeDependencyResolver.Resolve(request);
        Assert.True(plan.Ready, string.Join("; ", plan.Gaps.Select(gap => gap.Reason)));
        var library = Assert.Single(plan.Plugins);
        Assert.Equal(original, library.File); // Stage the assembly under its real name, not the renamed copy.
        Assert.Equal(new[] { original, renamed }, library.SourcePaths);

        _rig.Write("library-three/xunit.core.dll", RegressionRig.Assembly("xunit.core", null, marker: "different bytes"));
        request.SearchRoots.Add(Path.Combine(_rig.Root, "library-three"));
        var ambiguous = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(ambiguous.Gaps);
        Assert.Equal("assembly", gap.Kind);
        Assert.Equal(3, gap.Candidates.Count);
        Assert.Contains(original, gap.Candidates);
        Assert.Contains(renamed, gap.Candidates);
    }

    [Fact] public void SoftReferenceRequiresExplicitConfirmationBeforeItCanBeOmitted()
    {
        string mod = _rig.Write("soft/Uses.dll", RegressionRig.Assembly("Uses", new("example.uses") { Soft = ["example.soft"] }, reference: typeof(FactAttribute)));
        _rig.Write("soft/xunit.core.dll", RegressionRig.Assembly("xunit.core", new("example.soft")));
        var request = Request(mod);
        request.SearchRoots = [Path.Combine(_rig.Root, "soft")];
        var plan = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal("optional-reference", gap.Kind);
        Assert.Contains("Confirm optionalReferences", gap.Reason);
        Assert.Empty(plan.Plugins);
        request.OptionalReferences = ["xunit.core"];
        var confirmed = NativeDependencyResolver.Resolve(request);
        Assert.True(confirmed.Ready);
        Assert.Empty(confirmed.Plugins);
        Assert.Equal(new[] { "xunit.core" }, confirmed.OptionalReferences);
    }

    [Fact] public void MissingCliCapabilityNamesThePackProblemBeforeAnyInstallIsWritten()
    {
        var request = Request(_rig.Parent);
        request.Capabilities = ["valheim.world/terrain"];
        var error = Assert.Throws<InvalidOperationException>(() => NativeDependencyResolver.Resolve(request));
        Assert.Contains("lacks valheim.world/terrain", error.Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void ManifestPackMissingFromLocalBuildIsAnUnresolvedGap()
    {
        var request = Request(_rig.Parent);
        string coreOnly = Path.Combine(_rig.Root, "cli-core-only");
        Directory.CreateDirectory(coreOnly);
        File.Copy(_rig.Manifest().Cli.Core.File, Path.Combine(coreOnly, "valheimCLI.dll"));
        request.CliFiles = coreOnly;
        var plan = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal("cli", gap.Kind);
        Assert.Equal("Valheim.Cli.Standard.dll", gap.Name);
        Assert.Contains("pinned ValheimCLI build", gap.Reason);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void DuplicatePluginIdentityIsNotSilentlySelected()
    {
        string twin = _rig.Write("twin/Twin.dll", RegressionRig.Assembly("Twin", new("example.mod")));
        var request = Request(_rig.Parent);
        request.Mods.Add(twin);
        var plan = NativeDependencyResolver.Resolve(request);
        Assert.Equal("duplicate-plugin", Assert.Single(plan.Gaps).Kind);
        Assert.Contains("example.mod", plan.Gaps[0].Reason);
    }

    [Fact] public void MissingHardDependencyIsRefusedBeforeAnInstallIsCreated()
    {
        var request = Request(_rig.Parent);
        request.SearchRoots = [];
        var plan = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal("plugin", gap.Kind);
        Assert.Contains("example.dependency", gap.Reason);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void ExplicitlyIncompatiblePairIsRefusedBeforeAnInstallIsCreated()
    {
        string clash = _rig.Write("clash/Clash.dll", RegressionRig.Assembly("Clash",
            new("example.clash") { Incompatible = ["example.mod"] }));
        var request = Request(_rig.Parent);
        request.Mods.Add(clash);
        var plan = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal("incompatible-plugin", gap.Kind);
        Assert.Contains("example.mod", gap.Reason);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void SelectedSoftPartnerActivatesItsAssemblyReference()
    {
        string primary = _rig.Write("soft-primary/Primary.dll", RegressionRig.Assembly("Primary",
            new("example.primary") { Soft = ["example.partner"] }, reference: typeof(FactAttribute)));
        string partner = _rig.Write("soft-partner/xunit.core.dll", RegressionRig.Assembly("xunit.core",
            new("example.partner")));
        var request = Request(primary);
        request.SearchRoots = [Path.GetDirectoryName(partner)!];
        var alone = NativeDependencyResolver.Resolve(request);
        Assert.Equal("optional-reference", Assert.Single(alone.Gaps).Kind);

        request.Mods.Add(partner);
        var together = NativeDependencyResolver.Resolve(request);
        Assert.True(together.Ready, string.Join("; ", together.Gaps.Select(gap => gap.Reason)));
        Assert.Empty(together.Plugins);
        Assert.Equal(new[] { "example.primary", "example.partner" }, together.Mods
            .SelectMany(file => PluginMetadata.Read(file.File).Plugins.Select(plugin => plugin.Guid)));
    }

    [Fact] public void ComparisonMayChangeOnlyThePrimaryMod()
    {
        var before = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        Assert.True(before.Ready);
        var after = new NativeDependencyLock
        {
            Mods = [new("/alternate/Parent.dll", new string('a', 64), "selected mod")],
            Plugins = [.. before.Plugins], CliFiles = [.. before.CliFiles],
            OptionalReferences = [.. before.OptionalReferences],
        };
        before.RequireSameFixedInputs(after);

        after.Plugins = [new("/alternate/Dependency.dll", new string('b', 64), "dependency")];
        Assert.Contains("plugin dependencies differ", Assert.Throws<InvalidDataException>(() => before.RequireSameFixedInputs(after)).Message);
        after.Plugins = [.. before.Plugins];
        after.Mods.Add(new("/alternate/Companion.dll", new string('c', 64), "selected mod"));
        Assert.Contains("companion mods differ", Assert.Throws<InvalidDataException>(() => before.RequireSameFixedInputs(after)).Message);
        after.Mods.RemoveAt(1);
        after.CliFiles.RemoveAt(0);
        Assert.Contains("ValheimCLI files differ", Assert.Throws<InvalidDataException>(() => before.RequireSameFixedInputs(after)).Message);
        after.CliFiles = [.. before.CliFiles];
        after.OptionalReferences.Add("optional.integration");
        Assert.Contains("optional references differ", Assert.Throws<InvalidDataException>(() => before.RequireSameFixedInputs(after)).Message);
    }

    [Fact] public void RemoveModComparisonKeepsSurvivorsAndCliPinnedWhileAllowingDependencyPruning()
    {
        string secondary = _rig.Write("secondary/Secondary.dll", RegressionRig.Assembly("Secondary", new("example.secondary")));
        var request = Request(_rig.Parent);
        request.Mods.Add(secondary);
        var before = NativeDependencyResolver.Resolve(request);
        Assert.True(before.Ready);
        request.Mods.Remove(_rig.Parent);
        var after = NativeDependencyResolver.Resolve(request);
        Assert.True(after.Ready);
        Assert.Empty(after.Plugins); // The primary mod's hard dependency was pruned.
        before.RequireSameExceptRemovedMod(after, _rig.Parent);

        after.Mods[0] = after.Mods[0] with { Sha256 = new string('0', 64) };
        Assert.Contains("remaining selected mods changed", Assert.Throws<InvalidDataException>(() =>
            before.RequireSameExceptRemovedMod(after, _rig.Parent)).Message);
        after.Mods[0] = before.Mods[1];
        after.Plugins.Add(new("/new/Plugin.dll", new string('1', 64), "new dependency"));
        Assert.Contains("new plugin dependency", Assert.Throws<InvalidDataException>(() =>
            before.RequireSameExceptRemovedMod(after, _rig.Parent)).Message);
        after.Plugins.Clear();
        after.CliFiles.RemoveAt(0);
        Assert.Contains("ValheimCLI files changed", Assert.Throws<InvalidDataException>(() =>
            before.RequireSameExceptRemovedMod(after, _rig.Parent)).Message);
    }

    private NativeDependencyRequest Request(string mod) => new()
    {
        Mods = [mod], SearchRoots = [Path.Combine(_rig.Root, "deps")],
        GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(_rig.Game))!,
        BepInExCore = Path.Combine(_rig.Game, "BepInEx", "core"),
        CliManifest = _rig.CliManifest(save: true), CliFiles = Path.Combine(_rig.Root, "cli"),
        Capabilities = ["valheim.session/state", "valheim.session/save"],
    };
}
