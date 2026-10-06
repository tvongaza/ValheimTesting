using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Valheim.Testing.Game;
using Xunit;

// Targeted native regressions (#125): the setup mistakes two hand-written A/B runs hit before their first assertion, each
// refused before the game starts and naming the corrective action. Plugins are small assemblies emitted here with BepInEx's
// attribute shapes; no game, Steam or network connection.
public sealed class TargetedRegressionTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public void ATargetedRunValidatesItsExpectedErrorLinesBeforeStaging()
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

    [Fact] public void AValidManifestStagesOnlyTheAllowlistThenPreflightsEveryArm()
    {
        var regression = _rig.Regression(_rig.Manifest(), ["example.probe/read"]);
        var parent = regression.Stage("parent");
        string plugins = Path.Combine(_rig.Install, "BepInEx", "plugins");
        Assert.Equal(new[] { "Dependency.dll", "ExampleMod.dll", "Probe.dll", "Valheim.Cli.Standard.dll", "valheimCLI.dll" },
            Directory.EnumerateFiles(plugins).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(plugins, "Unrelated.dll"))); // The prepared game's own plugins are never copied.
        Assert.True(File.Exists(Path.Combine(_rig.Game, "BepInEx", "plugins", "Unrelated.dll"))); // ...nor touched.
        // Every staged plugin is pinned under its declared GUID by MD5, and nothing else: no absent pins for other mods.
        Assert.Equal(new[] { "example.dependency", "example.mod", "testing.probe", "valheimCLI.standard", "valheimCLI.valheimCLI" }, parent.Plan.Pins.Keys.Order(StringComparer.Ordinal));
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

    // ---- the fixture: nesting and identity ----

    [Theory]
    [InlineData("world-folder", "is a world folder itself", "Point fixture.root at its parent")]
    [InlineData("worlds_local", "holds a worlds_local/ wrapper", "Copy only the one world folder")]
    [InlineData("two-worlds", "holds 2 worlds (Other, SealFixture)", "Copy only the world under test")]
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

    [Fact] public void AFixtureHoldingAnotherUidIsRefusedNamingTheUidItHolds()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(_rig.Manifest(worldUid: "450017353")).Stage("parent"));
        Assert.Contains("hostWorld.worldUid is 450017353", error.Message);
        Assert.Contains($"holds world SealFixture with UID {_rig.Uid}", error.Message);
        Assert.Contains("Pin the UID of this exact fixture", error.Message);
        Assert.False(Directory.Exists(_rig.Install));
    }

    // ---- dependencies: declared by the manifest and by the mod's own metadata ----

    [Fact] public void AnOmittedDependencyTheModDeclaresIsRefusedNamingThePluginToStage()
    {
        var manifest = _rig.Manifest();
        manifest.Plugins.Clear(); // The JsonDotNET detector shape: the mod's [BepInDependency] is met by nothing staged.
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains("example.mod (BepInEx/plugins/ExampleMod.dll) has a hard [BepInDependency(\"example.dependency\", \"1.2.0\")] that nothing staged declares", error.Message);
        Assert.Contains("Add the DLL that declares [BepInPlugin(\"example.dependency\")] to plugins in the manifest", error.Message);
    }

    [Fact] public void ADependencyIsMetByWhatADllDeclaresNeverByItsFileName()
    {
        // A file named like the dependency that declares no plugin does not meet it.
        string impostor = _rig.Write("impostor/Dependency.dll", RegressionRig.Assembly("Dependency", null));
        var manifest = _rig.Manifest();
        manifest.Plugins = [new() { File = impostor, Sha256 = FileHash.Sha256(impostor) }];
        Assert.Contains("that nothing staged declares", Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent")).Message);
        // A renamed file that declares it does.
        string renamed = _rig.Write("renamed/SomethingElse.dll", RegressionRig.Assembly("Dependency", new("example.dependency", "1.3.0")));
        manifest.Plugins = [new() { File = renamed, Sha256 = FileHash.Sha256(renamed) }];
        _rig.Regression(manifest).Stage("parent");
        // An older build than the mod requires is named with both versions.
        string old = _rig.Write("old/Dependency.dll", RegressionRig.Assembly("Dependency", new("example.dependency", "1.1.0")));
        manifest.Plugins = [new() { File = old, Sha256 = FileHash.Sha256(old) }];
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains("needs example.dependency 1.2.0 or newer, and the staged BepInEx/plugins/Dependency.dll declares 1.1.0", error.Message);
    }

    [Fact] public void AnAssemblyNothingStagedProvidesIsRefusedUnlessDeclaredOptional()
    {
        string uses = _rig.Write("uses/Library.dll", RegressionRig.Assembly("UsesLibrary", null, reference: typeof(FactAttribute)));
        var manifest = _rig.Manifest();
        manifest.Plugins.Add(new() { File = uses, Sha256 = FileHash.Sha256(uses) });
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains("BepInEx/plugins/Library.dll references assembly xunit.core", error.Message);
        Assert.Contains("optionalReferences", error.Message);
        // Staging the library that provides it, by its assembly name, meets it.
        string library = _rig.Write("lib/xunit.core.dll", RegressionRig.Assembly("xunit.core", null));
        manifest.Plugins.Add(new() { File = library, Sha256 = FileHash.Sha256(library) });
        _rig.Regression(manifest).Stage("parent");
        manifest.Plugins.RemoveAt(manifest.Plugins.Count - 1);
        manifest.OptionalReferences = ["xunit.core"];
        _rig.Regression(manifest).Stage("parent");
    }

    [Fact] public void IncompatibleServerOnlyAndDuplicatePluginsAreRefused()
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

    [Fact] public void AnExtraPluginOutsideTheAllowlistIsRefusedBeforeTheLaunch()
    {
        var staged = _rig.Regression(_rig.Manifest()).Stage("parent");
        File.WriteAllText(Path.Combine(_rig.Install, "BepInEx", "plugins", "Extra.dll"), "a plugin the test would never call");
        var error = Assert.Throws<InvalidOperationException>(staged.Verify);
        Assert.Contains("BepInEx/plugins/Extra.dll is not in the allowlist", error.Message);
        Assert.Contains("Declare it in the manifest (plugins, probe or configs) and stage again", error.Message);
        File.WriteAllText(Path.Combine(_rig.Install, "BepInEx", "plugins", "Dependency.dll"), "changed");
        Assert.Contains("BepInEx/plugins/Dependency.dll changed after staging", Assert.Throws<InvalidOperationException>(staged.Verify).Message);
        // Staging again rebuilds the folder from the allowlist.
        _rig.Regression(_rig.Manifest()).Stage("parent").Verify();
        Assert.False(File.Exists(Path.Combine(_rig.Install, "BepInEx", "plugins", "Extra.dll")));
    }

    [Fact] public void AnInstallThisToolDidNotCreateIsNeverChanged()
    {
        Directory.CreateDirectory(Path.Combine(_rig.Install, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(_rig.Install, "BepInEx", "plugins", "Valued.dll"), "someone's install");
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(_rig.Manifest()).Stage("parent"));
        Assert.Contains("is not a disposable install this tool created", error.Message);
        Assert.Throws<InvalidOperationException>(() => _rig.Regression(_rig.Manifest()).Remove());
        Assert.True(File.Exists(Path.Combine(_rig.Install, "BepInEx", "plugins", "Valued.dll")));
        Directory.Delete(_rig.Install, recursive: true);
        _rig.Regression(_rig.Manifest()).Stage("parent");
        _rig.Regression(_rig.Manifest()).Remove();
        Assert.False(Directory.Exists(_rig.Install));
        // The prepared game itself can never hold the disposable install: a client environment whose runtime is inside it is refused.
        _rig.Runtime = Path.Combine(_rig.Game, "runs");
        Assert.Contains("overlap", Assert.Throws<ArgumentException>(() => _rig.Regression(_rig.Manifest())).Message);
    }

    [Fact] public void RemovingTheInstallIsTheLastArmsCleanupStep()
    {
        _rig.Regression(_rig.Manifest()).Stage("parent");
        string evidence = Path.Combine(_rig.Root, "evidence-last");
        var report = new ScenarioReport("last arm");
        report.Step("the scenario", () => { });
        _rig.Regression(_rig.Manifest()).Remove(report, evidence);
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

    [Fact] public void AnArmsCommitIsOptionalButNeverBlank()
    {
        var arm = new RegressionArm { File = Path.GetFullPath("/mods/My.dll"), Sha256 = new string('a', 64) };
        arm.Validate("mod.arms.parent"); // Not known: left out, never replaced by a stand-in.
        arm.Commit = " ";
        Assert.Contains("leave it out when it is not known", Assert.Throws<ArgumentException>(() => arm.Validate("mod.arms.parent")).Message);
    }

    [Fact] public void AReusedInstallIsCopiedAgainWhenTheGamesLoaderChanged()
    {
        // Found on a Windows station: the prepared game held a mod manager's Doorstop proxy, which the preflight refused.
        // Restoring the game's own proxy changed neither the game build nor BepInEx's core, and the reused copy kept the old one.
        _rig.Write("game/.doorstop_version", Encoding.UTF8.GetBytes("4.4.0"));
        _rig.Regression(_rig.Manifest()).Stage("parent");
        string earlier = Path.Combine(_rig.Install, "from-the-first-copy.txt");
        File.WriteAllText(earlier, "only in the disposable install");
        _rig.Regression(_rig.Manifest()).Stage("candidate");
        Assert.True(File.Exists(earlier)); // Nothing changed in the game: the install is reused.
        _rig.Write("game/.doorstop_version", Encoding.UTF8.GetBytes("3.4.0"));
        _rig.Regression(_rig.Manifest()).Stage("candidate").Verify();
        Assert.Equal("3.4.0", File.ReadAllText(Path.Combine(_rig.Install, ".doorstop_version")));
        Assert.False(File.Exists(earlier)); // Copied again from the game.

        // Removing a loader file must also invalidate the copy. Otherwise an old proxy can survive in the install.
        string stale = Path.Combine(_rig.Install, "from-the-second-copy.txt");
        File.WriteAllText(stale, "only in the disposable install");
        File.Delete(Path.Combine(_rig.Game, ".doorstop_version"));
        _rig.Regression(_rig.Manifest()).Stage("candidate").Verify();
        Assert.False(File.Exists(Path.Combine(_rig.Install, ".doorstop_version")));
        Assert.False(File.Exists(stale));
    }

    [Fact] public void AMissingCharacterIsRefusedBeforeTheLaunch()
    {
        var manifest = _rig.Manifest();
        manifest.Client.Character = "nobody";
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains("nobody.fch is not in", error.Message);
        Assert.Contains("Stage the disposable local character", error.Message);
    }

    [Fact] public void ARegisteredCharacterCanBePreflightedWithoutTouchingTheLiveCharacterFolder()
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

    // ---- the ValheimCLI set (#124's capability manifest) ----

    [Fact] public void AStaleOrIncompleteValheimCliSetIsRefusedByItsCapabilityManifest()
    {
        var manifest = _rig.Manifest();
        manifest.Cli.Manifest = _rig.CliManifest(save: true);
        var staged = _rig.Regression(manifest, ["valheim.session/join", "example.probe/read"]).Stage("parent");
        Assert.StartsWith("valheimCLI test build: BepInEx/plugins/valheimCLI.dll, BepInEx/plugins/Valheim.Cli.Standard.dll", staged.Manifest.CliManifest);
        Assert.Equal(CliCapabilities.HostedRounds.Append("valheim.session/join"), staged.Manifest.Capabilities);
        Assert.Equal(new[] { "valheim.session/join" }, staged.Plan.Capabilities); // The hosted rounds add their own; ValheimCLI's are checked statically and live.
        Assert.Equal(new[] { "example.probe/read" }, staged.Manifest.LiveOnlyCapabilities);
        // The Epic Loot shape: a pack with the expected file name and plugin GUID, from another build.
        var coherent = manifest.Cli.Packs;
        string stale = _rig.Write("stale/Valheim.Cli.Standard.dll", RegressionRig.Assembly("Valheim.Cli.Standard", new("valheimCLI.standard", "0.3.0") { Hard = ["valheimCLI.valheimCLI"] }, marker: "Stale"));
        manifest.Cli.Packs = [new() { File = stale, Sha256 = FileHash.Sha256(stale) }];
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains("another build of Valheim.Cli.Standard.dll", error.Message);
        Assert.Contains("Install the manifest's core and packs together", error.Message);
        // A set without a command the run uses.
        manifest.Cli.Packs = coherent;
        manifest.Cli.Manifest = _rig.CliManifest(save: false);
        error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains("lacks valheim.session/save", error.Message);
        Assert.Contains("valheim.session comes from the Standard pack", error.Message);
        manifest.Cli.Manifest = Path.Combine(_rig.Root, "missing-manifest.json"); // Named, never skipped.
        Assert.Throws<FileNotFoundException>(() => _rig.Regression(manifest).Stage("parent"));
    }

    // ---- the arms ----

    [Fact] public void AWrongCandidateHashIsRefusedNamingTheCommitToRebuild()
    {
        var manifest = _rig.Manifest();
        manifest.Mod.Arms["candidate"].Sha256 = new string('c', 64);
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent")); // Every arm is checked.
        Assert.Contains($"mod.arms.candidate: {_rig.Candidate} is sha256 {FileHash.Sha256(_rig.Candidate)}, but the manifest pins {new string('c', 64)} for commit c1", error.Message);
        Assert.Contains("Rebuild candidate from c1", error.Message);
        manifest.Mod.Arms["candidate"].File = Path.Combine(_rig.Root, "Jotunn.candidate.dll"); // A mistyped artifact name.
        Assert.Contains("Build commit c1, or correct the path", Assert.Throws<FileNotFoundException>(() => _rig.Regression(manifest).Stage("parent")).Message);
    }

    [Fact] public void TwoArmsWithOneInstalledHashAreRefusedUnlessARepeatabilityRun()
    {
        var manifest = _rig.Manifest();
        manifest.Mod.Arms["candidate"] = new() { File = _rig.Parent, Sha256 = FileHash.Sha256(_rig.Parent), Commit = "c1" };
        var error = Assert.Throws<ArgumentException>(() => _rig.Regression(manifest));
        Assert.Contains("mod.arms parent and candidate are the same build", error.Message);
        Assert.Contains("set mod.repeatability to true", error.Message);
        manifest.Mod.Repeatability = true;
        Assert.True(_rig.Regression(manifest).Stage("candidate").Manifest.Repeatability);
    }

    [Fact] public void AnUnpinnedFileIsRefusedWithTheHashToReview()
    {
        var manifest = _rig.Manifest();
        manifest.Plugins[0].Sha256 = "";
        var error = Assert.Throws<InvalidOperationException>(() => _rig.Regression(manifest).Stage("parent"));
        Assert.Contains($"plugins[0]: pin {manifest.Plugins[0].File} by its SHA256. It is {FileHash.Sha256(manifest.Plugins[0].File)}", error.Message);
    }

    [Fact] public void ArmsOfDifferentModsAreRefused()
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

    // The environment manifest it replaces described the machine; each such field is refused, naming where it now comes from.
    [Fact] public void ARetiredEnvironmentManifestNamesWhereEachMachineFieldNowComesFrom()
    {
        string path = Path.Combine(_rig.Root, "environment.json");
        File.WriteAllText(path, """
            { "name": "old", "game": "C:\\Valheim", "install": "C:\\disposable", "loaderPackage": "loader.json",
              "client": { "port": 5560, "character": "smoketest", "saveDirectory": "C:\\save", "steamUserDataDirectory": "C:\\Steam\\userdata" } }
            """);
        string message = Assert.Throws<ArgumentException>(() => RegressionInputs.Read(path)).Message;
        foreach (string field in new[] { "game now comes from", "install now comes from", "loaderPackage now comes from", "client.port now comes from",
                     "client.saveDirectory now comes from", "client.steamUserDataDirectory now comes from" })
            Assert.Contains(field, message);
    }

    // A run's inputs read back on the machine recorded beside them, or on this machine when nothing is.
    [Fact] public void ReadUsesTheRecordedMachineOrThisOne()
    {
        string run = Path.Combine(_rig.Root, "run");
        Directory.CreateDirectory(run);
        string inputs = Path.Combine(run, "regression.json");
        _rig.Manifest().Write(inputs);
        var machine = new FakeMachine(HostProfile.CurrentPlatform);
        string steam = machine.Platform switch { "windows" => @"C:\Steam", "macos" => "/Users/tester/Library/Application Support/Steam", _ => "/home/tester/.local/share/Steam" };
        if (machine.Platform == "windows") machine.SteamPath = steam;
        machine.Directories.Add(steam);
        string detected = machine.App(steam, "892970", "Valheim", machine.Platform switch
            { "windows" => GameLaunch.ClientWindowsExecutable, "macos" => "Valheim.app/Contents/MacOS/Valheim", _ => GameLaunch.ClientLinuxExecutable });
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

    // The example's sample is a regression's inputs (#376): no retired machine field, and every field one RegressionInputs has
    // (read strictly, unknown fields refused). Its placeholders are not valid inputs, so it is not validated; the shape is.
    [Fact] public void TheExamplesSampleIsARegressionsInputs()
    {
        string sample = Path.Combine(FixtureProjects.RepositoryRoot(), "examples", "TargetedRegression", "regression.sample.json");
        var error = Assert.Throws<ArgumentException>(() => RegressionInputs.Read(sample));
        Assert.DoesNotContain("retired environment manifest", error.Message);
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
    /// <summary>The disposable install: the client environment's runtime, regression-&lt;name&gt;.</summary>
    public string Install => Path.Combine(Runtime, "regression-example-regression");
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
    private readonly string _core, _pack, _dependency, _probe;

    public RegressionRig()
    {
        Runtime = Path.Combine(Root, "runs");
        var platform = GameLaunch.CurrentClientHost;
        var game = ClientLaunchTests.Install.For(platform);
        CopyTree(game.Root, Game);
        game.Dispose();
        string managed = platform == ClientPlatform.MacOS ? "Valheim.app/Contents/Resources/Data/Managed" : "valheim_Data/Managed";
        Write($"game/{managed}/assembly_valheim.dll", Encoding.UTF8.GetBytes("game"));
        Write($"game/{managed}/System.Private.CoreLib.dll", Encoding.UTF8.GetBytes("the emitted plugins' core library, as mscorlib is the game's"));
        Write("game/BepInEx/config/BepInEx.cfg", Encoding.UTF8.GetBytes("[Chainloader]\nHideManagerGameObject = true\n"));
        Write("game/BepInEx/plugins/Unrelated.dll", Encoding.UTF8.GetBytes("a plugin of the prepared game, never staged"));
        Write("save/characters_local/smoketest.fch", Encoding.UTF8.GetBytes("character"));
        Write("fixture/SealFixture/_main.1.fwl2", OwnedRunPreflightTests.Metadata("SealFixture", long.Parse(Uid)));
        Write("fixture/SealFixture/_main.1.db2", Encoding.UTF8.GetBytes("world"));
        _core = Write("cli/valheimCLI.dll", Assembly("valheimCLI", new("valheimCLI.valheimCLI", "0.4.0")));
        _pack = Write("cli/Valheim.Cli.Standard.dll", Assembly("Valheim.Cli.Standard", new("valheimCLI.standard", "0.4.0") { Hard = ["valheimCLI.valheimCLI"] }));
        _dependency = Write("deps/Dependency.dll", Assembly("Dependency", new("example.dependency", "1.3.0")));
        _probe = Write("probe/Probe.dll", Assembly("Probe", new("testing.probe") { Hard = ["valheimCLI.valheimCLI"], Soft = ["example.mod"] }));
        var mod = new Plugin("example.mod") { Minimum = [("example.dependency", "1.2.0")], Hard = ["valheimCLI.valheimCLI"], Soft = ["example.soft"] };
        Parent = Write("parent/ExampleMod.dll", Assembly("ExampleMod", mod, marker: "Parent"));
        Candidate = Write("candidate/ExampleMod.dll", Assembly("ExampleMod", mod, marker: "Candidate"));
    }

    /// <summary>The inventory of this machine as the rig has it: one client environment, the prepared game.</summary>
    public EnvironmentInventory Inventory() => new()
    {
        Hosts = new() { ["local"] = new HostProfile { Kind = "local", Platform = HostProfile.CurrentPlatform, Shell = OperatingSystem.IsWindows() ? "powershell" : "bash", Lock = Path.Combine(Root, "lock") } },
        Environments = [new EnvironmentRecipe { Name = "rig-client", Host = "local", Roles = ["client"], Install = Game, Runtime = Runtime, CliPort = 5560, LoaderPackage = LoaderPackage }],
        LeaseHost = "local", LeaseDirectory = Path.Combine(Root, "leases"),
    };

    /// <summary>The regression of <paramref name="inputs"/> on the rig's client environment, with the rig's save folder.</summary>
    public TargetedRegression Regression(RegressionInputs inputs, IEnumerable<string>? scenarioCapabilities = null) =>
        new(inputs, scenarioCapabilities, Inventory()) { SaveDirectory = Save, SteamUserData = SteamUserData };

    public RegressionInputs Manifest(string? fixture = null, string? worldUid = null) => new()
    {
        Name = "example-regression",
        Client = new() { Character = "smoketest" },
        Fixture = new() { Root = fixture ?? Fixture, WorldUid = worldUid ?? Uid },
        Cli = new() { Core = Pinned(_core), Packs = [Pinned(_pack)], Manifest = CliManifest(save: true) },
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

    /// <summary>A capability manifest of the rig's core and Standard pack, written to a new file; <paramref name="save"/> false leaves out valheim.session/save.</summary>
    public string CliManifest(bool save)
    {
        var session = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["state"] = 1, ["leave"] = 1, ["join"] = 1 };
        if (save) session["save"] = 1;
        var manifest = new CliCapabilityManifest
        {
            Build = "valheimCLI test build",
            Files =
            [
                new() { File = "valheimCLI.dll", Sha256 = FileHash.Sha256(_core), Plugins = ["valheimCLI.valheimCLI"] },
                new()
                {
                    File = "Valheim.Cli.Standard.dll", Sha256 = FileHash.Sha256(_pack), Plugins = ["valheimCLI.standard"],
                    Extensions = new(StringComparer.Ordinal) { ["valheim.session"] = session },
                },
            ],
        };
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
        Write($"fixture/{name}/_main.1.fwl2", OwnedRunPreflightTests.Metadata(name, uid));
        return Fixture;
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

    public void Dispose() => Directory.Delete(Root, recursive: true);

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
