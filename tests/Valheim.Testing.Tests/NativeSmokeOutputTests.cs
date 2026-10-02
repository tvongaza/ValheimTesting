using Xunit;

public sealed class NativeSmokeOutputTests
{
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
