// Local test-pyramid layers only; never launches Valheim. Run bootstrap-cli.cs first.
//
//   dotnet run scripts/validate.cs
//   dotnet run scripts/validate.cs -- --skip-ci-shell   CI only: shell integration runs in its own job
//   dotnet run scripts/validate.cs -- --only-ci-shell   CI only: run that shard with the same data-root guard
//   dotnet run scripts/validate.cs -- --ci-toolkit-tests CI only (Windows, macOS): the whole non-shell toolkit suite
//   dotnet run scripts/validate.cs -- --ci-main-tests  CI only (Windows, macOS): toolkit tests excluding this OS's measured slow methods
//   dotnet run scripts/validate.cs -- --ci-timed 1|2|3 CI only (Windows, macOS): one balanced slow-method shard of this OS
//   dotnet run scripts/validate.cs -- --ci-build-only  CI only (Windows, macOS): packaging and other test layers
//   dotnet run scripts/validate.cs -- --test-project ROOT PROJECT RESULTS [FILTER]  CI only: one project's dotnet test command
//   ... --test-project ROOT PROJECT RESULTS --target TARGET [ARGUMENTS]  CI only: one test-running build target
//   ... --split  with repository validation or plain --test-project above: this OS's share of classified tests (#735, below)
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
// Every test command has two time limits (#719), enforced in Execute and nowhere else. Past the soft limit (5 minutes) it keeps
// going and, when it ends, a warning names the command, its duration and its slowest tests (a ::warning:: annotation in CI): the
// signal to split or speed it up. Past the hard limit (10 minutes) its test host is stopped, so the blame sequence names the tests in
// flight. When a host was stopped, the test command then has 30 seconds to exit and write its completed results before its tree is
// killed; with no stopped host, its tree is killed at once. Validation fails either way. VT_VALIDATE_SOFT_LIMIT_SECONDS and
// VT_VALIDATE_HARD_LIMIT_SECONDS scale both limits down, for the tests of this mechanism (ValidationLimitsTests) only.
// A RunTestsOnBothFrameworks hard limit during the net48 xunit console leg cannot name tests: xunit.console.exe is not a testhost
// on Windows or under Mono, so there is no blame collector to flush.
//
// --split is the OS-aware split of a pull request's tests (#735), decided in CiSplitFilter and nowhere else. A test class carries at
// most one [Trait("Os", ...)]: "neutral" runs on Linux only, "windows" on Windows, "unix" on Linux and macOS, "macos" on macOS; a
// class without one is OS-sensitive and runs on every OS. Without --split (pushes to main, manual dispatches, local validation)
// every OS runs every test. OsSplitTests guards which classes may be neutral (CONTRIBUTING.md states the rule).
//
// No command may create, change or remove anything in ValheimTesting's own folder on this machine (the run journal, host lock,
// leases, runs and extracted bundles of the user running validation): tests use temporary folders (#411). Each command is
// followed by a comparison with the folder as validation found it, and the first command that changed it fails, naming what.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;

ValidationInvocation invocation = ValidationInvocation.Parse(args);
bool split = invocation.Split;
string[] mode = invocation.Mode;
bool skipCiShell = invocation.SkipCiShell;
bool onlyCiShell = invocation.OnlyCiShell;
bool ciToolkitTests = invocation.CiToolkitTests;
bool ciMainTests = invocation.CiMainTests;
bool ciBuildOnly = invocation.CiBuildOnly;
bool testProjectTest = invocation.TestProjectTest;
bool testProjectTarget = invocation.TestProjectTarget;
bool testProject = invocation.TestProject;
int ciTimed = invocation.CiTimed;
string root = testProject ? mode[1] : FindRoot();
string workingDirectory = testProject ? Path.GetDirectoryName(mode[2])! : root;
string operatingSystem = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "";
CiSplitFilter? splitSelection = split ? CiSplitFilter.For(operatingSystem) : null;
string? splitFilter = splitSelection?.Expression;
// A one-project run writes beside its caller's choice, never over another validation's results.
string results = testProject ? mode[3] : Path.Combine(root, "artifacts", "validate");
Directory.CreateDirectory(results);
string transcript = Path.Combine(results, "validate.log");
File.WriteAllText(transcript, "");
var started = Stopwatch.StartNew();
var noteLock = new object();
string dataRoot = DataRoot();
var dataBefore = Snapshot(dataRoot);
var softLimit = TimeSpan.FromSeconds(LimitSeconds("VT_VALIDATE_SOFT_LIMIT_SECONDS", 5 * 60));
var hardLimit = TimeSpan.FromSeconds(LimitSeconds("VT_VALIDATE_HARD_LIMIT_SECONDS", 10 * 60));
const int hardLimitGracePeriodSeconds = 30;
if (hardLimit <= softLimit) throw new InvalidOperationException("The hard limit of a test command must be longer than its soft limit.");
if (splitFilter != null) Note($"OS-aware split (#735): classified test commands here select {splitFilter}; repository validation's other test projects run whole");

