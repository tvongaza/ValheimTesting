using Valheim.Testing.Game;
using System.Diagnostics;
using Xunit;
using Valheim.Testing.GameSessions;

public sealed class HostedRuntimeStageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("host-stage-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Theory]
    [InlineData("valheim_server", "", true, "VT-GAME idle")]
    [InlineData("valheim_server", "/owned", false, "VT-GAME unknown")]
    [InlineData("/other/valheim_server.x86_64", "/owned", true, "VT-GAME idle")]
    [InlineData("/owned/valheim_server.x86_64", "/owned", false, "VT-GAME busy 4242")]
    [InlineData("/tmp/Valheim.app/Contents/MacOS/Valheim", "", true, "VT-GAME busy 4242")]
    [InlineData("ordinary-helper", "/owned", true, "VT-GAME idle")]
    // One check for every copy on a host (#257): a game running from any of them is busy, from none of them idle.
    [InlineData("/second/valheim_server.x86_64", "/first\n/second", false, "VT-GAME busy 4242")]
    [InlineData("/first/valheim.x86_64", "/first\n/second", false, "VT-GAME busy 4242")]
    [InlineData("/second2/valheim_server.x86_64", "/first\n/second", false, "VT-GAME idle")]
    [InlineData("valheim_server", "/first\n/second", false, "VT-GAME unknown")]
    public async Task BashProcessCheckRefusesConflictingUse(string process, string runtime, bool clientSession, string expected)
    {
        if (OperatingSystem.IsWindows()) return;
        string bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        string ps = Path.Combine(bin, "ps");
        // As ps -axo pid=,comm= prints it: the ID, then the command.
        File.WriteAllText(ps, "#!/bin/sh\nprintf '%s\\n' '  4242 " + process + "'\n");
        File.SetUnixFileMode(ps, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(HostedRuntimeStage.BashProcessCheck);
        start.Environment["runtime"] = runtime;
        start.Environment["clientSession"] = clientSession ? "true" : "false";
        start.Environment["PATH"] = bin + ":" + start.Environment["PATH"];
        using var child = Process.Start(start)!;
        string output = await child.StandardOutput.ReadToEndAsync();
        string errors = await child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        Assert.Equal(0, child.ExitCode);
        Assert.Contains(expected, output);
        Assert.Empty(errors);
    }

    // The real bash apply replaces the copied loader with a reviewed package's: the prepared runtime's loader pin is the package's.
    [Fact] public async Task MacShellReplacesTheCopiedLoaderWithAReviewedPackage()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // The fake bundle is unsigned, so the real macOS bundle check would refuse it (MacAppBundleTests covers that check).
        var host = new BundleAcceptingHost(new LocalGameHost("local-mac", HostShell.Bash));
        string source = Path.Combine(_root, "source");
        foreach (var (relative, text) in new[] { ("Valheim.app/Contents/MacOS/Valheim", "game"),
            ("Valheim.app/Contents/Resources/Data/Managed/" + InstallPins.GameAssemblyName, "game"), ("BepInEx/core/BepInEx.dll", "old core"),
            ("BepInEx/core/BepInEx.Preloader.dll", "old preloader"), ("BepInEx/core/stale.dll", "stale"), ("doorstop_libs/libdoorstop_x64.dylib", "old doorstop") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, relative))!);
            File.WriteAllText(Path.Combine(source, relative), text);
        }
        string packageRoot = Path.Combine(_root, "package");
        foreach (var (relative, text) in new[] { ("BepInEx/core/BepInEx.dll", "new core"), ("BepInEx/core/BepInEx.Preloader.dll", "new preloader"), ("libdoorstop.dylib", "new doorstop") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(packageRoot, relative))!);
            File.WriteAllText(Path.Combine(packageRoot, relative), text);
        }
        var package = BepInExLoaderPackage.Capture(packageRoot, "native-loader", "1");
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected plugin");
        string run = Path.Combine(_root, "vt-loader");
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, Path.Combine(run, "runtime"), Path.Combine(run, "staging"),
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30), loaderPackage: package);
        Assert.Equal(package.Loader, HostInstall.Pins(listing).Loader);
        Assert.Equal(package.Loader, InstallPins.Of(Path.Combine(run, "runtime")).Loader);
        Assert.Equal("1", Assert.Single(host.BundleChecks)["repair"]); // the stage asked about its copy, never the source
        Assert.StartsWith(run, Assert.Single(host.BundleChecks)["app"]);
        Assert.False(listing.Files.ContainsKey("BepInEx/core/stale.dll"));
        Assert.False(listing.Files.ContainsKey("doorstop_libs/libdoorstop_x64.dylib"));
    }

    // The Windows conflicting-use check for real: it runs in Windows PowerShell and, with no Valheim here, finds the host idle
    // for a client session and for an owned runtime path alike.
    [Fact] public async Task WindowsProcessCheckRunsAndFindsAnIdleHostIdle()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (System.Diagnostics.Process.GetProcessesByName("valheim").Length + System.Diagnostics.Process.GetProcessesByName("valheim_server").Length != 0) return; // a station with a game up
        var host = new LocalGameHost("windows-process", HostShell.WindowsPowerShell);
        await HostedRuntimeStage.RequireStoppedAsync(host, TimeSpan.FromSeconds(60), default, runtimes: [Path.Combine(_root, "runtime")], clientSession: true);
        await HostedRuntimeStage.RequireStoppedAsync(host, TimeSpan.FromSeconds(60), default, runtimes: [Path.Combine(_root, "a"), Path.Combine(_root, "b c")], clientSession: false);
        await HostedRuntimeStage.RequireStoppedAsync(host, TimeSpan.FromSeconds(60), default, clientSession: false);
    }

    [Fact] public async Task AnUnprovableWindowsGameProcessStillRefusesTheRuntime()
    {
        var host = new FakeServerHost("windows-process", Path.Combine(_root, "mirror"), windows: true);
        host.Failures["game-process"] = new HostResult(HostOutcome.Exited, 1, "", "Cannot establish the running game executable path", TimeSpan.Zero, false);
        var error = await Assert.ThrowsAsync<HostOperationException>(() =>
            HostedRuntimeStage.RequireStoppedAsync(host, TimeSpan.FromSeconds(30), runtimes: [Path.Combine(_root, "runtime")], clientSession: false));
        Assert.Contains("Cannot establish the running game executable path", error.ToString());
    }

    // One check covers every copy on a host (#257): a "server" running from the second of two runtimes is busy, and from none
    // of the named ones it is not. The stand-in is ping.exe copied under the server's name, so Win32_Process reports its path.
    [Fact] public async Task WindowsProcessCheckFindsAGameRunningFromAnyOfTheNamedRuntimes()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Process.GetProcessesByName("valheim").Length + Process.GetProcessesByName("valheim_server").Length != 0) return; // a station with a game up
        string first = Path.Combine(_root, "a"), second = Path.Combine(_root, "b c");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        string standIn = Path.Combine(second, "valheim_server.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), standIn);
        var host = new LocalGameHost("windows-process", HostShell.WindowsPowerShell);
        using var running = Process.Start(new ProcessStartInfo(standIn, "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            // The refusal names the process (#257): with no journal to ask, by its ID alone.
            Assert.Contains($"Conflicting Valheim process {running.Id} is running on", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HostedRuntimeStage.RequireStoppedAsync(host, TimeSpan.FromSeconds(60), default, runtimes: [first, second], clientSession: false))).Message);
            await HostedRuntimeStage.RequireStoppedAsync(host, TimeSpan.FromSeconds(60), default, runtimes: [first], clientSession: false);
        }
        finally { running.Kill(); running.WaitForExit(10_000); }
    }

    // Executes the actual bash copy, shipment, listing and apply scripts on macOS. A fake host cannot catch BSD-tool
    // option mismatches, which have repeatedly consumed native-test setup time.
    [Fact] public async Task MacShellStagesASelectedClientWithoutRunningTheGame()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // The fake bundle is unsigned, so the real macOS bundle check would refuse it (MacAppBundleTests covers that check).
        var host = new BundleAcceptingHost(new LocalGameHost("local-mac", HostShell.Bash));
        string source = Path.Combine(_root, "source");
        string executable = Path.Combine(source, "Valheim.app", "Contents", "MacOS", "Valheim");
        string managed = Path.Combine(source, "Valheim.app", "Contents", "Resources", "Data", "Managed", InstallPins.GameAssemblyName);
        string core = Path.Combine(source, "BepInEx", "core", "BepInEx.dll");
        // A complete loader: the preloader beside the core and BepInExPack's own Doorstop library.
        string preloader = Path.Combine(source, "BepInEx", "core", "BepInEx.Preloader.dll");
        string doorstop = Path.Combine(source, "doorstop_libs", "libdoorstop_x64.dylib");
        foreach (string path in new[] { executable, managed, core, preloader, doorstop })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Path.GetFileName(path));
        }
        string old = Path.Combine(source, "BepInEx", "plugins", "old.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "old plugin");
        string sourceConfig = Path.Combine(source, "BepInEx", "config", "BepInEx.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceConfig)!);
        File.WriteAllText(sourceConfig, "[Preloader.Entrypoint]\nType = GameObject\n");
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected plugin");
        string run = Path.Combine(_root, "vt-one");
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source,
            Path.Combine(run, "runtime"), Path.Combine(run, "staging"),
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), listing.Files["BepInEx/plugins/selected.dll"]);
        Assert.Equal("1", Assert.Single(host.BundleChecks)["repair"]);
        Assert.StartsWith(run, Assert.Single(host.BundleChecks)["app"]);
        Assert.False(listing.Files.ContainsKey("BepInEx/plugins/old.dll"));
        Assert.Equal(FileHash.Sha256(sourceConfig), listing.Files["BepInEx/config/BepInEx.cfg"]);
        Assert.Equal(File.ReadAllBytes(sourceConfig), File.ReadAllBytes(Path.Combine(run, "runtime", "BepInEx", "config", "BepInEx.cfg")));
        Assert.True(File.Exists(old));
        Assert.False(Directory.Exists(Path.Combine(run, "staging")));
        var inspected = await HostedRuntimeStage.InspectSourceAsync(host, HostedRuntimeKind.Client, source, null,
            TimeSpan.FromSeconds(30));
        File.AppendAllText(core, "changed after preflight");
        string changedRun = Path.Combine(_root, "vt-changed");
        var changed = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareWithInspectedSourceAsync(
            host, HostedRuntimeKind.Client, source, Path.Combine(changedRun, "runtime"), Path.Combine(changedRun, "staging"),
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30), default, null, inspected));
        Assert.Contains("differs from the pinned source", changed.Message);
        Assert.False(Directory.Exists(Path.Combine(changedRun, "runtime")));
    }

    [Fact] public async Task MacShellPreparesADedicatedServerWithoutChangingItsSource()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var host = new LocalGameHost("local-mac-server", HostShell.Bash);
        string source = Path.Combine(_root, "mac-server-source");
        string executable = Path.Combine(source, "valheim_server", "Valheim");
        string assembly = Path.Combine(source, "valheim_server", "Data", "Managed", InstallPins.GameAssemblyName);
        string core = Path.Combine(source, "BepInEx", "core");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        Directory.CreateDirectory(core);
        File.WriteAllText(executable, "server");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.WriteAllText(assembly, "game assembly");
        File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "core");
        File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "preloader");
        FakeInstalls.MacLoader(source);
        string sourceConfig = Path.Combine(source, "BepInEx", "config", "BepInEx.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceConfig)!);
        File.WriteAllText(sourceConfig, "[Preloader.Entrypoint]\nType = GameObject\n");
        string selected = Path.Combine(_root, "mac-server-mod.dll");
        File.WriteAllText(selected, "selected mod");
        var before = WorldFixture.Manifest(source);
        string run = Path.Combine(_root, "mac-server-run");

        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source,
            Path.Combine(run, "runtime"), Path.Combine(run, "staging"),
            [new HostedRuntimeFile(selected, "BepInEx/plugins/mac-server-mod.dll")], TimeSpan.FromSeconds(30));

        Assert.Equal(ServerPlatform.MacOS, HostInstall.DetectServer(listing));
        Assert.Contains(GameLaunch.ServerMacExecutable, listing.Executables);
        Assert.Equal(FileHash.Sha256(selected), listing.Files["BepInEx/plugins/mac-server-mod.dll"]);
        Assert.Equal(File.ReadAllBytes(sourceConfig), File.ReadAllBytes(Path.Combine(run, "runtime", "BepInEx", "config", "BepInEx.cfg")));
        Assert.Equal(before, WorldFixture.Manifest(source));
        Assert.False(File.Exists(Path.Combine(source, "BepInEx", "plugins", "mac-server-mod.dll")));
        Assert.False(Directory.Exists(Path.Combine(run, "staging")));
    }

    // One disposable install per actor, prepared on a fake host (HostedRuntimeStage.PrepareAsync): a copy, the selected files, the loader.
    private string Mirror => Path.Combine(_root, "host");
    private static void StageLoader(string root)
    {
        File.WriteAllText(Path.Combine(root, "winhttp.dll"), "MZ target_assembly");
        File.WriteAllText(Path.Combine(root, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
    }

    [Fact] public async Task MacDedicatedServerIsPinnedAndPreparedWithoutChangingItsSource()
    {
        var host = new FakeServerHost("mac-server", Mirror);
        const string source = "/game/mac-server", runtime = "/runs/mac/runtime", staging = "/runs/mac/staging";
        string install = host.Local(source);
        string executable = Path.Combine(install, "valheim_server", "Valheim");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "mac server");
        string assembly = Path.Combine(install, "valheim_server", "Data", "Managed", InstallPins.GameAssemblyName);
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        File.WriteAllText(assembly, "mac game assembly");
        FakeInstalls.MacLoader(install);
        string core = Path.Combine(install, "BepInEx", "core");
        Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "core");
        File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "preloader");
        string chosen = Path.Combine(_root, "chosen-mac.dll");
        File.WriteAllText(chosen, "selected plugin");
        var before = WorldFixture.Manifest(install);

        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen-mac.dll")], TimeSpan.FromSeconds(30));

        Assert.Equal(ServerPlatform.MacOS, HostInstall.DetectServer(listing));
        Assert.Equal(FileHash.Sha256(assembly), listing.Files["valheim_server/Data/Managed/assembly_valheim.dll"]);
        Assert.Equal(FileHash.Sha256(chosen), listing.Files["BepInEx/plugins/chosen-mac.dll"]);
        Assert.Equal(before, WorldFixture.Manifest(install));
        Assert.False(File.Exists(Path.Combine(install, "BepInEx", "plugins", "chosen-mac.dll")));
    }


    [Fact] public async Task OneHostedPreparationCopiesOnlySelectedFilesWithoutChangingTheSource()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\one\runtime", staging = @"C:\runs\one\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ServerWindowsExecutable), "server");
        StageLoader(install);
        string sourceConfig = Path.Combine(install, "BepInEx", "config", "BepInEx.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceConfig)!);
        File.WriteAllText(sourceConfig, "source settings");
        string old = Path.Combine(install, "BepInEx", "plugins", "unrelated.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "unrelated");
        Directory.CreateDirectory(Path.Combine(install, "logs"));
        File.WriteAllText(Path.Combine(install, "logs", "connection_log_2456.txt"), "Steam's own, from another day");
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "logs"));
        File.WriteAllText(Path.Combine(install, "BepInEx", "logs", "kept.txt"), "the install's own");
        string chosen = Path.Combine(_root, "chosen.dll");
        File.WriteAllText(chosen, "selected plugin");
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll")], TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), listing.Files["BepInEx/plugins/chosen.dll"]);
        Assert.Equal(File.ReadAllBytes(sourceConfig), File.ReadAllBytes(Path.Combine(host.Local(runtime), "BepInEx", "config", "BepInEx.cfg")));
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(Path.Combine(host.Local(runtime), "BepInEx", "plugins", "unrelated.dll")));
        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("apply-stage", host.Scripts);
        Assert.Contains("copy", host.Scripts);
        // #257: Steam's own runtime output in the install (logs/) stays in the install and is not the server's runtime.
        Assert.Equal("logs", Assert.Single(host.Runs, run => run.Script == "copy").Variables["skip"]);
        Assert.True(File.Exists(Path.Combine(install, "logs", "connection_log_2456.txt")));
        Assert.False(Directory.Exists(Path.Combine(host.Local(runtime), "logs")));
        Assert.DoesNotContain(listing.Files.Keys, file => file.StartsWith("logs/", StringComparison.OrdinalIgnoreCase));
        // A nested logs folder is the game's own: only the install's top level is left out.
        Assert.True(File.Exists(Path.Combine(host.Local(runtime), "BepInEx", "logs", "kept.txt")));

        string explicitConfig = Path.Combine(_root, "explicit-BepInEx.cfg");
        File.WriteAllText(explicitConfig, "explicit settings");
        const string explicitRuntime = @"C:\runs\explicit\runtime", explicitStaging = @"C:\runs\explicit\staging";
        var explicitListing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source,
            explicitRuntime, explicitStaging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll"),
                new HostedRuntimeFile(explicitConfig, BepInExSettings.RelativePath)], TimeSpan.FromSeconds(30));
        Assert.Equal("explicit settings", File.ReadAllText(Path.Combine(host.Local(explicitRuntime),
            "BepInEx", "config", "BepInEx.cfg")));
        Assert.Equal(FileHash.Sha256(explicitConfig), explicitListing.Files[BepInExSettings.RelativePath]);
        Assert.Equal("source settings", File.ReadAllText(sourceConfig));
    }

    [Fact] public async Task ASourceBepInExConfigChangedDuringStagingFailsItsPin()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\config-pin\runtime", staging = @"C:\runs\config-pin\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ServerWindowsExecutable), "server");
        StageLoader(install);
        string config = Path.Combine(install, "BepInEx", "config", "BepInEx.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "pinned source settings");
        string chosen = Path.Combine(_root, "config-pin.dll");
        File.WriteAllText(chosen, "selected plugin");
        host.AfterApply = copy => File.AppendAllText(Path.Combine(copy, "BepInEx", "config", "BepInEx.cfg"), "changed");

        var failure = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/config-pin.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("differs from its pinned source", failure.Message);
        Assert.Equal("pinned source settings", File.ReadAllText(config));
        Assert.False(Directory.Exists(host.Local(runtime)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReviewedLoaderReplacesAnIncoherentSourceOnlyInTheDisposableRuntime(bool packageHasSettings)
    {
        var host = new FakeServerHost("windows-client", Mirror, windows: true);
        const string source = @"C:\game\client", runtime = @"C:\runs\loader\runtime", staging = @"C:\runs\loader\staging";
        string install = host.Local(source);
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ClientWindowsExecutable), "client");
        StageLoader(install);
        string packageRoot = Path.Combine(_root, "approved-loader");
        Directory.CreateDirectory(Path.Combine(packageRoot, "BepInEx/core"));
        File.WriteAllText(Path.Combine(packageRoot, BepInExLoader.Core), "core");
        File.WriteAllText(Path.Combine(packageRoot, BepInExLoader.Preloader), "preloader");
        StageLoader(packageRoot);
        string sourceConfig = Path.Combine(install, "BepInEx", "config", "BepInEx.cfg");
        string packageConfig = Path.Combine(packageRoot, "BepInEx", "config", "BepInEx.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceConfig)!);
        File.WriteAllText(sourceConfig, "source settings");
        if (packageHasSettings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(packageConfig)!);
            File.WriteAllText(packageConfig, "package settings");
        }
        var package = BepInExLoaderPackage.Capture(packageRoot, "test-loader", "1");
        File.WriteAllText(Path.Combine(install, "winhttp.dll"), "target_assembly");
        File.WriteAllText(Path.Combine(install, "doorstop_config.ini"), "[General]\nenabled=true\ntargetAssembly=BepInEx/core/BepInEx.Preloader.dll\n");
        File.WriteAllText(Path.Combine(install, "BepInEx/core/stale.dll"), "must disappear");
        var before = WorldFixture.Manifest(install);
        string plugin = Path.Combine(_root, "selected.dll");
        File.WriteAllText(plugin, "plugin");
        var files = new[] { new HostedRuntimeFile(plugin, "BepInEx/plugins/selected.dll") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostedRuntimeStage.PrepareAsync(host,
            HostedRuntimeKind.Client, source, runtime, staging, files, TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain("copy", host.Scripts);
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, runtime, staging,
            files, TimeSpan.FromSeconds(30), loaderPackage: package);
        foreach (var file in package.Files) Assert.Equal(file.Value, listing.Files[file.Key]);
        Assert.Equal(File.ReadAllBytes(packageHasSettings ? packageConfig : sourceConfig),
            File.ReadAllBytes(Path.Combine(host.Local(runtime), "BepInEx", "config", "BepInEx.cfg")));
        Assert.False(listing.Files.ContainsKey("BepInEx/core/stale.dll"));
        Assert.Equal(package.Loader, HostInstall.Pins(listing).Loader);
        WorldFixture.Verify(install, before);
        Assert.Equal(before.Count, WorldFixture.Manifest(install).Count);
    }

    [Fact] public async Task ALoaderFileOutsideTheReviewedPackageFailsPreparation()
    {
        var host = new FakeServerHost("windows-client", Mirror, windows: true);
        const string source = @"C:\game\client", runtime = @"C:\runs\loader\runtime", staging = @"C:\runs\loader\staging";
        string install = host.Local(source);
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ClientWindowsExecutable), "client");
        StageLoader(install);
        string packageRoot = Path.Combine(_root, "approved-loader");
        Directory.CreateDirectory(Path.Combine(packageRoot, "BepInEx/core"));
        File.WriteAllText(Path.Combine(packageRoot, BepInExLoader.Core), "core");
        File.WriteAllText(Path.Combine(packageRoot, BepInExLoader.Preloader), "preloader");
        StageLoader(packageRoot);
        var package = BepInExLoaderPackage.Capture(packageRoot, "test-loader", "1");
        // A replacement that left a loader file the package does not hold.
        host.AfterApply = prepared =>
        {
            Directory.CreateDirectory(Path.Combine(prepared, "doorstop_libs"));
            File.WriteAllText(Path.Combine(prepared, "doorstop_libs", "libdoorstop_x64.so"), "left behind");
        };
        string plugin = Path.Combine(_root, "selected.dll");
        File.WriteAllText(plugin, "plugin");
        var files = new[] { new HostedRuntimeFile(plugin, "BepInEx/plugins/selected.dll") };
        var error = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, runtime, staging,
            files, TimeSpan.FromSeconds(30), loaderPackage: package));
        Assert.Contains("is not the reviewed package's (" + package.Loader + ")", error.Message);
    }

    [Fact] public async Task ClientPreparationRefusesAServerAndLeavesItsSourceAlone()
    {
        var host = new FakeServerHost("windows-client", Mirror, windows: true);
        const string source = @"C:\game\client", runtime = @"C:\runs\client\runtime", staging = @"C:\runs\client\staging";
        string install = host.Local(source);
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ClientWindowsExecutable), "client");
        StageLoader(install);
        string old = Path.Combine(install, "BepInEx", "plugins", "unrelated.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "unrelated");
        string chosen = Path.Combine(_root, "cli.dll");
        File.WriteAllText(chosen, "selected CLI");
        var files = new[] { new HostedRuntimeFile(chosen, "BepInEx/plugins/cli.dll") };
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, runtime, staging, files, TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), listing.Files["BepInEx/plugins/cli.dll"]);
        Assert.False(listing.Files.ContainsKey("BepInEx/plugins/unrelated.dll"));
        Assert.True(File.Exists(old));
        Assert.False(Directory.Exists(host.Local(staging)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source,
            @"C:\runs\server\runtime", @"C:\runs\server\staging", files, TimeSpan.FromSeconds(30)));
    }

    // The source inspection every campaign preparation starts with: an unrecognised Windows proxy and a Linux install without
    // its Doorstop library are refused before anything is copied, each naming what it found.
    [Fact] public async Task SourceInspectionRefusesAnUnrecognisedProxyAndAnIncompleteLinuxLoader()
    {
        var windows = new FakeServerHost("windows-client", Path.Combine(_root, "windows-source"), windows: true);
        string winSource = windows.Local(@"C:\game\source");
        FakeInstalls.Client(winSource);
        File.WriteAllText(Path.Combine(winSource, GameLaunch.ClientWindowsExecutable), "game");
        File.WriteAllText(Path.Combine(winSource, "winhttp.dll"), "MZ fake");
        File.WriteAllText(Path.Combine(winSource, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        var unrecognised = await Assert.ThrowsAsync<DoorstopPairingException>(() =>
            HostedRuntimeStage.InspectSourceAsync(windows, HostedRuntimeKind.Client, @"C:\game\source", null, TimeSpan.FromSeconds(30)));
        Assert.Contains("not a Doorstop proxy this check recognises", unrecognised.Message);
        // A reviewed package complete for another platform is refused as the package's fault, before the source is listed.
        string linuxPackage = Path.Combine(_root, "linux-package");
        foreach (string relative in new[] { "BepInEx/core/BepInEx.Preloader.dll", "BepInEx/core/BepInEx.dll", "doorstop_libs/libdoorstop_x64.so" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(linuxPackage, relative))!);
            File.WriteAllText(Path.Combine(linuxPackage, relative), relative);
        }
        var wrongPlatform = await Assert.ThrowsAsync<InvalidDataException>(() => HostedRuntimeStage.InspectSourceAsync(windows, HostedRuntimeKind.Client, @"C:\game\source",
            BepInExLoaderPackage.Capture(linuxPackage, "BepInExPack_Valheim", "5.4.2333"), TimeSpan.FromSeconds(30)));
        Assert.Contains("does not match the host platform", wrongPlatform.Message);
        Assert.Equal(1, windows.Runs.Count(run => run.Script == "list")); // the unrecognised proxy's listing only; the package was refused first

        var linux = new FakeServerHost("linux-client", Path.Combine(_root, "linux-source"));
        string linuxSource = linux.Local("/game/source");
        FakeInstalls.Client(linuxSource);
        File.WriteAllText(Path.Combine(linuxSource, GameLaunch.ClientLinuxExecutable), "game");
        var incomplete = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            HostedRuntimeStage.InspectSourceAsync(linux, HostedRuntimeKind.Client, "/game/source", null, TimeSpan.FromSeconds(30)));
        Assert.Contains("doorstop_libs/libdoorstop_x64.so", incomplete.Message);
        FakeInstalls.LinuxLoader(linuxSource);
        await HostedRuntimeStage.InspectSourceAsync(linux, HostedRuntimeKind.Client, "/game/source", null, TimeSpan.FromSeconds(30));
        // A macOS install loads BepInEx through either Doorstop library: BepInExPack's own x64 one is enough.
        var mac = new FakeServerHost("mac-client", Path.Combine(_root, "mac-source"), kind: GameHostKind.Local);
        string macSource = mac.Local("/game/mac");
        FakeInstalls.Client(macSource);
        File.WriteAllText(Path.Combine(macSource, "valheim_Data", "Managed", InstallPins.GameAssemblyName), "game build 1");
        Directory.CreateDirectory(Path.Combine(macSource, "Valheim.app", "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(macSource, "Valheim.app", "Contents", "MacOS", "Valheim"), "game");
        Assert.Contains("doorstop_libs/libdoorstop_x64.dylib or libdoorstop.dylib", (await Assert.ThrowsAsync<FileNotFoundException>(() =>
            HostedRuntimeStage.InspectSourceAsync(mac, HostedRuntimeKind.Client, "/game/mac", null, TimeSpan.FromSeconds(30)))).Message);
        Directory.CreateDirectory(Path.Combine(macSource, "doorstop_libs"));
        File.WriteAllText(Path.Combine(macSource, "doorstop_libs", "libdoorstop_x64.dylib"), "pack doorstop");
        await HostedRuntimeStage.InspectSourceAsync(mac, HostedRuntimeKind.Client, "/game/mac", null, TimeSpan.FromSeconds(30));
        Assert.All(new[] { windows, linux }, host => Assert.DoesNotContain(host.Scripts, script => script is "copy" or "ship" or "start"));
    }

    [Fact] public async Task MacClientBundleCanBePreparedFromItsContainingInstall()
    {
        var host = new FakeServerHost("mac-client", Mirror);
        const string source = "/game/client", runtime = "/runs/vt-one/runtime", staging = "/runs/vt-one/staging";
        string install = host.Local(source);
        string managed = Path.Combine(install, "Valheim.app", "Contents", "Resources", "Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, InstallPins.GameAssemblyName), "mac game");
        string executable = Path.Combine(install, "Valheim.app", "Contents", "MacOS", "Valheim");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "mac executable");
        string core = Path.Combine(install, "BepInEx", "core", "BepInEx.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(core)!);
        File.WriteAllText(core, "core");
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.Preloader.dll"), "preloader");
        FakeInstalls.MacLoader(install);
        string chosen = Path.Combine(_root, "mac-cli.dll");
        File.WriteAllText(chosen, "selected CLI");
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/mac-cli.dll")], TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), listing.Files["BepInEx/plugins/mac-cli.dll"]);
        Assert.True(File.Exists(executable));
    }

    [Fact] public async Task PreparationRejectsMixedLoaderFilesAndSourceNestedStagingBeforeShipping()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\game\server\runs\one\runtime", staging = @"C:\game\server\runs\one\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ServerWindowsExecutable), "server");
        StageLoader(install);
        string chosen = Path.Combine(_root, "chosen.dll");
        File.WriteAllText(chosen, "selected");
        await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll")], TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source,
            @"C:\runs\one\runtime", @"C:\runs\one\staging",
            [new HostedRuntimeFile(chosen, "winhttp.dll")], TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain("copy", host.Scripts);
    }

    [Fact] public async Task FailedPreparationRetiresOnlyItsNewCopyAndStaging()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\failed\runtime", staging = @"C:\runs\failed\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ServerWindowsExecutable), "server");
        StageLoader(install);
        string unrelated = Path.Combine(install, "BepInEx", "plugins", "unrelated.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "keep me");
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected");
        host.Failures["apply-stage"] = FakeServerHost.TransportFailure;
        await Assert.ThrowsAsync<HostOperationException>(() => HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(unrelated));
        Assert.False(Directory.Exists(host.Local(runtime)));
        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("cleanup-stage", host.Scripts);
    }

    [Theory]
    [InlineData("ship")]
    [InlineData("copy")]
    public async Task PartialPreparationIsCleanedAfterTheRemoteOperationThrows(string operation)
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\partial\runtime", staging = @"C:\runs\partial\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ServerWindowsExecutable), "server");
        StageLoader(install);
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected");
        if (operation == "ship") host.AfterShip = _ => throw new IOException("ship reply lost after copy");
        else host.AfterCopy = _ => throw new IOException("copy reply lost after copy");

        var error = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("reply lost", error.Message);
        Assert.False(Directory.Exists(host.Local(runtime)));
        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("cleanup-stage", host.Scripts);
        Assert.True(File.Exists(Path.Combine(install, GameLaunch.ServerWindowsExecutable)));
    }

    [Fact] public async Task FailedCleanupKeepsBothTheOriginalErrorAndTheUnprovenResidue()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\residue\runtime", staging = @"C:\runs\residue\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ServerWindowsExecutable), "server");
        StageLoader(install);
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected");
        host.AfterShip = _ => throw new IOException("ship reply lost after copy");
        host.Failures["cleanup-stage"] = FakeServerHost.TransportFailure;

        var error = await Assert.ThrowsAsync<AggregateException>(() => HostedRuntimeStage.PrepareAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("reply lost", error.InnerExceptions[0].Message);
        Assert.Contains("Cleaning failed preparation", error.InnerExceptions[1].Message);
        Assert.True(Directory.Exists(host.Local(staging)));
    }

    [Fact] public async Task CancelledPreparationStillCleansAStagedRemoteCopy()
    {
        var host = new FakeServerHost("windows-server", Mirror, windows: true);
        const string source = @"C:\game\server", runtime = @"C:\runs\cancelled\runtime", staging = @"C:\runs\cancelled\staging";
        string install = host.Local(source);
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, GameLaunch.ServerWindowsExecutable), "server");
        StageLoader(install);
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected");
        using var cancellation = new CancellationTokenSource();
        host.AfterShip = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostedRuntimeStage.PrepareAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30), cancellation.Token));

        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("cleanup-stage", host.Scripts);
    }
}
