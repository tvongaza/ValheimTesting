using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// #254 through the host's real shell (bash on macOS and Linux, Windows PowerShell on Windows): a preloader crash log the
// launch wrote is read and kept; one from before the launch is only named.
public sealed class HostedClientEvidenceShellTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("client-evidence-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact] public async Task AFreshPreloaderLogIsReadAndKeptAndAnOlderOneIsOnlyNamed()
    {
        var host = OperatingSystem.IsWindows() ? new LocalGameHost("client", HostShell.WindowsPowerShell) : new LocalGameHost("client", HostShell.Bash);
        string install = Path.Combine(_root, "game dir"), launch = Path.Combine(_root, "run", "client-1");
        Directory.CreateDirectory(install); Directory.CreateDirectory(launch);
        string older = Path.Combine(install, "preloader_20260101_000000.log");
        File.WriteAllText(older, "[Error  :   BepInEx] an old failure\n");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(launch, "pid"), "4242\n");
        File.SetLastWriteTimeUtc(Path.Combine(launch, "pid"), DateTime.UtcNow.AddMinutes(-1));
        string fresh = Path.Combine(install, "preloader_20261005_190000.log");
        File.WriteAllText(fresh, "[Info   :   BepInEx] starting\n[Fatal  :   BepInEx] Could not find BepInEx.Preloader.Core\n[Error  :   BepInEx] later\n");

        var read = await HostedClientScripts.ReadPreloaderAsync(host, install, launch);
        Assert.NotNull(read);
        var (name, first) = Assert.Single(read!.Fresh);
        Assert.Equal("preloader_20261005_190000.log", name);
        Assert.Equal("[Fatal  :   BepInEx] Could not find BepInEx.Preloader.Core", first);
        Assert.Equal(["preloader_20260101_000000.log"], read!.Stale);

        var kept = await host.RunAsync(HostedClientScripts.Keep(host.Shell.Kind), new Dictionary<string, string> { ["install"] = install, ["dir"] = launch }, TimeSpan.FromSeconds(60));
        Assert.True(kept.Succeeded, kept.Describe());
        Assert.Contains("VT-KEPT", kept.Stdout);
        Assert.Equal(File.ReadAllText(fresh), File.ReadAllText(Path.Combine(launch, "game-2.preloader-1.log")));
        Assert.False(File.Exists(Path.Combine(launch, "game-2.preloader-2.log")));
        Assert.Contains("preloader_20260101_000000.log", File.ReadAllText(Path.Combine(launch, "preloader.stale")));
        Assert.True(File.Exists(Path.Combine(launch, "game-0.log.absent")));
    }
}
