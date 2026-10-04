// The released package versions, and every copyable place that names one. Never launches Valheim. The fresh consumer of a
// release is scripts/consumer.cs --feed nuget.
//
//   dotnet run scripts/pins.cs -- write      after a release: rewrite every pin from toolkit-versions.json
//   dotnet run scripts/pins.cs -- check      CI: the pins, toolkit-versions.json and NuGet.org agree
//   dotnet run scripts/pins.cs -- versions   release.yml, before building
//
// toolkit-versions.json is the one record of what is released: the newest version NuGet.org lists of each package, and the
// valheimCLI commit the released Valheim.Testing.Cli was built from. Documentation and examples pin exactly these versions
// and follow every release: once a release is published, edit the file and run write; nothing else is edited by hand.
//
// Pins are the copyable versions in the README, CONTRIBUTING, docs, examples, tools and the packages' READMEs: a
// PackageReference, a `dotnet tool install` or `dotnet add package` command, ToolkitPackageVersion (the Game package; a -p:
// argument or a project property), and a backticked package ID followed by a backticked version, as in the package table. History in prose ("new in Game
// preview 11") is not a pin. Dated native-validation records are not scanned. The NativeSmoke tool reads the released Game
// version from the file itself (embedded at build).
//
// check reads NuGet.org and fails, naming the file and line, when: a pin differs from the file; a released version is not
// served, is a candidate, or is superseded by a newer version NuGet.org lists (the docs lag a release; an unlisted
// version, such as a release withdrawn as broken, supersedes nothing); the released Cli was not built from
// releasedCliCommit; a released package's Valheim.Testing* dependency is not met by the released set (so a page pinning
// both Doubles and Valheim.Testing names a pair that restores together); or a project's source version is older than its
// release. write and versions read only this checkout.
//
// versions refuses a release whose manifest or packed dependencies name a `-candidate.<sha>` version: a candidate is a
// local build identity, and NuGet.org never lets a published id/version be replaced. Checked are each packed project's
// <Version> and Valheim.Testing* PackageReferences, and the Cli packageVersion in cli-dependency.json. It also refuses a
// source version older than the release recorded in toolkit-versions.json. A candidate may sit on main between releases.
#:package NuGet.Versioning@7.9.0
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Versioning;

const string FlatContainer = "https://api.nuget.org/v3-flatcontainer";
// NuGet's v3 service index advertises this semver2 registration base; it says which versions are listed.
const string Registration = "https://api.nuget.org/v3/registration5-gz-semver2";
const string VersionsFile = "toolkit-versions.json";
const string Cli = "Valheim.Testing.Cli";
string[] packed = ["Valheim.Testing", "Valheim.Testing.Game", "Valheim.Testing.Doubles", "Valheim.Testing.Adapter", "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool", "Valheim.Testing.NativeSmoke"];

string root = FindRoot();
bool actions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

string mode = args.Length > 0 ? args[0] : "";
if (args.Length > 1) return Usage($"unknown argument '{args[1]}'");
return mode switch
{
    "write" => Write(),
    "check" => await Check(),
    "versions" => Versions(),
    _ => Usage(mode == "" ? "no mode" : $"unknown mode '{mode}'"),
};

int Usage(string problem)
{
    Console.Error.WriteLine($"pins: {problem}. Usage: dotnet run scripts/pins.cs -- (write | check | versions)");
    return 2;
}

int Write()
{
    var released = ReadReleased();
    var pins = FindPins();
    var unknown = pins.Where(p => !released.ContainsKey(p.Id)).ToList();
    if (unknown.Count > 0) return Report(unknown.Select(p => new Problem(p.File, p.Line, $"{p.Id} has no released version in {VersionsFile}.")).ToList(), "");
    int changed = 0;
    foreach (var file in pins.Where(p => !string.Equals(p.Version, released[p.Id], StringComparison.Ordinal)).GroupBy(p => p.File))
    {
        string path = Path.Combine(root, file.Key);
        string text = ReadText(path, out Encoding encoding);
        // From the end, so each earlier index still holds.
        foreach (Pin pin in file.OrderByDescending(p => p.Index))
            text = text[..pin.Index] + released[pin.Id] + text[(pin.Index + pin.Version.Length)..];
        File.WriteAllText(path, text, encoding);
        foreach (Pin pin in file)
        {
            Console.WriteLine($"{pin.File}:{pin.Line} {pin.Id} {pin.Version} -> {released[pin.Id]}");
            changed++;
        }
    }
    Console.WriteLine(changed == 0 ? $"All {pins.Count} pins already name the versions in {VersionsFile}." : $"Rewrote {changed} of {pins.Count} pins from {VersionsFile}.");
    return 0;
}

