using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class ServerLoadPhasesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-server-phases-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task EachPhaseCanSelectRepeatedOpaqueEnvironmentValuesWithoutCapturingThemAsPaths()
    {
        string planFile = Plan(), output = Path.Combine(_root, "environment-phases");
        var plan = JsonSerializer.Deserialize<ServerLoadPhases.Plan>(File.ReadAllText(planFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        for (int index = 0; index < plan.Phases.Length; index++)
            plan.Phases[index].Args = [.. plan.Phases[index].Args, "--env", "MOD_SETTING=" + ExplicitRunInputsTests.Opaque,
                "--env", "PHASE=" + index];
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
        int calls = 0;
        int result = await ServerLoadPhases.RunAsync(["--plan", planFile, "--output", output], args =>
        {
            Assert.True(ServerLoad.TryRead(args, out var parsed, out string error), error);
            Assert.Equal(ExplicitRunInputsTests.Opaque, parsed!.RunInputs.Environment["MOD_SETTING"]);
            Assert.Equal((calls++).ToString(), parsed.RunInputs.Environment["PHASE"]);
            FakeInstalls.World(Path.Combine(args[Array.IndexOf(args, "--bake-fixture") + 1], "worlds_local"));
            return Task.FromResult(0);
        });
        Assert.Equal(0, result);
        Assert.Equal(2, calls);
        Assert.Contains("MOD_SETTING=", File.ReadAllText(Path.Combine(output, "phase-plan.json")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ClientRoleOptionsNeverStartAPhaseOrCreateItsOutput(bool common)
    {
        string file = Plan(), output = Path.Combine(_root, "client-refused");
        var plan = JsonSerializer.Deserialize<ServerLoadPhases.Plan>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (common) plan.CommonArgs = [.. plan.CommonArgs, "--client-mod", "client.dll"];
        else plan.Phases[0].Args = [.. plan.Phases[0].Args, "--client-assert-command", "probe", "--client-assert-line", "READY"];
        File.WriteAllText(file, JsonSerializer.Serialize(plan)); int calls = 0;
        Assert.Equal(3, await ServerLoadPhases.RunAsync(["--plan", file, "--output", output], _ => { calls++; return Task.FromResult(0); }));
        Assert.Equal(0, calls); Assert.False(Directory.Exists(output));
    }

    private string Plan(bool keepFinal = true)
    {
        string fixture = FakeInstalls.World(Path.Combine(_root, "source-fixture"));
        string server = Path.Combine(_root, "server");
        FakeInstalls.Server(server);
        Directory.CreateDirectory(Path.Combine(server, InstallPins.CoreDirectory)); // the core the mod resolves against
        File.WriteAllText(Path.Combine(server, "server-marker"), "source server");
        File.WriteAllText(Path.Combine(_root, "first.dll"), "first plugin");
        File.WriteAllText(Path.Combine(_root, "second.dll"), "second plugin");
        File.WriteAllText(Path.Combine(_root, "second.cfg"), "second config");
        string bundle = Path.Combine(_root, "cli-bundle");
        Directory.CreateDirectory(bundle);
        string manifest = Path.Combine(bundle, "cli-manifest.json");
        File.WriteAllText(manifest, "{\"schema\":1,\"build\":\"fixture\",\"files\":[{\"file\":\"valheimCLI.dll\",\"sha256\":\"" +
            new string('a', 64) + "\",\"plugins\":[\"valheimCLI.valheimCLI\"],\"extensions\":{}}]}");
        string plan = Path.Combine(_root, "plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new
        {
            schema = 1, worldFixture = fixture, keepFinal,
            commonArgs = new[] { "--server", server, "--cli-manifest", manifest, "--cli-files", bundle },
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
    [InlineData("source")]
    [InlineData("host")]
    public async Task TheInitialSourceAndHostCannotBeRepinnedDuringLoaderSelection(string change)
    {
        string plan = Plan(), output = Path.Combine(_root, "refused"); int phases = 0, loaders = 0;
        int result = await ServerLoadPhases.RunAsync(["--plan", plan, "--output", output], _ => { phases++; return Task.FromResult(0); },
            shippedLoader: (_, _, _) =>
            {
                loaders++;
                if (change == "host") File.WriteAllBytes(InstallPins.GameAssembly(Path.Combine(_root, "server")), FakeInstalls.GameAssembly(maximum: 43));
                else File.WriteAllText(Path.Combine(_root, "source-fixture", "Campaign", "_main.1.db2"), "new data");
                return null;
            });
        Assert.Equal(3, result); Assert.Equal(1, loaders); Assert.Equal(0, phases); Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("host")]
    public async Task ACheckpointOrCompatibleHostReplacementIsRefusedBeforeTheNextPhase(string change)
    {
        string plan = Plan(), output = Path.Combine(_root, "refused"); int phases = 0;
        int result = await ServerLoadPhases.RunAsync(["--plan", plan, "--output", output], args =>
        {
            phases++;
            string worlds = FakeInstalls.World(Path.Combine(args[Array.IndexOf(args, "--bake-fixture") + 1], "worlds_local"));
            if (change == "host") File.WriteAllBytes(InstallPins.GameAssembly(Path.Combine(_root, "server")), FakeInstalls.GameAssembly(maximum: 43));
            else File.WriteAllBytes(Path.Combine(worlds, "Campaign", "_main.1.fwl2"), FakeInstalls.Metadata("Campaign", 4242, version: 43));
            return Task.FromResult(0);
        });
        Assert.Equal(1, result); Assert.Equal(1, phases);
        Assert.True(Directory.Exists(Path.Combine(output, "checkpoints", "01-first")));
        Assert.False(Directory.Exists(Path.Combine(output, "phases", "02-second")));
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
            FakeInstalls.World(Path.Combine(checkpoint, "worlds_local"));
            return Task.FromResult(0);
        });

        Assert.Equal(0, result);
        Assert.Equal(2, calls.Count);
        Assert.Equal(Path.Combine(_root, "source-fixture"), calls[0][Array.IndexOf(calls[0], "--world-fixture") + 1]);
        Assert.Equal(calls[0][Array.IndexOf(calls[0], "--bake-fixture") + 1],
            calls[1][Array.IndexOf(calls[1], "--world-fixture") + 1]);
        Assert.False(Directory.Exists(Path.Combine(output, "checkpoints", "01-first")));
        Assert.Equal(keepFinal, Directory.Exists(Path.Combine(output, "checkpoints", "02-second")));
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(_root, "source-fixture", "Campaign", "_main.1.db2")));
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
            if (calls == 1) FakeInstalls.World(Path.Combine(args[Array.IndexOf(args, "--bake-fixture") + 1], "worlds_local"));
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
    public async Task OutputInsideTheServerInstallIsRefusedBeforeAnyWrite()
    {
        string plan = Plan(), server = Path.Combine(_root, "server");
        string output = Path.Combine(server, "phase-run");
        int calls = 0;

        int result = await ServerLoadPhases.RunAsync(["--plan", plan, "--output", output], _ =>
        {
            calls++;
            return Task.FromResult(0);
        });

        Assert.Equal(3, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
        Assert.Equal("source server", File.ReadAllText(Path.Combine(server, "server-marker")));
    }

    // #745: the phases take the server's loader from the mod-input stage, so the pinned pack a phase will run with (#711) is
    // protected too, not only a --loader-package or an environment's loaderPackage.
    [Fact]
    public async Task ThePinnedLoaderChosenByTheStageIsProtectedBeforeAnyWrite()
    {
        using var rig = new RegressionRig();
        var source = BepInExLoaderPackage.Capture(rig.Game, "pinned-pack", "1");
        string root = Path.Combine(_root, "pinned-loader");
        foreach (string relative in source.Files.Keys)
        {
            string destination = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(rig.Game, relative.Replace('/', Path.DirectorySeparatorChar)), destination);
        }
        string manifest = Path.Combine(_root, "pinned-loader.json");
        BepInExLoaderPackage.Capture(root, "pinned-pack", "2").Write(manifest);
        string output = Path.Combine(root, "phase-run");
        var asked = new List<string>();
        int calls = 0;

        int result = await ServerLoadPhases.RunAsync(["--plan", Plan(), "--output", output], _ => { calls++; return Task.FromResult(0); },
            (role, _, _) => { asked.Add(role); return new ShippedLoader.Choice(manifest, "the pinned pack"); });

        Assert.Equal(3, result);
        Assert.Equal(["server"], asked);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task InventorySelectedServerAlsoProtectsItsInstallBeforeAnyWrite()
    {
        string plan = Plan(), server = Path.Combine(_root, "server");
        string inventory = Path.Combine(_root, "inventory.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            environments = new[] { new { name = "server", roles = new[] { "server" }, install = server } },
        }));
        var parsed = JsonSerializer.Deserialize<ServerLoadPhases.Plan>(File.ReadAllText(plan),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        parsed.CommonArgs = ["--inventory", inventory, .. parsed.CommonArgs.Skip(2)];
        File.WriteAllText(plan, JsonSerializer.Serialize(parsed));
        string output = Path.Combine(server, "inventory-phase-run");

        int result = await ServerLoadPhases.RunAsync(["--plan", plan, "--output", output], _ => Task.FromResult(0));

        Assert.Equal(3, result);
        Assert.False(Directory.Exists(output));
        Assert.Equal("source server", File.ReadAllText(Path.Combine(server, "server-marker")));
    }

    [Fact]
    public async Task ImplicitCliFilesDirectoryIsProtectedBeforeAnyWrite()
    {
        string plan = Plan(), bundle = Path.Combine(_root, "cli-bundle");
        var parsed = JsonSerializer.Deserialize<ServerLoadPhases.Plan>(File.ReadAllText(plan),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        parsed.CommonArgs = parsed.CommonArgs.Where((_, index) => index < 4).ToArray(); // manifest, but no --cli-files
        File.WriteAllText(plan, JsonSerializer.Serialize(parsed));
        string output = Path.Combine(bundle, "phase-run");
        int calls = 0;

        int result = await ServerLoadPhases.RunAsync(["--plan", plan, "--output", output], _ =>
        {
            calls++;
            return Task.FromResult(0);
        });

        Assert.Equal(3, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
        Assert.True(File.Exists(Path.Combine(bundle, "cli-manifest.json")));
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
        Assert.True(File.Exists(Path.Combine(_root, "source-fixture", "Campaign", "_main.1.fwl2")));
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
            FakeInstalls.World(Path.Combine(checkpoint, "worlds_local"));
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
            FakeInstalls.World(Path.Combine(checkpoint, "worlds_local"));
            File.WriteAllText(Path.Combine(_root, "second.dll"), "changed plugin");
            return Task.FromResult(0);
        });
        Assert.Equal(1, result);
        Assert.Equal(1, calls);
        Assert.True(Directory.Exists(Path.Combine(output, "checkpoints", "01-first")));
        Assert.Contains("failed", File.ReadAllText(Path.Combine(output, "phase-state.json")));
    }

    // #653: each server-load owns its profile source verdict. The wrapper no longer re-hashes the full server directory
    // before every phase, so Steam output there is not misreported as a phased build-input change.
    [Fact]
    public async Task SteamWritingServerLogsBetweenPhasesDoesNotFailTheNextPhase()
    {
        string output = Path.Combine(_root, "steam-log-run");
        int calls = 0;
        int result = await ServerLoadPhases.RunAsync(["--plan", Plan(), "--output", output], args =>
        {
            calls++;
            string checkpoint = args[Array.IndexOf(args, "--bake-fixture") + 1];
            Directory.CreateDirectory(checkpoint);
            FakeInstalls.World(Path.Combine(checkpoint, "worlds_local"));
            if (calls == 1)
            {
                string log = Path.Combine(_root, "server", "logs", "connection_log.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.WriteAllText(log, "Steam runtime output");
            }
            return Task.FromResult(0);
        });

        Assert.Equal(0, result);
        Assert.Equal(2, calls);
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
