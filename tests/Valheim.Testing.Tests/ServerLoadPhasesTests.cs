using System.Text.Json;
using Xunit;

public sealed class ServerLoadPhasesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-server-phases-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Plan(bool keepFinal = true)
    {
        string fixture = Path.Combine(_root, "source-fixture");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "world.fwl"), "source world");
        string server = Path.Combine(_root, "server");
        Directory.CreateDirectory(server);
        File.WriteAllText(Path.Combine(server, "server-marker"), "source server");
        File.WriteAllText(Path.Combine(_root, "first.dll"), "first plugin");
        File.WriteAllText(Path.Combine(_root, "second.dll"), "second plugin");
        File.WriteAllText(Path.Combine(_root, "second.cfg"), "second config");
        string plan = Path.Combine(_root, "plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new
        {
            schema = 1, worldFixture = fixture, keepFinal,
            commonArgs = new[] { "--server", server },
            phases = new[]
            {
                new { name = "first", args = new[] { "--mod", Path.Combine(_root, "first.dll"), "--assert-command", "first_ready", "--assert-line", "READY" } },
                new { name = "second", args = new[] { "--mod", Path.Combine(_root, "second.dll"), "--config", Path.Combine(_root, "second.cfg"),
                    "--assert-command", "second_ready", "--assert-line", "READY" } },
            },
        }));
        return plan;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachPhaseUsesThePreviousConfirmedFixtureAndOnlyTheRequestedFinalOneRemains(bool keepFinal)
    {
        string plan = Plan(keepFinal), output = Path.Combine(_root, "run");
        var calls = new List<string[]>();
        int result = await ServerLoadPhases.RunAsync(["--plan", plan, "--output", output], args =>
        {
            calls.Add(args);
            string checkpoint = args[Array.IndexOf(args, "--bake-fixture") + 1];
            Directory.CreateDirectory(checkpoint);
            File.WriteAllText(Path.Combine(checkpoint, "world.fwl"), "saved phase " + calls.Count);
            return Task.FromResult(0);
        });

        Assert.Equal(0, result);
        Assert.Equal(2, calls.Count);
        Assert.Equal(Path.Combine(_root, "source-fixture"), calls[0][Array.IndexOf(calls[0], "--world-fixture") + 1]);
        Assert.Equal(calls[0][Array.IndexOf(calls[0], "--bake-fixture") + 1],
            calls[1][Array.IndexOf(calls[1], "--world-fixture") + 1]);
        Assert.False(Directory.Exists(Path.Combine(output, "checkpoints", "01-first")));
        Assert.Equal(keepFinal, Directory.Exists(Path.Combine(output, "checkpoints", "02-second")));
        Assert.Equal("source world", File.ReadAllText(Path.Combine(_root, "source-fixture", "world.fwl")));
        Assert.True(File.Exists(Path.Combine(output, "phase-plan.json")));
    }

    [Fact]
    public async Task AFailedSecondPhaseKeepsItsLastGoodCheckpointAndNeverStartsAnotherPhase()
    {
        string output = Path.Combine(_root, "failed-run");
        int calls = 0;
        int result = await ServerLoadPhases.RunAsync(["--plan", Plan(), "--output", output], args =>
        {
            calls++;
            if (calls == 1) Directory.CreateDirectory(args[Array.IndexOf(args, "--bake-fixture") + 1]);
            return Task.FromResult(calls == 1 ? 0 : 1);
        });
        Assert.Equal(1, result);
        Assert.Equal(2, calls);
        Assert.True(Directory.Exists(Path.Combine(output, "checkpoints", "01-first")));
        Assert.Contains("failed", File.ReadAllText(Path.Combine(output, "phase-state.json")));
    }

    [Fact]
    public async Task APhaseCannotOverrideOwnedFixtureOrOutputPaths()
    {
        string plan = Plan(), output = Path.Combine(_root, "refused-run");
        string json = File.ReadAllText(plan).Replace("\"--mod\"", "\"--output\"", StringComparison.Ordinal);
        File.WriteAllText(plan, json);
        int calls = 0;
        int result = await ServerLoadPhases.RunAsync(["--plan", plan, "--output", output], _ => { calls++; return Task.FromResult(0); });
        Assert.Equal(3, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task AReportedSuccessWithoutAConfirmedFixtureStopsBeforeTheNextPhase()
    {
        string output = Path.Combine(_root, "unconfirmed-run");
        int calls = 0;
        int result = await ServerLoadPhases.RunAsync(["--plan", Plan(), "--output", output], _ =>
        {
            calls++;
            return Task.FromResult(0);
        });
        Assert.Equal(1, result);
        Assert.Equal(1, calls);
        Assert.Contains("failed", File.ReadAllText(Path.Combine(output, "phase-state.json")));
        Assert.True(File.Exists(Path.Combine(_root, "source-fixture", "world.fwl")));
    }

    [Fact]
    public async Task PackagedSourceIsVerifiedAndRetiredAfterTheLastBake()
    {
        string plan = Plan(), output = Path.Combine(_root, "packaged-run");
        var parsed = JsonSerializer.Deserialize<ServerLoadPhases.Plan>(File.ReadAllText(plan),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        parsed.WorldFixture = "packaged";
        File.WriteAllText(plan, JsonSerializer.Serialize(parsed));
        string? firstInput = null;
        int result = await ServerLoadPhases.RunAsync(["--plan", plan, "--output", output], args =>
        {
            string source = args[Array.IndexOf(args, "--world-fixture") + 1];
            firstInput ??= source;
            string checkpoint = args[Array.IndexOf(args, "--bake-fixture") + 1];
            Directory.CreateDirectory(checkpoint);
            File.WriteAllText(Path.Combine(checkpoint, "world.fwl"), "saved");
            return Task.FromResult(0);
        });
        Assert.Equal(0, result);
        Assert.Equal(Path.Combine(output, "source", "worlds_local"), firstInput);
        Assert.False(Directory.Exists(Path.Combine(output, "source")));
        Assert.True(Directory.Exists(Path.Combine(output, "checkpoints", "02-second")));
    }

    [Fact]
    public async Task ChangedModBetweenPhasesRefusesBeforeSecondServerStarts()
    {
        string output = Path.Combine(_root, "changed-mod-run");
        int calls = 0;
        int result = await ServerLoadPhases.RunAsync(["--plan", Plan(), "--output", output], args =>
        {
            calls++;
            string checkpoint = args[Array.IndexOf(args, "--bake-fixture") + 1];
            Directory.CreateDirectory(checkpoint);
            File.WriteAllText(Path.Combine(checkpoint, "world.fwl"), "saved first phase");
            File.WriteAllText(Path.Combine(_root, "second.dll"), "changed plugin");
            return Task.FromResult(0);
        });
        Assert.Equal(1, result);
        Assert.Equal(1, calls);
        Assert.True(Directory.Exists(Path.Combine(output, "checkpoints", "01-first")));
        Assert.Contains("failed", File.ReadAllText(Path.Combine(output, "phase-state.json")));
    }

    [Fact]
    public async Task ARefusedPinStopsBeforeTheNextPhaseAndPublishesNoCheckpoint()
    {
        string output = Path.Combine(_root, "pin-refused");
        int calls = 0;
        int result = await ServerLoadPhases.RunAsync(["--plan", Plan(), "--output", output], _ =>
        {
            calls++;
            return Task.FromResult(3); // the underlying server-load refused an exact plugin pin
        });
        Assert.Equal(3, result);
        Assert.Equal(1, calls);
        Assert.False(Directory.Exists(Path.Combine(output, "checkpoints", "01-first")));
        Assert.Contains("failed", File.ReadAllText(Path.Combine(output, "phase-state.json")));
    }
}
