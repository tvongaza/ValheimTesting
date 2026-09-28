using Xunit;

/// <summary>
/// Runs the real tools/dev-loop/dev-loop.ps1 under Windows PowerShell with inert tools; never builds or launches a game.
/// The cases of <see cref="DevLoopGuardTests"/>, which run its bash twin.
/// </summary>
public sealed class DevLoopGuardPowerShellTests : IDisposable
{
    private readonly DevLoopScripts _scripts = new();

    public void Dispose() => _scripts.Dispose();

    private async Task CheckStatus(string output, int status, int expected)
    {
        string binary = _scripts.Folder("bin");
        _scripts.Folder("game/BepInEx/plugins");
        string cli = _scripts.WindowsTool("fake-cli", "Write-Output $env:FAKE_STATUS\r\nexit [int]$env:FAKE_EXIT\r\n");
        _scripts.WindowsTool("dotnet", "Write-Output 'BUILD_REACHED'\r\nexit 77\r\n");

        ScriptRun run = await _scripts.Run("dev-loop.ps1", new[] { "unused.csproj" }, new Dictionary<string, string?>
        {
            ["PATH"] = DevLoopScripts.PathWith(binary),
            ["VALHEIM_CLI"] = cli,
            ["VALHEIM_PATH"] = _scripts.PathOf("game"),
            ["FAKE_STATUS"] = output,
            ["FAKE_EXIT"] = status.ToString(),
        });

        run.AssertExit(expected);
        Assert.Equal(expected == 77, run.Stdout.Contains("BUILD_REACHED"));
    }

    [WindowsFact]
    public Task RunningWithoutPlugin() => CheckStatus("game running=true local_process=true remote=false", 1, 5);

    [WindowsFact]
    public Task RunningWithPlugin() => CheckStatus("game local_process=true", 0, 5);

    [WindowsFact]
    public Task ExplicitlyStoppedWithUnavailablePlugin() => CheckStatus("game local_process=false", 1, 77);

    [WindowsFact]
    public Task StatusFailureWithoutProcessEvidence() => CheckStatus("", 1, 5);

    [WindowsFact]
    public Task ConflictingProcessEvidenceRefuses() => CheckStatus("local_process=false\nlocal_process=true", 0, 5);

    private sealed record PlanRun(ScriptRun Run, string Pins, string Used, string Installed);

    /// <summary>Runs with a plan against fakes: a stopped game and a build that succeeds.</summary>
    private async Task<PlanRun> RunPlan(string pinsText, Dictionary<string, string?>? extraEnvironment = null)
    {
        string binary = _scripts.Folder("bin");
        _scripts.Folder("game/BepInEx/plugins");
        _scripts.Folder("out");
        string dll = _scripts.PathOf("out/MyMod.dll");
        File.WriteAllText(dll, "fresh build");
        string pins = _scripts.PathOf("pins.txt");
        File.WriteAllText(pins, pinsText);
        string used = _scripts.PathOf("used-pins.txt");
        string cli = _scripts.WindowsTool("fake-cli",
            "if ($args -contains '--status') { Write-Output 'game local_process=false'; exit 1 }\r\n" +
            "for ($i = 0; $i -lt $args.Count - 1; $i++) {\r\n" +
            "    if ($args[$i] -eq '--expect-strict') { Copy-Item -LiteralPath $args[$i + 1] -Destination $env:USED_PINS }\r\n" +
            "}\r\n" +
            "exit 0\r\n");
        _scripts.WindowsTool("dotnet",
            "if ($args.Count -gt 0 -and $args[0] -eq 'msbuild') { Write-Output $env:FAKE_DLL } else { Write-Output 'BUILD_REACHED' }\r\n" +
            "exit 0\r\n");

        Dictionary<string, string?> environment = new()
        {
            ["PATH"] = DevLoopScripts.PathWith(binary),
            ["VALHEIM_CLI"] = cli,
            ["VALHEIM_PATH"] = _scripts.PathOf("game"),
            ["VALHEIM_EXPECTATIONS"] = pins,
            ["FAKE_DLL"] = dll,
            ["USED_PINS"] = used,
            ["VALHEIM_PLUGIN_KEY"] = null,
        };
        foreach ((string name, string? value) in extraEnvironment ?? new Dictionary<string, string?>())
            environment[name] = value;

        ScriptRun run = await _scripts.Run("dev-loop.ps1", new[] { "unused.csproj", "plan.yaml" }, environment);
        return new PlanRun(run, pins, used, _scripts.PathOf("game/BepInEx/plugins/MyMod.dll"));
    }

    [WindowsFact]
    public async Task PlanPinsTheDeployedBuildAndKeepsOtherLines()
    {
        const string original = "com.example.other=0123456789abcdef   # keep\nMyMod=11111111111111111111111111111111\nworld=Dev\n";
        PlanRun plan = await RunPlan(original);
        plan.Run.AssertExit(0);
        string fresh = DevLoopScripts.Md5Hex("fresh build");
        Assert.Equal(new[] { "com.example.other=0123456789abcdef   # keep", "MyMod=" + fresh, "world=Dev" },
            DevLoopScripts.Lines(File.ReadAllText(plan.Used)).Select(line => line.Split("   # derived")[0]).ToArray());
        Assert.Equal(original, File.ReadAllText(plan.Pins));
        Assert.Equal("fresh build", File.ReadAllText(plan.Installed));
    }

    [WindowsFact]
    public async Task PlanReplacesThePinNamedByPluginKey()
    {
        PlanRun plan = await RunPlan("com.example.mymod=11111111\n",
            new Dictionary<string, string?> { ["VALHEIM_PLUGIN_KEY"] = "com.example.mymod" });
        plan.Run.AssertExit(0);
        Assert.StartsWith("com.example.mymod=" + DevLoopScripts.Md5Hex("fresh build"), File.ReadAllText(plan.Used));
    }

    [WindowsFact]
    public async Task PlanWithoutAPinForTheBuildRefusesBeforeDeploying()
    {
        PlanRun plan = await RunPlan("com.example.other=any\n");
        plan.Run.AssertExit(4);
        Assert.False(File.Exists(plan.Used));
        Assert.False(File.Exists(plan.Installed));
    }
}
