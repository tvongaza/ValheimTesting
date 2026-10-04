using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>Low-level transport. Use GameActor for strictly pinned test actions and observations.</summary>
public interface IGameTransport : IDisposable
{
    CommandResult Execute(string command, TimeSpan timeout);
}
/// <summary>A separate command connection that can be closed on cancellation without losing the actor's control connection.</summary>
public interface ICancellableGameTransport : IGameTransport
{
    Task<CommandResult> ExecuteCancelableAsync(string expectations, string command, TimeSpan timeout, CancellationToken cancellation);
}
public sealed class CliTransport : ICancellableGameTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly ValheimClient _client;
    public CliTransport(string host, int port)
    {
        _host = host; _port = port;
        _client = new ValheimClient(host, port);
        if (!_client.Connect()) { _client.Dispose(); throw new IOException("CLI connection failed."); }
        if (!_client.SupportsCompletion) { _client.Dispose(); throw new IOException("Tests require command completion support."); }
    }
    public CommandResult Execute(string command, TimeSpan timeout)
    {
        _client.CommandTimeout = timeout;
        return _client.ExecuteCommand(command);
    }
    public Task<CommandResult> ExecuteCancelableAsync(string expectations, string command, TimeSpan timeout, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Use a second socket so closing an in-flight request cannot strand the control connection needed for cleanup.
        return Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            using var request = new ValheimClient(_host, _port);
            if (!request.Connect()) throw new IOException("CLI command connection failed.");
            if (!request.SupportsCompletion) throw new IOException("Tests require command completion support.");
            request.CommandTimeout = timeout;
            using var stop = cancellation.Register(request.Disconnect);
            cancellation.ThrowIfCancellationRequested();
            var pins = request.ExecuteCommand(expectations);
            cancellation.ThrowIfCancellationRequested();
            if (!pins.Ok || !PlanExpectations.Judge(new ExpectationSource { From = "actor", Strict = true }, pins.Output).Held)
                throw new InvalidOperationException("Game did not confirm the strict environment pins on the command connection.");
            var result = request.ExecuteCommand(command);
            cancellation.ThrowIfCancellationRequested();
            return result;
        }, CancellationToken.None);
    }
    public void Dispose() => _client.Dispose(); // Attachment never owns the game's process.
}
public sealed record Capability(string Path, string Instance, bool ReadOnly, int SchemaVersion);

