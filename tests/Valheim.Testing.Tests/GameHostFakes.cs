using System.Net;
using System.Net.Sockets;
using System.Text;
using Valheim.Testing.Game;

/// <summary>
/// Stands in for ssh, docker and local shells: records every call and answers from a queue, so a host's argument composition,
/// escaping and outcome mapping are tested without starting anything.
/// </summary>
internal sealed class FakeLauncher : IProcessLauncher
{
    private readonly Queue<Func<ProcessCall, ProcessExit>> _replies = new();
    public List<ProcessCall> Calls { get; } = [];
    public List<(string Executable, IReadOnlyList<string> Arguments)> Started { get; } = [];
    public Func<IReadOnlyList<string>, IStartedProcess>? OnStart { get; set; }

    public FakeLauncher Reply(Func<ProcessCall, ProcessExit> reply) { _replies.Enqueue(reply); return this; }
    /// <summary>The transport exits with <paramref name="code"/>; add <see cref="Report"/> to stderr for the host's own exit report.</summary>
    public FakeLauncher Exits(int code, string stdout = "", string stderr = "") => Reply(_ => Exit(code, stdout, stderr));
    public FakeLauncher TimesOut(string stdout = "", string stderr = "") => Reply(_ => new ProcessExit(ProcessEnd.TimedOut, -1, stdout, stderr, TimeSpan.FromSeconds(2)));

    public Task<ProcessExit> RunAsync(ProcessCall call, CancellationToken cancellation)
    {
        Calls.Add(call);
        return Task.FromResult(_replies.Dequeue()(call));
    }
    public IStartedProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        Started.Add((executable, arguments));
        return (OnStart ?? throw new InvalidOperationException("No process expected."))(arguments);
    }

    public static ProcessExit Exit(int code, string stdout = "", string stderr = "") => new(ProcessEnd.Exited, code, stdout, stderr, TimeSpan.FromMilliseconds(5));
    /// <summary>The wrapper's exit report, as the host writes it at the end of stderr.</summary>
    public static string Report(int code) => "\n[vt-exit] " + code + "\n";
    /// <summary>The script a call sent: the first line of stdin, base64. The host's wrapper writes it to a temporary file.</summary>
    public static string Script(ProcessCall call) => Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(call.Input).Split('\n')[0]));
    /// <summary>The secrets line a call sent: the second line of stdin, which the host's wrapper keeps in memory.</summary>
    public static string Secrets(ProcessCall call) => Encoding.ASCII.GetString(call.Input).Split('\n')[1];
}

/// <summary>An ssh port forward that listens on the requested loopback port until stopped.</summary>
internal sealed class FakeForward : IStartedProcess
{
    private readonly TcpListener? _listener;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public FakeForward(IReadOnlyList<string> arguments, bool listen = true, int exitCode = 0, string stderr = "")
    {
        int at = arguments.ToList().IndexOf("-L");
        LocalPort = int.Parse(arguments[at + 1].Split(':')[1]);
        Stderr = stderr;
        if (listen) { _listener = new TcpListener(IPAddress.Loopback, LocalPort); _listener.Start(); }
        else { ExitCode = exitCode; HasExited = true; _exit.SetResult(exitCode); }
    }
    public int LocalPort { get; }
    public int Id => 4242;
    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }
    public string Stderr { get; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }
    public Task<int> WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);
    public void Stop(TimeSpan timeout)
    {
        Stopped = true;
        _listener?.Stop();
        HasExited = true; ExitCode = 255; _exit.TrySetResult(255);
    }
    public void Dispose() { Disposed = true; _listener?.Stop(); }
}
