using System.Diagnostics;
using Xunit;

/// <summary>A test that is reported as skipped on Windows, for behaviour that exists only on macOS and Linux.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Covers macOS/Linux behaviour; runs on macOS/Linux";
    }
}

/// <summary>The exit code and combined console output of one <c>dotnet</c> command.</summary>
public sealed record BuildRun(int ExitCode, string Output)
{
    public void AssertSucceeded() => Assert.True(ExitCode == 0, $"expected the build to succeed, got exit {ExitCode}\n{Output}");

    public void AssertFailedWith(string message)
    {
        Assert.True(ExitCode != 0, $"expected the build to fail, it succeeded\n{Output}");
        Assert.True(Output.Contains(message, StringComparison.Ordinal), $"expected \"{message}\" in\n{Output}");
    }
}

/// <summary>The repository's source tree, and <c>dotnet</c> commands on fixture projects built from it.</summary>
internal static class FixtureProjects
{
    /// <summary>
    /// The repository root, found from the test binaries so tests read the source tree on any OS: the nearest
    /// directory above them that holds cli-dependency.json, as scripts/*.cs find it.
    /// </summary>
    public static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
        throw new InvalidOperationException("No directory above " + AppContext.BaseDirectory + " contains cli-dependency.json");
    }

    /// <summary>The full path of the dotnet host: the one running the tests, else the first on PATH.</summary>
    public static string DotnetHost { get; } = FindDotnet();

    private static string FindDotnet()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host) return host;
        string name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return name;
    }

    /// <summary>The lines of a text, without the empty string after a final newline.</summary>
    public static string[] Lines(string text)
    {
        List<string> lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.ToArray();
    }

    /// <summary>Whether <paramref name="name"/> is an executable file in a PATH directory.</summary>
    public static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(Path.Combine(dir, name)));

    /// <summary>
    /// Runs <c>dotnet <paramref name="arguments"/></c> in <paramref name="directory"/> with this process's environment
    /// plus <paramref name="environment"/> (a null value removes the variable). No build server or reused node
    /// outlives it.
    /// </summary>
    public static async Task<BuildRun> Dotnet(string directory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment = null,
        int timeoutMinutes = 3)
    {
        var start = new ProcessStartInfo(DotnetHost)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        // The test host runs inside an SDK command; its MSBuild variables must not steer the fixture's build.
        foreach (string name in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly" })
            start.Environment.Remove(name);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        foreach ((string name, string? value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(" ", arguments)} in {directory} did not finish within {timeoutMinutes} minutes");
        }
        return new BuildRun(process.ExitCode, await stdout + await stderr);
    }
}
