// Immutable 0.1 API snapshots: build them from a release tag, then install verified
// published archives alongside the moving Pages reference. No Python dependency.
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

#if !VERSIONED_API_DOCS_TESTS
return await VersionedApiDocs.Run(args);
#endif

internal static class VersionedApiDocs
{
    private static readonly Regex Version = new(@"^0\.1\.0(?:-rc\.[1-9][0-9]*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex Commit = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex Asset = new(@"^api-reference-(0\.1\.0(?:-rc\.[1-9][0-9]*)?)\.tgz$", RegexOptions.CultureInvariant);
    private const string Home = "https://github.com/tvongaza/ValheimTesting/";

    public static async Task<int> Run(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Usage: dotnet run scripts/versioned-api-docs.cs -- version PACKAGES... | archive SITE VERSION COMMIT OUTPUT | install ARCHIVE SHA256 VERSION SITE | assemble SITE");
        switch (args[0])
        {
            case "version" when args.Length > 1:
                Console.WriteLine(PackageVersion(args[1..]));
                break;
            case "archive" when args.Length == 5:
                MakeArchive(args[1], args[2], args[3], args[4]);
                Console.WriteLine($"Archived {args[2]}: {args[4]} ({Sha256(File.ReadAllBytes(args[4]))})");
                break;
            case "install" when args.Length == 5:
                InstallArchive(File.ReadAllBytes(args[1]), args[2], args[3], args[4]);
                WriteIndex(args[4]);
                break;
            case "assemble" when args.Length == 2:
                string repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? throw new ArgumentException("GITHUB_REPOSITORY is required");
                string token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? throw new ArgumentException("GH_TOKEN is required");
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    await foreach (var archive in PublishedArchives(http, repository, token))
                    {
                        if (!seen.Add(archive.Version)) throw new InvalidDataException($"More than one published release contains API reference {archive.Version}");
                        InstallArchive(archive.Data, archive.Digest, archive.Version, args[1]);
                    }
                    WriteIndex(args[1]);
                    Console.WriteLine("Versioned API references: " + (seen.Count == 0 ? "none yet" : string.Join(", ", seen.Order(StringComparer.Ordinal))));
                }
                break;
            default: throw new ArgumentException("Unknown versioned-api-docs command or argument count: " + args[0]);
        }
        return 0;
    }

