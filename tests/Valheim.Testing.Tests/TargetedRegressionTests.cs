using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;
using Xunit;

// Targeted native regressions (#125): the setup mistakes two hand-written A/B runs hit before their first assertion, each
// refused before the game starts and naming the corrective action. Plugins are small assemblies emitted here with BepInEx's
// attribute shapes; no game, Steam or network connection.
public sealed class TargetedRegressionTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public void DirectClientLaunchHasNoWindowsSessionZeroRoute()
    {
        Assert.Contains("Windows SSH/session 0", Assert.Throws<InvalidOperationException>(() =>
            DirectClientDesktop.Require((true, 0))).Message);
        DirectClientDesktop.Require((true, 1));
        DirectClientDesktop.Require((false, 0));
    }

    [Fact] public void AGeneratedConsumerRefusesWindowsSshBeforePreflightCopiesTheGame()
    {
        var regression = _rig.Regression(_rig.Manifest());
        regression.DirectClientSession = () => (true, 0);

        var error = Assert.Throws<InvalidOperationException>(() => regression.Preflight());
        Assert.Contains("Windows SSH/session 0", error.Message);
        Assert.Contains("interactive desktop terminal", error.Message);
        Assert.Contains("valheim-test start", error.Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    [Fact] public void AGeneratedConsumerRefusesWindowsSshBeforeRunWritesEvidenceOrCopiesTheGame()
    {
        var regression = _rig.Regression(_rig.Manifest());
        regression.DirectClientSession = () => (true, 0);
        string output = Path.Combine(_rig.Root, "ssh-refused");

        var error = Assert.Throws<InvalidOperationException>(() => regression.Run("parent", output, "smoke", ["first"], _ => { }));
        Assert.Contains("Windows SSH/session 0", error.Message);
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(_rig.Install));
    }

    [ClientHostTheory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void ADesktopOrNonWindowsPreflightCanStage(bool windows, int sessionId)
    {
        var regression = _rig.Regression(_rig.Manifest());
        regression.DirectClientSession = () => (windows, sessionId);

        Assert.Equal(2, regression.Preflight().Count);
        Assert.True(Directory.Exists(_rig.Install));
    }

    [Fact] public void RegressionClientArchitectureMustBeASupportedSlice()
    {
        var client = new RegressionClient { Character = "Tester", Architecture = "arm64" };
        client.Validate();
        client.Architecture = "native";
        Assert.Contains("client.architecture", Assert.Throws<ArgumentException>(client.Validate).Message);
    }

    [Fact] public void WindowsRegressionUsesItsExplicitRuntimeWithoutOfferingInPlace()
    {
        if (!OperatingSystem.IsWindows()) return;
        var inventory = _rig.Inventory();
        inventory.Environments[0].Runtime = @"D:\vt\regressions";
        var machine = new FakeMachine("windows");
        using var useMachine = EnvironmentInventory.UseMachine(machine);
        var regression = new TargetedRegression(_rig.Manifest(), inventory: inventory);

        var error = Assert.Throws<InvalidOperationException>(() => regression.Stage("parent"));

        Assert.Contains("explicit inventory runtime folder", error.Message);
        Assert.DoesNotContain("--in-place", error.Message);
    }

    [Fact] public void RegressionInputsOverrideTheInventorySlice()
    {
        var inventory = _rig.Inventory();
        inventory.Hosts["local"].Platform = "macos";
        inventory.Environments[0].Architecture = "arm64";
        var inputs = _rig.Manifest();
        Assert.Equal("arm64", new TargetedRegression(inputs, inventory: inventory).Architecture);
        inputs.Client.Architecture = "x64";
        Assert.Equal("x64", new TargetedRegression(inputs, inventory: inventory).Architecture);
    }

    [Fact] public void ARejectedMacSourceStopsBeforeTheDisposableCopy()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var runner = new TargetedRegression(_rig.Manifest(), inventory: _rig.Inventory())
        {
            SaveDirectory = _rig.Save,
            BundleInspection = (_, _) => new(MacBundleInspection.State.Broken, 1, "changed signed game file"),
        };
        Assert.Contains("Verify the game's files in Steam", Assert.Throws<InvalidOperationException>(() => runner.Stage("parent")).Message);
        Assert.False(Directory.Exists(runner.Install));
    }

    [Theory]
    [InlineData(9, 180)]
    [InlineData(300, 901)]
    public void RegressionClientUsesTheSameStartAndJoinLimitsAsClientRunPlan(int start, int join)
    {
        var client = new RegressionClient { Character = "disposable", StartSeconds = start, JoinSeconds = join };
        Assert.Throws<ArgumentException>(client.Validate);
        Assert.Equal(ClientTimeouts.DefaultStartSeconds, new RegressionClient().StartSeconds);
        Assert.Equal(ClientTimeouts.DefaultJoinSeconds, new RegressionClient().JoinSeconds);
    }

    [Fact] public void AnX64RegressionWithAnArm64OnlyLoaderStopsBeforeTheDisposableCopy()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var native = ClientLaunchTests.Install.Mac(packDoorstop: false, arm64Doorstop: true, core: new Version(25, 3, 4));
        var inventory = _rig.Inventory();
        inventory.Environments[0].Install = native.Root;
        inventory.Environments[0].Architecture = "x64";
        var runner = new TargetedRegression(_rig.Manifest(), inventory: inventory) { SaveDirectory = _rig.Save };

        Assert.Contains("x86_64 slice", Assert.Throws<InvalidOperationException>(() => runner.Stage("parent")).Message);
        Assert.False(Directory.Exists(runner.Install));
    }

    // The profile launches the source bundle, so a source macOS rejects is refused before anything is staged.
    [Fact] public void ARejectedMacSourceIsRefusedBeforeAnyProfileIsStaged()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var runner = new TargetedRegression(_rig.Manifest(), inventory: _rig.Inventory())
        {
            SaveDirectory = _rig.Save,
            BundleInspection = (_, _) => new(MacBundleInspection.State.Rejected, 0, "Gatekeeper refused the source"),
            ProcessCheck = (_, _) => Task.CompletedTask,
            SkipHostLockForTest = true,
        };
        Assert.Contains("notarized", Assert.Throws<InvalidOperationException>(() => runner.Stage("parent")).Message);
        Assert.False(Directory.Exists(runner.Install));
        Assert.True(Directory.Exists(_rig.Game));
    }

    [Fact] public void EachMacArmAssessesItsSourceBeforeStaging()
    {
        if (!OperatingSystem.IsMacOS()) return;
        int inspections = 0;
        bool reject = false;
        var runner = new TargetedRegression(_rig.Manifest(), inventory: _rig.Inventory())
        {
            SaveDirectory = _rig.Save,
            BundleInspection = (_, _) =>
            {
                inspections++;
                return reject ? new(MacBundleInspection.State.Rejected, 0, "source changed after the first arm") : new(MacBundleInspection.State.Accepted, 0, "");
            },
            ProcessCheck = (_, _) => Task.CompletedTask,
        };
        try
        {
            runner.Stage("parent");
            int perArm = inspections;
            Assert.True(perArm >= 1);
            reject = true;
            Assert.Contains("notarized", Assert.Throws<InvalidOperationException>(() => runner.Stage("candidate")).Message);
            Assert.Equal(perArm + 1, inspections); // refused at its first look, before the candidate is staged
        }
        finally { runner.Remove(); }
        Assert.False(Directory.Exists(runner.Install));
    }

    [ClientHostFact] public void ATargetedRunValidatesItsExpectedErrorLinesBeforeStaging()
    {
        var manifest = _rig.Manifest();
        manifest.LogScan[LogScanner.UnknownError] = new()
        {
            Expected = ["[Error  :   BepInEx] Unable to start Unity log writer"],
            Reason = "Known environmental log-writer failure on the test client.",
        };
        _rig.Regression(manifest).Preflight();
        manifest.LogScan[LogScanner.UnknownError].Expected = ["Unable to start Unity log writer"];
        Assert.Contains("exact expected BepInEx Error or Fatal header", Assert.Throws<ArgumentException>(() => _rig.Regression(manifest)).Message);
    }

    [ClientHostFact] public async Task DirectRegressionHoldsTheHostLockUntilItsCopyIsRetired()
    {
        var inventory = _rig.Inventory();
        var runner = new TargetedRegression(_rig.Manifest(), inventory: inventory)
        {
            SaveDirectory = _rig.Save,
            BundleInspection = (_, _) => new(MacBundleInspection.State.Accepted, 0, "synthetic test install"),
            ProcessCheck = (_, _) => Task.CompletedTask,
            PinnedLoader = (_, _) => null,
        };
        string lockPath = inventory.Hosts["local"].Lock;
        try
        {
            runner.Stage("parent");
            Assert.True(File.Exists(Path.Combine(lockPath, "owner")));
            var host = new LocalGameHost("test", HostShell.ThisMachine);
            Assert.Equal(HostLockState.HeldByOther, (await host.CheckLockAsync(lockPath, "a different run", TimeSpan.FromSeconds(10))).State);
        }
        finally { runner.Remove(); }
        Assert.False(File.Exists(Path.Combine(lockPath, "owner")));
    }

    [ClientHostFact] public async Task SourceIntegrityFailureRetiresTheProfileAndReleasesTheHostLockBeforeItIsRaised()
    {
        var inventory = _rig.Inventory();
        var runner = new TargetedRegression(_rig.Manifest(), inventory: inventory)
        {
            SaveDirectory = _rig.Save,
            BundleInspection = (_, _) => new(MacBundleInspection.State.Accepted, 0, "synthetic test install"),
            ProcessCheck = (_, _) => Task.CompletedTask,
            PinnedLoader = (_, _) => null,
        };
        string lockPath = inventory.Hosts["local"].Lock;
        runner.Stage("parent");
        File.AppendAllText(Directory.EnumerateFiles(_rig.Game, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First(), "changed during the run");

        var error = Assert.Throws<IOException>(runner.Remove);

        Assert.Contains("Source integrity failed", error.Message);
        Assert.False(Directory.Exists(runner.Install));
        Assert.False(File.Exists(Path.Combine(lockPath, "owner")));
        var host = new LocalGameHost("test", HostShell.ThisMachine);
        Assert.Equal(HostLockState.Free, (await host.CheckLockAsync(lockPath, "another run", TimeSpan.FromSeconds(10))).State);
    }

    [ClientHostFact] public void SourceIntegrityAndLockReleaseFailuresAreBothReported()
    {
        var inventory = _rig.Inventory();
        var runner = new TargetedRegression(_rig.Manifest(), inventory: inventory)
        {
            SaveDirectory = _rig.Save,
            BundleInspection = (_, _) => new(MacBundleInspection.State.Accepted, 0, "synthetic test install"),
            ProcessCheck = (_, _) => Task.CompletedTask,
            PinnedLoader = (_, _) => null,
        };
        string lockPath = inventory.Hosts["local"].Lock;
        runner.Stage("parent");
        File.AppendAllText(Directory.EnumerateFiles(_rig.Game, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First(), "changed during the run");
        File.WriteAllText(Path.Combine(lockPath, "owner"), "another run");

        var error = Assert.Throws<AggregateException>(runner.Remove);

        Assert.Contains(error.InnerExceptions, failure => failure.Message.Contains("Source integrity failed", StringComparison.Ordinal));
        Assert.Contains(error.InnerExceptions, failure => failure is HostLockException);
        Assert.False(Directory.Exists(runner.Install));
    }

    [ClientHostFact] public void AValidManifestStagesOnlyTheAllowlistThenPreflightsEveryArm()
    {
        var regression = _rig.Regression(_rig.Manifest(), ["example.probe/read"]);
        var parent = regression.Stage("parent");
        string plugins = Path.Combine(_rig.Install, "BepInEx", "plugins");
        Assert.Equal(new[] { "Dependency.dll", "ExampleMod.dll", "Probe.dll", "valheimCLI.dll" },
            Directory.EnumerateFiles(plugins).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(plugins, "Unrelated.dll"))); // The prepared game's own plugins are never copied.
        Assert.True(File.Exists(Path.Combine(_rig.Game, "BepInEx", "plugins", "Unrelated.dll"))); // ...nor touched.
        Assert.Contains("AllowOnServerClients = true", File.ReadAllText(Path.Combine(_rig.Install, "BepInEx", "config", "valheimCLI.valheimCLI.cfg")));
        // Every staged plugin is pinned under its declared GUID by MD5, and nothing else: no absent pins for other mods.
        Assert.Equal(new[] { "example.dependency", "example.mod", "testing.probe", "valheimCLI.valheimCLI" }, parent.Plan.Pins.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("absent", parent.Plan.Pins.Values);
        Assert.Equal(FileHash.Md5(_rig.Parent), parent.Plan.Pins["example.mod"]);
        Assert.Equal(_rig.Uid, parent.Plan.HostWorld!.WorldUid);
        Assert.Equal(CliCapabilities.HostedRounds, parent.Manifest.Capabilities);
        Assert.Equal(new[] { "example.probe/read" }, parent.Manifest.LiveOnlyCapabilities); // A probe's command: live only.
        Assert.StartsWith("valheimCLI test build: BepInEx/plugins/valheimCLI.dll", parent.Manifest.CliManifest); // The static check always runs (#296).
        // Each arm under its own artifact name, with its commit and hash, for review.
        Assert.Equal(new[] { "parent-ExampleMod.dll", "candidate-ExampleMod.dll" }, parent.Manifest.Arms.Select(arm => arm.Artifact));
        Assert.Equal(new[] { "p1", "c1" }, parent.Manifest.Arms.Select(arm => arm.Commit));
        Assert.Contains(parent.Describe(), line => line.Contains("arm candidate") && line.Contains(FileHash.Sha256(_rig.Candidate)));
        // The run manifest holds the allowlist and hashes only: no machine path, nothing outside the allowlist.
        string json = System.Text.Json.JsonSerializer.Serialize(parent.Manifest, TargetedRegression.ManifestJson);
        Assert.DoesNotContain(_rig.Root, json);
        Assert.DoesNotContain(Path.GetFileName(_rig.Root), json);
        Assert.DoesNotContain("Unrelated", json);
        parent.Verify();

        var arms = regression.Preflight();
        Assert.Equal(new[] { "parent", "candidate" }, arms.Select(arm => arm.Arm));
        Assert.Equal(FileHash.Md5(_rig.Candidate), arms[1].Plan.Pins["example.mod"]);
        Assert.Equal(FileHash.Md5(_rig.Candidate), FileHash.Md5(Path.Combine(plugins, "ExampleMod.dll"))); // Only the mod DLL changed.
        Assert.Equal(arms[0].Plan.Pins.Where(pin => pin.Key != "example.mod"), arms[1].Plan.Pins.Where(pin => pin.Key != "example.mod"));
    }

    [ClientHostFact] public void ARegressionCarriesOnlyItsDeclaredClientVariablesIntoTheOwnedPlan()
    {
        var inputs = _rig.Manifest();
        inputs.Client.Environment["PROCEDURALROADS_GENERATE_ROADS_ON_LOAD"] = "0";
        var staged = _rig.Regression(inputs).Stage("parent");
        Assert.Equal("0", staged.Plan.Environment["PROCEDURALROADS_GENERATE_ROADS_ON_LOAD"]);
        Assert.DoesNotContain("PROCEDURALROADS_GENERATE_ROADS_ON_LOAD", staged.Plan.LaunchArguments);
    }

    [Theory]
    [InlineData("DOORSTOP_ENABLED")]
    [InlineData("doorstop_target_assembly")]
    [InlineData("bad-name")]
    public void ARegressionRefusesInvalidOrLoaderClientVariablesBeforeStaging(string name)
    {
        var inputs = _rig.Manifest();
        inputs.Client.Environment[name] = "1";
        Assert.Throws<ArgumentException>(() => _rig.Regression(inputs));
        Assert.False(Directory.Exists(_rig.Install));
    }

    // ---- the fixture: nesting and identity ----

    [ClientHostTheory]
    [InlineData("world-folder", "is a world folder itself", "Point fixture.root at its parent")]
    [InlineData("worlds_local", "holds a worlds_local/ wrapper", "Copy only the one world folder")]
    [InlineData("two-worlds", "exactly one world, not 2: Other/, SealFixture/", "Copy only the world under test")]
    public void AWronglyNestedFixtureIsRefusedWithTheExpectedTree(string shape, string diagnosis, string action)
    {
        string root = shape switch
        {
            "world-folder" => Path.Combine(_rig.Fixture, "SealFixture"),
            "worlds_local" => _rig.Wrap("worlds_local"),
            _ => _rig.AddWorld("Other", 77),
        };
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(_rig.Manifest(fixture: root)).Stage("parent"));
        Assert.Contains(diagnosis, error.Message);
        Assert.Contains(action, error.Message);
        Assert.Contains("Expected:\n<fixture root>/\n  <WorldName>/", error.Message);
        Assert.Contains("_main.<n>.fwl2", error.Message);
        Assert.Contains("Found:\n", error.Message);
        Assert.False(Directory.Exists(_rig.Install)); // Refused before the install is created.
    }

    [ClientHostFact] public void AFixtureHoldingAnotherUidIsRefusedNamingTheUidItHolds()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(_rig.Manifest(worldUid: "450017353")).Stage("parent"));
        Assert.Contains("hostWorld.worldUid is 450017353", error.Message);
        Assert.Contains($"holds world SealFixture with UID {_rig.Uid}", error.Message);
        Assert.Contains("Pin the UID of this exact fixture", error.Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    // ---- dependencies: declared by the manifest and by the mod's own metadata ----

    [ClientHostFact] public void AnOmittedDependencyTheModDeclaresIsRefusedNamingThePluginToStage()
    {
        var manifest = _rig.Manifest();
        manifest.Plugins.Clear(); // The JsonDotNET detector shape: the mod's [BepInDependency] is met by nothing staged.
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains("example.mod (BepInEx/plugins/ExampleMod.dll) has a hard [BepInDependency(\"example.dependency\", \"1.2.0\")] that nothing staged declares", error.Message);
        Assert.Contains("Add the DLL that declares [BepInPlugin(\"example.dependency\")] to plugins in the manifest", error.Message);
    }

    [ClientHostFact] public void ADependencyIsMetByWhatADllDeclaresNeverByItsFileName()
    {
        // A file named like the dependency that declares no plugin does not meet it.
        string impostor = _rig.Write("impostor/Dependency.dll", RegressionRig.Assembly("Dependency", null));
        var manifest = _rig.Manifest();
        manifest.Plugins = [new() { File = impostor, Sha256 = FileHash.Sha256(impostor) }];
        var regression = _rig.Regression(manifest);
        Assert.Contains("that nothing staged declares", Assert.Throws<InvalidOperationException>(() => regression.Stage("parent")).Message);
        // A renamed file that declares it does.
        string renamed = _rig.Write("renamed/SomethingElse.dll", RegressionRig.Assembly("Dependency", new("example.dependency", "1.3.0")));
        manifest.Plugins = [new() { File = renamed, Sha256 = FileHash.Sha256(renamed) }];
        regression.Stage("parent");
        // An older build than the mod requires is named with both versions.
        string old = _rig.Write("old/Dependency.dll", RegressionRig.Assembly("Dependency", new("example.dependency", "1.1.0")));
        manifest.Plugins = [new() { File = old, Sha256 = FileHash.Sha256(old) }];
        var error = Assert.Throws<InvalidOperationException>(() => regression.Stage("parent"));
        Assert.Contains("needs example.dependency 1.2.0 or newer, and the staged BepInEx/plugins/Dependency.dll declares 1.1.0", error.Message);
    }

    [ClientHostFact] public void AnAssemblyNothingStagedProvidesIsRefusedUnlessDeclaredOptional()
    {
        string uses = _rig.Write("uses/Library.dll", RegressionRig.Assembly("UsesLibrary", null, reference: typeof(FactAttribute)));
        var manifest = _rig.Manifest();
        manifest.Plugins.Add(new() { File = uses, Sha256 = FileHash.Sha256(uses) });
        var regression = _rig.Regression(manifest);
        var error = Assert.Throws<InvalidOperationException>(() => regression.Stage("parent"));
        Assert.Contains("BepInEx/plugins/Library.dll references assembly xunit.core", error.Message);
        Assert.Contains("optionalReferences", error.Message);
        // Staging the library that provides it, by its assembly name, meets it.
        string library = _rig.Write("lib/xunit.core.dll", RegressionRig.Assembly("xunit.core", null));
        manifest.Plugins.Add(new() { File = library, Sha256 = FileHash.Sha256(library) });
        regression.Stage("parent");
        manifest.Plugins.RemoveAt(manifest.Plugins.Count - 1);
        manifest.OptionalReferences = ["xunit.core"];
        regression.Stage("parent");
    }

    [ClientHostFact] public void IncompatibleServerOnlyAndDuplicatePluginsAreRefused()
    {
        var manifest = _rig.Manifest();
        string server = _rig.Write("server/ServerOnly.dll", RegressionRig.Assembly("ServerOnly", new("example.serveronly") { Processes = ["valheim_server.exe"] }));
        string clash = _rig.Write("clash/Clash.dll", RegressionRig.Assembly("Clash", new("example.clash") { Incompatible = ["example.mod"] }));
        string twin = _rig.Write("twin/Twin.dll", RegressionRig.Assembly("Twin", new("example.dependency", "2.0.0")));
        foreach (string file in new[] { server, clash, twin }) manifest.Plugins.Add(new() { File = file, Sha256 = FileHash.Sha256(file) });
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains("example.serveronly (BepInEx/plugins/ServerOnly.dll) loads only in valheim_server.exe ([BepInProcess])", error.Message);
        Assert.Contains("example.clash (BepInEx/plugins/Clash.dll) declares [BepInIncompatibility(\"example.mod\")]", error.Message);
        Assert.Contains("example.dependency is declared by both BepInEx/plugins/Dependency.dll and BepInEx/plugins/Twin.dll", error.Message);
    }

    // ---- the allowlist and the disposable install ----

    [ClientHostFact] public void AnExtraPluginOutsideTheAllowlistIsRefusedBeforeTheLaunch()
    {
        var regression = _rig.Regression(_rig.Manifest());
        var staged = regression.Stage("parent");
        File.WriteAllText(Path.Combine(_rig.Install, "BepInEx", "plugins", "Extra.dll"), "a plugin the test would never call");
        var error = Assert.Throws<InvalidOperationException>(staged.Verify);
        Assert.Contains("BepInEx/plugins/Extra.dll is not in the allowlist", error.Message);
        Assert.Contains("Declare it in the manifest (plugins, probe or configs) and stage again", error.Message);
        File.WriteAllText(Path.Combine(_rig.Install, "BepInEx", "plugins", "Dependency.dll"), "changed");
        Assert.Contains("BepInEx/plugins/Dependency.dll changed after staging", Assert.Throws<InvalidOperationException>(staged.Verify).Message);
        // Staging again rebuilds the folder from the allowlist.
        regression.Stage("parent").Verify();
        Assert.False(File.Exists(Path.Combine(_rig.Install, "BepInEx", "plugins", "Extra.dll")));
    }

    [ClientHostFact] public void AnInstallThisToolDidNotCreateIsNeverChanged()
    {
        Directory.CreateDirectory(Path.Combine(_rig.Install, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(_rig.Install, "BepInEx", "plugins", "Valued.dll"), "someone's install");
        var error = Assert.Throws<IOException>(() => _rig.Regression(_rig.Manifest()).Stage("parent"));
        Assert.Contains("disposable install already exists", error.Message);
        Assert.Throws<InvalidOperationException>(() => _rig.Regression(_rig.Manifest()).Remove());
        Assert.True(File.Exists(Path.Combine(_rig.Install, "BepInEx", "plugins", "Valued.dll")));
        Directory.Delete(_rig.Install, recursive: true);
        var regression = _rig.Regression(_rig.Manifest());
        regression.Stage("parent");
        regression.Remove();
        Assert.False(Directory.Exists(_rig.Install));
        // The prepared game itself can never hold the disposable install: a client environment whose runtime is inside it is refused.
        _rig.Runtime = Path.Combine(_rig.Game, "runs");
        Assert.Contains("overlap", Assert.Throws<ArgumentException>(() => _rig.Regression(_rig.Manifest())).Message);
    }

    [ClientHostFact] public void RemovingTheInstallIsTheLastArmsCleanupStep()
    {
        var regression = _rig.Regression(_rig.Manifest());
        regression.Stage("parent");
        string evidence = Path.Combine(_rig.Root, "evidence-last");
        var report = new ScenarioReport("last arm");
        report.Step("the scenario", () => { });
        regression.Remove(report, evidence);
        Assert.False(Directory.Exists(_rig.Install));
        var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "result.json"))).RootElement;
        Assert.Equal("removed", result.GetProperty("Provenance").GetProperty("disposableInstall").GetString());
        Assert.True(result.GetProperty("CleanupVerified").GetBoolean());
        // An install this tool did not create is refused, recorded as the failed cleanup, and kept.
        Directory.CreateDirectory(_rig.Install);
        var refused = new ScenarioReport("last arm");
        refused.Step("the scenario", () => { });
        Assert.Throws<InvalidOperationException>(() => _rig.Regression(_rig.Manifest()).Remove(refused, evidence));
        Assert.True(Directory.Exists(_rig.Install));
        Assert.Equal((true, false), (refused.ScenarioPassed, refused.CleanupVerified));
        Assert.StartsWith("kept, removal refused", refused.Provenance["disposableInstall"]);
        Directory.Delete(_rig.Install);
    }

    [ClientHostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACompletedArmRetainsItsProfileUntilTheConsumerRecordsRetirement(bool scenarioFails)
    {
        var (host, scope) = InProcess(); using var _ = scope;
        var regression = _rig.Regression(_rig.Manifest(), skipHostLock: false);
        string output = Path.Combine(_rig.Root, "consumer-arm");
        var report = RunConsumer(regression, output, round => round.Step("the mod behavior", () =>
        {
            if (scenarioFails) throw new InvalidDataException("deliberate behavior failure");
        }));

        Assert.True(report.ScenarioPassed == !scenarioFails, string.Join("; ", report.Steps.Where(step => !step.Passed).Select(step => step.Name + ": " + step.Error)));
        Assert.Contains(report.Steps, step => step.Name.EndsWith("the mod behavior", StringComparison.Ordinal) && step.Passed == !scenarioFails);
        Assert.True(Directory.Exists(regression.Install));
        Assert.NotEmpty(host.Claims.Except(host.Releases));
        regression.Remove(report, output);
        Assert.Equal(!scenarioFails, report.Passed);
        Assert.True(report.CleanupVerified);
        Assert.Equal("removed", report.Provenance["disposableInstall"]);
        Assert.False(Directory.Exists(regression.Install));
        Assert.Empty(host.Claims.Except(host.Releases));
        int retired = host.Runs.Count(run => run.Script == "cleanup-stage");
        regression.Remove();
        Assert.Equal(retired, host.Runs.Count(run => run.Script == "cleanup-stage"));
    }

    [ClientHostFact] public void AReportWriteFailureStillRetiresTheProfileAndReleasesTheLock()
    {
        var (host, scope) = InProcess(); using var _ = scope;
        var regression = _rig.Regression(_rig.Manifest(), skipHostLock: false);
        string output = Path.Combine(_rig.Root, "blocked-report");
        var error = Record.Exception(() => RunConsumer(regression, output, _ => { },
            () => Directory.CreateDirectory(Path.Combine(output, "result.json"))));

        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.Contains("result.json", error.Message);
        Assert.False(Directory.Exists(regression.Install));
        Assert.Empty(host.Claims.Except(host.Releases));
        regression.Remove();
    }

    [ClientHostFact] public void ReportWriteAndRetirementFailuresAreBothKeptWithoutAnAutomaticRetry()
    {
        var (host, scope) = InProcess(); using var _ = scope;
        var regression = _rig.Regression(_rig.Manifest(), skipHostLock: false);
        string output = Path.Combine(_rig.Root, "blocked-report-and-cleanup");
        var error = Assert.Throws<AggregateException>(() => RunConsumer(regression, output, _ => { }, () =>
        {
            Directory.CreateDirectory(Path.Combine(output, "result.json"));
            host.Failures["cleanup-stage"] = FakeServerHost.TransportFailure;
        }));

        Assert.Contains(error.InnerExceptions, failure => failure.Message.Contains("result.json", StringComparison.Ordinal));
        Assert.Contains(error.InnerExceptions, failure => failure.Message.Contains("Connection refused", StringComparison.Ordinal));
        Assert.Single(host.Runs.Where(run => run.Script == "cleanup-stage"));
        Assert.True(Directory.Exists(regression.Install));
        Assert.NotEmpty(host.Claims.Except(host.Releases));
        // An explicit later cleanup can proceed after the injected fault is resolved; the failed attempt kept ownership.
        host.Failures.Clear();
        regression.Remove();
        Assert.False(Directory.Exists(regression.Install));
        Assert.Empty(host.Claims.Except(host.Releases));
    }

    [ClientHostFact] public void PreflightAndRetirementFailuresKeepTheOriginalRefusalAndAttemptCleanupOnce()
    {
        var (host, scope) = InProcess(); using var _ = scope;
        var inputs = _rig.Manifest();
        inputs.Client.Character = "missing-character";
        var regression = _rig.Regression(inputs, skipHostLock: false);
        host.Failures["cleanup-stage"] = FakeServerHost.TransportFailure;

        var error = Assert.Throws<AggregateException>(() => regression.Preflight());

        Assert.Contains(error.InnerExceptions, failure => failure.Message.Contains("missing-character", StringComparison.Ordinal));
        Assert.Contains(error.InnerExceptions, failure => failure.Message.Contains("Connection refused", StringComparison.Ordinal));
        Assert.Single(host.Runs.Where(run => run.Script == "cleanup-stage"));
        Assert.True(Directory.Exists(regression.Install));
        Assert.NotEmpty(host.Claims.Except(host.Releases));
        host.Failures.Clear();
        regression.Remove();
    }

    [ClientHostFact] public void AFailedRetirementWhileReplacingAnArmIsNotRetriedByTheStageFailureGuard()
    {
        var (host, scope) = InProcess(); using var _ = scope;
        var regression = _rig.Regression(_rig.Manifest(), skipHostLock: false);
        regression.Stage("parent");
        host.Failures["cleanup-stage"] = FakeServerHost.TransportFailure;

        var error = Assert.Throws<HostOperationException>(() => regression.Stage("candidate"));

        Assert.Contains("Connection refused", error.Message);
        Assert.Single(host.Runs.Where(run => run.Script == "cleanup-stage"));
        Assert.True(Directory.Exists(regression.Install));
        Assert.NotEmpty(host.Claims.Except(host.Releases));
        host.Failures.Clear();
        regression.Remove();
        Assert.False(Directory.Exists(regression.Install));
        Assert.Empty(host.Claims.Except(host.Releases));
    }

    [ClientHostFact] public void SuccessfulReplacementDoesNotSuppressCleanupOfTheNewFailedStage()
    {
        var (host, scope) = InProcess(); using var _ = scope;
        var inputs = _rig.Manifest();
        var regression = _rig.Regression(inputs, skipHostLock: false);
        regression.Stage("parent");
        inputs.Client.Character = "missing-after-replacement";

        var error = Assert.Throws<InvalidOperationException>(() => regression.Stage("candidate"));

        Assert.Contains("missing-after-replacement", error.Message);
        Assert.Equal(2, host.Runs.Count(run => run.Script == "cleanup-stage"));
        Assert.False(Directory.Exists(regression.Install));
        Assert.Empty(host.Claims.Except(host.Releases));
    }

    [ClientHostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementIntegrityFailureReleasesTheRetiredProfilesLockOrPreservesItsReleaseFailure(bool releaseFails)
    {
        var (host, scope) = InProcess(); using var _ = scope;
        string source = Path.Combine(_rig.Game, "notice.txt");
        File.WriteAllText(source, "source baseline");
        var regression = _rig.Regression(_rig.Manifest(), skipHostLock: false);
        regression.Stage("parent");
        File.AppendAllText(source, " changed during the arm");
        if (releaseFails)
            host.ReleaseLockResults.Enqueue(new HostLockResult(HostLockState.Unknown, null, "deliberate lock-release failure"));

        var error = Record.Exception(() => regression.Stage("candidate"));

        Assert.NotNull(error);
        Assert.Contains("Source integrity failed", error.ToString());
        Assert.Single(host.Runs.Where(run => run.Script == "cleanup-stage"));
        Assert.False(Directory.Exists(regression.Install));
        if (releaseFails)
        {
            Assert.IsType<AggregateException>(error);
            Assert.Contains("deliberate lock-release failure", error.ToString());
            Assert.NotEmpty(host.Claims.Except(host.Releases));
            Assert.Empty(host.Releases); // The operation did not retry the failed release.
            regression.Remove();
        }
        else Assert.IsType<IOException>(error);
        Assert.Empty(host.Claims.Except(host.Releases));
    }

    [ClientHostFact] public void TheReportingRemovePreservesCleanupAndWriteFailuresTogether()
    {
        var (host, scope) = InProcess(); using var _ = scope;
        var regression = _rig.Regression(_rig.Manifest(), skipHostLock: false);
        regression.Stage("parent");
        string output = Path.Combine(_rig.Root, "reporting-remove");
        Directory.CreateDirectory(Path.Combine(output, "result.json"));
        host.Failures["cleanup-stage"] = FakeServerHost.TransportFailure;
        var report = new ScenarioReport("last arm");
        report.Step("the behavior", () => { });

        var error = Assert.Throws<AggregateException>(() => regression.Remove(report, output));

        Assert.Contains(error.InnerExceptions, failure => failure.Message.Contains("Connection refused", StringComparison.Ordinal));
        Assert.Contains(error.InnerExceptions, failure => failure.Message.Contains("result.json", StringComparison.Ordinal));
        Assert.False(report.CleanupVerified);
        Assert.StartsWith("kept, removal refused", report.Provenance["disposableInstall"]);
        Assert.Single(host.Runs.Where(run => run.Script == "cleanup-stage"));
        Assert.True(Directory.Exists(regression.Install));
        host.Failures.Clear();
        regression.Remove();
    }

    private ScenarioReport RunConsumer(TargetedRegression regression, string output, Action<ClientRound> measure, Action? afterStaged = null)
    {
        using var data = new FakeClientDataDirectory(_rig.Save);
        return regression.Run("parent", output, "consumer", ["first"], measure, default, null,
            (plan, directory, _, _) => ClientSession.Launch(plan, directory, () => new LocalFakeOwnedProcess(93), HostingClient,
                (_, _) => Task.CompletedTask), afterStaged);
    }

    [Fact] public void AnArmsCommitIsOptionalButNeverBlank()
    {
        var arm = new RegressionArm { File = Path.GetFullPath("/mods/My.dll"), Sha256 = new string('a', 64) };
        arm.Validate("mod.arms.parent"); // Not known: left out, never replaced by a stand-in.
        arm.Commit = " ";
        Assert.Contains("leave it out when it is not known", Assert.Throws<ArgumentException>(() => arm.Validate("mod.arms.parent")).Message);
    }

    // Found on a Windows station: a disposable install reused across arms kept a mod manager's old Doorstop proxy. Every arm
    // stages a fresh profile, so nothing an earlier arm left in it survives, and a mod manager's Doorstop marker in the game
    // (not a loader file) never reaches a profile at all.
    [ClientHostFact] public void EachArmGetsAFreshProfile()
    {
        _rig.Write("game/.doorstop_version", Encoding.UTF8.GetBytes("4.4.0"));
        var regression = _rig.Regression(_rig.Manifest());
        regression.Stage("parent");
        Assert.False(File.Exists(Path.Combine(_rig.Install, ".doorstop_version")));
        string earlier = Path.Combine(_rig.Install, "from-the-first-profile.txt");
        File.WriteAllText(earlier, "only in the disposable profile");
        regression.Stage("candidate").Verify();
        Assert.False(File.Exists(earlier));
        string later = Path.Combine(_rig.Install, "from-the-second-profile.txt");
        File.WriteAllText(later, "only in the disposable profile");
        regression.Stage("candidate").Verify();
        Assert.False(File.Exists(later));
        regression.Remove();
    }

    [ClientHostFact] public void ADefaultArmUsesAnOwnedProfileWithoutChangingTheSourceInstall()
    {
        if (OperatingSystem.IsWindows())
            _rig.Write("game/version.dll", Encoding.UTF8.GetBytes("another mod manager's proxy"));
        var before = Directory.GetFiles(_rig.Game, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_rig.Game, path), FileHash.Sha256, StringComparer.Ordinal);
        var regression = _rig.Regression(_rig.Manifest());

        var arm = regression.Stage("parent");
        Assert.Equal("profile", arm.Plan.PreparedLaunchMode);
        Assert.Equal(OperatingSystem.IsWindows() ? regression.Install : _rig.Game, arm.Plan.Install);
        Assert.True(File.Exists(Path.Combine(regression.Install, "BepInEx", "plugins", "ExampleMod.dll")));
        if (OperatingSystem.IsWindows()) Assert.False(File.Exists(Path.Combine(regression.Install, "version.dll")));
        arm.Verify();
        regression.Remove();

        Assert.False(Directory.Exists(regression.Install));
        var after = Directory.GetFiles(_rig.Game, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_rig.Game, path), FileHash.Sha256, StringComparer.Ordinal);
        Assert.Equal(before.OrderBy(file => file.Key), after.OrderBy(file => file.Key));
    }

    [Fact] public void AReplacedWindowsSourceGameIsRefusedBeforeTheLinkedProfileLaunches()
    {
        if (!OperatingSystem.IsWindows()) return;
        var inventory = _rig.Inventory();
        var regression = new TargetedRegression(_rig.Manifest(), inventory: inventory)
        {
            SaveDirectory = _rig.Save,
            BundleInspection = (_, _) => new(MacBundleInspection.State.Accepted, 0, "synthetic test install"),
            ProcessCheck = (_, _) => Task.CompletedTask,
            PinnedLoader = (_, _) => null,
        };
        string lockPath = inventory.Hosts["local"].Lock;
        var arm = regression.Stage("parent");
        string sourceAssembly = InstallPins.GameAssembly(_rig.Game);
        string replacement = sourceAssembly + ".replacement";
        try
        {
            File.WriteAllBytes(replacement, Encoding.UTF8.GetBytes("a Steam update replaced the original file"));
            File.Move(replacement, sourceAssembly, overwrite: true);
            // The launch folder still has the old hard-linked file; checking it alone would pass.
            arm.Plan.InstallPins!.Check(arm.Plan.Install, arm.Plan.LoaderRoot, "linked profile");
            var beforeLaunch = Assert.Throws<InvalidOperationException>(arm.Plan.CheckInstallPins);
            Assert.Contains("source game before profile launch", beforeLaunch.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("assembly_valheim.dll", beforeLaunch.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(replacement)) File.Delete(replacement);
            var retirement = Assert.Throws<IOException>(regression.Remove);
            Assert.Contains("assembly_valheim.dll", retirement.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(lockPath, "owner")));
        }
    }

    [ClientHostFact] public void AMissingCharacterIsRefusedBeforeTheLaunch()
    {
        var manifest = _rig.Manifest();
        manifest.Client.Character = "nobody";
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains("nobody.fch is not in", error.Message);
        Assert.Contains("Stage the disposable local character", error.Message);
    }

    [ClientHostFact] public void ARegisteredCharacterCanBePreflightedWithoutTouchingTheLiveCharacterFolder()
    {
        string local = Path.Combine(_rig.Save, "characters_local");
        string source = Path.Combine(local, "smoketest.fch");
        File.WriteAllBytes(source, CharacterSaveReaderTests.Profile(playerId: 917).File);
        string storePath = Path.Combine(_rig.Root, "registered-test-characters");
        DisposableCharacterStore.Create(storePath).Register("smoketest", source);
        File.Delete(source);
        string steam = Path.Combine(_rig.Root, "userdata");
        Directory.CreateDirectory(steam);

        var manifest = _rig.Manifest();
        manifest.Client.CharacterStore = storePath;
        _rig.SteamUserData = steam;
        _rig.Regression(manifest).Preflight();
        Assert.Empty(Directory.EnumerateFiles(local)); // Preflight must not put a character into the game folder.
    }

    [Fact] public void ADirectConsumersCharacterUsesTheSameRecoverableEntriesAsStart()
    {
        const string characters = "/characters", userdata = "/userdata", name = "smoketest", hash = "1234";
        var intended = TargetedRegression.CharacterEntry(CharacterStageEvent.Intended, characters, userdata, name, hash);
        Assert.Equal(JournalEntry.CharacterIntended, intended.Kind);
        Assert.Equal("regression", intended.Fields["characterKind"]);
        Assert.Equal("true", intended.Fields["local"]);
        Assert.Equal(hash, intended.Fields["expectedSha256"]);
        Assert.Equal(JournalEntry.CharacterDone,
            TargetedRegression.CharacterEntry(CharacterStageEvent.Done, characters, userdata, name, hash).Kind);
        Assert.Equal(JournalEntry.CharacterRetired,
            TargetedRegression.CharacterEntry(CharacterStageEvent.Retired, characters, userdata, name, hash).Kind);
    }

    [Fact]
    public void OneShotCanDeferItsResultUntilAfterItsOwnCleanupVerdict()
    {
        string local = Path.Combine(_rig.Save, "characters_local");
        string source = Path.Combine(local, "smoketest.fch");
        File.WriteAllBytes(source, CharacterSaveReaderTests.Profile(playerId: 917).File);
        string storePath = Path.Combine(_rig.Root, "registered-test-characters");
        DisposableCharacterStore.Create(storePath).Register("smoketest", source);
        var manifest = _rig.Manifest();
        manifest.Client.CharacterStore = storePath;
        var regression = _rig.Regression(manifest);
        string output = Path.Combine(_rig.Root, "deferred-result");

        var report = regression.Run("parent", output, "smoke", ["first"], _ => { }, default, null,
            (_, _, _, _) => throw new Exception("client must not launch"), afterStaged: null,
            deferReportWrite: true);

        Assert.False(report.Passed); // missing Steam userdata stopped setup before any client launched
        Assert.False(File.Exists(Path.Combine(output, "result.json")));
        report.RecordFailure(StepPhase.Cleanup, "owned cleanup not verified", new IOException("still held"));
        report.Write(output);
        Assert.Contains("owned cleanup not verified", File.ReadAllText(Path.Combine(output, "result.json")));
        Assert.False(report.CleanupVerified);
    }

    // ---- start's hosted fixture world (#818, #820) ----
    // Through start's own seam: TargetedRegression.Run with its hosted ClientRounds, a fake hosting client (no game), this
    // machine's real host scripts (or, where a test answers it in process, InProcess) and the run journal on disk, then env status
    // and env recover as the command runs them.

    private const string FixtureWorld = "SealFixture";
    private string Worlds => Path.Combine(_rig.Save, "worlds_local");
    private Dictionary<string, HostProfile> Hosts() => new() { ["local"] = new HostProfile { Kind = "local", Lock = Path.Combine(_rig.Root, "lock") } };
    // This machine as the run reaches it: its shell, or in a test that answers it in process (InProcess) that test's host.
    private static IGameHost Here => LocalGameHost.ThisMachine();
    // For the tests of what a start decides (its verdicts, steps, journal and what it leaves): this machine answered in process for
    // the test's flow, so no step starts a shell. SourceScriptShellTests compares the fake's settlement answers with the real scripts
    // and names where the other scripts' real runs are.
    private static (FakeServerHost Host, IDisposable Scope) InProcess()
    {
        var host = FakeServerHost.ThisMachine();
        return (host, LocalGameHost.UseThisMachineForTest(host));
    }

    // A hosting client at its menu until it starts a world, then the world's UID once loaded and its player after.
    private ScriptedTransport HostingClient()
    {
        bool hosting = false; int readings = 0, save = 5;
        return new ScriptedTransport()
            .ClientAccess(() => hosting, () => hosting)
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'smoketest' (smoketest, Local)"))
            .OnPrefix("cli_start_host_world ", command =>
            {
                hosting = true; readings = 0;
                return ScriptedTransport.Ok($"OK: Starting hosted world '{command.Split(' ')[1]}' using character 'smoketest' (smoketest, Local); open=true, public=False, crossplay=False, backend=Steamworks, passwordSet=False");
            })
            .Extension("valheim.session", "state", _ =>
            {
                int reading = hosting ? ++readings : 0;
                bool present = hosting && reading > 1, player = hosting && reading > 2;
                return new
                {
                    source = "session-state", complete = true, phase = present ? "world-present" : hosting ? "loading" : "menu", worldUid = present ? _rig.Uid : null,
                    worldPresent = present, worldReady = present, server = present, dedicated = false, localPlayer = player, playerReady = player,
                    saving = false, loadError = false, connectionStatus = present ? "Connected" : "None",
                };
            })
            .Extension("valheim.session", "save", _ => new { source = "session-save", complete = true, worldUid = _rig.Uid, saved = true, before = save, after = ++save, milliseconds = 10 }, readOnly: false)
            .Extension("valheim.session", "leave", _ => { hosting = false; return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True ghostReplicated=True"));
    }

    // One start run (a new run ID, as the command allocates): each arm in turn on one regression, each in its own evidence folder
    // with its own client process, as the command runs them; the last arm's report and folder, and the run ID.
    private (ScenarioReport Report, string Output, string RunId) Start(Func<LocalFakeOwnedProcess> process, Action<ClientRound>? measure = null,
        bool end = true, string[]? arms = null, Action? beforeClientLaunch = null)
    {
        string runId = RunJournal.NewRunId();
        using var run = RunJournal.UseRun(runId);
        var regression = _rig.Regression(_rig.Manifest());
        ScenarioReport? report = null; string? output = null;
        foreach (string arm in arms ?? ["parent"])
        {
            output = Path.Combine(_rig.Root, "out-" + runId, "evidence", arm);
            var client = process();
            report = regression.Run(arm, output, "smoke", ["first"], measure ?? (_ => { }), default, null,
                (plan, directory, _, _) => ClientSession.Launch(plan, directory, () => { beforeClientLaunch?.Invoke(); return client; }, HostingClient, (_, _) => Task.CompletedTask), afterStaged: null);
            if (!report.Passed) break;
        }
        regression.Remove();
        // As the command ends a run it did not lose (none for a killed one).
        if (end) Assert.True(RunFinish.EndAsync(run, new RunEnd(report!.Passed ? RunOutcome.Passed : RunOutcome.Failed, CleanupProven: true)).GetAwaiter().GetResult());
        return (report!, output!, runId);
    }

    // The runner is killed: its lines now name a process that is gone.
    private void Kill(string runId)
    {
        foreach (string file in Directory.EnumerateFiles(Path.Combine(_rig.Root, "journal", runId), "*.jsonl"))
            File.WriteAllLines(file, File.ReadAllLines(file).Select(line =>
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(line)!;
                node["runner"]!["pid"] = RunJournalStatusTests.Gone.Pid;
                node["runner"]!["startedUtc"] = RunJournalStatusTests.Gone.StartedUtc.ToString("O");
                return node.ToJsonString();
            }));
    }

    private JournalRunStatus Status(string runId) =>
        Assert.Single(RunJournalStatus.InspectAsync(Hosts(), _ => Here, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().Runs, run => run.Run == runId);

    private static void RequireKilledStartReachedHold(ScenarioReport report, bool entered, bool markerWritten, bool cancelled)
    {
        string failed = string.Join("; ", report.Steps.Where(step => !step.Passed).Select(step => $"{step.Phase}: {step.Name}: {step.Error}"));
        Assert.True(!report.Passed && entered && markerWritten && cancelled &&
            report.Steps.Any(step => !step.Passed && step.Name.EndsWith(": hold until the runner is killed", StringComparison.Ordinal) && step.Error.Contains("the runner was killed", StringComparison.Ordinal)),
            $"Killed-start recovery was not exercised: hold entered={entered}, marker written={markerWritten}, deliberate cancellation={cancelled}. Failed steps: {failed}");
    }

    private Dictionary<string, byte[]> KilledStartJournal(string runId) => Directory.Exists(Path.Combine(_rig.Root, "journal", runId))
        ? Directory.GetFiles(Path.Combine(_rig.Root, "journal", runId), "*.jsonl").ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes) : new();

    // Same test-local retention pattern as HostedSteamReleaseTests: outside the disposable rig, one failure's report and exact
    // owned journal only. Capture before Kill/recovery changes those lines; never copy a machine's global journal or game tree.
    private string RetainKilledStartFailure(ScenarioReport report, string output, string runId, IReadOnlyDictionary<string, byte[]> journal,
        bool entered, bool markerWritten, bool cancelled)
    {
        string retained = Path.Combine(Path.GetTempPath(), "vt-killed-start-failure-" + Guid.NewGuid().ToString("N"));
        var errors = new List<string>();
        void Capture(string what, Action save)
        {
            try { save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(what + ": " + error.GetType().Name); }
        }
        Capture("create retained directory", () => Directory.CreateDirectory(retained));
        Capture("first report", () => report.Write(retained));
        Capture("create journal directory", () => Directory.CreateDirectory(Path.Combine(retained, "journal")));
        foreach (var (name, bytes) in journal) Capture(name, () => File.WriteAllBytes(Path.Combine(retained, "journal", name), bytes));
        Capture("boundary", () => File.WriteAllText(Path.Combine(retained, "boundary.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            runId, entered, markerWritten, deliberateCancellation = cancelled, output,
            fixtureWorld = Path.Combine(Worlds, FixtureWorld), profile = _rig.Install,
            runtime = _rig.Runtime, journal = Path.Combine(_rig.Root, "journal", runId),
            dotnet = Environment.Version.ToString(), shell = HostShell.ThisMachine.Kind.ToString(), toolkit = report.Toolkit,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
        return retained + (errors.Count == 0 ? "" : " (retention incomplete: " + string.Join(", ", errors) + ")");
    }

    [ClientHostFact]
    public void AnEarlyKilledStartFailureNamesTheUnreachedBoundaryAndRetainsItsFirstReport()
    {
        var (here, scope) = InProcess(); using var machine = scope;
        using var data = new FakeClientDataDirectory(_rig.Save);
        bool entered = false;
        var (failed, output, runId) = Start(() => new LocalFakeOwnedProcess(41), _ => entered = true, end: false,
            beforeClientLaunch: () => throw new IOException("controlled early launch failure before hold"));
        var journal = KilledStartJournal(runId);
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RequireKilledStartReachedHold(failed, entered, false, false));
        Assert.Contains("hold entered=False", error.Message);
        Assert.Contains("controlled early launch failure", error.Message);
        string retained = RetainKilledStartFailure(failed, output, runId, journal, entered, false, false);
        try
        {
            // The report and pre-recovery journal survive removal of the original evidence, as fixture cleanup would remove it.
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            Assert.Contains("controlled early launch failure", File.ReadAllText(Path.Combine(retained, "result.json")));
            Assert.Contains("\"entered\": false", File.ReadAllText(Path.Combine(retained, "boundary.json")));
            foreach (var (name, bytes) in journal) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(retained, "journal", name)));
        }
        finally { Directory.Delete(retained, recursive: true); }
    }

    // #818: a killed `start --hold` left its hosted fixture world in the client's worlds_local, unjournalled, so env status listed
    // nothing, env recover said nothing was left, and the next start refused. Its placement is now the campaign's journalled site:
    // env status lists it, env recover moves it (a chunked folder the game rewrote, proven by its world UID) into the run's
    // folder, the user's own world stays, and the next start places and hosts the fixture again.
    [ClientHostFact] public void AKilledStartsHostedWorldIsListedAndRecoveredSoTheNextStartRuns()
    {
        using var data = new FakeClientDataDirectory(_rig.Save);
        Directory.CreateDirectory(Worlds);
        File.WriteAllText(Path.Combine(Worlds, "MyWorld.fwl"), "the user's world");
        // The runner is killed while it holds the hosting client in its world, after the game saved it: here, the round stops
        // there and the client never stops, so the world stays in the client's worlds, as a killed runner leaves it.
        bool entered = false, markerWritten = false, deliberatelyCancelled = false;
        var held = new LocalFakeOwnedProcess(41) { StopFailure = () => new IOException("the runner was killed") };
        var (killed, killedOutput, runId) = Start(() => held, round => round.Step("hold until the runner is killed", () =>
        {
            entered = true;
            File.Copy(Path.Combine(Worlds, FixtureWorld, "_main.1.fwl2"), Path.Combine(Worlds, FixtureWorld, "_main.2.fwl2"));
            File.WriteAllText(Path.Combine(Worlds, FixtureWorld, "0_0.chunk"), "saved by the game");
            markerWritten = true;
            deliberatelyCancelled = true;
            throw new OperationCanceledException("the runner was killed");
        }), end: false);
        var firstJournal = KilledStartJournal(runId);
        try
        {
            RequireKilledStartReachedHold(killed, entered, markerWritten, deliberatelyCancelled);
            Assert.True(File.Exists(Path.Combine(Worlds, FixtureWorld, "0_0.chunk")));
            Kill(runId);

            string journalled = Path.Combine(Worlds, FixtureWorld);
            var left = Status(runId);
            Assert.Equal(JournalRunState.Recoverable, left.State);
            Assert.Equal(journalled, Assert.Single(left.Items, item => item.Kind == "copy").What);

            var recovered = RunRecovery.RecoverAsync(Hosts(), _ => Here, runId, teardown: false, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

            Assert.True(recovered.Clean, string.Join("\n", recovered.Steps));
            Assert.Contains($"world UID {_rig.Uid} as journalled", Assert.Single(recovered.Steps, step => step.What == "copy " + journalled).Outcome);
            Assert.Equal(new[] { "MyWorld.fwl" }, Directory.EnumerateFileSystemEntries(Worlds).Select(Path.GetFileName));
            Assert.Equal("saved by the game", File.ReadAllText(Path.Combine(killedOutput, runId, "host-world", FixtureWorld, "0_0.chunk")));
            Assert.Equal(JournalRunState.Ended, Status(runId).State);

            // The next start, an A/B run, places the same fixture for each arm, hosts it and moves it into that arm's evidence (its own
            // run folder there, so the arms never meet); the run ends clean and leaves nothing in the client environment's runtime.
            int pid = 50;
            var (next, output, nextRun) = Start(() => new LocalFakeOwnedProcess(pid++), arms: ["parent", "candidate"]);
            Assert.True(next.Passed, string.Join("; ", next.Steps.Where(step => !step.Passed).Select(step => step.Name + ": " + step.Error)));
            Assert.Equal(Path.Combine(output, "host-world"), next.Provenance["hostWorldEvidence"]);
            foreach (string arm in new[] { "parent", "candidate" })
            {
                string evidence = Path.Combine(Path.GetDirectoryName(output)!, arm);
                Assert.True(File.Exists(Path.Combine(evidence, "host-world", FixtureWorld, "_main.1.fwl2")), arm);
                Assert.True(File.Exists(Path.Combine(evidence, nextRun, "host-world", FixtureWorld, "_main.1.fwl2")), arm);
            }
            Assert.Equal(new[] { "MyWorld.fwl" }, Directory.EnumerateFileSystemEntries(Worlds).Select(Path.GetFileName));
            Assert.False(Path.Exists(Path.Combine(_rig.Runtime, nextRun)));
            Assert.Equal(JournalRunState.Ended, Status(nextRun).State);
        }
        catch (Exception error)
        {
            string retained = RetainKilledStartFailure(killed, killedOutput, runId, firstJournal, entered, markerWritten, deliberatelyCancelled);
            Assert.Fail(error.Message + " First killed-start report and pre-recovery journal retained at " + retained);
        }
    }

    // #612: a file the game writes into the install during an arm (a preloader log beside the executable on macOS, a mod writing
    // to the game root) is moved into that arm's evidence and named; the arm passes with the warning, and the install is as it was.
    [ClientHostFact] public void AFileTheGameAddsToTheInstallIsMovedIntoTheArmsEvidence()
    {
        var (here, scope) = InProcess();
        using var machine = scope;
        using var data = new FakeClientDataDirectory(_rig.Save);
        Directory.CreateDirectory(Worlds);
        var before = Directory.EnumerateFiles(_rig.Game, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        var (report, output, _) = Start(() => new LocalFakeOwnedProcess(61), round => round.Step("the game writes beside its executable",
            () => File.WriteAllText(Path.Combine(_rig.Game, "preloader_20261020.log"), "written by the fake game")));

        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(step => !step.Passed).Select(step => step.Name + ": " + step.Error)));
        Assert.Contains(report.Steps, step => step.Name == "verify client's source install after the run (size and modified time)" && step.Passed);
        Assert.Contains("preloader_20261020.log", report.Provenance["sourceIntegrity.client"]);
        if (OperatingSystem.IsWindows())
        {
            // A Windows client runs from its linked profile, so the game could not have written this: named, left in place.
            Assert.StartsWith("unchanged by the run, with a warning: ", report.Provenance["sourceIntegrity.client"]);
            Assert.True(File.Exists(Path.Combine(_rig.Game, "preloader_20261020.log")));
        }
        else
        {
            Assert.Equal("written by the fake game", File.ReadAllText(Path.Combine(output, "source-changes", "client", "added", "preloader_20261020.log")));
            Assert.Equal(before, Directory.EnumerateFiles(_rig.Game, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        }
        Assert.Empty(Directory.Exists(_rig.Runtime) ? Directory.EnumerateFileSystemEntries(_rig.Runtime) : []);
        Assert.Contains("source-evidence", here.Scripts); // settled in process, never through a shell
    }

    // #791: a passing start leaves nothing in the client environment's runtime folder (its disposable profile retired, its hosted
    // world collected into the evidence), and env status lists nothing of it.
    [ClientHostFact] public void APassingStartLeavesNoFolderInTheClientsRuntime()
    {
        var (here, scope) = InProcess();
        using var machine = scope;
        using var data = new FakeClientDataDirectory(_rig.Save);
        Directory.CreateDirectory(Worlds);
        var (report, output, runId) = Start(() => new LocalFakeOwnedProcess(60));
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(step => !step.Passed).Select(step => step.Name + ": " + step.Error)));
        Assert.True(File.Exists(Path.Combine(output, "host-world", FixtureWorld, "_main.1.fwl2")));
        Assert.Empty(Directory.Exists(_rig.Runtime) ? Directory.EnumerateFileSystemEntries(_rig.Runtime) : []);
        var status = Status(runId);
        Assert.Equal(JournalRunState.Ended, status.State);
        Assert.Empty(status.Items);
        Assert.Contains("source-evidence", here.Scripts); // settled in process, never through a shell
    }

    // #820: a start refused before launch because the client's worlds already hold the fixture world's name copied the fixture
    // first and left that copy journalled, so the run stayed recoverable and blocked the next one. The refusal now comes before
    // any copy: the user's world is untouched, no fixture copy exists, and the run ends with nothing left.
    [ClientHostFact] public void AStartRefusedForAWorldAlreadyThereLeavesNothingToRecover()
    {
        using var data = new FakeClientDataDirectory(_rig.Save);
        Directory.CreateDirectory(Path.Combine(Worlds, FixtureWorld));
        File.WriteAllText(Path.Combine(Worlds, FixtureWorld, "0_0.chunk"), "the user's own");
        var (refused, output, runId) = Start(() => new LocalFakeOwnedProcess(43));

        Assert.False(refused.Passed);
        var failed = Assert.Single(refused.Steps, step => !step.Passed);
        Assert.Equal("place the disposable fixture world in the client's local worlds", failed.Name);
        Assert.Contains("never copied over a world", failed.Error);
        Assert.False(File.Exists(Path.Combine(output, "client-process.json"))); // the client was never launched
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(Worlds, FixtureWorld, "0_0.chunk")));
        var status = Status(runId);
        Assert.Equal(JournalRunState.Ended, status.State);
        Assert.Empty(status.Items);
        Assert.Empty(Directory.GetDirectories(output, "valheim-test-*")); // no fixture copy was made
    }

    // ---- the ValheimCLI set (#124's capability manifest) ----

    [ClientHostFact] public void AStaleOrIncompleteValheimCliSetIsRefusedByItsCapabilityManifest()
    {
        var manifest = _rig.Manifest();
        manifest.Cli.Manifest = _rig.CliManifest(save: true);
        var regression = _rig.Regression(manifest, ["valheim.session/join", "example.probe/read"]);
        var staged = regression.Stage("parent");
        Assert.StartsWith("valheimCLI test build: BepInEx/plugins/valheimCLI.dll", staged.Manifest.CliManifest);
        Assert.Equal(CliCapabilities.HostedRounds.Append("valheim.session/join"), staged.Manifest.Capabilities);
        Assert.Equal(new[] { "valheim.session/join" }, staged.Plan.Capabilities); // The hosted rounds add their own; ValheimCLI's are checked statically and live.
        Assert.Equal(new[] { "example.probe/read" }, staged.Manifest.LiveOnlyCapabilities);
        // A plugin with the expected name and GUID, but from another build.
        var coherent = manifest.Cli.Core;
        string stale = _rig.Write("stale/valheimCLI.dll", RegressionRig.Assembly("valheimCLI", new("valheimCLI.valheimCLI", "0.3.0"), marker: "Stale"));
        manifest.Cli.Core = new() { File = stale, Sha256 = FileHash.Sha256(stale) };
        var error = Assert.Throws<InvalidOperationException>(() => regression.Stage("parent"));
        Assert.Contains("another build of valheimCLI.dll", error.Message);
        Assert.Contains("Install the manifest's pinned valheimCLI.dll", error.Message);
        // A set without a command the run uses.
        manifest.Cli.Core = coherent;
        manifest.Cli.Manifest = _rig.CliManifest(save: false);
        error = Assert.Throws<InvalidOperationException>(() => regression.Stage("parent"));
        Assert.Contains("lacks valheim.session/save", error.Message);
        Assert.Contains("valheim.session comes from the default valheimCLI.dll", error.Message);
        manifest.Cli.Manifest = Path.Combine(_rig.Root, "missing-manifest.json"); // Named, never skipped.
        Assert.Throws<FileNotFoundException>(() => regression.Stage("parent"));
    }

    // ---- the arms ----

    [ClientHostFact] public void AWrongCandidateHashIsRefusedNamingTheCommitToRebuild()
    {
        var manifest = _rig.Manifest();
        manifest.Mod.Arms["candidate"].Sha256 = new string('c', 64);
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent")); // Every arm is checked.
        Assert.Contains($"mod.arms.candidate: {_rig.Candidate} is sha256 {FileHash.Sha256(_rig.Candidate)}, but the manifest pins {new string('c', 64)} for commit c1", error.Message);
        Assert.Contains("Rebuild candidate from c1", error.Message);
        manifest.Mod.Arms["candidate"].File = Path.Combine(_rig.Root, "Jotunn.candidate.dll"); // A mistyped artifact name.
        Assert.Contains("Build commit c1, or correct the path", Assert.Throws<FileNotFoundException>(() => _rig.Regression(manifest).Stage("parent")).Message);
    }

    [ClientHostFact] public void TwoArmsWithOneInstalledHashAreRefusedUnlessARepeatabilityRun()
    {
        var manifest = _rig.Manifest();
        manifest.Mod.Arms["candidate"] = new() { File = _rig.Parent, Sha256 = FileHash.Sha256(_rig.Parent), Commit = "c1" };
        var error = Assert.Throws<ArgumentException>(() => _rig.Regression(manifest));
        Assert.Contains("mod.arms parent and candidate are the same build", error.Message);
        Assert.Contains("set mod.repeatability to true", error.Message);
        manifest.Mod.Repeatability = true;
        Assert.True(_rig.Regression(manifest).Stage("candidate").Manifest.Repeatability);
    }

    [ClientHostFact] public void AnUnpinnedFileIsRefusedWithTheHashToReview()
    {
        var manifest = _rig.Manifest();
        manifest.Plugins[0].Sha256 = "";
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains($"plugins[0]: pin {manifest.Plugins[0].File} by its SHA256. It is {FileHash.Sha256(manifest.Plugins[0].File)}", error.Message);
    }

    [ClientHostFact] public void ArmsOfDifferentModsAreRefused()
    {
        var manifest = _rig.Manifest();
        string other = _rig.Write("other/Other.dll", RegressionRig.Assembly("Other", new("example.other")));
        manifest.Mod.Arms["candidate"] = new() { File = other, Sha256 = FileHash.Sha256(other), Commit = "c1" };
        Assert.Contains("The arms declare different plugins", Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent")).Message);
    }

    // ---- the manifest file and the metadata reader ----

    [Fact] public void AManifestFileResolvesRelativePathsAndRefusesUnknownFields()
    {
        var manifest = _rig.Manifest();
        manifest.Fixture.Root = "fixture";
        string path = Path.Combine(_rig.Root, "regression.json");
        manifest.Write(path);
        var read = RegressionInputs.Read(path);
        Assert.Equal(_rig.Fixture, read.Fixture.Root);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"name\"", "\"absentPins\": {}, \"name\""));
        Assert.Throws<ArgumentException>(() => RegressionInputs.Read(path));
    }

    // A run's inputs read back on the machine recorded beside them, or on this machine when nothing is.
    [ClientHostFact] public void ReadUsesTheRecordedMachineOrThisOne()
    {
        string run = Path.Combine(_rig.Root, "run");
        Directory.CreateDirectory(run);
        string inputs = Path.Combine(run, "regression.json");
        _rig.Manifest().Write(inputs);
        var machine = new FakeMachine(HostProfile.CurrentPlatform);
        string steam = machine.Platform switch { "windows" => @"C:\Steam", "macos" => "/Users/tester/Library/Application Support/Steam", _ => "/home/tester/.steam/steam" };
        if (machine.Platform == "windows") machine.SteamPath = steam;
        machine.Directories.Add(steam);
        string detected = machine.App(steam, "892970", "Valheim", machine.Platform switch
            { "windows" => GameLaunch.ClientWindowsExecutable, "macos" => "Valheim.app/Contents/MacOS/Valheim", _ => throw new PlatformNotSupportedException(GameLaunch.LinuxClientRefusal) });
        using (EnvironmentInventory.UseMachine(machine))
            Assert.Equal((detected, "local-client"), (TargetedRegression.Read(inputs).Game, TargetedRegression.Read(inputs).ClientEnvironment));
        File.WriteAllText(Path.Combine(run, "environments.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            environments = new[] { new { name = "recorded", roles = new[] { "client" }, install = _rig.Game, runtime = _rig.Runtime, cliPort = 5600 } },
        }));
        var read = TargetedRegression.Read(inputs);
        Assert.Equal((_rig.Game, 5600, "recorded"), (read.Game, read.Port, read.ClientEnvironment));
        File.WriteAllText(inputs, "{ \"name\": ");
        Assert.Contains("is not a regression's inputs", Assert.Throws<ArgumentException>(() => RegressionInputs.Read(inputs)).Message);
    }

    // The run's machine is the inventory's client environment: its install, port and runtime, and only one on this machine.
    [Fact] public void TheClientEnvironmentSuppliesTheMachineAndMustBeOnThisMachine()
    {
        var regression = _rig.Regression(_rig.Manifest());
        Assert.Equal((_rig.Game, 5560, _rig.Install, "rig-client"), (regression.Game, regression.Port, regression.Install, regression.ClientEnvironment));
        var remote = _rig.Inventory();
        remote.Hosts["lab"] = new HostProfile { Kind = "ssh", Platform = "linux", Shell = "bash", Destination = "tester@lab", Lock = "/vt/lock" };
        remote.Environments.Insert(0, new EnvironmentRecipe { Name = "lab-client", Host = "lab", Roles = ["client"], Install = "/opt/valheim", Runtime = "/vt/runs", CliPort = 5600 });
        Assert.Contains("not this machine", Assert.Throws<ArgumentException>(() => new TargetedRegression(_rig.Manifest(), inventory: remote)).Message);
        Assert.Equal("rig-client", new TargetedRegression(_rig.Manifest(), inventory: remote, clientEnvironment: "rig-client").ClientEnvironment);
        Assert.Contains("no client environment nobody", Assert.Throws<ArgumentException>(() => new TargetedRegression(_rig.Manifest(), inventory: remote, clientEnvironment: "nobody")).Message);
    }

    // The example's sample is a regression's inputs (#376): every field is one RegressionInputs has
    // (read strictly, unknown fields refused). Its placeholders are not valid inputs, so it is not validated; the shape is.
    [Fact] public void TheExamplesSampleIsARegressionsInputs()
    {
        string sample = Path.Combine(FixtureProjects.RepositoryRoot(), "examples", "TargetedRegression", "regression.sample.json");
        var inputs = ClientPlanFile.Read<RegressionInputs>(sample);
        Assert.Equal("examplemod-mark", inputs.Name);
        Assert.Equal(["parent", "candidate"], inputs.Mod.Arms.Keys);
        Assert.NotNull(inputs.Cli.Manifest); // a regression.json must name cli.manifest (#296)
    }

    [Fact] public void PluginMetadataIsReadFromTheAttributesBepInExReads()
    {
        var read = PluginMetadata.Read(_rig.Parent);
        Assert.Equal("ExampleMod", read.AssemblyName);
        var plugin = Assert.Single(read.Plugins);
        Assert.Equal(("example.mod", "1.0.0", "Example.Plugin"), (plugin.Guid, plugin.Version, plugin.Type));
        Assert.Equal(new[] { new PluginDependency("example.dependency", true, "1.2.0"), new PluginDependency("example.soft", false, null), new PluginDependency("valheimCLI.valheimCLI", true, null) },
            plugin.Dependencies.OrderBy(dependency => dependency.Guid, StringComparer.Ordinal));
        Assert.Empty(PluginMetadata.Read(_rig.Write("lib/Library.dll", RegressionRig.Assembly("Library", null))).Plugins);
        string text = _rig.Write("text/NotAssembly.dll", Encoding.ASCII.GetBytes("not a PE file"));
        Assert.ThrowsAny<Exception>(() => PluginMetadata.Read(text));
    }
}

