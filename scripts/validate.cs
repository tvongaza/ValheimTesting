// Local test-pyramid layers only; never launches Valheim. Run bootstrap-cli.cs first.
//
//   dotnet run scripts/validate.cs
//   dotnet run scripts/validate.cs -- --skip-ci-shell   CI only: shell integration runs in its own job
//   dotnet run scripts/validate.cs -- --only-ci-shell   CI only: run that shard with the same data-root guard
//   dotnet run scripts/validate.cs -- --ci-main-tests  CI only: Windows toolkit tests excluding measured slow methods
//   dotnet run scripts/validate.cs -- --ci-timed 1|2   CI only: one balanced slow-method shard
//   dotnet run scripts/validate.cs -- --ci-build-only  CI only: Windows packaging and other test layers
//   dotnet run scripts/validate.cs -- --limits-probe PROJECT RESULTS [FILTER]  tests of the time limits only: one test command
//   sh scripts/validate.sh --restricted-sandbox  Selected no-host tests only; exits 4 (incomplete)
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
// flight, and the process tree is killed: validation fails. VT_VALIDATE_SOFT_LIMIT_SECONDS and VT_VALIDATE_HARD_LIMIT_SECONDS scale
// both down, for the tests of this mechanism (ValidationLimitsTests) only.
//
// VT_VALIDATE_HEARTBEAT_SECONDS=N (the macOS CI job sets 60; #736 investigation) adds a "heartbeat:" line every N seconds that names the
// running command and the machine's processes, free disk and, on macOS, free memory level.
//
// No command may create, change or remove anything in ValheimTesting's own folder on this machine (the run journal, host lock,
// leases, runs and extracted bundles of the user running validation): tests use temporary folders (#411). Each command is
// followed by a comparison with the folder as validation found it, and the first command that changed it fails, naming what.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;

string root = FindRoot();
bool skipCiShell = args.SequenceEqual(["--skip-ci-shell"]);
bool onlyCiShell = args.SequenceEqual(["--only-ci-shell"]);
bool ciMainTests = args.SequenceEqual(["--ci-main-tests"]);
bool ciBuildOnly = args.SequenceEqual(["--ci-build-only"]);
bool restrictedSandbox = args.SequenceEqual(["--restricted-sandbox"]);
bool limitsProbe = args is ["--limits-probe", _, _] or ["--limits-probe", _, _, _];
int ciTimed = args is ["--ci-timed", "1"] ? 1 : args is ["--ci-timed", "2"] ? 2 : 0;
if (args.Length != 0 && !skipCiShell && !onlyCiShell && !ciMainTests && !ciBuildOnly && !restrictedSandbox && !limitsProbe && ciTimed == 0)
    throw new ArgumentException("usage: dotnet run scripts/validate.cs [--skip-ci-shell|--only-ci-shell|--ci-main-tests|--ci-timed 1|2|--ci-build-only|--restricted-sandbox|--limits-probe PROJECT RESULTS [FILTER]]");
// A probe writes beside its caller's choice, never over the results of the validation that runs the probing test.
string results = limitsProbe ? Path.GetFullPath(args[2]) : Path.Combine(root, "artifacts", "validate");
Directory.CreateDirectory(results);
string transcript = Path.Combine(results, "validate.log");
File.WriteAllText(transcript, "");
var started = Stopwatch.StartNew();
var noteLock = new object();
string running = "";
string dataRoot = DataRoot();
var dataBefore = Snapshot(dataRoot);
var softLimit = TimeSpan.FromSeconds(LimitSeconds("VT_VALIDATE_SOFT_LIMIT_SECONDS", 5 * 60));
var hardLimit = TimeSpan.FromSeconds(LimitSeconds("VT_VALIDATE_HARD_LIMIT_SECONDS", 10 * 60));
if (hardLimit <= softLimit) throw new InvalidOperationException("The hard limit of a test command must be longer than its soft limit.");
// #736 investigation, remove it when that issue closes: the macOS CI job runs with VT_VALIDATE_HEARTBEAT_SECONDS=60. One line each
// period names the running command and the machine's state; it reaches GitHub in the job log while the job runs, so the last line
// before a lost runner says which command was running and whether the machine was short of memory, disk or processes. Lines that
// stop while the step is still running mean the whole machine stopped, not a test.
double heartbeatSeconds = Environment.GetEnvironmentVariable("VT_VALIDATE_HEARTBEAT_SECONDS") is { Length: > 0 }
    ? Math.Min(LimitSeconds("VT_VALIDATE_HEARTBEAT_SECONDS", 60), 3600) : 0;
