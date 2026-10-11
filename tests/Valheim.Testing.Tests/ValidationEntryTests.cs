using System.Diagnostics;
using Xunit;

public sealed class ValidationEntryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vt-validation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MacHostProbeNamesDeniedProcessAndPortCapabilities()
    {
        if (OperatingSystem.IsWindows()) return;
        Prepare("exit 1", "echo 'sysctl: Operation not permitted' >&2; exit 1");
        var result = Run("vt_probe_host");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("macOS process identity", result.Output);
        Assert.Contains("macOS TCP listener table", result.Output);
        Assert.Contains("Operation not permitted", result.Output);
    }

    [Fact]
    public void ReadOnlyPreflightRefusesDeniedHostBeforeAnyValidation()
    {
        if (OperatingSystem.IsWindows()) return;
        Prepare("exit 1", "exit 1");
        var result = Run("vt_default_cache() { echo /blocked; }; vt_nearest_writable_parent() { return 0; }; vt_main --preflight-only");
        Assert.Equal(3, result.ExitCode);
        Assert.DoesNotContain("full local validation can start", result.Output);
        Assert.DoesNotContain("INCOMPLETE:", result.Output);
        Assert.Contains("NEXT: run full validation in CI or an authorized Mac shell", result.Output);
    }

    [Fact]
    public void ReadOnlyPreflightNamesUnwritableNuGetCaches()
    {
        if (OperatingSystem.IsWindows()) return;
        Prepare("echo 123", "echo 'Proto Recv-Q Send-Q Local Address Foreign Address State'");
        var result = Run("vt_default_cache() { echo /blocked; }; vt_nearest_writable_parent() { return 1; }; vt_main --preflight-only");
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("NuGet packages cache /blocked and fallback", result.Output);
        Assert.Contains("NuGet http cache /blocked and fallback", result.Output);
        Assert.Contains("NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH", result.Output);
    }

    [Fact]
    public void RestrictedModeSelectsFallbackButReportsIncomplete()
    {
        if (OperatingSystem.IsWindows()) return;
        Prepare("exit 1", "exit 1");
        File.WriteAllText(Path.Combine(_root, "scripts", "run-file-app.sh"),
            "#!/bin/sh\nprintf 'selected mode: %s\\n' \"$2\"\nexit 4\n");
        var result = Run("vt_default_cache() { echo /blocked; }; " +
            "vt_nearest_writable_parent() { return 0; }; " +
            "vt_writable_cache() { case \"$1\" in */artifacts/nuget-bootstrap/*) return 0;; *) return 1;; esac; }; " +
            "vt_main --restricted-sandbox");
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("NuGet packages cache unavailable", result.Output);
        Assert.Contains("NuGet http cache unavailable", result.Output);
        Assert.Contains("selected mode: --restricted-sandbox", result.Output);
        Assert.Contains("INCOMPLETE", result.Output);
    }

    [Fact]
    public void AccessibleMacHostPassesTheReadOnlyPreflight()
    {
        if (OperatingSystem.IsWindows()) return;
        Prepare("echo 123", "echo 'Proto Recv-Q Send-Q Local Address Foreign Address State'");
        var result = Run("vt_default_cache() { echo /writable; }; vt_nearest_writable_parent() { return 0; }; vt_main --preflight-only");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("READY: full local validation can start", result.Output);
    }

    private void Prepare(string ps, string netstat)
    {
        string scripts = Path.Combine(_root, "scripts");
        string bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(bin);
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "scripts", "validate.sh")))
            repo = Directory.GetParent(repo)?.FullName ?? throw new InvalidOperationException("Cannot find validation scripts.");
        File.Copy(Path.Combine(repo, "scripts", "validate.sh"), Path.Combine(scripts, "validate.sh"));
        File.Copy(Path.Combine(repo, "scripts", "nuget-caches.sh"), Path.Combine(scripts, "nuget-caches.sh"));
        WriteExecutable("uname", "echo Darwin");
        WriteExecutable("ps", ps);
        WriteExecutable("netstat", netstat);
    }

    private void WriteExecutable(string name, string body)
    {
        string path = Path.Combine(_root, "bin", name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private (int ExitCode, string Output) Run(string command)
    {
        string driver = Path.Combine(_root, "scripts", "driver.sh");
        File.WriteAllText(driver, "#!/bin/sh\nVT_VALIDATE_SOURCE_ONLY=1\n. \"$(dirname \"$0\")/validate.sh\"\n" + command + "\n");
        var info = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = _root };
        info.ArgumentList.Add(driver);
        info.Environment["PATH"] = Path.Combine(_root, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        info.Environment.Remove("NUGET_PACKAGES");
        info.Environment.Remove("NUGET_HTTP_CACHE_PATH");
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), "Validation probe did not exit within ten seconds.");
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
