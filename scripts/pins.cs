// The released package versions, and every copyable place that names one. Never launches Valheim. The fresh consumer of a
// release is scripts/consumer.cs --feed nuget.
//
//   dotnet run scripts/pins.cs -- write      after a release: rewrite every pin from toolkit-versions.json
//   dotnet run scripts/pins.cs -- check      CI: the pins, toolkit-versions.json and NuGet.org agree
//   dotnet run scripts/pins.cs -- versions   release.yml, before building
//   dotnet run scripts/pins.cs -- docs       CI: the Markdown pages' headings and links (offline)
//   dotnet run scripts/pins.cs -- candidate  validate.cs, before packing: this checkout's candidate identity (offline)
//
// toolkit-versions.json is the one record of what is released: the newest version NuGet.org lists of each package, and the
// valheimCLI commit the released Valheim.Testing.Cli was built from. Documentation and examples pin exactly these versions
// and follow every release: once a release is published, edit the file and run write; nothing else is edited by hand.
//
// Pins are the copyable versions in the README, CONTRIBUTING, docs, examples, tools and the packages' READMEs: a
// PackageReference, a `dotnet tool install` or `dotnet add package` command, ToolkitPackageVersion (the Game package; a -p:
// argument or a project property), and a backticked package ID followed by a backticked version, as in the package table.
// A link to a page of the valheimCLI fork at a full commit pins releasedCliCommit, so the docs read the fork as the
// released Cli was built. History in prose ("new in Game preview 11") is not a pin. Dated native-validation records are
// not scanned. The NativeSmoke tool does not read this file: its generated consumer pins the Game the tool was built with.
//
// check reads NuGet.org and fails, naming the file and line, when: a pin differs from the file; a released version is not
// served, is a candidate, or is superseded by a newer version NuGet.org lists (the docs lag a release; an unlisted
// version, such as a release withdrawn as broken, supersedes nothing); the released Cli was not built from
// releasedCliCommit; a released package's Valheim.Testing* dependency is not met by the released set (so a page pinning
// both Doubles and Valheim.Testing names a pair that restores together); or a project's source version is older than its
// release; or a fork link pinned to releasedCliCommit names a file or heading the fork does not have at that commit (read
// from GitHub). write and docs read only this checkout.
//
// docs fails, naming the file and line, on a Markdown heading that names a version or a date (a page describes what is,
// and history belongs in release notes; native-validation records are exempt), a link to a review/ branch (which moves),
// a valheimCLI fork page linked at anything but a full commit (so it is pinned), and a relative link, or one to this
// repository's main branch, whose file or heading anchor does not exist (#278). It needs no network, so a docs problem is
// never hidden behind a release the pins have not caught up with.
//
// versions refuses a release that publishes a new version of a package another released package references, unless that one
// gets a new version too (its dependency floor would stay on the old one). It also refuses a release whose manifest or packed
// dependencies name a `-candidate.<sha>` version: a candidate is a
// local build identity, and NuGet.org never lets a published id/version be replaced. Checked are each packed project's
// <Version> and Valheim.Testing* PackageReferences, and the Cli packageVersion in cli-dependency.json. It also refuses a
// source version older than the release recorded in toolkit-versions.json. A candidate may sit on main between releases.
// And it refuses a change under a published version in a package the valheim-test tool embeds (#363): the tool ships the
// DLLs of the projects it references (GameSessions, and Game and Valheim.Testing through it), and the consumer its `init`
// creates restores those versions from NuGet.org. So when NuGet.org already serves such a package's source <Version>, its directory and
// those of the projects it references must equal the commit that package was built from (its nuspec's repository commit);
// otherwise the release skips it as published while the tool carries other bytes under the same version. This part reads
// NuGet.org and needs the checkout's full history (release.yml fetches it).
// candidate prints the identity a local pack of this checkout carries, `candidate.<12 hex>`: the start of the SHA-256 over the
// package inputs (every file under src/, docs/packages/, licenses/ and tools/game-references/ but bin/, obj/ and dot-files,
// the repository's root files, cli-dependency.json among them, and the ValheimCLI bundle and loader zip the tool embeds from
// .packages), each by its path and SHA-256. validate.cs packs every package as `<Version>-candidate.<12 hex>`
// (src/Directory.Build.targets), so different sources never share a version in .packages, and keeps a complete set already
// packed under the identity, so one identity has one set of bytes there. It needs no git: the station validates a
// `git archive` copy. A candidate is never released (versions refuses it).
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
// The valheim-test tool: it ships the DLLs of the projects it references, and its generated consumer restores their packages.
const string Tool = "Valheim.Testing.NativeSmoke";
// The pin a valheimCLI fork link at a full commit names: releasedCliCommit, the commit the released Cli was built from.
const string CliCommit = "releasedCliCommit";
Regex ForkLink = new(@"https://github\.com/tvongaza/valheimCLI/blob/(?<version>[0-9a-f]{40})/(?<path>[^#?)\s]+)(?:#(?<anchor>[^)\s]*))?", RegexOptions.IgnoreCase);
string[] packed = ["Valheim.Testing", "Valheim.Testing.Game", "Valheim.Testing.GameSessions", "Valheim.Testing.Doubles", "Valheim.Testing.Adapter", "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool", "Valheim.Testing.NativeSmoke"];

