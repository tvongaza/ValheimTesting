using System.Diagnostics;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// The real macOS check behind a client copy (#258 run A): a bundle with a file added inside it is fixable, the repair removes
// that file from the copy only and drops quarantine, and a changed sealed file refuses. macOS only (CI's macOS leg).
public sealed class MacAppBundleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mac-bundle-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact] public void HostedBundleAssessmentGetsTheDefaultClientStartBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(ClientTimeouts.DefaultStartSeconds),
            HostedTimeouts.MacBundleAssessment(HostedTimeouts.Quick));
        Assert.Equal(TimeSpan.FromSeconds(600), HostedTimeouts.MacBundleAssessment(TimeSpan.FromSeconds(600)));
    }

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
        Assert.NotEqual(MacBundleInspection.State.Fixable, (await MacAppBundle.InspectAsync(host, _root, timeout)).State);

        string added = Path.Combine(app, "Contents", "MacOS", "preloader_1.log");
        File.WriteAllText(added, "left by a launch");
        Assert.Equal(0, await Run("xattr", "-w", "com.apple.quarantine", "0081;00000000;test;", app));
        var inspected = await MacAppBundle.InspectAsync(host, _root, timeout);
        Assert.Equal(MacBundleInspection.State.Fixable, inspected.State);
        Assert.Equal(1, inspected.Count);
        Assert.Contains("Contents/MacOS/preloader_1.log", inspected.Detail);
        Assert.Null(MacBundleInspection.SourceRefusal(inspected));
        Assert.True(File.Exists(added)); // inspecting never changes the bundle
        var localInspection = MacBundleInspection.Inspect(_root, timeout); // the one-shot runner uses the same script
        Assert.Equal(MacBundleInspection.State.Fixable, localInspection.State);
        Assert.Equal(1, localInspection.Count);
        Assert.True(File.Exists(added));

        // An ad-hoc signature satisfies codesign; Gatekeeper rejects it unless assessments are disabled (CI runners may be).
        var repaired = await MacAppBundle.RepairAsync(host, _root, timeout);
        Assert.Contains(repaired.State, new[] { MacBundleInspection.State.Accepted, MacBundleInspection.State.Rejected });
        Assert.Equal(1, repaired.Count);
        Assert.False(File.Exists(added));
        Assert.Equal(0, await Run("codesign", "--verify", "--deep", "--strict", app));
        Assert.NotEqual(0, await Run("xattr", "-p", "com.apple.quarantine", app));

        File.WriteAllText(added, "left by a second launch");
        var localRepair = MacBundleInspection.Repair(_root, timeout);
        Assert.Contains(localRepair.State, new[] { MacBundleInspection.State.Accepted, MacBundleInspection.State.Rejected });
        Assert.Equal(1, localRepair.Count);
        Assert.False(File.Exists(added));

        File.WriteAllText(Path.Combine(app, "Contents", "Resources", "data.txt"), "changed");
        var broken = await MacAppBundle.InspectAsync(host, _root, timeout);
        Assert.Equal(MacBundleInspection.State.Broken, broken.State);
        Assert.Contains("data.txt", broken.Detail);
        Assert.Contains("Verify the game's files in Steam", MacBundleInspection.SourceRefusal(broken));
        Assert.Equal(MacBundleInspection.State.Broken, (await MacAppBundle.RepairAsync(host, _root, timeout)).State);
        Assert.Equal(MacBundleInspection.State.Broken, MacBundleInspection.Inspect(_root, timeout).State);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(app, "Contents", "Resources", "data.txt")));
    }

    [Fact] public async Task AFolderWithoutABundleIsNotAMacClient()
    {
        if (OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("local", HostShell.Bash);
        var verdict = await MacAppBundle.InspectAsync(host, _root, TimeSpan.FromSeconds(30));
        Assert.Equal(MacBundleInspection.State.Unknown, verdict.State);
        Assert.NotNull(MacBundleInspection.SourceRefusal(verdict));
    }

    [Theory]
    [InlineData("VT-BUNDLE none", "did not find")]
    [InlineData("VT-BUNDLE unexpected", "unknown bundle verdict")]
    [InlineData("VT-BUNDLE accepted", "malformed bundle verdict")]
    [InlineData("VT-BUNDLE accepted -1", "malformed bundle verdict")]
    [InlineData(null, "no verdict")]
    public void MissingAndUnknownBundleVerdictsRefuse(string? reply, string reason)
    {
        var verdict = MacBundleInspection.Parse(reply);
        Assert.Equal(MacBundleInspection.State.Unknown, verdict.State);
        Assert.Contains(reason, MacBundleInspection.SourceRefusal(verdict), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("VT-BUNDLE none", "did not find")]
    [InlineData("VT-BUNDLE unexpected", "unknown bundle verdict")]
    public async Task HostedBundleCheckUsesTheSameRefusalVerdict(string reply, string reason)
    {
        var host = new FakeServerHost("mac", _root) { MacBundleInspect = reply };
        var verdict = await MacAppBundle.InspectAsync(host, _root, TimeSpan.FromSeconds(3));
        Assert.Equal(MacBundleInspection.State.Unknown, verdict.State);
        Assert.Contains(reason, MacBundleInspection.SourceRefusal(verdict), StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public async Task HostedBundleCheckKeepsHostOutputWhenVerdictIsMissing()
    {
        var host = new FakeServerHost("mac", _root);
        host.Failures["mac-bundle"] = new HostResult(HostOutcome.Exited, 0,
            "codesign produced no verdict\n", "spctl could not assess this app\n", TimeSpan.Zero, false);
        var verdict = await MacAppBundle.InspectAsync(host, _root, TimeSpan.FromSeconds(3));
        string refusal = MacBundleInspection.SourceRefusal(verdict)!;
        Assert.Contains("codesign produced no verdict", refusal);
        Assert.Contains("spctl could not assess this app", refusal);
    }

    // Each refusal names the fix that works: Steam's verify restores changed or missing files, but cannot help a bundle macOS
    // rejects as signed, or a host whose verdict cannot be read.
    [Fact] public void EachSourceRefusalNamesItsOwnFix()
    {
        Assert.Null(MacBundleInspection.SourceRefusal(new(MacBundleInspection.State.Fixable, 2, "Contents/MacOS/preloader_1.log")));
        Assert.Null(MacBundleInspection.SourceRefusal(new(MacBundleInspection.State.Accepted, 0, "")));
        Assert.Contains("Verify the game's files in Steam", MacBundleInspection.SourceRefusal(new(MacBundleInspection.State.Broken, 1, "file missing: x")));
        string rejected = MacBundleInspection.SourceRefusal(new(MacBundleInspection.State.Rejected, 0, "Valheim.app: rejected"))!;
        Assert.DoesNotContain("Verify the game's files", rejected);
        Assert.Contains("notarized", rejected);
        Assert.Contains("/usr/bin", MacBundleInspection.SourceRefusal(new(MacBundleInspection.State.Unknown, 0, "codesign or spctl is not available")));
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
