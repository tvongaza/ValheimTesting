using Xunit;

/// <summary>
/// Runs the real tools/test-runners/run-tests.sh with fake dotnet, mono and xunit console tools that record their
/// arguments; nothing is built or tested. The real runs, with Mono and .NET Framework, are the test-runners CI job.
/// </summary>
public sealed class RunTestsScriptTests : IDisposable
{
    private readonly RunTestsFakes _fakes = new(powerShell: false);

    public void Dispose() => _fakes.Dispose();

    [UnixFact] public Task BothFrameworksPass() => RunTestsCases.BothFrameworksPass(_fakes, "[-parallel] [none]");
    [UnixFact] public Task FailingOnlyOnNet48FailsTheRun() => RunTestsCases.FailingOnlyOnNet48FailsTheRun(_fakes);
    [UnixFact] public Task FailingOnNet10StillRunsNet48() => RunTestsCases.FailingOnNet10StillRunsNet48(_fakes);
    [UnixFact] public Task Net48BuildFailure() => RunTestsCases.Net48BuildFailure(_fakes);
    [UnixFact] public Task NameFilterIsTranslated() => RunTestsCases.NameFilterIsTranslated(_fakes);
    [UnixFact] public Task TraitFilterIsTranslated() => RunTestsCases.TraitFilterIsTranslated(_fakes);
    [UnixFact] public Task UntranslatableFilterRunsNothing() => RunTestsCases.UntranslatableFilterRunsNothing(_fakes);
    [UnixFact] public Task PropertyNamesIgnoreCase() => RunTestsCases.PropertyNamesIgnoreCase(_fakes);
    [UnixFact] public Task NoTestsRanFails() => RunTestsCases.NoTestsRanFails(_fakes);
    [UnixFact] public Task ProjectWithoutNet48RunsNothing() => RunTestsCases.ProjectWithoutNet48RunsNothing(_fakes);
    [UnixFact] public Task NoConsoleRunner() => RunTestsCases.NoConsoleRunner(_fakes);
    [UnixFact] public Task ParallelOption() => RunTestsCases.ParallelOption(_fakes);
    [UnixFact] public Task ExtraArgumentsGoToDotnetTestOnly() => RunTestsCases.ExtraArgumentsGoToDotnetTestOnly(_fakes);

    [UnixFact]
    public async Task MissingMonoRefusesBeforeRunning()
    {
        _fakes.Environment["MONO"] = "no-such-mono";
        ScriptRun run = await _fakes.Run("Fixture.Tests.csproj");
        run.AssertExit(3);
        Assert.Contains("need Mono", run.Stderr);
        Assert.Empty(_fakes.Calls());
    }

    [UnixFact]
    public async Task ModernFrameworkAloneNeedsNoMono()
    {
        _fakes.Environment["MONO"] = "no-such-mono";
        ScriptRun run = await _fakes.Run("--framework", "net10.0", "Fixture.Tests.csproj");
        run.AssertExit(0);
        Assert.Equal(new[] { "net10.0: passed" }, RunTestsCases.Summary(run));
        Assert.Empty(_fakes.Calls("mono"));
    }

    [UnixFact]
    public async Task FindsTheConsoleRunnerTheProjectRestored()
    {
        string console = _fakes.Scripts.PathOf("packages/xunit.runner.console/2.8.1/tools/net48/xunit.console.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(console)!);
        File.WriteAllText(console, "stand-in");
        File.WriteAllText(_fakes.Scripts.PathOf("project.assets.json"), "{ \"libraries\": { \"xunit.runner.console/2.8.1\": { \"type\": \"package\" } } }");
        _fakes.Environment["XUNIT_CONSOLE"] = null;
        _fakes.Environment["FAKE_ASSETS"] = _fakes.Scripts.PathOf("project.assets.json");
        _fakes.Environment["FAKE_PACKAGES"] = _fakes.Scripts.PathOf("packages") + "/";

        ScriptRun run = await _fakes.Run("Fixture.Tests.csproj");

        run.AssertExit(0);
        Assert.StartsWith("mono [" + console + "] [" + _fakes.Dll + "]", Assert.Single(_fakes.Calls("mono")));
    }
}

/// <summary>The cases of <see cref="RunTestsScriptTests"/> against the PowerShell twin, run.ps1, under Windows PowerShell.</summary>
public sealed class RunTestsPowerShellTests : IDisposable
{
    private readonly RunTestsFakes _fakes = new(powerShell: true);

