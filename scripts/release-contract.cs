// Write the exact transport contract of a ValheimTesting release. The package hashes are of NuGet.org's
// published bytes, not of a local rebuild (which can have a different module GUID under the same version).
//
//   dotnet run scripts/release-contract.cs -- --output contract.json
//
// Run after this checkout's versions are published and the NuGet.org-only consumer passes.
#:project ../src/Valheim.Testing.Game/Valheim.Testing.Game.csproj
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Valheim.Testing.Game;

string root = FindRoot();
string? output = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
    else return Usage("unknown or invalid argument: " + args[i]);
}
if (output == null) return Usage("--output is required");

string[] packageIds = Directory.GetDirectories(Path.Combine(root, "src"), "Valheim.Testing*", SearchOption.TopDirectoryOnly)
    .Select(Path.GetFileName).OfType<string>()
    .Where(id => File.Exists(Path.Combine(root, "src", id, id + ".csproj")))
    .Append("Valheim.Testing.Cli").Order(StringComparer.Ordinal).ToArray();
using var cli = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cli-dependency.json")));
using var loader = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "loader-dependency.json")));
string Cli(string name) => cli.RootElement.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing CLI " + name);
string Loader(string name) => loader.RootElement.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing loader " + name);
string forkCommit = Cli("commit"), cliVersion = Cli("packageVersion");
string sourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "";
if (!Regex.IsMatch(sourceCommit, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase))
    throw new InvalidDataException("GITHUB_SHA must name the exact 40-character release source commit.");
if (!Regex.IsMatch(forkCommit, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase))
    throw new InvalidDataException("cli-dependency.json must pin a full ValheimCLI fork commit.");
string bundleHash = cli.RootElement.GetProperty("bundle").GetProperty("sha256").GetString() ?? "";
if (!Regex.IsMatch(bundleHash, "^[0-9a-f]{64}$", RegexOptions.IgnoreCase) || !Regex.IsMatch(Loader("sha256"), "^[0-9a-f]{64}$", RegexOptions.IgnoreCase))
    throw new InvalidDataException("The ValheimCLI bundle and BepInEx loader must each have a SHA-256 pin.");
