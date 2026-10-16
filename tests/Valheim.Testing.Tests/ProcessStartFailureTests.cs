using System.ComponentModel;
using System.Diagnostics;
using Valheim.Testing.GameSessions;
using Xunit;

public class ProcessStartFailureTests
{
    [Theory]
    [InlineData(0, "Other", false)] // No native error was supplied; do not read a stale last-error code.
    [InlineData(2, "ExecutableNotFound", true)]
    [InlineData(5, "ExecutableInaccessible", false)]
    [InlineData(32, "ExecutableInaccessible", false)]
    [InlineData(1920, "ExecutableInaccessible", false)]
    [InlineData(267, "InvalidWorkingDirectory", false)]
    [InlineData(3, "Other", false)] // Path not found does not identify which path failed.
    [InlineData(1450, "Other", false)] // Insufficient system resources.
    public async Task NativeFailuresKeepTheirFactsAndOnlyMissingExecutablesGetInstallAdvice(int code, string kind, bool advice)
    {
        // The words are deliberately the same, and look like a missing-file error. Classification uses the native code.
        var error = new Win32Exception(code, "raw localised message: file not found\r\nsecond line");
        var start = new ProcessStartInfo("pwsh") { WorkingDirectory = "test-working-directory" };
        start.ArgumentList.Add("secret-argument");
        ProcessExit exit = ProcessRunner.NotStarted(start, error, TimeSpan.FromMilliseconds(7), windows: true);
        var failure = Assert.IsType<ProcessStartFailure>(exit.StartFailure);
        Assert.Equal((kind, code, error.HResult, error.Message, "pwsh", start.WorkingDirectory),
            (failure.Kind.ToString(), failure.NativeErrorCode, failure.HResult, failure.Message, failure.Executable, failure.WorkingDirectory));
        Assert.Equal(typeof(Win32Exception).FullName, failure.ExceptionType);
        Assert.Contains(error.Message, exit.Stderr);
        Assert.DoesNotContain("secret-argument", exit.Stderr);

        var fake = new FakeLauncher().Reply(_ => exit);
        var host = new LocalGameHost("pc", HostShell.Pwsh, fake);
        var result = await host.RunAsync("exit 0", null, TimeSpan.FromSeconds(5));
        Assert.Equal(HostOutcome.TransportFailed, result.Outcome);
        Assert.False(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.Contains("raw localised message: file not found\nsecond line", result.Stderr);
        Assert.Contains("native error=" + code, result.Describe());
        Assert.Contains($"HRESULT=0x{error.HResult:X8}", result.Describe());
        Assert.Contains("working directory='test-working-directory'", result.Stderr);
        Assert.Equal(advice, result.Stderr.Contains("winget install", StringComparison.Ordinal));
        Assert.Single(fake.Calls); // No retry or caller-specific recovery.
    }

    [Fact] public void UnknownResultsAndAMissingDifferentExecutableDoNotJustifyInstallingPowerShell()
    {
        var host = new LocalGameHost("pc", HostShell.Pwsh, new FakeLauncher());
        var unknown = new ProcessExit(ProcessEnd.NotStarted, -1, "", "file not found", TimeSpan.Zero);
        Assert.DoesNotContain("winget", host.Interpret(unknown).Stderr);
        var ssh = ProcessRunner.NotStarted(new ProcessStartInfo("ssh"), new Win32Exception(2), TimeSpan.Zero, windows: true);
        Assert.DoesNotContain("winget", host.Interpret(ssh).Stderr);
        var unix = ProcessRunner.NotStarted(new ProcessStartInfo("pwsh"), new Win32Exception(2), TimeSpan.Zero, windows: false);
        Assert.Equal(ProcessStartFailureKind.Other, unix.StartFailure!.Kind);
        Assert.DoesNotContain("winget", host.Interpret(unix).Stderr);
    }

    [Fact] public async Task ARealMissingExecutableKeepsTheNativeErrorAndEffectiveDirectory()
    {
        using var dir = new TempDirectory();
        var start = new ProcessStartInfo(Path.Combine(dir.Path, "absent-helper.exe"));
        var exit = await Run(start);
        Assert.Equal(ProcessEnd.NotStarted, exit.End);
        Assert.NotNull(exit.StartFailure);
        Assert.Equal(start.FileName, exit.StartFailure.Executable);
        Assert.Equal(Environment.CurrentDirectory, exit.StartFailure.WorkingDirectory);
        Assert.Equal(2, exit.StartFailure.NativeErrorCode);
        Assert.Equal(OperatingSystem.IsWindows() ? ProcessStartFailureKind.ExecutableNotFound : ProcessStartFailureKind.Other, exit.StartFailure.Kind);
    }

    [Fact] public async Task ARealInvalidWorkingDirectoryDoesNotBecomeAMissingShell()
    {
        using var dir = new TempDirectory();
        var start = Helper(Path.Combine(dir.Path, "absent-directory"));
        var exit = await Run(start);
        Assert.Equal(ProcessEnd.NotStarted, exit.End);
        Assert.NotNull(exit.StartFailure);
        Assert.Equal(start.WorkingDirectory, exit.StartFailure.WorkingDirectory);
        Assert.NotEqual(ProcessStartFailureKind.ExecutableNotFound, exit.StartFailure.Kind);
        Assert.DoesNotContain("winget", new LocalGameHost("pc", HostShell.Pwsh, new FakeLauncher()).Interpret(exit).Stderr);
    }

    // An exclusive handle is a deterministic Windows barrier: the disposable helper exists but cannot be executed until
    // the handle is released. No installed executable is changed, no ACL survives, and successful runs bracket the failure.
    [Fact] public async Task AnInaccessibleDisposableWindowsHelperIsNotReportedAsMissing()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        string helper = Path.Combine(dir.Path, "helper.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), helper);
        var start = new ProcessStartInfo(helper) { WorkingDirectory = dir.Path, ArgumentList = { "/d", "/c", "exit 0" } };
        Assert.Equal(ProcessEnd.Exited, (await Run(start)).End);
        using (var barrier = new FileStream(helper, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var exit = await Run(start);
            Assert.Equal(ProcessEnd.NotStarted, exit.End);
            Assert.Equal(ProcessStartFailureKind.ExecutableInaccessible, exit.StartFailure!.Kind);
            Assert.Contains(exit.StartFailure.NativeErrorCode, new[] { 5, 32 });
            Assert.DoesNotContain("winget", new LocalGameHost("pc", HostShell.Pwsh, new FakeLauncher()).Interpret(exit).Stderr);
        }
        var after = await Run(start);
        Assert.Equal(ProcessEnd.Exited, after.End);
        Assert.Equal(0, after.ExitCode);
    }

    private static ProcessStartInfo Helper(string directory) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { WorkingDirectory = directory, ArgumentList = { "/d", "/c", "exit 0" } }
        : new ProcessStartInfo("/bin/sh") { WorkingDirectory = directory, ArgumentList = { "-c", "exit 0" } };
    private static Task<ProcessExit> Run(ProcessStartInfo start) => ProcessRunner.RunAsync(start, [], null, null, null, TimeSpan.FromSeconds(5), CancellationToken.None);
}
