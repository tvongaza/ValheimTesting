// Local test-pyramid layers only; never launches Valheim. Run bootstrap-cli.cs first.
//
//   dotnet run scripts/validate.cs
//
// Runs the library tests, compiles the adapter source package against reference stubs, builds every example,
// executes the two no-game examples, packs the libraries into the local feed and runs scripts/consumer.cs --feed local:
// a consumer outside this checkout of exactly the packages just packed.
//
// Each command is announced with the time and its duration. A test run that makes no progress for five minutes is stopped
// and the tests that had started and not completed are named (--blame-hang). The same lines go to
// artifacts/validate/validate.log, next to the test results, for CI to keep when the step fails.
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

Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj");
Test("tests/Valheim.Testing.Doubles.Tests/Valheim.Testing.Doubles.Tests.csproj");
Test("tests/Valheim.Testing.Bindings.Tests/Valheim.Testing.Bindings.Tests.csproj");
// The adapter source is compiled into a mod's game-side adapter against the game; here, against declared signatures
// and the real HarmonyX (see the project for what that does and does not prove).
Run("dotnet", "build", "tests/Valheim.Testing.Adapter.CompileCheck/Valheim.Testing.Adapter.CompileCheck.csproj", "-c", "Release", "-m:1");
Run("dotnet", "build", Solution("examples", Directory.GetFiles(Path.Combine(root, "examples"), "*.csproj", SearchOption.AllDirectories)
    .Where(p => Path.GetDirectoryName(Path.GetDirectoryName(p)) == Path.Combine(root, "examples"))), "-c", "Release", "-nodeReuse:false");
// The full-life-cycle example's external projects; its game-side mod and adapter need a game install and build elsewhere.
Test("examples/FullLifecycle/MyMod.IntegrationTests/MyMod.IntegrationTests.csproj");
Run("dotnet", "run", "--project", "examples/NoGameTerrain", "-c", "Release", "--no-build");
Run("dotnet", "run", "--project", "examples/SharedWorld", "-c", "Release", "--no-build");
Run("dotnet", "pack", Solution("packages", new[] { "Valheim.Testing", "Valheim.Testing.Doubles", "Valheim.Testing.Game", "Valheim.Testing.Adapter", "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool" }
    .Select(name => $"src/{name}/{name}.csproj").Append("examples/NativeSmoke/NativeSmoke.csproj")),
    "-c", "Release", "-nodeReuse:false", "-o", Path.Combine(root, ".packages"));
// A mod's view of what was just packed: outside this checkout, a new package cache, Valheim.Testing* only from .packages
// and byte-identical to it (NuGet.org serves published packages of the same id and version).
Run("dotnet", "run", "scripts/consumer.cs", "--", "--feed", "local");
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
