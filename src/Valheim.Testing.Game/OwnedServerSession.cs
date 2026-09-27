using System.Diagnostics;
using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

// The seam lets fast tests drive the actual restart/identity rules without Unity.
public interface IServerProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    void Stop(TimeSpan timeout);
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
    public OwnedServerSession(Func<string, IServerProcess> launch, Func<IGameTransport> connect,
        string saveRoot, string expectations, string sessionCapability, TimeSpan startup, TimeSpan command, TimeSpan? poll = null, CancellationToken cancellation = default)
    {
        var parts = sessionCapability.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.' && ch != '-' && ch != '_')))
            throw new ArgumentException("Use one namespaced session capability, without arguments.");
        _sessionCapability = sessionCapability; _extension = parts[0];
        _cancellation = cancellation; _launch = launch; _connect = connect; _saveRoot = Path.GetFullPath(saveRoot); _expectations = expectations;
        _startup = startup; _command = command; _poll = poll ?? TimeSpan.FromMilliseconds(500);
        if (startup <= TimeSpan.Zero || command <= TimeSpan.Zero || _poll < TimeSpan.Zero) throw new ArgumentException("Invalid session deadlines.");
    }
    public GameActor Start()
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_process != null) throw new InvalidOperationException("Stop the previous owned process before starting another.");
        string token = Guid.NewGuid().ToString("N");
        _process = _launch(token); StartedProcesses.Add(_process.Id);
        var clock = Stopwatch.StartNew(); string last = "No CLI connection";
        // Retry connection and incomplete startup observations only. Never retry a mutation.
        while (clock.Elapsed < _startup)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (_process.HasExited) throw new InvalidOperationException("Owned server exited during startup.");
            IGameTransport? transport = null;
            try
            {
                transport = _connect();
                var timeout = _startup - clock.Elapsed;
                if (timeout > _command) timeout = _command;
                if (timeout <= TimeSpan.Zero) break;
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
                    bool ready = CheckIdentity(document.RootElement, token, _process.Id, _saveRoot, _extension);
                    if (ready)
                    {
                        var actor = new GameActor("owned-server", transport) { CommandTimeout = _command };
                        transport = null;
                        try
                        {
                            actor.VerifyEnvironment(_expectations);
                            if (_process.HasExited || clock.Elapsed >= _startup) throw new InvalidOperationException("Server exited or startup deadline expired during verification.");
                            _actor = actor; return actor;
                        }
                        catch { actor.Dispose(); throw; }
                    }
                    last = "Owned server has not completed world/network loading";
                }
            }
            catch (IOException) { last = "CLI transport unavailable"; }
            catch (System.Net.Sockets.SocketException) { last = "CLI socket unavailable"; }
            finally { transport?.Dispose(); }
            var remaining = _startup - clock.Elapsed;
            if (remaining > TimeSpan.Zero && _poll > TimeSpan.Zero) Thread.Sleep(remaining < _poll ? remaining : _poll);
        }
        throw new TimeoutException("Server startup deadline: " + last);
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