/// <summary>A prepared game, a one-world fixture, a staged character and the DLLs of one regression, in a temporary directory.</summary>
internal sealed class RegressionRig : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("regression-").FullName;
    public string Game => Path.Combine(Root, "game");
    /// <summary>The disposable install prepared by the shared hosted-runtime owner.</summary>
    public string Install => Path.Combine(Runtime, "vt-prep-regression-example-regression-" + RunJournal.ThisProcess.RunId, "runtime");
    /// <summary>The rig's client environment's runtime; a test may move it.</summary>
    public string Runtime { get; set; }
    /// <summary>The client environment's loader package, when a test selects one.</summary>
    public string? LoaderPackage { get; set; }
    /// <summary>This machine's Steam userdata, as detection would report it, when a test needs one.</summary>
    public string? SteamUserData { get; set; }
    public string Fixture => Path.Combine(Root, "fixture");
    public string Save => Path.Combine(Root, "save");
    public string Uid => "-1320459616";
    public string Parent { get; }
    public string Candidate { get; }
    private readonly string _core, _dependency, _probe;
    private readonly IDisposable _journalScope;

    public RegressionRig()
    {
        _journalScope = RunJournal.UseLocalDirectory(Path.Combine(Root, "journal"));
        Runtime = Path.Combine(Root, "runs");
        // A Linux machine runs no client (#708): its rig holds a Windows-layout install, which plans validate from any machine,
        // plus the Linux Doorstop library its dedicated-server tests load.
        var platform = GameLaunch.CurrentClientHost == ClientPlatform.Linux ? ClientPlatform.Windows : GameLaunch.CurrentClientHost;
        var game = ClientLaunchTests.Install.For(platform);
        if (GameLaunch.CurrentClientHost == ClientPlatform.Linux) game.Add("doorstop_libs/libdoorstop_x64.so");
        CopyTree(game.Root, Game);
        game.Dispose();
        string managed = platform == ClientPlatform.MacOS ? "Valheim.app/Contents/Resources/Data/Managed" : "valheim_Data/Managed";
        Write($"game/{managed}/assembly_valheim.dll", Encoding.UTF8.GetBytes("game"));
        Write($"game/{managed}/System.Private.CoreLib.dll", Encoding.UTF8.GetBytes("the emitted plugins' core library, as mscorlib is the game's"));
        Write("game/BepInEx/config/BepInEx.cfg", Encoding.UTF8.GetBytes("[Chainloader]\nHideManagerGameObject = true\n"));
        Write("game/BepInEx/plugins/Unrelated.dll", Encoding.UTF8.GetBytes("a plugin of the prepared game, never staged"));
        Write("save/characters_local/smoketest.fch", Encoding.UTF8.GetBytes("character"));
        FakeInstalls.World(Fixture, "SealFixture", long.Parse(Uid));
        _core = Write("cli/valheimCLI.dll", Assembly("valheimCLI", new("valheimCLI.valheimCLI", "0.4.0")));
        _dependency = Write("deps/Dependency.dll", Assembly("Dependency", new("example.dependency", "1.3.0")));
        _probe = Write("probe/Probe.dll", Assembly("Probe", new("testing.probe") { Hard = ["valheimCLI.valheimCLI"], Soft = ["example.mod"] }));
        var mod = new Plugin("example.mod") { Minimum = [("example.dependency", "1.2.0")], Hard = ["valheimCLI.valheimCLI"], Soft = ["example.soft"] };
        Parent = Write("parent/ExampleMod.dll", Assembly("ExampleMod", mod, marker: "Parent"));
        Candidate = Write("candidate/ExampleMod.dll", Assembly("ExampleMod", mod, marker: "Candidate"));
    }

    /// <summary>The inventory of this machine as the rig has it: one client environment, the prepared game.</summary>
    public EnvironmentInventory Inventory() => new()
    {
        Hosts = new() { ["local"] = new HostProfile { Kind = "local", Platform = HostProfile.CurrentPlatform, Shell = OperatingSystem.IsWindows() ? "pwsh" : "bash", Lock = Path.Combine(Root, "lock") } },
        // The synthetic Mac install uses the stock x64-only Doorstop. Select Rosetta explicitly, as a real consumer must.
        Environments = [new EnvironmentRecipe { Name = "rig-client", Host = "local", Roles = ["client"], Install = Game, Runtime = Runtime, CliPort = 5560, LoaderPackage = LoaderPackage, Architecture = "x64" }],
        LeaseHost = "local", LeaseDirectory = Path.Combine(Root, "leases"),
    };

    /// <summary>The regression of <paramref name="inputs"/> on the rig's client environment, with the rig's save folder.</summary>
    public TargetedRegression Regression(RegressionInputs inputs, IEnumerable<string>? scenarioCapabilities = null, bool skipHostLock = true) =>
        new(inputs, scenarioCapabilities, Inventory())
        {
            SaveDirectory = Save, SteamUserData = SteamUserData,
            // This rig stages fake files without a desktop. Tests of the SSH refusal override this explicitly.
            DirectClientSession = () => (OperatingSystem.IsWindows(), 1),
            BundleInspection = (_, _) => new(MacBundleInspection.State.Accepted, 0, "synthetic test install"),
            ProcessCheck = (_, _) => Task.CompletedTask,
            SkipHostLockForTest = skipHostLock,
            // The rig's fake installs keep their fake loader; the pinned pack's rule has its own tests (ShippedLoaderTests).
            PinnedLoader = (_, _) => null,
        };

    public RegressionInputs Manifest(string? fixture = null, string? worldUid = null) => new()
    {
        Name = "example-regression",
        Client = new() { Character = "smoketest" },
        Fixture = new() { Root = fixture ?? Fixture, WorldUid = worldUid ?? Uid },
        Cli = new() { Core = Pinned(_core), Manifest = CliManifest(save: true) },
        Plugins = [Pinned(_dependency)],
        Probe = Pinned(_probe),
        Mod = new()
        {
            InstallAs = "ExampleMod.dll",
            Arms = new()
            {
                ["parent"] = new() { File = Parent, Sha256 = FileHash.Sha256(Parent), Commit = "p1" },
                ["candidate"] = new() { File = Candidate, Sha256 = FileHash.Sha256(Candidate), Commit = "c1" },
            },
        },
    };

    /// <summary>A capability manifest of the rig's one default plugin. With
    /// <paramref name="full"/> false, <paramref name="save"/> false leaves out valheim.session/save.</summary>
    public string CliManifest(bool save, bool full = false)
    {
        var session = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["state"] = 1, ["leave"] = 1, ["join"] = 1 };
        if (save) session["save"] = 1;
        var manifest = new CliCapabilityManifest
        {
            Build = "valheimCLI test build",
            Plugin = new() { File = "valheimCLI.dll", Sha256 = FileHash.Sha256(_core), Plugins = ["valheimCLI.valheimCLI"],
                Extensions = new(StringComparer.Ordinal) { ["valheim.session"] = session } },
        };
        if (full)
        {
            // The one-shot fixture models every built-in toolkit capability in the default plugin.
            var commands = CliCapabilities.Toolkit.Select(path => path.Split('/'))
                .GroupBy(parts => parts[0], StringComparer.Ordinal);
            foreach (var group in commands)
            {
                manifest.Plugin.Extensions[group.Key] = new SortedDictionary<string, int>(
                    group.ToDictionary(parts => parts[1], _ => 1, StringComparer.Ordinal), StringComparer.Ordinal);
            }
        }
        string path = Path.Combine(Root, $"cli-manifest-{Guid.NewGuid():N}.json");
        manifest.Write(path);
        return path;
    }

    private static RegressionFile Pinned(string path) => new() { File = path, Sha256 = FileHash.Sha256(path) };

    public string Write(string relative, byte[] content)
    {
        string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>The fixture's world inside <c>&lt;root&gt;/wrapper/</c>, as a copied save directory nests it.</summary>
    public string Wrap(string wrapper)
    {
        string root = Path.Combine(Root, "wrapped");
        CopyTree(Fixture, Path.Combine(root, wrapper));
        return root;
    }

    /// <summary>A second world beside the fixture's own.</summary>
    public string AddWorld(string name, long uid)
    {
        return FakeInstalls.World(Fixture, name, uid);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
    }

    public void Dispose()
    {
        _journalScope.Dispose();
        Directory.Delete(Root, recursive: true);
    }

    /// <summary>A plugin's declaration, as its BepInEx attributes state it.</summary>
    public sealed record Plugin(string Guid, string Version = "1.0.0")
    {
        public string[] Hard { get; init; } = [];
        public string[] Soft { get; init; } = [];
        public (string Guid, string Version)[] Minimum { get; init; } = [];
        public string[] Incompatible { get; init; } = [];
        public string[] Processes { get; init; } = [];
    }

    /// <summary>
    /// An assembly named <paramref name="name"/>, declaring <paramref name="plugin"/> (or none, a library) with BepInEx 5's
    /// attribute shapes. The attribute types are defined in the assembly itself: BepInEx and the reader match them by full
    /// name. <paramref name="marker"/> makes two builds of one plugin differ; <paramref name="reference"/> adds a reference
    /// to that type's assembly.
    /// </summary>
    public static byte[] Assembly(string name, Plugin? plugin, string marker = "Build", Type? reference = null)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(name);
        var flags = module.DefineEnum("BepInEx.DependencyFlags", TypeAttributes.Public, typeof(int));
        flags.DefineLiteral("HardDependency", 1);
        flags.DefineLiteral("SoftDependency", 2);
        flags.CreateType();
        ConstructorBuilder[] Define(string type, params Type[][] constructors)
        {
            var builder = module.DefineType(type, TypeAttributes.Public | TypeAttributes.Sealed, typeof(Attribute));
            var made = constructors.Select(parameters =>
            {
                var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, parameters);
                var il = constructor.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
                il.Emit(OpCodes.Ret);
                return constructor;
            }).ToArray();
            builder.CreateType();
            return made;
        }
        var pluginAttribute = Define("BepInEx.BepInPlugin", [typeof(string), typeof(string), typeof(string)])[0];
        // BepInDependency(string guid, DependencyFlags flags) and BepInDependency(string guid, string minimumVersion).
        var dependency = Define("BepInEx.BepInDependency", [typeof(string), flags], [typeof(string), typeof(string)]);
        var incompatibility = Define("BepInEx.BepInIncompatibility", [typeof(string)])[0];
        var process = Define("BepInEx.BepInProcess", [typeof(string)])[0];
        var type = module.DefineType("Example.Plugin", TypeAttributes.Public | TypeAttributes.Class);
        if (plugin != null)
        {
            type.SetCustomAttribute(pluginAttribute, Blob(plugin.Guid, plugin.Guid + " name", plugin.Version));
            foreach (var (guid, version) in plugin.Minimum) type.SetCustomAttribute(dependency[1], Blob(guid, version));
            foreach (string guid in plugin.Hard) type.SetCustomAttribute(dependency[0], Blob(guid, 1));
            foreach (string guid in plugin.Soft) type.SetCustomAttribute(dependency[0], Blob(guid, 2));
            foreach (string guid in plugin.Incompatible) type.SetCustomAttribute(incompatibility, Blob(guid));
            foreach (string filter in plugin.Processes) type.SetCustomAttribute(process, Blob(filter));
        }
        if (reference != null) type.DefineField("Uses", reference, FieldAttributes.Public | FieldAttributes.Static);
        type.CreateType();
        module.DefineType("Example." + marker, TypeAttributes.Public).CreateType();
        using var image = new MemoryStream();
        assembly.Save(image);
        return image.ToArray();
    }

    // A custom attribute blob (ECMA-335 II.23.3): prolog, fixed arguments (UTF-8 strings or int32), no named arguments.
    private static byte[] Blob(params object[] arguments)
    {
        using var blob = new MemoryStream();
        using var writer = new BinaryWriter(blob);
        writer.Write((ushort)1);
        foreach (object argument in arguments)
            if (argument is string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                if (bytes.Length > 127) throw new ArgumentException("Keep test strings short.");
                writer.Write((byte)bytes.Length);
                writer.Write(bytes);
            }
            else writer.Write((int)argument);
        writer.Write((ushort)0);
        return blob.ToArray();
    }
}