string root = FindRoot();
bool actions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

string mode = args.Length > 0 ? args[0] : "";
if (args.Length > 1) return Usage($"unknown argument '{args[1]}'");
return mode switch
{
    "write" => Write(),
    "check" => await Check(),
    "versions" => await Versions(),
    "docs" => Report(DocsProblems(), "Every heading names no version or date, and every link resolves."),
    "candidate" => Candidate(),
    _ => Usage(mode == "" ? "no mode" : $"unknown mode '{mode}'"),
};

int Usage(string problem)
{
    Console.Error.WriteLine($"pins: {problem}. Usage: dotnet run scripts/pins.cs -- (write | check | versions | docs | candidate)");
    return 2;
}

int Write()
{
    var released = PinTargets(ReadReleased());
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

    // A package NuGet.org has never served (a new one, #289) has no release to record or to pin yet; its first release adds it.
    foreach (string id in packed.Append(Cli).Where(id => !released.ContainsKey(id)))
        if (await Get(http, $"{FlatContainer}/{id.ToLowerInvariant()}/index.json") != null) problems.Add(new(VersionsFile, 1, $"{id} has no released version."));
        else Console.WriteLine($"new  {id}: NuGet.org serves no version yet; {VersionsFile} records its first release");
    // One package's requests do not wait for another's.
    foreach (var found in await Task.WhenAll(released.Select(p => CheckReleased(http, released, p.Key, p.Value))))
        problems.AddRange(found);
    var stated = SourceVersions();
    problems.AddRange(SourceOlderThanReleased(stated, released));

    problems.AddRange(await ForkLinkProblems(http));
    var pins = FindPins();
    var targets = PinTargets(released);
    if (pins.Count == 0) problems.Add(new(VersionsFile, 1, "No version pins found; the scan no longer matches the docs."));
    foreach (Pin pin in pins)
    {
        string? problem = !targets.TryGetValue(pin.Id, out string? version) ? $"{pin.Id} has no released version in {VersionsFile}."
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
        string? commit = NuspecCommit(nuspec);
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
        // NuGet restores the lowest version a range allows, so a floor below the released dependency makes a project with
        // this package alone restore an older one than the rest of the release (v2026.10.06: Doubles .11 kept Valheim.Testing .12).
        else if (range.MinVersion != null && range.MinVersion != pinnedVersion)
            problems.Add(new(VersionsFile, line, $"{id} {version} depends on {dependency.Id} {dependency.Range}, so a project with {id} alone restores {dependency.Id} " +
                $"{range.MinVersion}, not the released {pinned}. Release a new {id} built against {dependency.Id} {pinned}."));
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

async Task<int> Versions()
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
    var released = ReadReleased();
    problems.AddRange(SourceOlderThanReleased(stated, released));
    problems.AddRange(DependentsNotBumped(stated, released));
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var embedded = ProjectClosure(Tool);
    foreach (var found in await Task.WhenAll(embedded.Select(id => ChangedUnderPublishedVersion(http, stated, id))))
        problems.AddRange(found);
    return Report(problems, $"No candidate version in the {stated.Count} release versions and dependencies, none older than {VersionsFile}, " +
        $"and each package the {Tool} tool embeds ({string.Join(", ", embedded)}) is a new version or the source NuGet.org's copy was built from.");
}

// Same id, different bytes, for a package the tool embeds: when NuGet.org already serves the source version, the package's
// directory and those of the projects it references must equal the commit that package was built from (its nuspec's
// repository commit). The working tree is compared, untracked files included, so an edit not yet committed counts too.
async Task<List<Problem>> ChangedUnderPublishedVersion(HttpClient http, List<Pin> stated, string id)
{
    if (stated.FirstOrDefault(p => p.Declares && p.Id == id) is not { } source || IsCandidate(source.Version)
        || !NuGetVersion.TryParse(source.Version, out NuGetVersion? version)) return []; // refused above
    string lower = id.ToLowerInvariant(), v = version.ToNormalizedString().ToLowerInvariant();
    if (await Get(http, $"{FlatContainer}/{lower}/{v}/{lower}.nuspec") is not { } nuspecText) return []; // a new version
    Problem Refuse(string message) => new(source.File, source.Line, $"{id} {source.Version} is on NuGet.org, and {message}");
    if (NuspecCommit(XDocument.Parse(nuspecText)) is not { } commit || !Regex.IsMatch(commit, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase))
        return [Refuse("records no commit it was built from, so its source cannot be compared. Bump <Version>.")];
    if (Git("cat-file", "-e", commit + "^{commit}").Exit != 0)
        return [Refuse($"was built from {commit}, which this checkout does not have. Fetch the full history (actions/checkout fetch-depth: 0); if that commit is gone, bump <Version>.")];
    string[] projects = [.. ProjectClosure(id).Prepend(id)];
    // Each project's directory, and the tracked files outside it that it embeds (the root pins of the ValheimCLI bundle and the
    // BepInExPack GameSessions carries; their zips in .packages are untracked and pinned by those files' SHA-256).
    string[] directories = [.. projects.Select(p => Path.GetDirectoryName(ProjectFile(p))!.Replace('\\', '/')).Concat(projects.SelectMany(EmbeddedOutside)).Distinct()];
    var diff = Git(["diff", "--name-only", commit, "--", .. directories]);
    var untracked = Git(["ls-files", "--others", "--exclude-standard", "--", .. directories]);
    if (diff.Exit != 0 || untracked.Exit != 0) return [Refuse($"comparing its source with {commit} failed: {diff.Error.Trim()} {untracked.Error.Trim()}")];
    string[] changed = [.. (diff.Output + untracked.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Order(StringComparer.Ordinal)];
    if (changed.Length == 0) return [];
    return [Refuse($"its source changed since it was built ({commit[..9]}): {string.Join(", ", changed.Take(5))}{(changed.Length > 5 ? $" and {changed.Length - 5} more" : "")}. " +
        $"The release would skip {id} while the {Tool} tool ships the changed DLL, and a consumer its `init` creates restores the published bytes " +
        "under the same version. Bump <Version>.")];
}

// The candidate identity of this checkout's package inputs (see the top of this file).
int Candidate()
{
    var inputs = new List<string>();
    void Tree(string dir, bool recursive)
    {
        string full = Path.Combine(root, dir);
        if (!Directory.Exists(full)) return;
        foreach (string file in Directory.EnumerateFiles(full))
            if (!Path.GetFileName(file).StartsWith('.')) inputs.Add(file); // not .DS_Store and the like
        if (recursive)
            foreach (string sub in Directory.EnumerateDirectories(full))
                if (Path.GetFileName(sub) is not ("bin" or "obj") && !Path.GetFileName(sub).StartsWith('.'))
                    Tree(Path.GetRelativePath(root, sub), true);
    }
    Tree("src", true);
    Tree(Path.Combine("docs", "packages"), true);
    Tree(Path.Combine("tools", "game-references"), true);
    Tree("licenses", true);
    Tree("", false);
    // Not the Cli package bootstrap-cli.cs packs: it is rebuilt in a new folder each time; cli-dependency.json pins it.
    foreach (string name in new[] { "valheimcli-bundle.zip", "bepinexpack-valheim.zip" })
        if (File.Exists(Path.Combine(root, ".packages", name))) inputs.Add(Path.Combine(root, ".packages", name));
    var lines = new StringBuilder();
    foreach (string file in inputs.Select(file => (Path: Path.GetRelativePath(root, file).Replace('\\', '/'), File: file)).OrderBy(input => input.Path, StringComparer.Ordinal).Select(input => input.Path + "\0" + Sha256(input.File)))
        lines.Append(file).Append('\n');
    string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString()))).ToLowerInvariant()[..12];
    // A SemVer pre-release identifier of digits alone may not start with 0; one of letters and digits may.
    Console.WriteLine("candidate." + (id.All(char.IsAsciiDigit) ? "h" + id : id));
    return 0;

    static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }
}

