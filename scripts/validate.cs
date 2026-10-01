// Local test-pyramid layers only; never launches Valheim. Run bootstrap-cli.cs first.
//
//   dotnet run scripts/validate.cs
//
// Runs the library tests, compiles the adapter source package against reference stubs, builds every example,
// executes the two no-game examples, runs the package-consuming mod tests, packs the libraries into the local feed and
// runs the packed binding-check tool.
//
// Each command is announced with the time, watched while it runs and stopped at a deadline (#171): twice a macOS CI job sat
// in this script until GitHub cancelled it, its runner no longer answering, and the whole log was lost. Now a command that
// outlives VALHEIM_TESTING_VALIDATE_DEADLINE (seconds; default 600, five times the slowest normal command) or that runs
// the machine out of disk or memory is stopped with a process table first, while the runner can still report it. Test runs
// name a test that makes no progress for five minutes (--blame-hang). The same lines go to artifacts/validate/validate.log,
// next to the test results, for CI to keep when the step fails.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.RegularExpressions;
using System.Xml.Linq;

string root = FindRoot();
string results = Path.Combine(root, "artifacts", "validate");
Directory.CreateDirectory(results);
string transcript = Path.Combine(results, "validate.log");
File.WriteAllText(transcript, "");
var started = Stopwatch.StartNew();
TimeSpan deadline = TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("VALHEIM_TESTING_VALIDATE_DEADLINE") is { Length: > 0 } seconds
    ? int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)
    : 600);

Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj");
Test("tests/Valheim.Testing.Doubles.Tests/Valheim.Testing.Doubles.Tests.csproj");
Test("tests/Valheim.Testing.Bindings.Tests/Valheim.Testing.Bindings.Tests.csproj");
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
    Test(project, "-p:RestorePackagesPath=" + cache, "-p:WarningsAsErrors=CS0612%3BCS0618%3BSYSLIB0050%3BSYSLIB0051");
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
Test("examples/FullLifecycle/MyMod.IntegrationTests/MyMod.IntegrationTests.csproj");
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
Note("Local validation passed.");
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

// A test run's results go under artifacts/validate. When its test host is stopped (--blame-hang) or crashes, vstest reports
// only "Test host process crashed"; the blame sequence file says which tests had started and not completed, so name them.
void Test(string project, params string[] extra)
{
    string output = Path.Combine(results, Path.GetFileNameWithoutExtension(project));
    try
    {
        Run("dotnet", ["test", project, "-c", "Release", "-m:1", "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
            "--results-directory", output, .. extra]);
    }
    catch
    {
        if (Directory.Exists(output))
            foreach (string sequence in Directory.GetFiles(output, "Sequence_*.xml", SearchOption.AllDirectories))
                foreach (var test in XDocument.Load(sequence).Root!.Elements("Test").Where(t => (string?)t.Attribute("Completed") != "True"))
                    Note($"started and not completed: {(string?)test.Attribute("DisplayName")} ({Path.GetRelativePath(results, sequence)})");
        throw;
    }
}

void Run(string file, params string[] arguments)
{
    string command = file + " " + string.Join(' ', arguments);
    Note("start: " + command);
    var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = root };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
    var clock = Stopwatch.StartNew();
    // Waits for the exit; each minute without one says what the machine looks like, so a slow command leaves a trail.
    while (!process.WaitForExit(TimeSpan.FromSeconds(60)))
    {
        var (state, starved) = Machine();
        Note($"still running after {Elapsed(clock.Elapsed)}: {command}; {state}");
        string? reason = clock.Elapsed >= deadline ? $"no exit within the {Elapsed(deadline)} deadline" : starved;
        if (reason == null) continue;
        Note($"stopping {command}: {reason}");
        Diagnose();
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* Exited meanwhile. */ }
        process.WaitForExit();
        throw new TimeoutException($"Validation command stopped ({reason}): {command}");
    }
    if (process.ExitCode != 0)
    {
        Note($"FAILED ({process.ExitCode}) after {Elapsed(clock.Elapsed)}: {command}");
        throw new InvalidOperationException("Validation command failed with exit code " + process.ExitCode);
    }
    Note($"done in {Elapsed(clock.Elapsed)}: {command}");
}

// One line to the console and the transcript: UTC time and time since validation started.
void Note(string text)
{
    string line = $"[validate {DateTime.UtcNow:HH:mm:ss}Z +{Elapsed(started.Elapsed)}] {text}";
    Console.Out.WriteLine(line);
    Console.Out.Flush();
    File.AppendAllText(transcript, line + Environment.NewLine);
}

static string Elapsed(TimeSpan span) => $"{(int)span.TotalMinutes}m{span.Seconds:00}s";

// Free disk where builds and temporary copies go, and memory as the system judges it. Starved (a reason) when the disk has
// under 1 GiB left or the system reports critical memory pressure: either can stop the CI runner itself from reporting.
(string State, string? Starved) Machine()
{
    var parts = new List<string>();
    string? starved = null;
    foreach (string path in new[] { root, Path.GetTempPath() }.Select(p => Path.GetPathRoot(Path.GetFullPath(p))!).Distinct())
    {
        long free = new DriveInfo(path).AvailableFreeSpace;
        parts.Add($"free disk {path} {free / (1024.0 * 1024 * 1024):0.0} GiB");
        if (free < 1L << 30) starved ??= $"under 1 GiB free on {path}";
    }
    if (OperatingSystem.IsMacOS())
    {
        // kern.memorystatus_vm_pressure_level: 1 normal, 2 warning, 4 critical.
        string level = Capture("sysctl", "-n", "kern.memorystatus_vm_pressure_level").Trim();
        string free = Capture("memory_pressure", "-Q").Split('\n').FirstOrDefault(l => l.Contains("free percentage"))?.Split(':').Last().Trim() ?? "?";
        parts.Add($"memory pressure level {level}, {free} free");
        if (level == "4") starved ??= "critical memory pressure";
    }
    else if (OperatingSystem.IsLinux())
    {
        var info = File.ReadLines("/proc/meminfo").Select(l => l.Split(':')).ToDictionary(p => p[0], p => long.Parse(p[1].Trim().Split(' ')[0]));
        double available = (double)info["MemAvailable"] / info["MemTotal"];
        parts.Add($"memory {available:P0} available");
        if (available < 0.03) starved ??= "under 3% of memory available";
    }
    Process[] all = Process.GetProcesses();
    parts.Add($"{all.Length} processes");
    foreach (Process process in all) process.Dispose();
    return (string.Join(", ", parts), starved);
}

// Before a stop: every process (with parents, so the stuck command's tree can be read off) and the disks.
void Diagnose()
{
    string processes = OperatingSystem.IsWindows()
        ? Capture("tasklist")
        : Capture("ps", "-A", "-o", "pid,ppid,pgid,stat,etime,time,rss,%cpu,command");
    string disks = OperatingSystem.IsWindows() ? "" : Capture("df", "-h");
    foreach (string block in new[] { processes, disks })
    {
        Console.Out.WriteLine(block);
        File.AppendAllText(transcript, block + Environment.NewLine);
    }
    Console.Out.Flush();
}

// A diagnostic command's output, or why there is none; never waits more than 30 s.
static string Capture(string file, params string[] arguments)
{
    try
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return $"({file} did not finish within 30 s)";
        }
        return output.Wait(5_000) ? output.Result + (error.Wait(5_000) ? error.Result : "") : $"({file}: no output)";
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return $"({file}: {e.Message})";
    }
}
