using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed class OwnedScreenshotTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("owned-screenshot-").FullName;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9DwwAAAABJRU5ErkJggg==");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ADelayedPngIsCapturedInPrivateEvidenceOnlyAfterItIsComplete()
    {
        Task? write = null;
        var transport = new ScriptedTransport().OnPrefix("cli_screenshot ", command =>
        {
            string path = CommandPath(command);
            write = Task.Run(async () =>
            {
                await Task.Delay(100);
                File.WriteAllBytes(path, Png);
            });
            return ScriptedTransport.Ok("OK: screenshot queued path=" + path + " size=1x1");
        });
        using var actor = transport.Actor();
        var result = OwnedScreenshot.Capture(actor, _root, "cli_screenshot review", 1, TimeSpan.FromSeconds(2));
        await write!;
        Assert.True(result.Complete);
        Assert.Equal(FileHash.Sha256(Path.Combine(_root, result.RelativeImage)), result.Sha256);
        Assert.Contains("screenshots/owned-", result.RelativeImage);
        Assert.Equal(result.Sha256, JsonDocument.Parse(File.ReadAllText(result.ResultFile)).RootElement
            .GetProperty("sha256").GetString());
        Assert.Single(transport.Commands.Where(command => command.StartsWith("cli_screenshot \"", StringComparison.Ordinal)));
    }

    [Fact]
    public void AQueuedButMissingImageIsNotReportedAsCaptured()
    {
        using var actor = new ScriptedTransport().OnPrefix("cli_screenshot ", command =>
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

    [Fact]
    public void AnInterruptedWriteLeavesOnlyAPrivateIntentAndIncompleteResult()
    {
        using var cancel = new CancellationTokenSource();
        using var actor = new ScriptedTransport().OnPrefix("cli_screenshot ", command =>
        {
            cancel.CancelAfter(50);
            return ScriptedTransport.Ok("OK: screenshot queued path=" + CommandPath(command) + " size=1x1");
        }).Actor();
        Assert.Throws<OperationCanceledException>(() => OwnedScreenshot.Capture(actor, _root,
            "cli_screenshot interrupted", 1, TimeSpan.FromSeconds(2), cancel.Token));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "screenshots"), "*.intent.json"));
        Assert.Contains("interrupted", File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(_root, "screenshots"), "*.result.json"))));
    }

    [Fact]
    public void AReplyNamingAnExternalOrChangedFileIsRefusedWithoutTouchingIt()
    {
        string external = Path.Combine(_root, "personal.png");
        File.WriteAllBytes(external, [7, 8, 9]);
        using var actor = new ScriptedTransport().OnPrefix("cli_screenshot ", _ =>
            ScriptedTransport.Ok("OK: screenshot queued path=" + external + " size=1x1")).Actor();
        var result = OwnedScreenshot.Capture(actor, _root, "cli_screenshot bad", 1, TimeSpan.FromMilliseconds(100));
        Assert.False(result.Complete);
        Assert.Contains("different screenshot destination", result.Status);
        Assert.Equal([7, 8, 9], File.ReadAllBytes(external));
    }

    [Fact]
    public void ACommandFailureLeavesAnIncompletePrivateResult()
    {
        using var actor = new ScriptedTransport().OnPrefix("cli_screenshot ", _ =>
            throw new IOException("connection closed")).Actor();
        Assert.Throws<IOException>(() => OwnedScreenshot.Capture(actor, _root,
            "cli_screenshot failure", 1, TimeSpan.FromMilliseconds(100)));
        Assert.Contains("connection closed", File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(_root, "screenshots"), "*.result.json"))));
    }

    private static string CommandPath(string command)
    {
        int first = command.IndexOf('"');
        int last = command.LastIndexOf('"');
        Assert.True(first >= 0 && last > first);
        return command[(first + 1)..last];
    }
}
