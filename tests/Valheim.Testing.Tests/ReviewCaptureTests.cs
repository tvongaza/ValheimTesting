using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

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
    public async Task CaptureRestoresVisualStateAndWritesReviewOnlyEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (transport, client) = Client();
            using (client) using (var server = new ScriptedTransport().Actor())
            {
                bool arrived = false;
                var result = await ReviewCapture.CaptureCoreAsync(server, client, Plan(root), HostShellKind.PowerShell,
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
    public async Task CameraAzimuthChangesTheViewAndIsRecorded()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (transport, client) = Client();
            using (client) using (var server = new ScriptedTransport().Actor())
            {
                await ReviewCapture.CaptureCoreAsync(server, client, Plan(root) with { CameraAzimuthDegrees = 45 },
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
    public async Task CaptureFailureStillRestoresAndLeavesNoPassingImage()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client(_ => ScriptedTransport.Failed("ERROR: capture did not finish"));
        using (client) using (var server = new ScriptedTransport().Actor())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewCapture.CaptureCoreAsync(server, client, Plan(root),
                HostShellKind.PowerShell, Fetch, () => { }, CancellationToken.None));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
            Assert.False(Directory.Exists(root));
        }
    }

    [Fact]
    public async Task RetrievalFailureRestoresAndDoesNotExposePartialEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client();
        using (client) using (var server = new ScriptedTransport().Actor())
        {
            await Assert.ThrowsAsync<IOException>(() => ReviewCapture.CaptureCoreAsync(server, client, Plan(root),
                HostShellKind.PowerShell, (_, _, _) => throw new IOException("host transfer failed"), () => { }, CancellationToken.None));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
            Assert.False(Directory.Exists(root));
        }
    }

    [Fact]
    public async Task FailedRestorationCannotProduceAPassingCapture()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-review-" + Guid.NewGuid().ToString("N"));
        var (transport, client) = Client(restore: _ => throw new InvalidOperationException("restore failed"));
        using (client) using (var server = new ScriptedTransport().Actor())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewCapture.CaptureCoreAsync(server, client, Plan(root),
                HostShellKind.PowerShell, Fetch, () => { }, CancellationToken.None));
            Assert.Equal(1, transport.Count("cli_extension mymod.testing/review-restore"));
            Assert.False(Directory.Exists(root));
        }
    }
}
