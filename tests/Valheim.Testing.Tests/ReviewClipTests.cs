using System.Security.Cryptography;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed class ReviewClipTests
{
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAKAAAABaCAIAAACwpMoFAAAAQElEQVR4nO3BAQ0AAADCoPdPbQ8HFAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA8GOpGgABimw4TgAAAABJRU5ErkJggg==";
    private static readonly byte[] Png = Convert.FromBase64String(PngBase64);
    private static readonly string PngHash = Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant();
    private static ReviewClipPlan Plan(string output) => new("motion-1", "mymod.testing", @"C:\test-runs\motion-1", output,
        "1716468958", "Valheim 1.0.16", new Dictionary<string, string> { ["example.mymod"] = "exact-pin" }, 160, 90, 2, 2);

    private static (ScriptedTransport Transport, GameActor Actor) Client(bool ready = true,
        Func<IReadOnlyList<string>, object>? clip = null, Func<IReadOnlyList<string>, object>? restore = null)
    {
        var transport = new ScriptedTransport()
            .Extension("valheim.session", "state", _ => new {
                source = "session-state", complete = true, phase = ready ? "world-present" : "loading", worldUid = "1716468958",
                worldPresent = true, worldReady = ready, server = false, dedicated = false, localPlayer = ready,
                playerReady = ready, saving = false, loadError = false, connectionStatus = ready ? "Connected" : "None"
            })
            .Extension("mymod.testing", "review-begin", _ => new { source = "review-state", complete = true, id = "motion-1", state = "begun" })
            .Extension("mymod.testing", "review-clip-frames", clip ?? (args => new {
                source = "scene-only-frames", complete = true, id = args[0], directory = args[1], width = 160, height = 90, frames = 2
            }), readOnly: false)
            .Extension("mymod.testing", "review-restore", restore ?? (_ => new {
                source = "review-state", complete = true, id = "motion-1", state = "restored"
            }), readOnly: false);
        return (transport, transport.Actor(expectations: "cli_expect worlduid=1716468958"));
    }

    private static async Task<FetchedDirectory> Fetch(string host, string local, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(local);
        await File.WriteAllBytesAsync(Path.Combine(local, "frame-000.png"), Png, token);
        await File.WriteAllBytesAsync(Path.Combine(local, "frame-001.png"), Png, token);
        await File.WriteAllLinesAsync(Path.Combine(local, "frames.csv"), ["frame,elapsed_ms,bytes,sha256",
            $"0,0,{Png.Length},{PngHash}", $"1,500,{Png.Length},{PngHash}"], token);
        return new FetchedDirectory(local, "archive-digest", Png.Length * 2, 3);
    }

    [Fact]
    public void ReadyClientPublishesHashedFramesAndRestoresLease()
    {
        string output = Path.Combine(Path.GetTempPath(), "vt-motion-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (transport, client) = Client();
            using (client)
            {
                var receipt = ReviewClip.CaptureCore(client, Plan(output), Fetch, CancellationToken.None);
                Assert.Equal(output, receipt.FramesDirectory);
                Assert.Equal(2, receipt.Frames);
                Assert.Equal(500, receipt.DurationMs);
                Assert.Equal(Png.Length * 2L, receipt.Bytes);
                // The receipt's digest is the game's manifest, which lists every frame's digest.
                string manifest = Path.Combine(output, "frames.csv");
                Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))).ToLowerInvariant(), receipt.ManifestSha256);
                Assert.Equal(new[] { "frame-000.png", "frame-001.png", "frames.csv", "motion-1.json" },
                    Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
                string metadata = File.ReadAllText(receipt.MetadataPath);
                Assert.Contains("\"visualVerdict\": \"not asserted\"", metadata);
                Assert.Contains(receipt.ManifestSha256, metadata);
                Assert.Equal(new EvidenceReference("review-clip", "motion-1", "1716468958", receipt.MetadataPath, FileHash.Sha256(receipt.MetadataPath)), receipt.Evidence);
                Assert.Equal("cli_extension mymod.testing/review-restore motion-1", transport.Commands.Last());
            }
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }

    [Fact]
    public void UnreadyClientNeverStartsCapture()
    {
        var (transport, client) = Client(ready: false);
        using (client)
        {
            Assert.Throws<InvalidOperationException>(() => ReviewClip.CaptureCore(client,
                Plan(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), Fetch, CancellationToken.None));
            Assert.DoesNotContain(transport.Commands, command => command.Contains("review-begin"));
        }
    }

    [Fact]
    public void FailedTransferCannotBecomePassingEvidence()
    {
        string output = Path.Combine(Path.GetTempPath(), "vt-motion-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client();
        using (client)
        {
            Assert.Throws<IOException>(() => ReviewClip.CaptureCore(client, Plan(output),
                (_, local, _) => { Directory.CreateDirectory(local); throw new IOException("transfer failed"); }, CancellationToken.None));
            Assert.False(Directory.Exists(output));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".partial-*"));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
        }
    }

    [Fact]
    public void CancellationAfterBeginRestoresStateAndLeavesNoClip()
    {
        string output = Path.Combine(Path.GetTempPath(), "vt-motion-" + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        var (transport, client) = Client(clip: _ => { cancel.Cancel(); return new {
            source = "scene-only-frames", complete = true, id = "motion-1", directory = @"C:\test-runs\motion-1\motion-1-frames",
            width = 160, height = 90, frames = 2
        }; });
        using (client)
        {
            Assert.ThrowsAny<OperationCanceledException>(() => ReviewClip.CaptureCore(client, Plan(output), Fetch, cancel.Token));
            Assert.False(Directory.Exists(output));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
        }
    }

    [Fact]
    public void CancellationDuringTransferCannotPublishCompletedFrames()
    {
        string output = Path.Combine(Path.GetTempPath(), "vt-motion-" + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        var (transport, client) = Client();
        using (client)
        {
            async Task<FetchedDirectory> Transfer(string host, string local, CancellationToken _)
            {
                var copied = await Fetch(host, local, CancellationToken.None);
                cancel.Cancel();
                return copied;
            }
            Assert.ThrowsAny<OperationCanceledException>(() => ReviewClip.CaptureCore(client, Plan(output), Transfer, cancel.Token));
            Assert.False(Directory.Exists(output));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".partial-*"));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
        }
    }

    [Fact]
    public void PinDriftBeforeTheFrameCommandRefusesCapture()
    {
        string output = Path.Combine(Path.GetTempPath(), "vt-motion-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client();
        using (client)
        {
            // The actor was verified before the drift. Its pin check before the frame command must still refuse it.
            bool refused = false;
            transport.OnPrefix("cli_expect ", _ => {
                if (!refused && transport.Count("cli_extension mymod.testing/review-begin") > 0)
                { refused = true; return ScriptedTransport.Failed("MISMATCH worlduid: wrong fixture"); }
                return ScriptedTransport.Ok("OK: EXPECT");
            });
            // A drifted actor refuses every later command, the restore included (the adapter restores on unload).
            var error = Assert.Throws<AggregateException>(() => ReviewClip.CaptureCore(client, Plan(output), Fetch, CancellationToken.None));
            Assert.All(error.InnerExceptions, inner => Assert.IsType<InvalidOperationException>(inner));
            Assert.True(refused);
            Assert.Equal(0, transport.Count("cli_extension mymod.testing/review-clip-frames"));
            Assert.False(Directory.Exists(output));
        }
    }

    [Fact]
    public void CorruptedFrameDigestIsRefusedAndNothingIsPublished()
    {
        string output = Path.Combine(Path.GetTempPath(), "vt-motion-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client();
        using (client)
        {
            async Task<FetchedDirectory> Corrupt(string host, string local, CancellationToken token)
            {
                var copied = await Fetch(host, local, token);
                byte[] changed = (byte[])Png.Clone();
                changed[^13] ^= 1; // Same length, different bytes from those the game hashed.
                await File.WriteAllBytesAsync(Path.Combine(local, "frame-001.png"), changed, token);
                return copied;
            }
            var error = Assert.Throws<InvalidDataException>(() => ReviewClip.CaptureCore(client, Plan(output), Corrupt, CancellationToken.None));
            Assert.Contains("game-side digest", error.Message);
            Assert.False(Directory.Exists(output));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".partial-*"));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
        }
    }
}