if (testProject)
{
    if (testProjectTarget) TestTarget(mode[2], mode[5], mode[6..]);
    else Test(mode[2], mode.Length == 5 ? ["--filter", mode[4]] : []);
    return 0;
}

if (onlyCiShell)
{
    Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "--filter", "Category=CiShell", "--", "RunConfiguration.TreatNoTestsAsError=true");
    Note("Local-shell integration validation passed; the data-root guard found no changes.");
    return 0;
}

// The timing manifest is the one owner of the shards of both OSes (#725). It is an optimisation, not an allowlist: a newly added test
// stays in ci-main until its measured method is added here. Each entry lists the measured seconds of the method per OS ("Windows",
// "macOS"; an OS that does not name the method found it cheap). On an OS, the timed methods are those with seconds there: every one
// appears in exactly one of that OS's timed shards, balanced by that OS's seconds, and ci-main-tests excludes exactly that list.
using var timingDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "ci-test-timings.json")));
string timingOs = operatingSystem is "Windows" or "macOS" ? operatingSystem : "";
var manifest = timingDocument.RootElement.EnumerateArray()
    .Select(item => (Name: item.GetProperty("Name").GetString() ?? "",
        Seconds: item.GetProperty("Seconds").EnumerateObject().ToDictionary(os => os.Name, os => os.Value.GetDouble()))).ToArray();
if (manifest.Length == 0 || manifest.Any(method => method.Seconds.Count == 0 ||
    method.Seconds.Any(os => os.Key is not ("Windows" or "macOS") || os.Value <= 0) ||
    !method.Name.Contains('.') || !System.Text.RegularExpressions.Regex.IsMatch(method.Name, @"^[A-Za-z_][A-Za-z_0-9.]*$")) ||
    manifest.Select(method => method.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Length)
    throw new InvalidOperationException("CI test timing manifest has an invalid or duplicate method.");
if ((ciTimed != 0 || ciMainTests || ciToolkitTests) && timingOs == "")
    throw new PlatformNotSupportedException("--ci-toolkit-tests, --ci-main-tests and --ci-timed know Windows and macOS only.");
var timedMethods = manifest.Where(method => method.Seconds.ContainsKey(timingOs))
    .Select(method => new CiTimedMethod(method.Name, method.Seconds[timingOs])).ToArray();
var timedShards = CiToolkitShards.Assign(timedMethods, ValidationInvocation.TimedShardCount);
if (ciTimed != 0)
{
    var selected = timedShards.Where(method => method.Shard == ciTimed).Select(method => method.Name).ToArray();
    if (selected.Length == 0) throw new InvalidOperationException($"CI timed shard {ciTimed} has no methods on {timingOs}.");
    Note($"CI timed shard {ciTimed}: {selected.Length} methods; measured total {timedMethods.Where(method => selected.Contains(method.Name)).Sum(method => method.Seconds):F1} test-seconds.");
    Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "--filter",
        string.Join('|', selected.Select(name => "FullyQualifiedName=" + name)), "--", "RunConfiguration.TreatNoTestsAsError=true");
    Note($"CI timed shard {ciTimed} passed; the data-root guard found no changes.");
    return 0;
}

