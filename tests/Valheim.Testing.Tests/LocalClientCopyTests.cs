using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// #296 step 3: every owned client runs from a disposable copy of its install, made by the one copy owner (HostedRuntimeStage on
// this machine), with the run's ValheimCLI set staged into the copy and the static check always run; inPlace (--in-place)
// runs the install as it is and writes nothing into it. The copy tests run the real copy, stage and retire scripts on this
// machine's shell, on a fake install; nothing launches.
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
        var local = new LocalGameHost("this machine", OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash);
        // A fake Valheim.app is unsigned, so the real macOS bundle check would refuse it (MacAppBundleTests covers that check).
        return new BundleAcceptingHost(local, assumeNoGame: true);
    }

    private static Dictionary<string, string> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path), FileHash.Sha256);

    [Fact] public async Task AnOwnedClientRunsFromACopyWithTheSetStagedAndTheInstallUnchanged()
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

        // Bound to the copy: its install, its pins (the staged core's MD5 in place of the plan's) and the staged set's manifest.
        Assert.True(plan.Prepared);
        Assert.False(plan.CopySource);
        Assert.Equal(source, plan.CopiedFrom);
        Assert.Equal(copy.Runtime, plan.Install);
        Assert.StartsWith(Path.Combine(data, "runs", "local-clients", "vt-prep-"), copy.Runtime);
        Assert.Equal(FileHash.Md5(Path.Combine(_rig.Root, "cli", "valheimCLI.dll")), plan.Pins["valheimCLI.valheimCLI"]);
        Assert.Equal(FileHash.Md5(Path.Combine(_rig.Root, "cli", "Valheim.Cli.Standard.dll")), plan.Pins["valheimCLI.standard"]);
        Assert.Equal("absent", plan.Pins["example.unrelated"]);
        Assert.Equal(manifest, plan.CliManifest);
        Assert.Equal(InstallPins.Of(copy.Runtime).Game, plan.InstallPins!.Game);
        // The copy holds the install's own plugins and config, the staged set instead of its ValheimCLI, and ValheimCLI's port.
        Assert.Equal(FileHash.Sha256(Path.Combine(_rig.Root, "cli", "valheimCLI.dll")), FileHash.Sha256(Path.Combine(copy.Runtime, "BepInEx", "plugins", "valheimCLI.dll")));
        Assert.True(File.Exists(Path.Combine(copy.Runtime, "BepInEx", "plugins", "Valheim.Cli.Standard.dll")));
        Assert.True(File.Exists(Path.Combine(copy.Runtime, "BepInEx", "plugins", "Unrelated.dll")));
        Assert.True(File.Exists(Path.Combine(copy.Runtime, "BepInEx", "config", "BepInEx.cfg")));
        string cliConfig = File.ReadAllText(Path.Combine(copy.Runtime, "BepInEx", "config", "valheimCLI.valheimCLI.cfg"));
        Assert.Contains("Port = 5556", cliConfig);
        Assert.Contains("AllowOnServerClients = true", cliConfig);
        // The static check runs on the copy: exactly the staged set, by SHA256.
        Assert.Equal(2, plan.CheckCliManifest()!.Files.Count);
        // Nothing in the install changed.
        Assert.Equal(before, Tree(source));
        string journal = string.Concat(Directory.EnumerateFiles(Path.Combine(data, "journal"), "client.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("\"copy-intended\"", journal);
        Assert.Contains("\"copy-done\"", journal);

        await copy.RetireAsync();
        Assert.False(Directory.Exists(copy.Runtime));
        // The plan is as it was: a run that reuses it makes a new copy.
        Assert.True(plan.CopySource);
        Assert.Equal(source, plan.Install);
        Assert.Equal(manifest, plan.CliManifest);
        Assert.Contains("\"copy-retired\"", string.Concat(Directory.EnumerateFiles(Path.Combine(data, "journal"), "client.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText)));
        Assert.Equal(before, Tree(source));
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

    // An install whose Doorstop pair does not match takes the shipped BepInExPack in its copy: the pack's own files (its
    // BepInEx.cfg among them) replace the install's, which an install that ran once always has. Windows: the pack is Windows'.
    [Fact] public async Task AShippedLoaderReplacesTheInstallsLoaderFilesInTheCopy()
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
        File.Delete(Path.Combine(_rig.Root, "cli", "Valheim.Cli.Standard.dll"));
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
        Assert.Superset(new HashSet<string>(CliCapabilities.HostedRounds.Where(CliCapabilities.IsPackCapability)), plan.StaticCapabilities);
        var launch = Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, install.Root));
        Assert.Contains("runs from a disposable copy", launch.Message);
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
