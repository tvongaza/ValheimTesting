using Valheim.Testing.Game;
using Xunit;

// The hosted world's scripts in a real shell (bash, Windows PowerShell, pwsh), on a folder standing for a user's worlds_local: a
// fake host cannot show what the scripts themselves select and refuse.
public sealed class HostedWorldOnHostShellTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-world-shell-").FullName;
    private string Worlds => Path.Combine(_root, "worlds_local");
    private string To => Path.Combine(_root, "runs", "run-1", "host-world");
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void User()
    {
        Directory.CreateDirectory(Path.Combine(Worlds, "Campaign"));
        File.WriteAllText(Path.Combine(Worlds, "Campaign", "_main.0.fwl2"), "world");
        File.WriteAllText(Path.Combine(Worlds, "Campaign_backup_auto-20261006.db"), "backup");
        foreach (string other in new[] { "Campaign2.fwl", "MyWorld.fwl", "Campaign_mine.fwl", "campaign.fwl" })
            File.WriteAllText(Path.Combine(Worlds, other), "the user's");
    }
    private static IGameHost Host(string shell) => new LocalGameHost("local-" + shell, HostShell.Parse(shell));
    private static bool Windows(IGameHost host) => host.Shell.Kind == HostShellKind.PowerShell;

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task TheRefusalSeesEveryEntryNamedForTheWorldInAnyCase(string shell)
    {
        User();
        var host = Host(shell);
        var result = (await host.RunAsync(Windows(host) ? HostedWorldOnHost.WindowsEntries : HostedWorldOnHost.BashEntries,
            new Dictionary<string, string> { ["worlds"] = Worlds, ["name"] = "Campaign" }, TimeSpan.FromSeconds(60))).EnsureSuccess("entries");
        var listed = result.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("VT-WORLD-ENTRY ", StringComparison.Ordinal))
            .Select(line => line["VT-WORLD-ENTRY ".Length..]).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "Campaign", "Campaign_backup_auto-20261006.db", "Campaign_mine.fwl", "campaign.fwl" }.Order(StringComparer.Ordinal), listed);
    }

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task AMoveTakesOnlyTheWorldAndItsBackupsAndNeverOverwrites(string shell)
    {
        User();
        var host = Host(shell);
        // A destination that exists already: refused before anything moves.
        Directory.CreateDirectory(Path.Combine(To, "Campaign"));
        await Assert.ThrowsAnyAsync<Exception>(() => HostedWorldOnHost.MoveOut(host, Worlds, "Campaign", To, TimeSpan.FromSeconds(60), CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(Worlds, "Campaign", "_main.0.fwl2")));
        Assert.True(File.Exists(Path.Combine(Worlds, "Campaign_backup_auto-20261006.db")));
        Directory.Delete(Path.Combine(To, "Campaign"));

        var moved = await HostedWorldOnHost.MoveOut(host, Worlds, "Campaign", To, TimeSpan.FromSeconds(60), CancellationToken.None);
        // The world's own case on a case-sensitive folder; Windows folders ignore case, so campaign.fwl is the world's there.
        var expected = new List<string> { "Campaign", "Campaign_backup_auto-20261006.db" };
        if (Windows(host) || OperatingSystem.IsWindows()) expected.Add("campaign.fwl");
        Assert.Equal(expected.Order(StringComparer.Ordinal), moved.Order(StringComparer.Ordinal));
        Assert.Equal("world", File.ReadAllText(Path.Combine(To, "Campaign", "_main.0.fwl2")));
        foreach (string kept in new[] { "Campaign2.fwl", "MyWorld.fwl", "Campaign_mine.fwl" }) Assert.True(File.Exists(Path.Combine(Worlds, kept)), kept);
    }
}