// A pull request's macOS toolkit share runs in one job (#777). It uses the ci-main path with an empty timed exclusion
// list, so it is the union of ci-main and every timed shard by construction. --split then limits it to macOS's classes.
if (ciToolkitTests || ciMainTests)
{
    CiToolkitFilter selection = CiToolkitShards.Filter(invocation, timedMethods);
    Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "--filter", selection.Expression,
        "--", "RunConfiguration.TreatNoTestsAsError=true");
    Note(ciToolkitTests ? "CI whole toolkit share passed; the data-root guard found no changes."
        : "CI main toolkit tests passed; the data-root guard found no changes.");
    return 0;
}
// CI runs the slower local-shell integration tests in their own three-OS job. Plain local validation still runs all tests.
if (!ciBuildOnly)
{
    if (skipCiShell) Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "--filter", "Category!=CiShell");
    else Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj");
}
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
    BuildScript(script);
// The full-life-cycle example's test project; its game-side mod and adapter (and the suite's) need a game install and build
// elsewhere. The native session tests (trait Category=Native) launch the game when a private session.json sits beside their
// project (the example's skip without one, the native acceptance suite's fail), so validation leaves them out.
Test("examples/FullLifecycle/ExampleMod.Tests/ExampleMod.Tests.csproj", "--filter", "Category!=Native");
// The native acceptance suite's tests against scripted fakes.
Test("tests/Valheim.Testing.NativeAcceptance.Tests/Valheim.Testing.NativeAcceptance.Tests.csproj", "--filter", "Category!=Native");
Run("dotnet", "run", "--project", "examples/SharedWorld", "-c", "Release", "--no-build");
// Every package packs as <Version>-candidate.<hash of the package inputs> (pins.cs candidate, one owner of the identity):
// different inputs never share a version in .packages. Packs of these packages under any other version are removed first; a
// complete set already packed under this identity is kept rather than packed again, so one identity has one set of bytes.
string[] packed = Directory.GetDirectories(Path.Combine(root, "src"), "Valheim.Testing*", SearchOption.TopDirectoryOnly)
    .Select(Path.GetFileName).OfType<string>()
    .Where(id => File.Exists(Path.Combine(root, "src", id, id + ".csproj"))).Order(StringComparer.Ordinal).ToArray();
string candidate = CaptureScript("scripts/pins.cs", "candidate").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
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
    // PackTool includes everything under PublishDir, even files from an earlier run. Clear only the two generated tool
    // publish directories before the solution pack; other build outputs and the reviewed feed stay intact (#523).
    RunScript("scripts/tool-pack.cs", "clean");
    Run("dotnet", "pack", Solution("packages", packed.Select(name => $"src/{name}/{name}.csproj")),
        "-c", "Release", "-nodeReuse:false", "-o", feed, "-p:ValheimTestingCandidate=" + candidate);
}
RunScript("scripts/tool-pack.cs", "verify", feed);
RunScript("scripts/package-audit.cs", "--directory", feed, "--candidate");
// A mod's view of what was just packed: outside this checkout, the candidate packages only from .packages and byte-identical
// to it, the Cli from .packages too (its pin may not be published yet), every other package from NuGet.org.
RunScript("scripts/consumer.cs", "--feed", "local", "--candidate", candidate);
Note(skipCiShell || ciBuildOnly ? "CI validation passed; toolkit shards run in separate jobs."
    : split ? "Validation passed for this OS's share of the toolkit tests (--split); the other OSes run the rest." : "Local validation passed.");
return 0;

static string ScriptPath([CallerFilePath] string path = "") => path;

// The repository root holds cli-dependency.json. Search upward from the working directory first: CI path mapping can rewrite
// the compile-time source path CallerFilePath reports. Newer SDKs start a file-based app in its own folder, which is inside the
// repository for scripts/validate.cs; a copied one-project runner lives outside it, so it receives its root explicitly above.
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

