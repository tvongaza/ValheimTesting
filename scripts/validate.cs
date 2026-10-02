// Local test-pyramid layers only; never launches Valheim. Run bootstrap-cli.cs first.
//
//   bash scripts/run.sh validate                 (pwsh -File scripts/run.ps1 validate on Windows)
//
// Runs the library tests, compiles the adapter source package against reference stubs, builds every example,
// executes the two no-game examples, runs the package-consuming mod tests, packs the libraries into the local feed and
// runs the packed binding-check tool.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

string root = FindRoot();
PrepareNuGetCaches(root);
if (args is ["--cache-preflight-only"]) return 0;
if (args.Length != 0) throw new ArgumentException("usage: dotnet run scripts/validate.cs [-- --cache-preflight-only]");

Run("dotnet", "test", "tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "-c", "Release", "-m:1");
Run("dotnet", "test", "tests/Valheim.Testing.Doubles.Tests/Valheim.Testing.Doubles.Tests.csproj", "-c", "Release", "-m:1");
Run("dotnet", "test", "tests/Valheim.Testing.Bindings.Tests/Valheim.Testing.Bindings.Tests.csproj", "-c", "Release", "-m:1");
// The adapter source is compiled into a mod's game-side adapter against the game; here, against declared signatures
// and the real HarmonyX (see the project for what that does and does not prove).
Run("dotnet", "build", "tests/Valheim.Testing.Adapter.CompileCheck/Valheim.Testing.Adapter.CompileCheck.csproj", "-c", "Release", "-m:1");
// Pack the pure packages before the consumer example restores them. This exercises
// the actual source-package layout rather than linking the doubles directory.
foreach (string name in new[] { "Valheim.Testing", "Valheim.Testing.Doubles" })
    Run("dotnet", "pack", $"src/{name}/{name}.csproj", "-c", "Release", "-m:1", "-o", Path.Combine(root, ".packages"));
// Run the consumer example as a mod would: from a copy outside this checkout, pinned to this source's
// Doubles version, restoring Valheim.Testing* only from the fresh local feed into a new package cache.
// NuGet.org serves published packages of the same id and version, so without the mapping, the fresh
// cache and the hash check below, a published or cached copy could pass for this source.
string consumer = Path.Combine(Path.GetTempPath(), "valheim-mod-consumer-" + Guid.NewGuid().ToString("N"));
try
{
    string doublesVersion = SourceVersion("Valheim.Testing.Doubles");
    CopyDirectory(Path.Combine(root, "examples", "ModWithTests"), Path.Combine(consumer, "mod"));
    string project = Path.Combine(consumer, "mod", "MyMod.Tests", "MyMod.Tests.csproj");
    string pin = $"Include=\"Valheim.Testing.Doubles\" Version=\"[{doublesVersion}]\"";
    string text = Regex.Replace(File.ReadAllText(project), @"Include=""Valheim\.Testing\.Doubles"" Version=""\[[^\]]+\]""", pin);
    if (!text.Contains(pin)) throw new InvalidOperationException("ModWithTests no longer pins an exact Valheim.Testing.Doubles version.");
    File.WriteAllText(project, text);
    string feed = SecurityElement.Escape(Path.Combine(root, ".packages"));
    File.WriteAllText(Path.Combine(consumer, "NuGet.Config"), $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources><clear/><add key="local-preview" value="{feed}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
          <packageSourceMapping>
            <packageSource key="local-preview"><package pattern="Valheim.Testing*"/></packageSource>
            <packageSource key="nuget.org"><package pattern="*"/></packageSource>
          </packageSourceMapping>
        </configuration>
        """);
    string cache = Path.Combine(consumer, "packages");
    // The package's source compiles into the consumer: an obsolete API in it would be every adopting mod's warning.
    Run("dotnet", "test", project, "-c", "Release", "-m:1", "-p:RestorePackagesPath=" + cache, "-p:WarningsAsErrors=CS0612%3BCS0618%3BSYSLIB0050%3BSYSLIB0051");
    string packed = Path.Combine(root, ".packages", $"Valheim.Testing.Doubles.{doublesVersion}.nupkg");
    string restored = Path.Combine(cache, "valheim.testing.doubles", doublesVersion.ToLowerInvariant(), $"valheim.testing.doubles.{doublesVersion.ToLowerInvariant()}.nupkg");
    if (!File.Exists(restored) || !File.ReadAllBytes(packed).AsSpan().SequenceEqual(File.ReadAllBytes(restored)))
        throw new InvalidOperationException("ModWithTests did not restore the Valheim.Testing.Doubles package packed from this source.");
}
finally
{
    if (Directory.Exists(consumer)) Directory.Delete(consumer, recursive: true);
}
foreach (string project in Directory.GetFiles(Path.Combine(root, "examples"), "*.csproj", SearchOption.AllDirectories)
             .Where(p => Path.GetDirectoryName(Path.GetDirectoryName(p)) == Path.Combine(root, "examples"))
             .OrderBy(p => p, StringComparer.Ordinal))
    Run("dotnet", "build", project, "-c", "Release", "-m:1");
// The full-life-cycle example's external projects; its game-side mod and adapter need a game install and build elsewhere.
Run("dotnet", "test", "examples/FullLifecycle/MyMod.IntegrationTests/MyMod.IntegrationTests.csproj", "-c", "Release", "-m:1");
Run("dotnet", "run", "--project", "examples/NoGameTerrain", "-c", "Release", "--no-build");
Run("dotnet", "run", "--project", "examples/SharedWorld", "-c", "Release", "--no-build");
foreach (string name in new[] { "Valheim.Testing.Game", "Valheim.Testing.Adapter", "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool" })
    Run("dotnet", "pack", $"src/{name}/{name}.csproj", "-c", "Release", "-m:1", "-o", Path.Combine(root, ".packages"));
// Install the packed binding-check tool as a mod's CI would, from the fresh local feed only, and let it check its own
// library against the Mono.Cecil it ships with: a real assembly whose every Cecil reference must bind (exit 0).
string tools = Path.Combine(Path.GetTempPath(), "valheim-bindings-tool-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(tools);
    string config = Path.Combine(tools, "NuGet.Config");
    File.WriteAllText(config, $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration><packageSources><clear/><add key="local-preview" value="{SecurityElement.Escape(Path.Combine(root, ".packages"))}"/></packageSources></configuration>
        """);
    Run("dotnet", "tool", "install", "Valheim.Testing.Bindings.Tool", "--version", SourceVersion("Valheim.Testing.Bindings.Tool"),
        "--tool-path", Path.Combine(tools, "bin"), "--configfile", config);
    string built = Path.Combine(root, "src", "Valheim.Testing.Bindings.Tool", "bin", "Release", "net10.0");
    Run(Path.Combine(tools, "bin", OperatingSystem.IsWindows() ? "valheim-bindings.exe" : "valheim-bindings"),
        Path.Combine(built, "Valheim.Testing.Bindings.dll"), "--game-dir", built, "--only", "Mono.Cecil", "--require", "Mono.Cecil");
}
finally
{
    if (Directory.Exists(tools)) Directory.Delete(tools, recursive: true);
}
Console.WriteLine("Local validation passed.");
return 0;

string SourceVersion(string name) =>
    Regex.Match(File.ReadAllText(Path.Combine(root, "src", name, name + ".csproj")), "<Version>([^<]+)</Version>") is { Success: true } m
        ? m.Groups[1].Value
        : throw new InvalidOperationException("No <Version> in " + name + ".csproj");

// Copies an example's sources only; build output from an earlier in-place run stays behind.
static void CopyDirectory(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    foreach (string dir in Directory.GetDirectories(from))
        if (Path.GetFileName(dir) is not ("bin" or "obj")) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
}

static string ScriptPath([CallerFilePath] string path = "") => path;

// The repository root holds cli-dependency.json. Search upward from the working directory first:
// CI path mapping can rewrite the compile-time source path CallerFilePath reports.
static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(ScriptPath()) ?? "" })
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}

