using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// #296 step 3: every owned client runs from a disposable loader profile of its install, made by the one stage owner
// (HostedRuntimeStage on this machine), with the run's ValheimCLI set staged into the profile and the static check always run;
// inPlace (--in-place) runs the install as it is and writes nothing into it. The profile tests run the real seed, stage and
// retire scripts on this machine's shell, on a fake install; nothing launches.
public sealed class LocalClientCopyTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    // The rig's ValheimCLI set, its manifest beside its DLLs as a bundle holds them.
    private string CliSet()
    {
        string manifest = Path.Combine(_rig.Root, "cli", CliBundle.ManifestFile);
        File.Copy(_rig.CliManifest(save: true), manifest, overwrite: true);
        return manifest;
    }

    private ClientRunPlan Plan(string install) => new()
    {
        Mode = "owned", Install = install, Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Architecture = "x64", // The synthetic Mac install has only the stock x64 Doorstop.
        Pins = new() { ["valheimCLI.valheimCLI"] = OwnedRunPreflightTests.Md5("an older ValheimCLI core"), ["example.unrelated"] = "absent" },
        InstallPins = InstallPins.Of(install),
    };

    private static IGameHost ThisMachine()
    {
        var local = new LocalGameHost("this machine", HostShell.ThisMachine);
        // A fake Valheim.app is unsigned, so the real macOS bundle check would refuse it (MacAppBundleTests covers that check).
        return new BundleAcceptingHost(local, assumeNoGame: true);
    }

    private static Dictionary<string, string> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path), FileHash.Sha256);

    [Fact] public void AnOldSplitCliSetIsRefusedBeforeACopyIsPrepared()
    {
        string manifestPath = CliSet();
        var manifest = CliCapabilityManifest.Read(manifestPath);
        manifest.Files.Add(new CliManifestFile
        {
            File = "Valheim.Cli.Standard.dll", Sha256 = new string('a', 64), Plugins = ["valheimCLI.standard"]
        });
        manifest.Write(manifestPath);
        var plan = Plan(_rig.Game);
        plan.CliManifest = manifestPath;
        var error = Assert.Throws<InvalidOperationException>(plan.Preflight);
        Assert.Contains("not a one-plugin build", error.Message);
        Assert.False(plan.Prepared);
    }

    [Fact] public async Task AnOwnedClientRunsFromAProfileWithTheSetStagedAndTheInstallUnchanged()
    {
        string source = _rig.Game;
        _rig.Write("game/BepInEx/plugins/valheimCLI.dll", System.Text.Encoding.UTF8.GetBytes("an older ValheimCLI core")); // another build, by name
        string manifest = CliSet();
        var plan = Plan(source);
        plan.CliManifest = manifest;
        plan.Validate();
        plan.Preflight(); // The source's checks: its pins but ValheimCLI's, which are the staged set's.
        Assert.True(plan.CopySource);
        Assert.Equal("static and live", plan.CliPreflight);
        var before = Tree(source);

        string data = Path.Combine(_rig.Root, "data");
        using var journalDirectory = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));
        using var run = RunJournal.UseRun("run-localcopy-" + Guid.NewGuid().ToString("N")[..8]);
        var copy = await LocalClientCopy.PrepareAsync("client", plan, default, data, ThisMachine(), loader: (_, _) => null);

        // Bound to the profile: its game and loader roots, its pins (the staged core's MD5 in place of the plan's) and the staged
        // set's manifest. Windows launches from the profile's own folder; macOS and Linux launch the source game.
        Assert.True(plan.Prepared);
        Assert.False(plan.CopySource);
        Assert.Equal(OperatingSystem.IsWindows() ? copy.Runtime : source, plan.Install);
        Assert.Equal(copy.Runtime, plan.LoaderRoot);
        Assert.StartsWith(Path.Combine(data, "runs", "local-clients", "vt-prep-"), copy.Runtime);
        Assert.Equal(FileHash.Md5(Path.Combine(_rig.Root, "cli", "valheimCLI.dll")), plan.Pins["valheimCLI.valheimCLI"]);
        Assert.DoesNotContain("valheimCLI.standard", plan.Pins.Keys);
        Assert.Equal("absent", plan.Pins["example.unrelated"]);
        Assert.Equal(manifest, plan.CliManifest);
        Assert.Equal(InstallPins.Of(plan.Install, plan.LoaderRoot).Game, plan.InstallPins!.Game);
        // The profile holds the install's own plugins and config, the staged set instead of its ValheimCLI, and ValheimCLI's port.
        Assert.Equal(FileHash.Sha256(Path.Combine(_rig.Root, "cli", "valheimCLI.dll")), FileHash.Sha256(Path.Combine(copy.Runtime, "BepInEx", "plugins", "valheimCLI.dll")));
        Assert.False(File.Exists(Path.Combine(copy.Runtime, "BepInEx", "plugins", "Valheim.Cli.Standard.dll")));
        Assert.True(File.Exists(Path.Combine(copy.Runtime, "BepInEx", "plugins", "Unrelated.dll")));
        Assert.True(File.Exists(Path.Combine(copy.Runtime, "BepInEx", "config", "BepInEx.cfg")));
        string cliConfig = File.ReadAllText(Path.Combine(copy.Runtime, "BepInEx", "config", "valheimCLI.valheimCLI.cfg"));
        Assert.Contains("Port = 5556", cliConfig);
        Assert.Contains("AllowOnServerClients = true", cliConfig);
        // The static check runs on the profile: exactly the staged set, by SHA256.
        Assert.Single(plan.CheckCliManifest()!.Files);
        // Nothing in the install changed.
        Assert.Equal(before, Tree(source));
        string journal = string.Concat(Directory.EnumerateFiles(Path.Combine(data, "journal"), "client.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("\"copy-intended\"", journal);
        Assert.Contains("\"copy-done\"", journal);

        await copy.RetireAsync();
        Assert.False(Directory.Exists(copy.Runtime));
        // The plan is as it was: a run that reuses it makes a new profile.
        Assert.True(plan.CopySource);
        Assert.Equal(source, plan.Install);
        Assert.Equal(manifest, plan.CliManifest);
        Assert.Contains("\"copy-retired\"", string.Concat(Directory.EnumerateFiles(Path.Combine(data, "journal"), "client.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText)));
        Assert.Equal(before, Tree(source));
    }

    [Fact] public async Task AnOwnedClientDefaultsToAProfileAndKeepsItsSourceUnchanged()
    {
        string source = _rig.Game;
        var plan = Plan(source);
        plan.CliManifest = CliSet();
        var before = Tree(source);
        string data = Path.Combine(_rig.Root, "profile-data");
        using var journalDirectory = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));
        using var run = RunJournal.UseRun("run-localprofile-" + Guid.NewGuid().ToString("N")[..8]);
        var profile = await LocalClientCopy.PrepareAsync("client", plan, default, data, ThisMachine(), loader: (_, _) => null);
        try
        {
            Assert.Equal("profile", plan.PreparedLaunchMode);
            Assert.True(plan.Prepared);
            Assert.Equal(OperatingSystem.IsWindows() ? profile.Runtime : source, plan.Install);
            Assert.Equal(OperatingSystem.IsWindows() ? null : profile.Runtime, plan.PreparedLoaderRoot);
            Assert.Equal(InstallPins.Of(plan.Install, plan.LoaderRoot).Game, plan.InstallPins!.Game);
            Assert.Equal(InstallPins.Of(plan.Install, plan.LoaderRoot).Loader, plan.InstallPins.Loader);
            Assert.Single(plan.CheckCliManifest()!.Files);
            Assert.True(File.Exists(Path.Combine(profile.Runtime, "BepInEx", "plugins", "valheimCLI.dll")));
            Assert.Equal(before, Tree(source));
        }
        finally { await profile.RetireAsync(); }
        Assert.False(Directory.Exists(profile.Runtime));
        Assert.Equal(before, Tree(source));
        Assert.Null(plan.PreparedLaunchMode);
    }

    [Theory]
    [InlineData("[Server]\nEnabled = true\nPort = 5556\n")]
    [InlineData("[Server]\nEnabled = true\nAllowOnServerClients = false\nPort = 5556\n")]
    [InlineData("[Other]\nAllowOnServerClients = true\n[Server]\nEnabled = true\nPort = 5556\n")]
    [InlineData("[server]\nAllowOnServerClients = true\nPort = 5556\n")]
    [InlineData("[ Server ]\nAllowOnServerClients = true\nPort = 5556\n")]
    [InlineData("[Server]\nallowonserverclients = true\nPort = 5556\n")]
    [InlineData("[Server]\nAllowOnServerClients = true # test\nPort = 5556\n")]
    [InlineData("[Server]\nAllowOnServerClients = true ; test\nPort = 5556\n")]
    public async Task AnExplicitClientConfigWithoutMutationAccessIsRefusedBeforeTheCopy(string contents)
    {
        string config = Path.Combine(_rig.Game, "BepInEx", "config", "valheimCLI.valheimCLI.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, contents);
        var before = Tree(_rig.Game);
        var plan = Plan(_rig.Game);
        plan.CliManifest = CliSet();
        string data = Path.Combine(_rig.Root, "data");
        using var journalDirectory = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            LocalClientCopy.PrepareAsync("client", plan, default, data, ThisMachine(), loader: (_, _) => null));
        Assert.Contains("AllowOnServerClients = true", failure.Message);
        Assert.False(Directory.Exists(Path.Combine(data, "runs")));
        Assert.Equal(before, Tree(_rig.Game));
    }

    // AllowOnServerClients governs a client joined to someone else's server; a client hosting its own world needs no such setting.
    [Fact] public async Task AnExplicitConfigWithoutMutationAccessRunsForAClientThatHostsItsOwnWorld()
    {
        string config = Path.Combine(_rig.Game, "BepInEx", "config", "valheimCLI.valheimCLI.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        const string chosen = "[Server]\nEnabled = true\nPort = 5556\n";
        File.WriteAllText(config, chosen);
        var before = Tree(_rig.Game);
        var plan = Plan(_rig.Game);
        plan.Join = "";
        plan.HostWorld = new HostWorldPlan { WorldUid = "fixture-uid" };
        plan.CliManifest = CliSet();
        string data = Path.Combine(_rig.Root, "data");
        using var journalDirectory = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));
        var copy = await LocalClientCopy.PrepareAsync("client", plan, default, data, ThisMachine(), loader: (_, _) => null);
        Assert.Equal(chosen, File.ReadAllText(Path.Combine(copy.Runtime, "BepInEx", "config", "valheimCLI.valheimCLI.cfg")));
        Assert.Equal(before, Tree(_rig.Game));
        await copy.RetireAsync();
    }

    [Fact] public void OnlyAClientThatHostsNoWorldOfItsOwnNeedsMutationAccess()
    {
        Assert.True(CliServerConfig.RequiresClientMutations(Plan(_rig.Game)));
        Assert.True(CliServerConfig.RequiresClientMutations(new ClientRunPlan { JoinsHost = true }));
        Assert.True(CliServerConfig.RequiresClientMutations(new ClientRunPlan { Crossplay = true }));
        Assert.False(CliServerConfig.RequiresClientMutations(new ClientRunPlan { HostWorld = new HostWorldPlan() }));
        Assert.True(CliServerConfig.RequiresClientMutations(null)); // An unknown plan is treated as joined.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnExplicitConfigIsCheckedOnlyWhenTheRoleNeedsMutationAccess(bool requireClientMutations)
    {
        string selected = Path.Combine(_rig.Root, "selected.cfg"), generated = Path.Combine(_rig.Root, "generated.cfg");
        File.WriteAllText(selected, "[Server]\nEnabled = true\nPort = 5556\n");
        var files = new[] { new HostedRuntimeFile(selected, CliServerConfig.RelativePath) };
        if (requireClientMutations) Assert.Throws<InvalidDataException>(() => CliServerConfig.Stage(files, "actor", 5556, generated, true));
        else Assert.Equal(selected, Assert.Single(CliServerConfig.Stage(files, "actor", 5556, generated, false)).Source);
        Assert.False(File.Exists(generated)); // An explicit config is kept as selected, never rewritten.
    }

    // With no config of the actor's own, every role gets the same generated file.
    [Fact] public void AGeneratedConfigIsTheSameForEveryRole()
    {
        string joined = Path.Combine(_rig.Root, "joined.cfg"), hosting = Path.Combine(_rig.Root, "hosting.cfg");
        CliServerConfig.Stage([], "client", 5556, joined, requireClientMutations: true);
        CliServerConfig.Stage([], "host", 5556, hosting, requireClientMutations: false);
        Assert.Equal(File.ReadAllText(joined), File.ReadAllText(hosting));
        Assert.Contains("AllowOnServerClients = true", File.ReadAllText(joined));
    }

    [Fact] public async Task AnExplicitClientConfigWithMutationAccessIsStagedUnchanged()
    {
        string config = Path.Combine(_rig.Game, "BepInEx", "config", "valheimCLI.valheimCLI.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        const string chosen = "[Server]\nEnabled = true\nAllowOnServerClients = true\nPort = 5556\n[Extra]\nKeep = yes\n";
        File.WriteAllText(config, chosen);
        var before = Tree(_rig.Game);
        var plan = Plan(_rig.Game);
        plan.CliManifest = CliSet();
        string data = Path.Combine(_rig.Root, "data");
        using var journalDirectory = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));
        var copy = await LocalClientCopy.PrepareAsync("client", plan, default, data, ThisMachine(), loader: (_, _) => null);
        Assert.Equal(chosen, File.ReadAllText(Path.Combine(copy.Runtime, "BepInEx", "config", "valheimCLI.valheimCLI.cfg")));
        Assert.Equal(before, Tree(_rig.Game));
        await copy.RetireAsync();
    }

    // An install whose Doorstop pair does not match takes the shipped BepInExPack in its profile: the pack's own files (its
    // BepInEx.cfg among them) replace the install's, which an install that ran once always has. Windows: the pack is Windows'.
    [Fact] public async Task AShippedLoaderReplacesTheInstallsLoaderFilesInTheProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        string pack = Path.Combine(_rig.Root, "pack");
        foreach (var (relative, text) in new[] { ("winhttp.dll", "MZ target_assembly"), ("doorstop_config.ini", "[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Preloader.dll\n"),
            ("BepInEx/core/BepInEx.Preloader.dll", "shipped preloader"), ("BepInEx/core/BepInEx.dll", "shipped core"), ("BepInEx/config/BepInEx.cfg", "[Logging]\nshipped = true\n") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(pack, relative))!);
            File.WriteAllText(Path.Combine(pack, relative), text);
        }
        string package = Path.Combine(_rig.Root, "loader.json");
        BepInExLoaderPackage.Capture(pack, "BepInExPack_Valheim", "5.4.2351").Write(package);
        var plan = Plan(_rig.Game);
        plan.CliManifest = CliSet();
        var before = Tree(_rig.Game);
        string data = Path.Combine(_rig.Root, "data");
        using var journalDirectory = RunJournal.UseLocalDirectory(Path.Combine(data, "journal"));
        var copy = await LocalClientCopy.PrepareAsync("client", plan, default, data, ThisMachine(), loader: (_, _) => new ShippedLoader.Choice(package, "test"));
        Assert.Contains("shipped = true", File.ReadAllText(Path.Combine(copy.Runtime, "BepInEx", "config", "BepInEx.cfg")));
        Assert.Equal(BepInExLoaderPackage.Read(package).Loader, plan.InstallPins!.Loader);
        Assert.Equal(before, Tree(_rig.Game));
        await copy.RetireAsync();
    }

    // A copy whose named set is not complete, or a build with no pinned bundle and no named set, is refused before anything is copied.
    [Fact] public async Task ACopyWithoutACompleteSetToStageIsRefusedBeforeAnythingIsCopied()
    {
        string data = Path.Combine(_rig.Root, "data");
        var plan = Plan(_rig.Game);
        var none = await Assert.ThrowsAsync<InvalidDataException>(() =>
            LocalClientCopy.PrepareAsync("client", plan, default, data, ThisMachine(), cliBundle: () => null, loader: (_, _) => null));
        Assert.Contains("carries no pinned ValheimCLI bundle", none.Message);
        Assert.Contains("--in-place", none.Message);
        string manifest = CliSet();
        File.Delete(Path.Combine(_rig.Root, "cli", "valheimCLI.dll"));
        plan.CliManifest = manifest;
        var partial = await Assert.ThrowsAsync<InvalidDataException>(() =>
            LocalClientCopy.PrepareAsync("client", plan, default, data, ThisMachine(), loader: (_, _) => null));
        Assert.Contains("is not complete", partial.Message);
        Assert.False(Directory.Exists(Path.Combine(data, "runs")));
        Assert.False(plan.Prepared);
    }

    // ---- the plan's three states: the source of a copy, a bound copy, an install run in place ----

    [Fact] public void TheSourceOfACopyIsNeverLaunchedAndItsSetIsCheckedOnTheCopy()
    {
        using var install = PreflightInstall.Create();
        var plan = install.Plan();
        plan.InPlace = false; // The default: a disposable copy.
        plan.Pins.Remove("valheimCLI.valheimCLI"); // A copy's ValheimCLI pins are its staged set's.
        plan.Validate();
        plan.Preflight(CliCapabilities.HostedRounds); // Nothing reads the source's ValheimCLI files.
        Assert.Null(plan.CheckCliManifest(PlayerPlacement.ArrivalCapabilities));
        // What the runner needs before the copy exists is checked on the copy before its launch.
        Assert.Superset(new HashSet<string>(CliCapabilities.HostedRounds.Where(CliCapabilities.IsBuiltInCapability)), plan.StaticCapabilities);
        var launch = Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, install.Root));
        Assert.Contains("runs from a disposable loader profile", launch.Message);
        Assert.Contains("inPlace (--in-place)", launch.Message);
        // A named set must already offer them; a bound copy must name its set.
        plan.CliManifest = CliSet();
        Assert.Contains("lacks", Assert.Throws<InvalidOperationException>(() => plan.CheckCliManifest(["valheim.world/terrain"])).Message);
        plan.CliManifest = null;
        plan.Prepared = true;
        Assert.Contains("names no staged ValheimCLI manifest", Assert.Throws<InvalidOperationException>(() => plan.CheckCliManifest()).Message);
    }

    [Fact] public void AnInstallRunInPlaceMustHoldBepInExAndValheimCliAndGetsNothingStaged()
    {
        using var install = PreflightInstall.Create();
        var plan = install.Plan(); // inPlace
        plan.Validate();
        plan.Preflight();
        Assert.Equal("live only: static check not run: your install, run in place", plan.CliPreflight);
        File.Delete(Path.Combine(install.Root, "BepInEx", "plugins", "valheimCLI.dll"));
        File.Delete(Path.Combine(install.Root, "BepInEx", "core", "BepInEx.Preloader.dll"));
        plan.InstallPins = InstallPins.Of(install.Root); // Pinned as it now is: the in-place check, not the pins, refuses it.
        var error = Assert.Throws<InvalidOperationException>(() => plan.Preflight());
        Assert.Contains("lacks BepInEx (BepInEx/core/BepInEx.Preloader.dll is not there) and ValheimCLI", error.Message);
        Assert.Contains("leave out inPlace (--in-place)", error.Message);
        // An attached client is its operator's: in place or not is not the run's to say.
        var attached = new ClientRunPlan { Mode = "attach", InPlace = true, Port = 5556, Join = "127.0.0.1:2456", Character = "Tester", Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) } };
        Assert.Contains("inPlace", Assert.Throws<ArgumentException>(() => attached.Validate()).Message);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // --in-place sets every client plan the run reads; the plan field is the same name.
    [Fact] public void TheInPlaceOptionIsThePlanFieldForEveryClientTheRunReads()
    {
        var (inPlace, inventory, rest) = PinnedServerRun.LeadingOptions(["--in-place", "--inventory", "inv.json", "run", "plan.json", "out"], inventory: true);
        Assert.True(inPlace);
        Assert.Equal("inv.json", inventory);
        Assert.Equal(new[] { "run", "plan.json", "out" }, rest);
        Assert.False(PinnedServerRun.LeadingOptions(["validate", "plan.json", "out"], inventory: true).InPlace);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<ClientRunPlan>("{\"mode\":\"owned\"}", Json)!.InPlace);
        using (ClientRunPlan.InPlaceRun())
            Assert.True(System.Text.Json.JsonSerializer.Deserialize<ClientRunPlan>("{\"mode\":\"owned\"}", Json)!.InPlace);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<ClientRunPlan>("{\"mode\":\"owned\",\"inPlace\":true}", Json)!.InPlace);
    }
}
