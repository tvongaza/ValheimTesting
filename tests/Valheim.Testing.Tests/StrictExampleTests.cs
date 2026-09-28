using Xunit;

/// <summary>Runs the real shell helpers with inert tools; no game, build or deployment.</summary>
public sealed class StrictExampleTests : IDisposable
{
    private readonly DevLoopScripts _scripts = new();

    public void Dispose() => _scripts.Dispose();

    [UnixFact]
    public async Task PinHelperCheckAndRunAreStrict()
    {
        string cli = _scripts.Tool("cli", "#!/bin/sh\nprintf \"%s\\n\" \"$@\"\n");
        var environment = new Dictionary<string, string?> { ["VALHEIM_CLI"] = cli };
        string[][] cases =
        {
            new[] { "check", "pins with spaces.txt" },
            new[] { "check", "pins.txt", "--strict" },
            new[] { "run", "pins.txt", "cli_manifest" },
        };
        foreach (string[] args in cases)
        {
            ScriptRun run = await _scripts.Run("pin-mods.sh", args, environment);
            run.AssertExit(0);
            string[] lines = DevLoopScripts.Lines(run.Stdout);
            Assert.Contains("--expect-strict", lines);
            Assert.DoesNotContain("--expect", lines);
            Assert.Contains(args[1], lines);
        }
    }

    [UnixFact]
    public async Task DevLoopPlanWithoutPinsStopsBeforeTools()
    {
        ScriptRun run = await _scripts.Run("dev-loop.sh", new[] { "unused.csproj", "plan.yaml" },
            new Dictionary<string, string?> { ["VALHEIM_EXPECTATIONS"] = "", ["VALHEIM_CLI"] = "/does/not/exist" });
        run.AssertExit(4);
        Assert.Contains("VALHEIM_EXPECTATIONS", run.Stderr);
    }

    [UnixFact]
    public async Task DevLoopPassesPinsDerivedFromTheBuildToPlan()
    {
        string binary = _scripts.Folder("bin");
        _scripts.Folder("game/BepInEx/plugins");
        string dll = _scripts.PathOf("Test.dll");
        File.WriteAllText(dll, "inert test data");
        string pins = _scripts.PathOf("pins with spaces.txt");
        File.WriteAllText(pins, "core=01234567\nTest=11111111\n");
        string cli = _scripts.Tool("bin/cli",
            "#!/bin/sh\n" +
            "case \"$*\" in\n" +
            "  *--status*) echo \"local_process=false\";;\n" +
            "  *) printf \"%s\\n\" \"$@\" > \"$CAPTURE\"\n" +
            "     while [ $# -gt 0 ]; do if [ \"$1\" = --expect-strict ]; then cp \"$2\" \"$USED\"; fi; shift; done;;\n" +
            "esac\n");
        _scripts.Tool("bin/dotnet", "#!/bin/sh\nif [ \"$1\" = msbuild ]; then printf \"%s\\n\" \"$DLL\"; fi\n");
        string capture = _scripts.PathOf("args.txt");
        string used = _scripts.PathOf("used.txt");

        ScriptRun run = await _scripts.Run("dev-loop.sh", new[] { "unused.csproj", "plan.yaml" }, new Dictionary<string, string?>
        {
            ["PATH"] = DevLoopScripts.PathWith(binary),
            ["VALHEIM_CLI"] = cli,
            ["VALHEIM_PATH"] = _scripts.PathOf("game"),
            ["VALHEIM_EXPECTATIONS"] = pins,
            ["DLL"] = dll,
            ["CAPTURE"] = capture,
            ["USED"] = used,
            ["VALHEIM_PLUGIN_KEY"] = null,
        });

        run.AssertExit(0);
        string[] args = DevLoopScripts.Lines(File.ReadAllText(capture));
        Assert.Contains("--expect-strict", args);
        Assert.NotEqual(pins, args[Array.IndexOf(args, "--expect-strict") + 1]);
        Assert.Contains("--test", args);
        // Only the built plugin's pin changes, to the md5 of the file deployed; the supplied file is untouched.
        string fresh = DevLoopScripts.Md5Hex("inert test data");
        Assert.Equal(new[] { "core=01234567", "Test=" + fresh },
            DevLoopScripts.Lines(File.ReadAllText(used)).Select(line => line.Split("   #")[0]).ToArray());
        Assert.Equal("core=01234567\nTest=11111111\n", File.ReadAllText(pins));
    }
}
