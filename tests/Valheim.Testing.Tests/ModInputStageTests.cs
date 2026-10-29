using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;
using Xunit;

// #673: build, start and server-load share one mod-input stage, so the same inputs give the same game, loader and refusal.
public sealed class ModInputStageTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    private readonly IDisposable _preflight = LocalHostPreflight.ReplaceDefaultProbesForTest(new(
        Processes: (_, _, _, _) => Task.CompletedTask, Port: (_, _, _, _) => Task.CompletedTask,
        Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
        Desktop: _ => Task.CompletedTask, MacDesktop: () => { }, SteamRunning: () => true,
        Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null));
    public void Dispose() { _preflight.Dispose(); _rig.Dispose(); }

    private string DedicatedGame()
    {
        string server = Path.Combine(_rig.Root, "server-game");
        FakeInstalls.Server(server);
        if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.Combine(server, "valheim_server"));
            Directory.Move(Path.Combine(server, "valheim_server_Data"), Path.Combine(server, "valheim_server", "Data"));
        }
        string executable = Path.Combine(server, ServerRunPlan.ExecutableFor(HostProfile.CurrentPlatform switch
            { "windows" => ServerPlatform.Windows, "macos" => ServerPlatform.MacOS, _ => ServerPlatform.Linux }));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!); File.WriteAllText(executable, "synthetic dedicated server");
        string core = Path.Combine(server, InstallPins.CoreDirectory);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(_rig.Game, InstallPins.CoreDirectory)))
            File.Copy(file, Path.Combine(core, Path.GetFileName(file)), overwrite: true);
        string managed = Path.GetDirectoryName(InstallPins.GameAssembly(server))!;
        File.Copy(Path.Combine(Path.GetDirectoryName(InstallPins.GameAssembly(_rig.Game))!, "System.Private.CoreLib.dll"),
            Path.Combine(managed, "System.Private.CoreLib.dll"));
        File.WriteAllText(Path.Combine(managed, "System.Runtime.dll"), "provided synthetic framework assembly");
        return server;
    }

    private ModInputStage.Request TwoRoles(RegressionRig client, string output, ModInputStage.RoleSelection selection,
        string? serverProject = null)
    {
        string server = DedicatedGame();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(InstallPins.GameAssembly(client.Game))!, "System.Runtime.dll"), "provided synthetic framework assembly");
        var options = new Dictionary<string, string>
        {
            ["--server"] = server, ["--client"] = client.Game,
            ["--cli-manifest"] = _rig.CliManifest(save: true, full: true), ["--cli-files"] = Path.Combine(_rig.Root, "cli"),
        };
        if (serverProject != null) options["--project"] = serverProject;
        return new(ModInputStage.Route.Dedicated, output, options, new HashSet<string>(),
            serverProject == null ? [_rig.Parent] : [], [Path.Combine(_rig.Root, "deps")], [], [])
            { ClientSelection = selection, ClientArchitecture = (_, _, _, _) => { } };
    }

    [ClientHostTheory] [InlineData("mod")] [InlineData("game")] [InlineData("loader")]
    public async Task ClientBuildRecordUsesItsSelectedGameAndCoreBeforeHostChecks(string drift)
    {
        using var client = new RegressionRig();
        string dependency = _rig.Write("compile/BepInEx.dll", RegressionRig.Assembly("BepInEx", null, "server-core"));
        client.Write("game/BepInEx/core/BepInEx.dll", RegressionRig.Assembly("BepInEx", null, "client-core"));
        string harmony = client.Write("game/BepInEx/core/0Harmony.dll", RegressionRig.Assembly("0Harmony", null, "client-core"));
        string gameSource = InstallPins.GameAssembly(client.Game);
        string publicized = _rig.Write("client-compile/assembly_valheim_publicized.dll", File.ReadAllBytes(gameSource));
        string compiledCore = drift == "loader" ? dependency : Path.Combine(client.Game, InstallPins.CoreDirectory, "BepInEx.dll");
        string record = _rig.Write("client-build.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ModBuild.BuildRecord(
            client.Game, drift == "game" ? new string('0', 64) : InstallPins.GameHash(client.Game),
            Path.GetDirectoryName(gameSource)!, _rig.Candidate,
            drift == "mod" ? new string('0', 64) : FileHash.Sha256(_rig.Candidate), "BepInEx.AssemblyPublicizer/0.4.3",
            [new(gameSource, FileHash.Sha256(gameSource))], [new(publicized, FileHash.Sha256(publicized))],
            [new(compiledCore, FileHash.Sha256(compiledCore)), new(harmony, FileHash.Sha256(harmony))]))));
        int probes = 0;
        using var check = LocalHostPreflight.ReplaceDefaultProbesForTest(new(Processes: (_, _, _, _) => { probes++; return Task.CompletedTask; }, Packaged: () => null));
        var request = TwoRoles(client, Path.Combine(_rig.Root, "bad-client-" + drift), new([_rig.Candidate], [], [], []) { BuildInputs = record });
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ModInputStage.RunAsync(request, default));
        string expected = drift switch
        {
            "mod" => "Build input changed since preparation: " + _rig.Candidate,
            "game" => "Selected native game build differs from the mod's compile game SHA256",
            _ => "The selected native loader has a different BepInEx.dll",
        };
        Assert.True(failure.Message.Contains(expected, StringComparison.Ordinal), failure.ToString());
        Assert.Equal(0, probes); Assert.False(Directory.Exists(Path.Combine(request.Output, "build")));
        Assert.False(Directory.Exists(Path.Combine(request.Output, "client", "build")));
    }

    [ClientHostFact] public async Task BothExplicitProjectsBuildOnceAgainstTheirOwnCoreAndRecordSeparateLocks()
    {
        using var client = new RegressionRig();
        foreach (var (rig, marker) in new[] { (_rig, "server"), (client, "client") })
        {
            rig.Write("game/BepInEx/core/BepInEx.dll", RegressionRig.Assembly("BepInEx", null, marker));
            rig.Write("game/BepInEx/core/0Harmony.dll", RegressionRig.Assembly("0Harmony", null, marker));
        }
        string Project(RegressionRig rig, string version)
        {
            string project = rig.Write("role-project/RoleMod.csproj", Encoding.UTF8.GetBytes("""
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                <Target Name="CountRoleBuild" BeforeTargets="CoreCompile"><WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="build" Overwrite="false" /></Target></Project>
                """));
            rig.Write("role-project/RoleMod.cs", Encoding.UTF8.GetBytes("""
                namespace BepInEx {
                    [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class BepInPlugin : System.Attribute {
                        public BepInPlugin(string guid, string name, string version) { }
                    }
                }
                [BepInEx.BepInPlugin("test.rolemod", "RoleMod", "VERSION")] public sealed class RoleMod { }
                """.Replace("VERSION", version, StringComparison.Ordinal)));
            return project;
        }
        string serverProject = Project(_rig, "1.0.0"), clientProject = Project(client, "2.0.0");
        var request = TwoRoles(client, Path.Combine(_rig.Root, "two-builds"), new([], [], [], []) { Project = clientProject }, serverProject);
        var inputs = await ModInputStage.RunAsync(request, default);
        var selectedClient = Assert.IsType<ModInputStage.SelectedRole>(inputs.ClientMods);
        Assert.Equal(Path.Combine(request.Options["--server"], InstallPins.CoreDirectory), inputs.Core);
        Assert.NotEqual(inputs.Compile.Recipe.Install, selectedClient.Actor.Recipe.Install);
        Assert.Equal(Path.Combine(client.Game, InstallPins.CoreDirectory), selectedClient.Core);
        Assert.Single(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(serverProject)!, "build-count.txt")));
        Assert.Single(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(clientProject)!, "build-count.txt")));
        Assert.NotEqual(Assert.Single(inputs.Dependencies!.Mods).Sha256, Assert.Single(selectedClient.Dependencies.Mods).Sha256);
        var serverRecord = JsonSerializer.Deserialize<ModBuild.BuildRecord>(File.ReadAllText(inputs.BuildInputs!), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var clientRecord = JsonSerializer.Deserialize<ModBuild.BuildRecord>(File.ReadAllText(selectedClient.BuildInputs!), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(request.Options["--server"], serverRecord.Game); Assert.Equal(client.Game, clientRecord.Game);
        Assert.Contains(clientRecord.Dependencies, file => file.File == Path.Combine(client.Game, InstallPins.CoreDirectory, "BepInEx.dll"));
        Assert.True(File.Exists(Path.Combine(request.Output, "dependencies.lock.json")));
        Assert.True(File.Exists(Path.Combine(request.Output, "client", "dependencies.lock.json")));
    }

    [Fact] public void RemoteExplicitClientRefusesBeforeLoaderMaterializationOrOutput()
    {
        string server = DedicatedGame();
        string inventory = _rig.Write("remote-inventory.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            hosts = new { remote = new { kind = "ssh", platform = "windows", shell = "pwsh", destination = "test@remote", @lock = "C:/vt/lock" } },
            environments = new object[] { new { name = "server", roles = new[] { "server" }, install = server,
                    runtime = Path.Combine(_rig.Root, "server-runs"), cliPort = 5560, gamePort = 2456 },
                new { name = "remote-client", host = "remote", roles = new[] { "client" }, install = "C:/game", runtime = "C:/runs", cliPort = 5556 } },
            leaseHost = "remote", leaseDirectory = "C:/vt/leases",
        })));
        string output = Path.Combine(_rig.Root, "remote-refused"); int loaders = 0;
        var request = new ModInputStage.Request(ModInputStage.Route.Dedicated, output,
            new Dictionary<string, string> { ["--inventory"] = inventory, ["--join"] = "127.0.0.1:2456" }, new HashSet<string>(), [_rig.Parent], [], [], [])
        { ClientSelection = new([_rig.Candidate], [], [], []), Shipped = (_, _, _) => { loaders++; throw new Exception("unexpected materialization"); } };
        var refusal = Record.Exception(() => ModInputStage.Choose(request));
        Assert.True(refusal is ArgumentException && refusal.Message.Contains("local joining client", StringComparison.Ordinal),
            refusal?.ToString() ?? "The remote client was accepted.");
        Assert.Equal(0, loaders); Assert.False(Directory.Exists(output));
    }

    // The rig's game plays a vanilla dedicated server: its Steam install has no BepInEx or Doorstop, as a profile run
    // leaves it (#601). The environment names a reviewed loader package instead.
    private string VanillaServerWithLoaderPackage(out string package)
    {
        // A Windows-layout server is a compile source on every OS (as in ModBuildTests); nothing here launches it.
        _rig.Write("game/" + GameLaunch.ServerWindowsExecutable, Encoding.UTF8.GetBytes("fake dedicated executable"));
        _rig.Write("game/BepInEx/core/0Harmony.dll", Encoding.UTF8.GetBytes("the pack's Harmony"));
        var captured = BepInExLoaderPackage.Capture(_rig.Game, "BepInExPack_Valheim", "test");
        string root = Path.Combine(_rig.Root, "loader");
        foreach (string relative in captured.Files.Keys)
        {
            string target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(_rig.Game, relative.Replace('/', Path.DirectorySeparatorChar)), target);
        }
        foreach (string relative in captured.Files.Keys)
            File.Delete(Path.Combine(_rig.Game, relative.Replace('/', Path.DirectorySeparatorChar)));
        Directory.Delete(Path.Combine(_rig.Game, "BepInEx"), recursive: true);
        Assert.False(Directory.Exists(Path.Combine(_rig.Game, "BepInEx")));
        package = Path.Combine(_rig.Root, "loader.json");
        BepInExLoaderPackage.Capture(root, "BepInExPack_Valheim", "test").Write(package);
        string inventory = Path.Combine(_rig.Root, "inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            environments = new[] { new { name = "vanilla-server", roles = new[] { "server" }, install = _rig.Game, loaderPackage = package } },
        }));
        return inventory;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedDefaultOrExplicitWorldStopsBeforeTheLoaderAndCliBundles(bool explicitFixture)
    {
        using var data = new FakeDataRoot(Path.Combine(_rig.Root, "early-data"));
        File.WriteAllBytes(InstallPins.GameAssembly(_rig.Game), FakeInstalls.GameAssembly(maximum: 40));
        string output = Path.Combine(_rig.Root, "refused"); int loaders = 0;
        var options = new Dictionary<string, string> { ["--server"] = _rig.Game };
        if (explicitFixture) options["--world-fixture"] = _rig.Fixture;
        var request = new ModInputStage.Request(ModInputStage.Route.Dedicated, output, options, new HashSet<string> { "--server-only" }, [], [], [], [])
        {
            Shipped = (_, _, _) => { loaders++; throw new Xunit.Sdk.XunitException("Loader extracted before admission"); },
        };
        Assert.Throws<InvalidDataException>(() => ModInputStage.Choose(request));
        Assert.Equal(0, loaders); Assert.False(Directory.Exists(output)); Assert.False(Directory.Exists(CliBundle.DataRoot));
    }

    // Acceptance 2. On main before #673, build chose the first server install and compiled against its own BepInEx:
    // with a vanilla install it refused ("selected loader lacks BepInEx core") where start and server-load used the
    // environment's loader package. Now the same environment choice gives build that package.
    [Fact]
    public async Task BuildCompilesAgainstTheEnvironmentsLoaderPackageOnAVanillaInstall()
    {
        using var dataRoot = new FakeDataRoot(Path.Combine(_rig.Root, "machine"));
        string inventory = VanillaServerWithLoaderPackage(out string package);
        // Real IL for the publicizer; the rig's other game files are placeholders.
        File.WriteAllBytes(InstallPins.GameAssembly(_rig.Game), RegressionRig.Assembly("assembly_valheim", null));
        string project = _rig.Write("mod/VanillaMod.csproj", Encoding.UTF8.GetBytes(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"));
        _rig.Write("mod/VanillaMod.cs", Encoding.UTF8.GetBytes("""
            namespace BepInEx
            {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class BepInPlugin : System.Attribute
                {
                    public BepInPlugin(string guid, string name, string version) { Guid = guid; Name = name; Version = version; }
                    public string Guid { get; }
                    public string Name { get; }
                    public string Version { get; }
                }
            }
            [BepInEx.BepInPlugin("test.vanilla", "Vanilla", "1.0.0")]
            public sealed class VanillaMod { }
            """));
        string output = Path.Combine(_rig.Root, "build-run");

        int result = await ModBuild.RunAsync(["--role", "server", "--project", project, "--inventory", inventory, "--output", output]);

        Assert.Equal(0, result);
        var record = JsonSerializer.Deserialize<ModBuild.BuildRecord>(File.ReadAllText(Path.Combine(output, "build", "build-inputs.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        string core = Path.Combine(BepInExLoaderPackage.Read(package).Root, InstallPins.CoreDirectory);
        Assert.Contains(record.Dependencies, pin => pin.File == Path.Combine(core, "BepInEx.dll"));
        Assert.False(Directory.Exists(Path.Combine(_rig.Game, "BepInEx"))); // the install stays vanilla
        // The same build inputs then pass the run's loader match against that environment's package.
        ModBuild.RequireLoaderMatches(record, core);
    }

    // One refusal for one condition: a vanilla install with no loader package is refused before any build, and build
    // and server-load give the same words.
    [Fact]
    public async Task AVanillaInstallWithoutALoaderGetsTheSameRefusalFromBuildAndServerLoad()
    {
        VanillaServerWithLoaderPackage(out _);
        string project = _rig.Write("mod/VanillaMod.csproj", Encoding.UTF8.GetBytes("<Project />"));
        var options = new Dictionary<string, string> { ["--role"] = "server", ["--server"] = _rig.Game, ["--project"] = project };
        async Task<string> Refusal(ModInputStage.Route route, string name)
        {
            string output = Path.Combine(_rig.Root, name);
            var progress = new ModInputStage.Progress();
            var failure = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ModInputStage.RunAsync(new ModInputStage.Request(route, output,
                options, new HashSet<string> { "--server-only" }, [], [], [], []) { Progress = progress }, CancellationToken.None));
            Assert.False(progress.OutputChecked);
            Assert.False(Path.Exists(output));
            return failure.Message.Replace(output, "", StringComparison.Ordinal);
        }
        string build = await Refusal(ModInputStage.Route.Build, "build-refused");
        Assert.Equal(build, await Refusal(ModInputStage.Route.Dedicated, "server-load-refused"));
        Assert.Contains("--loader-package", build);
        Assert.Contains("loaderPackage", build);
    }

    // Pure input errors come before the host probes (#626); the host probes come before any build, so a run that cannot
    // start is refused before minutes of compiling.
    [Fact]
    public async Task HostChecksComeAfterInputErrorsAndBeforeAnyBuild()
    {
        _rig.Write("game/" + GameLaunch.ServerWindowsExecutable, Encoding.UTF8.GetBytes("fake dedicated executable"));
        string project = _rig.Write("mod/Unbuildable.csproj", Encoding.UTF8.GetBytes("<Project>never built</Project>"));
        int probes = 0;
        using var busy = LocalHostPreflight.ReplaceDefaultProbesForTest(new(
            Processes: (_, _, _, _) => Task.CompletedTask, Port: (_, _, _, _) => Task.CompletedTask,
            Lock: (_, _, _, _) => { probes++; return Task.FromResult(new HostLockResult(HostLockState.HeldByOther, "other", "another run holds it")); },
            Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null));
        ModInputStage.Request Request(string output, params (string Key, string Value)[] more)
        {
            var options = new Dictionary<string, string>
            {
                ["--server"] = _rig.Game, ["--output"] = output,
                ["--cli-manifest"] = _rig.CliManifest(save: true, full: true), ["--cli-files"] = Path.Combine(_rig.Root, "cli"),
            };
            foreach (var (key, value) in more) options[key] = value;
            // With no --project the mod is a given DLL: the rig's, for build inputs to check, else one that does not exist.
            var mods = options.ContainsKey("--project") ? [] : new List<string>
                { options.ContainsKey("--build-inputs") ? _rig.Parent : Path.Combine(_rig.Root, "missing", "Mod.dll") };
            return new(ModInputStage.Route.Dedicated, output, options, new HashSet<string> { "--server-only" }, mods, [], [], []);
        }

        string refused = Path.Combine(_rig.Root, "host-refused");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ModInputStage.RunAsync(Request(refused, ("--project", project)), CancellationToken.None));
        Assert.Contains("another run holds it", failure.Message);
        Assert.Equal(1, probes);
        Assert.False(Path.Exists(refused)); // no build, lock or record was written

        // Each input error is refused without a host probe: not a Mac server (or, on a Mac, no such package), a bake
        // target inside the server install, a missing mod, and build inputs that do not exist.
        string steam = Path.Combine(_rig.Root, "steam-runtime");
        foreach (var (name, extra) in new (string, (string, string)[])[]
        {
            ("steam-refused", [("--project", project), ("--steam-runtime-package", steam)]),
            ("bake-refused", [("--project", project), ("--bake-fixture", Path.Combine(_rig.Game, "baked"))]),
            ("mod-refused", []),
            ("build-inputs-refused", [("--build-inputs", Path.Combine(_rig.Root, "missing", "build-inputs.json"))]),
        })
        {
            string output = Path.Combine(_rig.Root, name);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => ModInputStage.RunAsync(Request(output, extra), CancellationToken.None));
            Assert.IsNotType<InvalidOperationException>(error); // not the host refusal
            Assert.False(Path.Exists(output), name);
        }
        Assert.Equal(1, probes); // no input error waited for a host probe
    }

    // #745: server-load-ab chooses through the stage, not on its own, so an environment's client loaderPackage is guarded
    // exactly as server-load and the stage itself guard it, and the stage's choice stops before the mod step.
    [ClientHostFact]
    public async Task AnEnvironmentsClientLoaderIsProtectedAlikeByTheStageServerLoadAndServerLoadAb()
    {
        var source = BepInExLoaderPackage.Capture(_rig.Game, "client-loader", "1");
        string loaderRoot = Path.Combine(_rig.Root, "client-loader");
        foreach (string relative in source.Files.Keys)
        {
            string destination = Path.Combine(loaderRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(_rig.Game, relative.Replace('/', Path.DirectorySeparatorChar)), destination);
        }
        string package = Path.Combine(_rig.Root, "client-loader.json");
        BepInExLoaderPackage.Capture(loaderRoot, "client-loader", "2").Write(package);
        string clientInstall = Path.Combine(_rig.Root, "client");
        Directory.CreateDirectory(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, "client-marker"), "a client install; an A/B run pins its files");
        string inventory = Path.Combine(_rig.Root, "client-loader-inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            environments = new object[]
            {
                new { name = "chosen-server", roles = new[] { "server" }, install = _rig.Game },
                new { name = "chosen-client", roles = new[] { "client" }, install = clientInstall, loaderPackage = package },
            },
        }));
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(SmokeSessionContract.SessionAdapterPluginGuid)));
        string cliManifest = _rig.CliManifest(save: true, full: true), cliFiles = Path.Combine(_rig.Root, "cli");
        void NoArchitectureCheck(EnvironmentInventory _, EnvironmentRecipe __, string ___, string? ____) { }
        string unsafeOutput = Path.Combine(loaderRoot, "run");

        // The stage's own guard names the client's loader root, and stops before it reads or writes anything.
        var stageFailure = Assert.Throws<ArgumentException>(() => ModInputStage.Choose(new ModInputStage.Request(ModInputStage.Route.Dedicated,
            unsafeOutput, new Dictionary<string, string> { ["--inventory"] = inventory, ["--cli-manifest"] = cliManifest, ["--cli-files"] = cliFiles },
            new HashSet<string>(), [], [], [], []) { ClientArchitecture = NoArchitectureCheck }));
        Assert.Contains(loaderRoot, stageFailure.Message);

        // server-load takes it through the stage.
        int ranCampaign = 0;
        int serverLoad = await ServerLoad.RunAsync(["--inventory", inventory, "--mod", _rig.Parent, "--cli-manifest", cliManifest,
            "--cli-files", cliFiles, "--adapter", adapter, "--output", unsafeOutput],
            new ServerLoad.Seams(Inspect: _ => throw new Xunit.Sdk.XunitException("A refused output reached the host preflight."),
                Campaign: (_, _, _, _, _) => { ranCampaign++; return Task.FromResult(0); }, ClientArchitecture: NoArchitectureCheck));
        Assert.Equal(3, serverLoad);
        Assert.Equal(0, ranCampaign);

        // server-load-ab too: the same output is refused, and a safe one is not.
        int arms = 0;
        string[] Comparison(string output) => ["--inventory", inventory, "--mod", _rig.Parent, "--mod", companion, "--remove-mod", companion,
            "--search-root", Path.Combine(_rig.Root, "deps"), "--adapter", adapter, "--cli-manifest", cliManifest, "--cli-files", cliFiles, "--output", output];
        Assert.Equal(3, await ServerLoadComparison.RunAsync(Comparison(unsafeOutput), runArm: _ => { arms++; return Task.FromResult(0); },
            clientArchitecture: NoArchitectureCheck));
        Assert.Equal(0, arms);
        Assert.False(Directory.Exists(unsafeOutput));
        Assert.Equal(0, await ServerLoadComparison.RunAsync(Comparison(Path.Combine(_rig.Root, "safe-ab")), runArm: _ => { arms++; return Task.FromResult(0); },
            clientArchitecture: NoArchitectureCheck));
        Assert.Equal(2, arms);
    }

    // #745: the stage's choice alone reads nothing but the environment and the pinned inputs, and writes nothing: a wrapper
    // that needs the choice before the stage runs takes it from here.
    [Fact]
    public void TheChoiceAloneNamesTheCoreAndCapabilitiesWithoutWritingTheOutput()
    {
        string output = Path.Combine(_rig.Root, "choice-only");
        var options = new Dictionary<string, string>
        {
            ["--server"] = _rig.Game, ["--cli-manifest"] = _rig.CliManifest(save: true, full: true), ["--cli-files"] = Path.Combine(_rig.Root, "cli"),
        };
        var progress = new ModInputStage.Progress();
        var inputs = ModInputStage.Choose(new ModInputStage.Request(ModInputStage.Route.Dedicated, output, options,
            new HashSet<string> { "--server-only" }, [], [], [], []) { Progress = progress });

        Assert.True(progress.OutputChecked);
        Assert.False(Path.Exists(output));
        Assert.Equal(Path.Combine(_rig.Game, InstallPins.CoreDirectory), inputs.Core);
        Assert.Equal(["valheim.session/state"], inputs.Capabilities);
        Assert.Equal(options["--cli-manifest"], inputs.CliManifest);
        Assert.Empty(inputs.Mods);
        Assert.Null(inputs.Dependencies);
    }

    // Build takes a role's install, environment and loader with the same options as the command that runs that role.
    [Fact]
    public void BuildParsesTheRunCommandsRoleOptions()
    {
        static string Refused(params string[] args)
        {
            Assert.False(SmokeCommandOptions.TryRead(args, SmokeCommandOptions.Command.Build, allowImplicitMod: true, out _, out string error));
            return error;
        }
        Assert.True(SmokeCommandOptions.TryRead(["--project", "m.csproj", "--role", "server", "--server", "s", "--loader-package", "l.json",
            "--search-root", "deps", "--dependency", "Jotunn=J.dll"], SmokeCommandOptions.Command.Build, allowImplicitMod: true, out var server, out string serverError), serverError);
        Assert.Equal(["deps"], server!.List("--search-root"));
        Assert.True(SmokeCommandOptions.TryRead(["--project", "m.csproj", "--game", "g", "--client-env", "c", "--client-loader-package", "l.json"],
            SmokeCommandOptions.Command.Build, allowImplicitMod: true, out _, out string clientError), clientError);
        Assert.Equal("Give --project MOD.csproj.", Refused("--game", "g"));
        Assert.Equal("--role must be client or server.", Refused("--project", "m.csproj", "--role", "both"));
        Assert.StartsWith("--game selects the other role's install", Refused("--project", "m.csproj", "--role", "server", "--game", "g"));
        Assert.StartsWith("--loader-package selects the other role's install", Refused("--project", "m.csproj", "--loader-package", "l.json"));
        Assert.Equal("Unknown option: --mod", Refused("--project", "m.csproj", "--mod", "m.dll"));
        Assert.Equal("Unknown option: --scenario", Refused("--project", "m.csproj", "--scenario", "t.dll"));
    }
}