    public static string PackageVersion(IReadOnlyList<string> files)
    {
        if (files.Count != 9) throw new InvalidDataException($"Expected all nine release packages, found {files.Count}");
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            using var zip = ZipFile.OpenRead(file);
            var specs = zip.Entries.Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).ToArray();
            if (specs.Length != 1) throw new InvalidDataException($"{file}: expected exactly one nuspec");
            using var stream = specs[0].Open();
            var metadata = XDocument.Load(stream).Descendants().SingleOrDefault(e => e.Name.LocalName == "metadata");
            string? version = metadata?.Elements().SingleOrDefault(e => e.Name.LocalName == "version")?.Value;
            if (string.IsNullOrEmpty(version)) throw new InvalidDataException($"{file}: missing nuspec version");
            versions.Add(version);
        }
        if (versions.Count != 1)
        {
            Console.Error.WriteLine("No coordinated 0.1 snapshot: package versions differ (" + string.Join(", ", versions.Order(StringComparer.Ordinal)) + ").");
            return "";
        }
        string only = versions.Single();
        return Version.IsMatch(only) ? only : "";
    }

    public static void MakeArchive(string site, string version, string commit, string output)
    {
        if (!Version.IsMatch(version) || !Commit.IsMatch(commit)) throw new InvalidDataException("An archive needs a coordinated 0.1 version and full source commit");
        if (!File.Exists(Path.Combine(site, "index.html")) || !Directory.Exists(Path.Combine(site, "api")))
            throw new InvalidDataException("Build the DocFX site before archiving it");
        if (File.Exists(Path.Combine(site, "manifest.json"))) throw new InvalidDataException("DocFX's absolute-path manifest must not enter a release archive");
        string[] files = SiteFiles(site).Order(StringComparer.Ordinal).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var destination = File.Create(output);
        using var gzip = new GZipStream(destination, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, leaveOpen: false);
        Write("version.json", Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"sourceCommit\":\"{commit}\"}}"));
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(site, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == "version.json" || relative.StartsWith("versions/", StringComparison.Ordinal)) continue;
            byte[] contents = File.ReadAllBytes(file);
            if (relative.EndsWith(".html", StringComparison.Ordinal))
            {
                string html = Encoding.UTF8.GetString(contents);
                foreach (string kind in new[] { "blob", "tree" })
                    html = html.Replace(Home + kind + "/main/", Home + kind + "/" + commit + "/", StringComparison.Ordinal);
                if (relative == "index.html")
                {
                    html = html.Replace("ValheimTesting API reference (preview)", $"ValheimTesting API reference ({version})", StringComparison.Ordinal)
                        .Replace("This reference is generated from the current package sources.", $"This reference is generated from release {version}'s package sources.", StringComparison.Ordinal);
                    if (!html.Contains("</h1>", StringComparison.Ordinal)) throw new InvalidDataException("DocFX's API home has no heading for the release banner");
                    string banner = $"<p>Release <strong>{version}</strong>, built from source <code>{commit[..12]}</code>. This reference is immutable; <a href=\"../../\">the unversioned site</a> follows current source.</p>";
                    int end = html.IndexOf("</h1>", StringComparison.Ordinal) + "</h1>".Length;
                    html = html.Insert(end, banner);
                }
                contents = Encoding.UTF8.GetBytes(html);
            }
            Write(relative, contents);
        }
        void Write(string name, byte[] contents)
        {
            using var payload = new MemoryStream(contents, writable: false);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = payload, ModificationTime = DateTimeOffset.UnixEpoch };
            writer.WriteEntry(entry);
        }
    }

    private static IEnumerable<string> SiteFiles(string folder)
    {
        foreach (FileSystemInfo item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (item.LinkTarget != null) throw new InvalidDataException("Site contains a symlink: " + item.FullName);
            if (item is DirectoryInfo directory)
            {
                foreach (string nested in SiteFiles(directory.FullName)) yield return nested;
            }
            else if (item is FileInfo file) yield return file.FullName;
        }
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static void InstallArchive(byte[] data, string expectedSha256, string version, string site)
    {
        if (!Version.IsMatch(version) || Sha256(data) != expectedSha256.ToLowerInvariant())
            throw new InvalidDataException($"API archive {version}: version or SHA-256 mismatch");
        string destination = Path.Combine(site, "versions", version);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new InvalidDataException("Duplicate API reference version: " + version);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var compressed = new MemoryStream(data, writable: false))
        using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            TarEntry? entry;
            long total = 0;
            while ((entry = reader.GetNextEntry(copyData: false)) != null)
            {
                string name = entry.Name;
                if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
                    name.Split('/').Any(part => part is "" or "." or "..") ||
                    entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException($"API archive {version}: unsafe member {name}");
                if (entry.Length > 100_000_000 || (total += entry.Length) > 250_000_000)
                    throw new InvalidDataException($"API archive {version}: oversized member {name}");
                using var contents = new MemoryStream();
                if (entry.DataStream is { } stream) stream.CopyTo(contents);
                else if (entry.Length != 0) throw new InvalidDataException($"API archive {version}: missing data for {name}");
                if (!files.TryAdd(name, contents.ToArray())) throw new InvalidDataException($"API archive {version}: unsafe duplicate member {name}");
            }
        }
        if (!files.TryGetValue("version.json", out byte[]? metadata)) throw new InvalidDataException($"API archive {version}: missing version metadata");
        using (var doc = JsonDocument.Parse(metadata))
        {
            var root = doc.RootElement;
            if (root.GetProperty("version").GetString() != version || !Commit.IsMatch(root.GetProperty("sourceCommit").GetString() ?? ""))
                throw new InvalidDataException($"API archive {version}: invalid version metadata");
        }
        if (!files.ContainsKey("index.html") || !files.Keys.Any(name => name.StartsWith("api/", StringComparison.Ordinal)))
            throw new InvalidDataException($"API archive {version}: missing reference pages");
        string versions = Path.Combine(site, "versions");
        Directory.CreateDirectory(versions);
        string staging = Path.Combine(versions, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, contents) in files)
            {
                string target = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, contents);
            }
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    public static void WriteIndex(string site)
    {
        string folder = Path.Combine(site, "versions");
        Directory.CreateDirectory(folder);
        string[] versions = Directory.GetDirectories(folder).Select(Path.GetFileName).OfType<string>()
            .Where(v => Version.IsMatch(v)).OrderByDescending(v => v == "0.1.0").ThenByDescending(v => v.Contains("-rc.") ? int.Parse(v[(v.LastIndexOf('.') + 1)..]) : 0).ToArray();
        string links = string.Join("\n", versions.Select(v => $"<li><a href=\"{v}/\">{v}</a></li>"));
        File.WriteAllText(Path.Combine(folder, "index.html"),
            "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Versioned ValheimTesting API references</title>" +
            "<h1>Versioned ValheimTesting API references</h1><p><a href=\"../\">Current reference</a></p>" +
            (versions.Length > 0 ? $"<ul>{links}</ul>" : "<p>No 0.1 release candidate has been published yet.</p>") + "</html>\n");
    }

    public static async Task<byte[]> Get(HttpClient http, string url, string? token = null, Func<TimeSpan, Task>? delay = null)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ValheimTesting-versioned-api-docs");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                using HttpResponseMessage response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt == 3 || response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                        response.EnsureSuccessStatusCode();
                }
                else return await response.Content.ReadAsByteArrayAsync();
            }
            catch (HttpRequestException error) when (attempt < 3 && error.StatusCode is null) { }
            catch (TaskCanceledException) when (attempt < 3) { }
            if (attempt == 3) throw new HttpRequestException("API archive download failed after retries: " + url);
            await (delay ?? Task.Delay)(TimeSpan.FromSeconds(1 << attempt));
        }
        throw new InvalidOperationException("Unreachable download retry state");
    }

    public static async IAsyncEnumerable<(string Version, byte[] Data, string Digest)> PublishedArchives(HttpClient http, string repository, string token)
    {
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new ArgumentException("Expected GITHUB_REPOSITORY=owner/repo");
        for (int page = 1; ; page++)
        {
            using var releases = JsonDocument.Parse(await Get(http, $"https://api.github.com/repos/{repository}/releases?per_page=100&page={page}", token));
            if (releases.RootElement.GetArrayLength() == 0) yield break;
            foreach (var release in releases.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean()) continue;
                var assets = release.GetProperty("assets").EnumerateArray().ToDictionary(asset => asset.GetProperty("name").GetString()!, StringComparer.Ordinal);
                foreach (var (name, asset) in assets)
                {
                    var match = Asset.Match(name);
                    if (!match.Success) continue;
                    if (!assets.TryGetValue("SHA256SUMS", out JsonElement sums)) throw new InvalidDataException($"{release.GetProperty("tag_name").GetString()}: reference archive lacks SHA256SUMS");
                    string lines = Encoding.UTF8.GetString(await Get(http, sums.GetProperty("browser_download_url").GetString()!));
                    string[] digests = lines.Split('\n').Where(line => line.TrimEnd('\r').EndsWith("  " + name, StringComparison.Ordinal))
                        .Select(line => line.Split(' ', 2)[0]).ToArray();
                    if (digests.Length != 1 || !Regex.IsMatch(digests[0], "^[0-9a-fA-F]{64}$"))
                        throw new InvalidDataException($"{release.GetProperty("tag_name").GetString()}: no unique SHA-256 for {name}");
                    yield return (match.Groups[1].Value, await Get(http, asset.GetProperty("browser_download_url").GetString()!), digests[0]);
                }
            }
        }
    }
}
