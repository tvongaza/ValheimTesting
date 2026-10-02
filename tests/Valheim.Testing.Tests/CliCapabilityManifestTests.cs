using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The static ValheimCLI capability check (#124): an owned client's core and packs, compared by SHA256 with a manifest of one
// coherent build, refused before the game starts when they are another generation. The DLLs are small assemblies compiled
// here with Roslyn in the shape of ValheimCLI's own registrations (a stub BepInEx, a core with the extension API, packs that
// register through it); no ValheimCLI, game or BepInEx assembly is used. No game, Steam or network connection.
public sealed class CliCapabilityManifestTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cli-manifest-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Core = "valheimCLI.dll", Standard = "Valheim.Cli.Standard.dll", WorldTools = "Valheim.Cli.WorldTools.dll";

    // ---- the generator: what each DLL registers, read from its metadata and IL ----

    [Theory]
    [InlineData(OptimizationLevel.Release)]
    [InlineData(OptimizationLevel.Debug)]
    public void TheManifestOfABuildIsReadFromEachDllsOwnRegistrations(OptimizationLevel level)
    {
        var builds = new Builds(level);
        var manifest = CliCapabilityManifest.Generate("valheimCLI test build", Write(builds, "build", (Core, builds.Core), (Standard, builds.Standard), (WorldTools, builds.WorldTools)));
        Assert.Equal(new[] { Core, Standard, WorldTools }, manifest.Files.Select(file => file.File));
        Assert.Equal(new[] { "valheimCLI.valheimCLI" }, manifest.Files[0].Plugins);
        Assert.Empty(manifest.Files[0].Extensions); // The core's own module host passes its id as an argument: nothing claimed.
        Assert.Equal(new[] { "valheimCLI.standard" }, manifest.Files[1].Plugins);
        // The console module (cli.standard/commands) is not a literal registration of the pack: not claimed either.
        Assert.Equal(new[] { "valheim.session/join", "valheim.session/leave", "valheim.session/save", "valheim.session/state" }, manifest.Files[1].Commands().Select(c => c.Path).Order(StringComparer.Ordinal));
        Assert.Equal(new Dictionary<string, int> { ["valheim.world/player-support"] = 1, ["valheim.world/terrain"] = 1, ["valheim.world/terrain-grid"] = 1 },
            manifest.Files[2].Commands().ToDictionary(c => c.Path, c => c.Version));
        Assert.Equal(Hash(builds.Standard), manifest.Files[1].Sha256);

        string path = Path.Combine(_root, "manifest.json");
        manifest.Write(path);
        var read = CliCapabilityManifest.Read(path);
        Assert.Equal(manifest.Capabilities, read.Capabilities);
        Assert.Equal(manifest.Files.Select(f => (f.File, f.Sha256)), read.Files.Select(f => (f.File, f.Sha256)));

        // A result version other than 1 is the constructor's last argument, recorded as such.
        var future = CliCapabilityManifest.Generate("future", Write(builds, "future", (WorldTools, Builds.Compile(level, "Valheim.Cli.WorldTools", Builds.WorldToolsSource(gridVersion: 2), builds.BepInEx, builds.Core))));
        Assert.Equal(2, future.Capabilities["valheim.world/terrain-grid"]);
        // A file that declares no plugin is not part of a ValheimCLI set.
        Assert.Contains("declares no [BepInPlugin]", Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Generate("x", Write(builds, "stub", ("BepInEx.dll", builds.BepInEx)))).Message);
    }

    [Fact] public void RequiredCommandsSelectOnlyTheirProvidingPacksFromOneBuild()
    {
        var builds = new Builds();
        var source = CliCapabilityManifest.Generate("pinned build", Write(builds, "select", (Core, builds.Core), (Standard, builds.Standard), (WorldTools, builds.WorldTools)));
        var selected = source.ForCapabilities(["valheim.world/terrain"]);
        Assert.Equal(new[] { Core, WorldTools }, selected.Files.Select(file => file.File));
        Assert.Equal("pinned build", selected.Build);
        Assert.Equal(source.Files[2].Sha256, selected.Files[1].Sha256);

        var hosted = source.ForCapabilities(["valheim.session/state", "valheim.world/terrain"]);
        Assert.Equal(new[] { Core, Standard, WorldTools }, hosted.Files.Select(file => file.File));
        Assert.Contains("valheim.world/missing", Assert.Throws<InvalidOperationException>(() => source.ForCapabilities(["valheim.world/missing"])).Message);
        var future = CliCapabilityManifest.Generate("future", Write(builds, "select-future", (Core, builds.Core),
            (WorldTools, Builds.Compile(OptimizationLevel.Release, "Valheim.Cli.WorldTools", Builds.WorldToolsSource(gridVersion: 2), builds.BepInEx, builds.Core))));
        Assert.Contains("result version 2", Assert.Throws<InvalidOperationException>(() => future.ForCapabilities(["valheim.world/terrain-grid"])).Message);
    }

    [Fact] public void TheLiveListingOfAClientThatLoadedTheSetConfirmsTheManifest()
    {
        var builds = new Builds();
        var files = Write(builds, "build", (Core, builds.Core), (Standard, builds.Standard), (WorldTools, builds.WorldTools));
        var live = new ScriptedTransport()
            .Extension("valheim.session", "state", _ => new { }).Extension("valheim.session", "join", _ => new { }).Extension("valheim.session", "leave", _ => new { }).Extension("valheim.session", "save", _ => new { })
            .Extension("cli.standard", "commands", _ => new { }).Extension("mymod.testing", "session", _ => new { }) // Owners the files do not register are not compared.
            .Extension("valheim.world", "terrain", _ => new { }).Extension("valheim.world", "player-support", _ => new { }).Extension("valheim.world", "terrain-grid", _ => new { });
        string listing = live.Execute("cli_extensions", TimeSpan.FromSeconds(1)).Output[0];
        Assert.StartsWith("EXTENSIONS ", listing);
        Assert.Equal(7, CliCapabilityManifest.Generate("checked build", files, listing).Capabilities.Count);
        // A listing from another set: the game lacks a command the IL registers.
        var other = new ScriptedTransport().Extension("valheim.session", "state", _ => new { })
            .Extension("valheim.world", "terrain", _ => new { }).Extension("valheim.world", "player-support", _ => new { }).Extension("valheim.world", "terrain-grid", _ => new { }, resultVersion: 2);
        var error = Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Generate("checked build", files, other.Execute("cli_extensions", TimeSpan.FromSeconds(1)).Output[0]));
        Assert.Contains("valheim.session/join (in Valheim.Cli.Standard.dll) is not live", error.Message);
        Assert.Contains("valheim.world/terrain-grid is result version 2 live and 1 in Valheim.Cli.WorldTools.dll", error.Message);
    }

    // ---- the static check of an owned install ----

    [Fact] public void ACoherentSetPassesTheStaticCheckAndThenTheLiveCheck()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Core), ("valheimCLI/" + Standard, builds.Standard), (WorldTools, builds.WorldTools));
        var plan = PlanFor(install, Manifest(builds));
        plan.Capabilities = ["valheim.world/terrain"];
        plan.Validate("my.mod");
        plan.Preflight(CliCapabilities.HostedRounds);
        var check = plan.CheckCliManifest(CliCapabilities.HostedRounds)!;
        Assert.Equal(new[] { "BepInEx/plugins/valheimCLI.dll", "BepInEx/plugins/valheimCLI/Valheim.Cli.Standard.dll", "BepInEx/plugins/Valheim.Cli.WorldTools.dll" }, check.Files);
        Assert.Equal(new[] { "valheim.world/terrain", "valheim.session/state", "valheim.session/save", "valheim.session/leave" }, check.Capabilities);
        Assert.Equal("static (cliManifest) and live", plan.CliPreflight);

        // Then the live check, which stays authoritative: the declared capabilities are required once the client answers.
        var game = new ScriptedTransport().Extension("valheim.world", "terrain", _ => new { });
        using (ClientSession.Launch(plan, _root, () => new Process(), () => game, (_, _) => Task.CompletedTask)) { }
        var process = new Process();
        var error = Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _root, () => process, () => new ScriptedTransport(), (_, _) => Task.CompletedTask));
        Assert.Contains("lacks valheim.world/terrain", error.Message);
        Assert.Equal(1, process.Stops); // A failed live check stops the client it started.
    }

    [Fact] public void TheJotunnRunsMonolithicValheimCliFailsBeforeLaunchNamingTheMissingPack()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Monolithic));
        var plan = PlanFor(install, Manifest(builds));
        var error = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains($"BepInEx/plugins/valheimCLI.dll, declaring valheimCLI.valheimCLI, has SHA256 {Hash(builds.Monolithic)}, not the manifest's valheimCLI.dll ({Hash(builds.Core)}): another build of valheimCLI.dll (plugin valheimCLI.valheimCLI)", error.Message);
        Assert.Contains("Valheim.Cli.Standard.dll (plugin valheimCLI.standard) is not installed", error.Message);
        Assert.Contains("The run needs valheim.session/state, valheim.session/save, valheim.session/leave from Valheim.Cli.Standard.dll (plugin valheimCLI.standard)", error.Message);
        // Before Valheim starts: the launch refuses in its install checks, before the CLI port, Steam or any process.
        Assert.Contains("another build of valheimCLI.dll", Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, _root)).Message);
        Assert.False(File.Exists(Path.Combine(_root, "client-process.json")));

        // A manifest of the monolithic build itself is honest about it: no extension commands at all.
        plan.CliManifest = Manifest(builds, "monolithic", (Core, builds.Monolithic));
        var own = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains("lacks valheim.session/state, valheim.session/save, valheim.session/leave", own.Message);
        Assert.Contains("register no extension commands at all, as an old monolithic ValheimCLI", own.Message);
        Assert.Contains("valheim.session comes from the Standard pack (Valheim.Cli.Standard.dll, plugin valheimCLI.standard)", own.Message);
    }

    [Fact] public void TheEpicLootRunsStaleCoreAndPackFailBeforeTheClientStarts()
    {
        var builds = new Builds();
        // The right file names and plugin GUIDs, from an older generation whose Standard pack had no valheim.session.
        using var install = Staged(builds, (Core, builds.OlderCore), (Standard, builds.OlderStandard), (WorldTools, builds.WorldTools));
        var plan = PlanFor(install, Manifest(builds));
        plan.Pins["valheimCLI.standard"] = PluginPins.Md5(Path.Combine(install.Root, "BepInEx", "plugins", Standard)); // Pinned, as the run did: its hash matched what was staged.
        var hosted = new HostedFixture(_root);
        plan.HostWorld = hosted.Plan;
        plan.Port = 5556; plan.Join = "";
        var report = new ScenarioReport("host");
        int opens = 0;
        var rounds = new HostRounds { Client = plan, Report = report, Output = hosted.Output };
        var error = Assert.Throws<InvalidOperationException>(() => rounds.Run(() => { opens++; throw new InvalidOperationException("never opened"); }, _ => { }));
        Assert.Equal(0, opens); // The client never launched.
        Assert.False(Directory.EnumerateFileSystemEntries(hosted.Worlds).Any()); // Nor was the fixture copied.
        Assert.Equal("preflight the fixture world and the owned client's install, before anything is copied or started", Assert.Single(report.Steps).Name);
        Assert.Equal("static (cliManifest) and live", report.Provenance["cliPreflight"]);
        Assert.Contains($"BepInEx/plugins/valheimCLI.dll, declaring valheimCLI.valheimCLI, has SHA256 {Hash(builds.OlderCore)}", error.Message);
        Assert.Contains($"BepInEx/plugins/Valheim.Cli.Standard.dll, declaring valheimCLI.standard, has SHA256 {Hash(builds.OlderStandard)}, not the manifest's Valheim.Cli.Standard.dll ({Hash(builds.Standard)})", error.Message);
        Assert.Contains("The run needs valheim.session/state, valheim.session/save, valheim.session/leave from Valheim.Cli.Standard.dll (plugin valheimCLI.standard)", error.Message);
        Assert.DoesNotContain("WorldTools", error.Message); // The current World Tools pack is the manifest's.

        // A manifest generated from the stale set itself names the capability the set lacks and the pack that provides it.
        plan.CliManifest = Manifest(builds, "stale", (Core, builds.OlderCore), (Standard, builds.OlderStandard), (WorldTools, builds.WorldTools));
        var stale = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains("lacks valheim.session/state, valheim.session/save, valheim.session/leave", stale.Message);
        Assert.Contains("valheim.session comes from the Standard pack", stale.Message);
    }

    [Fact] public void AMixedSetIsRefusedAsAnotherBuildOfThePack()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Core), (Standard, builds.OlderStandard), (WorldTools, builds.WorldTools));
        var plan = PlanFor(install, Manifest(builds));
        var error = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains("another build of Valheim.Cli.Standard.dll (plugin valheimCLI.standard)", error.Message);
        Assert.DoesNotContain("another build of valheimCLI.dll", error.Message);

        // A renamed older core beside the current one declares the same plugin: BepInEx would load one of the two.
        install.Add("BepInEx/plugins/" + Standard, builds.Standard);
        install.Add("BepInEx/plugins/old/valheimCLI-old.dll", builds.OlderCore);
        var renamed = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains($"BepInEx/plugins/old/valheimCLI-old.dll, declaring valheimCLI.valheimCLI, has SHA256 {Hash(builds.OlderCore)}", renamed.Message);
        File.Delete(Path.Combine(install.Root, "BepInEx", "plugins", "old", "valheimCLI-old.dll"));
        plan.Preflight(CliCapabilities.HostedRounds); // Negative control: the manifest's set passes.
    }

    [Fact] public void ReplacingAnyDeclaredDllWithoutItsHashInvalidatesTheManifest()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Core), (Standard, builds.Standard), (WorldTools, builds.WorldTools));
        var plan = PlanFor(install, Manifest(builds));
        plan.Preflight(CliCapabilities.HostedRounds);
        // A rebuilt World Tools pack with the same name and plugin: the run needs nothing from it, and it is still refused.
        var rebuilt = Builds.Compile(OptimizationLevel.Release, "Valheim.Cli.WorldTools", Builds.WorldToolsSource(gridVersion: 1) + "\nnamespace valheimCLI { internal static class Rebuilt { internal const string Note = \"another build\"; } }", builds.BepInEx, builds.Core);
        install.Add("BepInEx/plugins/" + WorldTools, rebuilt);
        var error = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds));
        Assert.Contains($"BepInEx/plugins/Valheim.Cli.WorldTools.dll, declaring valheimCLI.worldtools, has SHA256 {Hash(rebuilt)}, not the manifest's Valheim.Cli.WorldTools.dll ({Hash(builds.WorldTools)})", error.Message);
        Assert.DoesNotContain("The run needs", error.Message);
        // A file of that name that is not even an assembly is named by its path.
        install.Add("BepInEx/plugins/" + WorldTools, System.Text.Encoding.UTF8.GetBytes("not an assembly"));
        Assert.Contains("BepInEx/plugins/Valheim.Cli.WorldTools.dll has SHA256", Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds)).Message);
    }

    [Fact] public void AMissingOrDuplicatedPackFileIsNamed()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Core), (Standard, builds.Standard));
        var plan = PlanFor(install, Manifest(builds));
        var error = Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds.Append("valheim.world/terrain")));
        Assert.Contains("Valheim.Cli.WorldTools.dll (plugin valheimCLI.worldtools) is not installed in BepInEx/plugins or BepInEx/scripts", error.Message);
        Assert.Contains("The run needs valheim.world/terrain from Valheim.Cli.WorldTools.dll (plugin valheimCLI.worldtools)", error.Message);
        install.Add("BepInEx/plugins/" + WorldTools, builds.WorldTools);
        install.Add("BepInEx/scripts/" + WorldTools, builds.WorldTools);
        Assert.Contains("Valheim.Cli.WorldTools.dll (plugin valheimCLI.worldtools) is installed 2 times (BepInEx/plugins/Valheim.Cli.WorldTools.dll, BepInEx/scripts/Valheim.Cli.WorldTools.dll)",
            Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds)).Message);
    }

    [Fact] public void AManifestPackInScriptsMustLoadAtStartupEvenWhenItIsNotSeparatelyPinned()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Core), (WorldTools, builds.WorldTools));
        install.Add("BepInEx/scripts/" + Standard, builds.Standard);
        var plan = PlanFor(install, Manifest(builds));
        Assert.DoesNotContain("valheimCLI.standard", plan.Pins.Keys);
        Assert.Contains("does not pin ScriptEngine", Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds)).Message);

        install.Add("BepInEx/plugins/ScriptEngine.dll", "script engine");
        plan.Pins[OwnedClientPreflight.ScriptEngine] = OwnedRunPreflightTests.Md5("script engine");
        Assert.Contains("LoadOnStart defaults to false", Assert.Throws<InvalidOperationException>(() => plan.Preflight(CliCapabilities.HostedRounds)).Message);
        install.Add("BepInEx/config/com.bepis.bepinex.scriptengine.cfg", "[General]\nLoadOnStart = true\n");
        plan.Preflight(CliCapabilities.HostedRounds);
    }

    // ---- the policy without a manifest, and for attached clients ----

    [Fact] public void WithoutAManifestOnlyTheLiveCheckRunsAndTheReportSaysSo()
    {
        var builds = new Builds();
        using var install = Staged(builds, (Core, builds.Monolithic)); // Nothing statically checks it without a manifest.
        var plan = PlanFor(install, manifest: null);
        plan.Preflight(CliCapabilities.HostedRounds);
        Assert.Null(plan.CheckCliManifest(CliCapabilities.HostedRounds));
        Assert.Equal("live only: the plan names no cliManifest", plan.CliPreflight);
        // A named manifest that is missing or malformed is refused, never treated as absent.
        plan.CliManifest = Path.Combine(_root, "missing.json");
        Assert.Contains("does not exist", Assert.Throws<FileNotFoundException>(() => plan.Preflight()).Message);
        File.WriteAllText(plan.CliManifest, "{\"schema\":1,\"build\":\"x\",\"files\":[]}");
        Assert.Contains("List the set's DLLs", Assert.Throws<InvalidDataException>(() => plan.Preflight()).Message);
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
        plan.CliManifest = Path.Combine(_root, "manifest.json");
        Assert.Contains("leave out cliManifest", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
    }

    [Fact] public void ManifestsAndCapabilityNamesAreChecked()
    {
        var builds = new Builds();
        var good = CliCapabilityManifest.Generate("build", Write(builds, "build", (Core, builds.Core), (Standard, builds.Standard)));
        string path = Path.Combine(_root, "m.json");
        foreach (var (edit, problem) in new (Action<CliCapabilityManifest>, string)[]
        {
            (m => m.Schema = 2, "this toolkit reads schema 1"),
            (m => m.Files[1].Sha256 = "abc", "sha256 is the DLL's full SHA256"),
            (m => m.Files[1].File = "packs/" + Standard, "bare file name"),
            (m => m.Files[0].Extensions["valheim.session"] = new(StringComparer.Ordinal) { ["state"] = 1 }, "valheim.session is registered by both"),
            (m => m.Files[1].Plugins = [], "plugin GUIDs"),
            (m => m.Build = "", "Name the build"),
        })
        {
            var copy = JsonSerializer.Deserialize<CliCapabilityManifest>(JsonSerializer.Serialize(good))!;
            edit(copy);
            File.WriteAllText(path, JsonSerializer.Serialize(copy, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Assert.Contains(problem, Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Read(path)).Message);
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

    /// <summary>The manifest of <paramref name="files"/>, the current core and packs by default, written beside the test's files.</summary>
    private string Manifest(Builds builds, string build = "valheimCLI test build", params (string File, byte[] Image)[] files)
    {
        if (files.Length == 0) files = [(Core, builds.Core), (Standard, builds.Standard), (WorldTools, builds.WorldTools)];
        string path = Path.Combine(_root, "manifest-" + Guid.NewGuid().ToString("N") + ".json");
        CliCapabilityManifest.Generate(build, Write(builds, build, files)).Write(path);
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

    private static ClientRunPlan PlanFor(PreflightInstall install, string? manifest)
    {
        var plan = install.Plan();
        plan.Pins["valheimCLI.valheimCLI"] = PluginPins.Md5(Path.Combine(install.Root, "BepInEx", "plugins", Core));
        plan.CliManifest = manifest;
        return plan;
    }

    private sealed class Process : IServerProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public int Id => 99;
        public bool HasExited => _exit.Task.IsCompleted;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);
        public void Stop(TimeSpan timeout) { Stops++; _exit.TrySetResult(-1); }
        public void Dispose() { }
    }

    /// <summary>A pinned hosted fixture world and an empty local worlds folder for <see cref="HostRounds"/>.</summary>
    private sealed class HostedFixture
    {
        public string Worlds { get; }
        public string Output { get; }
        public HostWorldPlan Plan { get; }
        public HostedFixture(string root)
        {
            string fixture = Directory.CreateDirectory(Path.Combine(root, "fixture")).FullName;
            File.WriteAllBytes(Path.Combine(fixture, "HostFixture.fwl"), OwnedRunPreflightTests.Metadata("HostFixture", 4242));
            File.WriteAllText(Path.Combine(fixture, "HostFixture.db"), "fixture world");
            string save = Path.Combine(root, "client-data");
            Worlds = Directory.CreateDirectory(Path.Combine(save, "worlds_local")).FullName;
            Output = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
            Plan = new() { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242", SaveDirectory = save };
        }
    }

    /// <summary>
    /// The DLLs of a few ValheimCLI generations, compiled once per test from source in the shape of the real ones: a stub
    /// BepInEx; the current core with the extension API (its console-module host registers with an id it is given) and an
    /// older one; the Standard pack registering <c>valheim.session</c> beside its console module, and an older Standard with
    /// the module only (the Epic Loot run's set); the World Tools pack; and a monolithic ValheimCLI with no extension API.
    /// </summary>
    private sealed class Builds
    {
        public byte[] BepInEx { get; }
        public byte[] Core { get; }
        public byte[] OlderCore { get; }
        public byte[] Standard { get; }
        public byte[] OlderStandard { get; }
        public byte[] WorldTools { get; }
        public byte[] Monolithic { get; }

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
            Core = Compile(level, "valheimCLI", CoreSource("1.1.0"), BepInEx);
            OlderCore = Compile(level, "valheimCLI", CoreSource("1.0.9"), BepInEx);
            Standard = Compile(level, "Valheim.Cli.Standard", StandardSource(session: true), BepInEx, Core);
            OlderStandard = Compile(level, "Valheim.Cli.Standard", StandardSource(session: false), BepInEx, OlderCore);
            WorldTools = Compile(level, "Valheim.Cli.WorldTools", WorldToolsSource(gridVersion: 1), BepInEx, Core);
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
        }

        private static string CoreSource(string version) => $$"""
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
                [BepInPlugin("valheimCLI.valheimCLI", "valheimCLI", "{{version}}")]
                public sealed class valheimCLIPlugin : BaseUnityPlugin
                {
                    public static valheimCLIPlugin Instance;
                    public ExtensionRegistry Extensions = new ExtensionRegistry();
                    public ConsoleModuleHost Modules;
                    public valheimCLIPlugin() { Modules = new ConsoleModuleHost(Extensions); Instance = this; }
                }
            }
            """;

        // The older pack has no session capabilities at all, only its console module.
        private static string StandardSource(bool session) => $$"""
            using System.Collections;
            using BepInEx;
            using valheimCLI.Extensions;
            namespace valheimCLI
            {
            {{(session ? SessionSource : "")}}
                [BepInPlugin("valheimCLI.standard", "CLI Standard Commands", "0.1.0")]
                [BepInDependency("valheimCLI.valheimCLI", "1.1.0")]
                public sealed class StandardPack : BaseUnityPlugin
                {
                    private void Start()
                    {
                        var core = valheimCLIPlugin.Instance;
                        {{(session ? "SessionCapabilities.Register(core.Extensions);" : "")}}
                        core.Modules.Register("cli.standard", "0.1.0", () => { });
                    }
                }
            }
            """;

        private const string SessionSource = """
                internal static class SessionCapabilities
                {
                    internal static ExtensionRegistration Register(ExtensionRegistry registry) => registry.Register("valheim.session", "0.1.0", 1,
                        new ExtensionCommand("state", "Read session facts; mod readiness must be checked separately", State, readOnly: true),
                        new ExtensionCommand("join", "Join from menu: <host:port> <character> [password-environment-variable]", Join),
                        new ExtensionCommand("leave", "Save the local character and return to the menu", Leave, role: ExtensionRole.Client, needsWorld: true),
                        new ExtensionCommand("save", "Confirm a server world save: [timeout-seconds, 1..600]", Save, role: ExtensionRole.Server, needsWorld: true));
                    private static IEnumerator State(ExtensionContext context) { yield break; }
                    private static IEnumerator Join(ExtensionContext context) { yield break; }
                    private static IEnumerator Leave(ExtensionContext context) { yield break; }
                    private static IEnumerator Save(ExtensionContext context) { yield break; }
                }
            """;

        internal static string WorldToolsSource(int gridVersion) => $$"""
            using System.Collections;
            using BepInEx;
            using valheimCLI.Extensions;
            namespace valheimCLI
            {
                internal static class WorldObservations
                {
                    internal static ExtensionRegistration Register(ExtensionRegistry registry) => registry.Register("valheim.world", "0.1.0", 1,
                        new ExtensionCommand("terrain-grid", "Capture a bounded terrain grid", Read, readOnly: true, needsWorld: true, resultVersion: {{gridVersion}}),
                        new ExtensionCommand("player-support", "Read local player position, motion and grounded state", Read, readOnly: true, role: ExtensionRole.Client, needsWorld: true),
                        new ExtensionCommand("terrain", "terrain <x> <z> <generator|loaded-ground>", context => Read(context), readOnly: true, needsWorld: true));
                    private static IEnumerator Read(ExtensionContext context) { yield break; }
                }
                [BepInPlugin("valheimCLI.worldtools", "CLI World Tools", "0.1.0")]
                public sealed class WorldToolsPack : BaseUnityPlugin
                {
                    private void Start() { var core = valheimCLIPlugin.Instance; WorldObservations.Register(core.Extensions); core.Modules.Register("cli.worldtools", "0.1.0", () => { }); }
                }
            }
            """;

        // Only the core library's types are referenced; the packs reference the stub BepInEx and core, as real packs do.
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
