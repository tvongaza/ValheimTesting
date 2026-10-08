using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>Low-level transport. Use GameActor for strictly pinned test actions and observations.</summary>
public interface IGameTransport : IDisposable
{
    CommandResult Execute(string command, TimeSpan timeout);
}
/// <summary>A STATUS reading made on a separate socket, independent of a queued game-thread command.</summary>
public interface IGameThreadStatusTransport
{
    IReadOnlyDictionary<string, string> ReadStatus();
}
public sealed class CliTransport : IGameTransport, IGameThreadStatusTransport
{
    private readonly ValheimClient _client;
    private readonly string _host;
    private readonly int _port;
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
    public IReadOnlyDictionary<string, string> ReadStatus()
    {
        using var probe = new ValheimClient(_host, _port);
        if (!probe.Connect()) throw new IOException("ValheimCLI did not answer a separate STATUS connection.");
        var fields = probe.GetStatusDetails(fallbackToState: false);
        if (!probe.StatusLineRead) throw new IOException("ValheimCLI did not answer STATUS; the state-only fallback cannot prove game-thread liveness.");
        return fields;
    }
    public void Dispose() => _client.Dispose(); // Attachment never owns the game's process.
}
[ResultShape]
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
/// <see href="https://github.com/tvongaza/ValheimTesting/blob/main/examples/ObserveCheck/ObserveCheck.cs">pinning example</see>
/// and <see href="https://github.com/tvongaza/ValheimTesting/blob/main/tests/Valheim.Testing.NativeAcceptance/ServerFixture.cs">adapter observation</see>.
/// </example>
public sealed class GameActor : IDisposable
{
    private readonly IGameTransport _transport;
    private readonly object _sync = new();
    private bool _verified, _pinned = true;
    private string _expectations = "";
    private BusyReadScope? _busyReads;
    private long _longestMainThreadIdleMs;
    private string? _lastBusyNote;
    /// <summary>The largest observed frame age during a busy world-entry wait, or zero if none was observed.</summary>
    public long LongestMainThreadIdleMs => _longestMainThreadIdleMs;
    /// <summary>The last busy note the game published during that wait, if any.</summary>
    public string? BusyNote => _lastBusyNote;
    public string Name { get; }
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// False once the actor was explicitly unpinned (<see cref="VerifyEnvironment"/> with <see cref="EnvironmentPinning.None"/>)
    /// and not verified with pins since.
    /// </summary>
    public bool Pinned { get { lock (_sync) return _pinned; } }
    public GameActor(string name, IGameTransport transport)
    { Name = name; _transport = transport; }

