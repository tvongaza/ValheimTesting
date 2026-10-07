using Valheim.Testing.Game;
using Xunit;

// #409: a client on this machine that never wrote a BepInEx log line fails with the same preloader evidence a hosted client's
// does (#254): the first error of a preloader log this launch wrote is quoted and the log kept beside the boot output; an older
// one is named as not this launch's. Fake files only; nothing launches.
public sealed class PreloaderLogsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "preloader-logs-" + Guid.NewGuid().ToString("N"));
    private readonly string _install, _output;
    private const string Fatal = "[Fatal  :   BepInEx] Could not find BepInEx.Preloader.Core";

    public PreloaderLogsTests()
    {
        _install = Path.Combine(_root, "install");
        _output = Path.Combine(_root, "output");
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(_output);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    // An earlier start's log, and the launch time after it.
    private DateTime Launch()
    {
        string old = Path.Combine(_install, "preloader_20260101_000000.log");
        File.WriteAllText(old, "[Error  :   BepInEx] an earlier start\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        return DateTime.UtcNow.AddMinutes(-1);
    }

    private void Fresh(string text) => File.WriteAllText(Path.Combine(_install, "preloader_20261006_190000.log"), text);

    private string Explain(DateTime launched, string otherwise)
    {
        var reading = PreloaderLogs.Read(_install, launched);
        PreloaderLogs.Keep(_install, reading!, _output, "client-boot.");
        return PreloaderLogs.Explain(reading, "client-boot.game-2.preloader-*.log", otherwise);
    }

    [Fact] public void AClientThatExitedBeforeBepInExLoggedQuotesAndKeepsThisLaunchsPreloaderLog()
    {
        var launched = Launch();
        Fresh("[Info   :   BepInEx] starting\n" + Fatal + "\n[Error  :   BepInEx] a later error\n");
        string hint = StartupEvents.NoBepInExLog("LogOutput.log", "Player.log", Explain(launched, ""));
        Assert.Contains($"BepInEx's preloader failed: {Fatal} (from preloader_20261006_190000.log, which the client's evidence keeps as client-boot.game-2.preloader-*.log).", hint);
        Assert.Contains("Older preloader logs beside the game (preloader_20260101_000000.log) predate this launch and are not its.", hint);
        Assert.Equal(File.ReadAllText(Path.Combine(_install, "preloader_20261006_190000.log")), File.ReadAllText(Path.Combine(_output, "client-boot.game-2.preloader-1.log")));
        Assert.Single(Directory.GetFiles(_output, "client-boot.game-2.preloader-*.log")); // the older one is named, never kept
    }

    [Fact] public async Task AClientThatNeverLoggedQuotesThePreloaderInsteadOfTheDoorstopAdvice()
    {
        var launched = Launch();
        Fresh(Fatal + "\n");
        string log = Path.Combine(_root, "LogOutput.log");
        File.WriteAllText(log, "");
        using var wait = new LogWait(log) { SafetyInterval = TimeSpan.FromMilliseconds(50) };
        var error = await Assert.ThrowsAsync<WaitFailedException>(() =>
            StartupEvents.WaitForBepInExLog(wait, TimeSpan.FromMilliseconds(200), "Player.log", default, otherwise => Explain(launched, otherwise)));
        Assert.Contains($"BepInEx's preloader failed: {Fatal}", error.Message);
        Assert.DoesNotContain("Doorstop did not start BepInEx", error.Message);
        Assert.True(File.Exists(Path.Combine(_output, "client-boot.game-2.preloader-1.log")));
    }

    // Negative control: no preloader log from this launch leaves the Doorstop advice, and an older log is only named.
    [Fact] public async Task WithoutAFreshPreloaderLogTheDoorstopAdviceStandsAndTheOlderLogIsNamed()
    {
        var launched = Launch();
        string log = Path.Combine(_root, "LogOutput.log");
        File.WriteAllText(log, "");
        using var wait = new LogWait(log) { SafetyInterval = TimeSpan.FromMilliseconds(50) };
        var error = await Assert.ThrowsAsync<WaitFailedException>(() =>
            StartupEvents.WaitForBepInExLog(wait, TimeSpan.FromMilliseconds(200), "Player.log", default, otherwise => Explain(launched, otherwise)));
        Assert.Contains("Doorstop did not start BepInEx", error.Message);
        Assert.Contains("(preloader_20260101_000000.log) predate this launch", error.Message);
        Assert.Empty(Directory.GetFiles(_output));
    }

    [Fact] public void AFreshLogWithNoErrorLineIsNamedAndKept()
    {
        var launched = Launch();
        Fresh("[Info   :   BepInEx] starting\n");
        string text = Explain(launched, "fallback ");
        Assert.Contains("BepInEx's preloader wrote preloader_20261006_190000.log with no error line", text);
        Assert.DoesNotContain("fallback", text);
    }
}