async Task<int> Check()
{
    var released = ReadReleased();
    var problems = new List<Problem>();
    using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };

    foreach (string id in packed.Append(Cli).Where(id => !released.ContainsKey(id)))
        problems.Add(new(VersionsFile, 1, $"{id} has no released version."));
    // One package's requests do not wait for another's.
    foreach (var found in await Task.WhenAll(released.Select(p => CheckReleased(http, released, p.Key, p.Value))))
        problems.AddRange(found);
    var stated = SourceVersions();
    problems.AddRange(SourceOlderThanReleased(stated, released));

    var pins = FindPins();
    if (pins.Count == 0) problems.Add(new(VersionsFile, 1, "No version pins found; the scan no longer matches the docs."));
    foreach (Pin pin in pins)
    {
        string? problem = !released.TryGetValue(pin.Id, out string? version) ? $"{pin.Id} has no released version in {VersionsFile}."
            : !string.Equals(pin.Version, version, StringComparison.Ordinal)
                ? $"{pin.Id} {pin.Version} differs from {VersionsFile} ({version}). Edit only {VersionsFile}, then run `dotnet run scripts/pins.cs -- write`."
            : null;
        Console.WriteLine($"{(problem == null ? "ok  " : "FAIL")} {pin.File}:{pin.Line} {pin.Id} {pin.Version}");
        if (problem != null) problems.Add(new(pin.File, pin.Line, problem));
    }
    return Report(problems, $"All {pins.Count} pins name the released versions in {VersionsFile}, the newest NuGet.org lists.");
}

// One recorded release against NuGet.org: served, the newest listed version (an unlisted one, such as a release withdrawn
// as broken, does not supersede it), built from the recorded Cli commit, and its Valheim.Testing* dependencies met.
async Task<List<Problem>> CheckReleased(HttpClient http, Dictionary<string, string> released, string id, string version)
{
    var problems = new List<Problem>();
    int line = FileLine(VersionsFile, $"\"{id}\"");
    if (!packed.Append(Cli).Contains(id)) return [new(VersionsFile, line, $"{id} is not a package this repository publishes.")];
    if (IsCandidate(version) || !NuGetVersion.TryParse(version, out NuGetVersion? recorded))
        return [new(VersionsFile, line, $"{id} {version} is a candidate or not a version; only a published version is recorded.")];
    string lower = id.ToLowerInvariant(), v = recorded.ToNormalizedString().ToLowerInvariant();
    if (await Get(http, $"{FlatContainer}/{lower}/{v}/{lower}.nuspec") is not { } nuspecText)
        return [new(VersionsFile, line, $"{id} {version} is not served by NuGet.org.")];

    List<NuGetVersion> listed = await Listed(http, lower);
    if (!listed.Contains(recorded))
        problems.Add(new(VersionsFile, line, $"{id} {version} is not listed on NuGet.org: withdrawn, or its registration is not updated yet."));
    NuGetVersion? newest = listed.Where(l => !IsCandidate(l.ToString())).Max();
    if (newest != null && newest > recorded)
        problems.Add(new(VersionsFile, line, $"{id} {version} is superseded: NuGet.org lists {newest}. Record the release here, then run `dotnet run scripts/pins.cs -- write`."));

    XDocument nuspec = XDocument.Parse(nuspecText);
    if (id == Cli)
    {
        string? commit = nuspec.Descendants().FirstOrDefault(e => e.Name.LocalName == "repository")?.Attribute("commit")?.Value;
        string expected = ReleasedCliCommit();
        if (!string.Equals(commit, expected, StringComparison.OrdinalIgnoreCase))
            problems.Add(new(VersionsFile, FileLine(VersionsFile, "\"releasedCliCommit\""), $"{id} {version} was built from valheimCLI {commit ?? "(no commit recorded)"}, not releasedCliCommit {expected}."));
    }
    // The pairing: a released package's own Valheim.Testing* dependencies must accept the released versions, so pins that
    // equal this file restore together (Doubles needs its Valheim.Testing, Game its Cli and Valheim.Testing).
    foreach (var dependency in nuspec.Descendants().Where(e => e.Name.LocalName == "dependency")
                 .Select(e => (Id: e.Attribute("id")?.Value ?? "", Range: e.Attribute("version")?.Value ?? ""))
                 .Where(d => d.Id.StartsWith("Valheim.Testing", StringComparison.OrdinalIgnoreCase)).Distinct())
    {
        if (!released.TryGetValue(dependency.Id, out string? pinned))
            problems.Add(new(VersionsFile, line, $"{id} {version} depends on {dependency.Id}, which has no released version here."));
        else if (!VersionRange.TryParse(dependency.Range, out VersionRange? range) || !NuGetVersion.TryParse(pinned, out NuGetVersion? pinnedVersion) || !range.Satisfies(pinnedVersion))
            problems.Add(new(VersionsFile, line, $"{id} {version} depends on {dependency.Id} {dependency.Range}, but the released {dependency.Id} is {pinned}: a page pinning both would restore a mismatched pair."));
    }
    return problems;
}

