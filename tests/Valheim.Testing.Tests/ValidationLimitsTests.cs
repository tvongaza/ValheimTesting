using Xunit;

// #719: every test command scripts/validate.cs starts has a soft limit (a warning) and a hard limit (stopped, failed, the tests in
// flight named). The process-level checks run the real script against one built fixture, including one hard-limit run; message
// formatting also uses deterministic fake TRX/blame files. It stays with the other local-shell integration tests.
[Trait("Category", "CiShell")]
public sealed class ValidationLimitsTests : IClassFixture<ValidationLimitsTests.ProbeProject>
{
    private readonly ProbeProject _probe;

    public ValidationLimitsTests(ProbeProject probe) => _probe = probe;

    [Fact]
    public async Task ARunPastTheSoftLimitWarnsWithItsSlowestTestsAndPasses()
    {
        var run = await _probe.Run("PassesAfterTheSoftLimit", soft: 3, hard: 300, githubActions: true);
        run.AssertSucceeded();
        Has(run, "SOFT LIMIT:");
        Has(run, "slowest tests: LimitsProbe.ProbeTests.PassesAfterTheSoftLimit");
        Has(run, "::warning title=Test command over the soft limit::SOFT LIMIT:");
        Lacks(run, "HARD LIMIT");
        Assert.Single(Directory.GetFiles(_probe.Results("PassesAfterTheSoftLimit"), "*.trx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ARunUnderTheSoftLimitHasNoWarningAndNoAnnotation()
    {
        var run = await _probe.Run("PassesQuickly", soft: null, hard: null, githubActions: true);
        run.AssertSucceeded();
        Lacks(run, "SOFT LIMIT");
        Lacks(run, "::warning");
    }

    [Fact]
    public async Task ARunPastTheHardLimitKeepsFinishedResultsAndNamesTheTestInFlight()
    {
        var run = await _probe.Run("HardLimitProbeTests", soft: 2, hard: 20, githubActions: false);

        Assert.NotEqual(0, run.ExitCode);
        Has(run, "HARD LIMIT:");
        Has(run, "stopped 1 test host process(es); waiting up to 10 s for the blame sequence to flush");
        Has(run, "blame sequence flushed; waiting up to 30 s for the test command to exit");
        Has(run, "test command exited on its own after");
        Lacks(run, "test command killed after");
        Has(run, "FAILED: over the hard limit");
        Has(run, "started and not completed: LimitsProbe.HardLimitProbeTests.NeverFinishes");
        Lacks(run, "::warning");
        Assert.Equal(new[] { "LimitsProbe.HardLimitProbeTests.FinishesBeforeTheHardLimit" }, _probe.Passed("HardLimitProbeTests", split: false));
    }

    [Fact]
    public void LimitReportsNameTestsFromFakeTrxAndBlameAndExplainTheXunitConsoleGap()
    {
        string results = _probe.FakeResults();
        string warning = ValidationLimitReports.SoftLimit("dotnet test probe", TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(3), results);
        Assert.Contains("SOFT LIMIT: dotnet test probe", warning, StringComparison.Ordinal);
        Assert.Contains("slowest tests: LimitsProbe.Slower 7.0s; LimitsProbe.Faster 1.0s", warning, StringComparison.Ordinal);
        Assert.Contains("started and not completed: LimitsProbe.InFlight", Assert.Single(ValidationLimitReports.IncompleteTests(results, _probe.FixtureDirectory)), StringComparison.Ordinal);
        Assert.EndsWith("; no test names: net48 xunit console leg",
            ValidationLimitReports.HardLimitFailure("dotnet build probe -t:RunTestsOnBothFrameworks", TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(10), "net48 xunit console leg"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoStoppedTestHostMeansTheCommandTreeIsKilledImmediately()
    {
        Assert.False(ValidationLimitHandling.ShouldWaitForCommandExit(stoppedTestHosts: 0));
        Assert.True(ValidationLimitHandling.ShouldWaitForCommandExit(stoppedTestHosts: 1));
    }

    // #735: --split runs neutral tests on Linux only and each OS-specific class on its OS; a class without the trait runs everywhere.
    // Without it, every OS runs every test (a push to main).
    [Fact]
    public async Task TheSplitRunsNeutralTestsOnLinuxOnlyAndOsSpecificTestsOnTheirOs()
    {
        string[] all = ["LimitsProbe.SplitMacos.Ran", "LimitsProbe.SplitNeutral.Ran", "LimitsProbe.SplitSensitive.Ran", "LimitsProbe.SplitUnix.Ran", "LimitsProbe.SplitWindows.Ran"];
        var full = await _probe.Run("LimitsProbe.Split", soft: null, hard: null, githubActions: false);
        full.AssertSucceeded();
        Lacks(full, "OS-aware split");
        Assert.Equal(all, _probe.Passed("LimitsProbe.Split", split: false));

        var split = await _probe.Run("LimitsProbe.Split", soft: null, hard: null, githubActions: false, split: true);
        split.AssertSucceeded();
        Has(split, "OS-aware split (#735)");
        Has(split, "RunConfiguration.TreatNoTestsAsError=true");
        string[] expected = OperatingSystem.IsWindows() ? ["LimitsProbe.SplitSensitive.Ran", "LimitsProbe.SplitWindows.Ran"]
            : OperatingSystem.IsMacOS() ? ["LimitsProbe.SplitMacos.Ran", "LimitsProbe.SplitSensitive.Ran", "LimitsProbe.SplitUnix.Ran"]
            : ["LimitsProbe.SplitNeutral.Ran", "LimitsProbe.SplitSensitive.Ran", "LimitsProbe.SplitUnix.Ran"];
        Assert.Equal(expected, _probe.Passed("LimitsProbe.Split", split: true));
    }

    [Fact]
    public async Task TheTargetFormRunsTheNamedBuildTargetThroughExecute()
    {
        BuildRun run = await _probe.RunTarget(soft: 0.01);

        run.AssertSucceeded();
        string command = $"dotnet build {_probe.Project} -m:1 -t:WriteMarker";
        Has(run, "start: " + command);
        Has(run, "SOFT LIMIT: " + command);
        Has(run, "slowest tests: LimitsProbe.Target.Fake");
        string properties = File.ReadAllText(_probe.TargetMarker);
        Assert.Contains("LogFileName=results.trx", properties, StringComparison.Ordinal);
        Assert.Contains("|true|true|5m|none|", properties, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_probe.TargetResults, "*.trx", SearchOption.AllDirectories));
        string transcript = File.ReadAllText(Path.Combine(_probe.TargetResults, "validate.log"));
        Assert.Contains("start: " + command, transcript, StringComparison.Ordinal);
        Assert.Contains("SOFT LIMIT: " + command, transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitIsAcceptedBeforeOrAfterTheModeAndOtherShapesReportUsage()
    {
        string root = FixtureProjects.RepositoryRoot();
        string results = _probe.Results("invalid");
        foreach (string[] arguments in new[]
        {
            new[] { "--split", "--ci-main-tests" }, new[] { "--ci-main-tests", "--split" },
        })
        {
            ValidationInvocation parsed = ValidationInvocation.Parse(arguments);
            Assert.True(parsed.Split);
            Assert.True(parsed.CiMainTests);
            Assert.False(parsed.CiToolkitTests);
        }
        foreach (string[] arguments in new[]
        {
            new[] { "--split", "--ci-toolkit-tests" }, new[] { "--ci-toolkit-tests", "--split" },
        })
        {
            ValidationInvocation parsed = ValidationInvocation.Parse(arguments);
            Assert.True(parsed.Split);
            Assert.True(parsed.CiToolkitTests);
            Assert.False(parsed.CiMainTests);
        }
        string[][] invalid =
        [
            ["--unknown", root, _probe.Project, results, "FullyQualifiedName~PassesQuickly"],
            ["--test-project", "relative-root", _probe.Project, results],
            ["--test-project", root, "relative-project", results],
            ["--test-project", root, _probe.Project, "relative-results"],
            ["--test-project", _probe.FixtureDirectory, _probe.Project, results],
            ["--test-project", root, _probe.Project, results, "--target"],
            ["--test-project", root, _probe.Project, results, "--target", ""],
            ["--test-project", root, _probe.Project, results, "FullyQualifiedName~PassesQuickly", "extra"],
            ["--test-project", root, _probe.Project, results, "--target", "WriteMarker", "--split"],
            ["--split", "--split", "--ci-build-only"],
        ];

        foreach (string[] arguments in invalid)
        {
            var error = Assert.Throws<ArgumentException>(() => ValidationInvocation.Parse(arguments));
            Assert.Contains("usage: dotnet run scripts/validate.cs", error.Message, StringComparison.Ordinal);
        }
    }

    private static void Has(BuildRun run, string text) =>
        Assert.True(run.Output.Contains(text, StringComparison.Ordinal), $"expected \"{text}\" in (exit {run.ExitCode})\n{run.Output}");

    private static void Lacks(BuildRun run, string text) =>
        Assert.False(run.Output.Contains(text, StringComparison.Ordinal), $"did not expect \"{text}\" in (exit {run.ExitCode})\n{run.Output}");

    /// <summary>A test project with a quick test, one slower than a scaled soft limit and one hard-limit hang, built once.</summary>
    public sealed class ProbeProject : IAsyncLifetime
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "vt-limits-" + Guid.NewGuid().ToString("N"));
        private string _project = "";
        private string _script = "";

        public string FixtureDirectory => _directory;
        public string Project => _project;
        public string TargetMarker => Path.Combine(Path.GetDirectoryName(_project)!, "target.marker");
        public string TargetResults => Path.Combine(_directory, "target-results");

        public async Task InitializeAsync()
        {
            string project = Path.Combine(_directory, "probe");
            Directory.CreateDirectory(project);
            _project = Path.Combine(project, "LimitsProbe.csproj");
            File.WriteAllText(_project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><IsPackable>false</IsPackable></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" /><PackageReference Include="xunit" Version="2.8.1" /><PackageReference Include="xunit.runner.visualstudio" Version="2.8.1" />
                  </ItemGroup>
                  <Target Name="WriteMarker">
                    <MakeDir Directories="$(VSTestResultsDirectory)" />
                    <WriteLinesToFile File="$(VSTestResultsDirectory)/target.trx" Lines="&lt;TestRun&gt;&lt;UnitTestResult testName='LimitsProbe.Target.Fake' duration='00:00:04' /&gt;&lt;/TestRun&gt;" Overwrite="true" />
                    <WriteLinesToFile File="$(MSBuildProjectDirectory)/target.marker" Lines="$(VSTestLogger)|$(VSTestBlame)|$(VSTestBlameHang)|$(VSTestBlameHangTimeout)|$(VSTestBlameHangDumpType)|$(VSTestResultsDirectory)" Overwrite="true" />
                  </Target>
                </Project>
                """);
            File.WriteAllText(Path.Combine(project, "ProbeTests.cs"), """
                using System;
                using System.Collections.Generic;
                using System.Linq;
                using System.Threading;
                using Xunit;
                using Xunit.Abstractions;
                using Xunit.Sdk;

                namespace LimitsProbe;

                public class ProbeTests
                {
                    [Fact] public void PassesQuickly() { }
                    [Fact] public void PassesAfterTheSoftLimit() => Thread.Sleep(TimeSpan.FromSeconds(6));
                }

                [TestCaseOrderer("LimitsProbe.MethodNameOrderer", "LimitsProbe")]
                public class HardLimitProbeTests
                {
                    [Fact] public void FinishesBeforeTheHardLimit() { }
                    [Fact] public void NeverFinishes() => Thread.Sleep(TimeSpan.FromMinutes(30));
                }

                public sealed class MethodNameOrderer : ITestCaseOrderer
                {
                    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
                        where TTestCase : ITestCase => testCases.OrderBy(test => test.TestMethod.Method.Name, StringComparer.Ordinal);
                }

                // The OS-aware split (#735): one class per Os trait value, and one without the trait.
                [Trait("Os", "neutral")] public class SplitNeutral { [Fact] public void Ran() { } }
                [Trait("Os", "windows")] public class SplitWindows { [Fact] public void Ran() { } }
                [Trait("Os", "unix")] public class SplitUnix { [Fact] public void Ran() { } }
                [Trait("Os", "macos")] public class SplitMacos { [Fact] public void Ran() { } }
                public class SplitSensitive { [Fact] public void Ran() { } }
                """);
            // A copy of the script in this fixture's own folder, so the validation that runs these tests keeps its own build of it
            // and two validations never share one copy. The probe receives the repository explicitly: a file-based app's working
            // directory is the SDK's choice (#766).
            _script = Path.Combine(_directory, "validate-probe", "validate.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(_script)!);
            File.Copy(Path.Combine(FixtureProjects.RepositoryRoot(), "scripts", "validate.cs"), _script, overwrite: true);
            File.Copy(Path.Combine(FixtureProjects.RepositoryRoot(), "scripts", "Shared", "ValidationSupport.cs"), Path.Combine(Path.GetDirectoryName(_script)!, "ValidationSupport.cs"), overwrite: true);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(_script)!, "Directory.Build.props"),
                "<Project><ItemGroup><Compile Include=\"ValidationSupport.cs\" /></ItemGroup></Project>");
            // Restore and compile now, so the scaled limits below measure the test run and not the first build. The project is
            // named by the very path validate.cs will pass to dotnet test (not found from the working directory, which a temp
            // folder behind a symbolic link such as macOS's /var spells differently): another spelling is another project to
            // NuGet, so the first test run restored again and the runner then failed to load xunit.abstractions (macOS CI).
            (await FixtureProjects.Dotnet(project, ["build", _project, "-c", "Release", "-m:1"], timeoutMinutes: 4)).AssertSucceeded();
        }

        public Task DisposeAsync()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* A killed test host may still be letting go of its folder. */ }
            return Task.CompletedTask;
        }

        /// <summary>validate.cs's one-project mode on one test; null limits keep the script's own.</summary>
        public Task<BuildRun> Run(string test, double? soft, double? hard, bool githubActions, bool split = false) =>
            FixtureProjects.Dotnet(FixtureProjects.RepositoryRoot(),
                ["run", _script, "--", "--test-project", FixtureProjects.RepositoryRoot(), _project, Results(test, split), "FullyQualifiedName~" + test, .. (split ? new[] { "--split" } : Array.Empty<string>())],
                new Dictionary<string, string?>
                {
                    ["VT_VALIDATE_SOFT_LIMIT_SECONDS"] = soft?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["VT_VALIDATE_HARD_LIMIT_SECONDS"] = hard?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["GITHUB_ACTIONS"] = githubActions ? "true" : null,
                    ["VT_CI_PROFILE"] = null,
                }, timeoutMinutes: 5);

        public Task<BuildRun> RunTarget(double soft)
        {
            File.Delete(TargetMarker);
            return FixtureProjects.Dotnet(FixtureProjects.RepositoryRoot(),
                ["run", _script, "--", "--test-project", FixtureProjects.RepositoryRoot(), _project, TargetResults, "--target", "WriteMarker"],
                new Dictionary<string, string?>(CleanEnvironment())
                {
                    ["VT_VALIDATE_SOFT_LIMIT_SECONDS"] = soft.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }, timeoutMinutes: 5);
        }

        /// <summary>The tests a run passed, by name, from the TRX validate.cs keeps for it.</summary>
        public string[] Passed(string test, bool split)
        {
            string trx = Assert.Single(Directory.GetFiles(Results(test, split), "*.trx", SearchOption.AllDirectories));
            return System.Xml.Linq.XDocument.Load(trx).Descendants().Where(e => e.Name.LocalName == "UnitTestResult" && (string?)e.Attribute("outcome") == "Passed")
                .Select(e => (string?)e.Attribute("testName") ?? "").Order(StringComparer.Ordinal).ToArray();
        }

        public string Results(string test, bool split = false) => Path.Combine(_directory, "results-" + test + (split ? "-split" : ""));

        public string FakeResults()
        {
            string results = Path.Combine(_directory, "fake-results");
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "fake.trx"),
                "<TestRun><UnitTestResult testName='LimitsProbe.Faster' duration='00:00:01' /><UnitTestResult testName='LimitsProbe.Slower' duration='00:00:07' /></TestRun>");
            File.WriteAllText(Path.Combine(results, "Sequence_fake.xml"),
                "<Sequence><Test DisplayName='LimitsProbe.Done' Completed='True' /><Test DisplayName='LimitsProbe.InFlight' Completed='False' /></Sequence>");
            return results;
        }

        private static Dictionary<string, string?> CleanEnvironment() => new()
        {
            ["VT_VALIDATE_SOFT_LIMIT_SECONDS"] = null,
            ["VT_VALIDATE_HARD_LIMIT_SECONDS"] = null,
            ["GITHUB_ACTIONS"] = null,
            ["VT_CI_PROFILE"] = null,
        };
    }
}
