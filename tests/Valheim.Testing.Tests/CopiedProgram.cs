using System.Diagnostics;
using Xunit;

// #720: a test that runs a copy of a system program makes the copy here, once.
// Linux refuses to execute a file that any process holds open for writing (ETXTBSY, "Text file busy"). A copy written by this
// process holds a write descriptor for the length of the copy, and any other test in this process that starts a program at that
// moment forks a child that inherits the descriptor until it execs: the copy then cannot be started. A separate `cp` process
// never gives this process a descriptor to inherit, and has exited (closed it) before the copy is returned.
internal static class CopiedProgram
{
    /// <summary>Copies a program so that it can be started at once, even while other threads of this process start programs.</summary>
    public static void Copy(string source, string target)
    {
        if (OperatingSystem.IsWindows()) { File.Copy(source, target); return; } // Windows has no such refusal.
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("The copy's path is taken: " + target); // as File.Copy refuses; cp would overwrite or copy into it
        var start = new ProcessStartInfo("cp") { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add(source); start.ArgumentList.Add(target);
        using var cp = Process.Start(start) ?? throw new IOException("Could not start cp.");
        string error = cp.StandardError.ReadToEnd();
        cp.WaitForExit();
        if (cp.ExitCode != 0) throw new IOException($"cp {source} {target} exited {cp.ExitCode}: {error.Trim()}");
    }
}

// The forking threads would widen the same window for any other test in this process that writes a program and starts it.
[CollectionDefinition(nameof(CopiedProgramStress), DisableParallelization = true)]
public sealed class CopiedProgramStress;

[Collection(nameof(CopiedProgramStress))]
public sealed class CopiedProgramTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("copied-program-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    // The race of #720, made frequent: other threads start programs without pause while a copy is made and started, again and again.
    // Copied in this process, a start fails with "Text file busy" within a few hundred rounds; copied here it never does.
    [Fact] public void ACopyStartsWhileOtherThreadsStartPrograms()
    {
        if (!OperatingSystem.IsLinux()) return; // The copy of `true` runs unsigned only here (macOS kills an unsigned copy of a system program).
        string source = File.Exists("/usr/bin/true") ? "/usr/bin/true" : "/bin/true";
        using var stop = new CancellationTokenSource();
        Exception? forkFailure = null; // a thread's exception would end the whole test host; it is rethrown here instead
        var forkers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var other = Process.Start(new ProcessStartInfo(source) { UseShellExecute = false })!;
                    other.WaitForExit();
                }
            }
            catch (Exception failure) { forkFailure = failure; }
        })).ToArray();
        foreach (var forker in forkers) forker.Start();
        try
        {
            for (int round = 0; round < 300; round++)
            {
                string copy = Path.Combine(_root, "true-" + round);
                CopiedProgram.Copy(source, copy);
                using var running = Process.Start(new ProcessStartInfo(copy) { UseShellExecute = false })!;
                running.WaitForExit();
                Assert.Equal(0, running.ExitCode);
            }
        }
        finally { stop.Cancel(); foreach (var forker in forkers) forker.Join(); }
        Assert.Null(forkFailure);
    }
}
