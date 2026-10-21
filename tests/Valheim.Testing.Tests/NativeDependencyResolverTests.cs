using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Valheim.Testing.GameSessions;

public sealed class NativeDependencyResolverTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public void CampaignCarriesAndValidatesAClientArchitectureOverride()
    {
        string file = Path.Combine(_rig.Root, "architecture-campaign.json");
        File.WriteAllText(file, """{"clients":{"client":{"dependencyLock":"client.lock","architecture":"arm64"}}}""");
        Assert.Equal("arm64", HostedCampaignManifest.Read(file).Clients["client"].Architecture);
        File.WriteAllText(file, """{"clients":{"client":{"dependencyLock":"client.lock","architecture":"native"}}}""");
        Assert.Contains("architecture", Assert.Throws<InvalidDataException>(() => HostedCampaignManifest.Read(file)).Message);
        File.WriteAllText(file, """{"server":{"dependencyLock":"server.lock","architecture":"arm64"}}""");
        Assert.Contains("dedicated server", Assert.Throws<InvalidDataException>(() => HostedCampaignManifest.Read(file)).Message);
    }

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

    [ClientHostFact] public void HardPluginAndLibraryClosureArePinnedFromExplicitRoots()
    {
        var request = Request(_rig.Parent);
        var plan = NativeDependencyResolver.Resolve(request);
        Assert.True(plan.Ready, string.Join("; ", plan.Gaps.Select(gap => gap.Reason)));
        Assert.Equal(new[] { "Dependency.dll" }, plan.Plugins.Select(file => Path.GetFileName(file.File)));
        Assert.Equal(new[] { "valheimCLI.dll" }, plan.CliFiles.Select(file => Path.GetFileName(file.File)));
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
        _rig.Regression(environment).Stage("parent");
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

    [Fact] public void CampaignPluginPinsLeaveNonLoadingScriptsOutUnlessThePlanPinsOne()
    {
        string script = _rig.Write("script-pins/ScriptMod.dll", RegressionRig.Assembly("ScriptMod", new("example.script")));
        string engine = _rig.Write("script-pins/ScriptEngine.dll", RegressionRig.Assembly("ScriptEngine", new(OwnedClientPreflight.ScriptEngine)));
        string config = _rig.Write("script-pins/scriptengine.cfg", System.Text.Encoding.UTF8.GetBytes("[General]\nLoadOnStart = true\n"));
        var files = new List<HostedRuntimeFile> { new(script, "BepInEx/scripts/ScriptMod.dll") };

        Assert.Empty(HostedCampaignPreparation.PluginPins("server", files));
        Assert.Contains("does not pin ScriptEngine", Assert.Throws<InvalidOperationException>(() =>
            HostedCampaignPreparation.PluginPins("server", files, new Dictionary<string, string> { ["example.script"] = FileHash.Md5(script) })).Message);
        files.Add(new(engine, "BepInEx/plugins/ScriptEngine.dll"));
        Assert.DoesNotContain("example.script", HostedCampaignPreparation.PluginPins("server", files));
        Assert.Contains("LoadOnStart defaults to false", Assert.Throws<InvalidOperationException>(() =>
            HostedCampaignPreparation.PluginPins("server", files, new Dictionary<string, string> { ["example.script"] = FileHash.Md5(script) })).Message);
        files.Add(new(config, "BepInEx/config/" + OwnedClientPreflight.ScriptEngineConfig));

        var pins = HostedCampaignPreparation.PluginPins("server", files);
        Assert.Equal(FileHash.Md5(script), pins["example.script"]);
        Assert.Equal(FileHash.Md5(engine), pins[OwnedClientPreflight.ScriptEngine]);
    }

    [Fact] public async Task HostedCampaignPreparesServerAndAnyNumberOfNamedClientsAndRetiresTheirCopies()
    {
        string parent = _rig.Write("campaign/Server.dll", RegressionRig.Assembly("Server", new("example.server")));
        string client = _rig.Write("campaign/Client.dll", RegressionRig.Assembly("Client", new("example.client")));
        string serverLock = Lock(parent, "server"), clientLock = Lock(client, "client");
        string[] stores = [Store("one", 101), Store("two", 202), Store("three", 303)];
        string world = Path.Combine(_rig.Root, "campaign-world");
        FakeInstalls.World(world);
        var hosts = new Dictionary<string, FakeServerHost>(StringComparer.Ordinal);
        foreach (string name in new[] { "server", "client-a", "client-b", "client-c" })
        {
            var host = new FakeServerHost(name, Path.Combine(_rig.Root, "mirror-" + name), windows: true);
            hosts[name] = host;
            host.SteamUserReply = "VT-STEAMUSER account " + (name == "client-b" ? 202 : name == "client-c" ? 303 : 101) + "\n";
            string source = host.Local(@"C:\game\source");
            if (name == "server") FakeInstalls.Server(source); else FakeInstalls.Client(source);
            File.WriteAllText(Path.Combine(source, name == "server" ? GameLaunch.ServerWindowsExecutable : GameLaunch.ClientWindowsExecutable), "game");
            File.WriteAllText(Path.Combine(source, "winhttp.dll"), "MZ target_assembly");
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
            name, host, roles = new[] { role }, install, runtime = role == "server" ? @"C:\runs" : @"C:\client-runs", cliPort = port, localCliPort = port + 1000, gamePort = role == "server" ? 2456 : 0,
        };
        object Inventory(params object[] environments) => new
        {
            hosts = hosts.Keys.ToDictionary(name => name, name => new { kind = "ssh", platform = "windows", shell = "pwsh",
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
                // client-c leaves its folders out: they are its Windows host's standard ones, resolved there.
                ["client-c"] = new { dependencyLock = clientLock, character = new { store = stores[2], registeredName = "three", fileName = "vt-three" } },
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
        var hostReport = await HostedCampaignPreparation.InspectAsync(manifestFile, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        Assert.Contains(hostReport.Problems, problem => problem.Actor == "client-a" && problem.Input == "Steam identity");
        // client-c's standard folders do not exist yet: refused, naming each path tried on its host.
        var folders = Assert.Single(hostReport.Problems, problem => problem.Actor == "client-c" && problem.Input == "character folders");
        Assert.Contains(@"characters_local (tried C:\Users\tester\AppData\LocalLow\IronGate\Valheim\characters_local)", folders.Message);
        Assert.Contains(@"Steam userdata (tried C:\Program Files (x86)\Steam\userdata)", folders.Message);
        Assert.Contains(hostReport.Problems, problem => problem.Actor == "clients" && problem.Input == "Steam identities" && problem.Message.Contains("client-b, client-c"));
        Assert.All(hosts.Values, host => Assert.DoesNotContain(host.Scripts, script => script is "ship" or "copy" or "start"));
        var identities = await Assert.ThrowsAsync<ArgumentException>(() => HostedCampaignPreparation.PrepareAsync(manifestFile,
            Path.Combine(_rig.Root, "bad-identities"), TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null));
        Assert.Contains("client-a Steam identity", identities.Message);
        Assert.Contains("client-b, client-c use the same signed-in Steam account", identities.Message);
        Assert.All(hosts.Values, host => Assert.DoesNotContain("ship", host.Scripts));
        hosts["client-a"].SteamUserReply = "VT-STEAMUSER account 101\n";
        hosts["client-b"].SteamUserReply = "VT-STEAMUSER account 202\n";
        Directory.CreateDirectory(hosts["client-c"].Local(@"C:\Users\tester\AppData\LocalLow\IronGate\Valheim\characters_local"));
        Directory.CreateDirectory(hosts["client-c"].Local(@"C:\Program Files (x86)\Steam\userdata"));
        var resolvedReport = await HostedCampaignPreparation.InspectAsync(manifestFile, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        Assert.True(resolvedReport.Ready, string.Join("; ", resolvedReport.Problems.Select(problem => problem.Message)));
        var clientC = resolvedReport.Actors.Single(actor => actor.Name == "client-c");
        Assert.Equal((@"C:\Users\tester\AppData\LocalLow\IronGate\Valheim\characters_local", @"C:\Program Files (x86)\Steam\userdata"),
            (clientC.CharactersDirectory, clientC.SteamUserDataDirectory));
        Assert.Equal(@"C:\save\characters_local", resolvedReport.Actors.Single(actor => actor.Name == "client-a").CharactersDirectory);
        // Every actor gets an owned profile: only selected loader files are shipped, every source-game byte stays as it was,
        // and all four owned profiles are retired.
        static string SourceSnapshot(FakeServerHost host)
        {
            string source = host.Local(@"C:\game\source");
            return string.Join("\n", Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(source, file).Replace('\\', '/') + " " + FileHash.Sha256(file))
                .OrderBy(line => line, StringComparer.Ordinal));
        }
        var originalSources = hosts.ToDictionary(pair => pair.Key, pair => SourceSnapshot(pair.Value));
        var overlap = new Overlap();
        foreach (var host in hosts.Values) host.BeforeShip = overlap.EnterAsync;
        string output = Path.Combine(_rig.Root, "prepared");
        string[] prepared;
        await using (var campaign = await HostedCampaignPreparation.PrepareAsync(manifestFile, output, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null))
        {
            var profile = campaign.Environment;
            Assert.Equal(4, campaign.SourceChecks.Count);
            Assert.Equal(4, hosts.Values.Sum(host => host.Scripts.Count(script => script == "profile-seed")));
            Assert.DoesNotContain(hosts.Values, host => host.Scripts.Contains("copy"));
            prepared = [profile.Server!.Install, profile.Clients["client-a"].Install,
                profile.Clients["client-b"].Install, profile.Clients["client-c"].Install];
            Assert.Equal(4, campaign.Listings.Count);
            Assert.Contains("BepInEx/plugins/Server.dll", campaign.Listings["server"].Files.Keys);
            Assert.DoesNotContain("BepInEx/plugins/Server.dll", campaign.Listings["client-a"].Files.Keys);
            Assert.Contains("BepInEx/plugins/Client.dll", campaign.Listings["client-b"].Files.Keys);
            Assert.Contains("BepInEx/config/valheimCLI.valheimCLI.cfg", campaign.Listings["client-a"].Files.Keys);
            Assert.Contains("AllowOnServerClients = true", File.ReadAllText(Path.Combine(output, "inputs", "client-a", "valheimCLI.valheimCLI.cfg")));
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
            Assert.True(File.Exists(Path.Combine(plan.World.Source, "worlds_local", "Campaign", "_main.1.fwl2")));
            Assert.True(File.Exists(Path.Combine(plan.World.Source, "worlds_local", "Campaign", FakeInstalls.ChunkFile)));
            WorldFixture.Verify(plan.World.Source, plan.World.Sha256);
            Assert.Equal(profile.Server.Install, plan.Runtime.Source);
            Assert.Equal("vt-one", clients["client-a"].Character);
            Assert.Equal("absent", clients["client-a"].Pins["example.server"]); // Binding keeps what a client pins absent.
            Assert.Contains("example.client", clients["client-a"].Pins.Keys);
            Assert.Equal("test-server.example:2456", clients["client-b"].Join);
            Assert.Equal("vt-three", clients["client-c"].Character);
            Assert.All(clients.Values, role => Assert.True(File.Exists(role.CliManifest)));
            // #808: a bound client's static ValheimCLI check reads its profile on its own host, which this machine does not have.
            var remote = clients["client-b"];
            Assert.False(Directory.Exists(remote.LoaderRoot));
            Assert.Equal(new[] { "BepInEx/plugins/valheimCLI.dll" }, remote.CheckCliManifest()!.Files);
            string cli = hosts["client-b"].Local(HostPath.Join(remote.LoaderRoot, "BepInEx/plugins/valheimCLI.dll")), aside = cli + ".aside";
            File.Move(cli, aside);
            Assert.Contains("is not installed in BepInEx/plugins or BepInEx/scripts of " + remote.LoaderRoot + " on client-b",
                Assert.Throws<InvalidOperationException>(() => remote.CheckCliManifest()).Message);
            File.Move(aside, cli);
            Assert.True(File.Exists(hosts["client-a"].Local(@"C:\save\characters_local\vt-one.fch")));
            Assert.True(File.Exists(hosts["client-b"].Local(@"C:\save\characters_local\vt-two.fch")));
            Assert.True(File.Exists(hosts["client-c"].Local(@"C:\Users\tester\AppData\LocalLow\IronGate\Valheim\characters_local\vt-three.fch")));
            Assert.All(new[] { "server", "client-a", "client-b", "client-c" }, name => Assert.NotEmpty(hosts[name].Claims));
        }
        Assert.True(overlap.Seen, "Independent actors should stage concurrently, not wait for each prior actor's profile.");
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
        Assert.False(File.Exists(hosts["client-c"].Local(@"C:\Users\tester\AppData\LocalLow\IronGate\Valheim\characters_local\vt-three.fch")));
        Assert.False(File.Exists(Path.Combine(output, "profile.json"))); // The prepared environment, with the observed Steam IDs, stays in memory.

        Assert.All(hosts, pair => Assert.Equal(originalSources[pair.Key], SourceSnapshot(pair.Value)));

        // A dedicated server and one client may share a machine. Their installs are separate, but setup should
        // still overlap under one host claim rather than serialising two profiles.
        var sharedHost = hosts["server"];
        string sharedClientSource = sharedHost.Local(@"C:\game\client-source");
        FakeInstalls.Client(sharedClientSource);
        File.WriteAllText(Path.Combine(sharedClientSource, GameLaunch.ClientWindowsExecutable), "game");
        File.WriteAllText(Path.Combine(sharedClientSource, "winhttp.dll"), "MZ target_assembly");
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
        var sharedOverlap = new Overlap();
        sharedHost.BeforeShip = sharedOverlap.EnterAsync;
        int claimsBefore = sharedHost.Claims.Count, checksBefore = 0;
        await using (var campaign = await HostedCampaignPreparation.PrepareAsync(sameHostManifestFile,
            Path.Combine(_rig.Root, "shared-host-prepared"), TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null))
        {
            Assert.Equal(claimsBefore + 1, sharedHost.Claims.Count);
            Assert.True(sharedOverlap.Seen, "Server and client setup on one host should overlap under its single claim.");
            Assert.Contains("BepInEx/plugins/Server.dll", campaign.Listings["server"].Files.Keys);
            Assert.DoesNotContain("BepInEx/plugins/Server.dll", campaign.Listings["client-a"].Files.Keys);
            Assert.True(File.Exists(sharedHost.Local(@"C:\save\characters_local\vt-one.fch")));
            checksBefore = sharedHost.Scripts.Count(script => script == "game-process");
        }
        Assert.False(File.Exists(sharedHost.Local(@"C:\save\characters_local\vt-one.fch")));
        // Retiring two installs and a character on one host takes its lock once and checks its game processes once (#257).
        Assert.Equal(claimsBefore + 2, sharedHost.Claims.Count);
        Assert.Equal(checksBefore + 1, sharedHost.Scripts.Count(script => script == "game-process"));

        // A server copy kept because its process may still run is not one of the runtimes the shared-host retirement asks
        // to be stopped. Its source verdict is incomplete, while the character and client copy on that host are still retired.
        var runningCampaign = await HostedCampaignPreparation.PrepareAsync(sameHostManifestFile,
            Path.Combine(_rig.Root, "running-server-prepared"), TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        var serverCopy = runningCampaign.Copies.Single(copy => copy.Actor == "server");
        var sharedClientCopy = runningCampaign.Copies.Single(copy => copy.Actor == "client-a");
        var runningReport = new ScenarioReport("server may still run");
        var runningRetirement = new RunRetirement(runningReport, "");
        await runningRetirement.HostAsync(serverCopy.Host, sharedHost, runningCampaign.LoaderListings["server"],
            serverCopy.Runtime, launched: true, serverStopped: false);
        int runsBeforeRetirement = sharedHost.Runs.Count;
        Assert.Empty(await runningRetirement.CampaignAsync(runningCampaign, ["server"]));
        var processCheck = Assert.Single(sharedHost.Runs.Skip(runsBeforeRetirement).Where(run => run.Script == "game-process"));
        Assert.DoesNotContain(serverCopy.Runtime, processCheck.Variables["runtime"], StringComparison.Ordinal);
        Assert.Contains(sharedClientCopy.Runtime, processCheck.Variables["runtime"], StringComparison.Ordinal);
        Assert.True(Directory.Exists(sharedHost.Local(serverCopy.Runtime)));
        Assert.False(Directory.Exists(sharedHost.Local(sharedClientCopy.Runtime)));
        Assert.False(File.Exists(sharedHost.Local(@"C:\save\characters_local\vt-one.fch")));
        var unknownServerSource = Assert.Single(runningReport.Steps, step =>
            step.Name.Contains("server's source install after the run", StringComparison.Ordinal));
        Assert.False(unknownServerSource.Passed);
        Assert.Contains("may still run", unknownServerSource.Error, StringComparison.OrdinalIgnoreCase);
        await HostedRuntimeStage.RetireAsync(sharedHost, serverCopy.Runtime, serverCopy.Stage, TimeSpan.FromSeconds(30));
        runningCampaign.Retired = true;
        await runningCampaign.DisposeAsync();

        // A game can exit while its Mac GUI agent stays loaded. That host's character and profile
        // must remain journalled until recovery proves the agent was removed.
        var blockedCampaign = await HostedCampaignPreparation.PrepareAsync(sameHostManifestFile,
            Path.Combine(_rig.Root, "blocked-agent-prepared"), TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        string blockedRuntime = blockedCampaign.Copies.Single(copy => copy.Actor == "client-a").Runtime;
        var blockedReport = new ScenarioReport("blocked source verdict");
        var blocked = await new RunRetirement(blockedReport, "").CampaignAsync(blockedCampaign, ["server"], blockedHosts: ["server"]);
        Assert.Single(blocked);
        Assert.Contains("Mac GUI agent", blocked[0].Message + blocked[0].InnerException?.Message);
        Assert.True(File.Exists(sharedHost.Local(@"C:\save\characters_local\vt-one.fch")));
        Assert.True(Directory.Exists(sharedHost.Local(blockedRuntime)));
        var incomplete = Assert.Single(blockedReport.Steps, step => step.Name.Contains("client-a's source install after the run", StringComparison.Ordinal) &&
            step.Name.Contains("unknown: stop not proven", StringComparison.Ordinal));
        Assert.False(incomplete.Passed);
        Assert.Contains("unknown", incomplete.Error, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("unknown: stop not proven", blockedReport.Provenance["sourceIntegrity.client-a"]);
        await blockedCampaign.DisposeAsync();
        Assert.False(File.Exists(sharedHost.Local(@"C:\save\characters_local\vt-one.fch")));
        Assert.False(Directory.Exists(sharedHost.Local(blockedRuntime)));

        // VALHEIM_TESTING_KEEP_RUNTIME=1 keeps every actor's install (the disposable characters still go), and a second retire
        // by the same owner touches nothing, not even what the first one kept.
        var keptCampaign = await HostedCampaignPreparation.PrepareAsync(sameHostManifestFile, Path.Combine(_rig.Root, "kept-prepared"),
            TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        var retirement = new RunRetirement(null, "");
        int metadataBefore = hosts.Values.SelectMany(host => host.Runs)
            .Count(run => run.Script == "list" && run.Variables["metadata"] == "1");
        RunRetirement.KeepOverride.Value = true;
        try { Assert.Empty(await retirement.CampaignAsync(keptCampaign, [])); }
        finally { RunRetirement.KeepOverride.Value = null; }
        Assert.Equal(4, keptCampaign.Copies.Count);
        Assert.All(keptCampaign.Copies, copy => Assert.True(Directory.Exists(hosts[copy.Host].Local(copy.Runtime)), copy.Runtime));
        Assert.All(keptCampaign.Copies, copy => Assert.True(retirement.Kept(copy.Host.ToLowerInvariant(), copy.Runtime)));
        Assert.False(File.Exists(sharedHost.Local(@"C:\save\characters_local\vt-one.fch")));
        // Each settlement lists its source install once and its moved-files folder once (#612).
        Assert.Equal(metadataBefore + 2 * keptCampaign.SourceChecks.Count, hosts.Values.SelectMany(host => host.Runs)
            .Count(run => run.Script == "list" && run.Variables["metadata"] == "1"));
        int scripts = hosts.Values.Sum(host => host.Scripts.Count);
        Assert.Empty(await retirement.CampaignAsync(keptCampaign, []));
        Assert.Equal(scripts, hosts.Values.Sum(host => host.Scripts.Count));
        Assert.All(keptCampaign.Copies, copy => Assert.True(Directory.Exists(hosts[copy.Host].Local(copy.Runtime))));
    }

    // A ready campaign on two ssh hosts: a Windows dedicated server and a macOS client, each install a Valheim 1.0.17 (network version 40).
    private (string Manifest, Dictionary<string, FakeServerHost> Hosts, string MacSource) TwoHostCampaign(string prefix)
    {
        string serverMod = _rig.Write(prefix + "/Server.dll", RegressionRig.Assembly("Server", new("example.server")));
        string clientMod = _rig.Write(prefix + "/Client.dll", RegressionRig.Assembly("Client", new("example.client")));
        string serverLock = Lock(serverMod, prefix + "-server"), clientLock = Lock(clientMod, prefix + "-client");
        string store = Store("macone", 404, prefix + "/");
        string world = Path.Combine(_rig.Root, prefix + "-world");
        FakeInstalls.World(world);
        var server = new FakeServerHost("server", Path.Combine(_rig.Root, prefix + "-mirror-server"), windows: true);
        string serverSource = server.Local(@"C:\game\source");
        FakeInstalls.Server(serverSource);
        File.WriteAllText(Path.Combine(serverSource, GameLaunch.ServerWindowsExecutable), "game");
        File.WriteAllText(Path.Combine(serverSource, "winhttp.dll"), "MZ target_assembly");
        File.WriteAllText(Path.Combine(serverSource, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var mac = new FakeServerHost("mac", Path.Combine(_rig.Root, prefix + "-mirror-client")) { SteamUserReply = "VT-STEAMUSER account 404\n" };
        string macSource = mac.Local("/game/source");
        foreach (var (relative, text) in new[] { ("Valheim.app/Contents/MacOS/Valheim", "game"), ("BepInEx/core/BepInEx.dll", "bepinex"), ("BepInEx/core/BepInEx.Preloader.dll", "preloader"), ("libdoorstop.dylib", "doorstop") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(macSource, relative))!);
            File.WriteAllText(Path.Combine(macSource, relative), text);
        }
        string macManaged = Path.Combine(macSource, "Valheim.app", "Contents", "Resources", "Data", "Managed");
        Directory.CreateDirectory(macManaged);
        File.WriteAllBytes(Path.Combine(macManaged, InstallPins.GameAssemblyName), FakeInstalls.GameAssembly());
        Directory.CreateDirectory(mac.Local("/home/t/characters_local"));
        Directory.CreateDirectory(mac.Local("/home/t/userdata"));
        var hosts = new Dictionary<string, FakeServerHost> { ["server"] = server, ["mac"] = mac };
        string inventoryFile = Path.Combine(_rig.Root, prefix + "-inventory.json");
        File.WriteAllText(inventoryFile, JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object>
            {
                ["server"] = new { kind = "ssh", platform = "windows", shell = "pwsh", @lock = @"C:\locks\campaign.lock", destination = "test@server" },
                ["mac"] = new { kind = "ssh", platform = "macos", shell = "bash", @lock = "/locks/campaign.lock", destination = "test@mac" },
            },
            environments = new object[]
            {
                new { name = "server", host = "server", roles = new[] { "server" }, install = @"C:\game\source", runtime = @"C:\runs", cliPort = 5577, localCliPort = 6577, gamePort = 2456 },
                new { name = "mac-client", host = "mac", roles = new[] { "client" }, install = "/game/source", runtime = "/runs", cliPort = 5578, localCliPort = 6578, architecture = "x64" },
            },
            leaseHost = "server", leaseDirectory = @"C:\leases",
        }));
        string manifestFile = Path.Combine(_rig.Root, prefix + ".json");
        File.WriteAllText(manifestFile, JsonSerializer.Serialize(new
        {
            inventory = inventoryFile, world, join = "test-server.example:2456",
            server = new { dependencyLock = serverLock },
            clients = new Dictionary<string, object> { ["player"] = new { dependencyLock = clientLock,
                character = new { store, registeredName = "macone", fileName = "vt-mac", charactersLocalDirectory = "/home/t/characters_local", steamUserDataDirectory = "/home/t/userdata" } } },
        }));
        return (manifestFile, hosts, macSource);
    }

    // Run A (#258): a run never makes macOS show a dialog. A macOS client's source bundle with a changed sealed file is refused
    // in the preflight; one with files only added inside it is fine, since the profile launches that install, which macOS already
    // let run. The bundle check is a read-only bash script (MacAppBundle.Bash), answered here by the fake host; the real script
    // runs on CI's macOS leg (MacAppBundleTests).
    [Fact] public async Task AMacClientsSourceMustBeOneMacOSLaunchesWithoutADialog()
    {
        var (manifestFile, hosts, macSource) = TwoHostCampaign("mac-campaign");
        var mac = hosts["mac"];
        // A sealed file changed in the source: refused in the preflight, read only, naming the file.
        mac.MacBundleInspect = "VT-BUNDLE broken 1 " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("file modified: /game/source/Valheim.app/Contents/Info.plist"));
        var broken = await HostedCampaignPreparation.InspectAsync(manifestFile, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        var refusal = Assert.Single(broken.Problems, problem => problem.Actor == "player" && problem.Input == "macOS app bundle");
        Assert.Contains("Info.plist", refusal.Message);
        Assert.Contains("Verify the game's files in Steam", refusal.Message);

        // Files only added inside it (BepInEx preloader logs): fixable; the preflight passes.
        mac.MacBundleInspect = "VT-BUNDLE fixable 2 " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Contents/MacOS/preloader_1.log\nContents/MacOS/preloader_2.log"));
        var fixable = await HostedCampaignPreparation.InspectAsync(manifestFile, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        Assert.True(fixable.Ready, string.Join("; ", fixable.Problems.Select(problem => problem.Message)));

        // Prepared: the profile launches the source bundle, which is only ever read, and nothing is copied into a new bundle.
        await using (var campaign = await HostedCampaignPreparation.PrepareAsync(manifestFile, Path.Combine(_rig.Root, "mac-accepted"), TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null))
        {
            Assert.Equal("/game/source", campaign.Environment.Clients["player"].PreparedGameRoot);
            Assert.Contains("Valheim.app/Contents/MacOS/Valheim", campaign.Listings["player"].Files.Keys);
            Assert.DoesNotContain("Valheim.app/Contents/MacOS/Valheim", campaign.LoaderListings["player"].Files.Keys);
        }
        Assert.All(mac.Runs.Where(run => run.Script == "mac-bundle"), run => Assert.Equal("/game/source/Valheim.app", run.Variables["app"]));
        Assert.DoesNotContain("copy", mac.Scripts);
        Assert.Equal("game", File.ReadAllText(Path.Combine(macSource, "Valheim.app/Contents/MacOS/Valheim")));
    }

    // #860: Valheim refuses a join across network versions, so the host check reads each install's game assembly on its own host and
    // refuses a mismatched or unknown pair before any profile is staged. The same inspection serves session check --hosts, campaign
    // preparation and one-shot server-load.
    [Fact] public async Task InstallsOnAnotherNetworkVersionAreRefusedBeforeAnythingIsStaged()
    {
        var (manifestFile, hosts, macSource) = TwoHostCampaign("versions");
        string clientAssembly = Path.Combine(macSource, "Valheim.app", "Contents", "Resources", "Data", "Managed", InstallPins.GameAssemblyName);
        string serverAssembly = Path.Combine(hosts["server"].Local(@"C:\game\source"), "valheim_server_Data", "Managed", InstallPins.GameAssemblyName);
        Task<CampaignPreflightReport> Inspect() => HostedCampaignPreparation.InspectAsync(manifestFile, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);

        // One network version on both hosts: ready, and each actor carries the build read on its host.
        var ready = await Inspect();
        Assert.True(ready.Ready, string.Join("; ", ready.Problems.Select(problem => problem.Message)));
        Assert.All(ready.Actors, actor => Assert.Equal(("1.0.17", (uint?)40), (actor.GameVersion, actor.NetworkVersion)));
        Assert.All(hosts.Values, host => Assert.Contains(host.Runs, run => run.Script == "preflight-read" && run.Variables["path"].EndsWith(InstallPins.GameAssemblyName)));

        // The client updated first (1.0.20, network version 41): refused naming both installs, and nothing staged on either host.
        File.WriteAllBytes(clientAssembly, FakeInstalls.GameAssembly("1.0.20", 41));
        var mismatch = await Inspect();
        var problem = Assert.Single(mismatch.Problems);
        Assert.Equal(("player", "game version"), (problem.Actor, problem.Input));
        Assert.StartsWith("player's install /game/source on mac is Valheim 1.0.20 (network version 41), but server's install C:\\game\\source on server " +
            "is Valheim 1.0.17 (network version 40).", problem.Message);
        Assert.Equal([(uint?)40, 41], mismatch.Actors.OrderBy(actor => actor.Name == "server" ? 0 : 1).Select(actor => actor.NetworkVersion));
        await Assert.ThrowsAsync<ArgumentException>(() => HostedCampaignPreparation.PrepareAsync(manifestFile, Path.Combine(_rig.Root, "versions-refused"),
            TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null));
        Assert.All(hosts.Values, host => Assert.DoesNotContain(host.Scripts, script => script is "copy" or "profile-seed" or "apply-stage"));
        // Distinct game versions on one network version still join.
        File.WriteAllBytes(clientAssembly, FakeInstalls.GameAssembly("1.0.18", 40));
        Assert.True((await Inspect()).Ready);

        // A server assembly that is not the game's: unknown, refused, never inferred.
        File.WriteAllText(serverAssembly, "game build 1");
        var unknown = Assert.Single((await Inspect()).Problems);
        Assert.Equal(("server", "game version"), (unknown.Actor, unknown.Input));
        Assert.Contains(@"is unknown: C:\game\source\valheim_server_Data\Managed\assembly_valheim.dll on server is not a readable .NET assembly", unknown.Message);

        // A file that changed after the install was listed is not the listed build.
        File.WriteAllBytes(serverAssembly, FakeInstalls.GameAssembly());
        hosts["server"].BeforeScript = script => { if (script == "preflight-read") File.WriteAllBytes(serverAssembly, FakeInstalls.GameAssembly("1.0.20", 41)); };
        Assert.Contains("changed after the install was listed", Assert.Single((await Inspect()).Problems).Message);
    }

    // #256's acceptance in one preflight: six independent faults, each refused under its own actor and input, all in one
    // report, before any host is written to. A bad account (an unreadable signed-in Steam identity), a loader mix (a Doorstop 4
    // proxy beside a Doorstop 3 file), a missing pack (a lock whose ValheimCLI set lacks one), a wrong package (a client's
    // loader package for Linux on a Windows host), a stale world UID, and conflicting use of the host the server and a client
    // share (a game client already running in its session). The same campaign with the faults removed is ready.
    [Fact] public async Task OnePreflightReportsSixIndependentFaultsBeforeAnyHostWrite()
    {
        string serverMod = _rig.Write("six/Server.dll", RegressionRig.Assembly("Server", new("example.server")));
        string clientMod = _rig.Write("six/Client.dll", RegressionRig.Assembly("Client", new("example.client")));
        string serverLock = Lock(serverMod, "six-server"), clientLock = Lock(clientMod, "six-client");
        // Missing pack: the client's lock with one ValheimCLI pack taken out of its set.
        var withoutPack = JsonNode.Parse(File.ReadAllText(clientLock))!;
        var cliFiles = withoutPack["cliFiles"]!.AsArray();
        cliFiles.RemoveAt(cliFiles.Count - 1);
        string clientLockWithoutPack = Path.Combine(_rig.Root, "six-client-without-pack-lock.json");
        File.WriteAllText(clientLockWithoutPack, withoutPack.ToJsonString());
        string storeA = Store("one", 101, "six/"), storeB = Store("two", 202, "six/");
        string world = Path.Combine(_rig.Root, "six-world");
        FakeInstalls.World(world);
        // Wrong package: a reviewed loader package complete for Linux only.
        string linuxLoader = Path.Combine(_rig.Root, "six-linux-loader");
        foreach (string file in new[] { "BepInEx/core/BepInEx.Preloader.dll", "BepInEx/core/BepInEx.dll", "doorstop_libs/libdoorstop_x64.so" })
        {
            string path = Path.Combine(linuxLoader, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
        string linuxPackage = Path.Combine(_rig.Root, "six-linux-loader.json");
        BepInExLoaderPackage.Capture(linuxLoader, "BepInExPack_Valheim", "5.4.2333").Write(linuxPackage);

        // Two Windows hosts: "pc" runs the server and client-a, "laptop" client-b.
        var hosts = new Dictionary<string, FakeServerHost>(StringComparer.Ordinal);
        foreach (string name in new[] { "pc", "laptop" })
        {
            var host = new FakeServerHost(name, Path.Combine(_rig.Root, "six-mirror-" + name), windows: true);
            hosts[name] = host;
            host.SteamUserReply = "VT-STEAMUSER account " + (name == "pc" ? 101 : 202) + "\n";
            foreach (var (install, server) in new[] { (@"C:\game\server", true), (@"C:\game\client", false) })
            {
                string source = host.Local(install);
                if (server) FakeInstalls.Server(source); else FakeInstalls.Client(source);
                File.WriteAllText(Path.Combine(source, server ? GameLaunch.ServerWindowsExecutable : GameLaunch.ClientWindowsExecutable), "game");
                File.WriteAllText(Path.Combine(source, "winhttp.dll"), "MZ target_assembly");
                File.WriteAllText(Path.Combine(source, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
            }
            Directory.CreateDirectory(host.Local(@"C:\save\characters_local"));
            Directory.CreateDirectory(host.Local(@"C:\Steam\userdata"));
        }
        object Recipe(string name, string host, string role, string install, int port) => new
        {
            name, host, roles = new[] { role }, install, runtime = role == "server" ? @"C:\runs" : @"C:\client-runs", cliPort = port, localCliPort = port + 1000, gamePort = role == "server" ? 2456 : 0,
        };
        string inventory = Path.Combine(_rig.Root, "six-inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = hosts.Keys.ToDictionary(name => name, name => new { kind = "ssh", platform = "windows", shell = "pwsh", @lock = @"C:\locks\six.lock", destination = "test@" + name }),
            environments = new[] { Recipe("pc-server", "pc", "server", @"C:\game\server", 5577), Recipe("pc-client", "pc", "client", @"C:\game\client", 5578),
                Recipe("laptop-client", "laptop", "client", @"C:\game\client", 5579) },
            leaseHost = "pc", leaseDirectory = @"C:\leases",
        }));
        string Campaign(string file, string worldUid, string bLock, string? bLoader) => WriteJson(file, new
        {
            inventory, world, worldUid, join = "pc.example:2456",
            server = new { dependencyLock = serverLock },
            clients = new Dictionary<string, object>
            {
                ["client-a"] = new { dependencyLock = clientLock, environmentCandidates = new[] { "pc-client" }, character = Character(storeA, "one", "vt-one") },
                ["client-b"] = bLoader == null
                    ? new { dependencyLock = bLock, environmentCandidates = new[] { "laptop-client" }, character = Character(storeB, "two", "vt-two") }
                    : (object)new { dependencyLock = bLock, environmentCandidates = new[] { "laptop-client" }, loaderPackage = bLoader, character = Character(storeB, "two", "vt-two") },
            },
        });
        string WriteJson(string name, object value)
        {
            string path = Path.Combine(_rig.Root, name);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
            return path;
        }

        // The six faults at once.
        string faulty = Campaign("six-faulty.json", worldUid: "9999", bLock: clientLockWithoutPack, bLoader: linuxPackage);
        hosts["pc"].SteamUserReply = "VT-STEAMUSER unreadable\n";
        File.WriteAllText(Path.Combine(hosts["pc"].Local(@"C:\game\server"), "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        hosts["pc"].GameActive = true;
        var report = await HostedCampaignPreparation.InspectAsync(faulty, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        Assert.False(report.Ready);
        void Has(string actor, string input, string text) => Assert.True(report.Problems.Any(problem => problem.Actor == actor && problem.Input == input && problem.Message.Contains(text, StringComparison.Ordinal)),
            $"expected {actor} {input} containing \"{text}\" in: " + string.Join(" | ", report.Problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}")));
        Has("client-a", "Steam identity", "");                    // bad account
        Has("server", "game and loader", "Doorstop 4");           // loader mix
        Has("client-b", "dependencies and ValheimCLI", "");        // missing pack
        Has("client-b", "game and loader", "does not match the host platform"); // wrong package
        Has("server", "world fixture", "world UID");              // stale world UID
        Has("pc", "session", "conflicting");                      // server + client on one host, already in use
        Assert.All(hosts.Values, host => Assert.DoesNotContain(host.Scripts, script => script is "ship" or "copy" or "start" or "apply-stage" or "character-install"));
        // The static part through the CLI (no host): the missing pack and the stale UID together, exit 3.
        using var output = new StringWriter();
        Assert.Equal(3, await SessionCommand.RunAsync(["check", faulty], output, new StringWriter()));
        Assert.Contains("REFUSED client-b dependencies and ValheimCLI", output.ToString());
        Assert.Contains("REFUSED server world fixture", output.ToString());

        // The same campaign without the faults: server and client on one host are no conflict when nothing runs there.
        string clean = Campaign("six-clean.json", worldUid: "4242", bLock: clientLock, bLoader: null);
        hosts["pc"].SteamUserReply = "VT-STEAMUSER account 101\n";
        File.WriteAllText(Path.Combine(hosts["pc"].Local(@"C:\game\server"), "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        hosts["pc"].GameActive = false;
        var ready = await HostedCampaignPreparation.InspectAsync(clean, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        Assert.True(ready.Ready, string.Join("; ", ready.Problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}")));
        Assert.Equal(["pc", "pc", "laptop"], ready.Actors.Select(actor => actor.Host));

        // #711: with the pinned-loader rule (a fake pack here), every actor that names no package is asked with its host's
        // platform and its install's own loader is not read: the server's Doorstop mix no longer matters.
        File.WriteAllText(Path.Combine(hosts["pc"].Local(@"C:\game\server"), "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        byte[] pack = ShippedLoaderTests.Pack();
        var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
        var pinned = await HostedCampaignPreparation.InspectAsync(clean, TimeSpan.FromSeconds(30), name => hosts[name], (actor, install, platform) =>
        {
            asked.Add($"{actor} {install} {platform}");
            return ShippedLoader.For(actor, install, platform, new ShippedLoader.Pinned(ShippedLoaderTests.Pin(pack), "the test pin", () => new MemoryStream(pack)),
                Path.Combine(_rig.Root, "six-pinned-data"));
        });
        Assert.True(pinned.Ready, string.Join("; ", pinned.Problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}")));
        Assert.Equal(new[] { @"client-a pc:C:\game\client windows", @"client-b laptop:C:\game\client windows", @"server pc:C:\game\server windows" }, asked.Order(StringComparer.Ordinal));

        // #258 step 8b: the same actors without a dedicated server, one client to host the world and the other its peer. The
        // preflight is the same, without a server role; join (a server's address) is refused without a server.
        string Hosted(string file, object? join) => WriteJson(file, join == null
            ? new
            {
                inventory, world, worldUid = "4242",
                clients = new Dictionary<string, object>
                {
                    ["client-a"] = new { dependencyLock = clientLock, environmentCandidates = new[] { "pc-client" }, character = Character(storeA, "one", "vt-one") },
                    ["client-b"] = new { dependencyLock = clientLock, environmentCandidates = new[] { "laptop-client" }, character = Character(storeB, "two", "vt-two") },
                },
            }
            : (object)new { inventory, world, join, clients = new Dictionary<string, object> { ["client-a"] = new { dependencyLock = clientLock, character = Character(storeA, "one", "vt-one") } } });
        string serverless = Hosted("six-hosted.json", null);
        var hostedReady = await HostedCampaignPreparation.InspectAsync(serverless, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        Assert.True(hostedReady.Ready, string.Join("; ", hostedReady.Problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}")));
        Assert.Equal(["client-a", "client-b"], hostedReady.Actors.Select(actor => actor.Name));
        Assert.Equal(["pc", "laptop"], hostedReady.Actors.Select(actor => actor.Host));
        Assert.All(hostedReady.Actors, actor => Assert.Equal("client", actor.Kind));
        Assert.Contains(HostedCampaignPreparation.Inspect(Hosted("six-hosted-join.json", "pc.example:2456")).Problems,
            problem => problem.Message.Contains("join is the dedicated server's address", StringComparison.Ordinal));
        // The hosted plan's agreement: one client hosts, the other is its peer; a dedicated-server plan is refused on this campaign.
        ClientRunPlan HostSection() => new() { Mode = "owned", Install = @"C:\bound", Port = 5578, Character = "x", Pins = new() { ["example.mymod"] = "absent" },
            HostWorld = new() { World = new() { Source = world }, WorldUid = "4242" } };
        ClientRunPlan PeerSection() => new() { Mode = "owned", Install = @"C:\bound", Port = 5579, Character = "y", JoinsHost = true, Pins = new() { ["example.mymod"] = "absent" } };
        HostedCampaignPreparation.CheckHostedPlan(serverless, new Dictionary<string, ClientRunPlan> { ["client-a"] = HostSection(), ["client-b"] = PeerSection() });
        Assert.Contains("exactly one hosting client", Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.CheckHostedPlan(serverless,
            new Dictionary<string, ClientRunPlan> { ["client-a"] = HostSection(), ["client-b"] = HostSection() })).Message);
        Assert.Contains("set joinsHost", Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.CheckHostedPlan(serverless,
            new Dictionary<string, ClientRunPlan> { ["client-a"] = HostSection(), ["client-b"] = new() { Mode = "owned", Port = 5579 } })).Message);
        Assert.Contains("declares a dedicated server", Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.CheckHostedPlan(clean,
            new Dictionary<string, ClientRunPlan> { ["client-a"] = HostSection(), ["client-b"] = PeerSection() })).Message);
        // #812: any client's -savedir (either spelling, any case, bare or =value) is refused in the plan check, before any host is
        // contacted, on a hosted campaign and on one with a dedicated server; a plain argument is not.
        foreach (string argument in new[] { "-savedir", "--SAVEDIR", "-SaveDir=D:\\elsewhere", "--savedir=/elsewhere" })
        {
            var peer = PeerSection(); peer.LaunchArguments = ["-console", argument, "D:\\elsewhere"];
            var hostedError = Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.CheckHostedPlan(serverless,
                new Dictionary<string, ClientRunPlan> { ["client-a"] = HostSection(), ["client-b"] = peer })).Message;
            Assert.Contains($"Remove the client-b's launch argument {argument}", hostedError);
            var host = HostSection(); host.LaunchArguments = [argument];
            Assert.Contains($"Remove the client-a's launch argument {argument}", Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.CheckPlan(clean,
                new ServerRunPlan { Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"] },
                new Dictionary<string, ClientRunPlan> { ["client-a"] = host, ["client-b"] = PeerSection() })).Message);
        }
        var notSavedir = PeerSection(); notSavedir.LaunchArguments = ["-console", "-savedirectory", "-name", "-savedir-not"];
        HostedCampaignPreparation.CheckHostedPlan(serverless, new Dictionary<string, ClientRunPlan> { ["client-a"] = HostSection(), ["client-b"] = notSavedir });
    }

    // RunCampaignAsync: one report from the campaign's preflight through the run to retiring the prepared install, with the
    // prepared environment in memory (no profile.json, plan.json or campaign-times.json); a plan that disagrees with the
    // campaign is refused in Preflight before any host is contacted.
    // #711: a session's actor that names no loader package, on a Windows host, runs its disposable copy on the pinned
    // BepInExPack whatever its install holds, as server-load's do: the choice names the pin and its SHA-256 in provenance
    // (<actor>LoaderShipped) and the source install is unchanged. Negative control: with the rule off the copy keeps the
    // install's own BepInEx.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASessionActorThatNamesNoLoaderRunsOnThePinnedPack(bool rule)
    {
        string tag = rule ? "shipped" : "own";
        string serverDll = _rig.Write($"loader-{tag}/Server.dll", RegressionRig.Assembly("Server", new("example.server")));
        var resolved = NativeDependencyResolver.Resolve(Request(serverDll));
        Assert.True(resolved.Ready, string.Join("; ", resolved.Gaps.Select(gap => gap.Reason)));
        string serverLock = Path.Combine(_rig.Root, $"loader-{tag}-lock.json");
        resolved.Write(serverLock);
        string world = Path.Combine(_rig.Root, $"loader-{tag}-world");
        FakeInstalls.World(world);
        var server = new FakeOwnedServer("test.mod", saveRoot: @"C:\runs\run-test\world");
        var host = new FakeServerHost("pc", Path.Combine(_rig.Root, $"loader-{tag}-pc"), server, windows: true);
        string source = host.Local(@"C:\game\source");
        FakeInstalls.Server(source);
        File.WriteAllText(Path.Combine(source, GameLaunch.ServerWindowsExecutable), "server");
        File.WriteAllText(Path.Combine(source, "winhttp.dll"), "MZ target_assembly");
        File.WriteAllText(Path.Combine(source, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        byte[] sourceCore = File.ReadAllBytes(Path.Combine(source, "BepInEx", "core", "BepInEx.dll"));
        string inventory = Path.Combine(_rig.Root, $"loader-{tag}-inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new { pc = new { kind = "ssh", platform = "windows", shell = "pwsh", @lock = @"C:\locks\campaign.lock", destination = "test@pc" } },
            environments = new[] { new { name = "pc-server", host = "pc", roles = new[] { "server" }, install = @"C:\game\source", runtime = @"C:\runs", cliPort = 5577, localCliPort = 6577, gamePort = 2456 } },
        }));
        string manifest = Path.Combine(_rig.Root, $"loader-{tag}.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            inventory, world, join = "test-server.example:2456",
            server = new { dependencyLock = serverLock }, clients = new Dictionary<string, object>(),
        }));
        byte[] zip = ShippedLoaderTests.Pack();
        byte[]? copiedCore = null;
        host.AfterApply = runtime => copiedCore = File.ReadAllBytes(Path.Combine(runtime, "BepInEx", "core", "BepInEx.dll"));
        var options = new PinnedServerRunOptions<SitePlan>
        {
            Name = "loader-" + tag, ReadPlan = _ => throw new InvalidOperationException("A campaign plan is in memory."),
            Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
            Scenario = (_, _) => Task.CompletedTask,
            RunId = "run-detached-owner",
            Hooks = new FakeRunHooks
            {
                Host = _ => host, Connect = _ => server.Connect(), StateWaits = false, RunId = "run-test",
                PinnedLoader = rule ? (actor, install, platform) => ShippedLoader.For(actor, install, platform,
                    new ShippedLoader.Pinned(ShippedLoaderTests.Pin(zip), "the test pin", () => new MemoryStream(zip)), Path.Combine(_rig.Root, "loader-data")) : null,
            },
        };
        var plan = new SitePlan
        {
            Scenario = "smoke", Port = 5577,
            Arguments = ["-batchmode", "-nographics", "-savedir", "{world}", "-port", "2456", "-password", "secret", "-logFile", "{runtime}/toolkit-unity.log"],
            Pins = new() { ["example.server"] = "<md5 of the server's plugin>" },
        };
        string output = Path.Combine(_rig.Root, $"loader-{tag}-out");
        int exit = await PinnedServerRun.RunCampaignAsync(manifest, plan, _ => new Dictionary<string, ClientRunPlan>(), output, options);
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement;
        Assert.True(exit == 0, result.ToString());
        var provenance = result.GetProperty("Provenance");
        Assert.NotNull(copiedCore);
        Assert.Equal("run-detached-owner", provenance.GetProperty("runId").GetString());
        Assert.Equal(sourceCore, File.ReadAllBytes(Path.Combine(source, "BepInEx", "core", "BepInEx.dll"))); // the install is never changed
        if (rule)
        {
            string shipped = provenance.GetProperty("serverLoaderShipped").GetString()!;
            Assert.Contains(@"pc:C:\game\source's disposable profile runs on the pinned BepInExPack_Valheim 5.4.2351", shipped);
            Assert.Contains("sha256 " + ShippedLoaderTests.Pin(zip).Sha256 + ", from the test pin", shipped);
            Assert.Equal("core", System.Text.Encoding.UTF8.GetString(copiedCore!)); // the shipped pack's core
        }
        else
        {
            Assert.False(provenance.TryGetProperty("serverLoaderShipped", out _));
            Assert.Equal(sourceCore, copiedCore);
        }
    }

    [Fact] public async Task CampaignRunPreparesRunsAndRetiresInOneReportAndRefusesADisagreeingPlanFirst()
    {
        string serverDll = _rig.Write("run-campaign/Server.dll", RegressionRig.Assembly("Server", new("example.server")));
        var resolved = NativeDependencyResolver.Resolve(Request(serverDll));
        Assert.True(resolved.Ready, string.Join("; ", resolved.Gaps.Select(gap => gap.Reason)));
        string serverLock = Path.Combine(_rig.Root, "run-campaign-lock.json");
        resolved.Write(serverLock);
        string world = Path.Combine(_rig.Root, "run-campaign-world");
        FakeInstalls.World(world);
        var server = new FakeOwnedServer("test.mod", saveRoot: @"C:\runs\run-test\world");
        var host = new FakeServerHost("pc", Path.Combine(_rig.Root, "run-campaign-pc"), server, windows: true);
        string source = host.Local(@"C:\game\source");
        FakeInstalls.Server(source);
        File.WriteAllText(Path.Combine(source, GameLaunch.ServerWindowsExecutable), "server");
        File.WriteAllText(Path.Combine(source, "winhttp.dll"), "MZ target_assembly");
        File.WriteAllText(Path.Combine(source, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        string inventory = Path.Combine(_rig.Root, "run-campaign-inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new { pc = new { kind = "ssh", platform = "windows", shell = "pwsh", @lock = @"C:\locks\campaign.lock", destination = "test@pc" } },
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
            Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
            Scenario = (session, _) => { scenarioRan = true; Assert.NotNull(session.Server!.Host); return Task.CompletedTask; },
            Hooks = new FakeRunHooks { Host = _ => host, Connect = _ => server.Connect(), StateWaits = false, RunId = "run-test" },
        };
        IReadOnlyDictionary<string, ClientRunPlan> NoClients(SitePlan _) => new Dictionary<string, ClientRunPlan>();

        // A manifest without world or join is refused before any host.
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
        Assert.Empty(result.GetProperty("PinOrigins").EnumerateObject());
        var inputHashes = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "input-hashes.json"))).RootElement;
        Assert.Empty(inputHashes.GetProperty("pinOrigins").EnumerateObject());
        var steps = result.GetProperty("Steps").EnumerateArray().ToDictionary(step => step.GetProperty("Name").GetString()!, step => step.GetProperty("Phase").GetString());
        Assert.Equal("Setup", steps["check the hosts and prepare every actor's disposable install"]);
        // One retire owner (#257): each owned path is its own Cleanup step, retired under the lock the run already holds.
        Assert.Equal("Cleanup", Assert.Single(steps, step => step.Key.StartsWith("remove the prepared install ", StringComparison.Ordinal)).Value);
        Assert.DoesNotContain(host.Claims, claim => claim.Contains("retire", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(output, "prepared", "environment-assignments.json")));
        Assert.False(File.Exists(Path.Combine(output, "prepared", "profile.json")));
        Assert.False(File.Exists(Path.Combine(output, "campaign-times.json")));
        // The bound plan is evidence (NaN sites included), hashed as the run's plan; the campaign and inventory are hashed too.
        string bound = Path.Combine(output, "prepared", "plan.json");
        Assert.Equal(FileHash.Sha256(bound), result.GetProperty("Provenance").GetProperty("planSha256").GetString());
        Assert.Contains("NaN", File.ReadAllText(bound));
        Assert.Equal(FileHash.Sha256(manifest), result.GetProperty("Provenance").GetProperty("campaignSha256").GetString());
        Assert.Equal(FileHash.Sha256(inventory), result.GetProperty("Provenance").GetProperty("inventorySha256").GetString());
        Assert.Equal(host.Claims.Count, host.Releases.Count);
        Assert.Empty(Directory.GetDirectories(host.Local(@"C:\runs"), "vt-prep-*")); // The prepared install was retired.
        // One owned profile for the server (#257): the campaign's prepared install is the runtime, verified in place, never copied.
        Assert.Single(host.Scripts, script => script == "profile-seed");
        Assert.DoesNotContain("copy", host.Scripts);
        Assert.Equal("Setup", steps["reuse the verified prepared runtime listing"]);
        Assert.DoesNotContain("copy and verify pinned runtime on the server host", steps.Keys);
        Assert.Contains("vt-prep-", result.GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
        // The run journal (#257): one run id for the campaign; on the host, beside its lock, each copy was journalled before it
        // was made (the first journal entry precedes the profile seed), then done, retired, and the run's end.
        string runId = result.GetProperty("Provenance").GetProperty("runId").GetString()!;
        var journal = await RunJournalOnHost.ReadAsync(host, @"C:\locks\journal", runId, TimeSpan.FromSeconds(5));
        Assert.Equal([JournalEntry.LockIntended, JournalEntry.LockHeld, JournalEntry.LockReleased],
            journal.Where(record => record.Actor == "run" && record.Entry.Kind.StartsWith("lock-", StringComparison.Ordinal))
                .Select(record => record.Entry.Kind));
        var preparationClaim = journal.First(record => record.Entry.Kind == JournalEntry.LockIntended).Entry.Fields["claimant"];
        Assert.Equal(preparationClaim, journal.First(record => record.Entry.Kind == JournalEntry.LockHeld).Entry.Fields["claimant"]);
        Assert.Equal(preparationClaim, journal.First(record => record.Entry.Kind == JournalEntry.LockReleased).Entry.Fields["claimant"]);
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.SourceBaseline, JournalEntry.SourceSettled, JournalEntry.CopyRetired],
            journal.Where(record => record.Actor == "server").Select(record => record.Entry.Kind));
        // The server run's world copy on the host is journalled too, before its ship, and retired after its verified fetch (under
        // the hooks' fixed run id here; a real campaign's server run has the campaign's).
        var serverRun = await RunJournalOnHost.ReadAsync(host, @"C:\locks\journal", "run-test", TimeSpan.FromSeconds(5));
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.CopyDone, JournalEntry.CopyRetired],
            serverRun.Where(record => record.Entry.Kind.StartsWith("copy-", StringComparison.Ordinal) && record.Entry.Fields["runtime"].EndsWith(@"\world")).Select(record => record.Entry.Kind));
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.CopyRetired],
            serverRun.Where(record => record.Entry.Kind.StartsWith("copy-", StringComparison.Ordinal) && record.Entry.Fields["runtime"].EndsWith(@"\boot-1")).Select(record => record.Entry.Kind));
        Assert.Contains($"vt-prep-{runId}-server", journal.First(record => record.Entry.Kind == JournalEntry.CopyIntended).Entry.Fields["runtime"]);
        var ended = Assert.Single(journal, record => record.Entry.Kind == JournalEntry.RunEnded);
        Assert.Equal(("passed", "true"), (ended.Entry.Fields["state"], ended.Entry.Fields["cleanupVerified"]));
        Assert.True(host.Scripts.ToList().IndexOf("journal") < host.Scripts.ToList().IndexOf("profile-seed"));

        // A server that may still run (its start reply was lost) keeps the prepared install it may run from, named, and the
        // host's lock; the one retire owner removes nothing it cannot prove unused.
        host.Failures["start"] = FakeServerHost.TransportFailure;
        if (Directory.Exists(host.Local(@"C:\runs\run-test"))) Directory.Delete(host.Local(@"C:\runs\run-test"), true); // the fixed run id's world and boot evidence from the run above
        string unknownOutput = Path.Combine(_rig.Root, "run-campaign-unknown");
        Assert.Equal(3, await PinnedServerRun.RunCampaignAsync(manifest, Plan("example.server", "secret"), NoClients, unknownOutput, options));
        host.Failures.Remove("start");
        var unknown = JsonDocument.Parse(File.ReadAllText(Path.Combine(unknownOutput, "result.json"))).RootElement;
        Assert.Contains("may still run", unknown.GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
        string kept = Assert.Single(Directory.GetDirectories(host.Local(@"C:\runs"), "vt-prep-*"));
        Assert.True(Directory.Exists(Path.Combine(kept, "runtime")));
        Assert.DoesNotContain(unknown.GetProperty("Steps").EnumerateArray(), step => step.GetProperty("Name").GetString()!.StartsWith("remove the prepared install ", StringComparison.Ordinal));
        Assert.Equal(host.Claims.Count, host.Releases.Count + 1);
        string unknownRun = unknown.GetProperty("Provenance").GetProperty("runId").GetString()!;
        var unknownJournal = await RunJournalOnHost.ReadAsync(host, @"C:\locks\journal", unknownRun, TimeSpan.FromSeconds(5));
        // Its source is not settled: the journalled baseline stays for env recover to settle it from (#612).
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.SourceBaseline, JournalEntry.CopyKept],
            unknownJournal.Where(record => record.Actor == "server").Select(record => record.Entry.Kind));
        Assert.True(File.Exists(Path.Combine(kept, ProfileSourceCheck.BaselineFile)));
        // The world copy too: its server may still run.
        Assert.Equal(JournalEntry.CopyKept, (await RunJournalOnHost.ReadAsync(host, @"C:\locks\journal", "run-test", TimeSpan.FromSeconds(5)))
            .Last(record => record.Entry.Kind.StartsWith("copy-", StringComparison.Ordinal)).Entry.Kind);
        Assert.Equal("unknown", Assert.Single(unknownJournal, record => record.Entry.Kind == JournalEntry.RunEnded).Entry.Fields["state"]);
        // This negative control deliberately kept its exact claim. The independent cases below start on a free fake host.
        string keptClaim = Assert.Single(host.Claims.Except(host.Releases));
        Assert.Equal(HostLockState.Released, (await host.ReleaseLockAsync(@"C:\locks\campaign.lock", keptClaim, TimeSpan.FromSeconds(5))).State);

        // A host retire that fails keeps the install and what it kept beside it (not fetched yet): nothing else removes it.
        if (Directory.Exists(host.Local(@"C:\runs\run-test"))) Directory.Delete(host.Local(@"C:\runs\run-test"), true);
        host.Failures["retire"] = new HostResult(HostOutcome.Exited, 3, "", "Remove-Item: access denied", TimeSpan.Zero, false);
        string failedOutput = Path.Combine(_rig.Root, "run-campaign-retire-failed");
        Assert.Equal(1, await PinnedServerRun.RunCampaignAsync(manifest, Plan("example.server", "secret"), NoClients, failedOutput, options));
        host.Failures.Remove("retire");
        var failedRetire = JsonDocument.Parse(File.ReadAllText(Path.Combine(failedOutput, "result.json"))).RootElement;
        Assert.Equal((true, false), (failedRetire.GetProperty("RuntimeReady").GetBoolean(), failedRetire.GetProperty("CleanupVerified").GetBoolean()));
        Assert.StartsWith("cleanup failed", failedRetire.GetProperty("Provenance").GetProperty("runtimeCopy").GetString());
        Assert.Equal(2, Directory.GetDirectories(host.Local(@"C:\runs"), "vt-prep-*").Count(directory => Directory.Exists(Path.Combine(directory, "runtime"))));
        Assert.DoesNotContain(failedRetire.GetProperty("Steps").EnumerateArray(), step => step.GetProperty("Name").GetString()!.StartsWith("remove the prepared install ", StringComparison.Ordinal));

        // A lock intent that cannot be journalled stops the run before the claim and before any profile effect.
        int copiesBefore = host.Scripts.Count(script => script == "profile-seed");
        host.Failures["journal"] = new HostResult(HostOutcome.Exited, 4, "", "No space left on device", TimeSpan.Zero, false);
        string unjournalledOutput = Path.Combine(_rig.Root, "run-campaign-unjournalled");
        Assert.Equal(1, await PinnedServerRun.RunCampaignAsync(manifest, Plan("example.server", "secret"), NoClients, unjournalledOutput, options));
        host.Failures.Remove("journal");
        Assert.Equal(copiesBefore, host.Scripts.Count(script => script == "profile-seed"));
        var unjournalled = JsonDocument.Parse(File.ReadAllText(Path.Combine(unjournalledOutput, "result.json"))).RootElement;
        var prepare = unjournalled.GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == "check the hosts and prepare every actor's disposable install");
        Assert.False(prepare.GetProperty("Passed").GetBoolean());
        Assert.Contains("Journalling lock-intended", prepare.GetProperty("Error").GetString());

        // A preparation that fails after its copy was journalled ends its run in the journal, cleanup proven.
        host.Failures["apply-stage"] = new HostResult(HostOutcome.Exited, 3, "", "Access to the path is denied", TimeSpan.Zero, false);
        string failedPrepOutput = Path.Combine(_rig.Root, "run-campaign-failed-prep");
        Assert.Equal(1, await PinnedServerRun.RunCampaignAsync(manifest, Plan("example.server", "secret"), NoClients, failedPrepOutput, options));
        host.Failures.Remove("apply-stage");
        string failedRun = JsonDocument.Parse(File.ReadAllText(Path.Combine(failedPrepOutput, "result.json"))).RootElement.GetProperty("Provenance").GetProperty("runId").GetString()!;
        var failedJournal = await RunJournalOnHost.ReadAsync(host, @"C:\locks\journal", failedRun, TimeSpan.FromSeconds(5));
        // The partial copy is the campaign's from its intent, so the preparation's cleanup retires it and journals that.
        Assert.Equal([JournalEntry.CopyIntended, JournalEntry.CopyRetired], failedJournal.Where(record => record.Actor == "server").Select(record => record.Entry.Kind));
        var failedEnd = Assert.Single(failedJournal, record => record.Entry.Kind == JournalEntry.RunEnded).Entry.Fields;
        Assert.Equal(("failed", "true"), (failedEnd["state"], failedEnd["cleanupVerified"]));
        // The result and its JUnit say what the journal says (#424): the preparation's own cleanup is a passing Cleanup step.
        Assert.Equal((true, 1, 0), CleanupOf(failedPrepOutput));

        // Negative control: the partial copy's cleanup fails too. The journal, the result and the JUnit all say not verified.
        host.Failures["apply-stage"] = new HostResult(HostOutcome.Exited, 3, "", "Access to the path is denied", TimeSpan.Zero, false);
        host.Failures["cleanup-stage"] = new HostResult(HostOutcome.Exited, 3, "", "Remove-Item: access denied", TimeSpan.Zero, false);
        string uncleanedOutput = Path.Combine(_rig.Root, "run-campaign-failed-prep-uncleaned");
        Assert.Equal(1, await PinnedServerRun.RunCampaignAsync(manifest, Plan("example.server", "secret"), NoClients, uncleanedOutput, options));
        host.Failures.Remove("apply-stage");
        host.Failures.Remove("cleanup-stage");
        string uncleanedRun = JsonDocument.Parse(File.ReadAllText(Path.Combine(uncleanedOutput, "result.json"))).RootElement.GetProperty("Provenance").GetProperty("runId").GetString()!;
        var uncleanedEnd = Assert.Single(await RunJournalOnHost.ReadAsync(host, @"C:\locks\journal", uncleanedRun, TimeSpan.FromSeconds(5)),
            record => record.Entry.Kind == JournalEntry.RunEnded).Entry.Fields;
        Assert.Equal(("failed", "false"), (uncleanedEnd["state"], uncleanedEnd["cleanupVerified"]));
        var (verified, cleanupSteps, cleanupFailures) = CleanupOf(uncleanedOutput);
        Assert.Equal((false, true), (verified, cleanupSteps >= 1 && cleanupFailures >= 1));
    }

    // result.json's CleanupVerified, and the JUnit cleanup suite's test and failure counts.
    private static (bool Verified, int Tests, int Failures) CleanupOf(string output)
    {
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement;
        var suite = System.Xml.Linq.XDocument.Load(Path.Combine(output, "junit.xml")).Root!.Elements("testsuite")
            .Single(element => element.Attribute("name")!.Value.EndsWith(" / cleanup", StringComparison.Ordinal));
        return (result.GetProperty("CleanupVerified").GetBoolean(), (int)suite.Attribute("tests")!, (int)suite.Attribute("failures")!);
    }

    // #257: a Ctrl+C while one actor's profile seed runs (the server's, which then hangs until cancelled) cancels the campaign's
    // preparation, but the sibling actor's seed, already under way and not interruptible, settles first: only then are the
    // preparation's profiles cleaned up and its end journalled. Every journalled vt-prep profile and staging folder is gone, each
    // host journals the run's end with its cleanup verified, and the run fails.
    [Fact] public async Task ACtrlCWhileOneActorsSeedRunsLetsTheSiblingSettleThenRemovesEveryPreparedProfile()
    {
        string serverDll = _rig.Write("cut-campaign/Server.dll", RegressionRig.Assembly("Server", new("example.server")));
        string clientDll = _rig.Write("cut-campaign/Client.dll", RegressionRig.Assembly("Client", new("example.client")));
        string serverLock = Lock(serverDll, "cut-server"), clientLock = Lock(clientDll, "cut-client");
        string store = Store("cut-one", 101);
        string world = Path.Combine(_rig.Root, "cut-campaign-world");
        FakeInstalls.World(world);
        var server = new FakeOwnedServer("test.mod");
        var hosts = new Dictionary<string, FakeServerHost>(StringComparer.Ordinal);
        foreach (string name in new[] { "server", "client-a" })
        {
            var host = new FakeServerHost(name, Path.Combine(_rig.Root, "cut-" + name), name == "server" ? server : null, windows: true);
            hosts[name] = host;
            host.SteamUserReply = "VT-STEAMUSER account 101\n";
            string source = host.Local(@"C:\game\source");
            if (name == "server") FakeInstalls.Server(source); else FakeInstalls.Client(source);
            File.WriteAllText(Path.Combine(source, name == "server" ? GameLaunch.ServerWindowsExecutable : GameLaunch.ClientWindowsExecutable), "game");
            File.WriteAllText(Path.Combine(source, "winhttp.dll"), "MZ target_assembly");
            File.WriteAllText(Path.Combine(source, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
            if (name != "server")
            {
                Directory.CreateDirectory(host.Local(@"C:\save\characters_local"));
                Directory.CreateDirectory(host.Local(@"C:\Steam\userdata"));
            }
        }
        object Environment(string name, string role, int port) => new
        {
            name, host = name, roles = new[] { role }, install = @"C:\game\source", runtime = role == "server" ? @"C:\runs" : @"C:\client-runs", cliPort = port, localCliPort = port + 1000, gamePort = role == "server" ? 2456 : 0,
        };
        string inventory = Path.Combine(_rig.Root, "cut-campaign-inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = hosts.Keys.ToDictionary(name => name, name => new { kind = "ssh", platform = "windows", shell = "pwsh", @lock = @"C:\locks\campaign.lock", destination = "test@" + name }),
            environments = new[] { Environment("server", "server", 5577), Environment("client-a", "client", 5578) }, leaseHost = "server", leaseDirectory = @"C:\leases",
        }));
        string manifest = Path.Combine(_rig.Root, "cut-campaign.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            inventory, world, join = "test-server.example:2456", server = new { dependencyLock = serverLock },
            clients = new Dictionary<string, object> { ["client-a"] = new { dependencyLock = clientLock, character = Character(store, "cut-one", "vt-cut") } },
        }));
        var plan = new SitePlan
        {
            Scenario = "smoke", Port = 5577,
            Arguments = ["-batchmode", "-nographics", "-savedir", "{world}", "-port", "2456", "-password", "secret", "-logFile", "{runtime}/toolkit-unity.log"],
            Pins = new() { ["example.server"] = "<md5 of the server's plugin>" },
        };
        using var interrupt = new RunCancellation();
        // What ran where, in one order across both hosts.
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var serverCopying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journalledAfterServerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int serverJournalsAfterRelease = 0;
        bool serverWasCopying = false, serverDoneWhileSiblingCopied = false, journalledBeforeSiblingSettled = false;
        hosts["server"].Hang.Add("profile-seed");
        // The fake host answers at once; a real ship takes time. Yielding here lets client-a's preparation run beside the server's
        // instead of inside the call that starts it, so nothing but the preparation itself makes the cleanup wait for it.
        hosts["client-a"].BeforeShip = async () => await Task.Yield();
        // The server's preparation is done with its host once it releases its preparation claim (after its own cleanup).
        hosts["server"].AfterRelease = owner => { if (owner.StartsWith("campaign-prepare ", StringComparison.Ordinal)) serverReleased.TrySetResult(); };
        void Observe(string host, string name)
        {
            events.Enqueue(host + ":" + name);
            if (name == "profile-seed" && host == "server") serverCopying.TrySetResult();
            // The exact lifecycle journals lock-released immediately after the release. A second journal call would be the
            // premature run end this test guards; the correct run end waits for the sibling preparation to settle.
            if (host == "server" && name == "journal" && serverReleased.Task.IsCompleted &&
                Interlocked.Increment(ref serverJournalsAfterRelease) > 1) journalledAfterServerReleased.TrySetResult();
        }
        hosts["server"].BeforeScript = name => Observe("server", name);
        hosts["client-a"].BeforeScript = name =>
        {
            Observe("client-a", name);
            if (name != "profile-seed") return;
            // The Ctrl+C comes while the server's seed runs; this seed goes on to its end, as a host script already under way does.
            // Gates, not sleeps, order the steps (thread scheduling differs per runner): the cancelled server preparation removes
            // its partial copy and releases its host while this copy still runs.
            serverWasCopying = serverCopying.Task.Wait(TimeSpan.FromSeconds(20));
            interrupt.SignalCancel();
            serverDoneWhileSiblingCopied = serverReleased.Task.Wait(TimeSpan.FromSeconds(20));
            // A preparation that stopped waiting for its sibling would now journal the run's end. Nothing marks "it did not", so
            // this one wait is bounded: a correct preparation journals nothing until this copy settles and the wait runs out.
            journalledBeforeSiblingSettled = journalledAfterServerReleased.Task.Wait(TimeSpan.FromSeconds(1));
            events.Enqueue("client-a copy settled");
        };
        var options = new PinnedServerRunOptions<SitePlan>
        {
            Name = "cut-campaign", ReadPlan = _ => throw new InvalidOperationException("A campaign plan is in memory."),
            Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
            Scenario = (_, _) => throw new InvalidOperationException("The scenario must not run after a cancelled preparation."),
            Hooks = new FakeRunHooks { Host = name => hosts[name], Connect = _ => server.Connect(), StateWaits = false, Cancellation = interrupt },
        };
        string output = Path.Combine(_rig.Root, "cut-campaign-out");
        // Off the test framework's synchronization context, so no continuation waits for the sibling's thread by accident.
        Assert.Equal(1, await Task.Run(() => PinnedServerRun.RunCampaignAsync(manifest, plan, _ => new Dictionary<string, ClientRunPlan> { ["client-a"] = new() }, output, options))
            .WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(interrupt.Token.IsCancellationRequested);
        Assert.Null(interrupt.Abandoned);
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement;
        var prepare = result.GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == "check the hosts and prepare every actor's disposable install");
        Assert.False(prepare.GetProperty("Passed").GetBoolean());
        string runId = result.GetProperty("Provenance").GetProperty("runId").GetString()!;
        var order = events.ToList();
        int settled = order.IndexOf("client-a copy settled");
        Assert.True(settled >= 0, string.Join(", ", order));
        Assert.True(serverWasCopying, "The Ctrl+C must come while the server's copy runs.");
        Assert.True(serverDoneWhileSiblingCopied, "The cancelled server preparation must finish with its host while the sibling's copy runs: " + string.Join(", ", order));
        Assert.True(order.IndexOf("server:cleanup-stage") is >= 0 and var cleaned && cleaned < settled, string.Join(", ", order));
        Assert.False(journalledBeforeSiblingSettled, "The run's end was journalled while the sibling's copy still ran: " + string.Join(", ", order));
        // The sibling's copy ran to its end after the Ctrl+C; its own partial copy went after that.
        Assert.True(order.LastIndexOf("client-a:cleanup-stage") > settled, string.Join(", ", order));
        foreach (var (name, host) in hosts)
        {
            // The run's end is each host's last journal line, written only once the sibling had settled.
            Assert.True(order.LastIndexOf(name + ":journal") > settled, $"{name}: " + string.Join(", ", order));
            var journal = await RunJournalOnHost.ReadAsync(host, @"C:\locks\journal", runId, TimeSpan.FromSeconds(5));
            var intended = Assert.Single(journal, record => record.Entry.Kind == JournalEntry.CopyIntended);
            Assert.Equal(name, intended.Actor);
            Assert.Contains($"vt-prep-{runId}-{name}", intended.Entry.Fields["runtime"]);
            var ended = Assert.Single(journal, record => record.Entry.Kind == JournalEntry.RunEnded).Entry.Fields;
            Assert.Equal(("failed", "true"), (ended["state"], ended["cleanupVerified"]));
            Assert.Equal(JournalEntry.RunEnded, journal.OrderBy(record => record.Utc).Last().Entry.Kind);
            // Every prepared copy and its staging folder are gone; the host's lock was released.
            string runs = host.Local(@"C:\runs");
            Assert.True(!Directory.Exists(runs) || Directory.GetDirectories(runs, "vt-prep-*").Length == 0, name);
            Assert.Equal(host.Claims.Count, host.Releases.Count);
            Assert.True(File.Exists(Path.Combine(host.Local(@"C:\game\source"), "BepInEx", "core", "BepInEx.dll")));
        }
        Assert.Empty(server.Events);
        // The result and its JUnit agree with every host's journal (#424).
        Assert.True(result.GetProperty("CleanupVerified").GetBoolean());
        var (_, cleanupSteps, cleanupFailures) = CleanupOf(output);
        Assert.Equal((true, 0), (cleanupSteps >= 1, cleanupFailures));
    }

    // A campaign that leaves out its inventory runs on this machine: its dedicated server is the one Steam installed, with
    // the defaults preflight reported, and the run records them as its inventory. (A Mac gets no local dedicated server.)
    [Fact] public async Task ACampaignWithoutAnInventoryRunsOnThisMachinesSteamServer()
    {
        if (OperatingSystem.IsMacOS()) return;
        bool windows = OperatingSystem.IsWindows();
        string serverDll = _rig.Write("this-machine/Server.dll", RegressionRig.Assembly("Server", new("example.server")));
        var resolved = NativeDependencyResolver.Resolve(Request(serverDll));
        string serverLock = Path.Combine(_rig.Root, "this-machine-lock.json");
        resolved.Write(serverLock);
        string world = Path.Combine(_rig.Root, "this-machine-world");
        FakeInstalls.World(world);
        // This machine, as the detection reads it: Steam with Valheim Dedicated Server installed.
        var machine = new FakeMachine(windows ? "windows" : "linux") { SteamPath = windows ? @"C:\Steam" : null };
        string steam = windows ? @"C:\Steam" : "/home/tester/.steam/steam";
        machine.Directories.Add(steam);
        string install = machine.App(steam, "896660", "Valheim dedicated server", windows ? GameLaunch.ServerWindowsExecutable : GameLaunch.ServerLinuxExecutable);
        string runs = HostPath.Join(machine.DataRoot, "runs", "local-server");
        var server = new FakeOwnedServer("test.mod", saveRoot: HostPath.Join(runs, "run-test", "world"));
        var host = new FakeServerHost("local", Path.Combine(_rig.Root, "this-machine-host"), server, kind: GameHostKind.Local, windows: windows);
        string source = host.Local(install);
        FakeInstalls.Server(source);
        File.WriteAllText(Path.Combine(source, windows ? GameLaunch.ServerWindowsExecutable : GameLaunch.ServerLinuxExecutable), "server");
        if (windows)
        {
            File.WriteAllText(Path.Combine(source, "winhttp.dll"), "MZ target_assembly");
            File.WriteAllText(Path.Combine(source, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        }
        else FakeInstalls.LinuxLoader(source);
        string manifest = Path.Combine(_rig.Root, "this-machine-campaign.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            world, join = "127.0.0.1:2486", server = new { dependencyLock = serverLock }, clients = new Dictionary<string, object>(),
        }));
        var plan = new SitePlan
        {
            Scenario = "smoke", Port = 5688,
            Arguments = ["-batchmode", "-nographics", "-savedir", "{world}", "-port", "2486", "-password", "secret", "-logFile", "{runtime}/toolkit-unity.log"],
            Pins = new() { ["example.server"] = "<md5 of the server's plugin>" },
        };
        var options = new PinnedServerRunOptions<SitePlan>
        {
            Name = "this-machine-smoke", ReadPlan = _ => throw new InvalidOperationException("A campaign plan is in memory."),
            Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
            Scenario = (_, _) => Task.CompletedTask,
            Hooks = new FakeRunHooks { Host = _ => host, Connect = _ => server.Connect(), StateWaits = false, RunId = "run-test" },
        };
        string output = Path.Combine(_rig.Root, "this-machine-out");
        int exit;
        using (EnvironmentInventory.UseMachine(machine))
            exit = await PinnedServerRun.RunCampaignAsync(manifest, plan, _ => new Dictionary<string, ClientRunPlan>(), output, options);
        Assert.True(exit == 0, File.Exists(Path.Combine(output, "result.json"))
            ? File.ReadAllText(Path.Combine(output, "result.json")) : "No result.json was written.");
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement;
        Assert.Equal("this machine", result.GetProperty("Provenance").GetProperty("inventory").GetString());
        string detected = result.GetProperty("Provenance").GetProperty("inventoryDetected").GetString()!;
        Assert.Contains($"Valheim Dedicated Server (Steam app 896660): {install}", detected);
        Assert.Contains("game port 2486", detected);
        Assert.False(result.GetProperty("Provenance").TryGetProperty("inventorySha256", out _));
        Assert.True(result.GetProperty("CleanupVerified").GetBoolean());
    }

    private sealed class SitePlan : ServerRunPlan { public float Ground { get; set; } = float.NaN; }

    // A barrier, not a delay: each ship waits until a second one is inside at the same time. Overlapping setups always meet
    // (the first waits for the second); serialised setups never do, so each ship gives up after the bound and Seen stays false.
    private sealed class Overlap
    {
        private readonly TaskCompletionSource _met = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inside;
        public bool Seen => _met.Task.IsCompleted;
        public async Task EnterAsync()
        {
            if (Interlocked.Increment(ref _inside) >= 2) _met.TrySetResult();
            try { await Task.WhenAny(_met.Task, Task.Delay(TimeSpan.FromSeconds(5))); }
            finally { Interlocked.Decrement(ref _inside); }
        }
    }

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
        Assert.Contains("exactly one pinned valheimCLI.dll", Assert.Throws<InvalidDataException>(() => NativeDependencyLock.ReadReady(path)).Message);
        Assert.Throws<InvalidDataException>(() => plan.ApplyTo(_rig.Manifest(), Path.Combine(_rig.Root, "should-not-be-written.json")));
        Assert.False(File.Exists(Path.Combine(_rig.Root, "should-not-be-written.json")));
    }

    [Fact] public void AnEditedLockWithoutTheCliCoreIsNotReady()
    {
        var plan = NativeDependencyResolver.Resolve(Request(_rig.Parent));
        plan.CliManifest.Plugin.Plugins = ["a.different.plugin"];
        string path = Path.Combine(_rig.Root, "no-core-lock.json");
        plan.Write(path);
        Assert.Contains("not a one-plugin build", Assert.Throws<InvalidDataException>(() => NativeDependencyLock.ReadReady(path)).Message);
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

        request.SearchRoots.Add(library); // Exact file beats both directory scans.
        var chosen = NativeDependencyResolver.Resolve(request);
        Assert.True(chosen.Ready, string.Join("; ", chosen.Gaps.Select(item => item.Reason)));
        Assert.Equal(library, Assert.Single(chosen.Plugins).File);
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

        var request = Request(_rig.Parent);
        request.SearchRoots.Add(searchRoot); // Preserve the explicitly selected build despite the competing mod-folder copy.
        var selected = NativeDependencyResolver.Resolve(request);
        Assert.True(selected.Ready, string.Join("; ", selected.Gaps.Select(item => item.Reason)));
        Assert.Equal(searchRoot, Assert.Single(selected.Plugins).File);
    }

    [Fact] public void AnExplicitOlderPluginIsRefusedInsteadOfSubstitutingANewerScannedBuild()
    {
        string mod = _rig.Write("versioned/Needs.dll", RegressionRig.Assembly("Needs",
            new("example.needs") { Minimum = [("example.dependency", "2.0.0")] }));
        string old = _rig.Write("old/Dependency.dll", RegressionRig.Assembly("Dependency", new("example.dependency", "1.0.0")));
        string current = _rig.Write("current/Dependency.dll", RegressionRig.Assembly("Dependency", new("example.dependency", "3.0.0")));
        var request = Request(mod);
        request.SearchRoots = [Path.GetDirectoryName(current)!, old];

        var refused = NativeDependencyResolver.Resolve(request);
        Assert.False(refused.Ready);
        var gap = Assert.Single(refused.Gaps);
        Assert.Equal("plugin", gap.Kind);
        Assert.Contains("explicitly selected", gap.Reason);
        Assert.Contains(old, gap.Candidates);
        Assert.Empty(refused.Plugins);

        request.SearchRoots = [Path.GetDirectoryName(current)!, current];
        var selected = NativeDependencyResolver.Resolve(request);
        Assert.True(selected.Ready, string.Join("; ", selected.Gaps.Select(item => item.Reason)));
        Assert.Equal(current, Assert.Single(selected.Plugins).File);
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

    [Fact] public void PinnedDefaultPluginChangedInLocalBuildIsAnUnresolvedGap()
    {
        var request = Request(_rig.Parent);
        string coreOnly = Path.Combine(_rig.Root, "cli-core-only");
        Directory.CreateDirectory(coreOnly);
        File.Copy(_rig.Manifest().Cli.Core.File, Path.Combine(coreOnly, "valheimCLI.dll"));
        File.AppendAllText(Path.Combine(coreOnly, "valheimCLI.dll"), "changed");
        request.CliFiles = coreOnly;
        var plan = NativeDependencyResolver.Resolve(request);
        var gap = Assert.Single(plan.Gaps.Where(entry => entry.Kind == "cli"));
        Assert.Equal("cli", gap.Kind);
        Assert.Equal("valheimCLI.dll", gap.Name);
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
        Assert.Contains("example.mod is declared by both", plan.Gaps[0].Reason); // the one rule's text, as TargetedRegression refuses with
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
        Assert.Contains("declares [BepInIncompatibility(\"example.mod\")]", gap.Reason); // the one rule's text, as TargetedRegression refuses with
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

    // #258 step 8b, run B's shape with fakes: a campaign without a dedicated server, the hosting client on one macOS host and its
    // peer on another. The fixture is placed in the host user's own worlds_local (beside the characters_local its character was
    // staged in), journalled first; the peer joins the host by its Steam ID; teardown closes the peer, the host leaves and stops,
    // and the world is moved out of the user's worlds into the run's folder on that host and fetched into the evidence. Nothing
    // named for the world is left in the user's worlds, and their own world is untouched.
    [Fact] public Task AHostedCampaignPlacesTheWorldOnTheHostsMachineAndMovesItOutAfter() => HostedCampaign(localHost: false);

    // #813: the same campaign with the hosting client on this machine (pc is the inventory's local host, a macOS one, launched
    // through the local client launch seam). #809 gave it the same journalled site as a remote client's: its placement is journalled
    // in the campaign's journal (copy-intended, copy-done, copy-retired for the world), moved out into the client role's runtime,
    // and the user's own worlds_local is untouched. Putting back the local exception (no site for a local host) fails it.
    [OsFact("macos")] public Task AHostedCampaignWithALocalHostingClientPlacesItsWorldThroughTheJournalledSite() => HostedCampaign(localHost: true);

    private async Task HostedCampaign(bool localHost)
    {
        string clientMod = _rig.Write("hosted-campaign/Client.dll", RegressionRig.Assembly("Client", new("example.client")));
        string clientLock = Lock(clientMod, "hosted-client");
        string storeA = Store("hosta", 501, "hosted/"), storeB = Store("peerb", 502, "hosted/");
        // A chunked fixture, as Valheim 1.0 writes worlds: one Campaign/ directory.
        string world = FakeInstalls.World(Path.Combine(_rig.Root, "hosted-world"));
        // A host's path: on this machine (a local pc), a real one in the test's folder, read in place as a local host's files are.
        string pcHere = Path.Combine(_rig.Root, "pc-here");
        string On(string name, string path) => localHost && name == "pc" ? pcHere + path : path;
        var hosts = new Dictionary<string, FakeServerHost>(StringComparer.Ordinal);
        foreach (var (name, port, account) in new[] { ("pc", 15578, 501), ("laptop", 15579, 502) })
        {
            var host = localHost && name == "pc"
                ? new FakeServerHost(name, "/", tunnelPort: port, kind: GameHostKind.Local) { SteamUserReply = $"VT-STEAMUSER account {account}\n" }
                : new FakeServerHost(name, Path.Combine(_rig.Root, "hosted-mirror-" + name), tunnelPort: port) { SteamUserReply = $"VT-STEAMUSER account {account}\n" };
            hosts[name] = host;
            string source = host.Local(On(name, "/game/client"));
            FakeInstalls.MacClient(source);
            if (localHost && name == "pc")
                foreach (string binary in new[] { Path.Combine(source, GameLaunch.ClientMacBundle, "Contents", "MacOS", "Valheim"), Path.Combine(source, "libdoorstop.dylib") })
                {
                    File.WriteAllBytes(binary, ClientLaunchTests.Fat(ClientLaunchTests.X86_64, ClientLaunchTests.Arm64)); // read here, as a local launch reads it
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            Directory.CreateDirectory(Path.Combine(source, "BepInEx", "plugins"));
            Directory.CreateDirectory(host.Local(On(name, "/Users/t/Library/Application Support/IronGate/Valheim/characters_local")));
            Directory.CreateDirectory(host.Local(On(name, "/Users/t/userdata")));
        }
        string worlds = On("pc", "/Users/t/Library/Application Support/IronGate/Valheim/worlds_local");
        // A placement on this machine without the campaign's site would use this user's own data directory: simulated here as the
        // same folder, so even that never reaches the real one.
        using var data = localHost ? new FakeClientDataDirectory(On("pc", "/Users/t/Library/Application Support/IronGate/Valheim")) : null;
        // This machine's own checks a local client's preflight makes (its desktop, a running Steam), through their seam: never the
        // real ones, so the test does not depend on what runs here.
        int steamChecks = 0;
        using var probes = localHost ? LocalHostPreflight.ReplaceDefaultProbesForTest(new(MacDesktop: () => { }, Packaged: () => null,
            SteamRunning: () => { steamChecks++; return true; })) : null;
        // The host user's own world beside where the fixture goes: never touched.
        Directory.CreateDirectory(hosts["pc"].Local(worlds));
        File.WriteAllText(hosts["pc"].Local(worlds + "/MyWorld.fwl"), "the user's world");
        string inventory = Path.Combine(_rig.Root, "hosted-inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = hosts.Keys.ToDictionary(name => name, name => localHost && name == "pc"
                ? (object)new { kind = "local", platform = "macos", shell = "bash", @lock = On(name, "/locks/run.lock") }
                : new { kind = "ssh", platform = "macos", shell = "bash", @lock = "/locks/run.lock", destination = "test@" + name }),
            environments = new[]
            {
                new { name = "pc-client", host = "pc", roles = new[] { "client" }, install = On("pc", "/game/client"), runtime = On("pc", "/runs"), cliPort = 5578, localCliPort = localHost ? 0 : 6578, architecture = "x64" },
                new { name = "laptop-client", host = "laptop", roles = new[] { "client" }, install = "/game/client", runtime = "/runs", cliPort = 5579, localCliPort = 6579, architecture = "x64" },
            },
            leaseHost = "pc", leaseDirectory = On("pc", "/leases"),
        }));
        object Character(string host, string store, string registered, string file) => new
        {
            store, registeredName = registered, fileName = file,
            charactersLocalDirectory = On(host, "/Users/t/Library/Application Support/IronGate/Valheim/characters_local"), steamUserDataDirectory = On(host, "/Users/t/userdata"),
        };
        string manifest = Path.Combine(_rig.Root, "hosted-campaign.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            inventory, world, worldUid = "4242",
            clients = new Dictionary<string, object>
            {
                ["host"] = new { dependencyLock = clientLock, environmentCandidates = new[] { "pc-client" }, character = Character("pc", storeA, "hosta", "vt-host") },
                ["peer"] = new { dependencyLock = clientLock, environmentCandidates = new[] { "laptop-client" }, differentHostFrom = new[] { "host" }, character = Character("laptop", storeB, "peerb", "vt-peer") },
            },
        }));
        var preflight = await HostedCampaignPreparation.InspectAsync(manifest, TimeSpan.FromSeconds(30), name => hosts[name], pinnedLoader: null);
        Assert.True(preflight.Ready, string.Join("; ", preflight.Problems.Select(problem => $"{problem.Actor} {problem.Input}: {problem.Message}")));

        // The host: at its menu until it hosts; an open Steam server while it does. The peer joins it by its Steam ID.
        bool hosting = false, joined = false; int readings = 0;
        bool placedWhileHosting = false;
        var hostTransport = new ScriptedTransport()
            .ClientAccess(() => hosting, () => hosting)
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'vt-host' (vt-host, Local)"))
            .OnPrefix("cli_start_host_world ", command =>
            {
                hosting = true; readings = 0;
                placedWhileHosting = File.Exists(hosts["pc"].Local(worlds + "/Campaign/_main.1.fwl2"));
                return ScriptedTransport.Ok($"OK: Starting hosted world '{command.Split(' ')[1]}' using character 'vt-host' (vt-host, Local); open=true, public=False, crossplay=False, backend=Steamworks, passwordSet=False");
            })
            .On("cli_multiplayer_identity", _ => ScriptedTransport.Ok($"OK: steamId=76561197960266229, playFabLoginState=LoggedIn, playFabId=AB, backend=Steamworks, gameState=InGame, connectionStatus=Connected, isServer={(hosting ? "True" : "False")}, isOpenServer={(hosting ? "True" : "False")}, server="))
            .Extension("valheim.session", "state", _ =>
            {
                bool present = hosting && ++readings > 1;
                return new
                {
                    source = "session-state", complete = true, phase = present ? "world-present" : hosting ? "loading" : "menu", worldUid = present ? "4242" : null,
                    worldPresent = present, worldReady = present, server = present, dedicated = false, localPlayer = present, playerReady = present,
                    saving = false, loadError = false, connectionStatus = present ? "Connected" : "None",
                };
            })
            .Extension("valheim.session", "save", _ => new { source = "session-save", complete = true, worldUid = "4242", saved = true, before = 1, after = 2, milliseconds = 10 }, readOnly: false)
            .Extension("valheim.session", "leave", _ =>
            {
                hosting = false; joined = false;
                string backup = hosts["pc"].Local(worlds + "/Campaign_backup_auto-20261006-120000");
                Directory.CreateDirectory(backup);
                File.WriteAllText(Path.Combine(backup, "_main.1.db2"), "backup"); // the game's backup beside it
                return new { source = "session-leave", complete = true, action = "leave" };
            }, readOnly: false)
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True ghostReplicated=True"));
        // Each client's leased identity, which its launch confirms: SteamID64 of account 501 (the host) and 502 (the peer).
        var peerTransport = new ScriptedTransport()
            .ClientAccess(() => joined)
            .On("cli_multiplayer_identity", _ => ScriptedTransport.Ok("OK: steamId=76561197960266230, playFabLoginState=LoggedIn, playFabId=CD, backend=Steamworks, gameState=Menu, connectionStatus=None, isServer=False, isOpenServer=False, server="))
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'vt-peer' (vt-peer, Local)"))
            .OnPrefix("cli_connect_steam_user ", command => { joined = hosting && command.EndsWith(" 76561197960266229", StringComparison.Ordinal);
                return ScriptedTransport.Ok("OK: Steam user join started for 76561197960266229 using character 'vt-peer' (vt-peer, Local)"); })
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = joined ? "world-present" : "menu", worldUid = joined ? "4242" : null, worldPresent = joined,
                worldReady = joined, server = false, dedicated = false, localPlayer = joined, playerReady = joined, saving = false, loadError = false,
                connectionStatus = joined ? "Connected" : "None",
            })
            .Extension("valheim.session", "leave", _ => { joined = false; return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True ghostReplicated=True"));
        ClientRunPlan Section(bool host) => new()
        {
            Mode = "owned", Architecture = "x64", Install = "/bound", Port = 5578, Character = "bound", StartSeconds = 30, JoinSeconds = 10,
            Pins = new() { ["example.client"] = "<md5>" },
            JoinsHost = !host, HostWorld = host ? new() { World = new() { Source = world }, WorldUid = "4242" } : null,
        };
        var sections = new Dictionary<string, ClientRunPlan> { ["host"] = Section(host: true), ["peer"] = Section(host: false) };
        bool scenarioRan = false;
        var localProcess = new FakeOwnedProcess(81);
        var options = new HostedRunOptions<Dictionary<string, ClientRunPlan>>
        {
            Name = "hosted-campaign", ReadPlan = _ => throw new InvalidOperationException("A campaign plan is in memory."), Host = plan => plan["host"],
            Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
            Scenario = async (session, _) =>
            {
                Assert.Equal("host", session.Host!.Name);
                await session.Join("peer");
                Assert.True(joined);
                scenarioRan = true;
            },
            Hooks = new FakeRunHooks
            {
                Host = name => hosts[name], Connect = port => port is 15578 or 5578 or 6578 ? hostTransport : peerTransport, StateWaits = false, RunId = "run-hosted",
                // The hosting client on this machine: launched through the local client launch seam, no game, its process faked.
                RequireMacGui = localHost ? () => { } : null,
                LocalMacLaunch = localHost ? (plan, launchOutput, account, cancellation, processStarted) =>
                {
                    processStarted?.Invoke(localProcess);
                    return ClientSession.Launch(plan, launchOutput, () => localProcess, () => hostTransport, (_, _) => Task.CompletedTask, cancellation, null, null, account);
                } : null,
            },
        };
        string output = Path.Combine(_rig.Root, "hosted-run");
        int code = await PinnedServerRun.RunCampaignAsync(manifest, sections, plan => plan, output, options);
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement;
        var steps = result.GetProperty("Steps").EnumerateArray().Select(step => (Name: step.GetProperty("Name").GetString()!, Passed: step.GetProperty("Passed").GetBoolean(),
            Error: step.GetProperty("Error").GetString())).ToList();
        Assert.True(code == 0, string.Join(" | ", steps.Where(step => !step.Passed).Select(step => step.Name + ": " + step.Error)));
        Assert.True(scenarioRan); Assert.True(placedWhileHosting);
        var pc = hosts["pc"];
        // Placed in the host user's worlds_local, journalled first; then moved out into the run's folder there and fetched.
        var placed = pc.Runs.ToList();
        int listed = placed.FindIndex(run => run.Script == "world-entries");
        Assert.True(listed >= 0);
        Assert.Equal(worlds, placed[listed].Variables["worlds"]);
        Assert.Contains(placed, run => run.Script == "world-move" && run.Variables["to"] == On("pc", "/runs/run-hosted/host-world"));
        Assert.Equal(new[] { "MyWorld.fwl" }, Directory.EnumerateFileSystemEntries(pc.Local(worlds)).Select(Path.GetFileName));
        Assert.Equal("the user's world", File.ReadAllText(pc.Local(worlds + "/MyWorld.fwl")));
        // Fetched into the evidence, the host's collected copy goes, and the run's folder there with it (#791).
        Assert.False(Directory.Exists(pc.Local(On("pc", "/runs/run-hosted"))));
        string evidence = result.GetProperty("Provenance").GetProperty("hostWorldEvidence").GetString()!;
        Assert.True(File.Exists(Path.Combine(evidence, "Campaign", "_main.1.fwl2")));
        Assert.True(File.Exists(Path.Combine(evidence, "Campaign_backup_auto-20261006-120000", "_main.1.db2")));
        var journal = await RunJournalOnHost.ReadAsync(pc, RunJournal.DirectoryFor(EnvironmentInventory.Read(inventory).Hosts["pc"]), "run-hosted", TimeSpan.FromSeconds(5));
        var worldEntries = journal.Where(record => record.Entry.Fields.GetValueOrDefault("runtime") == worlds + "/Campaign").Select(record => record.Entry.Kind).ToList();
        Assert.Equal(new[] { JournalEntry.CopyIntended, JournalEntry.CopyIntended, JournalEntry.CopyDone, JournalEntry.CopyRetired }, worldEntries);
        // Teardown: the peer first, then the host leaves and stops, then its world; both peer and host were stopped on their hosts.
        var cleanup = steps.Select(step => step.Name).SkipWhile(name => !name.StartsWith("stop only the owned client peer", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "stop only the owned client peer", "the host leaves its world to its menu", "stop only the owned hosting client host",
            "move the hosted world from the client's local worlds into the evidence" }, cleanup.Take(4));
        Assert.Single(hosts["laptop"].Stops);
        if (localHost) { Assert.Equal(1, localProcess.Stops); Assert.True(steamChecks > 0); } else Assert.Single(pc.Stops); // a local client is this machine's process
        Assert.Equal(pc.Claims.Count, pc.Releases.Count); Assert.Equal(hosts["laptop"].Claims.Count, hosts["laptop"].Releases.Count);
    }

    // A campaign's inputs as the tests write them: a ready lock for a mod, a registered character store, and the campaign's
    // character object for a Windows client host.
    private string Lock(string mod, string name)
    {
        var resolved = NativeDependencyResolver.Resolve(Request(mod));
        Assert.True(resolved.Ready, string.Join("; ", resolved.Gaps.Select(gap => gap.Reason)));
        string path = Path.Combine(_rig.Root, name + "-lock.json");
        resolved.Write(path);
        return path;
    }
    private string Store(string name, long id, string prefix = "")
    {
        string local = _rig.Write(prefix + "characters_local/" + name + ".fch", CharacterSaveReaderTests.Profile(playerId: id).File);
        string store = Path.Combine(_rig.Root, (prefix + "registered-" + name).Replace('/', '-'));
        DisposableCharacterStore.Create(store).Register(name, local);
        return store;
    }
    private static object Character(string store, string registeredName, string fileName) => new
    {
        store, registeredName, fileName, charactersLocalDirectory = @"C:\save\characters_local", steamUserDataDirectory = @"C:\Steam\userdata",
    };

    private NativeDependencyRequest Request(string mod) => new()
    {
        Mods = [mod], SearchRoots = [Path.Combine(_rig.Root, "deps")],
        GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(_rig.Game))!,
        BepInExCore = Path.Combine(_rig.Game, "BepInEx", "core"),
        CliManifest = _rig.CliManifest(save: true), CliFiles = Path.Combine(_rig.Root, "cli"),
        Capabilities = ["valheim.session/state", "valheim.session/save"],
    };
}
