using Xunit;

public sealed class NativeSmokeOutputTests
{
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
}