// A validation test run's results go under artifacts/validate; a one-project mode uses its caller's results folder. Keep a TRX
// for ordinary assertion failures too: console output can be lost when an agent shell yields. When a host crashes or is stopped
// at the hard limit, the blame sequence names tests that started but did not complete. Old results are removed first: a stale
// sequence would name tests of an earlier run.
void Test(string project, params string[] extra)
{
    // The assembly-guard test launches this deliberately failing child with --no-build. Build it here, outside the
    // parent's bin/Release tree, so the test never mutates or rebuilds its own loaded assembly (#800).
    if (Path.GetFileName(project) == "Valheim.Testing.Tests.csproj")
        Run("dotnet", "build", Path.Combine(root, "tests", "Valheim.Testing.StandInProcessLeakProbe",
                "Valheim.Testing.StandInProcessLeakProbe.csproj"),
            "-c", "Release", "-m:1");
    string output = Path.Combine(results, Path.GetFileNameWithoutExtension(project));
    var arguments = new List<string> { "test", project, "-c", "Release", "-m:1", "--blame-hang-timeout", "5m",
        "--blame-hang-dump-type", "none", "--results-directory", output };
    arguments.AddRange(["--logger", Environment.GetEnvironmentVariable("VT_CI_PROFILE") == "1"
        ? "trx;LogFileName=timings.trx" : "trx;LogFileName=results.trx"]);
    arguments.AddRange(Split(project, extra));
    TestCommand(output, arguments.ToArray());
}

// Some projects own a test-running build target (tools/test-runners/Valheim.TestRunners.targets). Give its modern-framework
// dotnet test leg the same TRX and blame settings as a direct run, then put the outer build under the same limits too.
void TestTarget(string project, string target, params string[] extra)
{
    string output = Path.Combine(results, Path.GetFileNameWithoutExtension(project));
    string[] arguments = ["build", project, "-m:1", "-t:" + target, .. extra,
        "-p:VSTestLogger=trx%3BLogFileName=results.trx", "-p:VSTestResultsDirectory=" + output,
        "-p:VSTestBlame=true", "-p:VSTestBlameHang=true", "-p:VSTestBlameHangTimeout=5m", "-p:VSTestBlameHangDumpType=none"];
    TestCommand(output, arguments, Net48XunitConsoleCouldRun(target, extra) ? "net48 xunit console leg" : null);
}

void TestCommand(string output, string[] arguments, string? noTestNamesReason = null)
{
    if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
    try { Execute("dotnet", arguments, output, noTestNamesReason); }
    catch
    {
        foreach (string line in ValidationLimitReports.IncompleteTests(output, results)) Note(line);
        throw;
    }
}

bool Net48XunitConsoleCouldRun(string target, string[] extra)
{
    if (target != "RunTestsOnBothFrameworks") return false;
    string? framework = extra.Select((argument, index) => (argument, index))
        .Where(item => (item.argument is "-f" or "--framework") && item.index + 1 < extra.Length)
        .Select(item => extra[item.index + 1]).LastOrDefault();
    string? property = extra.LastOrDefault(argument => argument.StartsWith("-p:TargetFramework=", StringComparison.OrdinalIgnoreCase));
    framework ??= property?.Split('=', 2)[1];
    return framework == null || framework.StartsWith("net4", StringComparison.OrdinalIgnoreCase);
}

// The tests of this OS's share under --split (#735): the test command's own filter, if any, and the split's. Repository validation
// applies it to the toolkit project; plain --test-project applies it to its named project (including ValidationLimitsTests' probe).
// Other repository projects run whole; the xunit adapter would match no test if the trait filter reached an unclassified project.
// A split command must run a test, so a filter that empties it fails rather than passes. Run settings after "--" stay last.
string[] Split(string project, string[] extra)
{
    if (splitFilter == null || !(testProjectTest || project == "tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj")) return extra;
    const string noTestsFail = "RunConfiguration.TreatNoTestsAsError=true";
    int filter = Array.IndexOf(extra, "--filter");
    int settings = Array.IndexOf(extra, "--");
    string[] selection = filter >= 0 ? [.. extra[..(filter + 1)], $"({extra[filter + 1]})&{splitFilter}", .. extra[(filter + 2)..]]
        : settings < 0 ? [.. extra, "--filter", splitFilter] : [.. extra[..settings], "--filter", splitFilter, .. extra[settings..]];
    return selection.Contains(noTestsFail) ? selection : selection.Contains("--") ? [.. selection, noTestsFail] : [.. selection, "--", noTestsFail];
}

void BuildScript(string script) => Run("dotnet", "build", script);

void RunScript(string script, params string[] arguments) => Run("dotnet", ["run", script, "--", .. arguments]);

