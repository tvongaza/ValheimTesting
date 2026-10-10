// Inspect the exact .nupkg files a release is about to push, before NuGet.org makes them immutable.
//
//   dotnet run scripts/package-audit.cs -- --directory release --source-commit "$GITHUB_SHA"
//   dotnet run scripts/package-audit.cs -- --directory .packages --candidate
//
// The candidate form is for validate.cs's local feed. It accepts the shared -candidate.<hash> suffix; a release never does.
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

string root = FindRoot();
string? directory = null, sourceCommit = null;
bool candidate = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--directory" && i + 1 < args.Length) directory = args[++i];
    else if (args[i] == "--source-commit" && i + 1 < args.Length) sourceCommit = args[++i];
    else if (args[i] == "--candidate") candidate = true;
    else throw new ArgumentException("package-audit: unknown argument " + args[i]);
}
if (directory == null || (!candidate && (sourceCommit == null || !Regex.IsMatch(sourceCommit, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase))))
    throw new ArgumentException("Usage: dotnet run scripts/package-audit.cs -- --directory DIR --source-commit FULL_SHA | --directory DIR --candidate");

using var cli = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cli-dependency.json")));
string cliCommit = cli.RootElement.GetProperty("commit").GetString()!;
var versions = Directory.GetDirectories(Path.Combine(root, "src"), "Valheim.Testing*", SearchOption.TopDirectoryOnly)
    .Select(Path.GetFileName).OfType<string>()
    .Where(id => File.Exists(Path.Combine(root, "src", id, id + ".csproj")))
    .ToDictionary(id => id, id => XDocument.Load(Path.Combine(root, "src", id, id + ".csproj"))
        .Descendants("Version").Single().Value, StringComparer.Ordinal);
versions.Add("Valheim.Testing.Cli", cli.RootElement.GetProperty("packageVersion").GetString()!);
string folder = Path.GetFullPath(directory);
if (!Directory.Exists(folder)) throw new InvalidDataException("Package directory does not exist: " + folder);
var packageFiles = Directory.GetFiles(folder, "*.nupkg", SearchOption.TopDirectoryOnly);
if (packageFiles.Length != versions.Count) throw new InvalidDataException($"Expected {versions.Count} packages in {folder}, found {packageFiles.Length}.");

