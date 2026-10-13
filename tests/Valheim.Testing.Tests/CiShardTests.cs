using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

// #725: scripts/validate.cs and scripts/ci-test-timings.json decide the toolkit test shards of Windows and macOS; the workflow only starts them.
// What the workflow must still agree on is checked here: a timed shard that no job starts would silently stop running its tests.
public sealed class CiShardTests
{
    private static readonly string[] DirectTestCommands =
    [
        @"\bdotnet\s+test\b",
        @"\bdotnet\s+vstest\b",
        @"\bxunit\.console(?:\.exe)?\b",
        @"\b(?:dotnet\s+(?:build|msbuild)|msbuild(?:\.exe)?)\b[^\r\n]*(?:-t:|/t:|-target:|/target:|--target:)\S*test",
        "\\bdotnet\\s+run\\b[^\\r\\n]*--project[=\\s]+['\\\"]?(?:\\S*[/\\\\])?tests[/\\\\]",
    ];

    [Fact]
    public void TheWorkflowStartsExactlyTheTimedShardsValidateCsDefines()
    {
        string root = FixtureProjects.RepositoryRoot();
        int count = int.Parse(Regex.Match(File.ReadAllText(Path.Combine(root, "scripts", "Shared", "ValidationSupport.cs")), @"const int TimedShardCount = (\d+);").Groups[1].Value);
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "test.yml"));
        var started = Regex.Matches(workflow, @"args: --ci-timed (\d+)\s").Select(match => int.Parse(match.Groups[1].Value)).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, count), started);
        Assert.Contains("args: --ci-main-tests", workflow);
    }

    [Fact]
    public void EveryWorkflowTestRunGoesThroughValidateCs()
    {
        string root = FixtureProjects.RepositoryRoot();
        var automation = Directory.GetFiles(Path.Combine(root, ".github", "workflows"), "*.yml")
            .Concat(Directory.GetFiles(Path.Combine(root, ".github", "workflows"), "*.yaml"))
            .Concat(Directory.GetFiles(Path.Combine(root, ".github", "scripts"), "*.sh"))
            .Concat(Directory.GetFiles(Path.Combine(root, ".github", "actions"), "action.yml", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(Path.Combine(root, ".github", "actions"), "action.yaml", SearchOption.AllDirectories))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (string example in new[]
        {
            "dotnet build Tests.csproj -target:RunTests", "dotnet msbuild Tests.csproj /target:RunTests",
            "msbuild Tests.csproj --target:RunTests", "msbuild Tests.csproj -t:RunTests",
            "dotnet run --project tests/Valheim.Testing.NativeAcceptance",
            "dotnet run --project \"$GITHUB_WORKSPACE/tests/Valheim.Testing.NativeAcceptance\"",
            "dotnet run --project ./tests/Valheim.Testing.NativeAcceptance",
            "dotnet run --project=tests/Valheim.Testing.NativeAcceptance",
            "dotnet run --project .\\tests\\Valheim.Testing.NativeAcceptance",
        })
            Assert.Contains(DirectTestCommands, pattern => DirectTestCommandMatches(example, pattern).Any());
        const string nativeChecks = ".github/scripts/native-server-checks.sh";
        const string nativeReason = "live-game native acceptance needs its authorized disposable server fixture, so validate.cs's no-game limits do not own it";
        const string macosSampler = ".github/scripts/macos-sampler.sh";
        const string macosSamplerLine = "grep -E 'Runner\\.(Worker|Listener)|testhost|validate\\.cs|dotnet test'";
        const string macosSamplerReason = "the macOS diagnostic sampler only searches the process list for test commands; it does not start one";
        bool foundNativeException = false;
        bool foundMacosSamplerException = false;
        foreach (string file in automation)
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string text = File.ReadAllText(file);
            foreach (string pattern in DirectTestCommands)
                foreach (Match match in DirectTestCommandMatches(text, pattern))
                {
                    if (relative == nativeChecks && match.Value.Contains("tests/Valheim.Testing.NativeAcceptance", StringComparison.Ordinal))
                    {
                        foundNativeException = true;
                        continue; // Named exception: nativeReason above; this is the live-game suite, outside the no-game limits.
                    }
                    if (relative == macosSampler && match.Value.Contains(macosSamplerLine, StringComparison.Ordinal))
                    {
                        foundMacosSamplerException = true;
                        continue; // Named exception: macosSamplerReason above; grep observes processes and runs no tests.
                    }
                    Assert.Fail($"{relative} test command bypasses validate.cs: {match.Value.Trim()}");
                }
        }
        Assert.True(foundNativeException, $"The named exception disappeared; remove it or retain its reason: {nativeReason}");
        Assert.True(foundMacosSamplerException, $"The named exception disappeared; remove it or retain its reason: {macosSamplerReason}");

        string directScriptPattern = "\\b(?:Run|Execute)\\s*\\([^\\r\\n]*\"dotnet\"\\s*,\\s*\"test\"";
        Assert.Matches(directScriptPattern, "Run(environment, directory, \"dotnet\", \"test\", project)");
        foreach (string script in Directory.GetFiles(Path.Combine(root, "scripts"), "*.cs").Order(StringComparer.Ordinal))
            Assert.False(Regex.IsMatch(File.ReadAllText(script), directScriptPattern),
                $"{Path.GetRelativePath(root, script)} runs dotnet test directly instead of through validate.cs");
        string consumer = File.ReadAllText(Path.Combine(root, "scripts", "consumer.cs"));
        Assert.Contains("Path.Combine(root, \"scripts\", \"validate.cs\")", consumer, StringComparison.Ordinal);
        Assert.Contains("\"--test-project\", root, modProject", consumer, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(root, \"artifacts\", \"validate\", \"consumer\")", consumer, StringComparison.Ordinal);

        string testWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "test.yml"));
        Assert.Contains("--test-project \"$GITHUB_WORKSPACE\" \"$GITHUB_WORKSPACE/tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj\"", testWorkflow, StringComparison.Ordinal);
        Assert.Contains("--test-project \"$GITHUB_WORKSPACE\" \"$RUNNER_TEMP/mod/MyMod.Tests/MyMod.Tests.csproj\"", testWorkflow, StringComparison.Ordinal);
        Assert.Contains("--target RunTestsOnBothFrameworks", testWorkflow, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(testWorkflow, @"^\s*VT_TEST_RUNNER_PREFIX:", RegexOptions.Multiline).Cast<Match>());
        Assert.Single(Regex.Matches(testWorkflow, @"^\s*set \+e\s*$", RegexOptions.Multiline).Cast<Match>());
        Assert.Single(Regex.Matches(testWorkflow, @"PIPESTATUS\[0\]").Cast<Match>());
    }

    [Fact]
    public void TheWorkflowGuardMatchesDirectTestCommandsInCrlfText()
    {
        Match match = Assert.Single(DirectTestCommandMatches("echo before\r\ndotnet test Tests.csproj\r\necho after\r\n", DirectTestCommands[0]));
        Assert.Equal("dotnet test Tests.csproj", match.Value);
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

    private static Match[] DirectTestCommandMatches(string text, string pattern) =>
        Regex.Matches(text.Replace("\r\n", "\n", StringComparison.Ordinal), @"(?m)^(?!\s*#)[^\n]*(?:" + pattern + @")[^\n]*$", RegexOptions.IgnoreCase)
            .Cast<Match>().ToArray();
}
