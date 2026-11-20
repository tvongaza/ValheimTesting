using System.Diagnostics;
using Xunit;

// valheim-test --version (#955) and --help (#957): answered before any command runs, exit 0, nothing read or locked.
public sealed class ToolCommandLineTests
{
    [Fact] public void VersionPrintsThePackageVersionAndExitsZero()
    {
        foreach (string flag in new[] { "--version", "-V" })
        {
            var (exit, output) = Answer(flag);
            Assert.Equal(0, exit);
            string version = typeof(ToolCommandLine).Assembly.GetName().Version!.ToString(3);
            Assert.StartsWith("valheim-test " + version, output);
        }
    }

    [Fact] public void EachCommandsHelpPrintsItsUsageAndExitsZero()
    {
        foreach (var (args, usage) in new[]
        {
            (new[] { "server-load", "--help" }, ServerLoad.Usage),
            (new[] { "env", "status", "-h" }, EnvCommand.Usage),
            (new[] { "finish", "--help" }, ForegroundHold.FinishUsage),
            (new[] { "cli", "--evidence", "x", "--help" }, OwnedCliCommand.Usage),
            (new[] { "start", "--help" }, ToolCommandLine.StartUsage),
            // No command word: the arguments are start's.
            (new[] { "--mod", "A.dll", "--help" }, ToolCommandLine.StartUsage),
        })
        {
            var (exit, output) = Answer(args);
            Assert.Equal(0, exit);
            Assert.StartsWith("Usage: " + usage, output);
        }
    }

    [Fact] public void RootHelpListsEveryCommand()
    {
        foreach (string flag in new[] { "--help", "-h", "help" })
        {
            var (exit, output) = Answer(flag);
            Assert.Equal(0, exit);
            foreach (string command in new[] { "valheim-test start", "valheim-test server-load ", "valheim-test env", "valheim-test finish", "valheim-test cli", "valheim-test --version" })
                Assert.Contains(command, output);
        }
    }

    [Fact] public void OtherArgumentsAreLeftToTheirCommand()
    {
        Assert.False(ToolCommandLine.TryAnswer([], TextWriter.Null, out _));
        Assert.False(ToolCommandLine.TryAnswer(["server-load", "--mod", "A.dll"], TextWriter.Null, out _));
        Assert.False(ToolCommandLine.TryAnswer(["server-load", "--version"], TextWriter.Null, out _));
    }

    [Fact] public void TheInstalledToolAnswersBothFlags()
    {
        // The tool itself, as a shell runs it: the answer comes before any command's own option parsing.
        string tool = Path.Combine(AppContext.BaseDirectory, "Valheim.Testing.NativeSmoke.dll");
        foreach (var (args, expected) in new[] { (new[] { "--version" }, "valheim-test "), (new[] { "server-load", "--help" }, "Usage: valheim-test server-load") })
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(tool);
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(60_000), "the tool did not exit");
            Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {error}");
            Assert.StartsWith(expected, output);
        }
    }

    private static (int Exit, string Output) Answer(params string[] args)
    {
        using var output = new StringWriter();
        Assert.True(ToolCommandLine.TryAnswer(args, output, out int exit));
        return (exit, output.ToString());
    }
}
