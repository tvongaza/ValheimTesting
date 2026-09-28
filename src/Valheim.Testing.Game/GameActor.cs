using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>Low-level transport. Use GameActor for strictly pinned test actions and observations.</summary>
public interface IGameTransport : IDisposable
{
    CommandResult Execute(string command, TimeSpan timeout);
}
public sealed class CliTransport : IGameTransport
{
    private readonly ValheimClient _client;
    public CliTransport(string host, int port)
    {
        _client = new ValheimClient(host, port);
        if (!_client.Connect()) { _client.Dispose(); throw new IOException("CLI connection failed."); }
        if (!_client.SupportsCompletion) { _client.Dispose(); throw new IOException("Tests require command completion support."); }
    }
    public CommandResult Execute(string command, TimeSpan timeout)
    {
        _client.CommandTimeout = timeout;
        return _client.ExecuteCommand(command);
    }
    public void Dispose() => _client.Dispose(); // Attachment never owns the game's process.
}
public sealed record Capability(string Path, string Instance, bool ReadOnly, int SchemaVersion);

public sealed class GameActor : IDisposable
{
    private readonly IGameTransport _transport;
    private readonly object _sync = new();
    private bool _verified;
    private string _expectations = "";
    public string Name { get; }
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public GameActor(string name, IGameTransport transport)
    { Name = name; _transport = transport; }
    public void VerifyEnvironment(string expectationCommand)
    {
        lock (_sync)
        {
            _verified = false;
            _expectations = StrictExpectations.Normalize(expectationCommand);
            CheckEnvironment();
            _verified = true;
        }
    }
    /// <summary>After an attempted world transition, require fresh explicit environment pins.</summary>
    public void InvalidateEnvironment() { lock (_sync) _verified = false; }
    /// <summary>Every command rechecks strict pins. False permits inspecting an expected command refusal, never a pin mismatch.</summary>
    public CommandResult Execute(string command, bool requireSuccess = true)
    {
        lock (_sync)
        {
            if (!_verified) throw new InvalidOperationException("Verify the actor's world and plugin expectations before using it.");
            if (command.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("One command per call.");
            _verified = false;
            CheckEnvironment();
            _verified = true;
            CommandResult result = _transport.Execute(command, CommandTimeout);
            if (requireSuccess) RequireSuccess(result);
            return result;
        }
    }
    private void CheckEnvironment()
    {
        CommandResult response = _transport.Execute(_expectations, CommandTimeout);
        RequireSuccess(response);
        if (!PlanExpectations.Judge(new ExpectationSource { From = "actor", Strict = true }, response.Output).Held)
            throw new InvalidOperationException("Game did not confirm the strict environment pins.");
    }
    /// <summary>Wait only for an explicitly specified reload's pins; never retry a gameplay action or relax the expected set.</summary>
    public async Task WaitForEnvironment(string expectations, TimeSpan timeout, CancellationToken cancellation = default)
    {
        InvalidateEnvironment();
        expectations = StrictExpectations.Normalize(expectations);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var prior = CommandTimeout;
        try
        {
            while (clock.Elapsed < timeout)
            {
                cancellation.ThrowIfCancellationRequested();
                CommandTimeout = timeout - clock.Elapsed;
                if (CommandTimeout > prior) CommandTimeout = prior;
                try { VerifyEnvironment(expectations); return; }
                catch (InvalidOperationException) { /* Only explicit expectation checks are retried during reload. */ }
                var remaining = timeout - clock.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200), cancellation);
            }
            throw new TimeoutException("Expected strict environment did not appear; no gameplay action was issued.");
        }
        finally { CommandTimeout = prior; }
    }
    private static void RequireSuccess(CommandResult result)
    { if (!result.Ok) throw new InvalidOperationException($"{result.ErrorCode}: {result.Message}"); }
    public Capability RequireCapability(string path, int schemaVersion = 1)
    {
        var reply = Execute("cli_extensions");
        using var doc = ParseLine(reply, "EXTENSIONS ");
        if (doc.RootElement.GetProperty("apiVersion").GetInt32() != 1) throw new InvalidOperationException("Unsupported extension API.");
        foreach (var extension in doc.RootElement.GetProperty("extensions").EnumerateArray())
        {
            if (extension.GetProperty("closing").GetBoolean()) continue;
            foreach (var command in extension.GetProperty("commands").EnumerateArray())
                if (extension.GetProperty("id").GetString() + "/" + command.GetProperty("name").GetString() == path)
                {
                    if (command.GetProperty("resultVersion").GetInt32() != schemaVersion) throw new InvalidOperationException("Unsupported command result schema.");
                    return new Capability(path, extension.GetProperty("instance").GetString()!, command.GetProperty("readOnly").GetBoolean(), schemaVersion);
                }
        }
        throw new InvalidOperationException("Required capability is absent: " + path);
    }
    public JsonElement Invoke(Capability command, params string[] arguments)
    {
        if (arguments.Any(x => x.Any(char.IsWhiteSpace) || x.Length == 0)) throw new ArgumentException("Extension arguments must be single tokens in preview 1.");
        var reply = Execute("cli_extension " + command.Path + (arguments.Length == 0 ? "" : " " + string.Join(" ", arguments)));
        using var document = ParseLine(reply, "EXTENSION_RESULT "); var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Extension returned an error.");
        if (root.GetProperty("schemaVersion").GetInt32() != command.SchemaVersion || root.GetProperty("instance").GetString() != command.Instance ||
            root.GetProperty("extension").GetString() != command.Path.Split('/')[0]) throw new InvalidOperationException("Extension changed; explicitly rediscover capabilities after reload.");
        return root.GetProperty("data").Clone();
    }
    public Observation Observe(Capability command, params string[] arguments)
    {
        if (!command.ReadOnly) throw new InvalidOperationException("Polling requires a read-only capability; issue mutations once.");
        var data = Invoke(command, arguments);
        return new Observation(data.GetProperty("source").GetString()!, data.GetProperty("complete").GetBoolean(), data);
    }
    public static JsonDocument ParseLine(CommandResult reply, string prefix)
    {
        string[] lines = reply.Output.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1) throw new InvalidOperationException("Expected exactly one complete structured response.");
        return JsonDocument.Parse(lines[0][prefix.Length..]);
    }
    public void Dispose() { lock (_sync) { _verified = false; _transport.Dispose(); } }
}
public sealed record Observation(string Source, bool Complete, JsonElement Data)
{
    public void RequireComplete(string source)
    {
        if (!Complete || Source != source) throw new InvalidOperationException("Incomplete observation or wrong observation layer.");
    }
}