var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (string id in packageIds.Where(id => id != "Valheim.Testing.Cli"))
{
    string project = Path.Combine(root, "src", id, id + ".csproj");
    versions[id] = XDocument.Load(project).Descendants("Version").Single().Value;
}
versions["Valheim.Testing.Cli"] = cliVersion;
if (!packageIds.SequenceEqual(versions.Keys)) throw new InvalidDataException("Package versions must name every packable source project and the pinned CLI transport.");
if (versions.Values.Any(version => version.Contains("-candidate", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidDataException("A release contract cannot name candidate packages.");

var packages = new List<(string Id, string Version, string Sha256)>();
var nuspecs = new Dictionary<string, XDocument>(StringComparer.Ordinal);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
foreach ((string id, string version) in versions)
{
    string lower = id.ToLowerInvariant(), v = version.ToLowerInvariant();
    string url = $"https://api.nuget.org/v3-flatcontainer/{lower}/{v}/{lower}.{v}.nupkg";
    byte[] bytes = await GetBytes(http, url);
    if (bytes.Length == 0) throw new InvalidDataException($"NuGet.org served an empty {id} {version} package.");
    string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
    using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    var entry = zip.Entries.SingleOrDefault(file => file.FullName.Equals(id + ".nuspec", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"{id} {version} has no package nuspec.");
    using var nuspecStream = entry.Open();
    XDocument spec = XDocument.Load(nuspecStream);
    string Name(XElement element) => element.Name.LocalName;
    var metadata = spec.Descendants().Single(element => Name(element) == "metadata");
    string Packed(string field) => metadata.Elements().Single(element => Name(element) == field).Value;
    if (Packed("id") != id || Packed("version") != version)
        throw new InvalidDataException($"{id} {version} does not match its published nuspec identity.");
    nuspecs[id] = spec;
    packages.Add((id, version, hash));
}

// A local transport rebuild can have different bytes. Its published nuspec is the authority for the fork source.
var cliRepository = nuspecs["Valheim.Testing.Cli"].Descendants().SingleOrDefault(element => element.Name.LocalName == "repository")
    ?? throw new InvalidDataException("The published Valheim.Testing.Cli package has no repository metadata.");
string publishedForkCommit = cliRepository.Attribute("commit")?.Value ?? "";
if (!publishedForkCommit.Equals(forkCommit, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException($"The published Valheim.Testing.Cli {versions["Valheim.Testing.Cli"]} was built from {publishedForkCommit}, not {forkCommit}.");

static void RequireDependency(XDocument spec, string owner, string dependency, string version)
{
    var found = spec.Descendants().Where(element => element.Name.LocalName == "dependency"
        && (string?)element.Attribute("id") == dependency).ToList();
    if (found.Count != 1 || (string?)found[0].Attribute("version") != "[" + version + "]")
        throw new InvalidDataException($"Published {owner} must depend on exactly {dependency} [{version}].");
}
RequireDependency(nuspecs["Valheim.Testing.Game"], "Valheim.Testing.Game", "Valheim.Testing.Cli", versions["Valheim.Testing.Cli"]);
RequireDependency(nuspecs["Valheim.Testing.GameSessions"], "Valheim.Testing.GameSessions", "Valheim.Testing.Game", versions["Valheim.Testing.Game"]);

string[] requiredPacks = CliCapabilities.Toolkit.ToArray();
// The manifest describes the bundle a disposable game will actually load. A matching transport commit alone would
// not prove that its Standard and World Tools packs offer the commands the toolkit expects.
{
    byte[] bytes = await GetBytes(http, cli.RootElement.GetProperty("bundle").GetProperty("url").GetString()!);
    if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(bundleHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The ValheimCLI plugin bundle differs from cli-dependency.json's SHA-256 pin.");
    using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    using var listing = zip.GetEntry("cli-manifest.json")?.Open() ?? throw new InvalidDataException("The ValheimCLI bundle has no capability manifest.");
    using var manifest = JsonDocument.Parse(listing);
    if (manifest.RootElement.GetProperty("build").GetString() != forkCommit[..7])
        throw new InvalidDataException("The ValheimCLI bundle names another fork build.");
    var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
    if (!files.Any(file => file.GetProperty("file").GetString() == "valheimCLI.dll" &&
            file.GetProperty("plugins").EnumerateArray().Any(plugin => plugin.GetString() == "valheimCLI.valheimCLI")))
        throw new InvalidDataException("The ValheimCLI bundle has no core plugin.");
    foreach (var file in files)
    {
        string name = file.GetProperty("file").GetString() ?? "";
        using var member = zip.GetEntry(name)?.Open() ?? throw new InvalidDataException("The ValheimCLI bundle is missing " + name);
        if (!Convert.ToHexStringLower(SHA256.HashData(member)).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The ValheimCLI bundle's " + name + " differs from its manifest.");
    }
    foreach (string capability in requiredPacks)
    {
        string[] parts = capability.Split('/');
        if (!files.Any(file => file.GetProperty("extensions").TryGetProperty(parts[0], out var owner)
                && owner.TryGetProperty(parts[1], out var schema) && schema.GetInt32() == 1))
            throw new InvalidDataException("The pinned ValheimCLI bundle lacks required schema-1 capability " + capability);
    }
}
string destination = Path.GetFullPath(output);
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
using (var stream = File.Create(destination))
using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    json.WriteStartObject();
    json.WriteNumber("schema", 1);
    json.WriteString("sourceCommit", sourceCommit);
    json.WriteString("packageHashOf", "NuGet.org repository-signed .nupkg bytes; SHA256SUMS covers the unsigned files attached to the GitHub release");
    json.WriteStartObject("fork");
    json.WriteString("repository", Cli("repository"));
    json.WriteString("commit", forkCommit);
    json.WriteString("transportPackage", "Valheim.Testing.Cli");
    json.WriteString("transportVersion", versions["Valheim.Testing.Cli"]);
    json.WriteString("bundleSha256", bundleHash);
    json.WriteEndObject();
    json.WriteStartObject("loader");
    json.WriteString("package", Loader("name"));
    json.WriteString("version", Loader("version"));
    json.WriteString("sha256", Loader("sha256"));
    json.WriteEndObject();
    json.WriteStartObject("requiredCli");
    json.WriteStartArray("coreCommands");
    foreach (string command in new[] { "cli_expect", "cli_extensions" }) json.WriteStringValue(command);
    json.WriteEndArray();
    json.WriteStartArray("packCapabilities");
    foreach (string capability in requiredPacks) json.WriteStringValue(capability);
    json.WriteEndArray();
    json.WriteEndObject();
    json.WriteStartObject("compatiblePackages");
    json.WriteString("game", versions["Valheim.Testing.Game"]);
    json.WriteString("gameSessions", versions["Valheim.Testing.GameSessions"]);
    json.WriteString("nativeSmoke", versions["Valheim.Testing.NativeSmoke"]);
    json.WriteEndObject();
    json.WriteStartArray("packages");
    foreach (var package in packages)
    {
        json.WriteStartObject();
        json.WriteString("id", package.Id);
        json.WriteString("version", package.Version);
        json.WriteString("sha256", package.Sha256);
        json.WriteEndObject();
    }
    json.WriteEndArray();
    json.WriteEndObject();
}
Console.WriteLine($"Release contract: {destination} ({packages.Count} published packages; ValheimCLI {forkCommit[..7]}).");
return 0;

static int Usage(string error)
{
    Console.Error.WriteLine("release-contract: " + error + ". Usage: dotnet run scripts/release-contract.cs -- --output FILE");
    return 2;
}

// Retry only transient transport failures. The consumer has already proved that every exact version is served;
// a persistent 404 is a bad release input and must not be disguised as a slow package-index update.
static async Task<byte[]> GetBytes(HttpClient http, string url)
{
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            using var response = await http.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                response.EnsureSuccessStatusCode();
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException($"Release contract could not fetch {url}: HTTP {(int)response.StatusCode}.");
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception error) when (attempt < 4 && error is HttpRequestException or TaskCanceledException)
        {
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
        }
    }
}

static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(ScriptPath()) ?? "" })
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside ValheimTesting.");
}
static string ScriptPath([CallerFilePath] string path = "") => path;
