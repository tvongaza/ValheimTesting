using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game.Fakes;

/// <summary>
/// A scripted owned dedicated server for testing startup, readiness, identity, restart and teardown logic without a
/// game. <see cref="Launch"/> starts a <see cref="FakeServerProcess"/>; <see cref="Connect"/> returns a transport that
/// answers the session capability (token, PID, save root, complete, dedicated) and the strict pins like a server with a
/// session adapter. Switches make each failure happen; gates hold a step open until the test releases it.
/// <see cref="Events"/> records launch/probe/pins/stop/dispose/disconnect with the boot number, in order.
/// </summary>
public sealed class FakeOwnedServer
{
    private readonly object _sync = new();
    private readonly List<string> _events = [], _tokens = [];
    private readonly List<TimeSpan> _pinTimeouts = [];
    private FakeServerProcess? _current;
    private int _connects, _disconnects;

    /// <param name="extension">The session adapter's extension id; the capability is <c>{extension}/session</c>.</param>
    public FakeOwnedServer(string extension = "test.mod", string? saveRoot = null)
    {
        Extension = extension;
        SaveRoot = saveRoot ?? Path.GetTempPath();
    }
    public string Extension { get; }
    public string SessionCapability => Extension + "/session";
    public string SaveRoot { get; }
    /// <summary>Whether the adapter reports its session complete (mod ready).</summary>
    public bool Ready { get; set; } = true;
    public bool RefuseStop { get; set; }
    /// <summary>A clean stop's quit request is ignored, so the process is killed after the wait (<see cref="StopOutcome.Killed"/>).</summary>
    public bool IgnoreQuit { get; set; }
    public bool RefusePins { get; set; }
    /// <summary>The session reports another PID than the launched process's.</summary>
    public bool ReportWrongPid { get; set; }
    public bool ExitOnLaunch { get; set; }
    /// <summary>Connecting throws <see cref="IOException"/>, as before the CLI listens.</summary>
    public bool RefuseConnections { get; set; }
    /// <summary>How long each readiness probe takes (blocking).</summary>
    public TimeSpan ProbeDelay { get; set; }
    /// <summary>Held connections and pin checks wait for these gates (at most 30 s).</summary>
    public ManualResetEventSlim? ConnectGate { get; set; }
    public ManualResetEventSlim? PinGate { get; set; }
    /// <summary>Set when a connection or a pin check starts: synchronise failure injection with the stage under test.</summary>
    public ManualResetEventSlim ConnectEntered { get; } = new();
    public ManualResetEventSlim PinEntered { get; } = new();
    /// <summary>Runs on every launch, for example to write the boot's log lines or exit the process.</summary>
    public Action<FakeServerProcess>? OnLaunch { get; set; }

    public IReadOnlyList<string> Events { get { lock (_sync) return _events.ToArray(); } }
    public IReadOnlyList<string> Tokens { get { lock (_sync) return _tokens.ToArray(); } }
    public IReadOnlyList<TimeSpan> PinTimeouts { get { lock (_sync) return _pinTimeouts.ToArray(); } }
    public int Connects => Volatile.Read(ref _connects);
    public int Disconnects => Volatile.Read(ref _disconnects);
    internal void Record(string item) { lock (_sync) _events.Add(item); }

    public IServerProcess Launch(string token)
    {
        FakeServerProcess process;
        lock (_sync) { _tokens.Add(token); process = _current = new FakeServerProcess(this, _tokens.Count); _events.Add("launch" + process.Id); }
        if (ExitOnLaunch) process.Exit(1);
        OnLaunch?.Invoke(process);
        return process;
    }
    public IGameTransport Connect()
    {
        Interlocked.Increment(ref _connects);
        if (RefuseConnections) throw new IOException("CLI not listening yet.");
        SessionTransport transport;
        lock (_sync) transport = new SessionTransport(this, _current ?? throw new InvalidOperationException("Nothing launched."), _tokens[^1]);
        ConnectEntered.Set();
        ConnectGate?.Wait(TimeSpan.FromSeconds(30));
        return transport;
    }
    /// <summary>An owned session over this server with the given deadlines (poll 1 ms by default).</summary>
    public OwnedServerSession Session(TimeSpan startup, TimeSpan? command = null, TimeSpan? poll = null, StartupEvents? events = null, string expectations = "cli_expect worlduid=1") =>
        new(Launch, Connect, SaveRoot, expectations, SessionCapability, startup, command ?? TimeSpan.FromSeconds(1), poll ?? TimeSpan.FromMilliseconds(1)) { Events = events };

