using Valheim.Testing.Game;
using System.Diagnostics;
using Xunit;

public sealed class HostedRuntimeStageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("host-stage-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("valheim_server", "", true, "VT-GAME idle")]
    [InlineData("valheim_server", "/owned", false, "VT-GAME unknown")]
    [InlineData("/other/valheim_server.x86_64", "/owned", true, "VT-GAME idle")]
    [InlineData("/owned/valheim_server.x86_64", "/owned", false, "VT-GAME busy")]
    [InlineData("/tmp/Valheim.app/Contents/MacOS/Valheim", "", true, "VT-GAME busy")]
    [InlineData("ordinary-helper", "/owned", true, "VT-GAME idle")]
    // One check for every copy on a host (#257): a game running from any of them is busy, from none of them idle.
    [InlineData("/second/valheim_server.x86_64", "/first\n/second", false, "VT-GAME busy")]
    [InlineData("/first/valheim.x86_64", "/first\n/second", false, "VT-GAME busy")]
    [InlineData("/second2/valheim_server.x86_64", "/first\n/second", false, "VT-GAME idle")]
    [InlineData("valheim_server", "/first\n/second", false, "VT-GAME unknown")]
    public async Task BashProcessCheckRefusesConflictingUse(string process, string runtime, bool clientSession, string expected)
    {
        if (OperatingSystem.IsWindows()) return;
        string bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        string ps = Path.Combine(bin, "ps");
        File.WriteAllText(ps, "#!/bin/sh\nprintf '%s\\n' '" + process + "'\n");
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
        var host = new LocalGameHost("local-mac", HostShell.Bash);
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
            Assert.Contains("owned-runtime process", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
        var host = new LocalGameHost("local-mac", HostShell.Bash);
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
        string chosen = Path.Combine(_root, "selected.dll");
        File.WriteAllText(chosen, "selected plugin");
        string run = Path.Combine(_root, "vt-one");
        var listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source,
            Path.Combine(run, "runtime"), Path.Combine(run, "staging"),
            [new HostedRuntimeFile(chosen, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30));
        Assert.Equal(FileHash.Sha256(chosen), listing.Files["BepInEx/plugins/selected.dll"]);
        Assert.False(listing.Files.ContainsKey("BepInEx/plugins/old.dll"));
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
}
