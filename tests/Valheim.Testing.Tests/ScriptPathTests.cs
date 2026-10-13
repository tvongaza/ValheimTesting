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
}
