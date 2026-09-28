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
    void Stop(TimeSpan timeout);
}

/// <summary>
/// What an owned server's startup waits on instead of retrying connections. The process exit is always watched.
/// Without <see cref="CliLog"/>, connecting falls back to bounded retries at the session's poll interval.
/// </summary>
public sealed class StartupEvents
{
    /// <summary>ValheimCLI's line once its command server accepts connections.</summary>
    public static readonly Regex CliListening = new(@"Command server listening on \S+:\d+", RegexOptions.CultureInvariant);
    /// <summary>The log ValheimCLI writes to, normally the runtime's BepInEx/LogOutput.log. No connection is tried before <see cref="Listening"/> appears in it.</summary>
    public string? CliLog { get; init; }
    public Regex Listening { get; init; } = CliListening;
    /// <summary>Lines in <see cref="CliLog"/> that end startup at once, for example a required plugin's load error.</summary>
    public IReadOnlyList<Regex> Failures { get; init; } = [];
    /// <summary>
    /// Opens a connection used only for state pushes, for example <c>() =&gt; StateWait.Connect(host, port)</c>.
    /// Startup then waits for one of <see cref="ReadyStates"/> before the first readiness probe.
    /// </summary>
    public Func<StateWait>? States { get; init; }
    public IReadOnlyList<string> ReadyStates { get; init; } = StateWait.WorldLoaded;
    public IReadOnlyList<string> FailureStates { get; init; } = [];
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
    /// <summary>Events startup waits on; null keeps bounded connection retries. The process exit is watched either way.</summary>
    public StartupEvents? Events { get; init; }
    public OwnedServerSession(Func<string, IServerProcess> launch, Func<IGameTransport> connect,
        string saveRoot, string expectations, string sessionCapability, TimeSpan startup, TimeSpan command, TimeSpan? poll = null, CancellationToken cancellation = default)
    {
        var parts = sessionCapability.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.' && ch != '-' && ch != '_')))
            throw new ArgumentException("Use one namespaced session capability, without arguments.");
        _sessionCapability = sessionCapability; _extension = parts[0];
        _cancellation = cancellation; _launch = launch; _connect = connect; _saveRoot = Path.GetFullPath(saveRoot); _expectations = StrictExpectations.Normalize(expectations);
        _startup = startup; _command = command; _poll = poll ?? TimeSpan.FromMilliseconds(500);
        if (startup <= TimeSpan.Zero || command <= TimeSpan.Zero || _poll < TimeSpan.Zero) throw new ArgumentException("Invalid session deadlines.");
    }
    public GameActor Start() => StartAsync().GetAwaiter().GetResult();
    /// <summary>
    /// Launches one owned server and returns its strictly pinned actor. Every stage races the process exit, which ends
    /// startup at once with the exit code and the last log line. With <see cref="Events"/>, the first connection waits for
    /// ValheimCLI's listening line and, optionally, a world-loaded state push. The adapter's own readiness has no event:
    /// an unregistered or incomplete session observation is re-probed, read-only, every poll interval until the deadline.
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
        async Task<Exception> Exited(string stage) => new WaitFailedException(stage, "owned server exited with code " + await exited.ConfigureAwait(false),
            clock.Elapsed, log == null ? "no startup log configured" : log.Refresh());
        async Task UntilExit(string stage, Func<TimeSpan, CancellationToken, Task> wait)
        {
            using var abandon = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var waiting = wait(Left(stage), abandon.Token);
            if (await Task.WhenAny(waiting, exited).ConfigureAwait(false) == waiting) { await waiting.ConfigureAwait(false); return; }
            abandon.Cancel();
            try { await waiting.ConfigureAwait(false); } catch { /* Abandoned: the exit is the result. */ }
            throw await Exited(stage).ConfigureAwait(false);
        }
        try
        {
            if (log != null)
                await UntilExit("ValheimCLI listening", (left, ct) => log.WaitAsync(events!.Listening, left, events.Failures, ct)).ConfigureAwait(false);
            if (events?.States is { } openStates)
            {
                using var states = openStates();
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
                    transport = _connect();
                    var timeout = Left(stage);
                    if (timeout > _command) timeout = _command;
                    // Bootstrap exception: this adapter capability MUST be read-only. World pins cannot
                    // hold before loading completes. Prove our token/PID/save root first, then strict-pin
                    // before returning an actor or issuing any gameplay action.
                    var reply = transport.Execute("cli_extension " + _sessionCapability, timeout);
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
                            var actor = new GameActor("owned-server", transport) { CommandTimeout = _command };
                            transport = null;
                            try
                            {
                                actor.VerifyEnvironment(_expectations);
                                if (exited.IsCompleted || clock.Elapsed >= _startup) throw new InvalidOperationException("Server exited or startup deadline expired during verification.");
                                _actor = actor; return actor;
                            }
                            catch { actor.Dispose(); throw; }
                        }
                        last = "Owned server has not completed world/network loading";
                    }
                }
                catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException)
                {
                    // After the listening line a failed connection is a fault, not a startup race.
                    if (log != null) throw new WaitFailedException(stage, "ValheimCLI announced its listener but the connection failed: " + error.Message, clock.Elapsed, log.Refresh());
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
                _process.Stop(TimeSpan.FromSeconds(15));
                if (!_process.HasExited) throw new InvalidOperationException("Owned process did not exit; restart refused.");
                _process.Dispose(); _process = null;
            }
        }
    }
    public void Dispose() => Stop();
}

// Starts one direct executable; launch scripts must exec/wait, never detach a child.
// The PID handshake refuses a daemonized server. No process-name discovery/kill.
public sealed class DirectServerProcess : IServerProcess
{
    private readonly Process _process;
    private readonly Task _stdout, _stderr;
    private readonly string _logPrefix;
    private readonly string[] _gameLogs;
    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public DirectServerProcess(ProcessStartInfo start, string logPrefix, params string[] gameLogs)
    {
        _logPrefix = logPrefix; _gameLogs = gameLogs;
        start.UseShellExecute = false; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        _process = Process.Start(start) ?? throw new IOException("Could not start owned server.");
        _stdout = Capture(_process.StandardOutput, logPrefix + ".stdout.log");
        _stderr = Capture(_process.StandardError, logPrefix + ".stderr.log");
    }
    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        await _process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        return _process.ExitCode;
    }
    private static async Task Capture(StreamReader reader, string path)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await reader.BaseStream.CopyToAsync(file);
    }
    public void Stop(TimeSpan timeout)
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        if (!_process.WaitForExit((int)timeout.TotalMilliseconds)) throw new TimeoutException("Owned server did not stop.");
        if (!Task.WaitAll([_stdout, _stderr], timeout)) throw new TimeoutException("Process log capture did not finish.");
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
    public RecordingTransport(IGameTransport inner, string file)
    {
        _inner = inner;
        try { _writer = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true }; }
        catch { inner.Dispose(); throw; }
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
