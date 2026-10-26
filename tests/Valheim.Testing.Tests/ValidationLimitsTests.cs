using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;
using Valheim.Testing.Game;

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
    public async Task PreparationStopsBeforeTestEntryAndDoesNotReportStaleResults()
    {
        var probe = await _probe.RunPreparation("stall");
        Assert.NotEqual(0, probe.Run.ExitCode);
        Has(probe.Run, "PREPARATION LIMIT:");
        Has(probe.Run, "no test names: preparation ended before the test command started");
        Has(probe.Run, "cleanup: root and all known owned descendants exited");
        Lacks(probe.Run, "start: dotnet test");
        Lacks(probe.Run, "SOFT LIMIT");
        Lacks(probe.Run, "StalePreviousRun");
        Assert.True(probe.Ready, probe.Run.Output);
        Assert.InRange(probe.Elapsed.TotalSeconds, 4, 12);
        Assert.False(probe.TestEntered);
        Assert.False(probe.StaleResultsRemain);
        Assert.False(probe.OwnedProcessAliveBeforeHarnessCleanup, probe.Run.Output);
        Assert.NotEmpty(probe.ObservedOwned);
        using var graph = Evidence(probe, "observed-graph.json");
        using var child = Evidence(probe, "child.json");
        AssertOwnedChain(probe.ObservedOwned, graph.RootElement.GetProperty("RootPid").GetInt32(), child.RootElement.GetProperty("Pid").GetInt32());
        Assert.Empty(probe.AliveObservedPids);
        Assert.True(probe.UnrelatedSentinelSurvived);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("fail")]
    public async Task OrdinaryPreparationKeepsSuccessAndBuildFailure(string behavior)
    {
        var probe = await _probe.RunPreparation(behavior);
        Lacks(probe.Run, "PREPARATION LIMIT:");
        if (behavior == "fast")
        {
            probe.Run.AssertSucceeded();
            Assert.True(probe.TestEntered, probe.Run.Output);
        }
        else
        {
            Assert.NotEqual(0, probe.Run.ExitCode);
            Has(probe.Run, "PREPARATION_HELPER_EXIT_7");
            Has(probe.Run, "FAILED (1)"); // MSBuild reports its failed Exec as build exit 1, not helper exit 7.
            Assert.False(probe.TestEntered);
            Lacks(probe.Run, "start: dotnet test");
        }
        Assert.True(probe.UnrelatedSentinelSurvived);
    }

    [Theory]
    [InlineData("listing-hang")]
    [InlineData("listing-pipe")]
    [InlineData("listing-fail")]
    [InlineData("listing-root-exit")]
    public async Task AnUnavailableProcessTableKeepsThePreparationFailureAndBoundsShutdown(string behavior)
    {
        var probe = await _probe.RunPreparation(behavior);
        Assert.True(probe.Ready, probe.Run.Output);
        Assert.NotEqual(0, probe.Run.ExitCode);
        Assert.InRange(probe.Elapsed.TotalSeconds, 4, 12);
        Has(probe.Run, "PREPARATION LIMIT:");
        Has(probe.Run, "cleanup could not be verified");
        Has(probe.Run, "no readable blame sequence available");
        Lacks(probe.Run, "test host process(es); waiting");
        Lacks(probe.Run, "cleanup: root and all known owned descendants exited");
        Lacks(probe.Run, "start: dotnet test");
        Assert.False(probe.TestEntered);
        Assert.True(probe.UnrelatedSentinelSurvived);
        AssertListingBoundary(probe, behavior);
    }

    private static void AssertListingBoundary(ProbeProject.PreparationRun probe, string behavior)
    {
        // These assertions describe physical boundaries BEFORE the harness is allowed to clean the fixture.
        using var listing = Evidence(probe, "listing.json");
        using var listingReady = Evidence(probe, "listing.ready");
        int listingPid = listing.RootElement.GetProperty("Pid").GetInt32();
        Assert.True(listing.RootElement.GetProperty("StartUtcTicks").GetInt64() > 0);
        Assert.True(listingReady.RootElement.GetProperty("UtcTicks").GetInt64() > 0);
        if (behavior == "listing-root-exit")
        {
            using var snapshot = Evidence(probe, "listing-graph.json");
            using var exit = Evidence(probe, "root-exit.observed.json");
            using var returned = Evidence(probe, "graph.returned.json");
            using var admission = Evidence(probe, "admission.boundary.json");
            int rootPid = snapshot.RootElement.GetProperty("RootPid").GetInt32();
            long rootStart = snapshot.RootElement.GetProperty("RootStartUtcTicks").GetInt64();
            Assert.Equal(rootPid, exit.RootElement.GetProperty("Pid").GetInt32());
            Assert.Equal(rootStart, exit.RootElement.GetProperty("StartUtcTicks").GetInt64());
            Assert.True(exit.RootElement.GetProperty("ExitObserved").GetBoolean());
            Assert.True(snapshot.RootElement.GetProperty("CapturedUtcTicks").GetInt64() <= exit.RootElement.GetProperty("UtcTicks").GetInt64());
            Assert.True(exit.RootElement.GetProperty("UtcTicks").GetInt64() <= returned.RootElement.GetProperty("UtcTicks").GetInt64());
            Assert.True(snapshot.RootElement.GetProperty("Owned").GetArrayLength() >= 3);
            var graph = snapshot.RootElement.GetProperty("Owned").Deserialize<ProbeProject.ObservedProcess[]>()!;
            foreach (string name in new[] { "helper", "child" })
            {
                using var identity = Evidence(probe, name + ".json");
                Assert.Contains(graph, row => row.Pid == identity.RootElement.GetProperty("Pid").GetInt32() &&
                    row.StartUtcTicks == identity.RootElement.GetProperty("StartUtcTicks").GetInt64());
            }
            using var child = Evidence(probe, "child.json");
            AssertOwnedChain(graph, rootPid, child.RootElement.GetProperty("Pid").GetInt32());
            Assert.True(admission.RootElement.GetProperty("RootExited").GetBoolean());
            Assert.True(admission.RootElement.GetProperty("TableContainsRoot").GetBoolean());
            Assert.True(admission.RootElement.GetProperty("TableCount").GetInt32() >= 3);
            Assert.Equal(rootPid, admission.RootElement.GetProperty("RootPid").GetInt32());
            Assert.Equal(rootStart, admission.RootElement.GetProperty("RootStart").GetInt64());
            Has(probe.Run, "root exited or changed identity before ancestry was established");
            Assert.DoesNotContain(listingPid, probe.AliveRecordedPids);
        }
        else
        {
            using var boundary = Evidence(probe, "listing.boundary.json");
            Assert.Equal(listingPid, boundary.RootElement.GetProperty("Pid").GetInt32());
            Assert.Equal(listing.RootElement.GetProperty("StartUtcTicks").GetInt64(), boundary.RootElement.GetProperty("StartUtcTicks").GetInt64());
            Assert.True(listingReady.RootElement.GetProperty("UtcTicks").GetInt64() <= boundary.RootElement.GetProperty("UtcTicks").GetInt64());
            if (behavior == "listing-fail")
            {
                using var exit = Evidence(probe, "listing.exit.json");
                Assert.True(boundary.RootElement.GetProperty("HasExited").GetBoolean());
                Assert.True(boundary.RootElement.GetProperty("ReadCompleted").GetBoolean());
                Assert.Equal(7, boundary.RootElement.GetProperty("ExitCode").GetInt32());
                Assert.Equal(7, exit.RootElement.GetProperty("ExitCode").GetInt32());
                Has(probe.Run, "process listing failed with exit code 7");
            }
            else
            {
                // The live root or the inherited pipe must still block exit+EOF at this listing's actual deadline.
                Assert.False(boundary.RootElement.GetProperty("ReadCompleted").GetBoolean());
                Assert.True(boundary.RootElement.GetProperty("ElapsedSeconds").GetDouble() >= boundary.RootElement.GetProperty("DeadlineSeconds").GetDouble());
                Assert.InRange(boundary.RootElement.GetProperty("DeadlineSeconds").GetDouble(), 5.8, 8);
                Assert.Equal(behavior == "listing-pipe", boundary.RootElement.GetProperty("HasExited").GetBoolean());
                if (behavior == "listing-pipe")
                {
                    using var pipeReady = Evidence(probe, "pipe-child.ready");
                    using var pipeChild = Evidence(probe, "pipe-child.json");
                    Assert.True(pipeReady.RootElement.GetProperty("UtcTicks").GetInt64() > 0);
                    Assert.Equal(0, boundary.RootElement.GetProperty("ExitCode").GetInt32());
                    Assert.Contains(pipeChild.RootElement.GetProperty("Pid").GetInt32(), probe.AliveRecordedPids);
                    Assert.DoesNotContain(listingPid, probe.AliveRecordedPids);
                }
            }
        }
        // The unavailable table cannot establish these descendants. Their later harness cleanup is not candidate success.
        Assert.True(probe.OwnedProcessAliveBeforeHarnessCleanup, probe.Run.Output);
        Assert.NotEmpty(probe.AliveObservedPids);
    }

    private static void AssertOwnedChain(ProbeProject.ObservedProcess[] graph, int root, int leaf)
    {
        var byPid = graph.ToDictionary(row => row.Pid);
        var seen = new HashSet<int>();
        while (leaf != root)
        {
            Assert.True(seen.Add(leaf) && byPid.ContainsKey(leaf), "incomplete or cyclic observed process chain");
            leaf = byPid[leaf].Parent;
        }
        Assert.Contains(root, byPid.Keys);
    }

    private static JsonDocument Evidence(ProbeProject.PreparationRun probe, string file)
    {
        string path = Path.Combine(probe.EvidenceDirectory, file);
        Assert.True(File.Exists(path), "missing physical fixture evidence: " + path + "\n" + probe.Run.Output);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public async Task ANet48EligibleTargetHungInItsModernHostKeepsItsReadableTestNames()
    {
        BuildRun run = await _probe.RunEligibleTarget(noHost: false);
        Assert.NotEqual(0, run.ExitCode);
        Has(run, "HARD LIMIT:");
        Has(run, "stopped 1 test host process(es); waiting up to 10 s for the blame sequence to flush");
        Has(run, "blame sequence flushed; waiting up to 30 s for the test command to exit");
        Has(run, "started and not completed: LimitsProbe.HardLimitProbeTests.NeverFinishes");
        Lacks(run, "no test names: net48 xunit console leg");
        string trx = Assert.Single(Directory.GetFiles(_probe.EligibleTargetResults(noHost: false), "*.trx", SearchOption.AllDirectories));
        Assert.Contains(XDocument.Load(trx).Descendants(), element => element.Name.LocalName == "UnitTestResult" &&
            (string?)element.Attribute("testName") == "LimitsProbe.HardLimitProbeTests.FinishesBeforeTheHardLimit" &&
            (string?)element.Attribute("outcome") == "Passed");
    }

    [Fact]
    public async Task ANet48EligibleTargetWithoutAModernHostKeepsItsAbsenceReason()
    {
        BuildRun run = await _probe.RunEligibleTarget(noHost: true);
        Assert.NotEqual(0, run.ExitCode);
        Has(run, "HARD LIMIT:");
        Has(run, "PREPARATION_PAIR_READY"); // A real owned waiting pair inside the target, with no testhost.
        Has(run, "no test host was stopped;");
        Has(run, "no test names: net48 xunit console leg");
        Lacks(run, "blame sequence flushed;");
        Lacks(run, "started and not completed:");
        string results = _probe.EligibleTargetResults(noHost: true);
        Assert.Empty(Directory.Exists(results) ? Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories) : Array.Empty<string>());
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
        private string _compiledScript = "";
        private string _helper = "";

        public sealed record ObservedProcess(int Pid, long StartUtcTicks, int Parent = 0);
        public sealed record PreparationRun(BuildRun Run, TimeSpan Elapsed, bool Ready, bool TestEntered,
            bool StaleResultsRemain, bool OwnedProcessAliveBeforeHarnessCleanup, bool UnrelatedSentinelSurvived,
            string EvidenceDirectory, ObservedProcess[] ObservedOwned, int[] AliveObservedPids, int[] AliveRecordedPids);

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
                  <Target Name="RunTestsOnBothFrameworks">
                    <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="VSTest" Condition="'$(ProbeNoHost)' != 'true'"
                      Properties="Configuration=Release;VSTestTestCaseFilter=FullyQualifiedName~HardLimitProbeTests;VSTestNoBuild=true" />
                    <Exec Condition="'$(ProbeNoHost)' == 'true'" Command="dotnet &quot;$(PreparationHelper)&quot; stall &quot;$(PreparationEvidenceDirectory)&quot;" />
                  </Target>
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
            string identitySource = Path.Combine(FixtureProjects.RepositoryRoot(), "src", "Valheim.Testing.Game", "OwnedClientCommandLease.Identity.cs");
            File.Copy(identitySource, Path.Combine(Path.GetDirectoryName(_script)!, "OwnedClientCommandLease.Identity.cs"));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(_script)!, "Directory.Build.props"),
                "<Project><ItemGroup><Compile Include=\"ValidationSupport.cs\" /><Compile Include=\"OwnedClientCommandLease.Identity.cs\" /></ItemGroup></Project>");
            // Restore and compile now, so the scaled limits below measure the test run and not the first build. The project is
            // named by the very path validate.cs will pass to dotnet test (not found from the working directory, which a temp
            // folder behind a symbolic link such as macOS's /var spells differently): another spelling is another project to
            // NuGet, so the first test run restored again and the runner then failed to load xunit.abstractions (macOS CI).
            (await FixtureProjects.Dotnet(project, ["build", _project, "-c", "Release", "-m:1"], timeoutMinutes: 4)).AssertSucceeded();
            string helperDirectory = Path.Combine(_directory, "preparation-helper");
            Directory.CreateDirectory(helperDirectory);
            File.Copy(identitySource, Path.Combine(helperDirectory, "OwnedClientCommandLease.Identity.cs"));
            File.WriteAllText(Path.Combine(helperDirectory, "PreparationHelper.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(helperDirectory, "Program.cs"), """
                using System.Diagnostics;
                using System.Text.Json;
                using Valheim.Testing.Game;
                string mode = args[0], directory = args[1];
                string assembly = typeof(Program).Assembly.Location;
                void Write(string name, object value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value));
                void Record(string name) {
                    using var self = Process.GetCurrentProcess();
                    Write(name + ".json", new { Pid = self.Id, StartUtcTicks = OwnedClientCommandLease.LocalStartUtcTicks(self) });
                }
                void Ready(string name) => Write(name, new { UtcTicks = DateTime.UtcNow.Ticks });
                void WaitFor(string file) {
                    var clock = Stopwatch.StartNew();
                    while (!File.Exists(file)) {
                        if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception("fixture readiness absent: " + file);
                        Thread.Sleep(20);
                    }
                }
                Process Child(string childMode, bool isolateOutput = false) {
                    var info = new ProcessStartInfo("dotnet") { UseShellExecute = false,
                        RedirectStandardOutput = isolateOutput, RedirectStandardError = isolateOutput };
                    foreach (string arg in new[] { assembly, childMode, directory }) info.ArgumentList.Add(arg);
                    return Process.Start(info) ?? throw new Exception("fixture child did not start");
                }
                // Physical fixture evidence, independent of the production owner's parsed table. The root-exit wrapper
                // receives the original listing command verbatim; the independent observer uses the same OS command.
                (Dictionary<int, int> Table, string RootRow) ParseTable(string text, int rootPid) {
                    var table = new Dictionary<int, int>();
                    string rootRow = null;
                    foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                        var fields = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length >= 2 && int.TryParse(fields[0], out int pid) && int.TryParse(fields[1], out int parent)) {
                            table[pid] = parent;
                            if (pid == rootPid) rootRow = line;
                        }
                    }
                    return (table, rootRow);
                }
                HashSet<int> Below(Dictionary<int, int> table, int rootPid) {
                    var below = new HashSet<int> { rootPid };
                    for (bool grew = true; grew;) {
                        grew = false;
                        foreach (var (pid, parent) in table) if (below.Contains(parent) && below.Add(pid)) grew = true;
                    }
                    return below;
                }
                string Snapshot(int rootPid, long rootStart, string name) {
                    var start = args.Length > 4 ? new ProcessStartInfo(args[4], args.Skip(5))
                        : OperatingSystem.IsWindows()
                            ? new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command",
                                "Get-CimInstance Win32_Process | ForEach-Object { '{0} {1} {2}' -f $_.ProcessId, $_.ParentProcessId, $_.CommandLine }"])
                            : new ProcessStartInfo("ps", ["-ww", "-A", "-o", "pid=,ppid=,args="]);
                    start.UseShellExecute = false;
                    start.RedirectStandardOutput = true;
                    using var process = Process.Start(start) ?? throw new Exception("fixture snapshot did not start");
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    try { Task.WhenAll(output, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
                    finally {
                        if (!process.HasExited) {
                            process.Kill(entireProcessTree: true);
                            if (!process.WaitForExit(TimeSpan.FromSeconds(2))) throw new Exception("fixture snapshot cleanup failed");
                        }
                    }
                    if (process.ExitCode != 0) throw new Exception("fixture snapshot exit " + process.ExitCode);
                    string text = output.GetAwaiter().GetResult();
                    var (table, rootRow) = ParseTable(text, rootPid);
                    using var root = Process.GetProcessById(rootPid);
                    bool rootExited = root.HasExited;
                    long? observedStart = null;
                    bool? tableContainsRoot = null;
                    if (rootExited || (observedStart = OwnedClientCommandLease.LocalStartUtcTicks(root)) != rootStart ||
                        !(tableContainsRoot = table.ContainsKey(rootPid)).Value)
                        throw new Exception("fixture snapshot root is not the live expected identity; " +
                            FormattableString.Invariant($"mode={mode}; name={name}; RootPid={rootPid}; SuppliedStartUtcTicks={rootStart}; HasExited={rootExited}; ") +
                            $"ObservedStartUtcTicks={observedStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unevaluated"}; " +
                            FormattableString.Invariant($"TableContainsRoot={tableContainsRoot?.ToString() ?? "unevaluated"}; TableCount={table.Count}; RootRow={rootRow ?? "absent"}"));
                    var owned = Below(table, rootPid).Select(pid => {
                        using var child = Process.GetProcessById(pid);
                        return new { Pid = pid, Parent = table[pid], StartUtcTicks = OwnedClientCommandLease.LocalStartUtcTicks(child) };
                    }).ToArray();
                    Write(name + ".json", new { RootPid = rootPid, RootStartUtcTicks = rootStart,
                        CapturedUtcTicks = DateTime.UtcNow.Ticks, Owned = owned });
                    File.WriteAllText(Path.Combine(directory, name + ".txt"), text);
                    return text;
                }
                if (mode == "observe") { Snapshot(int.Parse(args[2]), long.Parse(args[3]), "observed-graph"); return 0; }
                if (mode == "fast") return 0;
                if (mode == "fail") { Console.WriteLine("PREPARATION_HELPER_EXIT_7"); return 7; }
                if (mode is "child" or "pipe-child") {
                    Record(mode);
                    Ready(mode + ".ready");
                    Thread.Sleep(Timeout.Infinite);
                }
                if (mode is "stall" or "exit-on-listing") {
                    Record("helper");
                    using var child = Child("child", isolateOutput: mode == "exit-on-listing");
                    WaitFor(Path.Combine(directory, "child.ready"));
                    Ready("pair.ready");
                    Console.WriteLine("PREPARATION_PAIR_READY");
                    if (mode == "exit-on-listing") { WaitFor(Path.Combine(directory, "listing.started")); return 0; }
                    Thread.Sleep(Timeout.Infinite);
                }
                Record("listing");
                Ready("listing.ready");
                if (mode == "listing-root-exit") {
                    int rootPid = int.Parse(args[2]);
                    long rootStart = long.Parse(args[3]);
                    void Stage(string stage) => Console.Error.WriteLine(FormattableString.Invariant(
                        $"PREPARATION_STAGE mode={mode}; stage={stage}; RootPid={rootPid}; RootStartUtcTicks={rootStart}; UtcTicks={DateTime.UtcNow.Ticks}"));
                    // Reuse this case's completed physical capture, before the root-exit handshake. No second OS listing.
                    string snapshot = File.ReadAllText(Path.Combine(directory, "observed-graph.txt"));
                    var (table, _) = ParseTable(snapshot, rootPid);
                    using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "observed-graph.json")));
                    var captured = capture.RootElement;
                    int capturedRootPid = captured.GetProperty("RootPid").GetInt32();
                    long capturedRootStart = captured.GetProperty("RootStartUtcTicks").GetInt64();
                    long capturedUtcTicks = captured.GetProperty("CapturedUtcTicks").GetInt64();
                    var recorded = captured.GetProperty("Owned").EnumerateArray().ToDictionary(
                        item => item.GetProperty("Pid").GetInt32(),
                        item => new { Pid = item.GetProperty("Pid").GetInt32(), Parent = item.GetProperty("Parent").GetInt32(),
                            StartUtcTicks = item.GetProperty("StartUtcTicks").GetInt64() });
                    if (!recorded.TryGetValue(capturedRootPid, out var capturedRoot) || capturedRoot.StartUtcTicks != capturedRootStart ||
                        !table.ContainsKey(capturedRootPid) || capturedRoot.Parent != table[capturedRootPid] ||
                        !Below(table, capturedRootPid).Contains(rootPid) || !table.ContainsKey(rootPid) ||
                        !recorded.TryGetValue(rootPid, out var recordedRoot) || recordedRoot.StartUtcTicks != rootStart)
                        throw new Exception("recorded snapshot does not contain the expected root identity");
                    var owned = Below(table, rootPid).Select(pid => {
                        if (!recorded.TryGetValue(pid, out var identity) || identity.Parent != table[pid])
                            throw new Exception("recorded snapshot subtree identity or parent is missing");
                        return identity;
                    }).ToArray();
                    Write("listing-graph.json", new { RootPid = rootPid, RootStartUtcTicks = rootStart,
                        CapturedUtcTicks = capturedUtcTicks, Owned = owned });
                    File.WriteAllText(Path.Combine(directory, "listing-graph.txt"), snapshot);
                    Stage("graph-written");
                    using var root = Process.GetProcessById(rootPid);
                    bool recheckExited = root.HasExited;
                    long? recheckStart = null;
                    bool rootLost = recheckExited || (recheckStart = OwnedClientCommandLease.LocalStartUtcTicks(root)) != rootStart;
                    Stage(FormattableString.Invariant($"root-recheck HasExited={recheckExited}; ObservedStartUtcTicks={recheckStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unevaluated"}; Lost={rootLost}"));
                    if (rootLost) throw new Exception("root lost before exit handshake");
                    Ready("listing.started");
                    Stage("listing-started-written");
                    Stage("root-wait-entered");
                    bool rootWaitExited = root.WaitForExit(TimeSpan.FromSeconds(2));
                    Stage("root-wait-completed result=" + rootWaitExited);
                    if (!rootWaitExited) throw new Exception("fixture root did not exit during listing");
                    Write("root-exit.observed.json", new { Pid = rootPid, StartUtcTicks = rootStart, UtcTicks = DateTime.UtcNow.Ticks, ExitObserved = root.HasExited });
                    Stage("exit-receipt-written");
                    Console.Write(snapshot);
                    Console.Out.Flush();
                    Ready("graph.returned.json");
                    Stage("graph-returned");
                    return 0;
                }
                if (mode == "listing-fail") { Write("listing.exit.json", new { ExitCode = 7 }); return 7; }
                if (mode == "listing-pipe") {
                    using var child = Child("pipe-child");
                    WaitFor(Path.Combine(directory, "pipe-child.ready"));
                    Write("listing.exit.json", new { ExitCode = 0 });
                    return 0; // The real ready child retains inherited stdout after its listing root exits.
                }
                Thread.Sleep(Timeout.Infinite); // listing-hang: no EOF and no root exit.
                return 0;
                """);
            (await FixtureProjects.Dotnet(helperDirectory, ["build", "-c", "Release", "-m:1"], timeoutMinutes: 4)).AssertSucceeded();
            _helper = Path.Combine(helperDirectory, "bin", "Release", "net10.0", "PreparationHelper.dll");
            string compiled = Path.Combine(_directory, "compiled-validator");
            (await FixtureProjects.Dotnet(_directory, ["build", _script, "-c", "Release", "-o", compiled], timeoutMinutes: 4)).AssertSucceeded();
            _compiledScript = Path.Combine(compiled, "validate.dll");

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
                new Dictionary<string, string?>(CleanEnvironment())
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

        public string EligibleTargetResults(bool noHost) => Path.Combine(_directory, noHost ? "eligible-no-host" : "eligible-modern", "LimitsProbe");

        public async Task<BuildRun> RunEligibleTarget(bool noHost)
        {
            string directory = Path.GetDirectoryName(EligibleTargetResults(noHost))!;
            Directory.CreateDirectory(directory);
            try
            {
                return await FixtureProjects.Dotnet(FixtureProjects.RepositoryRoot(),
                    [_compiledScript, "--test-project", FixtureProjects.RepositoryRoot(), _project, directory, "--target", "RunTestsOnBothFrameworks",
                        "-p:ProbeNoHost=" + (noHost ? "true" : "false"), "-p:PreparationHelper=" + _helper, "-p:PreparationEvidenceDirectory=" + directory],
                    new Dictionary<string, string?>(CleanEnvironment())
                    {
                        ["VT_VALIDATE_SOFT_LIMIT_SECONDS"] = "2",
                        ["VT_VALIDATE_HARD_LIMIT_SECONDS"] = "20",
                    }, timeoutMinutes: 2);
            }
            finally
            {
                foreach (string name in new[] { "child", "helper" }) StopRecorded(Path.Combine(directory, name + ".json"));
            }
        }

        // Drives the actual Test() preparation branch. Only the listing executable is substituted in a disposable script
        // for boundary cases; all executor/ownership/deadline bodies remain production code. No extra production mode.
        public async Task<PreparationRun> RunPreparation(string behavior)
        {
            string directory = Path.Combine(_directory, "preparation-" + behavior);
            Directory.CreateDirectory(directory);
            string repository = Path.Combine(directory, "repository");
            string tests = Path.Combine(repository, "tests", "Valheim.Testing.Tests");
            string preparation = Path.Combine(repository, "tests", "Valheim.Testing.StandInProcessLeakProbe");
            Directory.CreateDirectory(tests);
            Directory.CreateDirectory(preparation);
            File.WriteAllText(Path.Combine(repository, "cli-dependency.json"), "{}");
            string project = Path.Combine(tests, "Valheim.Testing.Tests.csproj");
            File.Copy(_project, project);
            string sentinel = Path.Combine(directory, "test-body.sentinel");
            File.WriteAllText(Path.Combine(tests, "ProbeTests.cs"),
                "using Xunit; public class PreparationProbe { [Fact] public void Entered() => System.IO.File.WriteAllText(@\"" +
                sentinel.Replace("\"", "\"\"", StringComparison.Ordinal) + "\", \"entered\"); }");
            string helperMode = behavior == "listing-root-exit" ? "exit-on-listing" : behavior.StartsWith("listing-", StringComparison.Ordinal) ? "stall" : behavior;
            string buildCommand = $"\"{FixtureProjects.DotnetHost}\" \"{_helper}\" {helperMode} \"{directory}\"";
            File.WriteAllText(Path.Combine(preparation, "Valheim.Testing.StandInProcessLeakProbe.csproj"),
                new XElement("Project", new XElement("Target", new XAttribute("Name", "Build"),
                    new XElement("Exec", new XAttribute("Command", buildCommand)))).ToString());
            string compiled = _compiledScript;
            if (behavior.StartsWith("listing-", StringComparison.Ordinal))
            {
                string copied = Path.Combine(directory, "validate-probe");
                Directory.CreateDirectory(copied);
                string source = File.ReadAllText(_script);
                const string anchor = "    start.RedirectStandardOutput = true;";
                Assert.Equal(1, source.Split(anchor, StringSplitOptions.None).Length - 1);
                string literal(string value) => "@\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                source = source.Replace(anchor, "    var originalListing = start;\n" +
                    "    start = new ProcessStartInfo(" + literal(FixtureProjects.DotnetHost) +
                    ", [" + literal(_helper) + ", " + literal(behavior) + ", " + literal(directory) +
                    ", rootProcess.Id.ToString(), rootStart!.Value.ToString(), originalListing.FileName, .. originalListing.ArgumentList]);\n" + anchor,
                    StringComparison.Ordinal);
                const string boundary = "        // This listing's own root is ours.";
                Assert.Equal(1, source.Split(boundary, StringSplitOptions.None).Length - 1);
                source = source.Replace(boundary,
                    "        {\n" +
                    "            var snapshot = new { Pid = listing.Id, StartUtcTicks = listingStart, HasExited = listing.HasExited, " +
                    "ExitCode = listing.HasExited ? (int?)listing.ExitCode : null, ReadCompleted = read.IsCompleted, " +
                    "ElapsedSeconds = clock.Elapsed.TotalSeconds, DeadlineSeconds = deadline.TotalSeconds, UtcTicks = DateTime.UtcNow.Ticks };\n" +
                    "            using var bytes = new MemoryStream();\n" +
                    "            using (var writer = new Utf8JsonWriter(bytes))\n" +
                    "            {\n" +
                    "                writer.WriteStartObject();\n" +
                    "                writer.WriteNumber(\"Pid\", snapshot.Pid);\n" +
                    "                if (snapshot.StartUtcTicks is long startTicks) writer.WriteNumber(\"StartUtcTicks\", startTicks); else writer.WriteNull(\"StartUtcTicks\");\n" +
                    "                writer.WriteBoolean(\"HasExited\", snapshot.HasExited);\n" +
                    "                if (snapshot.ExitCode is int exitCode) writer.WriteNumber(\"ExitCode\", exitCode); else writer.WriteNull(\"ExitCode\");\n" +
                    "                writer.WriteBoolean(\"ReadCompleted\", snapshot.ReadCompleted);\n" +
                    "                writer.WriteNumber(\"ElapsedSeconds\", snapshot.ElapsedSeconds);\n" +
                    "                writer.WriteNumber(\"DeadlineSeconds\", snapshot.DeadlineSeconds);\n" +
                    "                writer.WriteNumber(\"UtcTicks\", snapshot.UtcTicks);\n" +
                    "                writer.WriteEndObject();\n" +
                    "                writer.Flush();\n" +
                    "            }\n" +
                    "            File.WriteAllBytes(" + literal(Path.Combine(directory, "listing.boundary.json")) + ", bytes.ToArray());\n" +
                    "        }\n" + boundary,
                    StringComparison.Ordinal);
                const string admission = "        using var currentRoot = Process.GetProcessById(rootPid);";
                Assert.Equal(1, source.Split(admission, StringSplitOptions.None).Length - 1);
                source = source.Replace(admission,
                    "    {\n" +
                    "        var snapshot = new { RootPid = rootPid, RootStart = rootStart, RootExited = rootProcess.HasExited, " +
                    "TableContainsRoot = table.ContainsKey(rootPid), TableCount = table.Count };\n" +
                    "        using var bytes = new MemoryStream();\n" +
                    "        using (var writer = new Utf8JsonWriter(bytes))\n" +
                    "        {\n" +
                    "            writer.WriteStartObject();\n" +
                    "            writer.WriteNumber(\"RootPid\", snapshot.RootPid);\n" +
                    "            if (snapshot.RootStart is long startTicks) writer.WriteNumber(\"RootStart\", startTicks); else writer.WriteNull(\"RootStart\");\n" +
                    "            writer.WriteBoolean(\"RootExited\", snapshot.RootExited);\n" +
                    "            writer.WriteBoolean(\"TableContainsRoot\", snapshot.TableContainsRoot);\n" +
                    "            writer.WriteNumber(\"TableCount\", snapshot.TableCount);\n" +
                    "            writer.WriteEndObject();\n" +
                    "            writer.Flush();\n" +
                    "        }\n" +
                    "        File.WriteAllBytes(" + literal(Path.Combine(directory, "admission.boundary.json")) + ", bytes.ToArray());\n" +
                    "    }\n" + admission,
                    StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(copied, "validate.cs"), source);
                foreach (string file in new[] { "ValidationSupport.cs", "OwnedClientCommandLease.Identity.cs", "Directory.Build.props" })
                    File.Copy(Path.Combine(Path.GetDirectoryName(_script)!, file), Path.Combine(copied, file));
                string output = Path.Combine(directory, "compiled");
                (await FixtureProjects.Dotnet(directory, ["build", Path.Combine(copied, "validate.cs"), "-c", "Release", "-o", output], timeoutMinutes: 4)).AssertSucceeded();
                compiled = Path.Combine(output, "validate.dll");
            }
            string results = Path.Combine(directory, "results");
            string stale = Path.Combine(results, "Valheim.Testing.Tests");
            Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, "stale.trx"), "<TestRun><UnitTestResult testName='StalePreviousRun' /></TestRun>");
            File.WriteAllText(Path.Combine(stale, "Sequence_stale.xml"), "<Sequence><Test DisplayName='StalePreviousRun' Completed='False' /></Sequence>");
            string unrelated = Path.Combine(directory, "unrelated");
            Directory.CreateDirectory(unrelated);
            using var unrelatedProcess = Process.Start(StartHelper("child", unrelated))!;
            long unrelatedStart = OwnedClientCommandLease.LocalStartUtcTicks(unrelatedProcess);
            try
            {
                var readiness = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(unrelated, "child.ready")) && readiness.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
                Assert.True(File.Exists(Path.Combine(unrelated, "child.ready")), "unrelated sentinel failed to start");
                var start = new ProcessStartInfo(FixtureProjects.DotnetHost) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { compiled, "--test-project", repository, project, results, "FullyQualifiedName~Entered" }) start.ArgumentList.Add(argument);
                foreach (var (name, value) in CleanEnvironment()) if (value == null) start.Environment.Remove(name); else start.Environment[name] = value;
                start.Environment["VT_VALIDATE_PREPARATION_LIMIT_SECONDS"] = behavior is "fast" or "fail" ? "20" : "4";
                start.Environment["VT_VALIDATE_SOFT_LIMIT_SECONDS"] = "2";
                start.Environment["VT_VALIDATE_HARD_LIMIT_SECONDS"] = "20";
                start.Environment["VT_VALIDATE_CLEANUP_LIMIT_SECONDS"] = "6";
                start.Environment["VT_VALIDATE_PROCESS_LIST_LIMIT_SECONDS"] = "2";
                using var command = Process.Start(start)!;
                var elapsed = Stopwatch.StartNew();
                long commandStart = OwnedClientCommandLease.LocalStartUtcTicks(command);
                ObservedProcess[] graph = [];
                var captured = new StringBuilder();
                async Task Drain(StreamReader reader)
                {
                    char[] buffer = new char[4096];
                    int count;
                    while ((count = await reader.ReadAsync(buffer)) != 0)
                        lock (captured) captured.Append(buffer, 0, count);
                }
                Task drains = Task.WhenAll(Drain(command.StandardOutput), Drain(command.StandardError));
                try
                {
                    if (behavior is not ("fast" or "fail"))
                    {
                        while (!File.Exists(Path.Combine(directory, "pair.ready")) && !command.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(4))
                            await Task.Delay(20);
                        Assert.True(File.Exists(Path.Combine(directory, "pair.ready")), "preparation pair was not ready before the limit");
                        var observerStart = StartHelper("observe", directory);
                        observerStart.ArgumentList.Add(command.Id.ToString());
                        observerStart.ArgumentList.Add(commandStart.ToString());
                        using var observer = Process.Start(observerStart)!;
                        long observerIdentity = OwnedClientCommandLease.LocalStartUtcTicks(observer);
                        try { await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(0, observer.ExitCode); }
                        finally { StopIdentity(observer.Id, observerIdentity); }
                        graph = ReadGraph(Path.Combine(directory, "observed-graph.json"));
                        Assert.Contains(graph, item => item.Pid == command.Id && item.StartUtcTicks == commandStart);
                        foreach (string name in new[] { "helper", "child" })
                        {
                            var recorded = ReadIdentity(Path.Combine(directory, name + ".json"));
                            Assert.Contains(graph, item => item.Pid == recorded.Pid && item.StartUtcTicks == recorded.StartUtcTicks);
                        }
                        Assert.True(graph.Length >= 4, "incomplete independent validator/build/helper/child graph");
                    }
                    await command.WaitForExitAsync().WaitAsync(TimeSpan.FromTicks(Math.Max(0, (TimeSpan.FromSeconds(20) - elapsed.Elapsed).Ticks)));
                    TimeSpan rootElapsed = elapsed.Elapsed;
                    // An intentionally unverified descendant may retain the harness's inherited pipe too. Read available
                    // diagnostics now; wait for EOF only after the independent finally cleanup, never renew the command budget.
                    try { await drains.WaitAsync(TimeSpan.FromMilliseconds(200)); }
                    catch (TimeoutException) { }
                    string text;
                    lock (captured) text = captured.ToString();
                    int readyAt = text.IndexOf("PREPARATION_PAIR_READY", StringComparison.Ordinal);
                    int limitAt = text.IndexOf("PREPARATION LIMIT:", StringComparison.Ordinal);
                    bool ready = File.Exists(Path.Combine(directory, "pair.ready")) && readyAt >= 0 && limitAt > readyAt;
                    int[] aliveRecorded = new[] { "helper", "child", "listing", "pipe-child" }
                        .Select(name => Path.Combine(directory, name + ".json")).Where(File.Exists)
                        .Select(ReadIdentity).Where(RecordedAlive).Select(item => item.Pid).ToArray();
                    var result = new PreparationRun(new(command.ExitCode, text), rootElapsed, ready,
                        File.Exists(sentinel), File.Exists(Path.Combine(stale, "stale.trx")) || File.Exists(Path.Combine(stale, "Sequence_stale.xml")),
                        aliveRecorded.Length != 0, !unrelatedProcess.HasExited, directory, graph,
                        graph.Where(RecordedAlive).Select(item => item.Pid).ToArray(), aliveRecorded);
                    // Check physical case evidence while survivors still exist; only finally may clean the fixture.
                    if (behavior.StartsWith("listing-", StringComparison.Ordinal)) AssertListingBoundary(result, behavior);
                    if (behavior == "stall") Assert.True(result.AliveObservedPids.Length == 0 && !result.OwnedProcessAliveBeforeHarnessCleanup, text);
                    return result;
                }
                finally
                {
                    // Independent fixture cleanup. Assertions above record candidate outcome before this is allowed to act.
                    var cleanupErrors = new List<Exception>();
                    foreach (string name in new[] { "pipe-child", "child", "helper", "listing" })
                        try { StopRecorded(Path.Combine(directory, name + ".json")); }
                        catch (Exception error) { cleanupErrors.Add(error); }
                    foreach (var item in graph.Reverse())
                        try { StopIdentity(item.Pid, item.StartUtcTicks); }
                        catch (Exception error) { cleanupErrors.Add(error); }
                    try
                    {
                        if (!command.HasExited) { command.Kill(entireProcessTree: true); await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    }
                    catch (Exception error) { cleanupErrors.Add(error); }
                    try { await drains.WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                    if (cleanupErrors.Count != 0) throw new AggregateException("fixture cleanup failed", cleanupErrors);
                }
            }
            finally
            {
                StopIdentity(unrelatedProcess.Id, unrelatedStart);
            }
        }

        private ProcessStartInfo StartHelper(string mode, string directory)
        {
            var start = new ProcessStartInfo(FixtureProjects.DotnetHost) { UseShellExecute = false };
            foreach (string arg in new[] { _helper, mode, directory }) start.ArgumentList.Add(arg);
            return start;
        }

        private static ObservedProcess ReadIdentity(string record) =>
            JsonSerializer.Deserialize<ObservedProcess>(File.ReadAllText(record)) ?? throw new InvalidOperationException("missing identity: " + record);

        private static ObservedProcess[] ReadGraph(string record)
        {
            using var graph = JsonDocument.Parse(File.ReadAllText(record));
            return graph.RootElement.GetProperty("Owned").Deserialize<ObservedProcess[]>() ?? throw new InvalidOperationException("missing graph: " + record);
        }

        private static bool RecordedAlive(ObservedProcess identity)
        {
            Process process;
            try { process = Process.GetProcessById(identity.Pid); }
            catch (ArgumentException) { return false; }
            using (process)
            {
                if (process.HasExited) return false;
                long start;
                try { start = OwnedClientCommandLease.LocalStartUtcTicks(process); }
                catch (IOException) when (process.HasExited) { return false; }
                return start == identity.StartUtcTicks && !process.HasExited;
            }
        }

        private static void StopRecorded(string record)
        {
            if (!File.Exists(record)) return;
            using var identity = JsonDocument.Parse(File.ReadAllText(record));
            StopIdentity(identity.RootElement.GetProperty("Pid").GetInt32(), identity.RootElement.GetProperty("StartUtcTicks").GetInt64());
        }

        private static void StopIdentity(int pid, long start)
        {
            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { return; /* It already exited. */ }
            using (process)
            {
                if (process.HasExited) return;
                long observed;
                try { observed = OwnedClientCommandLease.LocalStartUtcTicks(process); }
                catch (IOException) when (process.HasExited) { return; }
                if (observed != start || process.HasExited) return;
                process.Kill(entireProcessTree: true);
                Assert.True(process.WaitForExit(TimeSpan.FromSeconds(3)), "fixture process did not exit");
            }
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
            ["VT_VALIDATE_PREPARATION_LIMIT_SECONDS"] = null,
            ["VT_VALIDATE_CLEANUP_LIMIT_SECONDS"] = null,
            ["VT_VALIDATE_PROCESS_LIST_LIMIT_SECONDS"] = null,
            ["GITHUB_ACTIONS"] = null,
            ["VT_CI_PROFILE"] = null,
        };
    }
}
