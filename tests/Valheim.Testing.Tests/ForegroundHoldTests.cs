using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed class ForegroundHoldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vt-hold-" + Guid.NewGuid().ToString("N"));

    public ForegroundHoldTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task FinishSignalsTheOwnerAndOnlyTheOwnerRemovesItsMarker()
    {
        string evidence = Path.Combine(_root, "evidence"), markers = Path.Combine(_root, "holds");
        Directory.CreateDirectory(evidence);
        using (var hold = ForegroundHold.Open("run-test", evidence, markers))
        {
            Task wait = hold.WaitAsync(CancellationToken.None);
            Assert.False(wait.IsCompleted);
            ForegroundHold.Request("run-test", markers);
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(Path.Combine(markers, "run-test.json")));
        }
        Assert.False(File.Exists(Path.Combine(markers, "run-test.json")));
        Assert.False(File.Exists(Path.Combine(markers, "run-test.finish")));
    }

    [Fact]
    public async Task CancellationEndsTheWaitAndNormalCleanupRemovesTheMarker()
    {
        string evidence = Path.Combine(_root, "evidence"), markers = Path.Combine(_root, "holds");
        Directory.CreateDirectory(evidence);
        using (var hold = ForegroundHold.Open("run-cancel", evidence, markers))
        using (var cancel = new CancellationTokenSource())
        {
            Task wait = hold.WaitAsync(cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }
        Assert.False(File.Exists(Path.Combine(markers, "run-cancel.json")));
    }

    [Fact]
    public void ASecondOwnerCannotReplaceAnActiveOrStaleMarker()
    {
        string evidence = Path.Combine(_root, "evidence"), markers = Path.Combine(_root, "holds");
        Directory.CreateDirectory(evidence);
        using var hold = ForegroundHold.Open("run-owned", evidence, markers);
        Assert.Throws<IOException>(() => ForegroundHold.Open("run-owned", evidence, markers));
        ForegroundHold.Request("run-owned", markers);
        Assert.Throws<IOException>(() => ForegroundHold.Request("run-owned", markers));
    }

    [Fact]
    public void FinishCommandSignalsOnlyALiveHeldRun()
    {
        string evidence = Path.Combine(_root, "evidence"), markers = Path.Combine(_root, "holds");
        Directory.CreateDirectory(evidence);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, ForegroundHold.FinishCommand(["--run"], output, error, markers));
        Assert.Equal(3, ForegroundHold.FinishCommand(["--run", "missing"], output, error, markers));
        using (var hold = ForegroundHold.Open("run-live", evidence, markers))
        {
            Assert.Equal(0, ForegroundHold.FinishCommand(["--run", "run-live"], output, error, markers));
            Assert.True(File.Exists(Path.Combine(markers, "run-live.json"))); // finish did not take ownership of cleanup
            Assert.Equal(3, ForegroundHold.FinishCommand(["--run", "run-live"], output, error, markers));
        }
        Assert.Empty(Directory.GetFiles(markers));
    }

    [Fact]
    public async Task AClientExitDuringTheHoldFailsAtOnceAndRecordsItsExitCode()
    {
        string evidence = Path.Combine(_root, "evidence"), markers = Path.Combine(_root, "holds");
        Directory.CreateDirectory(evidence);
        var process = new FakeOwnedProcess(42);
        using (var hold = ForegroundHold.Open("run-exited", evidence, markers))
        {
            Task wait = hold.WaitAsync(CancellationToken.None, [("client", process)]);
            process.Exit(17);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("Held client exited during the hold with code 17", failure.Message);
            Assert.Equal(0, process.Stops);
        }
        Assert.Empty(Directory.GetFiles(markers));
    }

    [Fact]
    public async Task AFinishRequestWinsOnlyWhileTheOwnedProcessStillRuns()
    {
        string evidence = Path.Combine(_root, "evidence"), markers = Path.Combine(_root, "holds");
        Directory.CreateDirectory(evidence);
        var process = new FakeOwnedProcess(43);
        using (var hold = ForegroundHold.Open("run-running", evidence, markers))
        {
            Task wait = hold.WaitAsync(CancellationToken.None, [("client", process)]);
            ForegroundHold.Request("run-running", markers);
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(process.HasExited);
        }
    }

    [Fact]
    public async Task AServerExitFailsAJoinedHoldWhileItsClientIsStillRunning()
    {
        string evidence = Path.Combine(_root, "evidence"), markers = Path.Combine(_root, "holds");
        Directory.CreateDirectory(evidence);
        var server = new FakeOwnedProcess(44);
        var client = new FakeOwnedProcess(45);
        using var hold = ForegroundHold.Open("run-server-exited", evidence, markers);
        Task wait = hold.WaitAsync(CancellationToken.None, [("server", server), ("client", client)]);
        server.Exit(3);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Held server exited during the hold with code 3", failure.Message);
        Assert.False(client.HasExited);
    }

    [Fact]
    public void ServerCommandHintRequiresTheHostLocalLeaseAHolderActuallyPublished()
    {
        string evidence = Path.Combine(_root, "server-hint");
        Directory.CreateDirectory(evidence);
        Assert.Null(ForegroundHold.ServerCommandHint(hasServer: true, evidence, serverEvidence: null));

        File.WriteAllText(Path.Combine(evidence, OwnedClientCommandLease.ServerFileName), "local server lease");
        Assert.Contains("--role server", ForegroundHold.ServerCommandHint(hasServer: true, evidence, serverEvidence: null));
        Assert.Null(ForegroundHold.ServerCommandHint(hasServer: false, evidence, serverEvidence: null));
    }

    [Fact]
    public void RecoveryOfAKilledJoinedServerHoldRetiresBothRolesCapabilities()
    {
        string clientEvidence = Path.Combine(_root, "client-evidence"), serverEvidence = Path.Combine(_root, "server-evidence"), markers = Path.Combine(_root, "holds");
        Directory.CreateDirectory(clientEvidence);
        Directory.CreateDirectory(serverEvidence);
        using (var live = ForegroundHold.Open("run-live", clientEvidence, markers, serverEvidence))
        {
            using var recorded = JsonDocument.Parse(File.ReadAllText(Path.Combine(markers, "run-live.json")));
            Assert.Equal(Path.GetFullPath(serverEvidence), recorded.RootElement.GetProperty("ServerEvidence").GetString());
            Assert.Throws<InvalidOperationException>(() => ForegroundHold.RetireRecovered("run-live", markers));
        }

        string marker = Path.Combine(markers, "run-dead.json"), request = Path.Combine(markers, "run-dead.finish");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { Run = "run-dead", Evidence = clientEvidence, ServerEvidence = serverEvidence, Pid = int.MaxValue, Started = "1" }));
        File.WriteAllText(request, "");
        foreach (string name in new[] { OwnedClientCommandLease.FileName, OwnedClientCommandLease.MenuPins, OwnedClientCommandLease.WorldPins })
            File.WriteAllText(Path.Combine(clientEvidence, name), "temporary client capability");
        foreach (string name in new[] { OwnedClientCommandLease.ServerFileName, OwnedClientCommandLease.ServerMenuPins, OwnedClientCommandLease.ServerWorldPins })
            File.WriteAllText(Path.Combine(serverEvidence, name), "temporary server capability");
        string clientRecord = Path.Combine(clientEvidence, "owned-cli-command-client-history.jsonl");
        string serverRecord = Path.Combine(serverEvidence, "owned-cli-command-server-history.jsonl");
        File.WriteAllText(clientRecord, "retained client command evidence");
        File.WriteAllText(serverRecord, "retained server command evidence");
        ForegroundHold.RetireRecovered("run-dead", markers);
        foreach (string name in new[] { OwnedClientCommandLease.FileName, OwnedClientCommandLease.MenuPins, OwnedClientCommandLease.WorldPins })
            Assert.False(File.Exists(Path.Combine(clientEvidence, name)));
        foreach (string name in new[] { OwnedClientCommandLease.ServerFileName, OwnedClientCommandLease.ServerMenuPins, OwnedClientCommandLease.ServerWorldPins })
            Assert.False(File.Exists(Path.Combine(serverEvidence, name)));
        Assert.Equal("retained client command evidence", File.ReadAllText(clientRecord));
        Assert.Equal("retained server command evidence", File.ReadAllText(serverRecord));
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(request));
    }
}

