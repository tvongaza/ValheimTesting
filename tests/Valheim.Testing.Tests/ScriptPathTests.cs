using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

// #772: a relative argument resolves against the repository root, not the working directory.
public sealed class ScriptPathTests
{
    [Fact]
    public async Task PackageAuditResolvesARelativeDirectoryFromTheRepositoryRoot()
    {
        string root = FixtureProjects.RepositoryRoot();
        string sandboxRelative = Path.Combine("artifacts", "script-path-" + Guid.NewGuid().ToString("N"));
        string sandbox = Path.Combine(root, sandboxRelative);
        string relative = Path.Combine(sandboxRelative, "packages");
        string expected = Path.Combine(root, relative);
        string workingDirectory = Path.Combine(sandbox, "working");
        Directory.CreateDirectory(expected);
        Directory.CreateDirectory(workingDirectory);
        try
        {
            BuildRun run = await FixtureProjects.Dotnet(workingDirectory,
                ["run", Path.Combine(root, "scripts", "package-audit.cs"), "--", "--directory", relative, "--candidate"]);

            run.AssertFailedWith($" packages in {expected}, found 0.");
            Assert.DoesNotContain(Path.Combine(workingDirectory, relative), run.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRealValidatorCompilesItsCanonicalIdentityBeforeUsageRefusal(bool run)
    {
        using var fixture = new ScriptCompileFixture(canonicalPresent: true);
        BuildRun result = await fixture.Invoke("validate", run);
        fixture.AssertMembership("validate", 1, result);
        if (run)
        {
            Assert.True(result.ExitCode != 0 && result.Output.Contains("ArgumentException", StringComparison.Ordinal) &&
                result.Output.Contains(ValidationInvocation.Usage, StringComparison.Ordinal) &&
                !result.Output.Contains("error CS", StringComparison.Ordinal), fixture.Describe(result));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "artifacts", "validate")), fixture.Describe(result));
        }
        else result.AssertSucceeded();
        fixture.Succeeded();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRealValidatorKeepsItsExactCanonicalInputRequired(bool run)
    {
        using var fixture = new ScriptCompileFixture(canonicalPresent: false);
        BuildRun result = await fixture.Invoke("validate", run);
        fixture.AssertMembership("validate", 1, result);
        string[] missingPaths = Regex.Matches(result.Output, @"\bCS2001: Source file '(?<path>[^']+)' could not be found")
            .Select(match => Path.GetFullPath(match.Groups["path"].Value, fixture.Root)).ToArray();
        File.WriteAllText(Path.Combine(fixture.Root, "required-input-diagnostics.json"),
            JsonSerializer.Serialize(new { Expected = fixture.Canonical, Reported = missingPaths }));
        Assert.True(result.ExitCode != 0 && missingPaths.Any(path => fixture.PathComparer.Equals(path, fixture.Canonical)) &&
            !result.Output.Contains("CS0246", StringComparison.Ordinal) &&
            !result.Output.Contains(ValidationInvocation.Usage, StringComparison.Ordinal), fixture.Describe(result));
        fixture.Succeeded();
    }

    [Fact]
    public async Task ANeighboringPinsEntryCompilesWithoutTheValidatorsCanonicalInput()
    {
        using var fixture = new ScriptCompileFixture(canonicalPresent: false);
        BuildRun result = await fixture.Invoke("pins", run: true);
        fixture.AssertMembership("pins", 0, result);
        Assert.True(result.ExitCode == 2 && result.Output.Contains("pins: unknown mode '--compile-input-control-invalid'. Usage:", StringComparison.Ordinal) &&
            !result.Output.Contains("error CS", StringComparison.Ordinal), fixture.Describe(result));
        fixture.Succeeded();
    }

    private sealed class ScriptCompileFixture : IDisposable
    {
        private readonly TempDirectory _temporary = new();
        private bool _succeeded;
        public string Root { get; }
        public string Canonical => Path.Combine(Root, "src", "Valheim.Testing.Game", "OwnedClientCommandLease.Identity.cs");
        public StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public ScriptCompileFixture(bool canonicalPresent)
        {
            Root = Path.Combine(_temporary.Path, "repo with spaces");
            string source = FixtureProjects.RepositoryRoot();
            foreach (string relative in new[] { "Directory.Build.props", "NuGet.Config", "cli-dependency.json",
                "scripts/Directory.Build.props", "scripts/Directory.Build.targets", "scripts/validate.cs", "scripts/pins.cs",
                "scripts/Shared/RepositoryPaths.cs", "scripts/Shared/ValidationSupport.cs" })
            {
                string destination = Path.Combine(Root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(source, relative), destination);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Canonical)!);
            Directory.CreateDirectory(Path.Combine(Root, ".packages"));
            if (canonicalPresent) File.Copy(Path.Combine(source, "src", "Valheim.Testing.Game", Path.GetFileName(Canonical)), Canonical);
            // Observation only: production props/targets decide membership. No identity Include is written by the fixture.
            File.WriteAllText(Path.Combine(Root, "compile-receipt.targets"), """
                <Project>
                  <Target Name="ObserveScriptCompileInputs" BeforeTargets="CoreCompile" Condition="'$(FileBasedProgram)' == 'true'">
                    <WriteLinesToFile File="$(CompileReceipt)/items.txt" Lines="@(Compile->'%(FullPath)')" Overwrite="true" />
                    <WriteLinesToFile File="$(CompileReceipt)/context.txt" Lines="SDK=$(NETCoreSdkVersion);FileBasedProgram=$(FileBasedProgram);Project=$(MSBuildProjectFullPath)" Overwrite="true" />
                  </Target>
                </Project>
                """);
        }

        public async Task<BuildRun> Invoke(string script, bool run)
        {
            string receipt = Path.Combine(Root, "receipt");
            Directory.CreateDirectory(receipt);
            string entry = Path.Combine(Root, "scripts", script + ".cs");
            string[] arguments = run ? ["run", entry, "--", "--compile-input-control-invalid"] : ["build", entry];
            var environment = new Dictionary<string, string?>
            {
                ["CustomAfterDirectoryBuildTargets"] = Path.Combine(Root, "compile-receipt.targets"),
                ["CompileReceipt"] = receipt,
            };
            try
            {
                BuildRun result = await FixtureProjects.Dotnet(Root, arguments, environment, timeoutMinutes: 2);
                // Keep the actual command result before any receipt/assertion can fail.
                File.WriteAllText(Path.Combine(Root, "actual-command.json"), JsonSerializer.Serialize(new { arguments, result.ExitCode, result.Output }));
                return result;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Root, "invocation-exception.txt"), error +
                    "\nExisting FixtureProjects owns this command. A timeout does not establish descendant exit; this fixture is retained.");
                throw new InvalidOperationException("Script compile fixture retained at " + Root, error);
            }
        }

        public void AssertMembership(string script, int canonicalCount, BuildRun result)
        {
            string items = Path.Combine(Root, "receipt", "items.txt"), context = Path.Combine(Root, "receipt", "context.txt");
            Assert.True(File.Exists(items) && File.Exists(context), Describe(result));
            string[] inputs = File.ReadAllLines(items).Where(line => line.Length != 0).Select(Path.GetFullPath).ToArray();
            Assert.True(inputs.Count(path => PathComparer.Equals(path, Canonical)) == canonicalCount &&
                inputs.Count(path => PathComparer.Equals(path, Path.Combine(Root, "scripts", script + ".cs"))) == 1, Describe(result));
            string[] metadata = File.ReadAllLines(context);
            Assert.True(metadata.Contains("FileBasedProgram=true", StringComparer.OrdinalIgnoreCase) &&
                metadata.Any(line => line.StartsWith("SDK=", StringComparison.Ordinal) && line.Length > 4) &&
                metadata.Any(line => line.StartsWith("Project=", StringComparison.Ordinal) && line.Length > 8), Describe(result));
        }

        public string Describe(BuildRun result) => "Fixture " + Root + ": exit " + result.ExitCode + "\n" + result.Output;
        public void Succeeded() => _succeeded = true;
        public void Dispose()
        {
            // Preserve original output/receipts on assertion or invocation failure, including helper timeout limitations.
            if (_succeeded) _temporary.Dispose();
        }
    }
}
