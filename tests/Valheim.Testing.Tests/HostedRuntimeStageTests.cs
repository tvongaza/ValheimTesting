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