    public void Dispose() => _fakes.Dispose();

    [WindowsFact] public Task BothFrameworksPass() => RunTestsCases.BothFrameworksPass(_fakes, null);
    [WindowsFact] public Task FailingOnlyOnNet48FailsTheRun() => RunTestsCases.FailingOnlyOnNet48FailsTheRun(_fakes);
    [WindowsFact] public Task FailingOnNet10StillRunsNet48() => RunTestsCases.FailingOnNet10StillRunsNet48(_fakes);
    [WindowsFact] public Task Net48BuildFailure() => RunTestsCases.Net48BuildFailure(_fakes);
    [WindowsFact] public Task NameFilterIsTranslated() => RunTestsCases.NameFilterIsTranslated(_fakes);
    [WindowsFact] public Task TraitFilterIsTranslated() => RunTestsCases.TraitFilterIsTranslated(_fakes);
    [WindowsFact] public Task UntranslatableFilterRunsNothing() => RunTestsCases.UntranslatableFilterRunsNothing(_fakes);
    [WindowsFact] public Task PropertyNamesIgnoreCase() => RunTestsCases.PropertyNamesIgnoreCase(_fakes);
    [WindowsFact] public Task NoTestsRanFails() => RunTestsCases.NoTestsRanFails(_fakes);
    [WindowsFact] public Task ProjectWithoutNet48RunsNothing() => RunTestsCases.ProjectWithoutNet48RunsNothing(_fakes);
    [WindowsFact] public Task NoConsoleRunner() => RunTestsCases.NoConsoleRunner(_fakes);
    [WindowsFact] public Task ParallelOption() => RunTestsCases.ParallelOption(_fakes);
    [WindowsFact] public Task ExtraArgumentsGoToDotnetTestOnly() => RunTestsCases.ExtraArgumentsGoToDotnetTestOnly(_fakes);
}

/// <summary>Cases shared by the bash and PowerShell runs. The console runner's calls are logged as "console".</summary>
internal static class RunTestsCases
{
    /// <summary>The lines after "== summary".</summary>
    public static string[] Summary(ScriptRun run) =>
        DevLoopScripts.Lines(run.Stdout).SkipWhile(line => line != "== summary").Skip(1).ToArray();

    public static async Task BothFrameworksPass(RunTestsFakes fakes, string? defaultParallel)
    {
        ScriptRun run = await fakes.Run("Fixture.Tests.csproj");
        run.AssertExit(0);
        Assert.Equal(new[] { "net10.0: passed", "net48: passed (5 tests)" }, Summary(run));
        Assert.Equal("dotnet [test] [Fixture.Tests.csproj] [-f] [net10.0] [-c] [Debug]", Assert.Single(fakes.Calls("dotnet [test]")));
        Assert.StartsWith("dotnet [build] [Fixture.Tests.csproj] [-f] [net48] [-c] [Debug]", Assert.Single(fakes.Calls("dotnet [build]")));
        string console = Assert.Single(fakes.ConsoleCalls());
        Assert.StartsWith("console [" + fakes.Dll + "] [-xml] [", console);
        Assert.Contains("] [-nologo]", console);
        if (defaultParallel is null) Assert.DoesNotContain("[-parallel]", console);
        else Assert.EndsWith(defaultParallel, console);
    }

    public static async Task FailingOnlyOnNet48FailsTheRun(RunTestsFakes fakes)
    {
        fakes.Environment["FAKE_NET48_EXIT"] = "1";
        ScriptRun run = await fakes.Run("Fixture.Tests.csproj");
        run.AssertExit(1);
        Assert.Equal(new[] { "net10.0: passed", "net48: FAILED (tests, exit 1)" }, Summary(run));
    }

    public static async Task FailingOnNet10StillRunsNet48(RunTestsFakes fakes)
    {
        fakes.Environment["FAKE_TEST_EXIT"] = "1";
        ScriptRun run = await fakes.Run("Fixture.Tests.csproj");
        run.AssertExit(1);
        Assert.Equal(new[] { "net10.0: FAILED (tests, exit 1)", "net48: passed (5 tests)" }, Summary(run));
        Assert.Single(fakes.ConsoleCalls());
    }

