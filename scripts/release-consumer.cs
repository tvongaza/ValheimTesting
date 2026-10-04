// Checks the versions a release names. pins needs NuGet.org; never launches Valheim. The fresh consumer of a release is
// scripts/consumer.cs --feed nuget.
//
//   dotnet run scripts/release-consumer.cs -- pins
//   dotnet run scripts/release-consumer.cs -- versions
//
// pins: every copyable version pin in the README, CONTRIBUTING, docs and examples must name a version served by
// NuGet.org. A missing version fails; an older tested version remains valid and reproducible. Copyable pins are
// a PackageReference, a `dotnet tool install` or `dotnet add package` command, `-p:ToolkitPackageVersion=` (the Game
// package), and a backticked package ID followed by a backticked version, as in the package table. History in prose
// ("new in Game preview.11") is not a pin. Dated native-validation records are not scanned.
//
// versions: offline. Refuses a release whose manifest or packed dependencies name a `-candidate.<sha>` version: a candidate
// is a local build identity, and NuGet.org never lets a published id/version be replaced. Checked are each packed
// project's <Version> and Valheim.Testing* PackageReferences, and the Cli packageVersion in cli-dependency.json.
// release.yml runs it before building. A candidate may sit on main between releases.
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

const string FlatContainer = "https://api.nuget.org/v3-flatcontainer";
string[] packed = ["Valheim.Testing", "Valheim.Testing.Game", "Valheim.Testing.Doubles", "Valheim.Testing.Adapter", "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool", "Valheim.Testing.NativeSmoke"];

string root = FindRoot();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
bool actions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

string mode = args.Length > 0 ? args[0] : "";
if (args.Length > 1) return Usage($"unknown argument '{args[1]}'");
return mode switch
{
    "pins" => await Pins(),
    "versions" => Versions(),
    _ => Usage(mode == "" ? "no mode" : $"unknown mode '{mode}'"),
};

int Usage(string problem)
{
    Console.Error.WriteLine($"release-consumer: {problem}. Usage: dotnet run scripts/release-consumer.cs -- (pins | versions)");
    return 2;
}