/// <summary>
/// An actor for ValheimCLI actions and observations. After strict <see cref="VerifyEnvironment"/>, it checks the
/// expected world and plugins before every command.
/// </summary>
/// <example>
/// Load reviewed, strict expectations, then discover the adapter command before reading its state:
/// <code>
/// string pins = StrictExpectations.Load(pinsFile);
/// using var actor = new GameActor("session", new CliTransport("127.0.0.1", 5577));
/// actor.VerifyEnvironment(pins);
/// var capability = actor.RequireCapability("mymod.testing/session");
/// bool complete = actor.Observe(capability).Complete;
/// </code>
/// The pin file comes from the test plan, not from the running game. After a world or plugin transition, verify the new
/// expected pins and rediscover capabilities before another action. See the compiling
/// <see href="https://github.com/tvongaza/ValheimTesting/blob/main/examples/SessionControl/Program.cs">pinning example</see>
/// and <see href="https://github.com/tvongaza/ValheimTesting/blob/main/examples/FullLifecycle/MyMod.SystemTests/ServerFixture.cs">adapter observation</see>.
/// </example>
public sealed class GameActor : IDisposable
{
    private readonly IGameTransport _transport;
    private readonly object _sync = new();
    private bool _verified, _pinned = true;
    private string _expectations = "";
    public string Name { get; }
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// False once the actor was explicitly unpinned (<see cref="VerifyEnvironment"/> with <see cref="EnvironmentPinning.None"/>)
    /// and not verified with pins since.
    /// </summary>
    public bool Pinned { get { lock (_sync) return _pinned; } }
    public GameActor(string name, IGameTransport transport)
    { Name = name; _transport = transport; }
    /// <summary>
    /// Checks the strict pins now and before every command. <see cref="EnvironmentPinning.None"/> instead of a
    /// <c>cli_expect</c> command is the explicit opt-out: the actor then runs commands without any pin check, prints
    /// <see cref="EnvironmentPinning.Warning"/> when it becomes unpinned, and reports <see cref="Pinned"/> false until it is
    /// verified with pins again. A transition still needs this call again, pinned or not.
    /// </summary>
    public void VerifyEnvironment(string expectationCommand)
    {
        lock (_sync)
        {
            _verified = false;
            if (expectationCommand == EnvironmentPinning.None)
            {
                if (_pinned) EnvironmentPinning.Warn($"game actor \"{Name}\"");
                _pinned = false; _expectations = ""; _verified = true;
                return;
            }
            _expectations = StrictExpectations.Normalize(expectationCommand);
            _pinned = true;
            CheckEnvironment();
            _verified = true;
        }
    }
    /// <summary>After an attempted world transition, require fresh explicit environment pins.</summary>
    public void InvalidateEnvironment() { lock (_sync) _verified = false; }
    /// <summary>
    /// Every command rechecks strict pins. By default the reply must be accepted (<see cref="GameReply.RequireAccepted"/>):
    /// the transport completed and the game did not refuse it. <paramref name="requireAccepted"/> false permits inspecting
    /// an expected refusal, never a pin mismatch.
    /// </summary>
    public GameReply Execute(string command, bool requireAccepted = true)
    {
        lock (_sync)
        {
            if (!_verified) throw new InvalidOperationException("Verify the actor's world and plugin expectations before using it.");
            if (command.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("One command per call.");
            if (_pinned)
            {
                _verified = false;
                try { CheckEnvironment(); _verified = true; }
                catch (InvalidOperationException error) when (IsUnstartedCommandTimeout(error))
                {
                    // The CLI explicitly says the pin check never ran. Permit a later read-only retry,
                    // which will issue a fresh pin check before observing anything.
                    _verified = true;
                    throw;
                }
            }
            var reply = new GameReply(command, _transport.Execute(command, CommandTimeout));
            return requireAccepted ? reply.RequireAccepted() : reply;
        }
    }
    private void CheckEnvironment()
    {
        CommandResult response = _transport.Execute(_expectations, CommandTimeout);
        RequireSuccess(response);
        if (!PlanExpectations.Judge(new ExpectationSource { From = "actor", Strict = true }, response.Output).Held)
            throw new InvalidOperationException("Game did not confirm the strict environment pins.");
    }
    /// <summary>
    /// Bounded fallback for a reload with no observable event: rechecks the pins every 200 ms. Prefer the overload that
    /// waits for the reload's own event.
    /// </summary>
    public Task WaitForEnvironment(string expectations, TimeSpan timeout, CancellationToken cancellation = default) =>
        WaitForEnvironment(expectations, timeout, (left, token) => Task.Delay(left < ReloadFallback ? left : ReloadFallback, token), cancellation);
    private static readonly TimeSpan ReloadFallback = TimeSpan.FromMilliseconds(200);
    /// <summary>
    /// Wait only for an explicitly specified reload's pins; never retry a gameplay action or relax the expected set.
    /// Checks now, then again each time <paramref name="changed"/> completes. It receives the remaining time and should
    /// await the event that follows the change, for example the reloaded plugin's load line through a <see cref="LogWait"/>:
    /// <c>(left, token) =&gt; log.WaitAsync(loadLine, left, cancellation: token)</c>. A failure it throws ends the wait.
    /// </summary>
    public async Task WaitForEnvironment(string expectations, TimeSpan timeout, Func<TimeSpan, CancellationToken, Task> changed, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(changed);
        InvalidateEnvironment();
        if (expectations == EnvironmentPinning.None) { VerifyEnvironment(expectations); return; } // Nothing to wait for.
        expectations = StrictExpectations.Normalize(expectations);
        WaitText.RequireTimeout(timeout);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var prior = CommandTimeout;
        string last = "pins not checked";
        try
        {
            while (clock.Elapsed < timeout)
            {
                cancellation.ThrowIfCancellationRequested();
                CommandTimeout = timeout - clock.Elapsed;
                if (CommandTimeout > prior) CommandTimeout = prior;
                try { VerifyEnvironment(expectations); return; }
                catch (InvalidOperationException error) { last = error.Message; /* Only explicit expectation checks are retried during reload. */ }
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(remaining);
                // The event's own deadline is ours: its expiry is this wait's expiry.
                try { await changed(remaining, deadline.Token).WaitAsync(remaining, cancellation).ConfigureAwait(false); }
                catch (Exception error) when (!cancellation.IsCancellationRequested && error is TimeoutException or OperationCanceledException) { break; }
            }
            throw new WaitTimeoutException("the expected strict environment (no gameplay action was issued)", clock.Elapsed, last);
        }
        finally { CommandTimeout = prior; }
    }
    private static void RequireSuccess(CommandResult result)
    { if (!result.Ok) throw new InvalidOperationException($"{result.ErrorCode}: {result.Message}"); }
    internal static bool IsUnstartedCommandTimeout(InvalidOperationException error) =>
        error.Message.StartsWith("command_failed: ERROR: code=command_timeout ", StringComparison.Ordinal) &&
        error.Message.Contains("had not started and will not run", StringComparison.Ordinal);
    public Capability RequireCapability(string path, int schemaVersion = 1)
    {
        var reply = CliCapabilities.ListingReply(this); // An old ValheimCLI without cli_extensions is named as one.
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
        throw new InvalidOperationException("Required capability is absent: " + path + ". " + CliCapabilities.Provider(path.Split('/')[0]));
    }
    public JsonElement Invoke(Capability command, params string[] arguments)
    {
        if (arguments.Any(x => x.Any(char.IsWhiteSpace) || x.Length == 0)) throw new ArgumentException("Extension arguments must be single tokens in preview 1.");
        // ParseInvocation checks success itself, after reading a failed extension's own code and message.
        var reply = Execute("cli_extension " + command.Path + (arguments.Length == 0 ? "" : " " + string.Join(" ", arguments)), requireAccepted: false);
        return ParseInvocation(command, reply.Result);
    }
    /// <summary>
    /// Issue one extension mutation on a fresh, strictly pinned CLI connection. Cancellation closes that socket, which
    /// asks the game to abandon the request; the actor's original connection remains available for cleanup commands.
    /// This requires a transport that can interrupt its own in-flight command.
    /// </summary>
    public async Task<JsonElement> InvokeCancelableAsync(Capability command, CancellationToken cancellation, params string[] arguments)
    {
        if (arguments.Any(x => x.Any(char.IsWhiteSpace) || x.Length == 0)) throw new ArgumentException("Extension arguments must be single tokens in preview 1.");
        if (_transport is not ICancellableGameTransport transport)
            throw new NotSupportedException("This transport cannot cancel an in-flight extension command.");
        string expectations;
        TimeSpan timeout;
        lock (_sync)
        {
            if (!_verified || !_pinned) throw new InvalidOperationException("Verify strict world and plugin pins before issuing a cancellable command.");
            expectations = _expectations;
            timeout = CommandTimeout;
        }
        string text = "cli_extension " + command.Path + (arguments.Length == 0 ? "" : " " + string.Join(" ", arguments));
        var reply = await transport.ExecuteCancelableAsync(expectations, text, timeout, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return ParseInvocation(command, reply);
    }
    private static JsonElement ParseInvocation(Capability command, CommandResult reply)
    {
        // A failed extension's own code and message are in its structured result; ValheimCLI's closing ERROR line only
        // says "see structured result", so read that first.
        if (!reply.Ok && reply.Output.Count(x => x.StartsWith("EXTENSION_RESULT ", StringComparison.Ordinal)) == 1)
        {
            using var failed = ParseLine(reply, "EXTENSION_RESULT ");
            if (failed.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) throw ExtensionFailed(command, failed.RootElement);
        }
        RequireSuccess(reply);
        using var document = ParseLine(reply, "EXTENSION_RESULT "); var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean()) throw ExtensionFailed(command, root);
        if (root.GetProperty("schemaVersion").GetInt32() != command.SchemaVersion || root.GetProperty("instance").GetString() != command.Instance ||
            root.GetProperty("extension").GetString() != command.Path.Split('/')[0]) throw new InvalidOperationException("Extension changed; explicitly rediscover capabilities after reload.");
        return root.GetProperty("data").Clone();
    }
    private static InvalidOperationException ExtensionFailed(Capability command, JsonElement result)
    {
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new InvalidOperationException($"{command.Path} failed: {Text(result, "code") ?? "no code"}: {Text(result, "message") ?? "no message"}");
    }
    public Observation Observe(Capability command, params string[] arguments)
    {
        if (!command.ReadOnly) throw new InvalidOperationException("Polling requires a read-only capability; issue mutations once.");
        var data = Invoke(command, arguments);
        return new Observation(data.GetProperty("source").GetString()!, data.GetProperty("complete").GetBoolean(), data);
    }
    /// <summary>Observes and requires a complete observation from <paramref name="source"/>.</summary>
    public Observation ObserveComplete(Capability command, string source, params string[] arguments)
    {
        var observation = Observe(command, arguments);
        observation.RequireComplete(source);
        return observation;
    }
    /// <summary>
    /// Saves the world with <c>cli_save</c> and requires ValheimCLI's confirmation line (<c>OK: SAVE ...</c>), which it
    /// returns. Anything else is a failure: never restart or compare persistence after an unconfirmed save.
    /// </summary>
    public string SaveConfirmed()
    {
        return Execute("cli_save").RequireLine("OK: SAVE ", "No confirmed world save");
    }
    /// <summary>The one structured line starting with <paramref name="prefix"/>, parsed; none or several is a failure.</summary>
    public static JsonDocument ParseLine(GameReply reply, string prefix) => ParseLine(reply.Result, prefix);
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
