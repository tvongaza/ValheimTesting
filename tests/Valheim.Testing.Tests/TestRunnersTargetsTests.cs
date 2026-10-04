using Xunit;

/// <summary>A test that runs .NET Framework tests for real: reported as skipped off Windows when Mono is not on PATH.</summary>
public sealed class NetFrameworkFactAttribute : FactAttribute
{
    public NetFrameworkFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !FixtureProjects.OnPath("mono"))
            Skip = "Runs .NET Framework tests: needs Windows, or Mono on PATH (the test-runners CI job runs them with Mono)";
    }
}

/// <summary>
/// tools/test-runners/Valheim.TestRunners.targets on a fixture test project that targets net10.0 and net48. The
/// refusals run on every OS without building; the real runs need .NET Framework (Windows) or Mono.
/// </summary>
[Collection(nameof(TestRunnersBuilds))]
public sealed class TestRunnersTargetsTests : IClassFixture<TestRunnersFixture>
{
    private readonly TestRunnersFixture _fixture;

    public TestRunnersTargetsTests(TestRunnersFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TranslatesFiltersForTheConsoleRunner()
    {
        BuildRun run = await FixtureProjects.Dotnet(_fixture.Root, new[] { "msbuild", "Filters.proj", "-nologo", "-tl:off", "-nodeReuse:false", "-t:Translate" });

        run.AssertSucceeded();
        Dictionary<string, string> results = FixtureProjects.Lines(run.Output)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("case ", StringComparison.Ordinal))
            .ToDictionary(line => line.Substring(5, line.IndexOf(':') - 5), line => line.Substring(line.IndexOf(':') + 2));
        foreach ((string name, string _, string unix, string windows) in TestRunnersFixture.FilterCases)
        {
            string expected = OperatingSystem.IsWindows() ? windows : unix;
            Assert.True(results.TryGetValue(name, out string? actual) && actual == expected,
                $"filter case {name}: expected '{expected}', got '{(results.TryGetValue(name, out string? got) ? got : "no line")}'\n{run.Output}");
        }
    }

    [Fact]
    public async Task RefusesAnUntranslatableFilterBeforeBuilding()
    {
        // A method term OR'd with a trait term: the console runner would AND them.
        BuildRun run = await _fixture.Msbuild("-p:VSTestTestCaseFilter=FullyQualifiedName~Passes | Case=net48");

        run.AssertFailedWith("The xunit console runner cannot express the filter 'FullyQualifiedName~Passes | Case=net48'");
        Assert.DoesNotContain("== summary", run.Output);
    }

    [Fact]
    public async Task RefusesAProjectThatTargetsOneFramework()
    {
        BuildRun run = await _fixture.Msbuild("-p:TargetFrameworks=", "-p:TargetFramework=net10.0");

        run.AssertFailedWith("RunTestsOnBothFrameworks needs <TargetFrameworks> with a .NET Framework and a modern framework");
        Assert.DoesNotContain("== summary", run.Output);
    }

    [Fact]
    public async Task RefusesAFrameworkTheProjectDoesNotTarget()
    {
        BuildRun run = await _fixture.Msbuild("-p:TargetFramework=net472");

        run.AssertFailedWith("does not target net472 (it targets net10.0;net48)");
        Assert.DoesNotContain("== summary", run.Output);
    }

    [UnixFact]
    public async Task RefusesWithoutMonoBeforeBuilding()
    {
        // PATH holds sh alone, which runs the targets' commands.
        string path = Directory.CreateDirectory(Path.Combine(_fixture.Root, "sh-only")).FullName;
        if (!File.Exists(Path.Combine(path, "sh"))) File.CreateSymbolicLink(Path.Combine(path, "sh"), "/bin/sh");

        BuildRun run = await _fixture.Msbuild(new Dictionary<string, string?> { ["PATH"] = path });

        run.AssertFailedWith(".NET Framework tests need Mono on macOS and Linux, and mono is not on PATH");
        Assert.DoesNotContain("== summary", run.Output);
    }

    [NetFrameworkFact]
    public async Task BothFrameworksPass()
    {
        BuildRun run = await _fixture.Run(TestRunnersFixture.Fixture, "-p:VSTestTestCaseFilter=FullyQualifiedName~Passes");

        run.AssertSucceeded();
        Assert.Equal(new[] { "net10.0: passed", "net48: passed (1 tests)" }, TestRunnersFixture.Summary(run));
    }

    [NetFrameworkFact]
    public async Task ShellCharactersInAFilterReachBothRunners()
    {
        // The second term matches nothing; it must reach dotnet test and the console runner without the shell acting on it.
        BuildRun run = await _fixture.Run(TestRunnersFixture.Fixture, "-p:VSTestTestCaseFilter=FullyQualifiedName~Passes | FullyQualifiedName~Box`1.It's$HOME%OS%");

        run.AssertSucceeded();
        Assert.Equal(new[] { "net10.0: passed", "net48: passed (1 tests)" }, TestRunnersFixture.Summary(run));
    }

