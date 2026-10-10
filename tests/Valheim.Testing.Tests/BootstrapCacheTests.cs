using System.Diagnostics;
using Xunit;

namespace Valheim.Testing.Tests;

public sealed class BootstrapCacheTests
{
    [Fact]
    public void BootstrapSelectsWritableCachesBeforeRunningDotnet()
    {
        string root = FixtureProjects.RepositoryRoot();
        string blocked = Path.GetTempFileName();
        try
        {
            bool windows = OperatingSystem.IsWindows();
            var start = new ProcessStartInfo(windows ? "pwsh" : "sh")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (windows)
            {
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-File");
                start.ArgumentList.Add(Path.Combine(root, "scripts", "bootstrap-cli.ps1"));
            }
            else start.ArgumentList.Add(Path.Combine(root, "scripts", "bootstrap-cli.sh"));
            start.ArgumentList.Add("--check-caches");
            start.Environment["NUGET_PACKAGES"] = blocked;
            start.Environment["NUGET_HTTP_CACHE_PATH"] = blocked;

            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            string errors = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30000), "Cache preflight did not finish");
            Assert.True(process.ExitCode == 0, errors);
            string fallback = Path.Combine(root, "artifacts", "nuget-bootstrap");
            Assert.Contains("NuGet packages: " + Path.Combine(fallback, "packages"), output);
            Assert.Contains("NuGet HTTP cache: " + Path.Combine(fallback, "http"), output);
            Assert.DoesNotContain("NuGet packages: " + blocked, output);
            Assert.DoesNotContain("NuGet HTTP cache: " + blocked, output);
        }
        finally { File.Delete(blocked); }
    }
}
