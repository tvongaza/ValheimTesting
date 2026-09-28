using Xunit;

/// <summary>
/// Runs the real PowerShell helpers under Windows PowerShell with inert tools; no game, build or deployment.
/// The cases of <see cref="StrictExampleTests"/>, which run their bash twins.
/// </summary>
public sealed class StrictExamplePowerShellTests : IDisposable
{
    private readonly DevLoopScripts _scripts = new();

    public void Dispose() => _scripts.Dispose();

    [WindowsFact]
    public async Task PinHelperCheckAndRunAreStrict()
    {
        string cli = _scripts.WindowsTool("cli", "foreach ($a in $args) { Write-Output $a }\r\nexit 0\r\n");
        Dictionary<string, string?> environment = new() { ["VALHEIM_CLI"] = cli };
        string[][] cases =
        {
            new[] { "check", "pins with spaces.txt" },
            new[] { "check", "pins.txt", "--strict" },
            new[] { "run", "pins.txt", "cli_manifest" },
        };
        foreach (string[] args in cases)
        {
            ScriptRun run = await _scripts.Run("pin-mods.ps1", args, environment);
            run.AssertExit(0);
            string[] lines = DevLoopScripts.Lines(run.Stdout);
            Assert.Contains("--expect-strict", lines);
            Assert.DoesNotContain("--expect", lines);
            Assert.Contains(args[1], lines);
        }
    }

    [WindowsFact]
    public async Task DevLoopPlanWithoutPinsStopsBeforeTools()
    {
        ScriptRun run = await _scripts.Run("dev-loop.ps1", new[] { "unused.csproj", "plan.yaml" },
            new Dictionary<string, string?> { ["VALHEIM_EXPECTATIONS"] = "", ["VALHEIM_CLI"] = _scripts.PathOf("does-not-exist.cmd") });
        run.AssertExit(4);
        Assert.Contains("VALHEIM_EXPECTATIONS", run.Stderr);
    }

    [WindowsFact]
    public async Task DevLoopPassesPinsDerivedFromTheBuildToPlan()
    {
        string binary = _scripts.Folder("bin");
        _scripts.Folder("game/BepInEx/plugins");
        string dll = _scripts.PathOf("Test.dll");
        File.WriteAllText(dll, "inert test data");
        string pins = _scripts.PathOf("pins with spaces.txt");
        File.WriteAllText(pins, "core=01234567\nTest=11111111\n");
        string cli = _scripts.WindowsTool("cli",
            "if (\"$args\" -like '*--status*') { Write-Output 'local_process=false'; exit 0 }\r\n" +
            "[IO.File]::WriteAllLines($env:CAPTURE, [string[]]$args)\r\n" +
            "for ($i = 0; $i -lt $args.Count - 1; $i++) {\r\n" +
            "    if ($args[$i] -eq '--expect-strict') { Copy-Item -LiteralPath $args[$i + 1] -Destination $env:USED }\r\n" +
            "}\r\n" +
            "exit 0\r\n");
        _scripts.WindowsTool("dotnet", "if ($args.Count -gt 0 -and $args[0] -eq 'msbuild') { Write-Output $env:DLL }\r\nexit 0\r\n");
        string capture = _scripts.PathOf("args.txt");
        string used = _scripts.PathOf("used.txt");

        ScriptRun run = await _scripts.Run("dev-loop.ps1", new[] { "unused.csproj", "plan.yaml" }, new Dictionary<string, string?>
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
