using System.Diagnostics;
using Valheim.Testing.Game;
using Xunit;

// The real macOS check behind a client copy (#258 run A): a bundle with a file added inside it is fixable, the repair removes
// that file from the copy only and drops quarantine, and a changed sealed file refuses. macOS only (CI's macOS leg).
public sealed class MacAppBundleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mac-bundle-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact] public async Task AnAddedFileIsRemovedFromTheCopyAndAChangedFileRefuses()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string app = Path.Combine(_root, GameLaunch.ClientMacBundle);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources"));
        File.Copy("/usr/bin/true", Path.Combine(app, "Contents", "MacOS", "Valheim"));
        File.WriteAllText(Path.Combine(app, "Contents", "Resources", "data.txt"), "sealed");
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>CFBundleExecutable</key><string>Valheim</string><key>CFBundleIdentifier</key><string>test.valheim.bundle</string></dict></plist>
            """);
        Assert.Equal(0, await Run("codesign", "--sign", "-", "--force", app));
        var host = new LocalGameHost("local-mac", HostShell.Bash);
        var timeout = TimeSpan.FromSeconds(60);
        Assert.NotEqual(MacBundleState.Fixable, (await MacAppBundle.InspectAsync(host, _root, timeout)).State);

        string added = Path.Combine(app, "Contents", "MacOS", "preloader_1.log");
        File.WriteAllText(added, "left by a launch");
        Assert.Equal(0, await Run("xattr", "-w", "com.apple.quarantine", "0081;00000000;test;", app));
        var inspected = await MacAppBundle.InspectAsync(host, _root, timeout);
        Assert.Equal(MacBundleState.Fixable, inspected.State);
        Assert.Equal(1, inspected.Count);
        Assert.Contains("Contents/MacOS/preloader_1.log", inspected.Detail);
        Assert.Null(MacAppBundle.SourceRefusal(inspected));
        Assert.True(File.Exists(added)); // inspecting never changes the bundle

        // An ad-hoc signature satisfies codesign; Gatekeeper rejects it unless assessments are disabled (CI runners may be).
        var repaired = await MacAppBundle.RepairAsync(host, _root, timeout);
        Assert.Contains(repaired.State, new[] { MacBundleState.Accepted, MacBundleState.Rejected });
        Assert.Equal(1, repaired.Count);
        Assert.False(File.Exists(added));
        Assert.Equal(0, await Run("codesign", "--verify", "--deep", "--strict", app));
        Assert.NotEqual(0, await Run("xattr", "-p", "com.apple.quarantine", app));

        File.WriteAllText(Path.Combine(app, "Contents", "Resources", "data.txt"), "changed");
        var broken = await MacAppBundle.InspectAsync(host, _root, timeout);
        Assert.Equal(MacBundleState.Broken, broken.State);
        Assert.Contains("data.txt", broken.Detail);
        Assert.Contains("Verify the game's files in Steam", MacAppBundle.SourceRefusal(broken));
        Assert.Equal(MacBundleState.Broken, (await MacAppBundle.RepairAsync(host, _root, timeout)).State);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(app, "Contents", "Resources", "data.txt")));
    }

    [Fact] public async Task AFolderWithoutABundleIsNotAMacClient()
    {
        if (OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("local", HostShell.Bash);
        Assert.Equal(MacBundleState.None, (await MacAppBundle.InspectAsync(host, _root, TimeSpan.FromSeconds(30))).State);
    }

    // Each refusal names the fix that works: Steam's verify restores changed or missing files, but cannot help a bundle macOS
    // rejects as signed, or a host whose verdict cannot be read.
    [Fact] public void EachSourceRefusalNamesItsOwnFix()
    {
        Assert.Null(MacAppBundle.SourceRefusal(new(MacBundleState.Fixable, 2, "Contents/MacOS/preloader_1.log")));
        Assert.Null(MacAppBundle.SourceRefusal(new(MacBundleState.Accepted, 0, "")));
        Assert.Contains("Verify the game's files in Steam", MacAppBundle.SourceRefusal(new(MacBundleState.Broken, 1, "file missing: x")));
        string rejected = MacAppBundle.SourceRefusal(new(MacBundleState.Rejected, 0, "Valheim.app: rejected"))!;
        Assert.DoesNotContain("Verify the game's files", rejected);
        Assert.Contains("notarized", rejected);
        Assert.Contains("/usr/bin", MacAppBundle.SourceRefusal(new(MacBundleState.Unknown, 0, "codesign or spctl is not available")));
    }

    private static async Task<int> Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
