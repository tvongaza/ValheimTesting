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

    /// <summary>Writes a script stub (its first line names the interpreter) that can be started at once, even while other threads of this process start programs. The owner of every test's stub (#723).</summary>
    public static void WriteScript(string path, string content)
    {
        if (OperatingSystem.IsWindows()) { File.WriteAllText(path, content); return; } // Windows has no such refusal; a test that starts a stub there writes a .cmd itself.
        // The stub is written to a scratch file this process never starts, and copied by `cp` (see Copy) to its path: this process never holds the stub open.
        // The mode is set on the finished copy: chmod opens nothing for writing.
        string scratch = Path.GetTempFileName();
        try
        {
            File.WriteAllText(scratch, content);
            File.Delete(path);
            Copy(scratch, path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        finally { File.Delete(scratch); }
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
        WhileOtherThreadsStartPrograms(source, () =>
        {
            for (int round = 0; round < 300; round++)
            {
                string copy = Path.Combine(_root, "true-" + round);
                CopiedProgram.Copy(source, copy);
                StartAndExpectSuccess(copy);
            }
        });
    }

    // The same race for a script stub (#723): written in this process, the stub is "Text file busy" to a start for as long as a forked child holds its descriptor.
    [Fact] public void AScriptStubStartsWhileOtherThreadsStartPrograms()
    {
        if (OperatingSystem.IsWindows()) return; // A stub runs unsigned on macOS too, so its scratch-file path is exercised there as well.
        string source = File.Exists("/usr/bin/true") ? "/usr/bin/true" : "/bin/true";
        WhileOtherThreadsStartPrograms(source, () =>
        {
            for (int round = 0; round < 300; round++)
            {
                string stub = Path.Combine(_root, "stub-" + round);
                CopiedProgram.WriteScript(stub, "#!/bin/sh\nexit 0\n");
                StartAndExpectSuccess(stub);
            }
        });
    }

    private static void StartAndExpectSuccess(string program)
    {
        using var running = Process.Start(new ProcessStartInfo(program) { UseShellExecute = false })!;
        running.WaitForExit();
        Assert.Equal(0, running.ExitCode);
    }

    private static void WhileOtherThreadsStartPrograms(string program, Action body)
    {
        using var stop = new CancellationTokenSource();
        Exception? forkFailure = null; // a thread's exception would end the whole test host; it is rethrown here instead
        var forkers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var other = Process.Start(new ProcessStartInfo(program) { UseShellExecute = false })!;
                    other.WaitForExit();
                }
            }
            catch (Exception failure) { forkFailure = failure; }
        })).ToArray();
        foreach (var forker in forkers) forker.Start();
        try { body(); }
        finally { stop.Cancel(); foreach (var forker in forkers) forker.Join(); }
        Assert.Null(forkFailure);
    }
}