using var heartbeat = heartbeatSeconds > 0
    ? new System.Threading.Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(heartbeatSeconds), TimeSpan.FromSeconds(heartbeatSeconds))
    : null;

if (limitsProbe)
{
    Test(args[1], args.Length == 4 ? ["--filter", args[3]] : []);
    return 0;
}

if (restrictedSandbox)
{
    Note("RESTRICTED SANDBOX: running only selected no-host tests. This cannot establish full validation.");
    var testHostProbe = new TcpListener(IPAddress.Loopback, 0);
    try
    {
        testHostProbe.Start();
    }
    catch (SocketException ex)
    {
        Note($"UNAVAILABLE: local test-host socket: {ex.Message}. The .NET test runner needs a localhost socket; run validation in CI or an authorized shell.");
        Note("INCOMPLETE: no tests ran in this restricted sandbox.");
        return 3;
    }
    finally
    {
        testHostProbe.Stop();
    }
    Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "--filter",
        "FullyQualifiedName~GridDumpTerrainTests|FullyQualifiedName~LayeredDumpTerrainTests", "--",
        "RunConfiguration.TreatNoTestsAsError=true");
    Test("tests/Valheim.Testing.Doubles.Tests/Valheim.Testing.Doubles.Tests.csproj");
    Test("tests/Valheim.Testing.Bindings.Tests/Valheim.Testing.Bindings.Tests.csproj");
    Note("INCOMPLETE: process, port, recovery, shell, package and consumer checks were not run. Full validation must pass in CI or an authorized shell.");
    return 4;
}

if (onlyCiShell)
{
    Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "--filter", "Category=CiShell", "--", "RunConfiguration.TreatNoTestsAsError=true");
    Note("Local-shell integration validation passed; the data-root guard found no changes.");
    return 0;
}

// The timing manifest is an optimisation, not an allowlist. A newly added test stays in ci-main until its measured
// method is added here. Every listed method appears in exactly one timed shard, and ci-main-tests excludes that list.
using var timingDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "ci-test-timings.json")));
var timedMethods = timingDocument.RootElement.EnumerateArray()
    .Select(item => new TimedMethod(item.GetProperty("Name").GetString() ?? "", item.GetProperty("Seconds").GetDouble())).ToArray();
if (timedMethods.Length == 0 || timedMethods.Any(method => method.Seconds <= 0 ||
    !method.Name.Contains('.') || !System.Text.RegularExpressions.Regex.IsMatch(method.Name, @"^[A-Za-z_][A-Za-z_0-9.]*$")) ||
    timedMethods.Select(method => method.Name).Distinct(StringComparer.Ordinal).Count() != timedMethods.Length)
    throw new InvalidOperationException("CI test timing manifest has an invalid or duplicate method.");
var timedShards = timedMethods.GroupBy(method => method.Name[..method.Name.LastIndexOf('.')])
    .SelectMany(group =>
    {
        double[] loads = [0, 0];
        return group.OrderByDescending(method => method.Seconds).ThenBy(method => method.Name, StringComparer.Ordinal)
            .Select(method =>
            {
                int shard = loads[0] <= loads[1] ? 0 : 1;
                loads[shard] += method.Seconds;
                return (method.Name, Shard: shard + 1);
            });
    }).ToArray();
if (ciTimed != 0)
{
    var selected = timedShards.Where(method => method.Shard == ciTimed).Select(method => method.Name).ToArray();
    if (selected.Length == 0) throw new InvalidOperationException($"CI timed shard {ciTimed} has no methods.");
    Note($"CI timed shard {ciTimed}: {selected.Length} methods; measured total {timedMethods.Where(method => selected.Contains(method.Name)).Sum(method => method.Seconds):F1} test-seconds.");
    Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "--filter",
        string.Join('|', selected.Select(name => "FullyQualifiedName=" + name)), "--", "RunConfiguration.TreatNoTestsAsError=true");
    Note($"CI timed shard {ciTimed} passed; the data-root guard found no changes.");
    return 0;
}

