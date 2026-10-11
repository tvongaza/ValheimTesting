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
    public void RecoveryRetiresOnlyADeadOwnersMarker()
    {
        string evidence = Path.Combine(_root, "evidence"), markers = Path.Combine(_root, "holds");
        Directory.CreateDirectory(evidence);
        using (var live = ForegroundHold.Open("run-live", evidence, markers))
            Assert.Throws<InvalidOperationException>(() => ForegroundHold.RetireRecovered("run-live", markers));

        string marker = Path.Combine(markers, "run-dead.json"), request = Path.Combine(markers, "run-dead.finish");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { Run = "run-dead", Evidence = evidence, Pid = int.MaxValue, Started = "1" }));
        File.WriteAllText(request, "");
        foreach (string name in new[] { OwnedClientCommandLease.FileName, OwnedClientCommandLease.MenuPins, OwnedClientCommandLease.WorldPins })
            File.WriteAllText(Path.Combine(evidence, name), "temporary lease");
        string record = Path.Combine(evidence, "owned-cli-command-history.jsonl");
        File.WriteAllText(record, "retained command evidence");
        ForegroundHold.RetireRecovered("run-dead", markers);
        foreach (string name in new[] { OwnedClientCommandLease.FileName, OwnedClientCommandLease.MenuPins, OwnedClientCommandLease.WorldPins })
            Assert.False(File.Exists(Path.Combine(evidence, name)));
        Assert.Equal("retained command evidence", File.ReadAllText(record));
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(request));
    }
}
