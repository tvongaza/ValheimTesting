using Xunit;

/// <summary>Runs the real development script with inert tools; never builds or launches a game.</summary>
public sealed class DevLoopGuardTests : IDisposable
{
    private readonly DevLoopScripts _scripts = new();

    public void Dispose() => _scripts.Dispose();

    private async Task CheckStatus(string output, int status, int expected)
    {
        string binary = _scripts.Folder("bin");
        _scripts.Folder("game/BepInEx/plugins");
        string cli = _scripts.Tool("bin/fake-cli", "#!/bin/sh\nprintf \"%s\\n\" \"$FAKE_STATUS\"\nexit \"$FAKE_EXIT\"\n");
        _scripts.Tool("bin/dotnet", "#!/bin/sh\necho BUILD_REACHED\nexit 77\n");

        ScriptRun run = await _scripts.Run("dev-loop.sh", new[] { "unused.csproj" }, new Dictionary<string, string?>
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

    [UnixFact]
    public Task RunningWithoutPlugin() => CheckStatus("game running=true local_process=true remote=false", 1, 5);

    [UnixFact]
    public Task RunningWithPlugin() => CheckStatus("game local_process=true", 0, 5);

    [UnixFact]
    public Task ExplicitlyStoppedWithUnavailablePlugin() => CheckStatus("game local_process=false", 1, 77);

    [UnixFact]
    public Task StatusFailureWithoutProcessEvidence() => CheckStatus("", 1, 5);

    [UnixFact]
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
        string cli = _scripts.Tool("bin/fake-cli",
            "#!/bin/sh\n" +
            "case \" $* \" in *\" --status \"*) echo \"game local_process=false\"; exit 1;; esac\n" +
            "while [ $# -gt 0 ]; do\n" +
            "  if [ \"$1\" = --expect-strict ]; then cp \"$2\" \"$USED_PINS\"; fi\n" +
            "  shift\n" +
            "done\n");
        _scripts.Tool("bin/dotnet",
            "#!/bin/sh\n" +
            "if [ \"$1\" = msbuild ]; then printf \"%s\\n\" \"$FAKE_DLL\"; else echo BUILD_REACHED; fi\n");

        var environment = new Dictionary<string, string?>
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

        ScriptRun run = await _scripts.Run("dev-loop.sh", new[] { "unused.csproj", "plan.yaml" }, environment);
        return new PlanRun(run, pins, used, _scripts.PathOf("game/BepInEx/plugins/MyMod.dll"));
    }

    [UnixFact]
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

    [UnixFact]
    public async Task PlanReplacesThePinNamedByPluginKey()
    {
        PlanRun plan = await RunPlan("com.example.mymod=11111111\n",
            new Dictionary<string, string?> { ["VALHEIM_PLUGIN_KEY"] = "com.example.mymod" });
        plan.Run.AssertExit(0);
        Assert.StartsWith("com.example.mymod=" + DevLoopScripts.Md5Hex("fresh build"), File.ReadAllText(plan.Used));
    }

    [UnixFact]
    public async Task PlanWithoutAPinForTheBuildRefusesBeforeDeploying()
    {
        PlanRun plan = await RunPlan("com.example.other=any\n");
        plan.Run.AssertExit(4);
        Assert.False(File.Exists(plan.Used));
        Assert.False(File.Exists(plan.Installed));
    }
}
