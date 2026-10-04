using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game.Fakes;

/// <summary>
/// A scripted owned dedicated server for testing startup, readiness, identity, restart and teardown logic without a
/// game. <see cref="Launch"/> starts a <see cref="FakeOwnedProcess"/>; <see cref="Connect"/> returns a transport that
/// answers the session capability (token, PID, save root, complete, dedicated) and the strict pins like a server with a
/// session adapter. Switches make each failure happen; gates hold a step open until the test releases it.
/// <see cref="Events"/> records launch/probe/pins/stop/dispose/disconnect with the boot number, in order.
/// </summary>
public sealed class FakeOwnedServer
{
    private readonly object _sync = new();
    private readonly List<string> _events = [], _tokens = [];
    private readonly List<TimeSpan> _pinTimeouts = [];
    private FakeOwnedProcess? _current;
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
    /// <summary>The server's test access as ValheimCLI's <c>cli_access</c> reports it; <c>devcommands</c> toggles it, <c>confirmcheats</c> acknowledges cheats. Each launch starts with both off, as a new game process does.</summary>
    public bool Devcommands { get; set; }
    public bool CheatsAcknowledged { get; set; }
    /// <summary>Accept <c>confirmcheats</c> without acknowledging cheats: a server whose access never becomes ready.</summary>
    public bool IgnoreConfirmCheats { get; set; }
    /// <summary>How long each readiness probe takes (blocking).</summary>
    public TimeSpan ProbeDelay { get; set; }
    /// <summary>Held connections and pin checks wait for these gates (at most 30 s).</summary>
    public ManualResetEventSlim? ConnectGate { get; set; }
    public ManualResetEventSlim? PinGate { get; set; }
    /// <summary>Set when a connection or a pin check starts: synchronise failure injection with the stage under test.</summary>
    public ManualResetEventSlim ConnectEntered { get; } = new();
    public ManualResetEventSlim PinEntered { get; } = new();
    /// <summary>Runs on every launch, for example to write the boot's log lines or exit the process.</summary>
    public Action<FakeOwnedProcess>? OnLaunch { get; set; }

    public IReadOnlyList<string> Events { get { lock (_sync) return _events.ToArray(); } }
    public IReadOnlyList<string> Tokens { get { lock (_sync) return _tokens.ToArray(); } }
    public IReadOnlyList<TimeSpan> PinTimeouts { get { lock (_sync) return _pinTimeouts.ToArray(); } }
    public int Connects => Volatile.Read(ref _connects);
    public int Disconnects => Volatile.Read(ref _disconnects);
    internal void Record(string item) { lock (_sync) _events.Add(item); }

    public IOwnedProcess Launch(string token)
    {
        FakeOwnedProcess process;
        lock (_sync)
        {
            _tokens.Add(token); process = _current = new FakeOwnedProcess(this, _tokens.Count); _events.Add("launch" + process.Id);
            // As in the game, a new process starts with devcommands off and cheats unacknowledged.
            Devcommands = false; CheatsAcknowledged = false;
        }
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

    private sealed class SessionTransport(FakeOwnedServer server, FakeOwnedProcess process, string token) : IGameTransport
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
            // Test access (PinnedServerRunOptions.TestAccess), as ValheimCLI's cli_access and the game's commands answer it.
            if (command == "cli_access")
                return new() { Ok = true, Output = ["ACCESS " + JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, complete = true, devcommands = server.Devcommands, cheatsAcknowledged = server.CheatsAcknowledged,
                    allowOnServerClients = false, server = true, dedicated = true, joinedClient = false, localPlayer = false, profileAvailable = true,
                })] };
            if (command == "devcommands") { server.Record("devcommands" + process.Id); server.Devcommands = !server.Devcommands; return new() { Ok = true, Output = ["Dev commands: " + server.Devcommands] }; }
            if (command == "confirmcheats") { server.Record("confirmcheats" + process.Id); if (!server.IgnoreConfirmCheats) server.CheatsAcknowledged = true; return new() { Ok = true, Output = [] }; }
            if (command != "cli_extension " + server.SessionCapability) throw new InvalidOperationException("Unexpected command during startup: " + command);
            server.Record("probe" + process.Id);
            if (server.ProbeDelay > TimeSpan.Zero) Thread.Sleep(server.ProbeDelay);
            var reply = SessionReply(server.Extension, token, server.ReportWrongPid ? process.Id + 1000 : process.Id, server.SaveRoot, server.Ready);
            return new() { Ok = true, Output = ["EXTENSION_RESULT " + reply.GetRawText()] };
        }
        public void Dispose() { server.Record("disconnect" + process.Id); Interlocked.Increment(ref server._disconnects); }
    }
}

