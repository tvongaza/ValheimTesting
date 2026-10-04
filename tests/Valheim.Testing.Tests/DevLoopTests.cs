using System.ComponentModel;
using Xunit;

/// <summary>
/// tools/dev-loop/dev-loop.cs, compiled into this project, run in-process against a fake tool runner and a temporary
/// game folder: nothing is built, installed into a real game or launched. One class for every OS.
/// </summary>
public sealed class DevLoopTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dev-loop-").FullName;
    private readonly List<(string Tool, string[] Arguments, string? Directory)> _calls = new();
    private readonly Dictionary<string, string?> _environment = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    /// <summary>What the fake valheim-cli --status prints and how it exits; null throws as a tool that does not start.</summary>
    private (string Text, int Exit)? _status = ("game local_process=false", 1);
    private int _buildExit;
    private int _planExit;
    private string? _usedPins;

    public DevLoopTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        File.WriteAllText(Dll, "fresh build");
        File.WriteAllText(Path.Combine(_root, "MyMod.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "plan.yaml"), "name: inert");
        File.WriteAllText(Pins, "com.example.other=0123456789abcdef   # keep\nMyMod=11111111111111111111111111111111\nworld=Dev\n");
        _environment["VALHEIM_PATH"] = Path.Combine(_root, "game");
        _environment["VALHEIM_CLI"] = "fake-cli";
        _environment["VALHEIM_EXPECTATIONS"] = Pins;
        _environment["PWD"] = _root;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Dll => Path.Combine(_root, "out", "MyMod.dll");
    private string Pins => Path.Combine(_root, "pins.txt");
    private string Installed => Path.Combine(_root, "game", "BepInEx", "plugins", "MyMod.dll");
    private string Log => Path.Combine(_root, "game", "BepInEx", "LogOutput.log");
    // Worked out here, not with the script's own DevLoop.Md5.
    private static string FreshMd5 => Convert.ToHexString(System.Security.Cryptography.MD5.HashData("fresh build"u8.ToArray())).ToLowerInvariant();

    private ToolRun Tools(string tool, IReadOnlyList<string> arguments, bool capture, string? directory)
    {
        _calls.Add((tool, arguments.ToArray(), directory));
        if (tool == "fake-cli" && arguments.Contains("--status"))
            return _status is { } status ? new ToolRun(status.Exit, status.Text, "") : throw new Win32Exception("not found");
        if (tool == "dotnet" && arguments[0] == "build") return new ToolRun(_buildExit, "", "");
        if (tool == "dotnet" && arguments[0] == "msbuild") return new ToolRun(0, Dll + "\n", "");
        if (tool == "fake-cli")
        {
            int strict = Array.IndexOf(arguments.ToArray(), "--expect-strict");
            if (strict >= 0) _usedPins = File.ReadAllText(arguments[strict + 1]);
            return new ToolRun(_planExit, "", "");
        }
        throw new InvalidOperationException("unexpected tool " + tool);
    }

    private int Run(params string[] args) => DevLoop.Run(args, name => _environment.GetValueOrDefault(name), Tools, _output, _error);

    private bool Built => _calls.Any(call => call.Tool == "dotnet");

    private string[] PlanArguments => _calls.Single(call => call.Tool == "fake-cli" && !call.Arguments.Contains("--status")).Arguments;

    [Theory]
    [InlineData("game running=true local_process=true remote=false", 1, 5)]
    [InlineData("game local_process=true", 0, 5)]
    [InlineData("game local_process=false", 1, 0)]
    [InlineData("", 1, 5)]
    [InlineData("local_process=false\nlocal_process=true", 0, 5)]
    public void DeploysOnlyToAGameStatusReportsStopped(string status, int statusExit, int expected)
    {
        _status = (status, statusExit);
        Assert.Equal(expected, Run("MyMod.csproj"));
        Assert.Equal(expected == 0, Built);
        Assert.Equal(expected == 0, File.Exists(Installed));
    }

    [Fact]
    public void ACliThatDoesNotStartIsNotPermissionToDeploy()
    {
        _status = null;
        Assert.Equal(5, Run("MyMod.csproj"));
        Assert.False(Built);
    }

    [Fact]
    public void WithoutAGameFolderRefusesBeforeAnyTool()
    {
        _environment["VALHEIM_PATH"] = null;
        Assert.Equal(3, Run("MyMod.csproj"));
        Assert.Empty(_calls);
        // Refused for the missing setting itself, not for a default folder that turned out to lack BepInEx.
        Assert.StartsWith("ERROR: set VALHEIM_PATH to the game folder", _error.ToString());
    }

    [Fact]
    public void ASteamLibraryInstallNeedsLive()
    {
        string steam = Path.Combine(_root, "Steam", "steamapps", "common", "Valheim");
        Directory.CreateDirectory(Path.Combine(steam, "BepInEx", "plugins"));
        _environment["VALHEIM_PATH"] = steam;

        Assert.Equal(3, Run("MyMod.csproj"));
        Assert.Empty(_calls);
        Assert.Contains("--live", _error.ToString());

        Assert.Equal(0, Run("--live", "MyMod.csproj"));
        Assert.Equal("fresh build", File.ReadAllText(Path.Combine(steam, "BepInEx", "plugins", "MyMod.dll")));
    }

    [Fact]
    public void AFolderLinkedIntoASteamLibraryNeedsLive()
    {
        // A link above the game folder, not at it: ~/games -> <library>/steamapps/common, VALHEIM_PATH=~/games/Valheim.
        string common = Path.Combine(_root, "Library", "steamapps", "common");
        Directory.CreateDirectory(Path.Combine(common, "Valheim", "BepInEx", "plugins"));
        string games = Path.Combine(_root, "games");
        try { Directory.CreateSymbolicLink(games, common); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return; // Windows without the symbolic-link privilege; the text check above still runs there.
        }
        _environment["VALHEIM_PATH"] = Path.Combine(games, "Valheim");

        Assert.Equal(3, Run("MyMod.csproj"));
        Assert.Empty(_calls);
        Assert.True(DevLoop.LooksLikeSteamInstall(Path.Combine(games, "Valheim", "BepInEx")));
        Assert.False(DevLoop.LooksLikeSteamInstall(_root));
    }

    [Theory]
    [InlineData("/home/me/.steam/steam/steamapps/common/Valheim", true)]
    [InlineData(@"C:\Program Files (x86)\Steam\SteamApps\Common\Valheim", true)]
    [InlineData("/Users/me/Library/Application Support/Steam/steamapps/common/Valheim", true)]
    [InlineData("/srv/valheim-copies/Valheim", false)]
    [InlineData("/srv/steamapps/Valheim", false)]
    public void RecognisesASteamLibraryInstall(string path, bool expected) => Assert.Equal(expected, DevLoop.LooksLikeSteamInstall(path));

    [Fact]
    public void RelativePathsAreTheCallersAndToolsRunThere()
    {
        // The SDK starts a file-based script in its own folder; the shell's PWD is the folder the paths were typed in.
        Directory.CreateDirectory(Path.Combine(_root, "mod", "game", "BepInEx", "plugins"));
        foreach (string file in new[] { "pins.txt", "MyMod.csproj", "plan.yaml" })
            File.Copy(Path.Combine(_root, file), Path.Combine(_root, "mod", file));
        _environment["PWD"] = Path.Combine(_root, "mod");
        _environment["VALHEIM_PATH"] = "game";
        _environment["VALHEIM_EXPECTATIONS"] = "pins.txt";
        _environment["VALHEIM_CLI"] = Path.Combine("tools", "fake-cli");
        string cli = Path.Combine(_root, "mod", "tools", "fake-cli");

        Assert.Equal(0, DevLoop.Run(new[] { "MyMod.csproj", "plan.yaml" }, name => _environment.GetValueOrDefault(name),
            (tool, arguments, capture, directory) => Tools(tool == cli ? "fake-cli" : tool, arguments, capture, directory), _output, _error));
        Assert.All(_calls, call => Assert.Equal(Path.Combine(_root, "mod"), call.Directory));
        Assert.Equal(Path.Combine(_root, "mod", "MyMod.csproj"), _calls.First(call => call.Tool == "dotnet").Arguments[1]);
        Assert.Equal(Path.Combine(_root, "mod", "plan.yaml"), PlanArguments[Array.IndexOf(PlanArguments, "--test") + 1]);
        Assert.True(File.Exists(Path.Combine(_root, "mod", "game", "BepInEx", "plugins", "MyMod.dll")));
    }

    [Fact]
    public void WithoutPwdOnlyFullPathsAreAccepted()
    {
        _environment["PWD"] = null;
        Assert.Equal(4, Run("MyMod.csproj"));
        Assert.Empty(_calls);
        Assert.Contains("relative path MyMod.csproj", _error.ToString());

        // The tools then run in the project's folder, never in tools/dev-loop.
        Directory.CreateDirectory(Path.Combine(_root, "mod"));
        File.Copy(Path.Combine(_root, "MyMod.csproj"), Path.Combine(_root, "mod", "MyMod.csproj"));
        Assert.Equal(0, Run(Path.Combine(_root, "mod", "MyMod.csproj")));
        Assert.All(_calls, call => Assert.Equal(Path.Combine(_root, "mod"), call.Directory));
    }

    [Theory]
    [InlineData("Other.csproj", "plan.yaml")]
    [InlineData("MyMod.csproj", "other.yaml")]
    public void AProjectOrPlanThatIsNotThereRefusesBeforeAnyTool(string project, string plan)
    {
        Assert.Equal(4, Run(project, plan));
        Assert.Empty(_calls);
        Assert.Contains("no file at " + Path.Combine(_root, project == "MyMod.csproj" ? plan : project), _error.ToString());
    }

    [Fact]
    public void WithoutPluginsFolderRefuses()
    {
        _environment["VALHEIM_PATH"] = _root;
        Assert.Equal(3, Run("MyMod.csproj"));
        Assert.Empty(_calls);
    }

    [Fact]
    public void PlanWithoutPinsStopsBeforeTools()
    {
        _environment["VALHEIM_EXPECTATIONS"] = null;
        Assert.Equal(4, Run("MyMod.csproj", "plan.yaml"));
        Assert.Empty(_calls);
        Assert.Contains("VALHEIM_EXPECTATIONS", _error.ToString());
    }

    [Fact]
    public void PlanPinsTheDeployedBuildAndKeepsOtherLines()
    {
        string original = File.ReadAllText(Pins);
        Assert.Equal(0, Run("MyMod.csproj", "plan.yaml"));
        Assert.Equal(new[] { "com.example.other=0123456789abcdef   # keep", "MyMod=" + FreshMd5, "world=Dev" },
            _usedPins!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split("   # derived")[0]));
        Assert.Equal(original, File.ReadAllText(Pins));
        Assert.Equal("fresh build", File.ReadAllText(Installed));

        string[] arguments = PlanArguments;
        string derived = arguments[Array.IndexOf(arguments, "--expect-strict") + 1];
        Assert.NotEqual(Pins, derived);
        Assert.False(File.Exists(derived), "the derived pins file outlived the round");
        Assert.Equal(Path.Combine(_root, "plan.yaml"), arguments[Array.IndexOf(arguments, "--test") + 1]);
        Assert.Contains("--launch", arguments);
        Assert.DoesNotContain("--expect", arguments);
    }

    [Fact]
    public void PlanReplacesThePinNamedByPluginKey()
    {
        File.WriteAllText(Pins, "com.example.mymod=11111111\n");
        _environment["VALHEIM_PLUGIN_KEY"] = "com.example.mymod";
        Assert.Equal(0, Run("MyMod.csproj", "plan.yaml"));
        Assert.StartsWith("com.example.mymod=" + FreshMd5, _usedPins);
    }

    [Theory]
    [InlineData("com.example.other=any\n")]
    [InlineData("MyMod=1\nmymod = 2\n")]
    public void PlanWithoutExactlyOnePinForTheBuildRefusesBeforeDeploying(string pins)
    {
        File.WriteAllText(Pins, pins);
        Assert.Equal(4, Run("MyMod.csproj", "plan.yaml"));
        Assert.Null(_usedPins);
        Assert.False(File.Exists(Installed));
    }

    [Fact]
    public void ExitCodesOfTheBuildAndThePlanAreTheRounds()
    {
        _buildExit = 9;
        Assert.Equal(9, Run("MyMod.csproj", "plan.yaml"));
        Assert.False(File.Exists(Installed));

        _buildExit = 0;
        _planExit = 6;
        Assert.Equal(6, Run("MyMod.csproj", "plan.yaml"));
    }

    [Fact]
    public void WaitAndStopSettingsReachTheRunner()
    {
        _environment["STOP_AFTER"] = "1";
        _environment["PROGRESS"] = "30s";
        _environment["STALL"] = "0";
        Assert.Equal(0, Run("MyMod.csproj", "plan.yaml"));
        string[] arguments = PlanArguments;
        Assert.Contains("--stop-after", arguments);
        Assert.Equal("30s", arguments[Array.IndexOf(arguments, "--progress") + 1]);
        Assert.Equal("0", arguments[Array.IndexOf(arguments, "--stall") + 1]);
    }

    [Theory]
    [InlineData("--fail-on")]
    [InlineData("--fail-on", "info")]
    [InlineData("--top", "3")]
    public void UnknownOptionsAreUsageErrors(params string[] options)
    {
        Assert.Equal(4, Run(options.Append("MyMod.csproj").ToArray()));
        Assert.Empty(_calls);
    }

    private const string LogText =
        "[Info   :   BepInEx] Loading [MyMod 1.0.0]\n" +
        "[Warning:     MyMod] zone 3,-4 has no road\n" +
        "[Warning:     MyMod] zone 5,2 has no road\n" +
        "[Error  :     MyMod] NullReferenceException at 12.5\n" +
        "  at MyMod.Patch () [0x00000]\n" +
        "[Warning: OtherMod] slow frame\n";

    [Fact]
    public void SummarisesTheLogByLevelSourceAndMaskedMessage()
    {
        File.WriteAllText(Log, LogText);
        Assert.Equal(0, Run("MyMod.csproj"));
        string output = _output.ToString().Replace("\r\n", "\n");
        Assert.Contains("Warning       2  MyMod\n", output);
        Assert.Contains("Error         1  MyMod\n", output);
        Assert.Contains("Warning       1  OtherMod\n", output);
        Assert.Contains("total: fatal=0 error=1 warning=3\n", output);
        Assert.Contains("     2x  [Warning: MyMod] zone #,# has no road\n", output);
        Assert.Contains("     1x  [Error: MyMod] NullReferenceException at #\n", output);
    }

    [Theory]
    [InlineData("error", LogText, 1)]
    [InlineData("error", "[Warning:     MyMod] only a warning\n", 0)]
    [InlineData("warning", "[Warning:     MyMod] only a warning\n", 1)]
    [InlineData(null, LogText, 0)]
    public void FailOnFailsAPassingRoundThatLogged(string? level, string log, int expected)
    {
        File.WriteAllText(Log, log);
        Assert.Equal(expected, Run(level == null ? new[] { "MyMod.csproj" } : new[] { "--fail-on", level, "MyMod.csproj" }));
    }

    [Fact]
    public void FailOnWithoutALogFails()
    {
        Assert.Equal(0, Run("MyMod.csproj"));
        Assert.Equal(3, Run("--fail-on", "error", "MyMod.csproj"));
    }

    [Fact]
    public void ToolThatDoesNotStartEndsTheRoundWithExitOne()
    {
        Assert.Equal(1, DevLoop.Run(new[] { "MyMod.csproj" }, name => _environment.GetValueOrDefault(name),
            (tool, arguments, capture, directory) => tool == "dotnet" ? throw new Win32Exception("no dotnet") : Tools(tool, arguments, capture, directory),
            _output, _error));
        Assert.Contains("no dotnet", _error.ToString());
    }

    [Fact]
    public void RunsAsAFileBasedScript()
    {
        // The shipped entry point, compiled by the SDK as a user runs it (the tests above compile it without that line).
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        string script = Path.Combine(FixtureProjects.RepositoryRoot(), "tools", "dev-loop", "dev-loop.cs");
        ToolRun run = DevLoop.RunProcess(dotnet, new[] { "run", "--file", script }, capture: true, directory: _root);
        Assert.True(run.ExitCode == 4, $"exit {run.ExitCode}\n{run.Output}\n{run.Errors}");
        Assert.Contains(DevLoop.Usage, run.Errors);
    }

    [Fact]
    public void RunProcessCapturesARealToolsOutputAndExitCode()
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        ToolRun run = DevLoop.RunProcess(dotnet, new[] { "--version" }, capture: true, directory: _root);
        Assert.Equal(0, run.ExitCode);
        Assert.Matches(@"^\d+\.\d+", run.Output.Trim());
        Assert.NotEqual(0, DevLoop.RunProcess(dotnet, new[] { "no-such-command-dev-loop" }, capture: true, directory: null).ExitCode);
        Assert.Throws<Win32Exception>(() => DevLoop.RunProcess(Path.Combine(_root, "no-such-tool"), Array.Empty<string>(), capture: true, directory: null));
    }
}
