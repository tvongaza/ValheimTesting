using Xunit;

// #719: every test command scripts/validate.cs starts has a soft limit (a warning) and a hard limit (stopped, failed, the tests in
// flight named). These run the real script against a tiny test project, with the limits scaled down through the variables the
// script reads for this purpose. They start dotnet test several times, so they run with the other local-shell integration tests.
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
    public async Task ARunPastTheHardLimitIsStoppedAndFailsNamingTheTestInFlight()
    {
        var run = await _probe.Run("NeverFinishes", soft: 2, hard: 75, githubActions: false);
        Assert.True(run.ExitCode != 0, $"expected the run to fail\n{run.Output}");
        Has(run, "HARD LIMIT:");
        Has(run, "FAILED: over the hard limit");
        Has(run, "started and not completed: LimitsProbe.ProbeTests.NeverFinishes");
        Lacks(run, "::warning");
    }

    private static void Has(BuildRun run, string text) =>
        Assert.True(run.Output.Contains(text, StringComparison.Ordinal), $"expected \"{text}\" in (exit {run.ExitCode})\n{run.Output}");

    private static void Lacks(BuildRun run, string text) =>
        Assert.False(run.Output.Contains(text, StringComparison.Ordinal), $"did not expect \"{text}\" in (exit {run.ExitCode})\n{run.Output}");

    /// <summary>A test project with a quick test, one slower than a scaled soft limit and one that never ends, built once.</summary>
    public sealed class ProbeProject : IAsyncLifetime
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "vt-limits-" + Guid.NewGuid().ToString("N"));
        private string _project = "";
        private string _script = "";

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
                </Project>
                """);
            File.WriteAllText(Path.Combine(project, "ProbeTests.cs"), """
                using System;
                using System.Threading;
                using Xunit;

                namespace LimitsProbe;

                public class ProbeTests
                {
                    [Fact] public void PassesQuickly() { }
                    [Fact] public void PassesAfterTheSoftLimit() => Thread.Sleep(TimeSpan.FromSeconds(6));
                    [Fact] public void NeverFinishes() => Thread.Sleep(TimeSpan.FromMinutes(30));
                }
                """);
            // A copy of the script, so the validation that runs these tests keeps its own build of it; it finds the repository
            // from the working directory.
            _script = Path.Combine(Path.GetTempPath(), "vt-validate-limits-probe", "validate.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(_script)!);
            File.Copy(Path.Combine(FixtureProjects.RepositoryRoot(), "scripts", "validate.cs"), _script, overwrite: true);
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

        /// <summary>validate.cs's probe mode on one test of the project; null limits keep the script's own.</summary>
        public Task<BuildRun> Run(string test, double? soft, double? hard, bool githubActions) =>
            FixtureProjects.Dotnet(FixtureProjects.RepositoryRoot(),
                ["run", _script, "--", "--limits-probe", _project, Path.Combine(_directory, "results-" + test), "FullyQualifiedName~" + test],
                new Dictionary<string, string?>
                {
                    ["VT_VALIDATE_SOFT_LIMIT_SECONDS"] = soft?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["VT_VALIDATE_HARD_LIMIT_SECONDS"] = hard?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["GITHUB_ACTIONS"] = githubActions ? "true" : null,
                    ["VT_CI_PROFILE"] = null,
                }, timeoutMinutes: 5);
    }
}