// The versions NuGet.org lists for a package (registration pages are inline for small packages, fetched otherwise).
async Task<List<NuGetVersion>> Listed(HttpClient http, string lower)
{
    var listed = new List<NuGetVersion>();
    if (await Get(http, $"{Registration}/{lower}/index.json") is not { } index) return listed;
    using JsonDocument doc = JsonDocument.Parse(index);
    foreach (JsonElement page in doc.RootElement.GetProperty("items").EnumerateArray())
    {
        JsonElement items;
        using JsonDocument? fetched = page.TryGetProperty("items", out items) ? null
            : JsonDocument.Parse(await Get(http, page.GetProperty("@id").GetString()!) ?? throw new InvalidOperationException("A registration page is missing."));
        if (fetched != null) items = fetched.RootElement.GetProperty("items");
        foreach (JsonElement leaf in items.EnumerateArray())
        {
            JsonElement entry = leaf.GetProperty("catalogEntry");
            if (entry.TryGetProperty("listed", out JsonElement l) && l.ValueKind == JsonValueKind.False) continue;
            if (NuGetVersion.TryParse(entry.GetProperty("version").GetString(), out NuGetVersion? version)) listed.Add(version);
        }
    }
    return listed;
}

int Versions()
{
    // Every version a release would publish or depend on: each packed project's <Version> and Valheim.Testing*
    // references (attributes in any order), and the Cli pin. A version set through an MSBuild property cannot be read
    // here, so it is refused rather than passed.
    var stated = SourceVersions();
    var problems = new List<Problem>();
    foreach (Pin pin in stated.Where(p => IsCandidate(p.Version) || p.Version.Contains("$(")))
        problems.Add(new(pin.File, pin.Line, pin.Version.Contains("$(")
            ? $"sets {pin.Id}'s version through a property ({pin.Version}), which this check cannot read; write the exact version."
            : $"names the candidate version {pin.Version}. A candidate is a local build identity and is never released; " +
              "move it to a version NuGet.org has never served before tagging."));
    problems.AddRange(SourceOlderThanReleased(stated, ReadReleased()));
    return Report(problems, $"No candidate version in the {stated.Count} release versions and dependencies, and none older than {VersionsFile}.");
}

// Each packed project's <Version> and its Valheim.Testing* references, and the Cli packageVersion. Declares marks the
// package's own version, as opposed to a dependency on another.
List<Pin> SourceVersions()
{
    var stated = new List<Pin>();
    var element = new Regex(@"<Version>(?<version>[^<]+)</Version>|<PackageReference\b[^>]*>|""packageVersion"":\s*""(?<version>[^""]+)""");
    var include = new Regex(@"\bInclude=""(?<id>Valheim\.Testing[\w.]*)""");
    var reference = new Regex(@"\b(?:Version|VersionOverride)=""\[?(?<version>[^\]""]+)\]?""");
    foreach (var (id, file) in packed.Select(id => (id, ProjectFile(id))).Append((Cli, "cli-dependency.json")))
    {
        string[] lines = File.ReadAllLines(Path.Combine(root, file));
        for (int i = 0; i < lines.Length; i++)
            foreach (Match m in element.Matches(lines[i]))
            {
                if (m.Groups["version"].Success) { stated.Add(new Pin(file, i + 1, id, m.Groups["version"].Value, Declares: true)); continue; }
                if (include.Match(m.Value) is not { Success: true } dependency) continue;
                foreach (Match v in reference.Matches(m.Value)) stated.Add(new Pin(file, i + 1, dependency.Groups["id"].Value, v.Groups["version"].Value));
            }
    }
    return stated;
}

// A source version older than its release would go backwards; one equal to it is simply not republished.
IEnumerable<Problem> SourceOlderThanReleased(List<Pin> stated, Dictionary<string, string> released) =>
    stated.Where(p => p.Declares && released.TryGetValue(p.Id, out string? r)
            && NuGetVersion.TryParse(p.Version, out NuGetVersion? source) && NuGetVersion.TryParse(r, out NuGetVersion? release) && source < release)
        .Select(p => new Problem(p.File, p.Line, $"{p.Id} {p.Version} in source is older than the release {released[p.Id]} recorded in {VersionsFile}."));

