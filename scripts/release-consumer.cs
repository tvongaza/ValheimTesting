// Checks the published packages the way a new mod author meets them. Needs NuGet.org; never launches Valheim.
//
//   dotnet run scripts/release-consumer.cs -- pins
//   dotnet run scripts/release-consumer.cs -- consumer [--wait-minutes 30]
//
// pins: every copyable version pin in the README, CONTRIBUTING, docs and examples must name the newest version of its
// package on NuGet.org. A pin to a superseded version, or to a version NuGet.org does not serve, fails. Copyable pins are
// a PackageReference, a `dotnet tool install` or `dotnet add package` command, `-p:ToolkitPackageVersion=` (the Game
// package), and a backticked package ID followed by a backticked version, as in the package table. History in prose
// ("new in Game preview.11") is not a pin. Dated native-validation records are not scanned.
//
// consumer: the release manifest is this checkout's package versions (the <Version> of each packed project and the Cli
// packageVersion in cli-dependency.json), so run it on the release tag. It waits up to --wait-minutes for NuGet.org to
// serve each one (a package still missing then is reported as pending, and the run fails), then, outside this checkout
// with NuGet.org as the only source and a new package cache: builds and runs a program using Valheim.Testing,
// Valheim.Testing.Game and Valheim.Testing.Bindings, runs a copy of ModWithTests pinned to the Doubles version, compiles
// the Adapter source package as a net48 game-side adapter against tests/Valheim.Testing.Adapter.CompileCheck's reference
// stubs, and installs the Bindings tool and runs it. Every Valheim.Testing* package must have been restored from
// NuGet.org at exactly the manifest version.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

const string NuGetOrg = "https://api.nuget.org/v3/index.json";
const string FlatContainer = "https://api.nuget.org/v3-flatcontainer";
string[] packed = ["Valheim.Testing", "Valheim.Testing.Game", "Valheim.Testing.Doubles", "Valheim.Testing.Adapter", "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool"];

string root = FindRoot();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
bool actions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

string mode = args.Length > 0 ? args[0] : "";
int waitMinutes = 0;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--wait-minutes" && i + 1 < args.Length && int.TryParse(args[++i], out waitMinutes) && waitMinutes >= 0) continue;
    return Usage($"unknown or invalid argument '{args[i]}'");
}
return mode switch
{
    "pins" => await Pins(),
    "consumer" => await Consumer(),
    _ => Usage(mode == "" ? "no mode" : $"unknown mode '{mode}'"),
};

int Usage(string problem)
{
    Console.Error.WriteLine($"release-consumer: {problem}. Usage: dotnet run scripts/release-consumer.cs -- (pins | consumer [--wait-minutes N])");
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
        string? newest = versions.Count == 0 ? null : versions.Max(VersionComparer.Instance);
        string? problem =
            newest == null ? $"{pin.Id} is not on NuGet.org" :
            !versions.Contains(pin.Version, StringComparer.OrdinalIgnoreCase) ? $"{pin.Id} {pin.Version} is not on NuGet.org (newest {newest})" :
            VersionComparer.Instance.Compare(pin.Version, newest) < 0 ? $"{pin.Id} {pin.Version} is superseded by {newest}" :
            null;
        Console.WriteLine($"{(problem == null ? "ok  " : "FAIL")} {pin.File}:{pin.Line} {pin.Id} {pin.Version}");
        if (problem == null) continue;
        bad++;
        Console.WriteLine($"     {problem}");
        if (actions) Console.WriteLine($"::error file={pin.File},line={pin.Line}::{problem}");
    }
    Console.WriteLine(bad == 0
        ? $"All {pins.Count} documented pins name the newest published version."
        : $"{bad} of {pins.Count} documented pins are stale. Update them to the newest published version (CONTRIBUTING.md, release step 4).");
    return bad == 0 ? 0 : 1;
}

