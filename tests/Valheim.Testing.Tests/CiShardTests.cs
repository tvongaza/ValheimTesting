using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

// #725: scripts/validate.cs and scripts/ci-test-timings.json decide the toolkit test shards of Windows and macOS; the workflow only starts them.
// What the workflow must still agree on is checked here: a timed shard that no job starts would silently stop running its tests.
public sealed class CiShardTests
{
    [Fact]
    public void TheWorkflowStartsExactlyTheTimedShardsValidateCsDefines()
    {
        string root = FixtureProjects.RepositoryRoot();
        int count = int.Parse(Regex.Match(File.ReadAllText(Path.Combine(root, "scripts", "validate.cs")), @"const int TimedShardCount = (\d+);").Groups[1].Value);
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "test.yml"));
        var started = Regex.Matches(workflow, @"args: --ci-timed (\d+)\s").Select(match => int.Parse(match.Groups[1].Value)).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, count), started);
        Assert.Contains("args: --ci-main-tests", workflow);
    }

    [Fact]
    public void EveryWorkflowTestRunGoesThroughValidateCs()
    {
        string root = FixtureProjects.RepositoryRoot();
        string workflows = string.Join('\n', Directory.GetFiles(Path.Combine(root, ".github", "workflows"), "*.yml")
            .Order(StringComparer.Ordinal).Select(File.ReadAllText));
        string[] directTestCommands =
        [
            @"\bdotnet\s+test\b",
            @"\bdotnet\s+vstest\b",
            @"\bxunit\.console(?:\.exe)?\b",
            @"\b(?:dotnet\s+(?:build|msbuild)|msbuild(?:\.exe)?)\b[^\r\n]*(?:-t:|/t:)\S*test",
        ];
        foreach (string pattern in directTestCommands)
            Assert.False(Regex.IsMatch(workflows, pattern, RegexOptions.IgnoreCase), $"workflow test command bypasses validate.cs: {pattern}");

        string testWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "test.yml"));
        Assert.Contains("--test-project \"$GITHUB_WORKSPACE\" \"$GITHUB_WORKSPACE/tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj\"", testWorkflow, StringComparison.Ordinal);
        Assert.Contains("--test-project \"$GITHUB_WORKSPACE\" \"$RUNNER_TEMP/mod/MyMod.Tests/MyMod.Tests.csproj\"", testWorkflow, StringComparison.Ordinal);
        Assert.Contains("--target RunTestsOnBothFrameworks", testWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTimedMethodOfTheManifestIsATestOfThisProject()
    {
        string root = FixtureProjects.RepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "ci-test-timings.json")));
        var tests = typeof(CiShardTests).Assembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes().Any(attribute => attribute is FactAttribute))
            .Select(method => type.Name + "." + method.Name)).ToHashSet(StringComparer.Ordinal);
        var unknown = manifest.RootElement.EnumerateArray().Select(item => item.GetProperty("Name").GetString()!).Where(name => !tests.Contains(name)).ToArray();
        Assert.True(unknown.Length == 0, "ci-test-timings.json names methods that are not tests (renamed or removed?): " + string.Join(", ", unknown));
    }
}
