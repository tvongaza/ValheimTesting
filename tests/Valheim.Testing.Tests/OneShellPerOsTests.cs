using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// #703: one shell per OS. A Windows host runs its scripts in PowerShell 7 (pwsh), a macOS or Linux host in bash; Windows
// PowerShell 5.1 is no shell at all.
public sealed class OneShellPerOsTests
{
    [Theory]
    [InlineData("windows", "pwsh", null)]
    [InlineData("linux", "bash", null)]
    [InlineData("macos", "bash", null)]
    [InlineData("linux", "pwsh", "PowerShell runs only on Windows hosts; use bash on this host.")]
    [InlineData("macos", "pwsh", "PowerShell runs only on Windows hosts; use bash on this host.")]
    [InlineData("windows", "bash", "bash on Windows (WSL or Git Bash) sees other paths and tools; use pwsh on this host.")]
    [InlineData("windows", "powershell", "shell must be bash or pwsh.")]
    [InlineData("linux", "powershell", "shell must be bash or pwsh.")]
    public void AHostProfileTakesOnlyItsPlatformsShell(string platform, string shell, string? refusal)
    {
        var errors = new List<string>();
        new HostProfile { Kind = "ssh", Platform = platform, Shell = shell, Destination = "tester@box.example",
            Lock = platform == "windows" ? @"C:\vt\lock" : "/tmp/vt/lock" }.Validate("box", errors);
        if (refusal == null) Assert.Empty(errors);
        else Assert.Equal($"Host 'box': {refusal}", Assert.Single(errors));
    }

    [Fact] public void OnlyBashAndPwshAreShells()
    {
        Assert.Same(HostShell.Bash, HostShell.Parse("bash"));
        Assert.Same(HostShell.Pwsh, HostShell.Parse("pwsh"));
        Assert.Contains("use bash or pwsh", Assert.Throws<ArgumentException>(() => HostShell.Parse("powershell")).Message);
        Assert.Same(OperatingSystem.IsWindows() ? HostShell.Pwsh : HostShell.Bash, HostShell.ThisMachine);
        Assert.Equal(OperatingSystem.IsWindows() ? "pwsh" : "bash", HostProfile.ShellFor(HostProfile.CurrentPlatform));
    }

    // A host that is known not to be Windows (this macOS or Linux machine, any container) refuses PowerShell when it is made.
    [Fact] public void AHostThatCannotBeWindowsRefusesPowerShell()
    {
        Assert.Contains("a container runs bash", Assert.Throws<ArgumentException>(() => new ContainerGameHost("ctr", "vt-server", HostShell.Pwsh)).Message);
        _ = new ContainerGameHost("ctr", "vt-server", HostShell.Bash);
        if (OperatingSystem.IsWindows()) _ = new LocalGameHost("here", HostShell.Pwsh);
        else Assert.Contains("this machine runs bash", Assert.Throws<ArgumentException>(() => new LocalGameHost("here", HostShell.Pwsh)).Message);
    }

    // Nothing the toolkit builds, runs or tests names Windows PowerShell 5.1 any more; the docs (Markdown, and the public-api
    // lists a release regenerates) may.
    [Fact] public void NoSourceNamesWindowsPowerShell()
    {
        string root = FixtureProjects.RepositoryRoot();
        string[] kinds = [".cs", ".ps1", ".sh", ".yml", ".yaml", ".json", ".csproj", ".props", ".targets"];
        string self = Path.Combine("tests", "Valheim.Testing.Tests", nameof(OneShellPerOsTests) + ".cs");
        // The source folders only: build output, caches and other checkouts (a dot folder, other than .github and .config) are not walked.
        var files = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (name is "docs" or "bin" or "obj" or "artifacts" or "TestResults" || (name.StartsWith('.') && name is not (".github" or ".config"))) continue;
                pending.Push(child);
            }
            files.AddRange(Directory.EnumerateFiles(directory).Select(file => Path.GetRelativePath(root, file))
                .Where(file => kinds.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && file != self));
        }
        files = files.Select(file => file.Replace('\\', '/')).ToList();
        Assert.Contains("src/Valheim.Testing.GameSessions/GameHost.cs", files);
        var found = files.Where(file => File.Exists(Path.Combine(root, file)))
            .SelectMany(file => File.ReadLines(Path.Combine(root, file)).Select((line, index) => (file, line, index)))
            .Where(item => item.line.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase) ||
                           item.line.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{item.file}:{item.index + 1}: {item.line.Trim()}").ToList();
        Assert.True(found.Count == 0, string.Join("\n", found));
    }
}
