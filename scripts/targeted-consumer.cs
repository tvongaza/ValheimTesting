// Prove the copyable targeted regression builds from a released Game package, outside this checkout, with NuGet.org as
// its only feed and writable, owned package/HTTP caches. Never launches Valheim.
//
//   dotnet run scripts/targeted-consumer.cs
//   dotnet run scripts/targeted-consumer.cs -- --version 0.1.0-preview.16  # refuses before restore
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

const string Minimum = "0.1.0-preview.17"; // First published Game package with RegressionEnvironment and TargetedRegression.
const string Package = "Valheim.Testing.Game";
const string Feed = "https://api.nuget.org/v3/index.json";
const string Index = "https://api.nuget.org/v3-flatcontainer/valheim.testing.game/index.json";

string root = Root();
string source = Path.Combine(root, "examples", "TargetedRegression");
string project = Path.Combine(source, "TargetedRegression.csproj");
string xml = File.ReadAllText(project);
var pin = Regex.Match(xml, @"<ToolkitPackageVersion[^>]*>([^<]+)</ToolkitPackageVersion>");
if (!pin.Success || xml.Contains("ProjectReference", StringComparison.Ordinal))
    throw new InvalidOperationException("TargetedRegression must pin a published Game package without a source-project fallback.");
string version = pin.Groups[1].Value;
if (args.Length != 0)
{
    if (args is not ["--version", _]) throw new ArgumentException("Usage: dotnet run scripts/targeted-consumer.cs -- [--version VERSION]");
    version = args[1];
}
if (Compare(version, Minimum) < 0)
    throw new InvalidOperationException($"{Package} {version} does not provide RegressionEnvironment/TargetedRegression; use {Minimum} or newer, once published on NuGet.org.");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
using JsonDocument published = JsonDocument.Parse(await http.GetStringAsync(Index));
if (!published.RootElement.GetProperty("versions").EnumerateArray().Any(item => item.GetString() == version))
    throw new InvalidOperationException($"{Package} {version} is not published on NuGet.org; publish a compatible Game package before building this example.");

string work = Path.Combine(Path.GetTempPath(), "valheim-targeted-consumer-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(work);
    foreach (string name in new[] { "TargetedRegression.csproj", "Program.cs", "Scenario.cs" })
        File.Copy(Path.Combine(source, name), Path.Combine(work, name));
    string config = Path.Combine(work, "NuGet.Config");
    File.WriteAllText(config, $"<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"{Feed}\"/></packageSources></configuration>");
    string packages = Path.Combine(work, "packages"), cache = Path.Combine(work, "http-cache");
    foreach (string directory in new[] { packages, cache })
    {
        Directory.CreateDirectory(directory);
        string probe = Path.Combine(directory, ".write-test");
        File.WriteAllText(probe, "writable");
        File.Delete(probe);
    }
    Console.WriteLine($"Restoring {Package} {version} from NuGet.org with owned caches: packages={packages}; http={cache}");
    Run("dotnet", ["restore", "TargetedRegression.csproj", "--configfile", config, "-p:ToolkitPackageVersion=" + version]);
    string metadata = Path.Combine(packages, "valheim.testing.game", version.ToLowerInvariant(), ".nupkg.metadata");
    if (!File.Exists(metadata) || !File.ReadAllText(metadata).Contains(Feed, StringComparison.Ordinal))
        throw new InvalidOperationException($"{Package} {version} was not restored from NuGet.org (missing or wrong source in {metadata}).");
    Run("dotnet", ["build", "TargetedRegression.csproj", "-c", "Release", "--no-restore", "-p:ToolkitPackageVersion=" + version]);
    Console.WriteLine($"PASS: TargetedRegression builds outside the checkout from {Package} {version} on NuGet.org only.");

    void Run(string command, string[] arguments)
    {
        var start = new ProcessStartInfo(command) { WorkingDirectory = work, UseShellExecute = false };
        start.Environment["NUGET_PACKAGES"] = packages;
        start.Environment["NUGET_HTTP_CACHE_PATH"] = cache;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{command} {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.");
    }
}
finally
{
    if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
}
return 0;

static int Compare(string value, string minimum)
{
    static (Version Core, int Preview) Parse(string text)
    {
        var match = Regex.Match(text, @"^(\d+\.\d+\.\d+)(?:-preview\.(\d+))?$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new ArgumentException($"Unsupported Game package version {text}; use a published numeric version or preview.N.");
        return (Version.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : int.MaxValue);
    }
    var a = Parse(value); var b = Parse(minimum);
    int core = a.Core.CompareTo(b.Core);
    return core == 0 ? a.Preview.CompareTo(b.Preview) : core;
}

static string Root([CallerFilePath] string file = "")
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(file) ?? "" })
        for (DirectoryInfo? directory = new(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "cli-dependency.json"))) return directory.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}
