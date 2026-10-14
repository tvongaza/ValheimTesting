using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// #821: start's host gate is env preflight's check. A Windows client whose inventory runtime sits on another volume than its
// install is refused before the build, the lock or any record, with the words env preflight prints, which name the hard-link
// limit. The inventory's paths must be Windows paths that exist, so this runs on Windows only; LocalHostPreflightTests holds
// the same check, and env preflight's side of it, on every OS.
[Trait("Os", "windows")]
public sealed class StartStorageGateWindowsTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task StartRefusesACrossVolumeClientRuntimeBeforeAnyBuild()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = new FakeServerHost("pc", Path.Combine(_rig.Root, "storage-mirror"), windows: true, kind: GameHostKind.Local)
        {
            ProfileSourceVolume = new WindowsStorageVolume(@"C:\", "volume-c", "NTFS", false),
            ProfileRuntimeVolume = new WindowsStorageVolume(@"V:\", "volume-v", "NTFS", false),
        };
        using var probes = LocalHostPreflight.ReplaceDefaultProbesForTest(new(
            Processes: (_, _, _, _) => Task.CompletedTask, Port: (_, _, _, _) => Task.CompletedTask,
            Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
            Desktop: _ => Task.CompletedTask, SteamRunning: () => true,
            Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null,
            HostFactory: _ => host)); // no ProfileStorage stand-in: the production check runs on this host
        string inventory = Path.Combine(_rig.Root, "windows-inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object> { ["pc"] = new { kind = "local", platform = "windows", shell = "pwsh", @lock = Path.Combine(_rig.Root, "host.lock") } },
            environments = new[] { new { name = "client", host = "pc", roles = new[] { "client" }, install = _rig.Game,
                runtime = @"V:\vtrun\local-client", architecture = "x64", cliPort = 5551 } },
            leaseHost = "pc", leaseDirectory = Path.Combine(_rig.Root, "leases"),
        }));
        string output = Path.Combine(_rig.Root, "start-refused");
        var options = new Dictionary<string, string>
        {
            ["--inventory"] = inventory, ["--client-env"] = "client", ["--output"] = output,
            ["--project"] = _rig.Write("mod/Unbuildable.csproj", Encoding.UTF8.GetBytes("<Project>never built</Project>")),
            ["--cli-manifest"] = _rig.CliManifest(save: true, full: true), ["--cli-files"] = Path.Combine(_rig.Root, "cli"),
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ModInputStage.RunAsync(
            new ModInputStage.Request(ModInputStage.Route.Hosted, output, options, new HashSet<string>(), [], [], [], [])
            { ClientArchitecture = (_, _, _, _) => { } }, CancellationToken.None));

        Assert.Contains(@"client profile storage: the explicit inventory runtime folder V:\vtrun\local-client is on V:\ (NTFS), not the source install's volume C:\ (NTFS)", failure.Message);
        Assert.Contains("a hard link cannot cross volumes", failure.Message);
        Assert.False(Path.Exists(output)); // refused before the build, the lock or any record
        Assert.DoesNotContain(host.Scripts, script => script is "profile-seed" or "apply-stage");
    }
}
