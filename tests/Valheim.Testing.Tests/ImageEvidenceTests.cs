using System.Text.Json;
using System.Text.Json.Nodes;
using Valheim.Testing.Game;
using Xunit;

// The one owner of image evidence: the PNG check, the destination rule and the sidecar that both capture paths share.
public sealed class ImageEvidenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("image-evidence-").FullName;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9DwwAAAABJRU5ErkJggg==");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ACompletePngIsVerifiedWithItsDigestAndSize()
    {
        string path = Path.Combine(_root, "a.png");
        File.WriteAllBytes(path, Png);
        var verified = ImageEvidence.VerifyPng(path);
        Assert.NotNull(verified);
        Assert.Equal(FileHash.Sha256(path), verified!.Sha256);
        Assert.Equal(Png.Length, verified.Bytes);
    }

    [Fact]
    public void AnImageThatIsMissingTruncatedOrWronglyEndedIsNotVerified()
    {
        Assert.Null(ImageEvidence.VerifyPng(Path.Combine(_root, "missing.png")));
        string half = Path.Combine(_root, "half.png");
        File.WriteAllBytes(half, Png[..(Png.Length / 2)]);
        Assert.Null(ImageEvidence.VerifyPng(half));
        string notPng = Path.Combine(_root, "not.png");
        File.WriteAllBytes(notPng, new byte[64]);
        Assert.Null(ImageEvidence.VerifyPng(notPng));
        // The last chunk must be the whole IEND chunk, checksum included.
        string badCrc = Path.Combine(_root, "crc.png");
        byte[] wrong = (byte[])Png.Clone();
        wrong[^1] ^= 1;
        File.WriteAllBytes(badCrc, wrong);
        Assert.Null(ImageEvidence.VerifyPng(badCrc));
    }

    [Theory]
    [InlineData("OK: screenshot queued path=/a/b c/x.png size=1x1", "/a/b c/x.png")]
    [InlineData("OK: screenshot queued path=\"/a/b/x.png\" size=1x1", "/a/b/x.png")]
    [InlineData("OK: CAPTURE name=s path=C:\\pc\\x.png bytes=20 size=1280x720 ready_ms=3", "C:\\pc\\x.png")]
    [InlineData("OK: CAPTURE path=/x.png", "/x.png")]
    public void TheReportedPathIsTheTextBeforeTheNextField(string reply, string expected) =>
        Assert.Equal(expected, ImageEvidence.ReportedPath(reply));

    [Theory]
    [InlineData("OK: screenshot queued")]
    [InlineData("OK: CAPTURE path= bytes=20")]
    [InlineData("")]
    [InlineData(null)]
    public void AReplyThatNamesNoPathHasNone(string? reply) => Assert.Null(ImageEvidence.ReportedPath(reply));

    [Fact]
    public void ThePathRuleFollowsTheDestinationHostsCaseRule()
    {
        const string reply = "OK: CAPTURE name=s path=c:\\pcstage\\review\\s.png bytes=20 size=1x1 ready_ms=3";
        const string expected = "C:\\pcstage\\review\\s.png";
        Assert.True(ImageEvidence.NamesPath(reply, expected, ignoreCase: true, local: false));
        Assert.False(ImageEvidence.NamesPath(reply, expected, ignoreCase: false, local: false));
        Assert.False(ImageEvidence.NamesPath(reply.Replace("review", "other"), expected, ignoreCase: true, local: false));
        Assert.False(ImageEvidence.NamesPath("OK: CAPTURE ready", expected, ignoreCase: true, local: false));
    }

    [Fact]
    public void ALocalPathIsComparedFullyQualified()
    {
        string expected = Path.Combine(_root, "screenshots", "x.png");
        string indirect = Path.Combine(_root, "screenshots", "..", "screenshots", "x.png");
        Assert.True(ImageEvidence.NamesPath("OK: screenshot queued path=" + indirect + " size=1x1", expected,
            ImageEvidence.LocalIgnoresCase, local: true));
        Assert.False(ImageEvidence.NamesPath("OK: screenshot queued path=" + Path.Combine(_root, "other.png") + " size=1x1", expected,
            ImageEvidence.LocalIgnoresCase, local: true));
    }

    [Fact]
    public void TheSidecarHasTheCommonFieldsInOrderAndNeverReplacesAnExistingFile()
    {
        string path = Path.Combine(_root, "s.json");
        string sha = ImageEvidence.WriteSidecar(path, "unit-image", new JsonObject { ["image"] = "x.png", ["sha256"] = "abc", ["bytes"] = 3 });
        Assert.Equal(FileHash.Sha256(path), sha);
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal(["schema", "kind", "image", "sha256", "bytes", "recordedUtc"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("unit-image", root.GetProperty("kind").GetString());
        Assert.Throws<IOException>(() => ImageEvidence.WriteSidecar(path, "unit-image", new JsonObject()));
    }
}
