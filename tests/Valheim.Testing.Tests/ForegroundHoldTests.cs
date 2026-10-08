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
}
