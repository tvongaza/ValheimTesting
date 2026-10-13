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

public sealed class CopiedProgramTests : IDisposable
{
    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly string _root = Directory.CreateTempSubdirectory("copied-program-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    // Why the helper exists (#720, #723), without a race: Linux refuses to start a file that a process holds open for writing.
    // A copy written in this process holds its write descriptor while a parallel fork can inherit it, hence "Text file busy".
    [Fact] public void LinuxRefusesToStartAFileHeldOpenForWriting()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = Path.Combine(_root, "held");
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(path, Executable);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var refused = Assert.Throws<System.ComponentModel.Win32Exception>(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = false }));
        Assert.Equal(26, refused.NativeErrorCode); // ETXTBSY
    }

    // The contract: a copy starts on the first try, and this process holds no descriptor to it once the helper returns.
    [Fact] public void ACopyStartsAtOnceAndIsNotHeldOpen()
    {
        if (!OperatingSystem.IsLinux()) return; // The copy of `true` runs unsigned only here (macOS kills an unsigned copy of a system program).
        string copy = Path.Combine(_root, "true-copy");
        CopiedProgram.Copy(File.Exists("/usr/bin/true") ? "/usr/bin/true" : "/bin/true", copy);
        Assert.DoesNotContain(copy, Directory.GetFiles("/proc/self/fd").Select(fd => new FileInfo(fd).LinkTarget));
        StartAndExpectSuccess(copy);
    }

    [Fact] public void AScriptStubStartsAtOnceHasItsContentAndIsNotHeldOpen()
    {
        if (OperatingSystem.IsWindows()) return; // A stub runs unsigned on macOS too, so its scratch-file path is exercised there as well.
        const string content = "#!/bin/sh\nexit 0\n";
        string stub = Path.Combine(_root, "stub");
        CopiedProgram.WriteScript(stub, content);
        Assert.Equal(content, File.ReadAllText(stub));
        Assert.Equal(Executable, File.GetUnixFileMode(stub));
        if (OperatingSystem.IsLinux())
            Assert.DoesNotContain(stub, Directory.GetFiles("/proc/self/fd").Select(fd => new FileInfo(fd).LinkTarget));
        StartAndExpectSuccess(stub);
    }

    private static void StartAndExpectSuccess(string program)
    {
        using var running = Process.Start(new ProcessStartInfo(program) { UseShellExecute = false })!;
        running.WaitForExit();
        Assert.Equal(0, running.ExitCode);
    }
}