// CI runs the slower local-shell integration tests in their own three-OS job. Plain local validation still runs all tests.
if (ciMainTests)
{
    Test("tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "--filter",
        "Category!=CiShell&" + string.Join('&', timedMethods.Select(method => "FullyQualifiedName!=" + method.Name)),
        "--", "RunConfiguration.TreatNoTestsAsError=true");
    Note("CI main toolkit tests passed; the data-root guard found no changes.");
    return 0;
}
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
Note(skipCiShell || ciBuildOnly ? "CI validation passed; toolkit shards run in separate jobs." : "Local validation passed.");
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

// A test run's results go under artifacts/validate. Keep a TRX for ordinary assertion failures too: console output can be
// lost when an agent shell yields. When a host crashes or is stopped at the hard limit, the blame sequence names tests that
// started but did not complete. Old results are removed first: a stale sequence would name tests of an earlier run.
void Test(string project, params string[] extra)
{
    string output = Path.Combine(results, Path.GetFileNameWithoutExtension(project));
    if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
    try
    {
        var arguments = new List<string> { "test", project, "-c", "Release", "-m:1", "--blame-hang-timeout", "5m",
            "--blame-hang-dump-type", "none", "--results-directory", output };
        arguments.AddRange(["--logger", Environment.GetEnvironmentVariable("VT_CI_PROFILE") == "1"
            ? "trx;LogFileName=timings.trx" : "trx;LogFileName=results.trx"]);
        arguments.AddRange(extra);
        Execute("dotnet", arguments.ToArray(), output);
    }
    catch
    {
        if (Directory.Exists(output))
            foreach (string sequence in Directory.GetFiles(output, "Sequence_*.xml", SearchOption.AllDirectories))
            {
                XElement root;
                // A command killed with its whole tree can leave a half-written sequence; the original failure must still surface.
                try { root = XDocument.Load(sequence).Root!; }
                catch (System.Xml.XmlException) { Note($"unreadable blame sequence: {Path.GetRelativePath(results, sequence)}"); continue; }
                foreach (var test in root.Elements("Test").Where(t => (string?)t.Attribute("Completed") != "True"))
                    Note($"started and not completed: {(string?)test.Attribute("DisplayName")} ({Path.GetRelativePath(results, sequence)})");
            }
        throw;
    }
}

// Full validation invokes more file apps after its own entrypoint. Give those builds and runs the same SDK-cache fallback.
void BuildScript(string script)
{
    if (OperatingSystem.IsWindows()) Run("dotnet", "build", script);
    else Run("sh", "scripts/run-file-app.sh", "--build-only", script);
}

void RunScript(string script, params string[] arguments)
{
    if (OperatingSystem.IsWindows()) Run("dotnet", ["run", script, "--", .. arguments]);
    else Run("sh", ["scripts/run-file-app.sh", script, .. arguments]);
}

string CaptureScript(string script, params string[] arguments) =>
    OperatingSystem.IsWindows()
        ? Capture("dotnet", ["run", script, "--", .. arguments])
        : Capture("sh", ["scripts/run-file-app.sh", script, .. arguments]);

void Run(string file, params string[] arguments) => Execute(file, arguments, null);

// Runs a command and fails on a non-zero exit. A test command (testResults: where its results go) also has the soft and hard
// limits of #719; this is the one place that enforces them.
void Execute(string file, string[] arguments, string? testResults)
{
    string command = file + " " + string.Join(' ', arguments);
    Note("start: " + command);
    running = command;
    var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = root };
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
            Note($"could not stop the test host ({ex.Message}); killing the process tree without a blame sequence");
        }
        if (stopped == 0 || !process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            if (stopped != 0) Note("the command did not exit within 60 s of its test host being stopped; killing its process tree");
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* It exited between the check and the kill. */ }
            process.WaitForExit();
        }
        Note($"FAILED: over the hard limit of {Elapsed(hardLimit)} after {Elapsed(clock.Elapsed)}: {command}");
        throw new InvalidOperationException($"Validation command passed its {Elapsed(hardLimit)} hard limit and was stopped: {command}");
    }
    if (testResults != null && clock.Elapsed > softLimit) SoftLimitWarning(command, clock.Elapsed, testResults);
    if (process.ExitCode != 0)
    {
        Note($"FAILED ({process.ExitCode}) after {Elapsed(clock.Elapsed)}: {command}");
        throw new InvalidOperationException("Validation command failed with exit code " + process.ExitCode);
    }
    Note($"done in {Elapsed(clock.Elapsed)}: {command}");
    running = "";
    DataRootUnchanged(command);
}