    public static async Task Net48BuildFailure(RunTestsFakes fakes)
    {
        fakes.Environment["FAKE_BUILD_EXIT"] = "1";
        ScriptRun run = await fakes.Run("Fixture.Tests.csproj");
        run.AssertExit(1);
        Assert.Equal(new[] { "net10.0: passed", "net48: FAILED (build, exit 1)" }, Summary(run));
        Assert.Empty(fakes.ConsoleCalls());
    }

    public static async Task NameFilterIsTranslated(RunTestsFakes fakes)
    {
        ScriptRun run = await fakes.Run("--filter", "FullyQualifiedName~DrySite", "Fixture.Tests.csproj");
        run.AssertExit(0);
        Assert.EndsWith("[--filter] [FullyQualifiedName~DrySite]", Assert.Single(fakes.Calls("dotnet [test]")));
        Assert.EndsWith("[-method] [*DrySite*]", Assert.Single(fakes.ConsoleCalls()));
    }

    public static async Task TraitFilterIsTranslated(RunTestsFakes fakes)
    {
        ScriptRun run = await fakes.Run("--filter", "Category=Slow | Category=Fast", "Fixture.Tests.csproj");
        run.AssertExit(0);
        Assert.EndsWith("[--filter] [Category=Slow | Category=Fast]", Assert.Single(fakes.Calls("dotnet [test]")));
        Assert.EndsWith("[-trait] [Category=Slow] [-trait] [Category=Fast]", Assert.Single(fakes.ConsoleCalls()));
    }

    public static async Task UntranslatableFilterRunsNothing(RunTestsFakes fakes)
    {
        // The console runner ANDs a method and a trait option, so an OR of the two cannot be passed on.
        // DisplayName, Name and ClassName are test properties, not traits: as -trait they would select nothing.
        foreach (string filter in new[] { "FullyQualifiedName~DrySite | Category=Slow", "FullyQualifiedName~DrySite&Category=Slow", "DisplayName~Dry",
                     "DisplayName=Dry", "Name=Dry", "classname=DrySiteTests" })
        {
            ScriptRun run = await fakes.Run("--filter", filter, "Fixture.Tests.csproj");
            run.AssertExit(2);
            Assert.Contains("cannot express the filter", run.Stderr);
        }
        Assert.Empty(fakes.Calls());
    }

    public static async Task PropertyNamesIgnoreCase(RunTestsFakes fakes)
    {
        ScriptRun run = await fakes.Run("--filter", "fullyqualifiedname=DrySiteTests.Boundary | FULLYQUALIFIEDNAME~Wet", "Fixture.Tests.csproj");
        run.AssertExit(0);
        Assert.EndsWith("[-method] [DrySiteTests.Boundary] [-method] [*Wet*]", Assert.Single(fakes.ConsoleCalls()));
    }

    public static async Task NoTestsRanFails(RunTestsFakes fakes)
    {
        // A filter or discovery problem that selects nothing exits 0 from the console runner; the run must still fail.
        fakes.Environment["FAKE_TOTAL"] = "0";
        ScriptRun run = await fakes.Run("Fixture.Tests.csproj");
        run.AssertExit(1);
        Assert.Equal(new[] { "net10.0: passed", "net48: FAILED (no tests ran)" }, Summary(run));
        Assert.Contains("ran no tests on net48", run.Stderr);
    }

    public static async Task ProjectWithoutNet48RunsNothing(RunTestsFakes fakes)
    {
        fakes.Environment["FAKE_TFMS"] = "";
        fakes.Environment["FAKE_TFM"] = "net10.0";
        ScriptRun run = await fakes.Run("Fixture.Tests.csproj");
        run.AssertExit(2);
        Assert.Contains("does not target net48 (it targets: net10.0)", run.Stderr);
        Assert.Empty(fakes.Calls("dotnet [test]"));
        Assert.Empty(fakes.Calls("dotnet [build]"));
    }

    public static async Task NoConsoleRunner(RunTestsFakes fakes)
    {
        File.WriteAllText(fakes.Scripts.PathOf("project.assets.json"), "{ \"libraries\": { \"xunit.core/2.8.1\": { \"type\": \"package\" } } }");
        fakes.Environment["XUNIT_CONSOLE"] = null;
        fakes.Environment["FAKE_ASSETS"] = fakes.Scripts.PathOf("project.assets.json");
        fakes.Environment["FAKE_PACKAGES"] = fakes.Scripts.Folder("packages");
        ScriptRun run = await fakes.Run("Fixture.Tests.csproj");
        run.AssertExit(3);
        Assert.Equal(new[] { "net10.0: passed", "net48: not run (no xunit.runner.console)" }, Summary(run));
        Assert.Contains("xunit.runner.console", run.Stderr);
    }