string CaptureScript(string script, params string[] arguments) => Capture("dotnet", ["run", script, "--", .. arguments]);

void Run(string file, params string[] arguments) => Execute(file, arguments, null);

// Runs a command and fails on a non-zero exit. A test command (testResults: where its results go) also has the soft and hard
// limits of #719; this is the one place that enforces them.
void Execute(string file, string[] arguments, string? testResults, string? noTestNamesReason = null)
{
    string command = file + " " + string.Join(' ', arguments);
    Note("start: " + command);
    var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = workingDirectory };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
    var clock = Stopwatch.StartNew();
    bool overHardLimit = false;
    if (testResults == null) process.WaitForExit();
    else if (!process.WaitForExit(softLimit))
    {
        Note($"{Elapsed(clock.Elapsed)} into the {Elapsed(softLimit)} soft limit: still running; stopped at {Elapsed(hardLimit)}: {command}");
        overHardLimit = !process.WaitForExit(TimeSpan.FromTicks(Math.Max(0, (hardLimit - clock.Elapsed).Ticks)));
    }
    if (overHardLimit)
    {
        // Stopping the test host (not the whole tree) lets the blame collector, which outlives it, write the sequence of
        // tests that started and did not complete; Test names them. With no test host yet, the command is in a build or restore.
        Note($"HARD LIMIT: {command} is still running after {Elapsed(clock.Elapsed)} (hard limit {Elapsed(hardLimit)}); stopping it");
        int stopped = 0;
        try { stopped = StopTestHosts(process.Id); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or AggregateException or IOException)
        {
            Note($"could not stop the test host ({ex.Message}); continuing without a blame sequence");
        }
        bool cannotNameTests = stopped == 0 && noTestNamesReason != null;
        // No stopped host means MSBuild may still start another target-framework leg, so only a stopped host earns the grace.
        bool waitForCommandExit = ValidationLimitHandling.ShouldWaitForCommandExit(stopped);
        if (waitForCommandExit)
        {
            Note($"stopped {stopped} test host process(es); waiting up to 10 s for the blame sequence to flush");
            Note(WaitForReadableBlameSequence(testResults!, TimeSpan.FromSeconds(10))
                ? $"blame sequence flushed; waiting up to {hardLimitGracePeriodSeconds} s for the test command to exit"
                : $"no readable blame sequence after 10 s; waiting up to {hardLimitGracePeriodSeconds} s for the test command to exit");
        }
        else Note("no test host was stopped; killing the process tree without a grace period");
        var exitClock = Stopwatch.StartNew();
        if (waitForCommandExit && process.WaitForExit(TimeSpan.FromSeconds(hardLimitGracePeriodSeconds)))
            Note($"test command exited on its own after {Math.Ceiling(exitClock.Elapsed.TotalSeconds):F0} s");
        else
        {
            try
            {
                process.Kill(entireProcessTree: true);
                Note(waitForCommandExit
                    ? $"test command killed after the {hardLimitGracePeriodSeconds} s grace period"
                    : "test command killed without a grace period");
            }
            catch (InvalidOperationException)
            {
                Note($"test command exited during the kill after {Math.Ceiling(exitClock.Elapsed.TotalSeconds):F0} s");
            }
        }
        process.WaitForExit();
        string? unavailable = cannotNameTests ? noTestNamesReason : null;
        Note(ValidationLimitReports.HardLimitFailure(command, clock.Elapsed, hardLimit, unavailable));
        throw new InvalidOperationException($"Validation command passed its {Elapsed(hardLimit)} hard limit and was stopped: {command}" +
            (unavailable == null ? "" : $"; no test names: {unavailable}"));
    }
    if (testResults != null && clock.Elapsed > softLimit) SoftLimitWarning(command, clock.Elapsed, testResults);
    if (process.ExitCode != 0)
    {
        Note($"FAILED ({process.ExitCode}) after {Elapsed(clock.Elapsed)}: {command}");
        throw new InvalidOperationException("Validation command failed with exit code " + process.ExitCode);
    }
    Note($"done in {Elapsed(clock.Elapsed)}: {command}");
    DataRootUnchanged(command);
}

