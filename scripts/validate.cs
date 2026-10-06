// Local test-pyramid layers only; never launches Valheim. Run bootstrap-cli.cs first.
//
//   dotnet run scripts/validate.cs
//
// Runs the library tests, compiles the adapter source package against reference stubs, builds every example, tool and script, runs
// the FullLifecycle example's and the native acceptance suite's tests against scripted fakes,
// executes the no-game example (SharedWorld), packs the libraries into the local feed and runs scripts/consumer.cs --feed local:
// a consumer outside this checkout of exactly the packages just packed.
//
// Each command is announced with the time and its duration. A test run that makes no progress for five minutes is stopped
// and the tests that had started and not completed are named (--blame-hang). The same lines go to
// artifacts/validate/validate.log, next to the test results, for CI to keep when the step fails.
//
// No command may create, change or remove anything in ValheimTesting's own folder on this machine (the run journal, host lock,
// leases, runs and extracted bundles of the user running validation): tests use temporary folders (#411). Each command is
// followed by a comparison with the folder as validation found it, and the first command that changed it fails, naming what.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

string root = FindRoot();
if (args.Length != 0) throw new ArgumentException("usage: dotnet run scripts/validate.cs");
string results = Path.Combine(root, "artifacts", "validate");
Directory.CreateDirectory(results);
string transcript = Path.Combine(results, "validate.log");
File.WriteAllText(transcript, "");
var started = Stopwatch.StartNew();
string dataRoot = DataRoot();
var dataBefore = Snapshot(dataRoot);

Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj");
Test("tests/Valheim.Testing.Doubles.Tests/Valheim.Testing.Doubles.Tests.csproj");
Test("tests/Valheim.Testing.Bindings.Tests/Valheim.Testing.Bindings.Tests.csproj");
// The adapter source is compiled into a mod's game-side adapter against the game; here, against declared signatures
// and the real HarmonyX (see the project for what that does and does not prove).
Run("dotnet", "build", "tests/Valheim.Testing.Adapter.CompileCheck/Valheim.Testing.Adapter.CompileCheck.csproj", "-c", "Release", "-m:1");
// Every example and maintainer tool one directory down, and the Linux image's boot check (docker/linux-server/smoke).
Run("dotnet", "build", Solution("examples", new[] { "examples", "tools" }
    .SelectMany(dir => Directory.GetDirectories(Path.Combine(root, dir)).SelectMany(sub => Directory.GetFiles(sub, "*.csproj")))
    .Append(Path.Combine(root, "docker", "linux-server", "smoke", "LinuxServerSmoke.csproj"))), "-c", "Release", "-nodeReuse:false");
// Every file-based script builds, warnings being errors here too: the maintainer tools no step below runs would otherwise
// first fail when someone needs them. This script is running, so it is not built again.
foreach (string script in Directory.GetFiles(Path.Combine(root, "scripts"), "*.cs").Order(StringComparer.Ordinal)
    .Where(script => Path.GetFileName(script) != "validate.cs"))
    Run("dotnet", "build", script);
// The full-life-cycle example's external projects; its game-side mod and adapter need a game install and build elsewhere.
// The native session tests (trait Category=Native) launch the game when a private session.json sits beside their project
// (the example's skip without one, the native acceptance suite's fail), so validation leaves them out.
Test("examples/FullLifecycle/MyMod.IntegrationTests/MyMod.IntegrationTests.csproj", "--filter", "Category!=Native");
// The native acceptance suite's tests against scripted fakes.
Test("tests/Valheim.Testing.NativeAcceptance.Tests/Valheim.Testing.NativeAcceptance.Tests.csproj", "--filter", "Category!=Native");
Run("dotnet", "run", "--project", "examples/SharedWorld", "-c", "Release", "--no-build");
// Every package packs as <Version>-candidate.<hash of the package inputs> (pins.cs candidate, one owner of the identity):
// different inputs never share a version in .packages. Packs of these packages under any other version are removed first; a
// complete set already packed under this identity is kept rather than packed again, so one identity has one set of bytes.
string[] packed = ["Valheim.Testing", "Valheim.Testing.Doubles", "Valheim.Testing.Game", "Valheim.Testing.GameSessions", "Valheim.Testing.Adapter", "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool", "Valheim.Testing.NativeSmoke"];
string candidate = Capture("dotnet", "run", "scripts/pins.cs", "--", "candidate").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
if (!System.Text.RegularExpressions.Regex.IsMatch(candidate, "^candidate\\.h?[0-9a-f]{12}$")) throw new InvalidOperationException("pins.cs candidate printed no identity: " + candidate);
Note("candidate identity: " + candidate);
string feed = Path.Combine(root, ".packages");
// A file of one of these packages: its ID, then a version (the Cli and the zips are not touched).
var ours = Directory.GetFiles(feed, "*.nupkg").Where(file => packed.Contains(System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(file), @"\.\d+\.\d+\.\d+(-[^/\\]*)?\.nupkg$", ""))).ToList();
var current = ours.Where(file => Path.GetFileName(file).EndsWith("-" + candidate + ".nupkg", StringComparison.Ordinal)).ToList();
foreach (string other in ours.Except(current)) File.Delete(other);
if (current.Count == packed.Length) Note($"{candidate}: every package is already packed under this identity in .packages; kept");
else
{
    foreach (string partial in current) File.Delete(partial);
    Run("dotnet", "pack", Solution("packages", packed.Select(name => $"src/{name}/{name}.csproj")),
        "-c", "Release", "-nodeReuse:false", "-o", feed, "-p:ValheimTestingCandidate=" + candidate);
}
// A mod's view of what was just packed: outside this checkout, the candidate packages only from .packages and byte-identical
// to it, the Cli from .packages too (its pin may not be published yet), every other package from NuGet.org.
Run("dotnet", "run", "scripts/consumer.cs", "--", "--feed", "local", "--candidate", candidate);
Note("Local validation passed.");
return 0;

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

