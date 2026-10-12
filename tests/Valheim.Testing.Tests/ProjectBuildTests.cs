using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

// #674: one helper starts the .NET SDK for every project build, so owned caches, output capture and cancellation are one decision.
public sealed class ProjectBuildTests
{
    [Fact]
    public void OnlyTheProjectBuildHelperStartsTheDotnetSdk()
    {
        string source = Path.Combine(FixtureProjects.RepositoryRoot(), "src");
        var start = new Regex(@"ProcessStartInfo\s*\(\s*""dotnet""|FileName\s*=\s*""dotnet""|Process\s*\.\s*Start\s*\(\s*""dotnet""");
        string[] files = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)).ToArray();
        Assert.NotEmpty(files);
        var starters = files.Where(file => start.IsMatch(File.ReadAllText(file))).Select(file => Path.GetRelativePath(source, file)).Order().ToArray();
        Assert.Equal(Path.Combine("Valheim.Testing.NativeSmoke", "ProjectBuild.cs"), Assert.Single(starters));
        Assert.Single(start.Matches(File.ReadAllText(Path.Combine(source, "Valheim.Testing.NativeSmoke", "ProjectBuild.cs"))));
    }

    [Fact]
    public async Task CancellingKillsTheStartedProcessAndEverythingItStarted()
    {
        using var directory = new TempDirectory();
        string marker = Path.Combine(directory.Path, "tree");
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(typeof(Valheim.Testing.ProcessSignalProbe.Marker).Assembly.Location);
        start.ArgumentList.Add("process-tree");
        start.ArgumentList.Add(marker);
        using var cancel = new CancellationTokenSource();
        Task<ProcessOutput> run = ProjectBuild.RunAsync(start, cancel.Token);
        var waited = Stopwatch.StartNew();
        while (!File.Exists(marker + ".ready") && waited.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(25);
        Assert.True(File.Exists(marker + ".ready"), "the probe did not start its child");
        int child = 0;
        while (child == 0 && waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            // The child may still be writing its pid.
            try { _ = int.TryParse(File.ReadAllText(marker + ".child"), out child); }
            catch (IOException) { }
            if (child == 0) await Task.Delay(25);
        }
        Assert.NotEqual(0, child);
        Assert.True(IsRunning(child), "the probe's child was not running before the cancellation");

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).WaitAsync(TimeSpan.FromSeconds(60));
        var gone = Stopwatch.StartNew();
        while (IsRunning(child) && gone.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(25);
        Assert.False(IsRunning(child), "the cancelled process's child is still running");
    }

    [Fact]
    public void ASinglePropertyQueryIsPrintedBareAndAnyOtherIsJson()
    {
        var one = new ProjectQuery([], ["TargetPath"]);
        Assert.Equal("/a/b.dll", new ProcessOutput(0, "/a/b.dll\n", "", one).Query.Properties["TargetPath"]);
        var many = new ProjectQuery(["Compile"], ["TargetPath"]);
        var parsed = new ProcessOutput(0, """{"Properties":{"TargetPath":"/a/b.dll"},"Items":{"Compile":[{"FullPath":"/a/A.cs"}]}}""", "", many).Query;
        Assert.Equal("/a/b.dll", parsed.Properties["TargetPath"]);
        Assert.Equal("/a/A.cs", Assert.Single(parsed.Items["Compile"])["FullPath"]);
        Assert.Throws<InvalidDataException>(() => new ProcessOutput(0, "not json", "", many).Query);
    }

    [Fact]
    public async Task AFailureBeforeTheFileLoggerStartsStillReachesTheBuildLog()
    {
        using var directory = new TempDirectory();
        string project = Path.Combine(directory.Path, "Failing.csproj");
        File.WriteAllText(project, "<Project />");
        string log = Path.Combine(directory.Path, "build.log");
        var build = new ProjectBuild(project, [], NuGetCaches.Staged(directory.Path));
        // An unknown switch is refused by MSBuild before any logger exists; the message goes to the process's own streams.
        var run = await build.BuildAsync(CancellationToken.None, restore: false, logFile: log, options: ["-notaswitch"],
            query: new ProjectQuery([], ["TargetPath"]));
        Assert.NotEqual(0, run.ExitCode);
        string said = (run.Output + run.Error).Trim();
        Assert.NotEmpty(said);
        Assert.Contains(said.Split('\n')[0].Trim(), File.ReadAllText(log));
    }

    [Fact]
    public async Task OutputAndExitCodeAreCapturedWithoutJudgingThem()
    {
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add("--version-that-does-not-exist");
        var result = await ProjectBuild.RunAsync(start, CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Combined));
    }

    private static bool IsRunning(int id)
    {
        try
        {
            // A killed grandchild nobody reaps (a container whose init is the test host) stays a zombie: not running.
            if (OperatingSystem.IsLinux() && File.Exists($"/proc/{id}/stat") &&
                File.ReadAllText($"/proc/{id}/stat").Split(')')[^1].TrimStart().StartsWith('Z')) return false;
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