// The packages a project references, directly or through another, by their ProjectReference items.
// A project's embedded resources outside its own directory that git tracks (not the .packages zips), as repository paths.
IEnumerable<string> EmbeddedOutside(string id)
{
    string project = ProjectFile(id), directory = Path.GetDirectoryName(Path.Combine(root, project))!;
    foreach (string include in XDocument.Load(Path.Combine(root, project)).Descendants("EmbeddedResource").Select(e => (string?)e.Attribute("Include")).OfType<string>())
    {
        string full = Path.GetFullPath(Path.Combine(directory, include.Replace('\\', '/')));
        string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !relative.StartsWith(".packages/", StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal) && !relative.Contains('*')) yield return relative;
    }
}

List<string> ProjectClosure(string id)
{
    var found = new List<string>();
    var pending = new Stack<string>([id]);
    while (pending.Count > 0)
    {
        string project = ProjectFile(pending.Pop());
        foreach (string include in XDocument.Load(Path.Combine(root, project)).Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include")).OfType<string>())
        {
            string referenced = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
            if (!found.Contains(referenced)) { found.Add(referenced); pending.Push(referenced); }
        }
    }
    return found;
}

static string? NuspecCommit(XDocument nuspec) =>
    nuspec.Descendants().FirstOrDefault(e => e.Name.LocalName == "repository")?.Attribute("commit")?.Value;

(int Exit, string Output, string Error) Git(params string[] arguments)
{
    var info = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Could not start git.");
    Task<string> error = process.StandardError.ReadToEndAsync();
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output, error.Result);
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
// A package packs each project it references as a dependency at that project's version as a minimum, and NuGet restores the
// lowest version a range allows. So when a release publishes a new version of a referenced package, every package that
// references it needs a new version too: otherwise its published floor stays on the old one, and a project with it alone
// restores the old dependency beside the new one (v2026.10.06: Doubles 0.1.0-preview.11 kept Valheim.Testing at .12).
IEnumerable<Problem> DependentsNotBumped(List<Pin> stated, Dictionary<string, string> released)
{
    bool IsNew(string id) => stated.FirstOrDefault(p => p.Declares && p.Id == id) is { } source
        && (!released.TryGetValue(id, out string? r) || !string.Equals(source.Version, r, StringComparison.OrdinalIgnoreCase));
    foreach (string id in packed.Where(id => !IsNew(id)))
        foreach (string referenced in DirectReferences(id).Where(referenced => packed.Contains(referenced) && IsNew(referenced)))
            if (stated.FirstOrDefault(p => p.Declares && p.Id == id) is { } source)
                yield return new Problem(source.File, source.Line, $"{id} {source.Version} is released and unchanged, but it references {referenced}, " +
                    $"whose new version this release publishes: {id}'s published dependency would keep the {referenced} it was packed against as its floor, " +
                    $"and a project with {id} alone restores that beside the new one. Bump {id}'s <Version>.");
}

IEnumerable<string> DirectReferences(string id) =>
    XDocument.Load(Path.Combine(root, ProjectFile(id))).Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include")).OfType<string>()
        .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));

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