// One MSBuild invocation for several projects, through a solution file written beside the results: shared references are
// evaluated and built once and independent projects build in parallel. One command per project spent about 2.5 s each on
// a Windows CI runner, most of it start-up. No worker node outlives the command (-nodeReuse:false at the call).
string Solution(string name, IEnumerable<string> projects)
{
    string file = Path.Combine(results, name + ".slnx");
    var solution = new XElement("Solution", projects.Select(project => Path.GetFullPath(project, root)).Order(StringComparer.Ordinal)
        .Select(project => new XElement("Project", new XAttribute("Path", Path.GetRelativePath(results, project).Replace('\\', '/')))));
    File.WriteAllText(file, solution.ToString());
    return file;
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
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        Note($"FAILED ({process.ExitCode}) after {Elapsed(clock.Elapsed)}: {command}");
        throw new InvalidOperationException("Validation command failed with exit code " + process.ExitCode);
    }
    Note($"done in {Elapsed(clock.Elapsed)}: {command}");
    DataRootUnchanged(command);
}

// Runs a command as Run does and returns its standard output (its errors still reach the console).
string Capture(string file, params string[] arguments)
{
    string command = file + " " + string.Join(' ', arguments);
    Note("start: " + command);
    var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = root, RedirectStandardOutput = true };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        Note($"FAILED ({process.ExitCode}): {command}");
        throw new InvalidOperationException("Validation command failed with exit code " + process.ExitCode);
    }
    DataRootUnchanged(command);
    return output;
}

// ValheimTesting's own folder on this machine, as the toolkit places it (LocalSteamLocator.DataRoot).
static string DataRoot() =>
    OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValheimTesting")
    : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ValheimTesting")
    : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "ValheimTesting");

// Every entry under the folder: a file by its size and write time, a directory by its write time (an entry made and removed
// again still moves its directory's), a link by its target and never followed. Empty when the folder does not exist. A
// directory removed during the walk is left out (reported as removed); Finder's .DS_Store files are not counted.
static Dictionary<string, string> Snapshot(string root)
{
    var entries = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!Directory.Exists(root)) return entries;
    entries["."] = "directory " + Directory.GetLastWriteTimeUtc(root).Ticks;
    var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
    void Walk(DirectoryInfo directory)
    {
        FileSystemInfo[] found;
        try { found = directory.GetFileSystemInfos("*", options); }
        catch (DirectoryNotFoundException) { return; }
        foreach (var entry in found)
        {
            if (entry.Name == ".DS_Store") continue;
            string relative = Path.GetRelativePath(root, entry.FullName);
            if (entry.LinkTarget != null) entries[relative] = "link " + entry.LinkTarget;
            else if (entry is DirectoryInfo child) { entries[relative] = "directory " + child.LastWriteTimeUtc.Ticks; Walk(child); }
            else entries[relative] = $"file {((FileInfo)entry).Length} {entry.LastWriteTimeUtc.Ticks}";
        }
    }
    Walk(new DirectoryInfo(root));
    return entries;
}

void DataRootUnchanged(string command)
{
    var now = Snapshot(dataRoot);
    var changes = now.Where(entry => !dataBefore.ContainsKey(entry.Key)).Select(entry => "added " + entry.Key)
        .Concat(now.Where(entry => dataBefore.TryGetValue(entry.Key, out string? was) && was != entry.Value).Select(entry => "changed " + entry.Key))
        .Concat(dataBefore.Keys.Where(key => !now.ContainsKey(key)).Select(key => "removed " + key)).Order(StringComparer.Ordinal).ToList();
    if (changes.Count == 0) return;
    foreach (string change in changes.Take(20)) Note($"{change} in {dataRoot}");
    if (changes.Count > 20) Note($"... and {changes.Count - 20} more");
    Note($"FAILED: {command} changed ValheimTesting's own folder on this machine ({dataRoot}); tests keep their journal, locks and leases in " +
        "temporary folders (#411). A real run on this machine during validation changes it too: validate again with no run going.");
    throw new InvalidOperationException("A validation command changed " + dataRoot);
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
