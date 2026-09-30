using valheim_cli.Testing;
using Xunit;

/// <summary>
/// tools/dev-loop/smoke-plan.yaml, the plan to start a mod's dev loop from, read with the plan model of the transport
/// package (the same parser the valheim-cli executable runs): it must parse, and it must stay strict.
/// </summary>
public sealed class SmokePlanTests
{
    private static string Root => DevLoopScripts.RepositoryRoot();

    private static string PlanPath => Path.Combine(Root, "tools", "dev-loop", "smoke-plan.yaml");

    private static TestPlan Load() => TestPlan.Parse(File.ReadAllText(PlanPath));

    [Fact]
    public void ParsesWithTheTransportsPlanModel()
    {
        TestPlan plan = Load();
        Assert.Equal("Mod smoke test", plan.Name);
        Assert.Equal(new[] { "MainMenu", "InWorld" }, plan.Tests.Where(test => test.WaitFor != null).Select(test => test.WaitFor!.State));
        Assert.Contains(plan.Tests, test => test.Commands.Contains("${mod_command}"));
        Assert.Contains("cli_logout_save", plan.Cleanup);
    }

    [Fact]
    public void RequestsStrictPinsFromThePlanItself()
    {
        TestPlan plan = Load();
        Assert.True(plan.Game.ExpectStrict);
        Assert.False(string.IsNullOrWhiteSpace(plan.Game.Expect));

        // Without an --expect/--expect-strict override the runner checks the plan's own file, strictly, relative to the plan.
        ExpectationSource? source = PlanExpectations.Resolve(null, false, plan.Game.Expect, plan.Game.ExpectStrict, PlanPath);
        Assert.NotNull(source);
        Assert.True(source!.Strict);
        Assert.Equal("plan", source.From);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(PlanPath)!, plan.Game.Expect), source.Path);

        // No step loosens the check: any cli_expect in a step or cleanup is strict too.
        IEnumerable<string> commands = plan.Tests.SelectMany(test => test.Commands).Concat(plan.Cleanup);
        Assert.All(commands.Where(command => command.StartsWith("cli_expect", StringComparison.Ordinal)),
            command => Assert.Contains("--strict", command));
    }

    [Fact]
    public void DevLoopDocsAndScriptsPointAtThisPlan()
    {
        foreach (string relative in new[] { "tools/dev-loop/README.md", "tools/dev-loop/dev-loop.sh", "tools/dev-loop/dev-loop.ps1", "docs/getting-started.md" })
        {
            string text = File.ReadAllText(Path.Combine(new[] { Root }.Concat(relative.Split('/')).ToArray()));
            Assert.True(text.Contains("smoke-plan.yaml", StringComparison.Ordinal), relative + " does not name the smoke plan");
            Assert.False(text.Contains("examples/smoke-plan.yaml", StringComparison.Ordinal) || text.Contains(@"examples\smoke-plan.yaml", StringComparison.Ordinal),
                relative + " still points at ValheimCLI's examples/smoke-plan.yaml");
        }
    }
}
