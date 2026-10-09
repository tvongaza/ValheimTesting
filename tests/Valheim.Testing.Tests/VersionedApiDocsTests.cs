using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

public sealed class VersionedApiDocsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vt-api-docs-" + Guid.NewGuid().ToString("N"));
    public VersionedApiDocsTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void OnlyOneCoordinatedReleaseVersionGetsASnapshot()
    {
        string[] files = Enumerable.Range(0, 9).Select(i => Package(i, "0.1.0-rc.1")).ToArray();
        Assert.Equal("0.1.0-rc.1", VersionedApiDocs.PackageVersion(files));
        Package(0, "0.1.0-preview.1");
        Assert.Equal("", VersionedApiDocs.PackageVersion(files));
        Package(0, "0.1.1"); // Ordinary independently versioned patch release.
        foreach (string file in files.Skip(1)) Package(int.Parse(Path.GetFileName(file)[7..8]), "0.1.0");
        Assert.Equal("", VersionedApiDocs.PackageVersion(files));
        Assert.Contains("all nine", Assert.Throws<InvalidDataException>(() => VersionedApiDocs.PackageVersion(files[..8])).Message);
    }

    [Fact]
    public void ArchiveAndInstallRetainPagesButPinSourceLinks()
    {
        string site = Path.Combine(_root, "site"), published = Path.Combine(_root, "published");
        Directory.CreateDirectory(Path.Combine(site, "api"));
        File.WriteAllText(Path.Combine(site, "index.html"), "<title>ValheimTesting API reference (preview)</title><h1>current</h1>" +
            "This reference is generated from the current package sources.<a href=\"https://github.com/tvongaza/ValheimTesting/blob/main/docs/adopting.md\">adopt</a>");
        File.WriteAllText(Path.Combine(site, "api", "GameActor.html"), "<a href=\"https://github.com/tvongaza/ValheimTesting/tree/main/examples/FullLifecycle\">owned</a>");
        File.WriteAllBytes(Path.Combine(site, "api", "empty.css"), []);
        Directory.CreateDirectory(Path.Combine(site, "versions"));
        File.WriteAllText(Path.Combine(site, "versions", "index.html"), "not nested");
        string archive = Path.Combine(_root, "reference.tgz"), commit = new string('a', 40);
        VersionedApiDocs.MakeArchive(site, "0.1.0-rc.1", commit, archive);
        byte[] data = File.ReadAllBytes(archive);
        VersionedApiDocs.InstallArchive(data, VersionedApiDocs.Sha256(data), "0.1.0-rc.1", published);
        VersionedApiDocs.WriteIndex(published);
        string release = Path.Combine(published, "versions", "0.1.0-rc.1");
        Assert.Contains("tree/" + commit + "/examples/FullLifecycle", File.ReadAllText(Path.Combine(release, "api", "GameActor.html")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(release, "api", "empty.css")));
        string home = File.ReadAllText(Path.Combine(release, "index.html"));
        Assert.Contains("Release <strong>0.1.0-rc.1</strong>", home);
        Assert.Contains("blob/" + commit + "/docs/adopting.md", home);
        Assert.False(Directory.Exists(Path.Combine(release, "versions")));
        Assert.Contains("0.1.0-rc.1/", File.ReadAllText(Path.Combine(published, "versions", "index.html")));
        Assert.Contains("SHA-256 mismatch", Assert.Throws<InvalidDataException>(() =>
            VersionedApiDocs.InstallArchive(data, new string('0', 64), "0.1.0-rc.1", Path.Combine(_root, "bad"))).Message);
    }

    [Fact]
    public void UnsafeArchiveIsRejectedBeforeWritingAnything()
    {
        byte[] archive = Tar(("version.json", Encoding.UTF8.GetBytes("{\"version\":\"0.1.0\",\"sourceCommit\":\"" + new string('a', 40) + "\"}")),
            ("../outside.txt", Encoding.UTF8.GetBytes("bad")));
        Assert.Contains("unsafe member", Assert.Throws<InvalidDataException>(() =>
            VersionedApiDocs.InstallArchive(archive, VersionedApiDocs.Sha256(archive), "0.1.0", Path.Combine(_root, "site"))).Message);
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "site", "versions", "0.1.0")));
    }

    [Fact]
    public void ADocFxManifestNeverEntersTheRelease()
    {
        string site = Path.Combine(_root, "site");
        Directory.CreateDirectory(Path.Combine(site, "api"));
        File.WriteAllText(Path.Combine(site, "index.html"), "<h1>home</h1>");
        string manifest = Path.Combine(site, "manifest.json");
        File.WriteAllText(manifest, "private path");
        Assert.Contains("absolute-path manifest", Assert.Throws<InvalidDataException>(() =>
            VersionedApiDocs.MakeArchive(site, "0.1.0", new string('a', 40), Path.Combine(_root, "out.tgz"))).Message);
    }

    [UnixFact]
    public void SymlinksNeverEnterTheRelease()
    {
        string site = Path.Combine(_root, "site");
        Directory.CreateDirectory(Path.Combine(site, "api"));
        File.WriteAllText(Path.Combine(site, "index.html"), "<h1>home</h1>");
        File.CreateSymbolicLink(Path.Combine(site, "api", "link.html"), Path.Combine(site, "index.html"));
        Assert.Contains("symlink", Assert.Throws<InvalidDataException>(() =>
            VersionedApiDocs.MakeArchive(site, "0.1.0", new string('a', 40), Path.Combine(_root, "out.tgz"))).Message);
    }

    [Fact]
    public async Task DownloadRetriesServerErrorsWithoutSendingTokenToAssets()
    {
        var handler = new Replies(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        byte[] data = await VersionedApiDocs.Get(http, "https://example.test/asset", delay: _ => Task.CompletedTask);
        Assert.Equal("asset", Encoding.UTF8.GetString(data));
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Tokens, token => Assert.Null(token));
        var missing = new Replies(HttpStatusCode.NotFound);
        using var no = new HttpClient(missing);
        await Assert.ThrowsAsync<HttpRequestException>(() => VersionedApiDocs.Get(no, "https://example.test/asset", "secret"));
        Assert.Equal(1, missing.Calls);
    }

    private string Package(int index, string version)
    {
        string path = Path.Combine(_root, $"package{index}.nupkg");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var spec = new StreamWriter(zip.CreateEntry($"package{index}.nuspec").Open());
        spec.Write($"<package><metadata><id>Test</id><version>{version}</version></metadata></package>");
        return path;
    }

    private static byte[] Tar(params (string Name, byte[] Data)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new TarWriter(gzip))
            foreach (var (name, data) in entries)
            {
                using var payload = new MemoryStream(data);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = payload });
            }
        return bytes.ToArray();
    }

    private sealed class Replies(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string?> Tokens = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(codes[Calls++]) { Content = new ByteArrayContent("asset"u8.ToArray()) });
        }
    }
}