int Report(List<Problem> problems, string success)
{
    foreach (Problem problem in problems)
    {
        Console.Error.WriteLine($"FAIL {problem.File}:{problem.Line}: {problem.Message}");
        if (actions) Console.WriteLine($"::error file={problem.File},line={problem.Line}::{problem.Message}");
    }
    if (problems.Count > 0) { Console.Error.WriteLine($"{problems.Count} problem(s)."); return 1; }
    Console.WriteLine(success);
    return 0;
}

Dictionary<string, string> ReadReleased()
{
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, VersionsFile)));
    if (!doc.RootElement.TryGetProperty("schema", out JsonElement schema) || schema.GetInt32() != 1)
        throw new InvalidOperationException($"{VersionsFile}: unsupported schema; this script reads schema 1.");
    return doc.RootElement.GetProperty("released").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
}

string ReleasedCliCommit()
{
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, VersionsFile)));
    return doc.RootElement.GetProperty("releasedCliCommit").GetString()!;
}

int FileLine(string file, string needle) =>
    Array.FindIndex(File.ReadAllLines(Path.Combine(root, file)), l => l.Contains(needle, StringComparison.Ordinal)) is var i and >= 0 ? i + 1 : 1;

// The copyable pins in the introductory files, with the index of each version in its file's text.
List<Pin> FindPins()
{
    var files = new List<string> { "README.md", "CONTRIBUTING.md" };
    foreach (string dir in new[] { "docs", "examples", "tools", "src" })
        files.AddRange(Directory.GetFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories)
            .Where(f => dir == "src" ? Path.GetExtension(f) == ".md" // a package's README, not its build
                : Path.GetExtension(f) is ".md" or ".csproj" or ".props" or ".targets" or ".yml" or ".yaml" or ".cs")
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Where(f => !Path.GetFileName(f).StartsWith("native-validation-", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')));
    const string Id = @"(?<id>Valheim\.Testing(?:\.[A-Za-z]+)*)";
    const string Version = @"(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]*[0-9A-Za-z])?)";
    // (pattern, package when the pattern names none)
    var patterns = new (Regex Pattern, string? Id)[]
    {
        (new Regex($@"Include=""{Id}""\s+Version=""\[?{Version}\]?"""), null),
        (new Regex($@"dotnet\s+(?:tool\s+install|add\s+package)\s+{Id}\s+--version\s+{Version}"), null),
        (new Regex($@"`{Id}`[ \t|]*`{Version}`"), null),
        (new Regex($@"\[{Id}\]\(https://www\.nuget\.org/packages/[^)]+\)[ \t]*\|[ \t]*`{Version}`"), null),
        (new Regex($@"ToolkitPackageVersion={Version}"), "Valheim.Testing.Game"),
        (new Regex($@"<ToolkitPackageVersion\b[^>]*>{Version}</ToolkitPackageVersion>"), "Valheim.Testing.Game"),
    };
    var pins = new List<Pin>();
    foreach (string file in files.Order(StringComparer.Ordinal))
    {
        string text = ReadText(Path.Combine(root, file), out _);
        foreach (var (pattern, fixedId) in patterns)
            foreach (Match m in pattern.Matches(text))
            {
                Group version = m.Groups["version"];
                int line = 1 + text.AsSpan(0, version.Index).Count('\n');
                pins.Add(new Pin(file, line, fixedId ?? m.Groups["id"].Value, version.Value, version.Index));
            }
    }
    return pins.OrderBy(p => p.File, StringComparer.Ordinal).ThenBy(p => p.Index).ToList();
}

static async Task<string?> Get(HttpClient http, string url)
{
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception e) when (attempt < 4 && e is HttpRequestException or TaskCanceledException)
        {
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
        }
    }
}

// The text of a file and its encoding, so a rewrite keeps a byte-order mark where there was one.
static string ReadText(string path, out Encoding encoding)
{
    byte[] bytes = File.ReadAllBytes(path);
    bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
    encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom);
    return Encoding.UTF8.GetString(bom ? bytes[3..] : bytes);
}

static bool IsCandidate(string version) => version.Contains("-candidate", StringComparison.OrdinalIgnoreCase);

static string ProjectFile(string name) => $"src/{name}/{name}.csproj";

static string ScriptPath([CallerFilePath] string path = "") => path;

static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(ScriptPath()) ?? "" })
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}

record Pin(string File, int Line, string Id, string Version, int Index = -1, bool Declares = false);
record Problem(string File, int Line, string Message);