async Task<int> Consumer()
{
    var manifest = packed.ToDictionary(id => id, SourceVersion, StringComparer.OrdinalIgnoreCase);
    manifest["Valheim.Testing.Cli"] = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cli-dependency.json"))).RootElement.GetProperty("packageVersion").GetString()
        ?? throw new InvalidOperationException("cli-dependency.json has no packageVersion.");
    foreach (var (id, version) in manifest.OrderBy(p => p.Key, StringComparer.Ordinal)) Console.WriteLine($"manifest: {id} {version}");

    // A new version is served by its exact URL within minutes of the push; the search index can take longer and is not consulted.
    DateTime deadline = DateTime.UtcNow.AddMinutes(waitMinutes);
    var pending = manifest.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    while (true)
    {
        foreach (var (id, version) in pending.ToList())
            if (await Served(id, version)) { Console.WriteLine($"served: {id} {version}"); pending.Remove(id); }
        if (pending.Count == 0) break;
        if (DateTime.UtcNow >= deadline)
        {
            foreach (var (id, version) in pending) Console.Error.WriteLine($"PENDING: NuGet.org does not serve {id} {version} after {waitMinutes} min.");
            Console.Error.WriteLine("Not a pass: rerun once NuGet.org has finished validating the packages.");
            return 1;
        }
        await Task.Delay(TimeSpan.FromSeconds(30));
    }

    string work = Path.Combine(Path.GetTempPath(), "valheim-release-consumer-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(work);
        string cache = Path.Combine(work, "packages");
        string config = Path.Combine(work, "NuGet.Config");
        File.WriteAllText(config, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration><packageSources><clear/><add key="nuget.org" value="{NuGetOrg}"/></packageSources></configuration>
            """);
        // The nearest NuGet.Config clears every other source for the projects below. A new global package folder and HTTP cache: nothing restored or listed earlier on this machine can stand in.
        var env = new Dictionary<string, string>
        {
            ["NUGET_PACKAGES"] = cache,
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(work, "http-cache"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };

        // Libraries: the pure package, the external game package (with its Cli dependency) and the binding-check library.
        string app = Path.Combine(work, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Consumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Valheim.Testing" Version="[{manifest["Valheim.Testing"]}]" />
                <PackageReference Include="Valheim.Testing.Game" Version="[{manifest["Valheim.Testing.Game"]}]" />
                <PackageReference Include="Valheim.Testing.Bindings" Version="[{manifest["Valheim.Testing.Bindings"]}]" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(app, "Program.cs"), """
            using Valheim.Testing;
            using Valheim.Testing.Bindings;
            using Valheim.Testing.Game;

            // 40 m at the origin, rising 0.5 m per metre in x: 41 m at x = 2.
            var plane = new PlaneTerrain(40, 0.5f, 0);
            if (plane.GetHeight(2, 0) != 41f) { Console.Error.WriteLine("PlaneTerrain returned " + plane.GetHeight(2, 0)); return 1; }
            foreach (Type type in new[] { typeof(PlaneTerrain), typeof(GameActor), typeof(BindingCheck) })
                Console.WriteLine($"{type.FullName}: {type.Assembly.GetName().Name} {type.Assembly.GetName().Version}");
            return 0;
            """);
        Run(env, app, "dotnet", "run", "--project", "Consumer.csproj", "-c", "Release");

        // Doubles: the introductory mod example, pinned to the manifest version.
        string mod = Path.Combine(work, "mod");
        CopyDirectory(Path.Combine(root, "examples", "ModWithTests"), mod);
        string modProject = Path.Combine(mod, "MyMod.Tests", "MyMod.Tests.csproj");
        string doublesPin = $"Include=\"Valheim.Testing.Doubles\" Version=\"[{manifest["Valheim.Testing.Doubles"]}]\"";
        string modText = Regex.Replace(File.ReadAllText(modProject), @"Include=""Valheim\.Testing\.Doubles"" Version=""\[[^\]]+\]""", doublesPin);
        if (!modText.Contains(doublesPin)) throw new InvalidOperationException("ModWithTests no longer pins an exact Valheim.Testing.Doubles version.");
        File.WriteAllText(modProject, modText);
        Run(env, mod, "dotnet", "test", "MyMod.Tests/MyMod.Tests.csproj", "-c", "Release");

        // Adapter: the source package compiled into a net48 game-side adapter, as in the compile check but from the package.
        string adapter = Path.Combine(work, "adapter");
        Directory.CreateDirectory(adapter);
        File.Copy(Path.Combine(root, "tests", "Valheim.Testing.Adapter.CompileCheck", "ReferenceStubs.cs"), Path.Combine(adapter, "ReferenceStubs.cs"));
        File.WriteAllText(Path.Combine(adapter, "MyMod.TestAdapter.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net48</TargetFramework><LangVersion>10</LangVersion><Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors><WarningsNotAsErrors>$(WarningsNotAsErrors);NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Valheim.Testing.Adapter" Version="[{manifest["Valheim.Testing.Adapter"]}]" PrivateAssets="all" />
                <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
                <PackageReference Include="HarmonyX" Version="[2.9.0]" />
              </ItemGroup>
            </Project>
            """);
        Run(env, adapter, "dotnet", "build", "MyMod.TestAdapter.csproj", "-c", "Release");
        string adapterAssets = File.ReadAllText(Path.Combine(adapter, "obj", "project.assets.json"));
        if (!adapterAssets.Contains("contentFiles/cs/any/ValheimTestingAdapter/", StringComparison.Ordinal))
            throw new InvalidOperationException("The adapter project compiled no ValheimTestingAdapter sources from the package.");

        // Tool: installed as a mod's CI installs it, then run on its own library against the Mono.Cecil it ships with, a real
        // assembly whose every Cecil reference must bind (exit 0), as validate.cs does for the locally packed tool.
        string tools = Path.Combine(work, "tools");
        string toolVersion = manifest["Valheim.Testing.Bindings.Tool"];
        Run(env, work, "dotnet", "tool", "install", "Valheim.Testing.Bindings.Tool", "--version", toolVersion, "--tool-path", tools, "--configfile", config);
        string exe = Path.Combine(tools, OperatingSystem.IsWindows() ? "valheim-bindings.exe" : "valheim-bindings");
        Run(env, work, exe, "--help");
        string library = Directory.GetFiles(Path.Combine(tools, ".store"), "Valheim.Testing.Bindings.dll", SearchOption.AllDirectories).Single();
        string libraryDir = Path.GetDirectoryName(library)!;
        Run(env, work, exe, library, "--game-dir", libraryDir, "--only", "Mono.Cecil", "--require", "Mono.Cecil");

        // Every Valheim.Testing* package restored came from NuGet.org at the manifest version, and nothing else.
        var restored = Directory.GetDirectories(cache, "valheim.testing*")
            .SelectMany(Directory.GetDirectories)
            .Select(d => (Id: Path.GetFileName(Path.GetDirectoryName(d)!), Version: Path.GetFileName(d), Dir: d))
            .ToList();
        var toolStore = Directory.GetDirectories(Path.Combine(tools, ".store"), "valheim.testing*")
            .SelectMany(Directory.GetDirectories)
            .Select(d => (Id: Path.GetFileName(Path.GetDirectoryName(d)!), Version: Path.GetFileName(d), Dir: Path.Combine(d, Path.GetFileName(Path.GetDirectoryName(d)!), Path.GetFileName(d))))
            .ToList();
        int wrong = 0;
        foreach (var (id, version) in manifest.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var found = restored.Concat(toolStore).Where(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            string? problem = found.Count == 0 ? "not restored"
                : found.Any(r => !r.Version.Equals(version, StringComparison.OrdinalIgnoreCase)) ? "restored at " + string.Join(", ", found.Select(r => r.Version))
                : found.Select(r => SourceOf(r.Dir)).FirstOrDefault(s => s != NuGetOrg) is { } other ? "restored from " + other
                : null;
            Console.WriteLine($"{(problem == null ? "ok  " : "FAIL")} restored {id} {version}{(problem == null ? " from NuGet.org" : ": " + problem)}");
            if (problem != null) wrong++;
        }
        if (wrong > 0) return 1;
        Console.WriteLine("A fresh consumer restored, built and ran the release from NuGet.org only.");
        return 0;
    }
    finally
    {
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
    }
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

async Task<bool> Served(string id, string version)
{
    string lower = id.ToLowerInvariant(), v = version.ToLowerInvariant();
    using var request = new HttpRequestMessage(HttpMethod.Head, $"{FlatContainer}/{lower}/{v}/{lower}.{v}.nupkg");
    try
    {
        using HttpResponseMessage response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode) return true;
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        Console.Error.WriteLine($"{id} {version}: HTTP {(int)response.StatusCode}; retrying.");
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine($"{id} {version}: {e.Message}; retrying.");
    }
    return false;
}

// NuGet writes the source a package came from into .nupkg.metadata beside it.
static string? SourceOf(string packageDir)
{
    string metadata = Path.Combine(packageDir, ".nupkg.metadata");
    if (!File.Exists(metadata)) return null;
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metadata));
    return doc.RootElement.TryGetProperty("source", out JsonElement source) ? source.GetString() : null;
}

string SourceVersion(string name) =>
    Regex.Match(File.ReadAllText(Path.Combine(root, "src", name, name + ".csproj")), "<Version>([^<]+)</Version>") is { Success: true } m
        ? m.Groups[1].Value
        : throw new InvalidOperationException("No <Version> in " + name + ".csproj");

static void Run(Dictionary<string, string> env, string directory, string file, params string[] arguments)
{
    Console.WriteLine($"> {file} {string.Join(' ', arguments)}");
    var start = new ProcessStartInfo(file) { WorkingDirectory = directory, UseShellExecute = false };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    foreach (var (name, value) in env) start.Environment[name] = value;
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + file);
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} exited {process.ExitCode}.");
}

// Copies an example's sources only; build output from an earlier in-place run stays behind.
static void CopyDirectory(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    foreach (string dir in Directory.GetDirectories(from))
        if (Path.GetFileName(dir) is not ("bin" or "obj")) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
}

static string ScriptPath([CallerFilePath] string path = "") => path;

static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(ScriptPath()) ?? "" })
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}

record Pin(string File, int Line, string Id, string Version);

// NuGet's SemVer 2 order for the versions used here: numeric release parts, then a prerelease sorts before its release,
// and prerelease labels compare part by part, numbers numerically.
sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var (xr, xp) = Split(x!);
        var (yr, yp) = Split(y!);
        for (int i = 0; i < Math.Max(xr.Length, yr.Length); i++)
        {
            int c = (i < xr.Length ? xr[i] : 0).CompareTo(i < yr.Length ? yr[i] : 0);
            if (c != 0) return c;
        }
        if (xp == null || yp == null) return xp == null ? (yp == null ? 0 : 1) : -1;
        string[] xs = xp.Split('.'), ys = yp.Split('.');
        for (int i = 0; i < Math.Min(xs.Length, ys.Length); i++)
        {
            bool xn = long.TryParse(xs[i], out long xv), yn = long.TryParse(ys[i], out long yv);
            int c = xn && yn ? xv.CompareTo(yv) : xn ? -1 : yn ? 1 : string.Compare(xs[i], ys[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return xs.Length.CompareTo(ys.Length);
    }

    static (long[] Release, string? Prerelease) Split(string version)
    {
        string core = version.Split('+')[0];
        int dash = core.IndexOf('-');
        string release = dash < 0 ? core : core[..dash];
        return (release.Split('.').Select(long.Parse).ToArray(), dash < 0 ? null : core[(dash + 1)..]);
    }
}
