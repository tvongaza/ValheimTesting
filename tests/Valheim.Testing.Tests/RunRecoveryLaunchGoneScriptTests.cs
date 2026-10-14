using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// The launch-abandonment checks themselves, through the real host shell. Controlled snapshots cover hazards; the real-listing
// tests make each CI OS prove that its production process-table command runs and reports this game-free runner idle.
[Trait("Os", "unix")]
[Collection(nameof(WindowsGameProcessInventory))]
public sealed class BashLaunchGoneScriptTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static IGameHost Host() => new LocalGameHost("launch-gone-bash", HostShell.Bash);
    private static string Hash(string command) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command)));

    [Fact]
    public async Task AnotherUsersValheimProcessRefusesAbandonment()
    {
        if (OperatingSystem.IsWindows()) return;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunRecovery.RequireNoLaunchProcessAsync(
            Host(), [], Timeout, processSnapshot: "31001 987654 ignored /srv/Application Support/Valheim/valheim_server.x86_64 -nographics"));

        Assert.Contains("process 31001 still runs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherUsersJournalledLaunchCommandLineRefusesAbandonment()
    {
        if (OperatingSystem.IsWindows()) return;
        string hash = Hash("a launch owned by another user");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunRecovery.RequireNoLaunchProcessAsync(
            Host(), [hash], Timeout, processSnapshot: $"31002 987654 {hash} /usr/bin/steam -silent"));

        Assert.Contains("process 31002 still runs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableProcessListingRefusesAndNamesTheMissingObservation()
    {
        if (OperatingSystem.IsWindows()) return;
        var error = await Assert.ThrowsAsync<HostOperationException>(() => RunRecovery.RequireNoLaunchProcessAsync(
            Host(), [new string('a', 64)], Timeout, processSnapshot: "__unreadable__"));

        Assert.Contains("inconclusive (unreadable process-list)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheToolkitsOwnProcessesAreNotTheGame()
    {
        if (OperatingSystem.IsWindows()) return;
        // valheim-test (the global tool's shim) and its apphost run the check itself; only the game's exact images count.
        await RunRecovery.RequireNoLaunchProcessAsync(Host(), [], Timeout, processSnapshot:
            "31003 501 ignored /Users/someone/.dotnet/tools/valheim-test env recover --launch-gone\n" +
            "31004 501 ignored /Users/someone/.dotnet/tools/.store/valheim-test/Valheim.Testing.NativeSmoke env recover");
    }

    [Fact]
    public async Task LinuxsTruncatedServerCommStillMatches()
    {
        if (OperatingSystem.IsWindows()) return;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunRecovery.RequireNoLaunchProcessAsync(
            Host(), [], Timeout, processSnapshot: "31005 1000 ignored valheim_server."));
        Assert.Contains("process 31005 still runs", error.Message, StringComparison.Ordinal);
    }

    [OsFact("linux")]
    public async Task RealLinuxProcessListingReportsThisCiRunnerIdle() =>
        await RunRecovery.RequireNoLaunchProcessAsync(Host(), [], Timeout);
}

[Trait("Os", "macos")]
[Collection(nameof(WindowsGameProcessInventory))]
public sealed class MacLaunchGoneScriptTests
{
    [Fact]
    public async Task RealMacProcessListingReportsThisCiRunnerIdle() =>
        await RunRecovery.RequireNoLaunchProcessAsync(new LocalGameHost("launch-gone-mac", HostShell.Bash), [], TimeSpan.FromSeconds(30));
}

[Trait("Os", "windows")]
[Collection(nameof(WindowsGameProcessInventory))]
public sealed class WindowsLaunchGoneScriptTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static IGameHost Host() => new LocalGameHost("launch-gone-windows", HostShell.Pwsh);
    private static string Hash(string command) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command)));
    private static string Snapshot(params object[] processes) => JsonSerializer.Serialize(processes);

    [Fact]
    public async Task AnotherUsersValheimProcessRefusesAbandonment()
    {
        if (!OperatingSystem.IsWindows()) return;
        string snapshot = Snapshot(new { ProcessId = 41001, SessionId = 17, Name = "Valheim.exe", ExecutablePath = (string?)null,
            CommandLine = (string?)null, User = "another-user" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunRecovery.RequireNoLaunchProcessAsync(
            Host(), [], Timeout, processSnapshot: snapshot));

        Assert.Contains("process 41001 (Valheim.exe) still runs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheToolkitsOwnProcessesAreNotTheGame()
    {
        if (!OperatingSystem.IsWindows()) return;
        string snapshot = Snapshot(
            new { ProcessId = 41010, SessionId = 1, Name = "valheim-test.exe", ExecutablePath = @"C:\Users\someone\.dotnet\tools\valheim-test.exe",
                CommandLine = "valheim-test env recover --launch-gone", User = "someone" },
            new { ProcessId = 41011, SessionId = 1, Name = "Valheim.Testing.NativeSmoke.exe", ExecutablePath = (string?)null,
                CommandLine = (string?)null, User = "someone" });
        await RunRecovery.RequireNoLaunchProcessAsync(Host(), [], Timeout, processSnapshot: snapshot);
    }

    [Fact]
    public async Task AnotherSessionsJournalledLaunchCommandLineRefusesAbandonment()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string command = "a launch in another desktop session";
        string hash = Hash(command);
        string snapshot = Snapshot(new { ProcessId = 41002, SessionId = 42, Name = "steam.exe", ExecutablePath = @"C:\Program Files (x86)\Steam\steam.exe",
            CommandLine = command, User = "another-user" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunRecovery.RequireNoLaunchProcessAsync(
            Host(), [hash], Timeout, processSnapshot: snapshot));

        Assert.Contains("process 41002 (steam.exe) still runs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableProcessListingRefusesAndNamesTheMissingObservation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var error = await Assert.ThrowsAsync<HostOperationException>(() => RunRecovery.RequireNoLaunchProcessAsync(
            Host(), [new string('a', 64)], Timeout, processSnapshot: "__unreadable__"));

        Assert.Contains("inconclusive (unreadable process-list)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealWindowsProcessListingWithoutHashesReportsThisCiRunnerIdle()
    {
        if (!OperatingSystem.IsWindows()) return;
        await RunRecovery.RequireNoLaunchProcessAsync(Host(), [], Timeout);
    }

    [Fact]
    public async Task RealWindowsProcessListingWithAJournalledHashIgnoresUnreadableNonGameProcessesAndReportsIdle()
    {
        if (!OperatingSystem.IsWindows()) return;
        await RunRecovery.RequireNoLaunchProcessAsync(Host(), [new string('a', 64)], Timeout);
    }

    [Fact]
    public async Task UnreadableKernelAndNonGameProcessesDoNotMakeAHashCheckUnknown()
    {
        if (!OperatingSystem.IsWindows()) return;
        string snapshot = Snapshot(
            new { ProcessId = 0, Name = "System Idle Process", ExecutablePath = (string?)null, CommandLine = (string?)null },
            new { ProcessId = 4, Name = "System", ExecutablePath = (string?)null, CommandLine = (string?)null },
            new { ProcessId = 41003, Name = "another-users-tool.exe", ExecutablePath = (string?)null, CommandLine = (string?)null });

        await RunRecovery.RequireNoLaunchProcessAsync(Host(), [new string('b', 64)], Timeout, processSnapshot: snapshot);
    }
}