    public static async Task ParallelOption(RunTestsFakes fakes)
    {
        ScriptRun run = await fakes.Run("--netfx-parallel", "collections", "Fixture.Tests.csproj");
        run.AssertExit(0);
        Assert.EndsWith("[-nologo] [-parallel] [collections]", Assert.Single(fakes.ConsoleCalls()));
        ScriptRun wrong = await fakes.Run("--netfx-parallel", "sometimes", "Fixture.Tests.csproj");
        wrong.AssertExit(2);
    }

    public static async Task ExtraArgumentsGoToDotnetTestOnly(RunTestsFakes fakes)
    {
        ScriptRun run = await fakes.Run("Fixture.Tests.csproj", "--logger", "trx", "--", "--blame");
        run.AssertExit(0);
        Assert.EndsWith("[-c] [Debug] [--logger] [trx] [--blame]", Assert.Single(fakes.Calls("dotnet [test]")));
        Assert.DoesNotContain("trx", Assert.Single(fakes.ConsoleCalls()));
    }
}

/// <summary>
/// Fake dotnet, mono (bash only) and xunit console tools for run-tests. Each call appends one line to calls.txt:
/// the tool's name and each argument in brackets. <c>dotnet msbuild -getProperty:X</c> prints FAKE_X-style values; the
/// console runner writes an XML report of FAKE_TOTAL tests where <c>-xml</c> names it.
/// </summary>
internal sealed class RunTestsFakes : IDisposable
{
    private readonly bool _powerShell;