void Run(string file, params string[] arguments)
{
    var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = root };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine($"FAILED ({process.ExitCode}): {file} {string.Join(' ', arguments)}");
        throw new InvalidOperationException("Validation command failed with exit code " + process.ExitCode);
    }
}

// NuGet can fail in a sandbox even when the directory's mode and owner look writable.
// Probe an actual file before the first restore, then move both caches together if either is denied.
static void PrepareNuGetCaches(string root)
{
    string packages = CachePath("NUGET_PACKAGES", "global-packages");
    string http = CachePath("NUGET_HTTP_CACHE_PATH", "http-cache");
    if (CanWrite(packages) && CanWrite(http))
    {
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);
        Environment.SetEnvironmentVariable("NUGET_HTTP_CACHE_PATH", http);
        Console.WriteLine($"NuGet caches writable: packages={packages}; HTTP={http}");
        return;
    }

    string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..12].ToLowerInvariant();
    string fallback = Path.Combine(Path.GetTempPath(), "valheimtesting-nuget", key);
    string fallbackPackages = Path.Combine(fallback, "packages");
    string fallbackHttp = Path.Combine(fallback, "http-cache");
    if (!CanWrite(fallbackPackages) || !CanWrite(fallbackHttp))
        throw new IOException($"NuGet caches are not writable at {packages} and {http}; fallback {fallback} is also not writable. Set NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH to writable directories.");
    Environment.SetEnvironmentVariable("NUGET_PACKAGES", fallbackPackages);
    Environment.SetEnvironmentVariable("NUGET_HTTP_CACHE_PATH", fallbackHttp);
    Console.WriteLine($"NuGet caches not writable at {packages} or {http}; using packages={fallbackPackages}; HTTP={fallbackHttp}");
}

static string CachePath(string variable, string kind)
{
    string? explicitPath = Environment.GetEnvironmentVariable(variable);
    if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
    var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    info.ArgumentList.Add("nuget");
    info.ArgumentList.Add("locals");
    info.ArgumentList.Add(kind);
    info.ArgumentList.Add("--list");
    using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not query NuGet's " + kind + " path.");
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    int separator = output.IndexOf(':');
    if (process.ExitCode != 0 || separator < 0 || string.IsNullOrWhiteSpace(output[(separator + 1)..]))
        throw new InvalidOperationException($"Could not query NuGet's {kind} path: {error.Trim()}");
    return Path.GetFullPath(output[(separator + 1)..].Trim());
}

static bool CanWrite(string directory)
{
    try
    {
        Directory.CreateDirectory(directory);
        string probe = Path.Combine(directory, ".valheimtesting-write-" + Guid.NewGuid().ToString("N"));
        using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            file.WriteByte(1);
        return true;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
    {
        return false;
    }
}
