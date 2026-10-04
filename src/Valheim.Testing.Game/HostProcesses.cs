using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Valheim.Testing.Game;

internal enum ProcessEnd { Exited, TimedOut, Stopped, NotStarted }
/// <summary>How a local process ended. <see cref="ExitCode"/> is meaningful only for <see cref="ProcessEnd.Exited"/>; for <see cref="ProcessEnd.NotStarted"/> <see cref="Stderr"/> says why.</summary>
internal sealed record ProcessExit(ProcessEnd End, int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed);

/// <summary>
/// One local process run: <see cref="Input"/> then <see cref="Upload"/> on stdin, which is then closed. Stdout goes raw to
/// <see cref="Output"/>, line by line to <see cref="Lines"/> (returning true stops the process), or is captured as text.
/// </summary>
internal sealed record ProcessCall(string Executable, IReadOnlyList<string> Arguments, byte[] Input, Stream? Upload, Stream? Output, Func<string, bool>? Lines, TimeSpan Timeout)
{
    /// <summary>Variables set for this process on top of the inherited environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

// The seam between a game host and the executables it drives (ssh, docker, a local shell, git). Tests replace it with fakes,
// so argument composition, escaping and outcome mapping run without any of them.
internal interface IProcessLauncher
{
    Task<ProcessExit> RunAsync(ProcessCall call, CancellationToken cancellation);
    /// <summary>Starts a long-running process (a port forward) with an empty stdin; stderr is kept for reports.</summary>
    IStartedProcess Start(string executable, IReadOnlyList<string> arguments);
}

/// <summary>
/// A started long-running helper process (an ssh port forward): an <see cref="IOwnedProcess"/> that also keeps its stderr and
/// exit code for reports. <see cref="IOwnedProcess.HasExited"/> is the liveness check: the exit task also waits for the
/// process's pipes to close, which a child that inherited them can delay.
/// </summary>
internal interface IStartedProcess : IOwnedProcess
{
    string Stderr { get; }
    /// <summary>The exit code; meaningful once <see cref="IOwnedProcess.HasExited"/> is true.</summary>
    int ExitCode { get; }
}

internal sealed class SystemProcessLauncher : IProcessLauncher
{
    public static SystemProcessLauncher Instance { get; } = new();
    public Task<ProcessExit> RunAsync(ProcessCall call, CancellationToken cancellation)
    {
        var start = StartInfo(call.Executable, call.Arguments);
        foreach (var (name, value) in call.Environment) start.Environment[name] = value;
        return ProcessRunner.RunAsync(start, call.Input, call.Upload, call.Output, call.Lines, call.Timeout, cancellation);
    }
    public IStartedProcess Start(string executable, IReadOnlyList<string> arguments) => new OwnedProcess(StartInfo(executable, arguments));
    private static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}

internal static class ProcessRunner
{
    /// <summary>
    /// Runs one process to its exit, the deadline or a stop requested by <paramref name="lines"/>; the last two kill it and its
    /// children. A process that cannot be started ends as <see cref="ProcessEnd.NotStarted"/>. Cancellation kills it too and throws.
    /// </summary>
    public static async Task<ProcessExit> RunAsync(ProcessStartInfo start, byte[] input, Stream? upload, Stream? output, Func<string, bool>? lines,
        TimeSpan timeout, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        start.UseShellExecute = false; start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        var clock = Stopwatch.StartNew();
        Process process;
        try { process = Process.Start(start) ?? throw new Win32Exception("Process.Start returned no process."); }
        catch (Win32Exception error) { return new ProcessExit(ProcessEnd.NotStarted, -1, "", $"could not start {start.FileName}: {error.Message}", clock.Elapsed); }
        using (process)
        {
            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new MemoryStream();
            Task readErrors = CopyLockedAsync(process.StandardError.BaseStream, errors);
            var text = new StringBuilder();
            Task readOutput = output != null ? process.StandardOutput.BaseStream.CopyToAsync(output)
                : lines != null ? ReadLinesAsync(process.StandardOutput.BaseStream, line => { lock (text) text.Append(line).Append('\n'); if (lines(line)) stop.TrySetResult(); })
                : ReadTextAsync(process.StandardOutput.BaseStream, text);
            Task write = Task.Run(async () =>
            {
                try
                {
                    var stdin = process.StandardInput.BaseStream;
                    await stdin.WriteAsync(input).ConfigureAwait(false);
                    if (upload != null) await upload.CopyToAsync(stdin).ConfigureAwait(false);
                }
                catch (IOException) { /* The process stopped reading; its exit says why. */ }
                finally { try { process.StandardInput.Close(); } catch (IOException) { } }
            });
            var exited = process.WaitForExitAsync(CancellationToken.None);
            // The run's timeout starts once the process is running, not at Process.Start.
            await ExitRace.RunAsync(stop.Task, exited, Stopwatch.StartNew(), timeout, cancellation).ConfigureAwait(false);
            var end = stop.Task.IsCompleted ? ProcessEnd.Stopped : exited.IsCompleted ? ProcessEnd.Exited : ProcessEnd.TimedOut;
            if (!exited.IsCompleted)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* Exited meanwhile. */ }
                await exited.ConfigureAwait(false);
            }
            // A child that outlived the process may still hold its pipes open; its output is not waited for indefinitely.
            await Task.WhenAny(Task.WhenAll(readOutput, readErrors, write), Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested && end != ProcessEnd.Exited) throw new OperationCanceledException(cancellation);
            string stdout; lock (text) stdout = text.ToString();
            string stderr; lock (errors) stderr = Encoding.UTF8.GetString(errors.ToArray());
            return new ProcessExit(end, end == ProcessEnd.Exited ? process.ExitCode : -1, stdout, stderr, clock.Elapsed);
        }
    }

    // Readers still running when a bounded wait gives up keep writing; everything they share is locked.
    private static async Task CopyLockedAsync(Stream stream, MemoryStream target)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            lock (target) target.Write(buffer, 0, read);
    }

    private static async Task ReadTextAsync(Stream stream, StringBuilder text)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        string all = await reader.ReadToEndAsync().ConfigureAwait(false);
        lock (text) text.Append(all);
    }

    private static async Task ReadLinesAsync(Stream stream, Action<string> line)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } next) line(next);
    }
}

internal sealed class OwnedProcess : IStartedProcess
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    public OwnedProcess(ProcessStartInfo start)
    {
        start.UseShellExecute = false; start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        _process = Process.Start(start) ?? throw new IOException("Could not start " + start.FileName);
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.Append(e.Data).Append('\n'); };
        _process.OutputDataReceived += (_, _) => { };
        _process.BeginErrorReadLine(); _process.BeginOutputReadLine();
        _process.StandardInput.Close();
    }
    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }
    public int ExitCode => _process.ExitCode;
    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        await _process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        // A wait abandoned until the tunnel is disposed completes after Dispose, when the code can no longer be read.
        try { return _process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
    public void Stop(TimeSpan timeout)
    {
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* Exited meanwhile. */ }
        }
        if (!_process.WaitForExit((int)timeout.TotalMilliseconds)) throw new TimeoutException("Process " + _process.Id + " did not stop.");
    }
    public void Dispose() => _process.Dispose();
}