// Stopping only the host gives vstest's blame collector a bounded chance to finish its sequence and the command to write its TRX.
// With no stopped host the tree is killed immediately. This polls only read-only evidence; the stop and tree kill are each issued once.
static bool WaitForReadableBlameSequence(string testResults, TimeSpan timeout)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < timeout)
    {
        if (Directory.Exists(testResults))
            foreach (string sequence in Directory.GetFiles(testResults, "Sequence_*.xml", SearchOption.AllDirectories))
                try { _ = XDocument.Load(sequence); return true; }
                catch (Exception error) when (error is System.Xml.XmlException or IOException) { }
        Thread.Sleep(100);
    }
    return false;
}

// The warning of a test command that ended past the soft limit: the command, its duration and its slowest tests from this
// run's TRX (the logger writes it when the run ends, so it is not available while the command is still going).
void SoftLimitWarning(string command, TimeSpan elapsed, string testResults)
{
    string message = ValidationLimitReports.SoftLimit(command, elapsed, softLimit, testResults);
    Note(message);
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
    {
        // A workflow command takes one line; '%' and line ends are escaped as GitHub documents.
        Console.Out.WriteLine("::warning title=Test command over the soft limit::" +
            message.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A"));
        Console.Out.Flush();
    }
}

// Stops every test host that the process tree under rootPid runs and returns how many. The process table comes from
// ps (macOS, Linux) or pwsh's CIM on Windows (no wmic: newer Windows lacks it).
static int StopTestHosts(int rootPid)
{
    var table = new Dictionary<int, (int Parent, string Command)>();
    var start = OperatingSystem.IsWindows()
        ? new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command",
            "Get-CimInstance Win32_Process | ForEach-Object { '{0} {1} {2}' -f $_.ProcessId, $_.ParentProcessId, $_.CommandLine }"])
        : new ProcessStartInfo("ps", ["-ww", "-A", "-o", "pid=,ppid=,args="]);
    start.RedirectStandardOutput = true;
    start.UseShellExecute = false;
    Process? started;
    try { started = Process.Start(start); }
    catch (System.ComponentModel.Win32Exception error)
    {
        throw new InvalidOperationException($"Could not list processes with {start.FileName} ({error.Message}). On Windows that is PowerShell 7: " +
            "install it with winget install --id Microsoft.PowerShell --source winget (docs/reference/platforms.md#host-shells).", error);
    }
    using (var listing = started ?? throw new InvalidOperationException("Could not list processes."))
    {
        string text = listing.StandardOutput.ReadToEnd();
        listing.WaitForExit();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && int.TryParse(fields[0], out int pid) && int.TryParse(fields[1], out int parent))
                table[pid] = (parent, fields.Length == 3 ? fields[2] : "");
        }
    }
    var testHost = new System.Text.RegularExpressions.Regex(@"(^|[\s\\/""])testhost(\.net[0-9]+)?(\.x86)?(\.dll|\.exe)?(""|\s|$)");
    var below = new HashSet<int> { rootPid };
    for (bool grew = true; grew;)
    {
        grew = false;
        foreach (var (pid, entry) in table)
            if (below.Contains(entry.Parent) && below.Add(pid)) grew = true;
    }
    int stopped = 0;
    foreach (int pid in below.Where(pid => pid != rootPid && table.TryGetValue(pid, out var entry) && testHost.IsMatch(entry.Command)))
    {
        try { using var host = Process.GetProcessById(pid); host.Kill(entireProcessTree: true); stopped++; }
        catch (ArgumentException) { /* It exited already. */ }
        catch (InvalidOperationException) { /* So did this one. */ }
    }
    return stopped;
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
    lock (noteLock)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
        File.AppendAllText(transcript, line + Environment.NewLine);
    }
}

static string Elapsed(TimeSpan span) => $"{(int)span.TotalMinutes}m{span.Seconds:00}s";

// A limit in seconds, from its override variable when set; those exist for the tests of the limits themselves.
static double LimitSeconds(string variable, double seconds) =>
    Environment.GetEnvironmentVariable(variable) is { Length: > 0 } text
        ? double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0
            ? value : throw new InvalidOperationException($"{variable} must be a positive number of seconds, not '{text}'.")
        : seconds;