/// <summary>
/// The toolkit's one fake <see cref="IOwnedProcess"/>, for an owned server or client: exits when the test says so
/// (<see cref="Exit"/>) or when stopped. Launched by a <see cref="FakeOwnedServer"/>, it records its stops there and follows
/// that server's switches. Created on its own, for example as the client process of <c>ClientSession.Launch</c>, its own
/// <see cref="IgnoreQuit"/> and <see cref="StopFailure"/> decide how a stop ends.
/// </summary>
public sealed class FakeOwnedProcess : IOwnedProcess
{
    private readonly FakeOwnedServer? _server;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stops, _disposals;
    internal FakeOwnedProcess(FakeOwnedServer server, int id) { _server = server; Id = id; }
    /// <summary>A fake process of its own, with the given PID.</summary>
    public FakeOwnedProcess(int id = 1) => Id = id;
    /// <summary>A fake process that has already exited with <paramref name="code"/>, as a client that crashes during startup.</summary>
    public static FakeOwnedProcess Exited(int code, int id = 1)
    {
        var process = new FakeOwnedProcess(id);
        process.Exit(code);
        return process;
    }
    /// <summary>The PID; for a <see cref="FakeOwnedServer"/>'s process, its boot number (1, 2, ...).</summary>
    public int Id { get; }
    public bool HasExited => _exit.Task.IsCompleted;
    /// <summary>How many times <see cref="Stop"/> or <see cref="StopCleanly"/> was called, failed stops included.</summary>
    public int Stops => Volatile.Read(ref _stops);
    public int Disposals => Volatile.Read(ref _disposals);
    /// <summary>Asked to quit, it does not exit, so <see cref="StopCleanly"/> kills it (<see cref="StopOutcome.Killed"/>). A <see cref="FakeOwnedServer"/>'s process follows <see cref="FakeOwnedServer.IgnoreQuit"/> instead.</summary>
    public bool IgnoreQuit { get; set; }
    /// <summary>
    /// When set, every stop (even of an exited process) counts, then throws a new exception from it and leaves the process as
    /// it was: an unproven stop (an <see cref="IOException"/>) or a refused one (a <see cref="TimeoutException"/>).
    /// </summary>
    public Func<Exception>? StopFailure { get; set; }
    public void Exit(int code) => _exit.TrySetResult(code);
    public Task<int> WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);
    public void Stop(TimeSpan timeout)
    {
        Stopping();
        if (_server?.RefuseStop == true) throw new TimeoutException("Fake server refused to stop.");
        Exit(-1);
    }
    /// <summary>
    /// Records <c>stop{Id}</c> as <see cref="Stop"/> does. Asked to quit, it exits with 0 (<see cref="StopOutcome.Clean"/>), unless
    /// it ignores the request (<see cref="IgnoreQuit"/>): then it is killed after <paramref name="quit"/> and exits with -1.
    /// </summary>
    public ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill)
    {
        Stopping();
        if (HasExited) return new(StopOutcome.AlreadyExited, _exit.Task.Result, TimeSpan.Zero, "not asked: it had exited");
        if (_server?.RefuseStop == true) throw new TimeoutException("Fake server refused to stop.");
        if (_server?.IgnoreQuit ?? IgnoreQuit) { Exit(-1); return new(StopOutcome.Killed, -1, quit, "fake quit request; no exit within the wait"); }
        Exit(0);
        return new(StopOutcome.Clean, 0, TimeSpan.Zero, "fake quit request");
    }
    private void Stopping()
    {
        Interlocked.Increment(ref _stops);
        _server?.Record("stop" + Id);
        if (StopFailure is { } failure) throw failure();
    }
    public void Dispose()
    {
        Interlocked.Increment(ref _disposals);
        _server?.Record("dispose" + Id);
    }
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
