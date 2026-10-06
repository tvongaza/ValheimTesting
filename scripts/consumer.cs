// A consumer of this checkout's packages, built as a mod author meets them: outside the checkout, with a new package
// cache, every Valheim.Testing* package from one feed at exactly this checkout's version. Never launches Valheim.
//
//   dotnet run scripts/consumer.cs -- --feed local --candidate ID          validate.cs, after packing into .packages
//   dotnet run scripts/consumer.cs -- --feed nuget [--wait-minutes N]     release.yml, on the release tag
//
// The manifest is this checkout's package versions: the <Version> of each packed project (local: with the -ID suffix the
// pack gave it, ID being pins.cs candidate's identity) and the Cli packageVersion in cli-dependency.json.
//
// local: the packed candidates and the Cli bootstrap-cli.cs packed (whose pin may not be published yet) restore only from
// .packages (NuGet's package source mapping, by exact ID), everything else from NuGet.org. Each of them, tools included,
// must be byte-identical to the one in .packages. The package cache (artifacts/consumer-cache) is kept between runs for
// NuGet.org's packages only: every package from .packages is removed from it first, so it is always extracted again from
// the bytes just packed. nuget: NuGet.org is the only source, in a new cache. The script waits up to --wait-minutes for
// it to serve each version (a package still missing then is reported as pending, and the run fails); tools wait for
// registration as well as the package file, since dotnet tool install consults registration, which can lag behind.
//
// Then, with either feed: builds and runs a program using Valheim.Testing, Valheim.Testing.Game and
// Valheim.Testing.Bindings; tests a copy of ModWithTests pinned to the Doubles version, with an obsolete API in the
// package's compiled source an error; compiles the Adapter source package as a net48 game-side adapter against
// tests/Valheim.Testing.Adapter.CompileCheck's reference stubs; installs the Bindings tool and runs it on its own library
// against the Mono.Cecil it ships with (every reference must bind); installs the NativeSmoke tool and runs it (with nuget,
// its init as well: that consumer restores its own pinned Game package). Every Valheim.Testing* package restored must come
// from the feed at exactly the manifest version.
//
// TargetedRegression pins a published Game package of its own, so it is built at that pin, from NuGet.org only, into
// caches of its own, with either feed.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;

const string NuGetOrg = "https://api.nuget.org/v3/index.json";
const string FlatContainer = "https://api.nuget.org/v3-flatcontainer";
string[] packed = ["Valheim.Testing", "Valheim.Testing.Game", "Valheim.Testing.GameSessions", "Valheim.Testing.Doubles", "Valheim.Testing.Adapter", "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool", "Valheim.Testing.NativeSmoke"];
string[] tools = ["Valheim.Testing.Bindings.Tool", "Valheim.Testing.NativeSmoke"];

string root = FindRoot();
string? feed = null, candidate = null;
int waitMinutes = 0;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--feed" && i + 1 < args.Length && args[i + 1] is "local" or "nuget") feed = args[++i];
    else if (args[i] == "--candidate" && i + 1 < args.Length && args[i + 1].StartsWith("candidate.", StringComparison.Ordinal)) candidate = args[++i];
    else if (args[i] == "--wait-minutes" && i + 1 < args.Length && int.TryParse(args[++i], out waitMinutes) && waitMinutes >= 0) { }
    else return Usage($"unknown or invalid argument '{args[i]}'");
}
if (feed == null) return Usage("no --feed");
bool local = feed == "local";
if (local && waitMinutes != 0) return Usage("--wait-minutes is for --feed nuget");
if (local != (candidate != null)) return Usage(local ? "--feed local needs --candidate ID (pins.cs candidate)" : "--candidate is for --feed local");
string localFeed = Path.Combine(root, ".packages");

var manifest = packed.ToDictionary(id => id, id => local ? SourceVersion(id) + "-" + candidate : SourceVersion(id), StringComparer.OrdinalIgnoreCase);
manifest["Valheim.Testing.Cli"] = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cli-dependency.json"))).RootElement.GetProperty("packageVersion").GetString()
    ?? throw new InvalidOperationException("cli-dependency.json has no packageVersion.");
// What restores from .packages: everything this checkout packed (the candidates and the bootstrapped Cli).
var fromLocal = local ? manifest.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var (id, version) in manifest.OrderBy(p => p.Key, StringComparer.Ordinal))
    Console.WriteLine($"manifest: {id} {version}{(local ? (fromLocal.Contains(id) ? " (.packages)" : " (NuGet.org)") : "")}");