async Task<int> Pins()
{
    var pins = FindPins();
    if (pins.Count == 0) { Console.Error.WriteLine("No version pins found; the scan no longer matches the docs."); return 1; }
    var published = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    foreach (string id in pins.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        published[id] = await PublishedVersions(id);
    int bad = 0;
    foreach (Pin pin in pins)
    {
        List<string> versions = published[pin.Id];
        string? problem =
            versions.Count == 0 ? $"{pin.Id} is not on NuGet.org" :
            !versions.Contains(pin.Version, StringComparer.OrdinalIgnoreCase) ? $"{pin.Id} {pin.Version} is not on NuGet.org" :
            null;
        Console.WriteLine($"{(problem == null ? "ok  " : "FAIL")} {pin.File}:{pin.Line} {pin.Id} {pin.Version}");
        if (problem == null) continue;
        bad++;
        Console.WriteLine($"     {problem}");
        if (actions) Console.WriteLine($"::error file={pin.File},line={pin.Line}::{problem}");
    }
    Console.WriteLine(bad == 0
        ? $"All {pins.Count} documented pins name versions served by NuGet.org."
        : $"{bad} of {pins.Count} documented pins are not served by NuGet.org.");
    return bad == 0 ? 0 : 1;
}

int Versions()
{
    // Every version a release would publish or depend on: each packed project's <Version> and Valheim.Testing*
    // references (attributes in any order), and the Cli pin. A version set through an MSBuild property cannot be read
    // here, so it is refused rather than passed.
    var stated = new List<Pin>();
    var element = new Regex(@"<Version>(?<version>[^<]+)</Version>|<PackageReference\b[^>]*>|""packageVersion"":\s*""(?<version>[^""]+)""");
    var include = new Regex(@"\bInclude=""(?<id>Valheim\.Testing[\w.]*)""");
    var reference = new Regex(@"\b(?:Version|VersionOverride)=""\[?(?<version>[^\]""]+)\]?""");
    foreach (string file in packed.Select(ProjectFile).Append("cli-dependency.json"))
    {
        string[] lines = File.ReadAllLines(Path.Combine(root, file));
        for (int i = 0; i < lines.Length; i++)
            foreach (Match m in element.Matches(lines[i]))
            {
                if (m.Groups["version"].Success) { stated.Add(new Pin(file, i + 1, file, m.Groups["version"].Value)); continue; }
                if (include.Match(m.Value) is not { Success: true } id) continue;
                foreach (Match v in reference.Matches(m.Value)) stated.Add(new Pin(file, i + 1, id.Groups["id"].Value, v.Groups["version"].Value));
            }
    }
    var refused = stated.Where(p => p.Version.Contains("-candidate", StringComparison.OrdinalIgnoreCase) || p.Version.Contains("$(")).ToList();
    foreach (Pin pin in refused)
    {
        string problem = pin.Version.Contains("$(")
            ? $"{pin.File}:{pin.Line} sets {pin.Id}'s version through a property ({pin.Version}), which this check cannot read; write the exact version."
            : $"{pin.File}:{pin.Line} names the candidate version {pin.Version}. A candidate is a local build identity and is never released; " +
              "move it to a version NuGet.org has never served before tagging.";
        Console.Error.WriteLine("FAIL " + problem);
        if (actions) Console.WriteLine($"::error file={pin.File},line={pin.Line}::{problem}");
    }
    if (refused.Count > 0) return 1;
    Console.WriteLine($"No candidate version in the {stated.Count} release versions and dependencies.");
    return 0;
}

// The copyable pins in the introductory files.
List<Pin> FindPins()
{
    var files = new List<string> { "README.md", "CONTRIBUTING.md" };
    foreach (string dir in new[] { "docs", "examples" })
        files.AddRange(Directory.GetFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".md" or ".csproj" or ".props" or ".targets" or ".yml" or ".yaml")
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Where(f => !Path.GetFileName(f).StartsWith("native-validation-", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')));
    const string Id = @"(?<id>Valheim\.Testing(?:\.[A-Za-z]+)*)";
    const string Version = @"(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)";
    var patterns = new[]
    {
        new Regex($@"Include=""{Id}""\s+Version=""\[?{Version}\]?"""),
        new Regex($@"dotnet\s+(?:tool\s+install|add\s+package)\s+{Id}\s+--version\s+{Version}"),
        new Regex($@"`{Id}`[\s|]*`{Version}`"),
        new Regex($@"\[{Id}\]\(https://www\.nuget\.org/packages/[^)]+\)\s*\|\s*`{Version}`"),
    };
    var toolkit = new Regex($@"ToolkitPackageVersion={Version}");
    var pins = new List<Pin>();
    foreach (string file in files.Order(StringComparer.Ordinal))
    {
        string[] lines = File.ReadAllLines(Path.Combine(root, file));
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Regex pattern in patterns)
                foreach (Match m in pattern.Matches(lines[i]))
                    pins.Add(new Pin(file, i + 1, m.Groups["id"].Value, m.Groups["version"].Value));
            foreach (Match m in toolkit.Matches(lines[i]))
                pins.Add(new Pin(file, i + 1, "Valheim.Testing.Game", m.Groups["version"].Value));
        }
    }
    return pins;
}

async Task<List<string>> PublishedVersions(string id)
{
    string url = $"{FlatContainer}/{id.ToLowerInvariant()}/index.json";
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
            response.EnsureSuccessStatusCode();
            using JsonDocument index = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return index.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToList();
        }
        catch (Exception e) when (attempt < 4 && e is HttpRequestException or TaskCanceledException)
        {
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
        }
    }
}

static string ProjectFile(string name) => name == "Valheim.Testing.NativeSmoke"
    ? "examples/NativeSmoke/NativeSmoke.csproj"
    : $"src/{name}/{name}.csproj";

static string ScriptPath([CallerFilePath] string path = "") => path;

static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(ScriptPath()) ?? "" })
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}

record Pin(string File, int Line, string Id, string Version);