// The warning of a test command that ended past the soft limit: the command, its duration and its slowest tests from this
// run's TRX (the logger writes it when the run ends, so it is not available while the command is still going).
void SoftLimitWarning(string command, TimeSpan elapsed, string testResults)
{
    string slowest = "the slowest tests are unknown: this run wrote no TRX";
    // Test removed the folder before the run, so any TRX in it is this run's.
    string? trx = Directory.Exists(testResults) ? Directory.GetFiles(testResults, "*.trx", SearchOption.AllDirectories).FirstOrDefault() : null;
    if (trx != null)
    {
        XDocument document;
        try { document = XDocument.Load(trx); }
        catch (System.Xml.XmlException) { document = new XDocument(new XElement("TestRun")); slowest = "the slowest tests are unknown: the TRX is unreadable"; }
        var tests = document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult")
            .Select(e => (Name: (string?)e.Attribute("testName") ?? "?", Duration: TimeSpan.TryParse((string?)e.Attribute("duration"), out var d) ? d : TimeSpan.Zero))
            .OrderByDescending(t => t.Duration).Take(5).ToArray();
        if (tests.Length > 0) slowest = "slowest tests: " + string.Join("; ", tests.Select(t => $"{t.Name} {t.Duration.TotalSeconds:F1}s"));
    }
    string message = $"SOFT LIMIT: {command} took {Elapsed(elapsed)}, over the {Elapsed(softLimit)} soft limit; split, refactor or speed it up; {slowest}";
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
            "install it with winget install --id Microsoft.PowerShell --source winget (docs/platforms.md#host-shells).", error);
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
    running = command;
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
    running = "";
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

// A timer thread must not end the process: a failing line is dropped.
void Heartbeat()
{
    string command = running; // Read once: the main thread writes it.
    try { Note($"heartbeat: {(command.Length == 0 ? "between commands" : "running " + command)}; {MachineSummary()}"); }
    catch (Exception) { /* The next period tries again. */ }
}

// The heartbeat's view of the machine: processes, free space on the drive holding the checkout and, on macOS, the free memory level
// the system reports (kern.memorystatus_level, a percentage). Never throws and never waits long: a failing or stalled probe is a
// word in the line.
string MachineSummary()
{
    string text = "processes " + Try(() =>
    {
        var all = Process.GetProcesses();
        foreach (var one in all) one.Dispose();
        return all.Length.ToString();
    });
    text += "; disk free " + Try(() => (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace / (1L << 30)).ToString() + " GiB");
    if (OperatingSystem.IsMacOS())
        text += "; free memory " + Try(() =>
        {
            using var probe = Process.Start(new ProcessStartInfo("sysctl", ["-n", "kern.memorystatus_level"]) { RedirectStandardOutput = true, UseShellExecute = false })!;
            var level = probe.StandardOutput.ReadToEndAsync();
            if (level.Wait(10_000)) return level.Result.Trim() + "%";
            try { probe.Kill(); } catch (InvalidOperationException) { /* It ended meanwhile. */ }
            return "unknown (sysctl did not answer in 10 s)";
        });
    return text;
    static string Try(Func<string> read)
    {
        try { return read(); } catch (Exception ex) { return "unknown (" + ex.GetType().Name + ")"; }
    }
}

static string Elapsed(TimeSpan span) => $"{(int)span.TotalMinutes}m{span.Seconds:00}s";

// A limit in seconds, from its override variable when set; those exist for the tests of the limits themselves.
static double LimitSeconds(string variable, double seconds) =>
    Environment.GetEnvironmentVariable(variable) is { Length: > 0 } text
        ? double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0
            ? value : throw new InvalidOperationException($"{variable} must be a positive number of seconds, not '{text}'.")
        : seconds;

file sealed record TimedMethod(string Name, double Seconds);
