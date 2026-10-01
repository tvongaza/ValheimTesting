using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

// The seam lets fast tests drive the actual restart/identity rules without Unity.
public interface IServerProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    /// <summary>Completes with the exit code when the process exits; startup fails as soon as it does.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellation);
    /// <summary>Kills the process at once and waits up to <paramref name="timeout"/> for it to exit.</summary>
    void Stop(TimeSpan timeout);
    /// <summary>
    /// Asks the process to quit, waits up to <paramref name="quit"/> for it to exit, and kills it (<see cref="Stop"/> with
    /// <paramref name="kill"/>) only if it has not. Returns how it ended. A process that cannot be asked is killed at once,
    /// which this default does: implementations that can ask override it.
    /// </summary>
    ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill)
    {
        bool exited = HasExited;
        var clock = Stopwatch.StartNew();
        Stop(kill);
        return new(exited ? StopOutcome.AlreadyExited : StopOutcome.Killed, null, clock.Elapsed, "this process cannot be asked to quit");
    }
}

/// <summary>
/// What an owned server's startup waits on instead of retrying connections. The process exit is always watched.
/// Without <see cref="CliLog"/>, connecting falls back to bounded retries at the session's poll interval.
/// </summary>
public sealed class StartupEvents
{
    /// <summary>ValheimCLI's line once its command server accepts connections.</summary>
    public static readonly Regex CliListening = new(@"Command server listening on \S+:\d+", RegexOptions.CultureInvariant);
    /// <summary>
    /// BepInEx's chainloader lines for a plugin it did not load: a missing dependency, an incompatibility or an exception
    /// while loading. In a pinned runtime any of them means the wrong environment, so pass them as <see cref="Failures"/>.
    /// </summary>
    public static readonly IReadOnlyList<Regex> BepInExPluginLoadFailures =
        [new(@"^\[(?:Warning|Error|Fatal) *: *BepInEx\] (?:Could not load|Error loading) \[", RegexOptions.CultureInvariant)];
    /// <summary>
    /// Lines that mean the runtime's assemblies do not fit the game: a <c>TypeLoadException</c>, <c>MissingMethodException</c>
    /// or <c>MissingFieldException</c> (a leftover preloader patcher or a mod built for another game version) in a BepInEx
    /// warning, error or fatal line, or an exception line that starts with its name as Unity and .NET write it
    /// (<c>System.TypeLoadException: ...</c>); and ValheimCLI's packs reporting that its core never became ready ("CLI core
    /// 1.1 is not ready."). An info or debug line that only mentions an exception (a mod's handled soft dependency) does not
    /// count. Before ValheimCLI listens, all of them mean the run cannot work, so startup should end at once instead of at its deadline.
    /// </summary>
    public static readonly IReadOnlyList<Regex> RuntimeLoadFailures =
    [
        new(@"^\[(?:Warning|Error|Fatal) *:[^\]]*\].*\b(?:TypeLoadException|MissingMethodException|MissingFieldException)\b", RegexOptions.CultureInvariant),
        new(@"^(?:System\.)?(?:TypeLoadException|MissingMethodException|MissingFieldException): ", RegexOptions.CultureInvariant),
        new(@"^\[(?:Error|Fatal) *:[^\]]*\] CLI core \S+ is not ready\.", RegexOptions.CultureInvariant),
    ];
    /// <summary><see cref="BepInExPluginLoadFailures"/> and <see cref="RuntimeLoadFailures"/>: what the owned server and client startups fail on.</summary>
    public static readonly IReadOnlyList<Regex> StartupFailures = [.. BepInExPluginLoadFailures, .. RuntimeLoadFailures];
    /// <summary>The log ValheimCLI writes to, normally the runtime's BepInEx/LogOutput.log. No connection is tried before <see cref="Listening"/> appears in it.</summary>
    public string? CliLog { get; init; }
    public Regex Listening { get; init; } = CliListening;
    /// <summary>
    /// Waits for ValheimCLI's listening line somewhere <see cref="CliLog"/> cannot reach, such as a server host's log through
    /// <see cref="IGameHost.WaitForLogAsync"/>; it gets the time left and a token cancelled when the process exits first, and
    /// throws <see cref="WaitFailedException"/> or <see cref="WaitTimeoutException"/> as <see cref="HostLogResult.EnsureMatched"/>
    /// does. Used only without <see cref="CliLog"/>; after it, as after the local line, a failed connection is a fault.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task>? CliListeningWait { get; init; }
    /// <summary>Lines in <see cref="CliLog"/> that end startup at once, for example a required plugin's load error.</summary>
    public IReadOnlyList<Regex> Failures { get; init; } = [];
    /// <summary>
    /// Opens a connection used only for state pushes, for example <c>() =&gt; StateWait.Connect(host, port)</c>.
    /// Startup then waits for one of <see cref="ReadyStates"/> before the first readiness probe.
    /// </summary>
    public Func<StateWait>? States { get; init; }
    public IReadOnlyList<string> ReadyStates { get; init; } = StateWait.WorldLoaded;
    public IReadOnlyList<string> FailureStates { get; init; } = [];