// What each pin must name: the released versions, and the released Cli's commit for fork links.
Dictionary<string, string> PinTargets(Dictionary<string, string> released) =>
    new(released, StringComparer.Ordinal) { [CliCommit] = ReleasedCliCommit() };

// The Markdown pages: a heading names no version or date, no link goes to a review/ branch or to a valheimCLI fork page
// at anything but a full commit, and every relative link (and every link to this repository's main branch) reaches an
// existing file and, in a Markdown file, an existing heading.
List<Problem> DocsProblems()
{
    var problems = new List<Problem>();
    var versioned = new Regex(@"preview\.?\s?\d|\bv?\d+\.\d+\.\d+\b|\b20\d\d-\d\d-\d\d\b|\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+20\d\d\b", RegexOptions.IgnoreCase);
    var anchors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    foreach (string file in Walk(root, ".md"))
    {
        bool record = Path.GetFileName(file).StartsWith("native-validation-", StringComparison.Ordinal);
        foreach (var (line, text, heading) in MarkdownLines(File.ReadLines(Path.Combine(root, file))))
        {
            if (heading != null && !record && versioned.IsMatch(heading))
                problems.Add(new(file, line, "A heading names a version or a date, so its anchor changes with every release. Name the concept; history belongs in release notes."));
            foreach (string url in Links(text))
            {
                if (Regex.IsMatch(url, @"/(blob|tree)/review/"))
                {
                    problems.Add(new(file, line, $"{url} links a review branch, which moves or goes away; link a released commit or main."));
                    continue;
                }
                if (Regex.Match(url, @"^https?://(?:www\.)?github\.com/tvongaza/valheimCLI/(?:blob|tree)/(?<ref>[^/#?]+)", RegexOptions.IgnoreCase) is { Success: true } fork
                    && !Regex.IsMatch(fork.Groups["ref"].Value, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase))
                {
                    problems.Add(new(file, line, $"{url} links the valheimCLI fork at a branch or short commit; link it at the full releasedCliCommit, which write keeps in step."));
                    continue;
                }
                string target, from;
                if (Regex.Match(url, @"^https://github\.com/tvongaza/ValheimTesting/(?:blob|tree)/main/(?<path>.*)$") is { Success: true } main) (target, from) = (main.Groups["path"].Value, "");
                else if (Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*:")) continue;
                else if (url.StartsWith('/')) (target, from) = (url.TrimStart('/'), "");
                else (target, from) = (url, Path.GetDirectoryName(file) ?? "");
                int hash = target.IndexOf('#');
                string path = hash < 0 ? target : target[..hash], anchor = hash < 0 ? "" : Uri.UnescapeDataString(target[(hash + 1)..]);
                if (path.Contains('?')) (path, anchor) = (path[..path.IndexOf('?')], ""); // ?plain=1#L5 names a line, not a heading
                path = Uri.UnescapeDataString(path);
                string resolved = path == "" ? file : Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, from, path))).Replace('\\', '/');
                string full = Path.Combine(root, resolved);
                if (!File.Exists(full) && !Directory.Exists(full)) { problems.Add(new(file, line, $"{url}: {resolved} does not exist.")); continue; }
                if (anchor == "" || !resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                if (!anchors.TryGetValue(resolved, out var known)) anchors[resolved] = known = HeadingAnchors(File.ReadAllText(full));
                if (!known.Contains(anchor)) problems.Add(new(file, line, $"{url}: {resolved} has no heading with the anchor #{anchor}."));
            }
        }
    }
    return problems;
}

