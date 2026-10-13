using Xunit;
using Valheim.Testing.Game;

public sealed class NativeSmokeOutputTests
{
    [Fact]
    public void StartLogSummaryNamesTotalsAndMostFrequentPatterns()
    {
        var logs = new[]
        {
            new LogFileScan("client BepInEx log", "bepinex.log", true, null,
            [
                new(LogScanner.UnknownWarning, LogSeverity.Warning, null, 5, 1, "warning", 2, "expected warning"),
                new(LogScanner.UnknownError, LogSeverity.Failure, null, 1, 8, "error"),
            ]),
            new LogFileScan("client Player.log", "Player.log", true, null,
            [
                new("missing-script", LogSeverity.Warning, null, 3, 4, "missing script"),
                new("shader-unsupported", LogSeverity.Warning, null, 1, 9, "shader"),
            ]),
            new LogFileScan("required server log", "server.log", false, "required log is missing", []),
        };

        Assert.Equal("log scan: 2/3 logs present; failure=1 warning=9 expected=2 required-missing=1; " +
            "most frequent: client BepInEx log/unknown-warning x5, client Player.log/missing-script x3, " +
            "client BepInEx log/unknown-error x1", NativeSmokeLogSummary.Format(logs));
    }

    [Fact]
    public void LogSummarySaysWhenTheScanDidNotRunAndLabelsComparisonArms()
    {
        Assert.Equal("log scan: not run", NativeSmokeLogSummary.Format([]));
        Assert.Equal("before: log scan: not run", NativeSmokeLogSummary.Format([], "before"));
        Assert.StartsWith("after: log scan: 2/3 logs present", NativeSmokeLogSummary.Format(
        [
            new LogFileScan("client BepInEx log", "bepinex.log", true, null, []),
            new LogFileScan("client Player.log", "Player.log", true, null, []),
            new LogFileScan("required server log", "server.log", false, "required log is missing", []),
        ], "after"));
    }

    [Fact]
    public void Arm64OneShotRefusesAStockMacLoaderBeforeAnyCopy()
    {
        using var stock = ClientLaunchTests.Install.Mac();
        using var native = ClientLaunchTests.Install.Mac(packDoorstop: false, arm64Doorstop: true, core: new Version(25, 3, 4));
        var inventory = new EnvironmentInventory { Hosts = new() { ["local"] = new HostProfile { Kind = "local", Platform = "macos" } } };
        var recipe = new EnvironmentRecipe { Name = "local-client", Host = "local", Roles = ["client"], Install = stock.Root };
        string refusal = Assert.Throws<InvalidOperationException>(() =>
            SmokeInputResolver.RequireClientArchitecture(inventory, recipe, "arm64", null)).Message;
        Assert.Contains("arm64 slice", refusal);
        recipe.Install = native.Root;
        SmokeInputResolver.RequireClientArchitecture(inventory, recipe, "arm64", null);
        string x64Refusal = Assert.Throws<InvalidOperationException>(() =>
            SmokeInputResolver.RequireClientArchitecture(inventory, recipe, "x64", null)).Message;
        Assert.Contains("x86_64 slice", x64Refusal);
    }
    [Fact]
    public void StartArchitectureAcceptsOnlyTheTwoSupportedSlices()
    {
        Assert.True(StartArguments.TryRead(["--mod", "mod.dll", "--output", "new-run", "--client-architecture", "arm64"],
            out var options, out _, out _, out _, out _));
        Assert.Equal("arm64", options!["--client-architecture"]);
        Assert.False(StartArguments.TryRead(["--mod", "mod.dll", "--output", "new-run", "--client-architecture", "native"],
            out _, out _, out _, out _, out string error));
        Assert.Contains("x64 or arm64", error);
    }
    [Theory]
    [InlineData("9")]
    [InlineData("901")]
    [InlineData("-1")]
    [InlineData("600.5")]
    [InlineData("invalid")]
    public void StartRefusesInvalidWorldEntryDeadlineBeforePreparingFiles(string seconds)
    {
        Assert.False(StartArguments.TryRead(
            ["--mod", "mod.dll", "--output", "new-run", "--join-seconds", seconds],
            out _, out _, out _, out _, out string error));
        Assert.Contains("--join-seconds", error);
    }

    [Fact]
    public void StartAcceptsLongWorldEntryDeadlineForFirstGeneration()
    {
        Assert.True(StartArguments.TryRead(
            ["--mod", "mod.dll", "--output", "new-run", "--join-seconds", "600"],
            out var options, out _, out _, out _, out string error), error);
        Assert.Equal("600", options!["--join-seconds"]);
    }

    [Fact]
    public void StartHoldIsAFlagAndCannotBeRepeated()
    {
        Assert.True(StartArguments.TryRead(["--mod", "mod.dll", "--output", "new-run", "--hold"],
            out var options, out _, out _, out _, out string error), error);
        Assert.Equal("true", options!["--hold"]);
        Assert.False(StartArguments.TryRead(["--mod", "mod.dll", "--output", "new-run", "--hold", "--hold"],
            out _, out _, out _, out _, out error));
        Assert.Contains("Repeated option", error);
    }

    [Fact]
    public void DisposableOutputMustStayOutsideInstallsAndAccountData()
    {
        string root = Path.Combine(Path.GetTempPath(), "native-smoke-path-test");
        string game = Path.Combine(root, "game");
        string steam = Path.Combine(root, "steam", "userdata");

        Assert.Throws<ArgumentException>(() => SmokeOutput.RefuseInside(game, game, steam));
        Assert.Throws<ArgumentException>(() => SmokeOutput.RefuseInside(Path.Combine(game, "run"), game, steam));
        Assert.Throws<ArgumentException>(() => SmokeOutput.RefuseInside(Path.Combine(steam, "run"), game, steam));
        SmokeOutput.RefuseInside(Path.Combine(root, "runs", "new-run"), game, steam);
        SmokeOutput.RefuseInside(Path.Combine(root, "game-sibling"), game, steam);
    }

    [Fact]
    public void ResolvedActorsAndLoaderAreProtectedByTheSameOutputCheck()
    {
        string root = Path.Combine(Path.GetTempPath(), "native-smoke-resolved-path-test");
        var chosen = new EnvironmentRecipe { Install = Path.Combine(root, "chosen-game") };
        var inventory = new EnvironmentInventory();
        string cli = Path.Combine(root, "cli"), loader = Path.Combine(root, "loader");
        foreach (string protectedRoot in new[] { chosen.Install, cli, loader })
            Assert.Throws<ArgumentException>(() => SmokeOutput.RefuseResolved(
                Path.Combine(protectedRoot, "run"), cli, inventory, [chosen], loader));
        SmokeOutput.RefuseResolved(Path.Combine(root, "safe-run"), cli, inventory, [chosen], loader);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("server-load")]
    [InlineData("server-load-ab")]
    public void EveryOneShotCommandWritesTheSameRefusalMarker(string command)
    {
        string output = Directory.CreateTempSubdirectory("smoke-refusal-").FullName;
        try
        {
            SmokeOutput.MarkRefused(output, command, ["a selected input is missing"]);
            string text = File.ReadAllText(Path.Combine(output, "REFUSED.txt"));
            Assert.Contains("refused " + command, text);
            Assert.Contains("a selected input is missing", text);
            Assert.Contains("env status", text);
        }
        finally { Directory.Delete(output, recursive: true); }
    }
}
