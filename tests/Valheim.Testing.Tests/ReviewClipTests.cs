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
    public async Task ReadyClientProducesBoundedAnimatedPngAndRestoresLease()
    {
        string output = Path.Combine(Path.GetTempPath(), "vt-motion-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (transport, client) = Client();
            using (client)
            {
                var receipt = await ReviewClip.CaptureCoreAsync(client, Plan(output), Fetch, CancellationToken.None);
                Assert.True(File.Exists(receipt.ClipPath));
                Assert.Equal(2, receipt.Frames);
                Assert.Equal(500, receipt.DurationMs);
                Assert.Contains("\"visualVerdict\": \"not asserted\"", File.ReadAllText(receipt.MetadataPath));
                byte[] bytes = File.ReadAllBytes(receipt.ClipPath);
                Assert.True(bytes.AsSpan(0, 8).SequenceEqual(Png.AsSpan(0, 8)));
                Assert.Contains("acTL", System.Text.Encoding.ASCII.GetString(bytes));
                Assert.Contains("fdAT", System.Text.Encoding.ASCII.GetString(bytes));
                Assert.Equal("cli_extension mymod.testing/review-restore motion-1", transport.Commands.Last());
            }
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }

    [Fact]
    public async Task UnreadyClientNeverStartsCapture()
    {
        var (transport, client) = Client(ready: false);
        using (client)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewClip.CaptureCoreAsync(client,
                Plan(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), Fetch, CancellationToken.None));
            Assert.DoesNotContain(transport.Commands, command => command.Contains("review-begin"));
        }
    }

    [Fact]
    public async Task FailedTransferCannotBecomePassingEvidence()
    {
        string output = Path.Combine(Path.GetTempPath(), "vt-motion-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client();
        using (client)
        {
            await Assert.ThrowsAsync<IOException>(() => ReviewClip.CaptureCoreAsync(client, Plan(output),
                (_, local, _) => { Directory.CreateDirectory(local); throw new IOException("transfer failed"); }, CancellationToken.None));
            Assert.False(Directory.Exists(output));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".partial-*"));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
        }
    }

    [Fact]
    public async Task CancellationAfterBeginRestoresStateAndLeavesNoClip()
    {
        string output = Path.Combine(Path.GetTempPath(), "vt-motion-" + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        var (transport, client) = Client(clip: _ => { cancel.Cancel(); return new {
            source = "scene-only-frames", complete = true, id = "motion-1", directory = @"C:\test-runs\motion-1\motion-1-frames",
            width = 160, height = 90, frames = 2
        }; });
        using (client)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReviewClip.CaptureCoreAsync(client, Plan(output), Fetch, cancel.Token));
            Assert.False(Directory.Exists(output));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
        }
    }

    [Fact]
    public async Task CancellationDuringTransferCannotPublishCompletedFrames()
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReviewClip.CaptureCoreAsync(client, Plan(output), Transfer, cancel.Token));
            Assert.False(Directory.Exists(output));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".partial-*"));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
        }
    }

    [Fact]
    public void CorruptedFrameDigestIsRefused()
    {
        string dir = Directory.CreateTempSubdirectory("vt-bad-frame-").FullName;
        try
        {
            string frame = Path.Combine(dir, "frame.png");
            File.WriteAllBytes(frame, Png);
            Assert.Throws<InvalidDataException>(() => ApngClip.Write(Path.Combine(dir, "clip.apng"), [
                new ApngClip.Frame(frame, 0, new string('0', 64), Png.Length),
                new ApngClip.Frame(frame, 500, PngHash, Png.Length)]));
        }
        finally { Directory.Delete(dir, true); }
    }

}
