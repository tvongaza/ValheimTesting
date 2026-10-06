using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

public sealed class ReviewCaptureTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];
    private static object State(string state, string id = "shot-1") => new { source = "review-state", complete = true, id, state, debugEnvironment = "Rain", mistVolumes = 4 };
    private static ReviewCapturePlan Plan(string local, bool mist = true, bool clutter = true) =>
        new("shot-1", new HeightExpectation(100, -40, 42.5f), "Clear", .5f, 25, 15,
            "mymod.testing", @"C:\pcstage\review-shot-1", local, "1716468958", "Valheim 1.0.16",
            new Dictionary<string, string> { ["example.mymod"] = "pinned-md5" }, mist, clutter);

    private static (ScriptedTransport Transport, GameActor Actor) Client(Func<string, valheim_cli.Testing.CommandResult>? capture = null,
        Func<IReadOnlyList<string>, object>? restore = null)
    {
        var transport = new ScriptedTransport()
            .Extension("mymod.testing", "review-begin", _ => State("begun"))
            .Extension("mymod.testing", "review-mist-off", _ => State("mist-off"), readOnly: false)
            .Extension("mymod.testing", "review-clutter-off", _ => State("clutter-off"), readOnly: false)
            .Extension("mymod.testing", "review-restore", restore ?? (_ => State("restored")), readOnly: false)
            .OnPrefix("cli_env ", _ => ScriptedTransport.Ok("OK: ENV current=Clear tod=0.500"))
            .OnPrefix("cli_capture ", capture ?? (command => ScriptedTransport.Ok("OK: CAPTURE name=shot-1 path=" + command.Split(' ')[1] + " bytes=20 size=1280x720 ready_ms=3")));
        return (transport, transport.Actor());
    }

    private static async Task<FetchedDirectory> Fetch(string host, string local, CancellationToken _) {
        Directory.CreateDirectory(local);
        await File.WriteAllBytesAsync(Path.Combine(local, "shot-1.png"), Png);
        return new FetchedDirectory(local, "archive-sha", Png.Length, 1);
    }

    [Fact]
    public void CaptureRestoresVisualStateAndWritesReviewOnlyEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (transport, client) = Client();
            using (client) using (var server = new ScriptedTransport().Actor())
            {
                bool arrived = false;
                var result = ReviewCapture.CaptureCore(server, client, Plan(root), HostShellKind.PowerShell,
                    Fetch, () => arrived = true, CancellationToken.None);
                Assert.True(arrived);
                Assert.True(File.Exists(result.ImagePath));
                Assert.Equal(Png.Length, result.Bytes);
                string metadata = File.ReadAllText(result.MetadataPath);
                Assert.Contains("\"visualVerdict\": \"not asserted\"", metadata);
                Assert.Contains("\"debugEnvironment\": \"Rain\"", metadata);
                string[] commands = transport.Commands.Where(c => !c.StartsWith("cli_expect", StringComparison.Ordinal)).ToArray();
                Assert.True(Array.IndexOf(commands, "cli_extension mymod.testing/review-begin shot-1") < Array.IndexOf(commands, "cli_env 0.5 Clear"));
                Assert.True(Array.IndexOf(commands, "cli_extension mymod.testing/review-clutter-off shot-1") < Array.FindIndex(commands, c => c.StartsWith("cli_capture ", StringComparison.Ordinal)));
                Assert.Equal("cli_extension mymod.testing/review-restore shot-1", commands.Last());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void TheStillIsLinkedFromResultJsonBesideSnapshotsInAttachmentOrder()
    {
        string run = Directory.CreateTempSubdirectory("vt-review-report-").FullName;
        try
        {
            var (_, client) = Client();
            using (client) using (var server = new ScriptedTransport().Actor())
            {
                var receipt = ReviewCapture.CaptureCore(server, client, Plan(Path.Combine(run, "review-shot-1")), HostShellKind.PowerShell,
                    Fetch, () => { }, CancellationToken.None);
                Assert.Equal(new EvidenceReference("review-still", "shot-1", "1716468958", receipt.MetadataPath, FileHash.Sha256(receipt.MetadataPath)), receipt.Evidence);
                Assert.Contains(receipt.Sha256, File.ReadAllText(receipt.MetadataPath)); // The sidecar carries the image's digest.
                var report = new ScenarioReport("review evidence");
                report.Step("capture", () => { });
                report.Attach(receipt.Evidence);
                report.Attach(new EvidenceReference("review-still", "outside", "1716468958", "/elsewhere/outside.png", new string('a', 64)));
                report.Write(run);
                Assert.Equal(new[] { "review-shot-1/shot-1.json", Path.GetFullPath("/elsewhere/outside.png") }, report.Evidence.Select(e => e.File).ToArray());
                using var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "result.json")));
                var linked = result.RootElement.GetProperty("Evidence")[0];
                Assert.Equal("review-still", linked.GetProperty("Kind").GetString());
                Assert.Equal(FileHash.Sha256(receipt.MetadataPath), linked.GetProperty("Sha256").GetString());
            }
        }
        finally { Directory.Delete(run, true); }
    }

    [Fact]
    public void AReportRefusesEvidenceItCannotNameOrHash()
    {
        var report = new ScenarioReport("bad evidence");
        Assert.Throws<ArgumentException>(() => report.Attach(new EvidenceReference("Review Still", "s", "1", "/a.png", new string('a', 64))));
        Assert.Throws<ArgumentException>(() => report.Attach(new EvidenceReference("review-still", "s", "1", "/a.png", "not-a-hash")));
        Assert.Throws<ArgumentException>(() => report.Attach(new EvidenceReference("review-still", "s", "1", " ", new string('a', 64))));
    }

    [Fact]
    public void CameraAzimuthChangesTheViewAndIsRecorded()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (transport, client) = Client();
            using (client) using (var server = new ScriptedTransport().Actor())
            {
                ReviewCapture.CaptureCore(server, client, Plan(root) with { CameraAzimuthDegrees = 45 },
                    HostShellKind.PowerShell, Fetch, () => { }, CancellationToken.None);
                var capture = transport.Commands.Single(c => c.StartsWith("cli_capture ", StringComparison.Ordinal)).Split(' ');
                Assert.True(double.Parse(capture[2], System.Globalization.CultureInfo.InvariantCulture) > 100);
                Assert.True(double.Parse(capture[4], System.Globalization.CultureInfo.InvariantCulture) > -40);
                Assert.Contains("\"CameraAzimuthDegrees\": 45", File.ReadAllText(Path.Combine(root, "shot-1.json")));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CaptureFailureStillRestoresAndLeavesNoPassingImage()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client(_ => ScriptedTransport.Failed("ERROR: capture did not finish"));
        using (client) using (var server = new ScriptedTransport().Actor())
        {
            Assert.Throws<InvalidOperationException>(() => ReviewCapture.CaptureCore(server, client, Plan(root),
                HostShellKind.PowerShell, Fetch, () => { }, CancellationToken.None));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
            Assert.False(Directory.Exists(root));
        }
    }

    [Fact]
    public void RetrievalFailureRestoresAndDoesNotExposePartialEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client();
        using (client) using (var server = new ScriptedTransport().Actor())
        {
            Assert.Throws<IOException>(() => ReviewCapture.CaptureCore(server, client, Plan(root),
                HostShellKind.PowerShell, (_, _, _) => throw new IOException("host transfer failed"), () => { }, CancellationToken.None));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
            Assert.False(Directory.Exists(root));
        }
    }

    [Fact]
    public void CancellationDuringTransferRestoresAndPublishesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        var (transport, client) = Client();
        using (client) using (var server = new ScriptedTransport().Actor())
        {
            async Task<FetchedDirectory> Transfer(string host, string local, CancellationToken _)
            {
                var copied = await Fetch(host, local, CancellationToken.None);
                cancel.Cancel();
                return copied;
            }
            Assert.ThrowsAny<OperationCanceledException>(() => ReviewCapture.CaptureCore(server, client, Plan(root),
                HostShellKind.PowerShell, Transfer, () => { }, cancel.Token));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
            Assert.False(Directory.Exists(root));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(root)!, Path.GetFileName(root) + ".partial-*"));
        }
    }

    [Fact]
    public void FailedRestorationCannotProduceAPassingCapture()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client(restore: _ => throw new InvalidOperationException("restore failed"));
        using (client) using (var server = new ScriptedTransport().Actor())
        {
            Assert.Throws<InvalidOperationException>(() => ReviewCapture.CaptureCore(server, client, Plan(root),
                HostShellKind.PowerShell, Fetch, () => { }, CancellationToken.None));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
            Assert.False(Directory.Exists(root));
        }
    }
}