// Each fork link write moves (a pin of releasedCliCommit) must name a page the fork has at its commit, with the heading
// its anchor names. The pages are fetched once each, together.
async Task<List<Problem>> ForkLinkProblems(HttpClient http)
{
    var links = new List<(Pin Pin, string Url, string Page, string Path, string Anchor)>();
    foreach (Pin pin in FindPins().Where(p => p.Id == CliCommit))
    {
        string text = ReadText(Path.Combine(root, pin.File), out _);
        int start = text.LastIndexOf("http", pin.Index, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || ForkLink.Match(text, start) is not { Success: true } m || m.Index != start) continue;
        links.Add((pin, m.Value, m.Groups["version"].Value + "/" + m.Groups["path"].Value, m.Groups["path"].Value, Uri.UnescapeDataString(m.Groups["anchor"].Value)));
    }
    var pages = links.Select(l => l.Page).Distinct(StringComparer.Ordinal).ToList();
    var fetched = await Task.WhenAll(pages.Select(page => Get(http, "https://raw.githubusercontent.com/tvongaza/valheimCLI/" + page)));
    var anchors = pages.Zip(fetched, (page, content) => (page, content == null ? null : HeadingAnchors(content))).ToDictionary(p => p.page, p => p.Item2, StringComparer.Ordinal);
    var problems = new List<Problem>();
    foreach (var (pin, url, page, path, anchor) in links)
    {
        if (anchors[page] is not { } known) problems.Add(new(pin.File, pin.Line, $"{url}: the fork has no {path} at {pin.Version}."));
        else if (anchor != "" && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !known.Contains(anchor))
            problems.Add(new(pin.File, pin.Line, $"{url}: the fork's {path} at {pin.Version} has no heading with the anchor #{anchor}."));
    }
    return problems;
}