    /// <summary>
    /// Waits for this launch's first line in BepInEx's log (<paramref name="log"/>, opened before the launch, so an earlier
    /// run's lines never count) within <paramref name="within"/>; a <see cref="StartupFailures"/> line ends it at once. Doorstop
    /// starts BepInEx before the game's first frame, so a game still running without that line runs without BepInEx: the
    /// expiry is a <see cref="WaitFailedException"/> naming the loader, not a timeout of the whole startup.
    /// </summary>
    internal static async Task WaitForBepInExLog(LogWait log, TimeSpan within, string? playerLog, CancellationToken cancellation)
    {
        try { await log.WaitAsync(AnyLine, within, StartupFailures, cancellation).ConfigureAwait(false); }
        catch (WaitTimeoutException timeout)
        {
            throw new WaitFailedException("BepInEx's startup log", $"BepInEx wrote nothing to {log.LogPath} within {WaitText.Seconds(within)} of the launch, so the game runs without it: " +
                $"Doorstop did not start BepInEx. Check that {BepInExLoader.WindowsProxy} and {BepInExLoader.WindowsConfig} (the Doorstop library and run script on macOS and Linux) come from one BepInExPack; " +
                $"Unity's player log ({playerLog ?? "Player.log"}) shows what the game did", timeout.Elapsed, timeout.LastSeen);
        }
    }
    private static readonly Regex AnyLine = new("^", RegexOptions.CultureInvariant);

    // A process that exits before BepInEx writes its log never ran BepInEx: the reason is in Unity's own log.
    internal static string NoBepInExLog(string bepInExLog, string? playerLog) =>
        $" before BepInEx wrote {bepInExLog}. Read the Unity player log ({playerLog ?? "Player.log, or the file passed with -logFile"}) for the reason;" +
        " security software that blocks or quarantines the game or BepInEx's Doorstop loader is a known cause";
}