    public RunTestsFakes(bool powerShell)
    {
        _powerShell = powerShell;
        Scripts = new DevLoopScripts("test-runners");
        string bin = Scripts.Folder("bin");
        Scripts.Folder("out");
        Dll = Scripts.PathOf("out/Fixture.Tests.dll");
        File.WriteAllText(Dll, "stand-in");
        string console;
        if (powerShell)
        {
            Scripts.WindowsTool("dotnet",
                "$argv = @(); for ($i = 0; $i -lt $args.Count; $i++) { $a = [string]$args[$i]; if ($a -match '^-[^:]+:$' -and $i + 1 -lt $args.Count) { $a += [string]$args[++$i] }; $argv += $a }\r\n" +
                "Add-Content -LiteralPath $env:CALLS -Value ('dotnet' + (($argv | ForEach-Object { ' [' + $_ + ']' }) -join ''))\r\n" +
                "if ($args.Count -gt 0 -and $args[0] -eq 'msbuild') {\r\n" +
                "    # From the raw command line: how PowerShell splits -name:value arguments into $args varies.\r\n" +
                "    $m = [regex]::Match([Environment]::CommandLine, '-getProperty:\\s*(\\w+)')\r\n" +
                "    switch ($m.Groups[1].Value) {\r\n" +
                "        'TargetFrameworks' { Write-Output $env:FAKE_TFMS }\r\n" +
                "        'TargetFramework' { Write-Output $env:FAKE_TFM }\r\n" +
                "        'TargetPath' { Write-Output $env:FAKE_DLL }\r\n" +
                "        'ProjectAssetsFile' { Write-Output $env:FAKE_ASSETS }\r\n" +
                "        'NuGetPackageRoot' { Write-Output $env:FAKE_PACKAGES }\r\n" +
                "        default { [Console]::Error.WriteLine('fake dotnet: no property in ' + [Environment]::CommandLine); exit 9 }\r\n" +
                "    }\r\n" +
                "    exit 0\r\n" +
                "}\r\n" +
                "if ($args.Count -gt 0 -and $args[0] -eq 'test') { exit [int]$env:FAKE_TEST_EXIT }\r\n" +
                "if ($args.Count -gt 0 -and $args[0] -eq 'build') { exit [int]$env:FAKE_BUILD_EXIT }\r\n" +
                "[Console]::Error.WriteLine('fake dotnet: unexpected ' + [Environment]::CommandLine)\r\n" +
                "exit 9\r\n");
            console = Scripts.WindowsTool("xunit-console",
                "Add-Content -LiteralPath $env:CALLS -Value ('console' + (($args | ForEach-Object { ' [' + $_ + ']' }) -join ''))\r\n" +
                "$x = [array]::IndexOf($args, '-xml')\r\n" +
                "if ($x -ge 0) { Set-Content -LiteralPath $args[$x + 1] -Value ('<assemblies><assembly name=\"Fixture.Tests.dll\" total=\"' + $env:FAKE_TOTAL + '\" /></assemblies>') }\r\n" +
                "exit [int]$env:FAKE_NET48_EXIT\r\n");
        }
        else
        {
            const string record = "for a in \"$@\"; do line=\"$line [$a]\"; done\nprintf '%s\\n' \"$line\" >> \"$CALLS\"\n";
            Scripts.Tool("bin/dotnet",
                "#!/bin/sh\nline=dotnet\n" + record +
                "case \"$1\" in\n" +
                "  msbuild)\n" +
                "    for a in \"$@\"; do case \"$a\" in\n" +
                "      -getProperty:TargetFrameworks) printf '%s\\n' \"$FAKE_TFMS\"; exit 0 ;;\n" +
                "      -getProperty:TargetFramework) printf '%s\\n' \"$FAKE_TFM\"; exit 0 ;;\n" +
                "      -getProperty:TargetPath) printf '%s\\n' \"$FAKE_DLL\"; exit 0 ;;\n" +
                "      -getProperty:ProjectAssetsFile) printf '%s\\n' \"$FAKE_ASSETS\"; exit 0 ;;\n" +
                "      -getProperty:NuGetPackageRoot) printf '%s\\n' \"$FAKE_PACKAGES\"; exit 0 ;;\n" +
                "    esac; done; exit 9 ;;\n" +
                "  test) exit \"$FAKE_TEST_EXIT\" ;;\n" +
                "  build) exit \"$FAKE_BUILD_EXIT\" ;;\n" +
                "esac\nexit 9\n");
            // Mono runs the console runner: its first argument is the runner.
            Scripts.Tool("bin/mono", "#!/bin/sh\nline=mono\n" + record +
                "while [ $# -gt 0 ]; do\n" +
                "  if [ \"$1\" = -xml ]; then printf '<assemblies><assembly name=\"Fixture.Tests.dll\" total=\"%s\" /></assemblies>\\n' \"$FAKE_TOTAL\" > \"$2\"; fi\n" +
                "  shift\n" +
                "done\n" +
                "exit \"$FAKE_NET48_EXIT\"\n");
            console = Scripts.PathOf("console/xunit.console.exe");
            Scripts.Folder("console");
            File.WriteAllText(console, "stand-in");
        }
        Console = console;
        Environment = new Dictionary<string, string?>
        {
            ["PATH"] = DevLoopScripts.PathWith(bin),
            ["CALLS"] = Scripts.PathOf("calls.txt"),
            ["MONO"] = null,
            ["XUNIT_CONSOLE"] = console,
            ["FAKE_TFMS"] = "net48;net10.0",
            ["FAKE_TFM"] = "",
            ["FAKE_DLL"] = Dll,
            ["FAKE_ASSETS"] = "",
            ["FAKE_PACKAGES"] = "",
            ["FAKE_TEST_EXIT"] = "0",
            ["FAKE_BUILD_EXIT"] = "0",
            ["FAKE_NET48_EXIT"] = "0",
            ["FAKE_TOTAL"] = "5",
        };
    }

    public DevLoopScripts Scripts { get; }

    /// <summary>The test assembly the fake build reports.</summary>
    public string Dll { get; }

    /// <summary>The xunit console runner passed as XUNIT_CONSOLE.</summary>
    public string Console { get; }

    public Dictionary<string, string?> Environment { get; }

    public Task<ScriptRun> Run(params string[] args) =>
        Scripts.Run(_powerShell ? "run-tests.ps1" : "run-tests.sh", args, Environment);

    /// <summary>Every recorded call, or those starting with <paramref name="prefix"/>.</summary>
    public string[] Calls(string prefix = "")
    {
        string path = Scripts.PathOf("calls.txt");
        if (!File.Exists(path)) return Array.Empty<string>();
        return DevLoopScripts.Lines(File.ReadAllText(path)).Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>The console runner's calls, without the "mono [runner]" prefix under Mono.</summary>
    public string[] ConsoleCalls() => _powerShell
        ? Calls("console ")
        : Calls("mono [" + Console + "]").Select(line => "console" + line.Substring(("mono [" + Console + "]").Length)).ToArray();

    public void Dispose() => Scripts.Dispose();
}