// The link targets on a Markdown line outside inline code: inline links, with or without a title, and reference definitions.
static IEnumerable<string> Links(string text)
{
    string plain = Regex.Replace(text, "`[^`]*`", "");
    foreach (Match m in Regex.Matches(plain, @"\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)")) yield return m.Groups["url"].Value;
    if (Regex.Match(plain, @"^\s{0,3}\[(?!\^)[^\]]+\]:\s*<?(?<url>[^\s>]+)") is { Success: true } definition) yield return definition.Groups["url"].Value;
}

// A page's lines outside fenced code, each with its number and, for an ATX heading, the heading's text.
static IEnumerable<(int Line, string Text, string? Heading)> MarkdownLines(IEnumerable<string> lines)
{
    string? fence = null;
    int number = 0;
    foreach (string text in lines)
    {
        number++;
        // A fence closes on a line of its own character, at least as long as its opening, and nothing else.
        if (Regex.Match(text, @"^\s{0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$") is { Success: true } marker)
        {
            string run = marker.Groups["fence"].Value;
            if (fence == null) { fence = run; continue; }
            if (run[0] == fence[0] && run.Length >= fence.Length && marker.Groups["info"].Value.Trim() == "") { fence = null; continue; }
        }
        if (fence != null) continue;
        yield return (number, text, Regex.Match(text, @"^#{1,6}\s+(?<text>.*?)\s*#*\s*$") is { Success: true } h ? h.Groups["text"].Value : null);
    }
}

// GitHub's anchors for a page's headings: lower case, punctuation dropped, spaces as hyphens, a repeat numbered.
static HashSet<string> HeadingAnchors(string content)
{
    var anchors = new HashSet<string>(StringComparer.Ordinal);
    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var (_, _, heading) in MarkdownLines(content.Split('\n').Select(l => l.TrimEnd('\r'))))
    {
        if (heading == null) continue;
        string label = Regex.Replace(Regex.Replace(heading, @"!?\[(?<text>[^\]]*)\]\([^)]*\)", "${text}"), "<[^>]+>", "");
        string slug = Regex.Replace(label.ToLowerInvariant(), @"[^\w\- ]", "").Replace(' ', '-');
        int n = seen.TryGetValue(slug, out int count) ? count : 0;
        seen[slug] = n + 1;
        anchors.Add(n == 0 ? slug : $"{slug}-{n}");
    }
    return anchors;
}

// The checkout's files with one of the extensions under a directory, relative to the root with '/'. Build output,
// artifacts and dot-directories other than .github are never entered.
List<string> Walk(string directory, params string[] extensions)
{
    var found = new List<string>();
    var pending = new Stack<string>([directory]);
    while (pending.Count > 0)
    {
        string dir = pending.Pop();
        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            string name = Path.GetFileName(sub);
            if (name is "bin" or "obj" or "artifacts" or "_site" or "node_modules" || (name.StartsWith('.') && name != ".github")) continue;
            if (!new FileInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(sub);
        }
        found.AddRange(Directory.EnumerateFiles(dir).Where(f => extensions.Contains(Path.GetExtension(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')));
    }
    return found.Order(StringComparer.Ordinal).ToList();
}

// The copyable pins in the introductory files, with the index of each version in its file's text.
List<Pin> FindPins()
{
    var files = Walk(root, ".md").Where(f => !f.Contains('/')).ToList(); // the root pages: README, CONTRIBUTING, AGENTS, ...
    foreach (string dir in new[] { "docs", "examples", "tools", "src" })
        files.AddRange((dir == "src" ? Walk(Path.Combine(root, dir), ".md") // a package's README, not its build
                : Walk(Path.Combine(root, dir), ".md", ".csproj", ".props", ".targets", ".yml", ".yaml", ".cs"))
            .Where(f => !Path.GetFileName(f).StartsWith("native-validation-", StringComparison.Ordinal)));
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
        (ForkLink, CliCommit),
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