    /// <summary>The session capability's reply, as a session adapter builds it.</summary>
    public static JsonElement SessionReply(string extension, string token, int pid, string saveRoot, bool complete, bool dedicated = true) =>
        JsonSerializer.SerializeToElement(new { schemaVersion = 1, ok = true, extension, data = new { source = "owned-test-session", token, pid, saveRoot, complete, dedicated } });

    private sealed class SessionTransport(FakeOwnedServer server, FakeServerProcess process, string token) : IGameTransport
    {
        public CommandResult Execute(string command, TimeSpan timeout)
        {
            if (command.StartsWith("cli_expect", StringComparison.Ordinal))
            {
                lock (server._sync) { server._events.Add("pins" + process.Id); server._pinTimeouts.Add(timeout); }
                server.PinEntered.Set();
                server.PinGate?.Wait(TimeSpan.FromSeconds(30));
                return new() { Ok = !server.RefusePins, Output = [server.RefusePins ? "ERROR: pins" : "OK: EXPECT"] };
            }
            if (command != "cli_extension " + server.SessionCapability) throw new InvalidOperationException("Unexpected command during startup: " + command);
            server.Record("probe" + process.Id);
            if (server.ProbeDelay > TimeSpan.Zero) Thread.Sleep(server.ProbeDelay);
            var reply = SessionReply(server.Extension, token, server.ReportWrongPid ? process.Id + 1000 : process.Id, server.SaveRoot, server.Ready);
            return new() { Ok = true, Output = ["EXTENSION_RESULT " + reply.GetRawText()] };
        }
        public void Dispose() { server.Record("disconnect" + process.Id); Interlocked.Increment(ref server._disconnects); }
    }
}

/// <summary>A launched fake server: exits when the test says so or when stopped (unless its server refuses to stop).</summary>
public sealed class FakeServerProcess : IServerProcess
{
    private readonly FakeOwnedServer _server;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal FakeServerProcess(FakeOwnedServer server, int id) { _server = server; Id = id; }
    /// <summary>The boot number (1, 2, ...), also used as the PID.</summary>
    public int Id { get; }
    public bool HasExited => _exit.Task.IsCompleted;
    public void Exit(int code) => _exit.TrySetResult(code);
    public Task<int> WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);
    public void Stop(TimeSpan timeout) { _server.Record("stop" + Id); if (_server.RefuseStop) throw new TimeoutException("Fake server refused to stop."); Exit(-1); }
    /// <summary>
    /// Records <c>stop{Id}</c> as <see cref="Stop"/> does. Asked to quit, it exits with 0 (<see cref="StopOutcome.Clean"/>), unless
    /// <see cref="FakeOwnedServer.IgnoreQuit"/>: then it is killed after <paramref name="quit"/> and exits with -1.
    /// </summary>
    public ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill)
    {
        _server.Record("stop" + Id);
        if (HasExited) return new(StopOutcome.AlreadyExited, _exit.Task.Result, TimeSpan.Zero, "not asked: it had exited");
        if (_server.RefuseStop) throw new TimeoutException("Fake server refused to stop.");
        if (_server.IgnoreQuit) { Exit(-1); return new(StopOutcome.Killed, -1, quit, "fake quit request; no exit within the wait"); }
        Exit(0);
        return new(StopOutcome.Clean, 0, TimeSpan.Zero, "fake quit request");
    }
    public void Dispose() => _server.Record("dispose" + Id);
}

/// <summary>A temporary runtime directory with a BepInEx log, for startup-event tests. Deleted on dispose.</summary>
public sealed class TempRuntime : IDisposable
{
    public TempRuntime()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "valheim-testing-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "BepInEx"));
    }
    public string DirectoryPath { get; }
    public string LogPath => Path.Combine(DirectoryPath, "BepInEx", "LogOutput.log");
    public void Append(string text) => File.AppendAllText(LogPath, text);
    /// <summary>Rewrites the log from the start, as BepInEx does at every boot.</summary>
    public void Replace(string text) => File.WriteAllText(LogPath, text);
    public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
}