    [NetFrameworkFact]
    public async Task AFailureOnlyOnNetFrameworkFailsTheRun()
    {
        BuildRun run = await _fixture.Run(TestRunnersFixture.Fixture, "-p:VSTestTestCaseFilter=Case=net48");

        run.AssertFailedWith("Tests failed on net48");
        Assert.Equal(new[] { "net10.0: passed", "net48: FAILED (tests, exit 1)" }, TestRunnersFixture.Summary(run));
    }

    [NetFrameworkFact]
    public async Task AModernFailureStillRunsNetFramework()
    {
        BuildRun run = await _fixture.Run(TestRunnersFixture.Fixture, "-p:VSTestTestCaseFilter=Case=net10");

        run.AssertFailedWith("Tests failed on net10.0");
        Assert.Equal(new[] { "net10.0: FAILED (tests, exit 1)", "net48: passed (1 tests)" }, TestRunnersFixture.Summary(run));
    }

    [NetFrameworkFact]
    public async Task ANetFrameworkRunWithNoTestsFails()
    {
        // The console runner exits 0 when a filter selects nothing; the run must still fail.
        BuildRun run = await _fixture.Run(TestRunnersFixture.Fixture, "-f", "net48", "-p:VSTestTestCaseFilter=FullyQualifiedName~NoSuchTest");

        run.AssertFailedWith("The xunit console runner ran no tests on net48");
        Assert.Equal(new[] { "net48: FAILED (no tests ran)" }, TestRunnersFixture.Summary(run));
    }

    [NetFrameworkFact]
    public async Task NoConsoleRunnerFailsTheNetFrameworkRun()
    {
        BuildRun run = await _fixture.Run(TestRunnersFixture.NoConsole, "-f", "net48");

        run.AssertFailedWith("No xunit console runner. Add <PackageReference Include=\"xunit.runner.console\"");
        Assert.Equal(new[] { "net48: not run (no xunit.runner.console)" }, TestRunnersFixture.Summary(run));
    }

    [NetFrameworkFact]
    public async Task ABuildFailureFailsTheRun()
    {
        BuildRun run = await _fixture.Run(TestRunnersFixture.Broken, "-f", "net48");

        run.AssertFailedWith("error CS0029");
        Assert.Equal(new[] { "net48: FAILED (build, exit 1)" }, TestRunnersFixture.Summary(run));
    }
}

/// <summary>
/// The real runs start several builds and test hosts each; they run alone, after the parallel tests, so their load does
/// not stretch the timing of tests that run beside them.
/// </summary>
[CollectionDefinition(nameof(TestRunnersBuilds), DisableParallelization = true)]
public sealed class TestRunnersBuilds { }

/// <summary>
/// A temporary directory holding <see cref="Fixture"/> (net10.0;net48, with the console runner), <see cref="NoConsole"/>,
/// <see cref="Broken"/> and Filters.proj, which runs the filter translation on <see cref="FilterCases"/>. All import the targets
/// file from this repository.
/// </summary>
public sealed class TestRunnersFixture : IDisposable
{
    /// <summary>
    /// Name, dotnet test filter, and the expected console options as sh and as a Windows .cmd file quote them ("False"
    /// when refused).
    /// </summary>
    public static readonly (string Name, string Filter, string Unix, string Windows)[] FilterCases =
    {
        ("contains", "FullyQualifiedName~DrySite", "True -method '*DrySite*'", "True -method \"*DrySite*\""),
        ("bare", "DrySite", "True -method '*DrySite*'", "True -method \"*DrySite*\""),
        ("traits", "Category=Slow | Category=Fast", "True -trait 'Category=Slow' -trait 'Category=Fast'", "True -trait \"Category=Slow\" -trait \"Category=Fast\""),
        ("any-case", "fullyqualifiedname=DrySiteTests.Boundary | FULLYQUALIFIEDNAME~Wet",
            "True -method 'DrySiteTests.Boundary' -method '*Wet*'", "True -method \"DrySiteTests.Boundary\" -method \"*Wet*\""),
        // Shell characters reach the runner as written: a generic class's backtick, a quote, a variable reference.
        ("shell-characters", "FullyQualifiedName~Box`1.It's$HOME%OS%\"",
            "True -method '*Box`1.It'\\''s$HOME%OS%\"*'", "True -method \"*Box`1.It's$HOME%%OS%%\\\"*\""),
        ("method-or-trait", "FullyQualifiedName~DrySite | Category=Slow", "False", "False"),
        ("and", "FullyQualifiedName~DrySite&Category=Slow", "False", "False"),
        ("not-equal", "FullyQualifiedName!=DrySite", "False", "False"),
        ("empty-term", "DrySite||Wet", "False", "False"),
        ("display-name-contains", "DisplayName~Dry", "False", "False"),
        ("display-name", "DisplayName=Dry", "False", "False"),
        ("name", "Name=Dry", "False", "False"),
        ("class-name", "classname=DrySiteTests", "False", "False"),
    };

    private const string Packages =
        "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.10.0\" />\n" +
        "    <PackageReference Include=\"xunit\" Version=\"2.8.1\" />\n" +
        "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"2.8.1\" PrivateAssets=\"all\" />\n";