    private sealed class BusyReadScope(GameActor actor, TimeSpan timeout, BusyReadScope? previous, CancellationToken cancellation) : IDisposable
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        public TimeSpan Left => timeout - _clock.Elapsed;
        public CancellationToken Cancellation => cancellation;
        public bool ResponsiveRetryUsed { get; set; }
        public void Dispose() => actor._busyReads = previous;
    }

    /// <summary>Bound safe read retries by a world-entry deadline; mutating commands are never retried.</summary>
    internal IDisposable BusyReadRetries(TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (_busyReads is { } previous && previous.Left < timeout)
            timeout = previous.Left;
        if (timeout <= TimeSpan.Zero)
            throw new WaitTimeoutException("the game-thread read", TimeSpan.Zero, "The world-entry deadline has passed.");
        var scope = new BusyReadScope(this, timeout, _busyReads, cancellation);
        _busyReads = scope;
        return scope;
    }

    // Session waits and their read retries must use the same deadline. Re-applying the original timeout
    // after capability or pin round trips leaves a window of 1 ms reads after the retry budget expires.
    internal TimeSpan BusyReadTimeLeft(string target)
    {
        var left = _busyReads?.Left ?? throw new InvalidOperationException("No busy-read scope is active.");
        if (left <= TimeSpan.FromMilliseconds(25))
            throw new WaitTimeoutException(target, TimeSpan.Zero, "The world-entry deadline passed before another session read could start.");
        return left;
    }

    private CommandResult ReadOnlyCommand(string command)
    {
        while (true)
        {
            try
            {
                var reply = Execute(command, requireAccepted: false);
                if (!reply.Ok && reply.ErrorCode == "command_failed" &&
                    reply.Message?.Contains("had not started and will not run", StringComparison.Ordinal) == true)
                    reply.RequireAccepted();
                if (reply.Ok && _busyReads != null) _busyReads.ResponsiveRetryUsed = false;
                return reply.Result;
            }
            catch (InvalidOperationException error) when (_busyReads != null && IsUnstartedCommandTimeout(error))
            { WaitForResponsiveThread(error); }
        }
    }

    private TimeSpan EffectiveTimeout()
    {
        var left = _busyReads?.Left;
        // An in-flight read can consume the deadline. The caller's wait should report its last valid state;
        // no new read is started after expiry by ObservedWait.
        if (left <= TimeSpan.Zero) return TimeSpan.FromMilliseconds(1);
        return left is { } time && time < CommandTimeout ? time : CommandTimeout;
    }

    private void WaitForResponsiveThread(InvalidOperationException timeout)
    {
        var scope = _busyReads ?? throw timeout;
        if (_transport is not IGameThreadStatusTransport statusTransport)
            throw new InvalidOperationException("The read timed out before starting, but this transport cannot inspect ValheimCLI STATUS.", timeout);
        bool first = true;
        while (true)
        {
            scope.Cancellation.ThrowIfCancellationRequested();
            if (scope.Left <= TimeSpan.Zero)
                throw new WaitTimeoutException("the game thread to resume", TimeSpan.Zero,
                    $"longest idle {LongestMainThreadIdleMs} ms; busy {BusyNote ?? "none"}");
            IReadOnlyDictionary<string, string> fields;
            try { fields = statusTransport.ReadStatus(); }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            { throw new InvalidOperationException("The client or ValheimCLI stopped answering STATUS during world entry.", error); }
            if (!fields.TryGetValue("mainThreadIdleMs", out string? text) ||
                !long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long age) || age < 0)
                throw new InvalidOperationException("The pinned ValheimCLI STATUS has no valid game-thread heartbeat.");
            if (first && age < 2000)
            {
                // The game may have resumed between the command's timeout and this independent STATUS read.
                // Give that race one read-only retry; two such expiries with a responsive thread are a harness fault.
                if (scope.ResponsiveRetryUsed)
                    throw new InvalidOperationException("Harness fault: the game thread is responsive but read-only commands repeatedly expire before starting.", timeout);
                scope.ResponsiveRetryUsed = true;
                return;
            }
            if (age < 2000) return;
            if (age > _longestMainThreadIdleMs) _longestMainThreadIdleMs = age;
            if (fields.TryGetValue("busy", out string? busy) && busy is not (null or "none"))
                _lastBusyNote = Uri.UnescapeDataString(busy);
            first = false;
            var pause = scope.Left < TimeSpan.FromMilliseconds(250) ? scope.Left : TimeSpan.FromMilliseconds(250);
            if (scope.Cancellation.WaitHandle.WaitOne(pause)) scope.Cancellation.ThrowIfCancellationRequested();
        }
    }
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
                    // The CLI explicitly says the pin check never ran. A world-entry read scope can
                    // verify game-thread liveness and retry the read without relaxing the pins.
                    _verified = true;
                    throw;
                }
            }
            var reply = new GameReply(command, _transport.Execute(command, EffectiveTimeout()));
            return requireAccepted ? reply.RequireAccepted() : reply;
        }
    }
    private void CheckEnvironment()
    {
        CommandResult response;
        while (true)
        {
            try { response = _transport.Execute(_expectations, EffectiveTimeout()); RequireSuccess(response); break; }
            catch (InvalidOperationException error) when (_busyReads != null && IsUnstartedCommandTimeout(error))
            { WaitForResponsiveThread(error); }
        }
        if (!PlanExpectations.Judge(new ExpectationSource { From = "actor", Strict = true }, response.Output).Held)
            throw new InvalidOperationException("Game did not confirm the strict environment pins.");
    }
    /// <summary>
    /// Bounded fallback for a reload with no observable event: rechecks the pins every 200 ms. Prefer the overload that
    /// waits for the reload's own event.
    /// </summary>
    public Task WaitForEnvironment(string expectations, TimeSpan timeout, CancellationToken cancellation = default) =>
        WaitForEnvironment(expectations, timeout, ReloadFallback, ObservedWait.Delay, cancellation);
    private static readonly TimeSpan ReloadFallback = TimeSpan.FromMilliseconds(200), LastCheck = TimeSpan.FromMilliseconds(100);
    /// <summary>
    /// Wait only for an explicitly specified reload's pins; never retry a gameplay action or relax the expected set.
    /// Checks now, then again each time <paramref name="changed"/> completes. It receives the remaining time and should
    /// await the event that follows the change, for example the reloaded plugin's load line through a <see cref="LogWait"/>:
    /// <c>(left, token) =&gt; log.WaitAsync(loadLine, left, cancellation: token)</c>. A failure it throws ends the wait.
    /// </summary>
    public Task WaitForEnvironment(string expectations, TimeSpan timeout, Func<TimeSpan, CancellationToken, Task> changed, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(changed);
        // The event's own deadline is ours: one interval spans the whole wait, so its expiry is this wait's expiry.
        return WaitForEnvironment(expectations, timeout, timeout, ObservedWait.EndsEarlyOn(changed), cancellation);
    }
    private async Task WaitForEnvironment(string expectations, TimeSpan timeout, TimeSpan interval, Func<TimeSpan, CancellationToken, Task> pause, CancellationToken cancellation)
    {
        InvalidateEnvironment();
        if (expectations == EnvironmentPinning.None) { VerifyEnvironment(expectations); return; } // Nothing to wait for.
        expectations = StrictExpectations.Normalize(expectations);
        WaitText.RequireTimeout(timeout);
        var prior = CommandTimeout;
        string last = "pins not checked";
        int checks = 0;
        try
        {
            // The observation is the pin check (null once it holds), bounded by the time left; only explicit expectation checks are retried during reload.
            await ObservedWait.Run("the expected strict environment (no gameplay action was issued)", (left, _) =>
            {
                // After an expired event, next to no time is left: another pin check would only time out and hide the last mismatch.
                if (checks++ > 0 && left < LastCheck) return new ValueTask<string?>(last);
                CommandTimeout = left < prior ? left : prior;
                try { VerifyEnvironment(expectations); return new ValueTask<string?>((string?)null); }
                catch (InvalidOperationException error) { return new ValueTask<string?>(last = error.Message); }
            }, failure => failure == null, timeout, interval, cancellation, null, failure => failure ?? "pins hold", pause).ConfigureAwait(false);
        }
        finally { CommandTimeout = prior; }
    }
    private static void RequireSuccess(CommandResult result)
    { if (!result.Ok) throw new InvalidOperationException($"{result.ErrorCode}: {result.Message}"); }
    internal static bool IsUnstartedCommandTimeout(InvalidOperationException error) =>
        error.Message.StartsWith("command_failed: ERROR: code=command_timeout ", StringComparison.Ordinal) &&
        error.Message.Contains("had not started and will not run", StringComparison.Ordinal);
    public Capability RequireCapability(string path, int schemaVersion = 1) => RequireCapabilities([path], schemaVersion)[0];
    /// <summary><see cref="RequireCapability"/> for each of <paramref name="paths"/>, in order, from one <c>cli_extensions</c> listing.</summary>
    internal IReadOnlyList<Capability> RequireCapabilities(IReadOnlyList<string> paths, int schemaVersion = 1)
    {
        var reply = CliCapabilities.ListingReply(this); // An old ValheimCLI without cli_extensions is named as one.
        using var doc = ParseLine(reply, "EXTENSIONS ");
        if (doc.RootElement.GetProperty("apiVersion").GetInt32() != 1) throw new InvalidOperationException("Unsupported extension API.");
        return paths.Select(path =>
        {
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
        }).ToArray();
    }
    internal GameReply ReadOnlyReply(string command) => new(command, ReadOnlyCommand(command));
    public JsonElement Invoke(Capability command, params string[] arguments) => Invoke(command, null, arguments);
    /// <summary><see cref="Invoke(Capability, string[])"/>, handing the complete reply lines to <paramref name="replied"/> before judging them.</summary>
    internal JsonElement Invoke(Capability command, Action<IReadOnlyList<string>>? replied, string[] arguments)
    {
        if (arguments.Any(x => x.Any(char.IsWhiteSpace) || x.Length == 0)) throw new ArgumentException("Extension arguments must be single tokens in preview 1.");
        // ParseInvocation checks success itself, after reading a failed extension's own code and message.
        var text = "cli_extension " + command.Path + (arguments.Length == 0 ? "" : " " + string.Join(" ", arguments));
        var reply = command.ReadOnly && _busyReads != null ? ReadOnlyReply(text) : Execute(text, requireAccepted: false);
        replied?.Invoke(reply.Result.Output.ToArray());
        return ParseInvocation(command, reply.Result);
    }
    private static JsonElement ParseInvocation(Capability command, CommandResult reply)
    {
        // A failed extension's own code and message are in its structured result; ValheimCLI's closing ERROR line only
        // says "see structured result", so read that first.
        if (!reply.Ok && reply.Output.Count(x => x.StartsWith("EXTENSION_RESULT ", StringComparison.Ordinal)) == 1)
        {
            using var failed = ParseLine(reply, "EXTENSION_RESULT ");
            if (failed.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) throw ExtensionFailed(command, failed.RootElement, reply);
        }
        RequireSuccess(reply);
        using var document = ParseLine(reply, "EXTENSION_RESULT "); var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean()) throw ExtensionFailed(command, root, reply);
        if (root.GetProperty("schemaVersion").GetInt32() != command.SchemaVersion || root.GetProperty("instance").GetString() != command.Instance ||
            root.GetProperty("extension").GetString() != command.Path.Split('/')[0]) throw new InvalidOperationException("Extension changed; explicitly rediscover capabilities after reload.");
        return root.GetProperty("data").Clone();
    }
    private static InvalidOperationException ExtensionFailed(Capability command, JsonElement result, CommandResult reply)
    {
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? code = Text(result, "code"), message = Text(result, "message");
        var error = new InvalidOperationException($"{command.Path} failed: {code ?? "no code"}: {message ?? "no message"}");
        // Keep the existing exception type while giving internal callers a structured way to identify a game wait
        // expiry. Never classify it by translated or changing message text.
        error.Data["extension.path"] = command.Path;
        error.Data["extension.code"] = code;
        error.Data["transport.errorCode"] = reply.ErrorCode;
        return error;
    }
    internal static bool IsExtensionFailure(InvalidOperationException error, string path, string code, string transportCode) =>
        error.Data["extension.path"] is string actualPath && actualPath == path &&
        error.Data["extension.code"] is string actualCode && actualCode == code &&
        error.Data["transport.errorCode"] is string actualTransportCode && actualTransportCode == transportCode;
    public Observation Observe(Capability command, params string[] arguments) => Observe(command, null, arguments);
    /// <summary><see cref="Observe(Capability, string[])"/>, handing the complete reply lines to <paramref name="replied"/> before judging them.</summary>
    internal Observation Observe(Capability command, Action<IReadOnlyList<string>>? replied, string[] arguments)
    {
        if (!command.ReadOnly) throw new InvalidOperationException("Polling requires a read-only capability; issue mutations once.");
        var data = Invoke(command, replied, arguments);
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
