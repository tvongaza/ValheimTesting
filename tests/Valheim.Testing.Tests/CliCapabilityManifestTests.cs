using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;
using Xunit;

// The static ValheimCLI capability check (#124): an owned client's ValheimCLI plugin, compared by SHA256 with the manifest of one
// one-plugin build (#670), refused before the game starts when it is another generation. The DLLs are small assemblies compiled
// here with Roslyn in the shape of ValheimCLI's own registrations (a stub BepInEx and a plugin with the extension API that
// registers its built-in commands); no ValheimCLI, game or BepInEx assembly is used. No game, Steam or network connection.
public sealed class CliCapabilityManifestTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cli-manifest-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Core = "valheimCLI.dll", Standard = "Valheim.Cli.Standard.dll";

    [Fact] public void ReleaseContractCoversTheToolkitsSessionAndTerrainCommands()
    {
        Assert.Equal(new[]
        {
            "valheim.observe/config", "valheim.observe/content-census", "valheim.observe/custom-data", "valheim.observe/dungeon-rooms",
            "valheim.observe/globalkeys", "valheim.observe/harmony", "valheim.observe/review-begin", "valheim.observe/review-clip-frames",
            "valheim.observe/review-clutter-off", "valheim.observe/review-mist-off", "valheim.observe/review-restore",
            "valheim.observe/unresolved-prefabs", "valheim.observe/zones",
            "valheim.session/join", "valheim.session/leave", "valheim.session/save", "valheim.session/state",
            "valheim.session/teleport-signals", "valheim.world/player-support", "valheim.world/player-support-wait",
            "valheim.world/terrain", "valheim.world/terrain-grid", "valheim.world/terrain-paint", "valheim.world/terrain-surface"
        }, CliCapabilities.Toolkit);
        Assert.All(CliCapabilities.HostedRounds.Concat(PlayerPlacement.ArrivalCapabilities),
            capability => Assert.Contains(capability, CliCapabilities.Toolkit));
    }

    // ---- the generator: what the plugin registers, read from its metadata and IL ----

    [Fact] public void ForkGeneratedManifestIsTheRuntimeManifestContract()
    {
        var builds = new Builds(OptimizationLevel.Release);
        var files = Write(builds, "fork-contract", (Core, builds.Current));
        string output = Path.Combine(_root, "fork-contract.json");
        valheim_cli.Testing.CliBuildManifest.Write("fork-contract", files, output);
        var fromFork = CliCapabilityManifest.Read(output);
        var fromFacade = CliCapabilityManifest.Generate("fork-contract", files);
        Assert.Equal(fromFacade.Plugin.File, fromFork.Plugin.File);
        Assert.Equal(fromFacade.Plugin.Sha256, fromFork.Plugin.Sha256);
        Assert.Equal(fromFacade.Capabilities.OrderBy(entry => entry.Key), fromFork.Capabilities.OrderBy(entry => entry.Key));
    }

    [Theory]
    [InlineData(OptimizationLevel.Release)]
    [InlineData(OptimizationLevel.Debug)]
    public void TheManifestOfABuildIsReadFromThePluginsOwnRegistrations(OptimizationLevel level)
    {
        var builds = new Builds(level);
        var manifest = CliCapabilityManifest.Generate("valheimCLI test build", Write(builds, "build", (Core, builds.Current)));
        Assert.Equal(Core, manifest.Plugin.File);
        Assert.Equal(new[] { "valheimCLI.valheimCLI" }, manifest.Plugin.Plugins);
        Assert.Equal(Hash(builds.Current), manifest.Plugin.Sha256);
        // The console module host passes its id as an argument: cli.standard/commands is not claimed.
        Assert.Equal(new Dictionary<string, int>
        {
            ["valheim.session/join"] = 1, ["valheim.session/leave"] = 1, ["valheim.session/save"] = 1, ["valheim.session/state"] = 1, ["valheim.session/teleport-signals"] = 1,
            ["valheim.world/player-support"] = 1, ["valheim.world/player-support-wait"] = 1, ["valheim.world/terrain"] = 1, ["valheim.world/terrain-grid"] = 1,
        }, manifest.Capabilities.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToDictionary());

        string path = Path.Combine(_root, "manifest.json");
        manifest.Write(path);
        var read = CliCapabilityManifest.Read(path);
        Assert.Equal(manifest.Capabilities, read.Capabilities);
        Assert.Equal((manifest.Plugin.File, manifest.Plugin.Sha256), (read.Plugin.File, read.Plugin.Sha256));

        // A result version other than 1 is the constructor's last argument, recorded as such.
        var future = CliCapabilityManifest.Generate("future", Write(builds, "future", (Core, Builds.Compile(level, "valheimCLI", Builds.PluginSource("1.2.0", gridVersion: 2), builds.BepInEx))));
        Assert.Equal(2, future.Capabilities["valheim.world/terrain-grid"]);
        // A file that declares no plugin is not ValheimCLI.
        Assert.Contains("declares no [BepInPlugin]", Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Generate("x", Write(builds, "stub", ("BepInEx.dll", builds.BepInEx)))).Message);
    }

    // The old split build (a core plus first-party packs) is not one plugin: the generator refuses it as Read does.
    [Fact] public void AnOldSplitBuildIsNotOneValheimCliBuild()
    {
        var builds = new Builds();
        var error = Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Generate("split", Write(builds, "split", (Core, builds.SplitCore), (Standard, builds.Pack))));
        Assert.Contains("These files are not one ValheimCLI build: files lists valheimCLI.dll (valheimCLI.valheimCLI), Valheim.Cli.Standard.dll (valheimCLI.standard): not a one-plugin build", error.Message);
        // A pack alone is not the plugin either.
        Assert.Contains("files lists Valheim.Cli.Standard.dll (valheimCLI.standard): not a one-plugin build",
            Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Generate("pack", Write(builds, "pack", (Standard, builds.Pack)))).Message);
    }

    [Fact] public void ThePluginMustProvideEveryRequiredCommandWithResultVersionOne()
    {
        var builds = new Builds();
        var manifest = CliCapabilityManifest.Generate("pinned build", Write(builds, "require", (Core, builds.Current)));
        manifest.RequireCapabilities(["valheim.world/terrain", "valheim.session/state"]);
        Assert.Contains("lacks valheim.world/missing", Assert.Throws<InvalidOperationException>(() => manifest.RequireCapabilities(["valheim.world/terrain", "valheim.world/missing"])).Message);
        var future = CliCapabilityManifest.Generate("future", Write(builds, "require-future", (Core, Builds.Compile(OptimizationLevel.Release, "valheimCLI", Builds.PluginSource("1.2.0", gridVersion: 2), builds.BepInEx))));
        Assert.Contains("valheim.world/terrain-grid (result version 2; the runner needs 1)", Assert.Throws<InvalidOperationException>(() => future.RequireCapabilities(["valheim.world/terrain-grid"])).Message);
        Assert.Contains("owner/command", Assert.Throws<ArgumentException>(() => manifest.RequireCapabilities(["valheim.session"])).Message);
    }

    [Fact] public void ArrivalCapabilitiesRefuseAPreviousBuildBeforeLaunch()
    {
        var builds = new Builds();
        using var current = Staged(builds, (Core, builds.Current));
        var plan = PlanFor(current, Manifest(builds));
        plan.Capabilities = [.. PlayerPlacement.ArrivalCapabilities]; // what an arriving run lists to have it checked before launch
        var checkedSet = plan.CheckCliManifest()!;
        Assert.Contains(CliCapabilities.TeleportSignals, checkedSet.Capabilities);
        Assert.Contains("valheim.world/player-support-wait", checkedSet.Capabilities);

        using var previous = Staged(builds, (Core, builds.Previous));
        var previousPlan = PlanFor(previous, Manifest(builds, "previous build", builds.Previous));
        previousPlan.Capabilities = [.. PlayerPlacement.ArrivalCapabilities];
        Assert.Contains(CliCapabilities.TeleportSignals,
            Assert.Throws<InvalidOperationException>(() => previousPlan.CheckCliManifest()).Message);
    }

    // #808: a campaign profile on a Windows client host reached over SSH is checked on that host, before launch. Its files are only
    // on the host: the runner's disk has nothing at the profile's path, so a check that read it here would refuse every remote client.
    [Fact] public void ARemoteProfilesManifestCheckReadsItsPluginOnItsHost()
    {
        var builds = new Builds();
        var host = new FakeServerHost("client-host", Path.Combine(_root, "mirror"), windows: true);
        const string root = @"C:\vt-808\runtime";
        string plugin = host.Local(HostPath.Join(root, "BepInEx/plugins/" + Core));
        void OnHost(string relative, byte[] image)
        {
            string path = host.Local(HostPath.Join(root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, image);
        }
        OnHost("BepInEx/plugins/" + Core, builds.Current);
        OnHost("BepInEx/plugins/Mod/BepInEx.dll", builds.BepInEx); // A DLL that is not ValheimCLI is not judged.
        var plan = new ClientRunPlan
        {
            Mode = "owned", Install = root, CliManifest = Manifest(builds), Prepared = true,
            PreparedCliCheck = (manifest, capabilities) => InstallSelection.CheckCliManifestAsync(host, root, manifest, capabilities,
                TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult(),
        };
        Assert.False(Directory.Exists(plan.LoaderRoot)); // Nothing of the profile is on this machine.

        var check = plan.CheckCliManifest(PlayerPlacement.ArrivalCapabilities)!;
        Assert.Equal(new[] { "BepInEx/plugins/valheimCLI.dll" }, check.Files);
        Assert.Equal(PlayerPlacement.ArrivalCapabilities.Order(StringComparer.Ordinal), check.Capabilities.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "list", "fetch" }, host.Scripts); // One listing, then only the plugins folder is fetched: there is no scripts folder.

        // A previous build on the host lacks the arrival's teleport signals: refused, as another build.
        File.WriteAllBytes(plugin, builds.Previous);
        var previous = Assert.Throws<InvalidOperationException>(() => plan.CheckCliManifest(PlayerPlacement.ArrivalCapabilities)).Message;
        Assert.Contains("another build", previous);
        Assert.Contains(CliCapabilities.TeleportSignals, previous);

        // The plugin missing on the host: refused, naming where it was looked for.
        File.Delete(plugin);
        Assert.Contains(@"valheimCLI.dll (plugin valheimCLI.valheimCLI) is not installed in BepInEx/plugins or BepInEx/scripts of C:\vt-808\runtime on client-host",
            Assert.Throws<InvalidOperationException>(() => plan.CheckCliManifest(PlayerPlacement.ArrivalCapabilities)).Message);
    }

    [Fact] public void TheLiveListingOfAClientThatLoadedThePluginConfirmsTheManifest()
    {
        var builds = new Builds();
        var files = Write(builds, "build", (Core, builds.Current));
        var live = new ScriptedTransport()
            .Extension("valheim.session", "state", _ => new { }).Extension("valheim.session", "join", _ => new { }).Extension("valheim.session", "leave", _ => new { }).Extension("valheim.session", "save", _ => new { })
            .Extension("valheim.session", "teleport-signals", _ => new { })
            .Extension("cli.standard", "commands", _ => new { }).Extension("mymod.testing", "session", _ => new { }) // Owners the plugin does not register are not compared.
            .Extension("valheim.world", "terrain", _ => new { }).Extension("valheim.world", "player-support", _ => new { }).Extension("valheim.world", "player-support-wait", _ => new { }).Extension("valheim.world", "terrain-grid", _ => new { });
        string listing = live.Execute("cli_extensions", TimeSpan.FromSeconds(1)).Output[0];
        Assert.StartsWith("EXTENSIONS ", listing);
        Assert.Equal(9, CliCapabilityManifest.Generate("checked build", files, listing).Capabilities.Count);
        // A listing from another build: the game lacks a command the IL registers.
        var other = new ScriptedTransport().Extension("valheim.session", "state", _ => new { })
            .Extension("valheim.world", "terrain", _ => new { }).Extension("valheim.world", "player-support", _ => new { }).Extension("valheim.world", "terrain-grid", _ => new { }, resultVersion: 2);
        var error = Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Generate("checked build", files, other.Execute("cli_extensions", TimeSpan.FromSeconds(1)).Output[0]));
        Assert.Contains("valheim.session/join (in valheimCLI.dll) is not live", error.Message);
        Assert.Contains("valheim.world/terrain-grid is result version 2 live and 1 in valheimCLI.dll", error.Message);
    }

    // ---- the static check of an owned install ----

    [ClientHostFact] public void TheManifestsPluginPassesTheStaticCheckAndThenTheLiveCheck()
    {
        var builds = new Builds();
        using var install = Staged(builds, ("valheimCLI/" + Core, builds.Current));
        var plan = PlanFor(install, Manifest(builds), "plugins/valheimCLI/" + Core);
        plan.Capabilities = ["valheim.world/terrain"];
        plan.Validate("my.mod");
        plan.Preflight(CliCapabilities.HostedRounds);
        var check = plan.CheckCliManifest(CliCapabilities.HostedRounds)!;
        Assert.Equal(new[] { "BepInEx/plugins/valheimCLI/valheimCLI.dll" }, check.Files);
        Assert.Equal(new[] { "valheim.world/terrain", "valheim.session/state", "valheim.session/save", "valheim.session/leave" }, check.Capabilities);
        Assert.Equal("static and live", plan.CliPreflight);

        // Then the live check, which stays authoritative: the declared capabilities are required once the client answers.
        var game = new ScriptedTransport().Extension("valheim.world", "terrain", _ => new { });
        using (ClientSession.Launch(plan, _root, () => new FakeOwnedProcess(99), () => game, (_, _) => Task.CompletedTask)) { }
        var process = new FakeOwnedProcess(99);
        var error = Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _root, () => process, () => new ScriptedTransport(), (_, _) => Task.CompletedTask));
        Assert.Contains("lacks valheim.world/terrain", error.Message);
        Assert.Equal(1, process.Stops); // A failed live check stops the client it started.
    }

    [Fact] public void AModAdapterExtensionIsDeferredToTheLiveCheckNotRequiredOfValheimCli()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Current));
        var plan = PlanFor(install, Manifest(builds, "client with adapter"));
        plan.Capabilities = ["mymod.testing/markers"];
        var checkedSet = plan.CheckCliManifest()!;
        Assert.DoesNotContain("mymod.testing/markers", checkedSet.Capabilities);
        var error = Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _root,
            () => new FakeOwnedProcess(99), () => new ScriptedTransport(), (_, _) => Task.CompletedTask));
        Assert.Contains("mymod.testing/markers", error.Message);
    }

    [ClientHostFact] public void TheJotunnRunsMonolithicValheimCliFailsBeforeLaunchNamingWhatTheRunNeeds()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Monolithic));
        var plan = PlanFor(install, Manifest(builds));
        var error = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains($"BepInEx/plugins/valheimCLI.dll, declaring valheimCLI.valheimCLI, has SHA256 {Hash(builds.Monolithic)}, not the manifest's valheimCLI.dll ({Hash(builds.Current)}): another build of valheimCLI.dll (plugin valheimCLI.valheimCLI)", error.Message);
        Assert.Contains("The run needs valheim.session/state, valheim.session/save, valheim.session/leave from valheimCLI.dll (plugin valheimCLI.valheimCLI)", error.Message);
        // Before Valheim starts: the launch refuses in its install checks, before the CLI port, Steam or any process.
        Assert.Contains("another build of valheimCLI.dll", Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _root)).Message);
        Assert.False(File.Exists(Path.Combine(_root, "client-process.json")));

        // A manifest of the monolithic build itself is honest about it: no extension commands at all.
        plan.CliManifest = Manifest(builds, "monolithic", builds.Monolithic);
        var own = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains("lacks valheim.session/state, valheim.session/save, valheim.session/leave", own.Message);
        Assert.Contains("plugin registers no extension commands", own.Message);
        Assert.Contains("valheim.session comes from the default valheimCLI.dll", own.Message);
    }

    [ClientHostFact] public void TheEpicLootRunsStalePluginFailsBeforeTheClientStarts()
    {
        var builds = new Builds();
        // The right file name and plugin GUID, from an older generation without valheim.session.
        using var install = Staged(builds, (Core, builds.Older));
        var plan = PlanFor(install, Manifest(builds)); // Pinned, as the run did: its hash matched what was staged.
        var hosted = new HostedFixture(_root);
        plan.HostWorld = hosted.Plan;
        plan.Port = 5556; plan.Join = "";
        var report = new ScenarioReport("host");
        int opens = 0;
        var rounds = new ClientRounds { Client = plan, Report = report, Output = hosted.Output };
        var error = Assert.Throws<InvalidOperationException>(() => rounds.Run(() => { opens++; throw new InvalidOperationException("never opened"); }, _ => { }));
        Assert.Equal(0, opens); // The client never launched.
        Assert.False(Directory.EnumerateFileSystemEntries(hosted.Worlds).Any()); // Nor was the fixture copied.
        Assert.Equal("preflight the fixture world and the owned client's install, before anything is copied or started", Assert.Single(report.Steps).Name);
        Assert.Equal("static and live", report.Provenance["cliPreflight"]);
        Assert.Contains($"BepInEx/plugins/valheimCLI.dll, declaring valheimCLI.valheimCLI, has SHA256 {Hash(builds.Older)}, not the manifest's valheimCLI.dll ({Hash(builds.Current)})", error.Message);
        Assert.Contains("The run needs valheim.session/state, valheim.session/save, valheim.session/leave from valheimCLI.dll (plugin valheimCLI.valheimCLI)", error.Message);

        // A manifest generated from the stale plugin itself names the capability it lacks and where it comes from.
        plan.CliManifest = Manifest(builds, "stale", builds.Older);
        var stale = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains("lacks valheim.session/state, valheim.session/save, valheim.session/leave", stale.Message);
        Assert.Contains("valheim.session comes from the default valheimCLI.dll", stale.Message);
    }

    [ClientHostFact] public void ARenamedOlderBuildBesideThePluginIsRefused()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Current));
        var plan = PlanFor(install, Manifest(builds));
        plan.Preflight(CliCapabilities.HostedRounds);
        // A renamed older build beside the current one declares the same plugin: BepInEx would load one of the two.
        install.Add("BepInEx/plugins/old/valheimCLI-old.dll", builds.Older);
        var renamed = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains($"BepInEx/plugins/old/valheimCLI-old.dll, declaring valheimCLI.valheimCLI, has SHA256 {Hash(builds.Older)}, not the manifest's valheimCLI.dll", renamed.Message);
        File.Delete(Path.Combine(install.Root, "BepInEx", "plugins", "old", "valheimCLI-old.dll"));
        plan.Preflight(CliCapabilities.HostedRounds); // Negative control: the manifest's plugin passes.
    }

    [Fact] public void ReplacingThePluginWithoutItsHashInvalidatesTheManifest()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Current));
        var manifest = CliCapabilityManifest.Read(Manifest(builds));
        Assert.Equal(new[] { "BepInEx/plugins/valheimCLI.dll" }, manifest.Check(install.Root, CliCapabilities.HostedRounds).Files);
        // A rebuilt plugin with the same name and GUID: another build, refused.
        var rebuilt = Builds.Compile(OptimizationLevel.Release, "valheimCLI", Builds.PluginSource("1.2.0") + "\nnamespace valheimCLI { internal static class Rebuilt { internal const string Note = \"another build\"; } }", builds.BepInEx);
        install.Add("BepInEx/plugins/" + Core, rebuilt);
        var error = Assert.Throws<InvalidOperationException>(() => manifest.Check(install.Root, CliCapabilities.HostedRounds));
        Assert.Contains($"BepInEx/plugins/valheimCLI.dll, declaring valheimCLI.valheimCLI, has SHA256 {Hash(rebuilt)}, not the manifest's valheimCLI.dll ({Hash(builds.Current)})", error.Message);
        // A file of that name that is not even an assembly is named by its path.
        install.Add("BepInEx/plugins/" + Core, System.Text.Encoding.UTF8.GetBytes("not an assembly"));
        Assert.Contains("BepInEx/plugins/valheimCLI.dll has SHA256", Assert.Throws<InvalidOperationException>(() => manifest.Check(install.Root, CliCapabilities.HostedRounds)).Message);
    }

    // ValheimCLI is one plugin: a leftover first-party pack (any other valheimCLI.* plugin) would load beside it.
    [Fact] public void ALeftoverFirstPartyPackBesideThePluginIsRefused()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Current), (Standard, builds.Pack));
        var manifest = CliCapabilityManifest.Read(Manifest(builds));
        var error = Assert.Throws<InvalidOperationException>(() => manifest.Check(install.Root, CliCapabilities.HostedRounds));
        Assert.Contains("BepInEx/plugins/Valheim.Cli.Standard.dll declares ValheimCLI plugin valheimCLI.standard beside valheimCLI.dll (plugin valheimCLI.valheimCLI); ValheimCLI is that one plugin, so remove it", error.Message);
        Assert.Contains("with no other ValheimCLI plugin", error.Message);
        File.Delete(Path.Combine(install.Root, "BepInEx", "plugins", Standard));
        Assert.Equal(new[] { "BepInEx/plugins/valheimCLI.dll" }, manifest.Check(install.Root, CliCapabilities.HostedRounds).Files); // Negative control.
    }

    [Fact] public void AMissingOrDuplicatedPluginIsNamed()
    {
        var builds = new Builds();
        using var install = Staged(builds);
        var manifest = CliCapabilityManifest.Read(Manifest(builds));
        var error = Assert.Throws<InvalidOperationException>(() => manifest.Check(install.Root, CliCapabilities.HostedRounds));
        Assert.Contains("valheimCLI.dll (plugin valheimCLI.valheimCLI) is not installed in BepInEx/plugins or BepInEx/scripts", error.Message);
        Assert.Contains("The run needs valheim.session/state, valheim.session/save, valheim.session/leave from valheimCLI.dll (plugin valheimCLI.valheimCLI)", error.Message);
        install.Add("BepInEx/plugins/" + Core, builds.Current);
        install.Add("BepInEx/scripts/" + Core, builds.Current);
        Assert.Contains("valheimCLI.dll (plugin valheimCLI.valheimCLI) is installed 2 times (BepInEx/plugins/valheimCLI.dll, BepInEx/scripts/valheimCLI.dll)",
            Assert.Throws<InvalidOperationException>(() => manifest.Check(install.Root, CliCapabilities.HostedRounds)).Message);
    }

    // The one static check of a ValheimCLI plugin (#296): each wrong shape is one message, from CliCapabilityManifest.Locate,
    // whether an owned client's install, a dependency lock or the check itself asks.
    [Theory]
    [InlineData("missing")]
    [InlineData("twice")]
    [InlineData("another build")]
    [InlineData("other plugins")]
    public void EachWrongShapeIsOneMessageFromTheOneCheck(string shape)
    {
        var builds = new Builds();
        var manifest = CliCapabilityManifest.Read(Manifest(builds));
        var files = new List<(string Relative, byte[] Image)> { (Core, builds.Current) };
        string expected;
        switch (shape)
        {
            case "missing":
                files.Clear();
                expected = "valheimCLI.dll (plugin valheimCLI.valheimCLI) is not installed in ";
                break;
            case "twice":
                files.Add(("copy/" + Core, builds.Current));
                expected = "valheimCLI.dll (plugin valheimCLI.valheimCLI) is installed 2 times";
                break;
            case "another build":
                files[0] = (Core, builds.Older);
                expected = "another build of valheimCLI.dll (plugin valheimCLI.valheimCLI)";
                break;
            default: // the manifest's hash is of a DLL that declares another plugin: it was generated from another build
                files[0] = (Core, builds.Renamed);
                manifest.Plugin.Sha256 = Hash(builds.Renamed);
                expected = "has the manifest's SHA256 for valheimCLI.dll but declares valheimCLI.renamed, not valheimCLI.valheimCLI";
                break;
        }
        string folder = Path.Combine(_root, "set-" + Guid.NewGuid().ToString("N"));
        var paths = files.Select(file =>
        {
            string path = Path.Combine(folder, file.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Image);
            return path;
        }).ToList();

        // The check itself.
        Assert.Contains(manifest.Locate(paths, "in the folder").Problems, problem => problem.Message.Contains(expected, StringComparison.Ordinal));
        // A source folder only the located file is copied from (the resolver): another build there is a missing file only
        // when it is the one of that name; a stray copy beside the exact one is not judged.
        var source = manifest.Locate(paths, "in the folder", othersLoad: false).Problems.Select(problem => problem.Message).ToList();
        if (shape == "another build") Assert.Contains(source, message => message.Contains("valheimCLI.dll (plugin valheimCLI.valheimCLI) is not installed in the folder; ", StringComparison.Ordinal) && message.Contains("another build", StringComparison.Ordinal));
        else Assert.Contains(source, message => message.Contains(expected, StringComparison.Ordinal));
        // An owned client's install (ClientRunPlan.Preflight -> CliCapabilityManifest.Check).
        using (var install = Staged(builds, [.. files]))
            Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => manifest.Check(install.Root, CliCapabilities.HostedRounds)).Message);
        // A dependency lock holds exactly the one plugin, which the same check judges.
        var dependencyLock = new NativeDependencyLock { CliManifest = manifest, CliFiles = paths.Select(path => new NativeDependencyFile(path, FileHash.Sha256(path), "test")).ToList() };
        string refusal = Assert.Throws<InvalidDataException>(dependencyLock.RequireExactCliSet).Message;
        Assert.Contains(paths.Count == 1 ? expected : "must contain exactly one pinned valheimCLI.dll", refusal);
    }

    [ClientHostFact] public void ThePluginInScriptsMustLoadAtStartup()
    {
        var builds = new Builds();
        using var install = Staged(builds);
        install.Add("BepInEx/scripts/" + Core, builds.Current);
        var plan = PlanFor(install, Manifest(builds), "scripts/" + Core);
        Assert.Contains("does not pin ScriptEngine", Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds)).Message);

        install.Add("BepInEx/plugins/ScriptEngine.dll", "script engine");
        plan.Pins[OwnedClientPreflight.ScriptEngine] = OwnedRunPreflightTests.Md5("script engine");
        Assert.Contains("LoadOnStart defaults to false", Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds)).Message);
        install.Add("BepInEx/config/com.bepis.bepinex.scriptengine.cfg", "[General]\nLoadOnStart = true\n");
        plan.Preflight(CliCapabilities.HostedRounds);
    }

    // ---- the policy without a manifest, and for attached clients ----

    [ClientHostFact] public void InPlaceWithoutAManifestOnlyTheLiveCheckRunsAndTheReportSaysSo()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Monolithic)); // Nothing statically checks it without a manifest.
        var plan = PlanFor(install, manifest: null);
        Assert.True(plan.InPlace);
        plan.Preflight(CliCapabilities.HostedRounds);
        Assert.Null(plan.CheckCliManifest(CliCapabilities.HostedRounds));
        Assert.Equal("live only: static check not run: your install, run in place", plan.CliPreflight);
        // A named manifest that is missing or malformed is refused, never treated as absent.
        plan.CliManifest = Path.Combine(_root, "missing.json");
        Assert.Contains("does not exist", Assert.Throws<FileNotFoundException>(() => plan.Preflight()).Message);
        File.WriteAllText(plan.CliManifest, "{\"schema\":1,\"build\":\"x\",\"files\":[]}");
        Assert.Contains("files lists no DLL: not a one-plugin build", Assert.Throws<InvalidDataException>(() => plan.Preflight()).Message);
        plan.CliManifest = "manifest.json";
        Assert.Contains("full path", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
    }

    [Fact] public void AnAttachedClientGetsOnlyTheLiveCheck()
    {
        var plan = new ClientRunPlan
        {
            Mode = "attach", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
            Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) }, Capabilities = [.. CliCapabilities.HostedRounds],
        };
        plan.Validate();
        plan.Preflight(); // Reads nothing: the client's files are its operator's.
        Assert.Null(plan.CheckCliManifest());
        Assert.Equal("live only: an attached client's files are its operator's", plan.CliPreflight);
        var older = new ScriptedTransport().Extension("valheim.session", "state", _ => new { });
        Assert.Contains("lacks valheim.session/save, valheim.session/leave", Assert.Throws<InvalidOperationException>(() => ClientSession.Attach(plan, _root, older)).Message);
        var current = new ScriptedTransport().Extension("valheim.session", "state", _ => new { }).Extension("valheim.session", "save", _ => new { }).Extension("valheim.session", "leave", _ => new { });
        using (ClientSession.Attach(plan, _root, current)) { }
        plan.Capabilities = [.. plan.Capabilities, .. PlayerPlacement.ArrivalCapabilities];
        var missingSignal = new ScriptedTransport().Extension("valheim.session", "state", _ => new { })
            .Extension("valheim.session", "save", _ => new { }).Extension("valheim.session", "leave", _ => new { })
            .Extension("valheim.world", "player-support-wait", _ => new { });
        Assert.Contains(CliCapabilities.TeleportSignals,
            Assert.Throws<InvalidOperationException>(() => ClientSession.Attach(plan, _root, missingSignal)).Message);
        var signal = new ScriptedTransport().Extension("valheim.session", "state", _ => new { })
            .Extension("valheim.session", "save", _ => new { }).Extension("valheim.session", "leave", _ => new { })
            .Extension("valheim.world", "player-support-wait", _ => new { }).Extension("valheim.world", "player-support", _ => new { })
            .Extension("valheim.session", "teleport-signals", _ => new { });
        using (ClientSession.Attach(plan, _root, signal)) { }
        plan.CliManifest = Path.Combine(_root, "manifest.json");
        Assert.Contains("leave out cliManifest", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
    }

    [Fact] public void ManifestsAndCapabilityNamesAreChecked()
    {
        var builds = new Builds();
        var good = CliCapabilityManifest.Generate("build", Write(builds, "build", (Core, builds.Current)));
        string path = Path.Combine(_root, "m.json");
        foreach (var (edit, problem) in new (Action<CliCapabilityManifest>, string)[]
        {
            (m => m.Schema = 2, "this toolkit reads schema 1"),
            (m => m.Plugin.Sha256 = "abc", "sha256 is the DLL's full SHA256"),
            (m => m.Plugin.File = "plugins/" + Core, "files lists plugins/valheimCLI.dll (valheimCLI.valheimCLI): not a one-plugin build"),
            (m => m.Plugin.Plugins = [], "files lists valheimCLI.dll (no plugin): not a one-plugin build"),
            (m => m.Plugin.Plugins = ["valheimCLI.valheimCLI", "valheimCLI.standard"], "not a one-plugin build"),
            (m => m.Plugin.Extensions["valheim session"] = new(StringComparer.Ordinal) { ["state"] = 1 }, "\"valheim session\" is not an extension owner name"),
            (m => m.Plugin.Extensions["valheim.session"]["state"] = 0, "valheim.session/state needs a command name and a result version of 1 or more"),
            (m => m.Build = "", "Name the build"),
        })
        {
            var copy = JsonSerializer.Deserialize<CliCapabilityManifest>(JsonSerializer.Serialize(good))!;
            edit(copy);
            File.WriteAllText(path, JsonSerializer.Serialize(copy, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Assert.Contains(problem, Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Read(path)).Message);
            Assert.Contains(problem, Assert.Throws<ArgumentException>(() => copy.Write(path)).Message); // Write checks what Read does.
        }
        File.WriteAllText(path, "{\"schema\":1,\"build\":\"x\",\"files\":[],\"extra\":true}");
        Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Read(path));
        Assert.Throws<ArgumentException>(() => good.Check(_root, ["valheim.session"]));
        var plan = new ClientRunPlan { Mode = "attach", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester", Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) } };
        foreach (var bad in new[] { new[] { "valheim.session" }, ["valheim.session/state", "valheim.session/state"], ["valheim.session/sta te"], ["/state"] })
        {
            plan.Capabilities = bad;
            Assert.Contains("owner/command", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
        }
    }

    // ---- helpers ----

    private static string Hash(byte[] image) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant();

    private string[] Write(Builds builds, string folder, params (string File, byte[] Image)[] files)
    {
        string directory = Path.Combine(_root, folder + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return files.Select(file => { string path = Path.Combine(directory, file.File); File.WriteAllBytes(path, file.Image); return path; }).ToArray();
    }

    /// <summary>The manifest of <paramref name="plugin"/>, the current build by default, written beside the test's files.</summary>
    private string Manifest(Builds builds, string build = "valheimCLI test build", byte[]? plugin = null)
    {
        string path = Path.Combine(_root, "manifest-" + Guid.NewGuid().ToString("N") + ".json");
        CliCapabilityManifest.Generate(build, Write(builds, build, (Core, plugin ?? builds.Current))).Write(path);
        return path;
    }

    /// <summary>A passing owned install whose <c>BepInEx/plugins</c> holds <paramref name="files"/> instead of the text core.</summary>
    private static PreflightInstall Staged(Builds builds, params (string Relative, byte[] Image)[] files)
    {
        var install = PreflightInstall.Create();
        File.Delete(Path.Combine(install.Root, "BepInEx", "plugins", Core));
        foreach (var (relative, image) in files) install.Add("BepInEx/plugins/" + relative, image);
        return install;
    }

    /// <summary>The install's plan, with the ValheimCLI plugin installed at <paramref name="pinned"/> (under <c>BepInEx</c>) as an exact pin.</summary>
    private static ClientRunPlan PlanFor(PreflightInstall install, string? manifest, string pinned = "plugins/" + Core)
    {
        var plan = install.Plan();
        plan.Pins["valheimCLI.valheimCLI"] = FileHash.Md5(Path.Combine(install.Root, "BepInEx", pinned));
        plan.CliManifest = manifest;
        return plan;
    }

    /// <summary>A pinned hosted fixture world and an empty local worlds folder for the hosted <see cref="ClientRounds"/>.</summary>
    private sealed class HostedFixture
    {
        public string Worlds { get; }
        public string Output { get; }
        public HostWorldPlan Plan { get; }
        public HostedFixture(string root)
        {
            string fixture = FakeInstalls.World(Path.Combine(root, "fixture"), "HostFixture", 4242);
            string save = Path.Combine(root, "client-data");
            Worlds = Directory.CreateDirectory(Path.Combine(save, "worlds_local")).FullName;
            Output = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
            Plan = new() { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242", SaveDirectory = save };
        }
    }

    /// <summary>
    /// The DLLs of a few ValheimCLI generations, compiled once per test from source in the shape of the real ones: a stub
    /// BepInEx; the current one-plugin build registering <c>valheim.session</c> and <c>valheim.world</c> beside its console-module
    /// host, a previous one without <c>teleport-signals</c>, an older one without <c>valheim.session</c> (the Epic Loot run's), one
    /// declaring another GUID, and a monolithic ValheimCLI with no extension API; and an old split build (a core with no commands
    /// and a Standard pack registering through it), which no manifest may describe.
    /// </summary>
    private sealed class Builds
    {
        public byte[] BepInEx { get; }
        public byte[] Current { get; }
        public byte[] Previous { get; }
        public byte[] Older { get; }
        public byte[] Renamed { get; }
        public byte[] Monolithic { get; }
        public byte[] SplitCore { get; }
        public byte[] Pack { get; }

        public Builds(OptimizationLevel level = OptimizationLevel.Release)
        {
            BepInEx = Compile(level, "BepInEx", """
                using System;
                namespace BepInEx
                {
                    [AttributeUsage(AttributeTargets.Class)] public sealed class BepInPlugin : Attribute { public BepInPlugin(string GUID, string Name, string Version) { } }
                    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] public sealed class BepInDependency : Attribute { public BepInDependency(string DependencyGUID, string MinimumDependencyVersion) { } }
                    public abstract class BaseUnityPlugin { }
                }
                """);
            Current = Compile(level, "valheimCLI", PluginSource("1.2.0"), BepInEx);
            Previous = Compile(level, "valheimCLI", PluginSource("1.1.5", signals: false), BepInEx);
            Older = Compile(level, "valheimCLI", PluginSource("1.0.9", session: false), BepInEx);
            Renamed = Compile(level, "valheimCLI", CoreSource("1.2.0", "valheimCLI.renamed"), BepInEx);
            Monolithic = Compile(level, "valheimCLI", """
                using BepInEx;
                namespace valheimCLI
                {
                    [BepInPlugin("valheimCLI.valheimCLI", "valheimCLI", "1.0.0")]
                    public sealed class valheimCLIPlugin : BaseUnityPlugin
                    {
                        public static readonly string[] Commands = { "cli_expect", "cli_world", "cli_manifest" };
                    }
                }
                """, BepInEx);
            SplitCore = Compile(level, "valheimCLI", CoreSource("1.1.0"), BepInEx);
            Pack = Compile(level, "Valheim.Cli.Standard", StandardSource(), BepInEx, SplitCore);
        }

        /// <summary>The one-plugin build: the core with its built-in commands registered in the same assembly.</summary>
        internal static string PluginSource(string version, bool session = true, bool signals = true, int gridVersion = 1) =>
            CoreSource(version) + $$"""

            namespace valheimCLI
            {
            {{(session ? signals ? SessionSource : SessionSource.Replace(TeleportCommand, "", StringComparison.Ordinal) : "")}}
            {{WorldSource(gridVersion)}}
            }
            """;

        internal static string CoreSource(string version, string guid = "valheimCLI.valheimCLI") => $$"""
            using System;
            using System.Collections;
            using BepInEx;
            using valheimCLI.Extensions;
            namespace valheimCLI.Extensions
            {
                public enum ExtensionRole { Any, Client, Server }
                public sealed class ExtensionContext { }
                public sealed class ExtensionRegistration { }
                public sealed class ExtensionCommand
                {
                    public ExtensionCommand(string name, string help, Func<ExtensionContext, IEnumerator> execute,
                        bool readOnly = false, ExtensionRole role = ExtensionRole.Any, bool needsWorld = false, int resultVersion = 1) { }
                }
                public sealed class ExtensionRegistry
                {
                    public const int ApiVersion = 1;
                    public ExtensionRegistration Register(string id, string version, int apiVersion, params ExtensionCommand[] commands) => new ExtensionRegistration();
                }
                public sealed class ConsoleModuleHost
                {
                    private readonly ExtensionRegistry _registry;
                    public ConsoleModuleHost(ExtensionRegistry registry) { _registry = registry; }
                    public ExtensionRegistration Register(string id, string version, Action register) =>
                        _registry.Register(id, version, ExtensionRegistry.ApiVersion, new ExtensionCommand("commands", "List this pack's compatible console commands", Describe, readOnly: true));
                    private static IEnumerator Describe(ExtensionContext context) { yield break; }
                }
            }
            namespace valheimCLI
            {
                [BepInPlugin("{{guid}}", "valheimCLI", "{{version}}")]
                public sealed class valheimCLIPlugin : BaseUnityPlugin
                {
                    public static valheimCLIPlugin Instance;
                    public ExtensionRegistry Extensions = new ExtensionRegistry();
                    public ConsoleModuleHost Modules;
                    public valheimCLIPlugin() { Modules = new ConsoleModuleHost(Extensions); Instance = this; }
                }
            }
            """;

        // The old split build's Standard pack: a separate plugin registering valheim.session through the core.
        private static string StandardSource() => $$"""
            using System.Collections;
            using BepInEx;
            using valheimCLI.Extensions;
            namespace valheimCLI
            {
            {{SessionSource}}
                [BepInPlugin("valheimCLI.standard", "CLI Standard Commands", "0.1.0")]
                [BepInDependency("valheimCLI.valheimCLI", "1.1.0")]
                public sealed class StandardPack : BaseUnityPlugin
                {
                    private void Start()
                    {
                        var core = valheimCLIPlugin.Instance;
                        SessionCapabilities.Register(core.Extensions);
                        core.Modules.Register("cli.standard", "0.1.0", () => { });
                    }
                }
            }
            """;

        private const string TeleportCommand = "new ExtensionCommand(\"teleport-signals\", \"Report bounded teleport commands\", Signals, readOnly: true),";
        private const string SessionSource = """
                internal static class SessionCapabilities
                {
                    internal static ExtensionRegistration Register(ExtensionRegistry registry) => registry.Register("valheim.session", "0.1.0", 1,
                        new ExtensionCommand("state", "Read session facts; mod readiness must be checked separately", State, readOnly: true),
                        new ExtensionCommand("teleport-signals", "Report bounded teleport commands", Signals, readOnly: true),
                        new ExtensionCommand("join", "Join from menu: <host:port> <character> [password-environment-variable]", Join),
                        new ExtensionCommand("leave", "Save the local character and return to the menu", Leave, role: ExtensionRole.Client, needsWorld: true),
                        new ExtensionCommand("save", "Confirm a server world save: [timeout-seconds, 1..600]", Save, role: ExtensionRole.Server, needsWorld: true));
                    private static IEnumerator State(ExtensionContext context) { yield break; }
                    private static IEnumerator Signals(ExtensionContext context) { yield break; }
                    private static IEnumerator Join(ExtensionContext context) { yield break; }
                    private static IEnumerator Leave(ExtensionContext context) { yield break; }
                    private static IEnumerator Save(ExtensionContext context) { yield break; }
                }
            """;

        private static string WorldSource(int gridVersion) => $$"""
                internal static class WorldObservations
                {
                    internal static ExtensionRegistration Register(ExtensionRegistry registry) => registry.Register("valheim.world", "0.1.0", 1,
                        new ExtensionCommand("terrain-grid", "Capture a bounded terrain grid", Read, readOnly: true, needsWorld: true, resultVersion: {{gridVersion}}),
                        new ExtensionCommand("player-support", "Read local player position, motion and grounded state", Read, readOnly: true, role: ExtensionRole.Client, needsWorld: true),
                        new ExtensionCommand("player-support-wait", "Wait for supported arrival", Read, readOnly: true, role: ExtensionRole.Client, needsWorld: true),
                        new ExtensionCommand("terrain", "terrain <x> <z> <generator|loaded-ground>", context => Read(context), readOnly: true, needsWorld: true));
                    private static IEnumerator Read(ExtensionContext context) { yield break; }
                }
            """;

        // Only the core library's types are referenced; the old pack references the stub BepInEx and its core, as real packs did.
        private static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

        internal static byte[] Compile(OptimizationLevel level, string name, string source, params byte[][] references)
        {
            var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
                Framework.Concat(references.Select(r => MetadataReference.CreateFromImage(r))),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: level, deterministic: true));
            using var image = new MemoryStream();
            var result = compilation.Emit(image);
            if (!result.Success)
                throw new InvalidOperationException(name + " did not compile:" + Environment.NewLine + string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return image.ToArray();
        }
    }
}