public sealed class OwnedServerSession : IDisposable
{
    private readonly Func<string, IServerProcess> _launch;
    private readonly Func<IGameTransport> _connect;
    private readonly string _saveRoot, _expectations, _sessionCapability, _extension;
    private readonly TimeSpan _startup, _command, _poll;
    private readonly CancellationToken _cancellation;
    private IServerProcess? _process;
    private GameActor? _actor;
    public List<int> StartedProcesses { get; } = [];
    /// <summary>
    /// How long a stop or restart waits for the server to quit after asking it (its world save and shutdown) before it kills it.
    /// Default two minutes.
    /// </summary>
    public TimeSpan QuitTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>How each owned process ended, in launch order: one entry per stop, including restarts.</summary>
    public List<ProcessStop> Stops { get; } = [];
    /// <summary>Events startup waits on; null keeps bounded connection retries. The process exit is watched either way.</summary>
    public StartupEvents? Events { get; init; }
    public OwnedServerSession(Func<string, IServerProcess> launch, Func<IGameTransport> connect,
        string saveRoot, string expectations, string sessionCapability, TimeSpan startup, TimeSpan command, TimeSpan? poll = null, CancellationToken cancellation = default)
    {
        var parts = sessionCapability.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.' && ch != '-' && ch != '_')))
            throw new ArgumentException("Use one namespaced session capability, without arguments.");
        _sessionCapability = sessionCapability; _extension = parts[0];
        _cancellation = cancellation; _launch = launch; _connect = connect; _saveRoot = Path.GetFullPath(saveRoot);
        // EnvironmentPinning.None is an explicitly unpinned run's (ServerRunPlan.ExpectCommand); the actor warns when it takes effect.
        _expectations = expectations == EnvironmentPinning.None ? expectations : StrictExpectations.Normalize(expectations);
        _startup = startup; _command = command; _poll = poll ?? TimeSpan.FromMilliseconds(500);
        if (startup <= TimeSpan.Zero || command <= TimeSpan.Zero || _poll < TimeSpan.Zero) throw new ArgumentException("Invalid session deadlines.");
    }
    public GameActor Start() => StartAsync().GetAwaiter().GetResult();
    /// <summary>
    /// Launches one owned server and returns its strictly pinned actor. Every stage races the process exit, which ends
    /// startup at once with the exit code and the last log line. With <see cref="Events"/>, the first connection waits for
    /// ValheimCLI's listening line and, optionally, a world-loaded state push. The adapter's own readiness has no event:
    /// an unregistered or incomplete session observation is re-probed, read-only, every poll interval until the deadline.
    /// The startup deadline bounds everything, including connecting and the final pin verification, which gets only the
    /// time left (at most the command timeout).
    /// </summary>
    public async Task<GameActor> StartAsync()
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_process != null) throw new InvalidOperationException("Stop the previous owned process before starting another.");
        var events = Events;
        string token = Guid.NewGuid().ToString("N");
        // Opened before launch: a previous boot's lines in the same log can never satisfy this one.
        using var log = events?.CliLog is { } cliLog ? new LogWait(cliLog) : null;
        var process = _process = _launch(token); StartedProcesses.Add(process.Id);
        var clock = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        var exited = process.WaitForExitAsync(stop.Token);
        TimeSpan Left(string stage)
        {
            var left = _startup - clock.Elapsed;
            return left > TimeSpan.Zero ? left : throw new WaitTimeoutException(stage + " within the startup deadline", clock.Elapsed, log?.Refresh());
        }
        var hostWait = log == null ? events?.CliListeningWait : null;
        async Task<Exception> Exited(string stage) => new WaitFailedException(stage, "owned server exited with code " + await exited.ConfigureAwait(false) +
            (log != null && !log.HasOutput() ? StartupEvents.NoBepInExLog(log.LogPath, null) : ""),
            clock.Elapsed, log != null ? log.Refresh() : hostWait != null ? "the startup log is on the server's host" : "no startup log configured");
        async Task UntilExit(string stage, Func<TimeSpan, CancellationToken, Task> wait)
        {
            using var abandon = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var waiting = wait(Left(stage), abandon.Token);
            if (await Task.WhenAny(waiting, exited).ConfigureAwait(false) == waiting) { await waiting.ConfigureAwait(false); return; }
            abandon.Cancel();
            try { await waiting.ConfigureAwait(false); } catch { /* Abandoned: the exit is the result. */ }
            throw await Exited(stage).ConfigureAwait(false);
        }
        // A blocking call (connecting, a command) raced against the process exit and the time left. When either wins,
        // startup ends at once: `abandon` (or the caller's cleanup) closes what the call is blocked on, a result that
        // still arrives afterwards (a late connection) is disposed, never leaked, and `afterAbandoned` runs once the
        // call has returned, for cleanup that must wait for it.
        async Task<T> Bounded<T>(string stage, Func<T> call, string? lastSeen = null, Action? abandon = null, Action? afterAbandoned = null)
        {
            var left = Left(stage);
            // A thread of its own: a busy thread pool must not delay startup, and a blocked call must not hold a pool thread.
            var running = Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var first = await Task.WhenAny(running, exited, Task.Delay(left, expiry.Token)).ConfigureAwait(false);
            expiry.Cancel();
            if (first == running) return await running.ConfigureAwait(false);
            try { abandon?.Invoke(); } catch { /* Best effort: the call is abandoned either way. */ }
            _ = running.ContinueWith(late =>
            {
                if (late.IsCompletedSuccessfully) (late.Result as IDisposable)?.Dispose(); else _ = late.Exception;
                afterAbandoned?.Invoke();
            }, TaskScheduler.Default);
            _cancellation.ThrowIfCancellationRequested();
            if (first == exited) throw await Exited(stage).ConfigureAwait(false);
            throw new WaitTimeoutException(stage + " within the startup deadline", clock.Elapsed, lastSeen ?? log?.Refresh());
        }
        try
        {
            if (log != null)
                await UntilExit("ValheimCLI listening", (left, ct) => log.WaitAsync(events!.Listening, left, events.Failures, ct)).ConfigureAwait(false);
            else if (hostWait != null)
                await UntilExit("ValheimCLI listening", hostWait).ConfigureAwait(false);
            if (events?.States is { } openStates)
            {
                using var states = await Bounded("world loaded", openStates).ConfigureAwait(false);
                await UntilExit("world loaded", (left, ct) => states.WaitAsync(events.ReadyStates, left, events.FailureStates, ct)).ConfigureAwait(false);
            }
            const string stage = "owned server readiness";
            string last = "No CLI connection";
            // Retry incomplete read-only startup observations only (and, without a listening line, the connection). Never retry a mutation.
            while (true)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (exited.IsCompleted) throw await Exited(stage).ConfigureAwait(false);
                IGameTransport? transport = null;
                try
                {
                    var connected = transport = await Bounded(stage, _connect, last).ConfigureAwait(false);
                    var timeout = Left(stage);
                    if (timeout > _command) timeout = _command;
                    // Bootstrap exception: this adapter capability MUST be read-only. World pins cannot
                    // hold before loading completes. Prove our token/PID/save root first, then strict-pin
                    // before returning an actor or issuing any gameplay action.
                    var reply = await Bounded(stage, () => connected.Execute("cli_extension " + _sessionCapability, timeout), last).ConfigureAwait(false);
                    if (!reply.Ok)
                    {
                        // The core may answer before the optional adapter is registered.
                        if (StartupUnavailable(reply))
                            last = "Console or session adapter not ready yet";
                        else throw new InvalidOperationException("Session observation refused: " + reply.ErrorCode);
                    }
                    else
                    {
                        using var document = GameActor.ParseLine(reply, "EXTENSION_RESULT ");
                        bool ready = CheckIdentity(document.RootElement, token, process.Id, _saveRoot, _extension);
                        if (ready)
                        {
                            // Verification gets the time left, not a full command timeout.
                            var verify = Left(stage);
                            var actor = new GameActor("owned-server", transport) { CommandTimeout = verify < _command ? verify : _command };
                            var attached = transport!; transport = null;
                            // Disposing the actor waits for its running command, so an abandoned verification closes the
                            // transport at once (ending the command) and disposes the actor once the command returns.
                            bool abandoned = false;
                            try
                            {
                                await Bounded(stage, () => { actor.VerifyEnvironment(_expectations); return true; },
                                    abandon: () => { abandoned = true; attached.Dispose(); }, afterAbandoned: actor.Dispose).ConfigureAwait(false);
                                if (exited.IsCompleted || clock.Elapsed >= _startup) throw new InvalidOperationException("Server exited or startup deadline expired during verification.");
                                actor.CommandTimeout = _command;
                                _actor = actor; return actor;
                            }
                            catch { if (!abandoned) actor.Dispose(); throw; }
                        }
                        last = "Owned server has not completed world/network loading";
                    }
                }
                catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException)
                {
                    // After the listening line a failed connection is a fault, not a startup race.
                    if (log != null || hostWait != null) throw new WaitFailedException(stage, "ValheimCLI announced its listener but the connection failed: " + error.Message, clock.Elapsed, log?.Refresh());
                    last = error is IOException ? "CLI transport unavailable" : "CLI socket unavailable";
                }
                finally { transport?.Dispose(); }
                var remaining = _startup - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new WaitTimeoutException(stage + " within the startup deadline", clock.Elapsed, last);
                if (_poll > TimeSpan.Zero) await Task.WhenAny(Task.Delay(remaining < _poll ? remaining : _poll, stop.Token), exited).ConfigureAwait(false);
            }
        }
        finally { stop.Cancel(); }
    }
    public static bool StartupUnavailable(CommandResult reply) => !reply.Ok &&
        (reply.Output.Any(x => x == "Error: Console not available (game not fully loaded)") ||
         reply.Output.Any(x => x.StartsWith("EXTENSION_RESULT ", StringComparison.Ordinal) && IsMissingExtension(x)));
    private static bool IsMissingExtension(string line)
    {
        try { using var doc = JsonDocument.Parse(line["EXTENSION_RESULT ".Length..]); return doc.RootElement.GetProperty("code").GetString() == "no_extension_command"; }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    public static bool CheckIdentity(JsonElement result, string token, int pid, string saveRoot, string expectedExtension)
    {
        if (result.GetProperty("schemaVersion").GetInt32() != 1 || !result.GetProperty("ok").GetBoolean() ||
            result.GetProperty("extension").GetString() != expectedExtension) throw new InvalidOperationException("Invalid session response.");
        var data = result.GetProperty("data");
        if (data.GetProperty("source").GetString() != "owned-test-session" || data.GetProperty("token").GetString() != token ||
            data.GetProperty("pid").GetInt32() != pid || !PathsEqual(data.GetProperty("saveRoot").GetString()!, saveRoot))
            throw new InvalidOperationException("Session identity mismatch; refusing this server.");
        if (!data.GetProperty("complete").GetBoolean()) return false;
        if (!data.GetProperty("dedicated").GetBoolean()) throw new InvalidOperationException("This session requires a dedicated server.");
        return true;
    }
    private static bool PathsEqual(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    /// <summary>
    /// Waits until an owned dedicated server accepts game connections, as its adapter's <paramref name="sessionCapability"/>
    /// reports in <c>acceptingConnections</c> (Valheim.Testing.Adapter). The game opens its socket when world generation
    /// finishes, which on a first boot comes well after the world has loaded, so a join before it times out. Call it just
    /// before the first join rather than at startup, so the wait overlaps other work. The game offers no event for it: the
    /// read-only observation is repeated every 250 ms until the timeout, which reports the last reading.
    /// </summary>
    public static void WaitUntilJoinable(GameActor server, string sessionCapability, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var capability = server.RequireCapability(sessionCapability);
        var clock = Stopwatch.StartNew();
        string last = "none";
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var reading = server.Observe(capability);
            last = reading.Data.GetRawText();
            if (!reading.Data.TryGetProperty("acceptingConnections", out var accepting) || accepting.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("The session capability does not report acceptingConnections; update the adapter to Valheim.Testing.Adapter's TestExtension.");
            if (accepting.GetBoolean()) return;
            if (clock.Elapsed >= timeout) throw new WaitTimeoutException("server accepting game connections", clock.Elapsed, last);
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
        }
    }

    public GameActor Restart()
    {
        Stop(); // A failure here must never launch the next process.
        return Start();
    }
    public void Stop()
    {
        try { _actor?.Dispose(); }
        finally
        {
            _actor = null;
            if (_process != null)
            {
                // Asked to quit first, so the game saves and retires its lobby; killed only after QuitTimeout.
                Stops.Add(_process.StopCleanly(QuitTimeout, TimeSpan.FromSeconds(15)));
                if (!_process.HasExited) throw new InvalidOperationException("Owned process did not exit; restart refused.");
                _process.Dispose(); _process = null;
            }
        }
    }
    public void Dispose() => Stop();
}

// Starts one direct executable (an owned server, or an owned client through ClientSession); launch scripts must exec/wait, never detach a child.
// The PID handshake refuses a daemonized server. No process-name discovery/kill. A clean stop asks only this process to quit
// (Quit); the kill fallback ends this process and its children.
public sealed class DirectServerProcess : IServerProcess
{
    private readonly Process _process;
    private readonly Task _stdout, _stderr;
    private readonly string _logPrefix;
    private readonly string[] _gameLogs;
    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    /// <summary>How <see cref="StopCleanly"/> asks the process to quit: <see cref="QuitRequest.Interrupt"/> for a dedicated server (the default), <see cref="QuitRequest.CloseWindow"/> for a game client.</summary>
    public QuitRequest Quit { get; init; } = QuitRequest.Interrupt;
    public DirectServerProcess(ProcessStartInfo start, string logPrefix, params string[] gameLogs)
    {
        _logPrefix = logPrefix; _gameLogs = gameLogs;
        start.UseShellExecute = false; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        _process = Process.Start(start) ?? throw new IOException("Could not start the owned process.");
        _stdout = Capture(_process.StandardOutput, logPrefix + ".stdout.log");
        _stderr = Capture(_process.StandardError, logPrefix + ".stderr.log");
    }
    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        await _process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        return _process.ExitCode;
    }
    // Each output is copied on its own thread, not the thread pool: on Windows a redirected pipe has no overlapped I/O, so an
    // async copy holds a pool thread in a blocking read for the process's whole life, and its last read and continuation wait
    // for a free pool thread after the process ends. In a busy test run (many tests blocking pool threads at once) that wait
    // outlasted the 5 s stop bound (#121). A dedicated thread's read returns as soon as the pipe's last writer closes.
    private static Task Capture(StreamReader reader, string path)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) reader.BaseStream.CopyTo(file);
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true, Name = "capture " + Path.GetFileName(path) }.Start();
        return done.Task;
    }
    public void Stop(TimeSpan timeout)
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        if (!_process.WaitForExit((int)timeout.TotalMilliseconds)) throw new TimeoutException("The owned process did not stop.");
        Keep(timeout);
    }
    /// <summary>
    /// Asks the process to quit as <see cref="Quit"/> says and waits up to <paramref name="quit"/>; kills it only if it is
    /// still running then (or could not be asked). Keeps the logs either way.
    /// </summary>
    public ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill)
    {
        var clock = Stopwatch.StartNew();
        if (_process.HasExited) { Keep(kill); return new(StopOutcome.AlreadyExited, _process.ExitCode, TimeSpan.Zero, "not asked: it had exited"); }
        if (quit <= TimeSpan.Zero)
        {
            Stop(kill);
            return new(StopOutcome.Killed, _process.ExitCode, clock.Elapsed, "not asked to quit");
        }
        var (sent, request) = ProcessQuit.Request(_process, Quit);
        if (sent && _process.WaitForExit((int)quit.TotalMilliseconds))
        {
            _process.WaitForExit(); // Also completes the exit code.
            Keep(kill);
            return new(StopOutcome.Clean, _process.ExitCode, clock.Elapsed, request);
        }
        string why = sent ? $"{request}; no exit within {WaitText.Seconds(quit)}" : request;
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        if (!_process.WaitForExit((int)kill.TotalMilliseconds)) throw new TimeoutException("The owned process did not stop after it was killed.");
        Keep(kill);
        return new(StopOutcome.Killed, _process.ExitCode, clock.Elapsed, why);
    }
    // The process output and the game's logs as they were when it stopped (the next boot overwrites the game's).
    private void Keep(TimeSpan timeout)
    {
        if (!Task.WaitAll([_stdout, _stderr], timeout))
        {
            // The process has exited, so a capture still open means another process holds its pipe: one the owned process
            // started outside its own tree, or one that inherited the handle.
            var open = new[] { ("stdout", _stdout), ("stderr", _stderr) }.Where(capture => !capture.Item2.IsCompleted).Select(capture => capture.Item1);
            throw new TimeoutException($"Process log capture did not finish within {WaitText.Seconds(timeout)} after process {_process.Id} exited (exit {_process.ExitCode}): " +
                $"{string.Join(" and ", open)} still open, so another process still holds that pipe. The copy so far is in {_logPrefix}.std*.log.");
        }
        for (int i = 0; i < _gameLogs.Length; i++)
        {
            string target = _logPrefix + ".game-" + i + ".log";
            if (File.Exists(_gameLogs[i])) File.Copy(_gameLogs[i], target, overwrite: true);
            else File.WriteAllText(target + ".absent", "Game did not create this log: " + _gameLogs[i]);
        }
    }
    public void Dispose() => _process.Dispose();
}

// Local evidence only. Review before publishing: world names/positions and IDs may appear.
public sealed class RecordingTransport : IGameTransport
{
    private readonly IGameTransport _inner;
    private readonly StreamWriter _writer;
    public RecordingTransport(IGameTransport inner, string file) : this(inner, file, null) { }
    /// <summary>
    /// With <paramref name="environment"/> (for example <see cref="EnvironmentPinning.NotPinned"/>), the record's first line
    /// is <c>{"utc":...,"environment":...}</c>, before any command.
    /// </summary>
    public RecordingTransport(IGameTransport inner, string file, string? environment)
    {
        _inner = inner;
        try
        {
            _writer = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            if (environment != null) _writer.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, environment }));
        }
        catch { _writer?.Dispose(); inner.Dispose(); throw; }
    }
    public CommandResult Execute(string command, TimeSpan timeout)
    {
        try
        {
            var reply = _inner.Execute(command, timeout);
            _writer.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, command, reply }));
            return reply;
        }
        catch (Exception error)
        {
            _writer.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, command, error = error.Message })); throw;
        }
    }
    public void Dispose() { try { _inner.Dispose(); } finally { _writer.Dispose(); } }
}
