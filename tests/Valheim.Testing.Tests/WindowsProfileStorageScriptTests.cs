using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

[Trait("Os", "windows")]
public sealed class WindowsProfileStorageScriptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("profile-storage-script-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task TheRealProbeAcceptsAnExtendedLocalRootBeforeDriveInfo()
    {
        if (!OperatingSystem.IsWindows()) return;
        string extended = @"\\?\" + _root;
        var host = new LocalGameHost("windows-storage", HostShell.Pwsh);

        var result = await host.RunAsync(HostedRuntimeStage.WindowsProfileStorage,
            new Dictionary<string, string> { ["source"] = extended, ["runtime"] = extended }, TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded, result.Describe());
        string encoded = Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("VT-PROFILE-STORAGE ", StringComparison.Ordinal))["VT-PROFILE-STORAGE ".Length..];
        using var reply = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.StartsWith(@"\\?\", reply.RootElement.GetProperty("Source").GetProperty("Root").GetString());
    }

    [Fact]
    public async Task TheRealProbeClassifiesAPlainUncPathBeforeDriveInfo()
    {
        if (!OperatingSystem.IsWindows()) return;
        string unc = $@"\\localhost\{char.ToUpperInvariant(_root[0])}$\{_root[3..]}";
        var host = new LocalGameHost("windows-storage", HostShell.Pwsh);

        var result = await host.RunAsync(HostedRuntimeStage.WindowsProfileStorage,
            new Dictionary<string, string> { ["source"] = unc, ["runtime"] = unc }, TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded, result.Describe());
        string encoded = Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("VT-PROFILE-STORAGE ", StringComparison.Ordinal))["VT-PROFILE-STORAGE ".Length..];
        using var reply = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.StartsWith(@"\\localhost\", reply.RootElement.GetProperty("Source").GetProperty("Root").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal((uint)4, reply.RootElement.GetProperty("Source").GetProperty("DriveType").GetUInt32());
    }

    [Fact]
    public void FinalPathOfAFolderWithNoLinksIsUnchangedAndStopsAtTheVolumeRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Windows refuses to resolve a volume root (C:\ throws), so walking up from a plain folder must stop before it.
        Assert.Equal(Path.GetFullPath(_root), new LocalSteamLocator().FinalPath(_root));
    }
}