// finish runs from another shell: a separate valheim-test process must read the live holder's identity exactly as the
// holder recorded it. On Linux each .NET process derives Process.StartTime from its own boot-time estimate, so a marker
// in that form can differ from the finisher's read and refuse a live holder as gone.
[Trait("Os", "unix")]
public sealed class ForegroundHoldCrossProcessTests
{
    [Fact]
    public async Task FinishFromASeparateProcessReachesTheLiveHolder()
    {
        if (OperatingSystem.IsWindows()) return; // A child's data root cannot be redirected there; Process.StartTime is exact on Windows.
        string root = Directory.CreateTempSubdirectory("vt-hold-cross-").FullName;
        try
        {
            // The finisher's own data root on this machine: XDG_DATA_HOME on Linux, the home folder on macOS.
            string home = Path.Combine(root, "home"), xdg = Path.Combine(root, "xdg");
            string data = OperatingSystem.IsLinux() ? Path.Combine(xdg, "ValheimTesting") : Path.Combine(home, "Library", "Application Support", "ValheimTesting");
            string markers = Path.Combine(data, "runs", "holds"), evidence = Path.Combine(root, "evidence");
            Directory.CreateDirectory(evidence);
            using var hold = ForegroundHold.Open("run-cross", evidence, markers);
            Task wait = hold.WaitAsync(CancellationToken.None);

            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { typeof(ForegroundHold).Assembly.Location, "finish", "--run", "run-cross" },
            };
            start.Environment["HOME"] = home;
            start.Environment["XDG_DATA_HOME"] = xdg;
            await using var finisher = StandInProcess.Start(start, nameof(FinishFromASeparateProcessReachesTheLiveHolder));
            Task<string> output = finisher.Process.StandardOutput.ReadToEndAsync(), error = finisher.Process.StandardError.ReadToEndAsync();
            await finisher.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(finisher.Process.ExitCode == 0, "finish refused the live holder: " + await error + await output);
            Assert.Contains("Finish requested for held run run-cross", await output);
            await wait.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