var found = new Dictionary<string, (string Version, IReadOnlyList<(string Id, string Version)> Dependencies)>(StringComparer.Ordinal);
string? candidateSuffix = null;
string? candidateCommit = null;
foreach (string file in packageFiles)
{
    using var archive = ZipFile.OpenRead(file);
    string[] paths = archive.Entries.Select(entry => entry.FullName).ToArray();
    var specEntry = archive.Entries.SingleOrDefault(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException(Path.GetFileName(file) + " has no nuspec.");
    using var specStream = specEntry.Open();
    XDocument spec = XDocument.Load(specStream);
    XElement metadata = spec.Descendants().Single(element => element.Name.LocalName == "metadata");
    string Field(string name) => metadata.Elements().Single(element => element.Name.LocalName == name).Value;
    string id = Field("id"), version = Field("version");
    if (!versions.TryGetValue(id, out string? baseVersion) || !found.TryAdd(id, (version, [])))
        throw new InvalidDataException($"Unexpected or duplicate package {id} in {folder}.");
    if (!Path.GetFileName(file).Equals(id + "." + version + ".nupkg", StringComparison.OrdinalIgnoreCase) ||
        !specEntry.FullName.Equals(id + ".nuspec", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"{id} {version}: file and nuspec names disagree.");
    if (candidate && id != "Valheim.Testing.Cli")
    {
        if (!version.StartsWith(baseVersion + "-candidate.", StringComparison.Ordinal) || version.Length == baseVersion.Length + "-candidate.".Length)
            throw new InvalidDataException($"{id}: expected a candidate of {baseVersion}, found {version}.");
        string suffix = version[(baseVersion.Length + 1)..];
        if (candidateSuffix == null) candidateSuffix = suffix;
        else if (candidateSuffix != suffix) throw new InvalidDataException($"{id}: candidate identity differs from the other packages.");
    }
    else if (version != baseVersion) throw new InvalidDataException($"{id}: expected {baseVersion}, found {version}.");

    XElement repository = metadata.Elements().Single(element => element.Name.LocalName == "repository");
    string expectedRepository = id == "Valheim.Testing.Cli" ? "https://github.com/tvongaza/valheimCLI.git" : "https://github.com/tvongaza/ValheimTesting.git";
    string? actualCommit = (string?)repository.Attribute("commit");
    string expectedCommit = id == "Valheim.Testing.Cli" ? cliCommit : sourceCommit ?? "";
    bool commitMatches = candidate && id != "Valheim.Testing.Cli"
        ? Regex.IsMatch(actualCommit ?? "", "^[0-9a-f]{40}$", RegexOptions.IgnoreCase) &&
            (candidateCommit == null || string.Equals(candidateCommit, actualCommit, StringComparison.OrdinalIgnoreCase))
        : string.Equals(actualCommit, expectedCommit, StringComparison.OrdinalIgnoreCase);
    if ((string?)repository.Attribute("type") != "git" || (string?)repository.Attribute("url") != expectedRepository || !commitMatches)
        throw new InvalidDataException($"{id} {version}: repository provenance does not match the {(candidate ? "candidate set" : "release source")}.");
    if (candidate && id != "Valheim.Testing.Cli") candidateCommit ??= actualCommit;

    XElement license = metadata.Elements().Single(element => element.Name.LocalName == "license");
    if ((string?)license.Attribute("type") != "file" || license.Value != "LICENSE" || Field("readme") != "README.md")
        throw new InvalidDataException($"{id} {version}: package metadata must point to its LICENSE and README.md.");
    string[] legalFiles = id == "Valheim.Testing.Cli" ? ["LICENSE", "README.md"] : ["LICENSE", "README.md", "THIRD-PARTY-NOTICES.md"];
    foreach (string legal in legalFiles)
    {
        var entry = archive.GetEntry(legal) ?? throw new InvalidDataException($"{id} {version}: missing {legal}.");
        if (entry.Length == 0) throw new InvalidDataException($"{id} {version}: empty {legal}.");
    }

    foreach (string path in paths)
    {
        string name = Path.GetFileName(path);
        if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains('\\') || path.Split('/').Contains("..") ||
            name.Equals("Valheim.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("valheim.x86_64", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("valheim_server", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("valheim_server.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("valheim_server.x86_64", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("valheim_Data/", StringComparison.OrdinalIgnoreCase) ||
            (IsBinary(name) && (name.StartsWith("assembly_valheim", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) ||
                // The publicizer is a build tool, not a game/runtime assembly. Keep this exception to one exact file.
                (name.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                    !(id == "Valheim.Testing.NativeSmoke" &&
                      path == "tools/net10.0/any/BepInEx.AssemblyPublicizer.dll")) ||
                name.StartsWith("steamclient", StringComparison.OrdinalIgnoreCase))) ||
            name.Equals("Player.log", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("LogOutput.log", StringComparison.OrdinalIgnoreCase) ||
            new[] { ".db", ".fwl", ".fwl2", ".fch", ".env", ".pem", ".pfx", ".key", ".log" }
                .Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{id} {version}: forbidden game, save, log or unsafe path {path}.");
    }

    string payload = id switch
    {
        "Valheim.Testing.Adapter" => "contentFiles/cs/any/ValheimTestingAdapter/",
        "Valheim.Testing.Doubles" => "contentFiles/cs/any/ValheimTestingDoubles/",
        "Valheim.Testing.Bindings.Tool" or "Valheim.Testing.NativeSmoke" => "tools/net10.0/any/",
        "Valheim.Testing" or "Valheim.Testing.Bindings" => "lib/netstandard2.0/",
        _ => "lib/net10.0/",
    };
    if (id is "Valheim.Testing.Adapter" or "Valheim.Testing.Doubles")
    {
        string sources = Path.Combine(root, "src", id, id == "Valheim.Testing.Adapter" ? "Adapter" : "Doubles");
        string[] expected = Directory.GetFiles(sources, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(path => payload + Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
        string[] actual = paths.Where(path => path.StartsWith(payload, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual)) throw new InvalidDataException($"{id} {version}: source payload differs from the {id} source project.");
    }
    else
    {
        string assembly = id == "Valheim.Testing.Cli" ? "Valheim.Cli.Testing.dll" : id + ".dll";
        if (!paths.Contains(payload + assembly)) throw new InvalidDataException($"{id} {version}: missing {payload + assembly}.");
    }
    if (payload.StartsWith("tools/", StringComparison.Ordinal) && !paths.Contains(payload + "DotnetToolSettings.xml"))
        throw new InvalidDataException($"{id} {version}: missing .NET tool command declaration.");
    if (payload.StartsWith("contentFiles/", StringComparison.Ordinal) && paths.Any(path => path.StartsWith("lib/", StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidDataException($"{id} {version}: a source-only package unexpectedly contains an assembly.");

    var dependencies = spec.Descendants().Where(element => element.Name.LocalName == "dependency")
        .Select(element => ((string?)element.Attribute("id") ?? "", (string?)element.Attribute("version") ?? ""))
        .Where(dependency => dependency.Item1.StartsWith("Valheim.Testing", StringComparison.Ordinal)).ToArray();
    found[id] = (version, dependencies);
    Console.WriteLine($"ok   {id} {version}: provenance, legal files and {payload} payload");
}

foreach ((string id, var package) in found)
    foreach ((string dependency, string version) in package.Dependencies)
    {
        if (!found.TryGetValue(dependency, out var target) || (version != target.Version && version != "[" + target.Version + "]"))
            throw new InvalidDataException($"{id}: dependency {dependency} {version} does not name the audited package version.");
    }
Console.WriteLine($"Package audit passed: {found.Count} packages, one coherent {(candidate ? "candidate" : "release")} set.");

static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(SourcePath()) ?? "" })
        for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from the ValheimTesting repository.");
}
static string SourcePath([CallerFilePath] string path = "") => path;
static bool IsBinary(string name) => new[] { ".dll", ".exe", ".so", ".dylib" }
    .Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
