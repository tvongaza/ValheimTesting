using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed class OwnedScreenshotTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("owned-screenshot-").FullName;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9DwwAAAABJRU5ErkJggg==");

    // Margin for waits that end in milliseconds; reached only when something that should happen does not.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The writer starts only after Capture reports that its first check found no complete PNG and it is about to wait,
    // and finishes after Capture reports a later check, so the order comes from events and not from sleeps (a later check
    // can be the wake for the new file's creation, so that the image was seen half written is not guaranteed here; the
    // test below holds that case). The writer is a dedicated thread, so a busy thread pool cannot delay it. The 30 s
    // deadline is a margin over a wait that takes milliseconds; it is reached only if the completed image goes unnoticed.
    [Fact]
    public void ADelayedPngIsCapturedInPrivateEvidenceOnlyAfterItIsComplete()
    {
        string evidence = Path.Combine(_root, "run evidence with spaces");
        using var waits = new SemaphoreSlim(0);
        Thread? writer = null;
        Exception? writerFailure = null;
        var transport = new ScriptedTransport().OnPrefix("cli_screenshot_path64 ", command =>
        {
            string path = CommandPath(command);
            writer = new Thread(() =>
            {
                try
                {
                    Assert.True(waits.Wait(Limit), "Capture never reached its wait for the image");
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    file.Write(Png, 0, Png.Length / 2);
                    file.Flush(true);
                    Assert.True(waits.Wait(Limit), "Capture never looked again after the image began to appear");
                    file.Write(Png, Png.Length / 2, Png.Length - Png.Length / 2);
                }
                catch (Exception error) { writerFailure = error; }
            }) { IsBackground = true };
            writer.Start();
            return ScriptedTransport.Ok("OK: screenshot queued path=" + path + " size=1x1");
        });
        using var actor = transport.Actor();
        var result = OwnedScreenshot.Capture(actor, evidence, "cli_screenshot review", 1, Limit, aboutToWait: () => waits.Release());
        Assert.True(writer!.Join(Limit));
        Assert.Null(writerFailure);
        Assert.True(result.Complete);
        Assert.Equal(FileHash.Sha256(Path.Combine(evidence, result.RelativeImage)), result.Sha256);
        Assert.Contains("screenshots/owned-", result.RelativeImage);
        Assert.Equal(result.Sha256, JsonDocument.Parse(File.ReadAllText(result.ResultFile)).RootElement
            .GetProperty("sha256").GetString());
        Assert.Single(transport.Commands.Where(command => command.StartsWith("cli_screenshot_path64 ", StringComparison.Ordinal)));
    }

    // A file event says the image may be complete; it does not say the writer has let go of it. Here the writer holds the file
    // (FileShare.None refuses every reader, on Windows and, through .NET's advisory lock, elsewhere) and writes the whole
    // image. It then lets Capture look three more times, counted after a drain so that looks before the file existed do not
    // count, and closes it. Closing raises no event Capture waits on, so only its bounded re-check can see the image.
    // Capture re-checks every 20 ms here.
    [Fact]
    public void AnImageClosedByItsWriterAfterTheLastFileEventIsStillCaptured()
    {
        using var waits = new SemaphoreSlim(0);
        Thread? writer = null;
        Exception? writerFailure = null;
        var transport = new ScriptedTransport().OnPrefix("cli_screenshot_path64 ", command =>
        {
            string path = CommandPath(command);
            writer = new Thread(() =>
            {
                try
                {
                    Assert.True(waits.Wait(Limit), "Capture never reached its wait for the image");
                    using var file = OpenHeld(path);
                    file.Write(Png);
                    file.Flush(true);
                    while (waits.Wait(0)) { }
                    for (int looks = 0; looks < 3; looks++)
                        Assert.True(waits.Wait(Limit), "Capture stopped looking while the writer held the image");
                }
                catch (Exception error) { writerFailure = error; }
            }) { IsBackground = true };
            writer.Start();
            return ScriptedTransport.Ok("OK: screenshot queued path=" + path + " size=1x1");
        });
        using var actor = transport.Actor();
        var result = OwnedScreenshot.Capture(actor, _root, "cli_screenshot held", 1, Limit,
            aboutToWait: () => waits.Release(), recheck: TimeSpan.FromMilliseconds(20));
        Assert.True(writer!.Join(Limit));
        Assert.Null(writerFailure);
        Assert.True(result.Complete);
        Assert.Equal(FileHash.Sha256(Path.Combine(_root, result.RelativeImage)), result.Sha256);
    }

    // On Unix .NET creates the file and then locks it, so a reader woken by the creation can take its shared lock in
    // between and the exclusive open fails once; opening again succeeds as soon as that reader lets go.
    private static FileStream OpenHeld(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
            catch (IOException) when (attempt < 200) { Thread.Sleep(5); }
        }
    }

    [Fact]
    public void AHalfWrittenPngIsNeverReportedAsCaptured()
    {
        string path = "";
        using var actor = new ScriptedTransport().OnPrefix("cli_screenshot_path64 ", command =>
        {
            path = CommandPath(command);
            File.WriteAllBytes(path, Png[..(Png.Length / 2)]);
            return ScriptedTransport.Ok("OK: screenshot queued path=" + path + " size=1x1");
        }).Actor();
        var result = OwnedScreenshot.Capture(actor, _root, "cli_screenshot half", 1, TimeSpan.FromMilliseconds(100));
        Assert.True(result.Accepted);
        Assert.False(result.Complete);
        Assert.Null(result.Sha256);
        Assert.Contains("did not appear", File.ReadAllText(result.ResultFile));
        Assert.Equal(Png[..(Png.Length / 2)], File.ReadAllBytes(path));
    }

    [Fact]
    public void AQueuedButMissingImageIsNotReportedAsCaptured()
    {
        using var actor = new ScriptedTransport().OnPrefix("cli_screenshot_path64 ", command =>
            ScriptedTransport.Ok("OK: screenshot queued path=" + CommandPath(command) + " size=1x1")).Actor();
        var result = OwnedScreenshot.Capture(actor, _root, "cli_screenshot missing", 1, TimeSpan.FromMilliseconds(100));
        Assert.True(result.Accepted);
        Assert.False(result.Complete);
        Assert.Null(result.Sha256);
        Assert.False(File.Exists(Path.Combine(_root, result.RelativeImage)));
        Assert.Contains("did not appear", File.ReadAllText(result.ResultFile));
    }

    [Fact]
    public void ACollidingNameIsSkippedBeforeAnyCommandOrExistingFileIsChanged()
    {
        string directory = Path.Combine(_root, "screenshots");
        Directory.CreateDirectory(directory);
        string first = new('a', 32);
        string existing = Path.Combine(directory, "owned-" + first + ".png");
        File.WriteAllBytes(existing, [1, 2, 3]);
        string[] ids = [first, new string('b', 32)];
        int index = 0;
        var request = OwnedScreenshot.Prepare(_root, "cli_screenshot review", nextId: () => ids[index++]);
        Assert.Equal(2, index);
        Assert.EndsWith("owned-" + ids[1] + ".png", request.Image);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(existing));
    }

    // Cancellation is ordered by Capture's own events, never by a timer: a timer's callback runs on the thread pool, and
    // a pool busy with other tests delayed it past the whole write deadline (#732), so Capture ran out its deadline and
    // returned an incomplete result normally, which is correct for a token nobody had cancelled yet. Here the token is
    // cancelled inside Capture's wait window (its check found no image and it is about to wait), the deadline is a margin
    // that is reached only if the cancellation goes unnoticed, and the write never happens.
    [Fact]
    public void AnInterruptedWriteLeavesOnlyAPrivateIntentAndIncompleteResult()
    {
        using var cancel = new CancellationTokenSource();
        using var actor = QueuedOnly();
        Assert.Throws<OperationCanceledException>(() => OwnedScreenshot.Capture(actor, _root,
            "cli_screenshot interrupted", 1, Limit, cancel.Token, aboutToWait: cancel.Cancel));
        AssertOnlyIntentAndInterruptedResult();
    }

    // The same interruption while Capture is parked in its wait: a dedicated thread cancels once Capture reports that it
    // is about to wait, so the wake comes from the token and not from a re-check or the deadline.
    [Fact]
    public void AnInterruptionWhileWaitingWakesCaptureAtOnce()
    {
        using var cancel = new CancellationTokenSource();
        using var waiting = new SemaphoreSlim(0);
        var canceller = new Thread(() =>
        {
            if (waiting.Wait(Limit)) cancel.Cancel();
        }) { IsBackground = true };
        canceller.Start();
        using var actor = QueuedOnly();
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => OwnedScreenshot.Capture(actor, _root,
            "cli_screenshot parked", 1, Limit, cancel.Token, aboutToWait: () => waiting.Release(),
            recheck: TimeSpan.FromSeconds(20)));
        // Neither the 20 s re-check nor the 30 s deadline can have ended the wait, whatever the host's speed.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), "Capture was not woken by the cancellation: " + started.Elapsed);
        Assert.True(canceller.Join(Limit));
        AssertOnlyIntentAndInterruptedResult();
    }

    private GameActor QueuedOnly() => new ScriptedTransport().OnPrefix("cli_screenshot_path64 ", command =>
        ScriptedTransport.Ok("OK: screenshot queued path=" + CommandPath(command) + " size=1x1")).Actor();

    private void AssertOnlyIntentAndInterruptedResult()
    {
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "screenshots"), "*.intent.json"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "screenshots"), "*.png"));
        Assert.Contains("interrupted", File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(_root, "screenshots"), "*.result.json"))));
    }

    [Fact]
    public void AReplyNamingAnExternalOrChangedFileIsRefusedWithoutTouchingIt()
    {
        string external = Path.Combine(_root, "personal.png");
        File.WriteAllBytes(external, [7, 8, 9]);
        using var actor = new ScriptedTransport().OnPrefix("cli_screenshot_path64 ", _ =>
            ScriptedTransport.Ok("OK: screenshot queued path=" + external + " size=1x1")).Actor();
        var result = OwnedScreenshot.Capture(actor, _root, "cli_screenshot bad", 1, TimeSpan.FromMilliseconds(100));
        Assert.False(result.Complete);
        Assert.Contains("different screenshot destination", result.Status);
        Assert.Equal([7, 8, 9], File.ReadAllBytes(external));
    }

    [Fact]
    public void ACommandFailureLeavesAnIncompletePrivateResult()
    {
        using var actor = new ScriptedTransport().OnPrefix("cli_screenshot_path64 ", _ =>
            throw new IOException("connection closed")).Actor();
        Assert.Throws<IOException>(() => OwnedScreenshot.Capture(actor, _root,
            "cli_screenshot failure", 1, TimeSpan.FromMilliseconds(100)));
        Assert.Contains("connection closed", File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(_root, "screenshots"), "*.result.json"))));
    }

    private static string CommandPath(string command)
    {
        string token = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Replace('-', '+').Replace('_', '/');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token.PadRight((token.Length + 3) / 4 * 4, '=')));
    }
}