    public TestRunnersFixture()
    {
        Root = Directory.CreateTempSubdirectory("test-runners-").FullName;
        string targets = Path.Combine(FixtureProjects.RepositoryRoot(), "tools", "test-runners", "Valheim.TestRunners.targets");
        // The order decides the run order: net10.0 first, so a modern failure shows net48 still runs.
        string console = "    <PackageReference Include=\"xunit.runner.console\" Version=\"2.8.1\" PrivateAssets=\"all\" />\n";
        WriteProject(Fixture, targets, Packages + console);
        WriteProject(NoConsole, targets, Packages);
        WriteProject(Broken, targets, Packages + console);
        File.WriteAllText(Path.Combine(Root, "Broken", "Broken.cs"), "public class Broken { int Value = \"not a number\"; }\n");
        File.WriteAllText(Path.Combine(Root, "Filters.proj"),
            "<Project>\n" +
            $"  <Import Project=\"{targets}\" />\n" +
            "  <ItemGroup>\n" +
            string.Concat(FilterCases.Select(c => $"    <FilterCase Include=\"{c.Name}\" Filter=\"{System.Security.SecurityElement.Escape(c.Filter)}\" />\n")) +
            "  </ItemGroup>\n" +
            "  <Target Name=\"Translate\" Outputs=\"%(FilterCase.Identity)\">\n" +
            "    <ValheimTestFilter Filter=\"%(FilterCase.Filter)\">\n" +
            "      <Output TaskParameter=\"Translated\" PropertyName=\"_Translated\" />\n" +
            "      <Output TaskParameter=\"ConsoleArguments\" PropertyName=\"_Arguments\" />\n" +
            "    </ValheimTestFilter>\n" +
            "    <Message Importance=\"high\" Text=\"case %(FilterCase.Identity): $(_Translated)$(_Arguments)\" />\n" +
            "  </Target>\n" +
            "</Project>\n");
    }

    public string Root { get; }

    public const string Fixture = "Fixture/Fixture.Tests.csproj";

    /// <summary>The same project without xunit.runner.console.</summary>
    public const string NoConsole = "NoConsole/NoConsole.Tests.csproj";

    /// <summary>The same project with a source file that does not compile.</summary>
    public const string Broken = "Broken/Broken.Tests.csproj";

    private void WriteProject(string relative, string targets, string packages)
    {
        string project = Path.Combine(new[] { Root }.Concat(relative.Split('/')).ToArray());
        string folder = Path.GetDirectoryName(project)!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(project,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup><TargetFrameworks>net10.0;net48</TargetFrameworks><LangVersion>10</LangVersion><IsPackable>false</IsPackable></PropertyGroup>\n" +
            "  <ItemGroup>\n" + packages + "  </ItemGroup>\n" +
            $"  <Import Project=\"{targets}\" />\n" +
            "</Project>\n");
        File.WriteAllText(Path.Combine(folder, "Cases.cs"),
            "using Xunit;\n\n" +
            "public class Cases\n{\n" +
            "    [Fact] public void Passes() { }\n\n" +
            "    [Fact, Trait(\"Case\", \"net48\")]\n" +
            "    public void FailsOnlyOnNetFramework()\n    {\n#if NETFRAMEWORK\n        Assert.Fail(\"negative control: fails only on .NET Framework\");\n#endif\n    }\n\n" +
            "    [Fact, Trait(\"Case\", \"net10\")]\n" +
            "    public void FailsOnlyOnModernNet()\n    {\n#if !NETFRAMEWORK\n        Assert.Fail(\"negative control: fails only on modern .NET\");\n#endif\n    }\n}\n");
    }

    /// <summary><c>dotnet msbuild -t:RunTestsOnBothFrameworks</c> on <see cref="Fixture"/>: no restore, so only the refusals can pass.</summary>
    public Task<BuildRun> Msbuild(params string[] arguments) => Msbuild(new Dictionary<string, string?>(), arguments);

    public Task<BuildRun> Msbuild(IReadOnlyDictionary<string, string?> environment, params string[] arguments) =>
        FixtureProjects.Dotnet(Root, new[] { "msbuild", Fixture, "-nologo", "-tl:off", "-nodeReuse:false", "-t:RunTestsOnBothFrameworks" }.Concat(arguments), environment);

    /// <summary><c>dotnet build &lt;project&gt; -t:RunTestsOnBothFrameworks</c>, as a mod runs it.</summary>
    public Task<BuildRun> Run(string project, params string[] arguments) =>
        FixtureProjects.Dotnet(Root, new[] { "build", project, "-nologo", "-tl:off", "-nodeReuse:false", "-t:RunTestsOnBothFrameworks" }.Concat(arguments), timeoutMinutes: 6);

    /// <summary>The lines after "== summary", up to the first that is not a framework's result.</summary>
    public static string[] Summary(BuildRun run) =>
        FixtureProjects.Lines(run.Output).Select(line => line.Trim())
            .SkipWhile(line => line != "== summary").Skip(1)
            .TakeWhile(line => line.StartsWith("net", StringComparison.Ordinal))
            .ToArray();

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