if (local)
{
    var missing = manifest.Where(p => fromLocal.Contains(p.Key) && !File.Exists(LocalPackage(p.Key, p.Value))).Select(p => $"{p.Key} {p.Value}").ToList();
    if (missing.Count > 0)
    {
        Console.Error.WriteLine($"Not in {localFeed}: {string.Join(", ", missing)}. Run bootstrap-cli.cs and pack first (validate.cs does).");
        return 1;
    }
}
else
{
    // A candidate is a local build identity that NuGet.org never serves; say so now rather than after the wait.
    if (manifest.FirstOrDefault(p => p.Value.Contains("-candidate", StringComparison.OrdinalIgnoreCase)) is { Key: not null } unpublished)
    {
        Console.Error.WriteLine($"{unpublished.Key} {unpublished.Value} is a candidate version, never published; see pins.cs versions.");
        return 1;
    }
    if (!await WaitUntilServed()) return 1;
}

string work = Path.Combine(Path.GetTempPath(), "valheim-consumer-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(work);
    string cache = local ? Path.Combine(root, "artifacts", "consumer-cache") : Path.Combine(work, "packages");
    if (local) ForgetLocalPackages(cache);
    // The nearest NuGet.Config, above every project below. A new global package folder: nothing restored earlier on this
    // machine can stand in. With nuget, a new HTTP cache too, so no earlier listing can; a folder feed bypasses that cache,
    // so local keeps the machine's and NuGet.org's other packages are not downloaded again.
    string config = Path.Combine(work, "NuGet.Config");
    File.WriteAllText(config, local ? $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources><clear/><add key="local-preview" value="{SecurityElement.Escape(localFeed)}"/><add key="nuget.org" value="{NuGetOrg}"/></packageSources>
          <packageSourceMapping>
            <packageSource key="local-preview">{string.Concat(fromLocal.Order(StringComparer.Ordinal).Select(id => $"<package pattern=\"{id}\"/>"))}</packageSource>
            <packageSource key="nuget.org"><package pattern="*"/></packageSource>
          </packageSourceMapping>
        </configuration>
        """ : NuGetOnly());
    // Tool packages carry their dependencies, so a tool installs from the feed alone.
    string toolConfig = Path.Combine(work, "tools.config");
    File.WriteAllText(toolConfig, local
        ? $"""<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="local-preview" value="{SecurityElement.Escape(localFeed)}"/></packageSources></configuration>"""
        : NuGetOnly());
    var env = ConsumerEnvironment(cache, local ? null : Path.Combine(work, "http-cache"));

    // Libraries: the pure package, the external game package (with its Cli dependency), the game sessions package (on exactly
    // that Game) and the binding-check library.
    string app = Path.Combine(work, "app");
    Directory.CreateDirectory(app);
    File.WriteAllText(Path.Combine(app, "Consumer.csproj"), $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Valheim.Testing" Version="[{manifest["Valheim.Testing"]}]" />
            <PackageReference Include="Valheim.Testing.Game" Version="[{manifest["Valheim.Testing.Game"]}]" />
            <PackageReference Include="Valheim.Testing.GameSessions" Version="[{manifest["Valheim.Testing.GameSessions"]}]" />
            <PackageReference Include="Valheim.Testing.Bindings" Version="[{manifest["Valheim.Testing.Bindings"]}]" />
          </ItemGroup>
        </Project>
        """);
    File.WriteAllText(Path.Combine(app, "Program.cs"), """
        using Valheim.Testing;
        using Valheim.Testing.Bindings;
        using Valheim.Testing.Game;
        using Valheim.Testing.GameSessions;

        // 40 m at the origin, rising 0.5 m per metre in x: 41 m at x = 2.
        var plane = new PlaneTerrain(40, 0.5f, 0);
        if (plane.GetHeight(2, 0) != 41f) { Console.Error.WriteLine("PlaneTerrain returned " + plane.GetHeight(2, 0)); return 1; }
        foreach (Type type in new[] { typeof(PlaneTerrain), typeof(GameActor), typeof(GameSession), typeof(BindingCheck) })
            Console.WriteLine($"{type.FullName}: {type.Assembly.GetName().Name} {type.Assembly.GetName().Version}");
        return 0;
        """);
    Run(env, app, "dotnet", "run", "--project", "Consumer.csproj", "-c", "Release");

    // Doubles: the introductory mod example, pinned to the manifest version. The package's source compiles into the
    // consumer: an obsolete API in it would be every adopting mod's warning.
    string mod = Path.Combine(work, "mod");
    CopyDirectory(Path.Combine(root, "examples", "ModWithTests"), mod);
    string modProject = Path.Combine(mod, "MyMod.Tests", "MyMod.Tests.csproj");
    string doublesPin = $"Include=\"Valheim.Testing.Doubles\" Version=\"[{manifest["Valheim.Testing.Doubles"]}]\"";
    string modText = Regex.Replace(File.ReadAllText(modProject), @"Include=""Valheim\.Testing\.Doubles"" Version=""\[[^\]]+\]""", doublesPin);
    if (!modText.Contains(doublesPin)) throw new InvalidOperationException("ModWithTests no longer pins an exact Valheim.Testing.Doubles version.");
    File.WriteAllText(modProject, modText);
    Run(env, mod, "dotnet", "test", "MyMod.Tests/MyMod.Tests.csproj", "-c", "Release", "-p:WarningsAsErrors=CS0612%3BCS0618%3BSYSLIB0050%3BSYSLIB0051",
        "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none");

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
    if (!File.ReadAllText(Path.Combine(adapter, "obj", "project.assets.json")).Contains("contentFiles/cs/any/ValheimTestingAdapter/", StringComparison.Ordinal))
        throw new InvalidOperationException("The adapter project compiled no ValheimTestingAdapter sources from the package.");

    // Tool: installed as a mod's CI installs it, then run on its own library against the Mono.Cecil it ships with.
    string bindingsTools = Path.Combine(work, "tools");
    Run(env, work, "dotnet", "tool", "install", "Valheim.Testing.Bindings.Tool", "--version", manifest["Valheim.Testing.Bindings.Tool"],
        "--tool-path", bindingsTools, "--configfile", toolConfig);
    string exe = Path.Combine(bindingsTools, OperatingSystem.IsWindows() ? "valheim-bindings.exe" : "valheim-bindings");
    Run(env, work, exe, "--help");
    string library = Directory.GetFiles(Path.Combine(bindingsTools, ".store"), "Valheim.Testing.Bindings.dll", SearchOption.AllDirectories).Single();
    Run(env, work, exe, library, "--game-dir", Path.GetDirectoryName(library)!, "--only", "Mono.Cecil", "--require", "Mono.Cecil");

    // The installed native-smoke tool works without a repository checkout. Its init creates and builds an editable
    // consumer of its own pinned Game package from NuGet.org, which a local build cannot vouch for.
    string smokeTools = Path.Combine(work, "smoke-tools");
    Run(env, work, "dotnet", "tool", "install", "Valheim.Testing.NativeSmoke", "--version", manifest["Valheim.Testing.NativeSmoke"],
        "--tool-path", smokeTools, "--configfile", toolConfig);
    string smoke = Path.Combine(smokeTools, OperatingSystem.IsWindows() ? "valheim-test.exe" : "valheim-test");
    Run(env, work, smoke, "help");
    if (!local)
    {
        string smokeOutput = Path.Combine(work, "smoke-consumer");
        Run(env, work, smoke, "init", "--output", smokeOutput);
        if (!File.Exists(Path.Combine(smokeOutput, "consumer", "SmokeCheck.csproj")))
            throw new InvalidOperationException("The installed native-smoke tool wrote no editable consumer.");
        // The server consumer runs a session: it also restores Valheim.Testing.GameSessions, on exactly that Game.
        string serverOutput = Path.Combine(work, "smoke-server-consumer");
        Run(env, work, smoke, "init", "server", "--output", serverOutput);
        if (!File.ReadAllText(Path.Combine(serverOutput, "consumer", "SmokeCheck.csproj")).Contains("Valheim.Testing.GameSessions", StringComparison.Ordinal))
            throw new InvalidOperationException("The installed native-smoke tool's server consumer does not reference Valheim.Testing.GameSessions.");
    }

    // Every Valheim.Testing* package restored came from the feed at the manifest version, and nothing else.
    var restored = Directory.GetDirectories(cache, "valheim.testing*")
        .SelectMany(Directory.GetDirectories)
        .Select(d => (Id: Path.GetFileName(Path.GetDirectoryName(d)!), Version: Path.GetFileName(d), Dir: d, Tool: false))
        .ToList();
    var toolStore = new[] { bindingsTools, smokeTools }.SelectMany(toolRoot =>
        Directory.GetDirectories(Path.Combine(toolRoot, ".store"), "valheim.testing*")
            .SelectMany(Directory.GetDirectories)
            .Select(d => (Id: Path.GetFileName(Path.GetDirectoryName(d)!), Version: Path.GetFileName(d), Dir: Path.Combine(d, Path.GetFileName(Path.GetDirectoryName(d)!), Path.GetFileName(d)), Tool: true)))
        .ToList();
    int wrong = 0;
    foreach (var (id, version) in manifest.OrderBy(p => p.Key, StringComparer.Ordinal))
    {
        var found = restored.Concat(toolStore).Where(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        string? problem = found.Count == 0 ? "not restored"
            : found.Any(r => !r.Version.Equals(version, StringComparison.OrdinalIgnoreCase)) ? "restored at " + string.Join(", ", found.Select(r => r.Version))
            // A folder feed is not always recorded as the source (tool installs leave it out), so the local packages are
            // matched by content: the .nupkg NuGet keeps beside what it extracted.
            : fromLocal.Contains(id) ? (found.Any(r => !SameBytes(r.Dir, id, version)) ? "restored bytes differ from " + LocalPackage(id, version) : null)
            // A tool install records no source; its config lists NuGet.org alone, so only another recorded source is wrong.
            : found.FirstOrDefault(r => Metadata(r.Dir, "source") is var source && source != NuGetOrg && !(r.Tool && source == null)) is { Dir: not null } stray
                ? "restored from " + (Metadata(stray.Dir, "source") ?? "an unknown source")
            : null;
        Console.WriteLine($"{(problem == null ? "ok  " : "FAIL")} restored {id} {version}{(problem == null ? $" from {(fromLocal.Contains(id) ? "the local feed" : "NuGet.org")}" : ": " + problem)}");
        if (problem != null) wrong++;
    }
    if (wrong > 0) return 1;

    await TargetedRegression();
    Console.WriteLine($"A fresh consumer restored, built and ran this checkout's packages from {(local ? "the local feed" : "NuGet.org")} only.");
    return 0;
}
finally
{
    if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
}

int Usage(string problem)
{
    Console.Error.WriteLine($"consumer: {problem}. Usage: dotnet run scripts/consumer.cs -- --feed local --candidate ID | --feed nuget [--wait-minutes N]");
    return 2;
}

// TargetedRegression at its own pin: NuGet.org only, owned caches, outside the checkout.
async Task TargetedRegression()
{
    string source = Path.Combine(root, "examples", "TargetedRegression");
    string xml = File.ReadAllText(Path.Combine(source, "TargetedRegression.csproj"));
    var pin = Regex.Match(xml, @"<ToolkitPackageVersion[^>]*>([^<]+)</ToolkitPackageVersion>");
    if (!pin.Success || xml.Contains("ProjectReference", StringComparison.Ordinal))
        throw new InvalidOperationException("TargetedRegression must pin a published Game package without a source-project fallback.");
    string version = pin.Groups[1].Value;
    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        if (!await Exists(http, $"{FlatContainer}/valheim.testing.game/{version.ToLowerInvariant()}/valheim.testing.game.{version.ToLowerInvariant()}.nupkg", "Valheim.Testing.Game " + version))
            throw new InvalidOperationException($"TargetedRegression pins Valheim.Testing.Game {version}, which NuGet.org does not serve; pin a published version.");
    string directory = Path.Combine(work, "targeted");
    Directory.CreateDirectory(directory);
    foreach (string name in new[] { "TargetedRegression.csproj", "Program.cs", "Scenario.cs" })
        File.Copy(Path.Combine(source, name), Path.Combine(directory, name));
    string configFile = Path.Combine(directory, "NuGet.Config");
    File.WriteAllText(configFile, NuGetOnly());
    string packages = Path.Combine(directory, "packages");
    var targetedEnv = ConsumerEnvironment(packages, Path.Combine(directory, "http-cache"));
    Run(targetedEnv, directory, "dotnet", "restore", "TargetedRegression.csproj", "--configfile", configFile);
    string metadata = Path.Combine(packages, "valheim.testing.game", version.ToLowerInvariant());
    if (Metadata(metadata, "source") != NuGetOrg)
        throw new InvalidOperationException($"TargetedRegression did not restore Valheim.Testing.Game {version} from NuGet.org ({metadata}).");
    Run(targetedEnv, directory, "dotnet", "build", "TargetedRegression.csproj", "-c", "Release", "--no-restore");
    Console.WriteLine($"ok   TargetedRegression builds outside the checkout from Valheim.Testing.Game {version} on NuGet.org only.");
}

async Task<bool> WaitUntilServed()
{
    // The exact package URL usually appears first. Tool installation also needs NuGet's registration entry, which can lag
    // behind the package URL; neither check depends on the slower search index.
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    DateTime deadline = DateTime.UtcNow.AddMinutes(waitMinutes);
    var pending = manifest.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    while (true)
    {
        foreach (var (id, version) in pending.ToList())
        {
            string lower = id.ToLowerInvariant(), v = version.ToLowerInvariant();
            if (!await Exists(http, $"{FlatContainer}/{lower}/{v}/{lower}.{v}.nupkg", $"{id} {version}")) continue;
            // NuGet's v3 service index advertises this semver2 registration base. A published .nupkg can return 200 while
            // this entry is still 404, and dotnet tool install then says "not found".
            if (tools.Contains(id) && !await Exists(http, $"https://api.nuget.org/v3/registration5-gz-semver2/{lower}/{v}.json", $"{id} {version} registration")) continue;
            Console.WriteLine($"served: {id} {version}");
            pending.Remove(id);
        }
        if (pending.Count == 0) return true;
        if (DateTime.UtcNow >= deadline)
        {
            foreach (var (id, version) in pending) Console.Error.WriteLine($"PENDING: NuGet.org does not serve {id} {version} after {waitMinutes} min.");
            Console.Error.WriteLine("Not a pass: rerun once NuGet.org has finished validating the packages.");
            return false;
        }
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

static async Task<bool> Exists(HttpClient http, string url, string what)
{
    using var request = new HttpRequestMessage(HttpMethod.Head, url);
    try
    {
        using HttpResponseMessage response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode) return true;
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        Console.Error.WriteLine($"{what}: HTTP {(int)response.StatusCode}; retrying.");
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine($"{what}: {e.Message}; retrying.");
    }
    return false;
}

static string NuGetOnly() =>
    $"""<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="nuget.org" value="{NuGetOrg}"/></packageSources></configuration>""";

// No MSBuild worker node outlives a command: on Windows one would hold files in the work tree this script removes.
static Dictionary<string, string> ConsumerEnvironment(string packages, string? httpCache)
{
    var env = new Dictionary<string, string>
    {
        ["NUGET_PACKAGES"] = packages,
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
    };
    if (httpCache != null) env["NUGET_HTTP_CACHE_PATH"] = httpCache;
    return env;
}

string LocalPackage(string id, string version) => Path.Combine(localFeed, $"{id}.{version}.nupkg");

// The kept cache holds NuGet.org's packages for the next run; every package this checkout packed is removed from it (all its
// versions), so the restore extracts it again from .packages and the byte check below compares what was just packed.
void ForgetLocalPackages(string cache)
{
    int removed = 0;
    foreach (string id in fromLocal)
        if (Directory.Exists(Path.Combine(cache, id.ToLowerInvariant()))) { Directory.Delete(Path.Combine(cache, id.ToLowerInvariant()), recursive: true); removed++; }
    Console.WriteLine($"cache: {cache} ({(removed == 0 ? "nothing of this checkout's in it" : $"removed {removed} package(s) of this checkout's; NuGet.org's kept")})");
}

bool SameBytes(string packageDir, string id, string version)
{
    string kept = Path.Combine(packageDir, $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg");
    return File.Exists(kept) && File.ReadAllBytes(LocalPackage(id, version)).AsSpan().SequenceEqual(File.ReadAllBytes(kept));
}

// NuGet writes the source a package came from into .nupkg.metadata beside it.
static string? Metadata(string packageDir, string property)
{
    string metadata = Path.Combine(packageDir, ".nupkg.metadata");
    if (!File.Exists(metadata)) return null;
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metadata));
    return doc.RootElement.TryGetProperty(property, out JsonElement value) ? value.GetString() : null;
}

string SourceVersion(string name)
{
    string project = $"src/{name}/{name}.csproj";
    return Regex.Match(File.ReadAllText(Path.Combine(root, project)), "<Version>([^<]+)</Version>") is { Success: true } m
        ? m.Groups[1].Value
        : throw new InvalidOperationException("No <Version> in " + project);
}

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

// The repository root holds cli-dependency.json. Search upward from the working directory first: CI path mapping can
// rewrite the compile-time source path CallerFilePath reports.
static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(ScriptPath()) ?? "" })
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}
