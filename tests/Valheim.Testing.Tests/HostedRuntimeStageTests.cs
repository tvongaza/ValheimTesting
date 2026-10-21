using Valheim.Testing.Game;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Valheim.Testing.GameSessions;

// Classes in this collection check the real process table (this one, and the launch-gone idle checks). Other test classes
// launch stand-ins with the game's names; an idle check cannot race those launches and still have a deterministic verdict.
[CollectionDefinition(nameof(WindowsGameProcessInventory), DisableParallelization = true)]
public sealed class WindowsGameProcessInventory { }

[Collection(nameof(WindowsGameProcessInventory))]
public sealed class HostedRuntimeStageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("host-stage-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Theory]
    [InlineData("boot-1", true)]
    [InlineData("boot-21", true)]
    [InlineData("boot-0", false)]
    [InlineData("boot-01", false)]
    [InlineData("boot-1/other", false)]
    [InlineData("boot-1/../../elsewhere", false)]
    [InlineData("boot-1.old", false)]
    public void RetirementAcceptsOnlyANumberedBootDirectlyUnderItsOwnRun(string name, bool allowed)
    {
        foreach (bool windows in new[] { false, true })
        {
            var host = new FakeServerHost("test", _root, windows: windows);
            string parent = windows ? @"C:\runs\run-evidence" : "/runs/run-evidence";
            string directory = parent + (windows ? @"\" : "/") + name;
            if (allowed) Assert.Equal(parent, HostedRuntimeStage.RequirePrepared(host, directory, "run-evidence"));
            else Assert.Throws<ArgumentException>(() => HostedRuntimeStage.RequirePrepared(host, directory, "run-evidence"));
            Assert.Throws<ArgumentException>(() => HostedRuntimeStage.RequirePrepared(host, directory, "someone-else"));
        }
    }

    [Theory]
    [InlineData("valheim_server", "", true, "VT-GAME idle")]
    [InlineData("valheim_server", "/owned", false, "VT-GAME unknown")]
    [InlineData("/other/valheim_server.x86_64", "/owned", true, "VT-GAME idle")]
    [InlineData("/owned/valheim_server.x86_64", "/owned", false, "VT-GAME busy 4242")]
    [InlineData("/tmp/Valheim.app/Contents/MacOS/Valheim", "", true, "VT-GAME busy 4242")]
    [InlineData("ordinary-helper", "/owned", true, "VT-GAME idle")]
    // One check for every copy on a host (#257): a game running from any of them is busy, from none of them idle.
    [InlineData("/second/valheim_server.x86_64", "/first\n/second", false, "VT-GAME busy 4242")]
    [InlineData("/first/Valheim.app/Contents/MacOS/Valheim", "/first\n/second", false, "VT-GAME busy 4242")]
    [InlineData("/second2/valheim_server.x86_64", "/first\n/second", false, "VT-GAME idle")]
    [InlineData("valheim_server", "/first\n/second", false, "VT-GAME unknown")]
    public async Task BashProcessCheckRefusesConflictingUse(string process, string runtime, bool clientSession, string expected)
    {
        if (OperatingSystem.IsWindows()) return;
        string bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        string ps = Path.Combine(bin, "ps");
        // As ps -axo pid=,comm= prints it: the ID, then the command.
        CopiedProgram.WriteScript(ps, "#!/bin/sh\nprintf '%s\\n' '  4242 " + process + "'\n"); // not File.WriteAllText: ETXTBSY (#723)
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

    // The real bash apply stages a reviewed package in place of the source's loader: the profile's loader pin is the package's.
    [Fact] public async Task MacShellProfileTakesAReviewedLoaderPackageInPlaceOfTheSources()
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
        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Client, source, Path.Combine(run, "runtime"), Path.Combine(run, "staging"),
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30), loaderPackage: package);
        Assert.Equal(package.Loader, prepared.Pins.Loader);
        Assert.Equal(source, prepared.GameRoot);
        // The stage asked about the source bundle the profile launches, read only.
        Assert.Equal(Path.Combine(source, GameLaunch.ClientMacBundle), Assert.Single(host.BundleChecks)["app"]);
        Assert.False(prepared.Loader.Files.ContainsKey("BepInEx/core/stale.dll"));
        Assert.False(prepared.Loader.Files.ContainsKey("doorstop_libs/libdoorstop_x64.dylib"));
        Assert.Equal("old core", File.ReadAllText(Path.Combine(source, "BepInEx", "core", "BepInEx.dll")));
    }

    // The Windows conflicting-use check for real: it runs in pwsh and, with no Valheim here, finds the host idle
    // for a client session and for an owned runtime path alike.
    [Fact] public async Task WindowsProcessCheckRunsAndFindsAnIdleHostIdle()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (SkipForPersonalWindowsGame()) return;
        var host = new LocalGameHost("windows-process", HostShell.Pwsh);
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
        if (SkipForPersonalWindowsGame()) return;
        string first = Path.Combine(_root, "a"), second = Path.Combine(_root, "b c");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        string standIn = Path.Combine(second, "valheim_server.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), standIn);
        var host = new LocalGameHost("windows-process", HostShell.Pwsh);
        // Controlled overlap: first prove the host idle, then introduce a same-named process before the next real
        // inventory read. That later read must report the new process as a conflict, not reuse the earlier answer.
        await HostedRuntimeStage.RequireStoppedAsync(host, TimeSpan.FromSeconds(60), default, runtimes: [first, second], clientSession: false);
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

    // Local native work may coexist with someone's game, but a hosted CI job must not silently skip the real
    // idle/conflict controls. The non-parallel collection above excludes this assembly's stand-in game launches.
    private static bool SkipForPersonalWindowsGame()
    {
        Process[] processes = [.. Process.GetProcessesByName("valheim"), .. Process.GetProcessesByName("valheim_server")];
        try
        {
            if (processes.Length == 0) return false;
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase),
                "The Windows CI process-inventory control found a Valheim-named process before it began; it must not be skipped.");
            return true;
        }
        finally { foreach (Process process in processes) process.Dispose(); }
    }

    // Executes the actual bash profile seed, shipment, listing and apply scripts on macOS. A fake host cannot catch BSD-tool
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
        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Client, source,
            Path.Combine(run, "runtime"), Path.Combine(run, "staging"),
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), prepared.Loader.Files["BepInEx/plugins/selected.dll"]);
        Assert.Equal(Path.Combine(source, GameLaunch.ClientMacBundle), Assert.Single(host.BundleChecks)["app"]);
        Assert.False(prepared.Loader.Files.ContainsKey("BepInEx/plugins/old.dll"));
        Assert.False(prepared.Loader.Files.ContainsKey("Valheim.app/Contents/MacOS/Valheim")); // the game stays in the source
        Assert.Equal(FileHash.Sha256(sourceConfig), prepared.Loader.Files["BepInEx/config/BepInEx.cfg"]);
        Assert.Equal(File.ReadAllBytes(sourceConfig), File.ReadAllBytes(Path.Combine(run, "runtime", "BepInEx", "config", "BepInEx.cfg")));
        Assert.True(File.Exists(old));
        Assert.False(Directory.Exists(Path.Combine(run, "staging")));
        var inspected = await HostedRuntimeStage.InspectSourceAsync(host, HostedRuntimeKind.Client, source, null,
            TimeSpan.FromSeconds(30));
        File.AppendAllText(core, "changed after preflight");
        string changedRun = Path.Combine(_root, "vt-changed");
        var changed = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareProfileAsync(
            host, HostedRuntimeKind.Client, source, Path.Combine(changedRun, "runtime"), Path.Combine(changedRun, "staging"),
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30), inspectedSource: inspected));
        Assert.Contains("changed while it was copied", changed.Message);
        Assert.False(Directory.Exists(Path.Combine(changedRun, "runtime")));
    }

    [Fact] public async Task MacProfileStagesOnlyItsLoaderAndSelectedModWithoutCopyingTheGame()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var host = new LocalGameHost("mac-profile", HostShell.Bash);
        string source = Path.Combine(_root, "profile-source");
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
        string old = Path.Combine(source, "BepInEx", "plugins", "old.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "unselected");
        string config = Path.Combine(source, "BepInEx", "config", "BepInEx.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "[Preloader.Entrypoint]\nType = GameObject\n");
        string selected = Path.Combine(_root, "profile-mod.dll");
        File.WriteAllText(selected, "selected");
        var before = WorldFixture.Manifest(source);
        string parent = Path.Combine(_root, "vt-prep-profile");
        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source,
            Path.Combine(parent, "runtime"), Path.Combine(parent, "staging"),
            [new HostedRuntimeFile(selected, "BepInEx/plugins/profile-mod.dll")], TimeSpan.FromSeconds(30));

        // #612 review: the Mac server's game is linked two folders below its loader (ProfileLayouts.GameFolder), with macOS's own tools.
        Assert.Equal(Path.Combine(parent, "runtime", "game", "server"), prepared.GameRoot);
        Assert.Equal(Path.Combine(parent, "runtime"), prepared.LoaderRoot);
        Assert.Equal(new FileInfo(executable).Length, new FileInfo(Path.Combine(prepared.GameRoot, "valheim_server", "Valheim")).Length);
        Assert.Equal((await host.RunAsync("stat -f %i -- \"$path\"", new Dictionary<string, string> { ["path"] = executable }, TimeSpan.FromSeconds(30))).Stdout,
            (await host.RunAsync("stat -f %i -- \"$path\"", new Dictionary<string, string> { ["path"] = Path.Combine(prepared.GameRoot, "valheim_server", "Valheim") },
                TimeSpan.FromSeconds(30))).Stdout);
        Assert.Equal(InstallPins.Of(source).Game, prepared.Pins.Game);
        Assert.Equal(InstallPins.Of(source).Loader, prepared.Pins.Loader);
        Assert.Equal(FileHash.Sha256(selected), prepared.Loader.Files["BepInEx/plugins/profile-mod.dll"]);
        Assert.Equal(FileHash.Sha256(config), prepared.Loader.Files[BepInExSettings.RelativePath]);
        Assert.False(prepared.Loader.Files.ContainsKey("BepInEx/plugins/old.dll"));
        Assert.False(prepared.Loader.Files.ContainsKey("valheim_server/Data/Managed/assembly_valheim.dll"));
        Assert.True(prepared.Loader.Files.ContainsKey("game/server/valheim_server/Data/Managed/assembly_valheim.dll"));
        Assert.Equal(before, WorldFixture.Manifest(source));
        Assert.False(Directory.Exists(Path.Combine(parent, "staging")));
        await HostedRuntimeStage.RetireAsync(host, prepared.LoaderRoot, Path.Combine(parent, "staging"), TimeSpan.FromSeconds(30));
        Assert.False(Directory.Exists(parent));
    }

    [Fact] public async Task AClientProfileRefusesADamagedMacBundleBeforeWritingAnyProfileFiles()
    {
        var host = new FakeServerHost("mac-profile", Path.Combine(_root, "fake-mac"));
        const string source = "/games/valheim", parent = "/runs/vt-prep-mac-client";
        string app = GameLaunch.ClientMacBundle;
        void Source(string relative, string text)
        {
            string path = host.Local(source + "/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Source(app + "/Contents/MacOS/Valheim", "game executable");
        Source(app + "/Contents/Resources/Data/Managed/" + InstallPins.GameAssemblyName, "game assembly");
        Source("BepInEx/core/BepInEx.dll", "core");
        Source("BepInEx/core/BepInEx.Preloader.dll", "preloader");
        Source("doorstop_libs/libdoorstop_x64.dylib", "doorstop");
        host.MacBundleInspect = "VT-BUNDLE broken 1 " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("invalid signature"));
        string selected = Path.Combine(_root, "selected.dll");
        File.WriteAllText(selected, "selected");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Client, source, parent + "/runtime", parent + "/staging",
            [new HostedRuntimeFile(selected, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("source Valheim.app", error.Message);
        Assert.DoesNotContain("ship", host.Scripts);
        Assert.False(Directory.Exists(host.Local(parent)));
    }

    [Fact] public async Task AnExistingProfileDestinationIsNeverDeletedAfterSeedRefusal()
    {
        if (OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("profile-collision", HostShell.Bash);
        string source = Path.Combine(_root, "collision-source");
        string executable = Path.Combine(source, "valheim_server", "Valheim");
        string assembly = Path.Combine(source, "valheim_server", "Data", "Managed", InstallPins.GameAssemblyName);
        string core = Path.Combine(source, "BepInEx", "core");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        Directory.CreateDirectory(core);
        File.WriteAllText(executable, "server");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.WriteAllText(assembly, "game");
        File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "core");
        File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "preloader");
        FakeInstalls.MacLoader(source);
        string parent = Path.Combine(_root, "vt-prep-collision");
        string runtime = Path.Combine(parent, "runtime");
        Directory.CreateDirectory(runtime);
        string marker = Path.Combine(runtime, "other-run.txt");
        File.WriteAllText(marker, "keep");
        string selected = Path.Combine(_root, "collision-mod.dll");
        File.WriteAllText(selected, "selected");

        var error = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Server, source, runtime, Path.Combine(parent, "staging"),
            [new HostedRuntimeFile(selected, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("already exists", error.Message);
        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.False(Directory.Exists(Path.Combine(parent, "staging")));
    }

    [Fact] public async Task MacProfileRefusesAChangedSourceAndRemovesItsOwnedFiles()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var host = new LocalGameHost("mac-profile", HostShell.Bash);
        string source = Path.Combine(_root, "changed-profile-source");
        string executable = Path.Combine(source, "valheim_server", "Valheim");
        string assembly = Path.Combine(source, "valheim_server", "Data", "Managed", InstallPins.GameAssemblyName);
        string core = Path.Combine(source, "BepInEx", "core");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        Directory.CreateDirectory(core);
        File.WriteAllText(executable, "server");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.WriteAllText(assembly, "game");
        File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "core");
        File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "preloader");
        FakeInstalls.MacLoader(source);
        var inspected = await HostedRuntimeStage.InspectSourceAsync(host, HostedRuntimeKind.Server, source, null, TimeSpan.FromSeconds(30));
        File.AppendAllText(Path.Combine(core, "BepInEx.dll"), "changed");
        string selected = Path.Combine(_root, "changed-profile-mod.dll");
        File.WriteAllText(selected, "selected");
        string parent = Path.Combine(_root, "vt-prep-changed");
        var error = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Server, source, Path.Combine(parent, "runtime"), Path.Combine(parent, "staging"),
            [new HostedRuntimeFile(selected, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30),
            inspectedSource: inspected));
        Assert.Contains("changed while it was copied", error.Message);
        Assert.False(Directory.Exists(parent));
    }

    [Fact] public async Task WindowsProfileLinksGameFilesButCopiesItsLoaderAndSelectedMod()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("windows-profile", HostShell.Pwsh);
        string source = Path.Combine(_root, "windows-profile-source");
        FakeInstalls.Client(source);
        File.WriteAllText(Path.Combine(source, GameLaunch.ClientWindowsExecutable), "game executable");
        StageLoader(source);
        File.WriteAllText(Path.Combine(source, "version.dll"), "another mod manager proxy");
        File.WriteAllText(Path.Combine(source, "newsteamthing64.dll"), "a root DLL the profile does not know");
        File.WriteAllText(Path.Combine(source, "tier0_s64.dll"), "steam runtime");
        File.WriteAllText(Path.Combine(source, "vstdlib_s64.dll"), "steam runtime");
        string originalMod = Path.Combine(source, "BepInEx", "plugins", "original.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(originalMod)!);
        File.WriteAllText(originalMod, "unselected");
        string selected = Path.Combine(_root, "windows-profile-mod.dll");
        File.WriteAllText(selected, "selected");
        var before = WorldFixture.Manifest(source);
        string parent = Path.Combine(_root, "vt-prep-windows-profile");
        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Client, source,
            Path.Combine(parent, "runtime"), Path.Combine(parent, "staging"),
            [new HostedRuntimeFile(selected, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(45));

        Assert.Equal(source, prepared.Source.Root);
        Assert.Equal(prepared.GameRoot, prepared.LoaderRoot);
        Assert.NotEqual(source, prepared.GameRoot);
        Assert.Equal(InstallPins.Of(source).Game, prepared.Pins.Game);
        Assert.Equal(InstallPins.Of(source).Loader, prepared.Pins.Loader);
        Assert.Equal(FileHash.Sha256(selected), prepared.Loader.Files["BepInEx/plugins/selected.dll"]);
        Assert.False(prepared.Loader.Files.ContainsKey("BepInEx/plugins/original.dll"));
        Assert.False(prepared.Loader.Files.ContainsKey("version.dll"));
        Assert.False(prepared.Loader.Files.ContainsKey("newsteamthing64.dll"));
        var unknown = Assert.Single(prepared.LeftOut, item => item.Path == "newsteamthing64.dll");
        Assert.Equal("newsteamthing64.dll", unknown.Path);
        Assert.Contains("unknown root DLL", unknown.Reason);
        Assert.DoesNotContain(prepared.LeftOut, item => item.Path == "BepInEx/**");
        Assert.Equal(FileHash.Sha256(Path.Combine(source, "tier0_s64.dll")), prepared.Loader.Files["tier0_s64.dll"]);
        Assert.Equal(FileHash.Sha256(Path.Combine(source, "vstdlib_s64.dll")), prepared.Loader.Files["vstdlib_s64.dll"]);
        Assert.Equal(before, WorldFixture.Manifest(source));
        Assert.False(Directory.Exists(Path.Combine(parent, "staging")));
        string retirementOutput = Path.Combine(_root, "windows-profile-retirement");
        await new RunRetirement(null, retirementOutput).HostAsync("windows-profile", host, prepared.Loader,
            prepared.LoaderRoot, launched: true, serverStopped: true);
        var changes = JsonDocument.Parse(File.ReadAllText(Path.Combine(retirementOutput, "runtime-changes", "changes.json"))).RootElement;
        Assert.Equal(0, changes.GetProperty("Added").GetArrayLength());
        Assert.Equal(0, changes.GetProperty("Changed").GetArrayLength());
        Assert.Equal(0, changes.GetProperty("Missing").GetArrayLength());
        Assert.Equal(0, changes.GetProperty("KeptBytes").GetInt64());
        Assert.False(Directory.Exists(prepared.LoaderRoot));
        await HostedRuntimeStage.RetireAsync(host, prepared.LoaderRoot, Path.Combine(parent, "staging"), TimeSpan.FromSeconds(45));
        Assert.False(Directory.Exists(parent));
        Assert.Equal(before, WorldFixture.Manifest(source));
    }

    // Synthetic minimal client listing: no private run in the available evidence held a client-file manifest. It covers the
    // corresponding client launch classes without claiming to reproduce an installed client.
    [Fact] public void WindowsProfileLinksOnlyTheClientLaunchInputs()
    {
        string[] listing =
        [
            "valheim.exe", "UnityCrashHandler64.exe", "UnityPlayer.dll", "steam_api64.dll", "steamwebrtc64.dll",
            "D3D12/D3D12Core.dll",
            "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll", "MonoBleedingEdge/etc/mono/config",
            "valheim_Data/boot.config", "valheim_Data/Managed/assembly_valheim.dll",
            "valheim_Data/Plugins/x86_64/party.dll", "diagnostics/output_log.txt",
            "valheim_server_Data/Managed/not-this-install.dll", "readme.md",
        ];

        var plan = HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Windows, listing);

        Assert.Equal(new[]
        {
            "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll", "MonoBleedingEdge/etc/mono/config", "UnityCrashHandler64.exe",
            "D3D12/D3D12Core.dll", "UnityPlayer.dll", "steam_api64.dll", "steamwebrtc64.dll", "valheim.exe", "valheim_Data/Managed/assembly_valheim.dll",
            "valheim_Data/Plugins/x86_64/party.dll", "valheim_Data/boot.config",
        }.Order(StringComparer.Ordinal), plan.Links);
        Assert.Equal(new[] { "diagnostics/**", "readme.md", "valheim_server_Data/**" }, plan.LeftOut.Select(item => item.Path));
        Assert.All(plan.LeftOut.Where(item => item.Path.EndsWith("/**", StringComparison.Ordinal)),
            item => Assert.StartsWith("1 file left out:", item.Reason));
    }

    // File names from the top two levels of a real prepared Windows dedicated-server manifest, plus the real nested plugin
    // location that proves steam_api64.dll is not a server-root file. No hashes or machine paths are fixture data.
    [Fact] public void WindowsProfileLinksOnlyTheDedicatedServerLaunchInputs()
    {
        string[] serverListing =
        [
            ".doorstop_version", "UnityCrashHandler64.exe", "UnityPlayer.dll", "Valheim Dedicated Server Manual.pdf",
            "doorstop_config.ini", "start_headless_server.bat", "steam_appid.txt", "steamclient.dll", "steamclient64.dll",
            "steamwebrtc.dll", "steamwebrtc64.dll", "tier0_s.dll", "tier0_s64.dll", "valheim_server.exe", "vstdlib_s.dll",
            "vstdlib_s64.dll", "winhttp.dll", "config/config.vdf", "D3D12/D3D12Core.dll",
            "doorstop_libs/libdoorstop_x64.dylib", "doorstop_libs/libdoorstop_x64.so",
            "valheim_server_Data/app.info", "valheim_server_Data/boot.config", "valheim_server_Data/globalgamemanagers",
            "valheim_server_Data/globalgamemanagers.assets", "valheim_server_Data/level0", "valheim_server_Data/resources.assets",
            "valheim_server_Data/RuntimeInitializeOnLoads.json", "valheim_server_Data/ScriptingAssemblies.json",
            "valheim_server_Data/sharedassets0.assets", "valheim_server_Data/Plugins/Steamworks.NET.txt",
            "valheim_server_Data/Plugins/x86_64/libonigwrap.dll", "valheim_server_Data/Plugins/x86_64/lib_burst_generated.dll",
            "valheim_server_Data/Plugins/x86_64/Microsoft.Xbox.Services.141.GDK.C.Thunks.dll",
            "valheim_server_Data/Plugins/x86_64/Party.dll", "valheim_server_Data/Plugins/x86_64/PartyWin32.dll",
            "valheim_server_Data/Plugins/x86_64/PartyXboxLive.dll", "valheim_server_Data/Plugins/x86_64/PartyXboxLiveWin32.dll",
            "valheim_server_Data/Plugins/x86_64/steam_api64.dll", "valheim_server_Data/Plugins/x86_64/XCurl.dll",
            "valheim_server_Data/Plugins/x86_64/XGamingRuntimeThunks.dll",
        ];
        string[] expectedLinks =
        [
            "D3D12/D3D12Core.dll", "UnityCrashHandler64.exe", "UnityPlayer.dll", "steamclient64.dll", "steamwebrtc64.dll",
            "tier0_s64.dll", "valheim_server.exe", "vstdlib_s64.dll", "valheim_server_Data/app.info",
            "valheim_server_Data/boot.config", "valheim_server_Data/globalgamemanagers", "valheim_server_Data/globalgamemanagers.assets",
            "valheim_server_Data/level0", "valheim_server_Data/resources.assets", "valheim_server_Data/RuntimeInitializeOnLoads.json",
            "valheim_server_Data/ScriptingAssemblies.json", "valheim_server_Data/sharedassets0.assets",
            "valheim_server_Data/Plugins/x86_64/libonigwrap.dll", "valheim_server_Data/Plugins/x86_64/lib_burst_generated.dll",
            "valheim_server_Data/Plugins/x86_64/Microsoft.Xbox.Services.141.GDK.C.Thunks.dll",
            "valheim_server_Data/Plugins/x86_64/Party.dll", "valheim_server_Data/Plugins/x86_64/PartyWin32.dll",
            "valheim_server_Data/Plugins/x86_64/PartyXboxLive.dll", "valheim_server_Data/Plugins/x86_64/PartyXboxLiveWin32.dll",
            "valheim_server_Data/Plugins/x86_64/steam_api64.dll", "valheim_server_Data/Plugins/x86_64/XCurl.dll",
            "valheim_server_Data/Plugins/x86_64/XGamingRuntimeThunks.dll",
        ];
        var plan = HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Windows, serverListing);
        Assert.Equal(expectedLinks.Order(StringComparer.Ordinal), plan.Links);
        Assert.DoesNotContain("steam_api64.dll", plan.Links);
        Assert.Contains("valheim_server_Data/Plugins/x86_64/steam_api64.dll", plan.Links);
        Assert.Contains(plan.LeftOut, item => item.Path == "config/**" && item.Reason.StartsWith("1 file left out:"));
        Assert.DoesNotContain(plan.LeftOut, item => item.Path == "doorstop_libs/**");
        Assert.Contains(plan.LeftOut, item => item.Path == "valheim_server_Data/**" && item.Reason.StartsWith("1 file left out:"));
        foreach (string dll in new[] { "steamclient.dll", "steamwebrtc.dll", "tier0_s.dll", "vstdlib_s.dll" })
            Assert.Equal("32-bit Steam client library, not loaded by the 64-bit game",
                Assert.Single(plan.LeftOut, item => item.Path == dll).Reason);
        // A case-insensitive host lists the same files in another spelling.
        var spelled = HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Windows, ["d3d12/D3D12Core.DLL", "STEAMWEBRTC64.DLL", "TIER0_S64.DLL", "VSTDLIB_S64.dll", "Version.dll"]);
        Assert.Equal(new[] { "STEAMWEBRTC64.DLL", "TIER0_S64.DLL", "VSTDLIB_S64.dll", "d3d12/D3D12Core.DLL" }.Order(StringComparer.Ordinal), spelled.Links);
        Assert.Empty(spelled.LeftOut);
    }

    [Fact] public void WindowsProfileNeverLinksFilesTheGameOrSteamMayWrite()
    {
        string[] writable =
        [
            "output_log.txt", "Player.log", "server-notes.TXT", "logs/server.log", "logs/connection_log_123.txt",
            "crash.dmp", "error.MDMP", "core.dump", "crashdumps/report.dmp", "BepInEx/LogOutput.log",
            "BepInEx/config/BepInEx.cfg", "BepInEx/plugins/cache.bin", "diagnostics/output_log.txt",
            "valheim.exe", "valheim_Data/diagnostics/output_log.txt", "valheim_Data/crashdumps/report.dmp",
        ];

        var plan = HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Windows, writable);

        Assert.Equal(new[] { "valheim.exe" }, plan.Links);
        Assert.Equal(new[] { "Player.log", "core.dump", "crash.dmp", "crashdumps/**", "diagnostics/**", "error.MDMP", "logs/**",
            "output_log.txt", "server-notes.TXT", "valheim_Data/**" }, plan.LeftOut.Select(item => item.Path));
        Assert.DoesNotContain(plan.LeftOut, item => item.Path == "BepInEx/**");
        Assert.Contains(plan.LeftOut, item => item.Path == "logs/**" && item.Reason.StartsWith("2 files left out:"));
        Assert.Contains(plan.LeftOut, item => item.Path == "valheim_Data/**" && item.Reason.StartsWith("2 files left out:"));
    }

    // A root DLL the profile does not know is left out of the launch folder, and the run says so with what to do about it:
    // a DLL the game needs and the profile dropped fails inside Steam or Unity start-up with no other hint (#651, #689).
    [Fact] public void WindowsProfileRecordsAnUnknownRootDllAndAggregatesNestedOmissions()
    {
        string[] listing =
        [
            "valheim_server.exe", "UnityPlayer.dll", "steam_api64.dll", "valheim_server_Data/Managed/assembly_valheim.dll",
            "version.dll", "WINMM.dll", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "doorstop_libs/x64/winhttp.dll",
            "BepInEx/core/BepInEx.dll", "newsteamthing64.dll", "crash.log", "logs/server.log",
            "valheim_server_Data/Plugins/x86_64/nested.dll",
        ];
        var plan = HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Windows, listing);

        Assert.Equal(new[] { "crash.log", "logs/**", "newsteamthing64.dll" },
            plan.LeftOut.Select(item => item.Path));
        var unknown = Assert.Single(plan.LeftOut, item => item.Path == "newsteamthing64.dll");
        Assert.Contains("unknown root DLL", unknown.Reason);
        Assert.Contains("WindowsGameRootDlls", unknown.Reason);
        Assert.Equal("a log file", Assert.Single(plan.LeftOut, item => item.Path == "crash.log").Reason);
        Assert.DoesNotContain("newsteamthing64.dll", plan.Links);
        Assert.Contains("valheim_server_Data/Plugins/x86_64/nested.dll", plan.Links);
        // The text a run keeps names the file and the reason.
        string evidence = HostedRuntimeStage.ProfileLeftOut.Describe(plan.LeftOut)!;
        Assert.DoesNotContain("loader file", evidence);
        Assert.Contains("newsteamthing64.dll: an unknown root DLL", evidence);
        Assert.Null(HostedRuntimeStage.ProfileLeftOut.Describe(
            HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Windows, ["valheim.exe", "UnityPlayer.dll", "version.dll", "winhttp.dll"]).LeftOut));
    }

    // Positive selection already omits every unrelated nested folder. Those omissions are evidence even though RuntimeSkips
    // now belongs only to a standalone hosted server's install comparison (#689).
    [Fact] public void WindowsProfileRecordsEachTopLevelFolderItDoesNotLink()
    {
        string[] listing = ["valheim.exe", "logs/steam.txt", "crashdumps/a.dmp", "cache/temporary.bin", "cache/other.bin"];
        var plan = HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Windows, listing);
        Assert.Equal(new[] { "valheim.exe" }, plan.Links);
        Assert.Equal(new[] { "cache/**", "crashdumps/**", "logs/**" }, plan.LeftOut.Select(item => item.Path));
        Assert.StartsWith("2 files left out:", plan.LeftOut[0].Reason);
        // A standalone hosted run's runtime copy uses the same skip test.
        Assert.True(HostInstall.Skipped("logs/steam.txt", HostedRuntimeStage.RuntimeSkips, StringComparer.Ordinal));
    }

    // #612: a Linux dedicated server's profile is linked like a Windows one, by the same rules from its own table: the executable,
    // its data tree, linux64 (the Steam client library) and the root libraries the game needs. Logs, text, crash dumps, the loader
    // and the start scripts never become links.
    [Fact] public void LinuxProfileLinksOnlyTheDedicatedServerLaunchInputs()
    {
        string[] listing =
        [
            "valheim_server.x86_64", "UnityPlayer.so", "libparty.so", "linux64/steamclient.so", "steam_appid.txt",
            "start_server.sh", "start_server_xterm.sh", "start_server_bepinex.sh", "Valheim Dedicated Server Manual.pdf",
            "valheim_server_Data/boot.config", "valheim_server_Data/Managed/assembly_valheim.dll",
            "valheim_server_Data/Plugins/libparty.so", "valheim_server_Data/Plugins/libsteam_api.so",
            "valheim_server_Data/output_log.txt", "valheim_server_Data/crashdumps/core.dmp", "valheim_server_Data/Player.log",
            "doorstop_libs/libdoorstop_x64.so", ".doorstop_version", "BepInEx/core/BepInEx.Preloader.dll", "BepInEx/LogOutput.log",
            "logs/stats_log.txt", "preloader_20261020.log", "core.dump",
        ];

        var plan = HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Linux, listing);

        Assert.Equal(new[]
        {
            "UnityPlayer.so", "libparty.so", "linux64/steamclient.so", "valheim_server.x86_64", "valheim_server_Data/Managed/assembly_valheim.dll",
            "valheim_server_Data/Plugins/libparty.so", "valheim_server_Data/Plugins/libsteam_api.so", "valheim_server_Data/boot.config",
        }.Order(StringComparer.Ordinal), plan.Links);
        Assert.Equal(new[]
        {
            "Valheim Dedicated Server Manual.pdf", "core.dump", "logs/**", "preloader_20261020.log", "start_server.sh",
            "start_server_bepinex.sh", "start_server_xterm.sh", "steam_appid.txt", "valheim_server_Data/**",
        }, plan.LeftOut.Select(item => item.Path));
        Assert.Equal("not an input to the Linux game launch", Assert.Single(plan.LeftOut, item => item.Path == "start_server.sh").Reason);
        Assert.Equal("3 files left out: a crash dump; a log file; a writable text output",
            Assert.Single(plan.LeftOut, item => item.Path == "valheim_server_Data/**").Reason);
        Assert.DoesNotContain(plan.LeftOut, item => item.Path is "doorstop_libs/**" or "BepInEx/**" or ".doorstop_version");
    }

    // An unknown root library is left out with what to do about it, as an unknown root DLL is on Windows (#651). Linux names are
    // case-sensitive: another spelling of a game file is not the game's.
    [Fact] public void LinuxProfileRecordsAnUnknownRootLibraryAndComparesNamesExactly()
    {
        var plan = HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Linux,
            ["valheim_server.x86_64", "UnityPlayer.so", "libnewsteam.so", "unityplayer.SO", "Linux64/steamclient.so"]);

        Assert.Equal(new[] { "UnityPlayer.so", "valheim_server.x86_64" }, plan.Links);
        var unknown = Assert.Single(plan.LeftOut, item => item.Path == "libnewsteam.so");
        Assert.Contains("unknown root library", unknown.Reason);
        Assert.Contains("HostedRuntimeStage.LinuxGameRootLibraries", unknown.Reason);
        Assert.Contains(plan.LeftOut, item => item.Path == "unityplayer.SO");
        Assert.Contains(plan.LeftOut, item => item.Path == "Linux64/**");
    }

    // #612 review: the Mac dedicated server's profile is linked too. Its whole valheim_server folder (the executable, its
    // libraries, data and bundles) is linked; its root holds only Steam's folders, the manual and the start script, left out.
    [Fact] public void MacServerProfileLinksItsWholeServerFolder()
    {
        string[] listing =
        [
            "valheim_server/Valheim", "valheim_server/libmono-native.dylib", "valheim_server/UnityPlayer.dylib",
            "valheim_server/Data/Managed/assembly_valheim.dll", "valheim_server/Data/boot.config", "valheim_server/.DS_Store",
            "valheim_server/MonoBleedingEdge/etc/mono/config", "valheim_server/PlugIns/libparty.bundle/Contents/MacOS/libparty",
            "valheim_server/Data/output_log.txt", ".DS_Store", "steamapps/appmanifest_896660.acf", "EmptySteamDepot/readme",
            "Valheim Dedicated Server Manual.pdf", "start_server.command", "steam_appid.txt", "libdoorstop.dylib",
            "BepInEx/core/BepInEx.Preloader.dll", "libextra.dylib",
        ];

        var plan = HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.MacOS, listing);

        Assert.Equal(new[]
        {
            "valheim_server/Data/Managed/assembly_valheim.dll", "valheim_server/Data/boot.config", "valheim_server/MonoBleedingEdge/etc/mono/config",
            "valheim_server/PlugIns/libparty.bundle/Contents/MacOS/libparty", "valheim_server/UnityPlayer.dylib", "valheim_server/Valheim",
            "valheim_server/libmono-native.dylib",
        }.Order(StringComparer.Ordinal), plan.Links);
        Assert.Equal(new[] { ".DS_Store", "EmptySteamDepot/**", "Valheim Dedicated Server Manual.pdf", "libextra.dylib", "start_server.command",
            "steam_appid.txt", "steamapps/**", "valheim_server/**" }.Order(StringComparer.Ordinal), plan.LeftOut.Select(item => item.Path));
        Assert.Equal("2 files left out: Finder's folder metadata; a writable text output",
            Assert.Single(plan.LeftOut, item => item.Path == "valheim_server/**").Reason);
        Assert.Contains("unknown root library", Assert.Single(plan.LeftOut, item => item.Path == "libextra.dylib").Reason);
        Assert.Equal("not an input to the macOS game launch", Assert.Single(plan.LeftOut, item => item.Path == "start_server.command").Reason);
        // #612 review 6: its native code, re-checked before each boot, is the executable and the libraries beside it.
        Assert.Equal(new[] { "valheim_server/UnityPlayer.dylib", "valheim_server/Valheim", "valheim_server/libmono-native.dylib" }, plan.Checked);
    }

    // The re-checked native code on Windows and Linux stays every root file the profile linked.
    [Fact] public void WindowsAndLinuxRecheckEveryLinkedRootFile()
    {
        Assert.Equal(new[] { "UnityPlayer.so", "valheim_server.x86_64" }, HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Linux,
            ["valheim_server.x86_64", "UnityPlayer.so", "linux64/steamclient.so", "valheim_server_Data/Managed/assembly_valheim.dll"]).Checked);
        Assert.Equal(new[] { "UnityPlayer.dll", "valheim.exe" }, HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.Windows,
            ["valheim.exe", "UnityPlayer.dll", "valheim_Data/boot.config"]).Checked);
    }

    // #612: one decision between a linked profile and one that launches its source executable, from the host's platform.
    [Theory]
    [InlineData("windows", true, true)]
    [InlineData("windows", false, true)]
    [InlineData("linux", true, true)]
    [InlineData("macos", true, true)]
    [InlineData("macos", false, false)]
    public void TheHostPlatformDecidesTheProfileLayout(string platform, bool server, bool linked) =>
        Assert.Equal(linked ? ProfileLayout.Linked : ProfileLayout.SourceExecutable, ProfileLayouts.For(platform, server));

    // One disposable profile per actor, prepared on a fake host (HostedRuntimeStage.PrepareProfileAsync): the selected files, the loader.
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

        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen-mac.dll")], TimeSpan.FromSeconds(30));

        Assert.Equal(ServerPlatform.MacOS, HostInstall.DetectServer(prepared.Game));
        Assert.Equal(runtime + "/game/server", prepared.GameRoot); // #612 review: linked, two folders down
        Assert.Equal(FileHash.Sha256(assembly), prepared.Game.Files["valheim_server/Data/Managed/assembly_valheim.dll"]);
        Assert.Equal(FileHash.Sha256(chosen), prepared.Loader.Files["BepInEx/plugins/chosen-mac.dll"]);
        Assert.DoesNotContain("valheim_server/Data/Managed/assembly_valheim.dll", prepared.Loader.Files.Keys);
        Assert.Equal(before, WorldFixture.Manifest(install));
        Assert.False(File.Exists(Path.Combine(install, "BepInEx", "plugins", "chosen-mac.dll")));
    }


    // A Linux dedicated server's install as a profile reads it: the executable, Unity's player, the data tree, the Steam client
    // library, the loader, and the start scripts and Steam's app id that the launch does not need.
    private static void LinuxServerInstall(string install)
    {
        FakeInstalls.Server(install);
        FakeInstalls.LinuxLoader(install);
        foreach (string relative in new[] { GameLaunch.ServerLinuxExecutable, "UnityPlayer.so", "linux64/steamclient.so",
            "valheim_server_Data/Plugins/libparty.so", "start_server.sh", "steam_appid.txt", "BepInEx/core/BepInEx.dll",
            "BepInEx/core/BepInEx.Preloader.dll" })
        {
            string path = Path.Combine(install, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relative);
        }
    }

    // #612: a Linux server's profile is its launch folder, as a Windows one is: the game's files are linked into it (the seed's
    // gameFiles), the loader and selected plugin copied, so the launch's working directory, executable, library path and
    // command line all name the profile. Paths.GameRootPath, and the preloader log BepInEx writes beside the executable, are
    // the profile's.
    [Fact] public async Task ALinuxServerProfileIsItsLaunchFolderWithTheGameFilesLinked()
    {
        var host = new FakeServerHost("linux-server", Mirror);
        const string source = "/srv/valheim", runtime = "/srv/runs/linked/runtime", staging = "/srv/runs/linked/staging";
        string install = host.Local(source);
        LinuxServerInstall(install);
        string chosen = Path.Combine(_root, "chosen-linux.dll");
        File.WriteAllText(chosen, "selected plugin");
        var before = WorldFixture.Manifest(install);

        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen-linux.dll")], TimeSpan.FromSeconds(30));

        Assert.Equal((runtime, runtime), (prepared.GameRoot, prepared.LoaderRoot));
        Assert.Equal(source, prepared.Source.Root);
        Assert.Equal(source + "\n" + runtime, Assert.Single(host.Runs, run => run.Script == "profile-storage").Variables["paths"]);
        var seed = Assert.Single(host.Runs, run => run.Script == "profile-seed");
        string[] Decoded(string variable) => seed.Variables[variable].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line))).Order(StringComparer.Ordinal).ToArray();
        string[] links = Decoded("gameFiles");
        Assert.All(new[] { "UnityPlayer.so", "linux64/steamclient.so", GameLaunch.ServerLinuxExecutable,
            "valheim_server_Data/Managed/" + InstallPins.GameAssemblyName, "valheim_server_Data/Plugins/libparty.so" }, path => Assert.Contains(path, links));
        Assert.All(links, path => Assert.True(path is "UnityPlayer.so" or "linux64/steamclient.so" or GameLaunch.ServerLinuxExecutable ||
            path.StartsWith("valheim_server_Data/", StringComparison.Ordinal), path));
        Assert.Empty(Decoded("files").Where(path => !path.StartsWith("BepInEx/core/", StringComparison.Ordinal) && !path.StartsWith("doorstop_libs/", StringComparison.Ordinal)));
        Assert.Contains(prepared.LeftOut, item => item.Path == "start_server.sh");
        Assert.Contains(GameLaunch.ServerLinuxExecutable, prepared.Game.Files.Keys);
        Assert.Contains(GameLaunch.ServerLinuxExecutable, prepared.Game.Executables);
        Assert.Equal(FileHash.Sha256(chosen), prepared.Loader.Files["BepInEx/plugins/chosen-linux.dll"]);
        Assert.True(File.Exists(Path.Combine(host.Local(runtime), GameLaunch.ServerLinuxExecutable)));
        Assert.Equal(before, WorldFixture.Manifest(install));

        var launch = GameLaunch.ForServerWithLoader(prepared.GameRoot, prepared.LoaderRoot, ["-batchmode"], null, ServerPlatform.Linux, linked: true);
        Assert.Equal(runtime, launch.WorkingDirectory);
        Assert.Equal(runtime + "/" + GameLaunch.ServerLinuxExecutable, launch.Executable);
        Assert.Equal(runtime + "/linux64:" + runtime + "/doorstop_libs", launch.Prepended["LD_LIBRARY_PATH"]);
        Assert.Equal(runtime + "/BepInEx/core/BepInEx.Preloader.dll", launch.Environment["DOORSTOP_TARGET_ASSEMBLY"]);
        // Still a profile: the runner's injected libraries are dropped, as from a macOS profile's separate loader root.
        Assert.Contains("LD_PRELOAD", launch.Unset);
        Assert.Contains("LD_LIBRARY_PATH", launch.Unset);
        Assert.Equal(HostProcessProbe.ExpectedCommandLineSha256(false, runtime + "/" + GameLaunch.ServerLinuxExecutable, ["-batchmode"]),
            launch.CommandLineSha256());
    }

    // #612 review: a Mac dedicated server's profile is linked too, two folders down (ProfileLayouts.GameFolder). BepInEx on macOS
    // takes Paths.GameRootPath as the executable's fourth parent, which must be the profile, where its loader and preloader log
    // go; with the source's own layout that was two folders above the install. The launch starts the profile's linked
    // executable from the profile, with its libraries beside it. (A Mac client still launches its source bundle:
    // MacShellProfileTakesAReviewedLoaderPackageInPlaceOfTheSources and MacShellStagesASelectedClientWithoutRunningTheGame assert GameRoot is the source.)
    [Fact] public async Task AMacServerProfileIsLinkedSoItsGameRootIsTheProfile()
    {
        var host = new FakeServerHost("mac-server", Mirror);
        const string source = "/game/mac-server", runtime = "/runs/mac-linked/runtime", staging = "/runs/mac-linked/staging";
        string install = host.Local(source);
        string executable = Path.Combine(install, "valheim_server", "Valheim");
        Directory.CreateDirectory(Path.Combine(install, "valheim_server", "Data", "Managed"));
        File.WriteAllText(executable, "mac server");
        File.WriteAllText(Path.Combine(install, "valheim_server", "Data", "Managed", InstallPins.GameAssemblyName), "mac game assembly");
        File.WriteAllText(Path.Combine(install, "start_server.command"), "#!/bin/sh");
        FakeInstalls.MacLoader(install);
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.dll"), "core");
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.Preloader.dll"), "preloader");
        string chosen = Path.Combine(_root, "chosen-mac-linked.dll");
        File.WriteAllText(chosen, "selected plugin");
        var before = WorldFixture.Manifest(install);

        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen-mac-linked.dll")], TimeSpan.FromSeconds(30));

        const string game = runtime + "/game/server";
        Assert.Equal((game, runtime), (prepared.GameRoot, prepared.LoaderRoot));
        Assert.Equal(source + "\n" + runtime, Assert.Single(host.Runs, run => run.Script == "profile-storage").Variables["paths"]);
        var seed = Assert.Single(host.Runs, run => run.Script == "profile-seed");
        Assert.Equal(game, seed.Variables["game"]);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(GameLaunch.ServerMacExecutable)), seed.Variables["gameFiles"].Split('\n'));
        Assert.True(File.Exists(host.Local(game + "/" + GameLaunch.ServerMacExecutable)));
        Assert.False(File.Exists(host.Local(runtime + "/" + GameLaunch.ServerMacExecutable)));
        Assert.Contains(prepared.LeftOut, item => item.Path == "start_server.command");
        Assert.Equal(ServerPlatform.MacOS, HostInstall.DetectServer(prepared.Game));
        Assert.Equal(prepared.Source.Files[GameLaunch.ServerMacExecutable], prepared.Game.Files[GameLaunch.ServerMacExecutable]);
        // The loader's listing carries the links under their folder, so retirement tells them from what the run writes.
        Assert.Equal(prepared.Source.Files[GameLaunch.ServerMacExecutable], prepared.Loader.Files["game/server/" + GameLaunch.ServerMacExecutable]);
        Assert.Equal(FileHash.Sha256(chosen), prepared.Loader.Files["BepInEx/plugins/chosen-mac-linked.dll"]);
        Assert.Equal(InstallPins.Of(install).Game, prepared.Pins.Game);
        Assert.Equal(before, WorldFixture.Manifest(install));

        var launch = GameLaunch.ForServerWithLoader(prepared.GameRoot, prepared.LoaderRoot, [], null, ServerPlatform.MacOS, linked: true);
        Assert.Equal(game + "/" + GameLaunch.ServerMacExecutable, launch.Executable);
        string gameRootPath = launch.Executable;
        for (int up = 0; up < 4; up++) gameRootPath = HostPath.Parent(gameRootPath);
        Assert.Equal(runtime, gameRootPath); // BepInEx's ParentDirectory(executable, 4)
        Assert.Equal(runtime, launch.WorkingDirectory);
        Assert.StartsWith(game + "/valheim_server", launch.Prepended["DYLD_LIBRARY_PATH"]);
        Assert.Equal(runtime + "/libdoorstop.dylib", launch.Prepended["DYLD_INSERT_LIBRARIES"]);
        Assert.Contains("DYLD_INSERT_LIBRARIES", launch.Unset);
    }

    // A Linux runtime the links cannot reach is refused before anything is staged on the host, in the Windows refusal's words.
    [Fact] public async Task ALinuxServerProfileOnAnotherMountIsRefusedBeforeStaging()
    {
        var host = new FakeServerHost("linux-server", Mirror);
        const string source = "/srv/valheim", runtime = "/home/tester/runs/other/runtime", staging = "/home/tester/runs/other/staging";
        LinuxServerInstall(host.Local(source));
        host.UnixVolumes["/srv"] = new UnixStorageVolume("/srv", "2065", "ext4", Writable: true);
        string chosen = Path.Combine(_root, "chosen-other-mount.dll");
        File.WriteAllText(chosen, "selected plugin");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging, [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll")],
            TimeSpan.FromSeconds(30)));

        Assert.Equal("the default runtime folder /home/tester/runs/other/runtime is on / (ext4, device 2049), not the source install's mount " +
            "/srv (ext4, device 2065). A Linux profile hard-links the install's game files, and a hard link cannot cross mounts. " +
            "Put the default runtime on the install's mount /srv.", error.Message);
        Assert.DoesNotContain(host.Scripts, script => script is "list" or "ship" or "profile-seed" or "apply-stage");
        Assert.False(Directory.Exists(host.Local(runtime)));
    }

    // #612 review: fs.protected_hardlinks (Linux's default) refuses a link to an install this user does not own, and no listing
    // shows it. The storage probe's one trial link finds it, so the profile is refused before anything is staged, saying why.
    [Fact] public async Task ALinuxServerInstallThisUserCannotLinkIsRefusedBeforeStaging()
    {
        var host = new FakeServerHost("linux-server", Mirror)
        {
            LinkError = "ln: failed to create hard link '/srv/runs/.vt-link-probe.Ab12/game' => '/srv/valheim/valheim_server.x86_64': Operation not permitted",
        };
        const string source = "/srv/valheim", runtime = "/srv/runs/protected/runtime", staging = "/srv/runs/protected/staging";
        LinuxServerInstall(host.Local(source));
        string chosen = Path.Combine(_root, "chosen-protected.dll");
        File.WriteAllText(chosen, "selected plugin");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging, [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll")],
            TimeSpan.FromSeconds(30)));

        Assert.StartsWith("this user cannot hard-link the install's game files from /srv/valheim into the default runtime folder " +
            "/srv/runs/protected/runtime: ln: failed to create hard link", error.Message);
        Assert.Contains("fs.protected_hardlinks", error.Message);
        Assert.Equal("1", Assert.Single(host.Runs, run => run.Script == "profile-storage").Variables["linktest"]);
        Assert.DoesNotContain(host.Scripts, script => script is "list" or "ship" or "profile-seed" or "apply-stage");
    }

    [Fact] public async Task OneHostedProfileStagesOnlySelectedFilesWithoutChangingTheSource()
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
        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll")], TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), prepared.Loader.Files["BepInEx/plugins/chosen.dll"]);
        Assert.Equal(File.ReadAllBytes(sourceConfig), File.ReadAllBytes(Path.Combine(host.Local(runtime), "BepInEx", "config", "BepInEx.cfg")));
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(Path.Combine(host.Local(runtime), "BepInEx", "plugins", "unrelated.dll")));
        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("apply-stage", host.Scripts);
        Assert.Contains("profile-seed", host.Scripts);
        Assert.DoesNotContain("copy", host.Scripts);
        // #257: Steam's own runtime output in the install (logs/) stays in the install and is not the server's runtime.
        Assert.True(File.Exists(Path.Combine(install, "logs", "connection_log_2456.txt")));
        Assert.False(Directory.Exists(Path.Combine(host.Local(runtime), "logs")));
        Assert.DoesNotContain(prepared.Loader.Files.Keys, file => file.StartsWith("logs/", StringComparison.OrdinalIgnoreCase));
        // The install's own BepInEx folder is never linked: only the selected files and the loader reach the profile.
        Assert.False(File.Exists(Path.Combine(host.Local(runtime), "BepInEx", "logs", "kept.txt")));
        Assert.True(File.Exists(Path.Combine(install, "BepInEx", "logs", "kept.txt")));

        string explicitConfig = Path.Combine(_root, "explicit-BepInEx.cfg");
        File.WriteAllText(explicitConfig, "explicit settings");
        const string explicitRuntime = @"C:\runs\explicit\runtime", explicitStaging = @"C:\runs\explicit\staging";
        var explicitProfile = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source,
            explicitRuntime, explicitStaging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll"),
                new HostedRuntimeFile(explicitConfig, BepInExSettings.RelativePath)], TimeSpan.FromSeconds(30));
        Assert.Equal("explicit settings", File.ReadAllText(Path.Combine(host.Local(explicitRuntime),
            "BepInEx", "config", "BepInEx.cfg")));
        Assert.Equal(FileHash.Sha256(explicitConfig), explicitProfile.Loader.Files[BepInExSettings.RelativePath]);
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

        var failure = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/config-pin.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("differs from its pinned source", failure.Message);
        Assert.Equal("pinned source settings", File.ReadAllText(config));
        Assert.False(Directory.Exists(host.Local(runtime)));
    }

    // #711: the profile's BepInEx.cfg is the plan's explicit one, else the package's default, else none (BepInEx writes its
    // defaults); the install's own is never carried over onto a package.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ReviewedLoaderReplacesAnIncoherentSourceOnlyInTheDisposableRuntime(bool packageHasSettings, bool explicitSettings)
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Client, source, runtime, staging, files, TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain("profile-seed", host.Scripts);
        string explicitConfig = Path.Combine(_root, "explicit-BepInEx.cfg");
        File.WriteAllText(explicitConfig, "explicit settings");
        if (explicitSettings) files = [.. files, new HostedRuntimeFile(explicitConfig, BepInExSettings.RelativePath)];
        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Client, source, runtime, staging,
            files, TimeSpan.FromSeconds(30), loaderPackage: package);
        var listing = prepared.Loader;
        foreach (var file in package.Files.Where(file => !(explicitSettings && file.Key == BepInExSettings.RelativePath)))
            Assert.Equal(file.Value, listing.Files[file.Key]);
        string profileConfig = Path.Combine(host.Local(runtime), "BepInEx", "config", "BepInEx.cfg");
        if (explicitSettings || packageHasSettings)
            Assert.Equal(File.ReadAllBytes(explicitSettings ? explicitConfig : packageConfig), File.ReadAllBytes(profileConfig));
        else Assert.False(File.Exists(profileConfig));
        Assert.False(listing.Files.ContainsKey("BepInEx/core/stale.dll"));
        Assert.Equal(package.Loader, prepared.Pins.Loader);
        // No source-loader file was copied in this case. An empty selected scope must skip its check, not list and hash the
        // entire launch folder (the final owner-selected profile listing is always narrow).
        Assert.DoesNotContain(host.Runs, run => run.Script == "list" && run.Variables["root"] == runtime &&
            run.Variables["dirs"].Length == 0);
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
        var error = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Client, source, runtime, staging,
            files, TimeSpan.FromSeconds(30), loaderPackage: package));
        Assert.Contains("is not the reviewed package's loader", error.Message);
        Assert.False(Directory.Exists(host.Local(runtime)));
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
        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Client, source, runtime, staging, files, TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), prepared.Loader.Files["BepInEx/plugins/cli.dll"]);
        Assert.False(prepared.Loader.Files.ContainsKey("BepInEx/plugins/unrelated.dll"));
        Assert.True(File.Exists(old));
        Assert.False(Directory.Exists(host.Local(staging)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source,
            @"C:\runs\server\runtime", @"C:\runs\server\staging", files, TimeSpan.FromSeconds(30)));
    }

    // The source inspection every campaign preparation starts with: an unrecognised Windows proxy and a Linux client install
    // (Linux hosts run the dedicated server only) are refused before anything is copied, each naming what it found.
    [Fact] public async Task SourceInspectionRefusesAnUnrecognisedProxyAndALinuxClient()
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
        FakeInstalls.LinuxLoader(linuxSource);
        var linuxClient = await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            HostedRuntimeStage.InspectSourceAsync(linux, HostedRuntimeKind.Client, "/game/source", null, TimeSpan.FromSeconds(30)));
        Assert.Equal("Linux hosts run the dedicated server only; start the client on Windows or macOS.", linuxClient.Message);
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
        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Client, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/mac-cli.dll")], TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), prepared.Loader.Files["BepInEx/plugins/mac-cli.dll"]);
        Assert.Equal(source, prepared.GameRoot);
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
        await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/chosen.dll")], TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<ArgumentException>(() => HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source,
            @"C:\runs\one\runtime", @"C:\runs\one\staging",
            [new HostedRuntimeFile(chosen, "winhttp.dll")], TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain(host.Scripts, script => script is "ship" or "profile-seed");
    }

    [Fact] public async Task FailedPreparationRetiresOnlyItsNewProfileAndStaging()
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
        await Assert.ThrowsAsync<HostOperationException>(() => HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(unrelated));
        Assert.False(Directory.Exists(host.Local(runtime)));
        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("cleanup-stage", host.Scripts);
    }

    [Theory]
    [InlineData("ship")]
    [InlineData("seed")]
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
        else host.AfterSeed = _ => throw new IOException("seed reply lost after the launch folder was made");

        var error = await Assert.ThrowsAsync<IOException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("reply lost", error.Message);
        Assert.False(Directory.Exists(host.Local(runtime)));
        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("cleanup-stage", host.Scripts);
        Assert.True(File.Exists(Path.Combine(install, GameLaunch.ServerWindowsExecutable)));
    }

    [Fact]
    public async Task TheRealProfileSeedKeepsItsOwnFileErrorInsteadOfBecomingAHardLinkDiagnosis()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "seed source install " + new string('s', 64))).FullName;
        string runtime = Path.Combine(_root, "seed runtime folder " + new string('r', 64));
        const string missing = "missing-game-file.dll";
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(missing));
        var variables = new Dictionary<string, string>
        {
            ["source"] = source,
            ["runtime"] = runtime,
            ["game"] = runtime,
            ["gameFiles"] = encoded,
            ["files"] = OperatingSystem.IsWindows() ? "" : encoded,
        };
        var host = new LocalGameHost("local-seed", HostShell.ThisMachine);
        string script = OperatingSystem.IsWindows() ? HostedRuntimeStage.WindowsProfileSeed : HostedRuntimeStage.BashProfileSeed;

        var result = await host.RunAsync(script, variables, TimeSpan.FromSeconds(30));

        Assert.True(result.Failed, result.Describe());
        Assert.Contains(missing, result.Stderr);
        Assert.DoesNotContain("must support hard links", result.Describe(), StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(runtime, result.Stderr);
            Assert.Contains(source, result.Stderr);
            Assert.Contains("install's volume", result.Stderr);
        }
    }

    // #612, with Linux's own tools: the seed hard-links a game file (the install's inode), copies the loader, and refuses a link
    // across filesystems in the Windows seed's words, never copying the game instead. /dev/shm is a tmpfs; the second half is
    // skipped where it shares the temporary folder's filesystem.
    [Fact]
    public async Task TheRealBashSeedLinksTheGameAndRefusesALinkAcrossFilesystems()
    {
        if (!OperatingSystem.IsLinux()) return;
        var host = new LocalGameHost("local-link", HostShell.Bash);
        string source = Directory.CreateDirectory(Path.Combine(_root, "link source")).FullName;
        const string loader = "doorstop_libs/libdoorstop_x64.so";
        File.WriteAllText(Path.Combine(source, GameLaunch.ServerLinuxExecutable), "server");
        Directory.CreateDirectory(Path.Combine(source, "doorstop_libs"));
        File.WriteAllText(Path.Combine(source, loader), "loader");
        static string Encode(string relative) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(relative));
        Task<HostResult> Seed(string runtime) => host.RunAsync(HostedRuntimeStage.BashProfileSeed, new Dictionary<string, string>
        {
            ["source"] = source, ["runtime"] = runtime, ["game"] = runtime,
            ["gameFiles"] = Encode(GameLaunch.ServerLinuxExecutable), ["files"] = Encode(loader),
        }, TimeSpan.FromSeconds(30));
        async Task<string> Stat(string format, string path) => (await host.RunAsync("stat -c \"$format\" -- \"$path\"",
            new Dictionary<string, string> { ["format"] = format, ["path"] = path }, TimeSpan.FromSeconds(30))).EnsureSuccess("stat").Stdout.Trim();

        string runtime = Path.Combine(_root, "link runtime");
        Assert.Contains("VT-PROFILE-SEEDED", (await Seed(runtime)).EnsureSuccess("Seeding a linked profile").Stdout);
        Assert.Equal(await Stat("%i", Path.Combine(source, GameLaunch.ServerLinuxExecutable)), await Stat("%i", Path.Combine(runtime, GameLaunch.ServerLinuxExecutable)));
        Assert.NotEqual(await Stat("%i", Path.Combine(source, loader)), await Stat("%i", Path.Combine(runtime, loader)));

        const string shm = "/dev/shm";
        if (!Directory.Exists(shm) || await Stat("%d", shm) == await Stat("%d", source)) return;
        string other = Path.Combine(shm, "vt-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            var refused = await Seed(other);
            Assert.True(refused.Failed, refused.Describe());
            Assert.Contains($"Could not create a hard link in runtime folder {other} from the install's mount containing {source}: ", refused.Stderr);
            Assert.Contains("Invalid cross-device link", refused.Stderr);
            Assert.False(File.Exists(Path.Combine(other, GameLaunch.ServerLinuxExecutable)));
        }
        finally { if (Directory.Exists(other)) Directory.Delete(other, recursive: true); }
    }

    // #612: the real storage probe through this machine's bash, on Linux (GNU stat) and macOS (BSD stat) alike. A server's
    // profile is linked: a runtime beside the install is on its mount or volume, the host probe agrees with the inventory's own
    // reading of this machine (one probe for both paths), and its trial link of the install's executable succeeds and leaves
    // nothing behind. A bash client's profile is a Mac's, launched from its source: the host is not asked. On Linux, /dev/shm's
    // tmpfs is another mount, refused before anything is made.
    [Fact]
    public async Task TheRealStorageProbeDecidesTheLayoutAndReadsTheMount()
    {
        if (OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("local-probe", HostShell.Bash);
        string source = Directory.CreateDirectory(Path.Combine(_root, "probe source")).FullName;
        string executable = Path.Combine(source, OperatingSystem.IsMacOS() ? GameLaunch.ServerMacExecutable : GameLaunch.ServerLinuxExecutable);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "server");
        string runtime = Path.Combine(_root, "probe runs", "runtime"); // not made yet: its nearest existing parent is read
        var server = await HostedRuntimeStage.RequireProfileStorageAsync(host, HostedRuntimeKind.Server, source, runtime, TimeSpan.FromSeconds(30));
        Assert.Equal((ProfileLayout.SourceExecutable, "macos", (string?)null), await Client());
        async Task<(ProfileLayout, string, string?)> Client()
        {
            var check = await HostedRuntimeStage.RequireProfileStorageAsync(host, HostedRuntimeKind.Client, "/no/such/install", "/no/such/runtime", TimeSpan.FromSeconds(30));
            return (check.Layout, check.Platform, check.Description);
        }

        Assert.Equal((ProfileLayout.Linked, OperatingSystem.IsMacOS() ? "macos" : "linux"), (server.Layout, server.Platform));
        var volumes = UnixStorageProbe.Inspect([source, runtime]);
        Assert.Equal($"source {source} and runtime {runtime} are on {volumes[0].Description}", server.Description);
        Assert.True(volumes[0].SameVolume(volumes[1]));
        Assert.True(volumes[1].Writable);
        Assert.False(volumes[0].AtMountPoint);
        Assert.Equal(OperatingSystem.IsMacOS() ? ProfileLinkRules.MacOS : ProfileLinkRules.Linux, volumes[0].Rules);
        Assert.Equal(["probe source"], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));

        const string shm = "/dev/shm";
        if (!OperatingSystem.IsLinux() || !Directory.Exists(shm) || UnixStorageProbe.Inspect([shm])[0].SameVolume(volumes[0])) return;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => HostedRuntimeStage.RequireProfileStorageAsync(host,
            HostedRuntimeKind.Server, source, shm + "/vt-runs/runtime", TimeSpan.FromSeconds(30), explicitRuntime: true));
        Assert.Contains("A Linux profile hard-links the install's game files, and a hard link cannot cross mounts. " +
            $"Put the inventory runtime on the install's mount {volumes[0].MountPoint}.", refused.Message);
    }

    // #612 review 5: a preflight's trial link never writes inside the install. A runtime inside it is refused by the apart check
    // before the probe runs, on a fake host and with this machine's own tools, and nothing is left in the install.
    [Fact]
    public async Task ARuntimeInsideTheInstallIsRefusedBeforeTheTrialLinkWrites()
    {
        var fake = new FakeServerHost("linux-server", Mirror);
        LinuxServerInstall(fake.Local("/srv/valheim"));
        await Assert.ThrowsAnyAsync<Exception>(() => HostedRuntimeStage.RequireProfileStorageAsync(fake, HostedRuntimeKind.Server,
            "/srv/valheim", "/srv/valheim/runs/runtime", TimeSpan.FromSeconds(30), explicitRuntime: true));
        Assert.Equal(["apart"], fake.Scripts);

        if (OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("local-inside", HostShell.Bash);
        string source = Directory.CreateDirectory(Path.Combine(_root, "inside source")).FullName;
        string executable = Path.Combine(source, OperatingSystem.IsMacOS() ? GameLaunch.ServerMacExecutable : GameLaunch.ServerLinuxExecutable);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "server");
        var before = WorldFixture.Manifest(source);
        await Assert.ThrowsAnyAsync<Exception>(() => HostedRuntimeStage.RequireProfileStorageAsync(host, HostedRuntimeKind.Server,
            source, Path.Combine(source, "runs", "runtime"), TimeSpan.FromSeconds(30), explicitRuntime: true));
        Assert.Equal(before, WorldFixture.Manifest(source));
        Assert.Empty(Directory.GetDirectories(source, ".vt-link-probe.*", SearchOption.AllDirectories));
        // The probe itself never puts its trial folder inside the install either, even when asked with no apart check.
        var probed = await host.RunAsync(UnixStorageProbe.Script, new Dictionary<string, string>
        {
            ["paths"] = source + "\n" + Path.Combine(source, "runs"), ["linktest"] = "1",
        }, TimeSpan.FromSeconds(30));
        Assert.DoesNotContain("VT-PROFILE-LINK", probed.EnsureSuccess("probing").Stdout);
        Assert.Equal(before, WorldFixture.Manifest(source));
    }

    // #612 review 11: this machine's probe is bounded: one still running at the deadline is killed and refused as an IOException,
    // which the default root reads as a probe that could not answer; a runner's own linktest never reaches it.
    [Fact]
    public void ThisMachinesProbeIsKilledAtItsDeadlineAndIgnoresTheRunnersLinktest()
    {
        if (OperatingSystem.IsWindows()) return;
        string marker = Path.Combine(_root, "probe-still-ran");
        var clock = Stopwatch.StartNew();
        var error = Assert.Throws<IOException>(() => UnixStorageProbe.Run($"sleep 20; touch '{marker}'", [_root], TimeSpan.FromSeconds(1)));
        Assert.Contains("did not finish within 1 s", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), clock.Elapsed.ToString());
        Environment.SetEnvironmentVariable("linktest", "1");
        try { Assert.Equal("", UnixStorageProbe.Run("printf '%s' \"${linktest:-}\"", [_root], TimeSpan.FromSeconds(30))); }
        finally { Environment.SetEnvironmentVariable("linktest", null); }
        Assert.False(File.Exists(marker));
    }

    // #612 review 12: the seed keeps a folder name's trailing line break, which $( ) would strip, for the links and the loader alike.
    [Fact]
    public async Task TheRealBashSeedKeepsAFolderNamesTrailingLineBreak()
    {
        if (OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("local-newline", HostShell.Bash);
        string source = Directory.CreateDirectory(Path.Combine(_root, "newline source")).FullName;
        const string game = "Data\n/game.bin", loader = "BepInEx\n/core.dll";
        foreach (string relative in new[] { game, loader })
        {
            Directory.CreateDirectory(Path.Combine(source, relative.Split('/')[0]));
            File.WriteAllText(Path.Combine(source, relative), relative);
        }
        static string Encode(string relative) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(relative));
        string runtime = Path.Combine(_root, "newline runtime");
        (await host.RunAsync(HostedRuntimeStage.BashProfileSeed, new Dictionary<string, string>
        {
            ["source"] = source, ["runtime"] = runtime, ["game"] = runtime, ["gameFiles"] = Encode(game), ["files"] = Encode(loader),
        }, TimeSpan.FromSeconds(30))).EnsureSuccess("Seeding names with line breaks");
        Assert.Equal(game, File.ReadAllText(Path.Combine(runtime, game)));
        Assert.Equal(loader, File.ReadAllText(Path.Combine(runtime, loader)));
        Assert.False(Directory.Exists(Path.Combine(runtime, "Data")));
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

        var error = await Assert.ThrowsAsync<AggregateException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30)));

        Assert.Contains("reply lost", error.InnerExceptions[0].Message);
        Assert.Contains("Cleaning failed profile preparation", error.InnerExceptions[1].Message);
        Assert.True(Directory.Exists(host.Local(staging)));
    }

    [Fact] public async Task CancelledPreparationStillCleansAStagedRemoteProfile()
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Server, source, runtime, staging,
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30), cancellation.Token));

        Assert.False(Directory.Exists(host.Local(staging)));
        Assert.Contains("cleanup-stage", host.Scripts);
    }
}
