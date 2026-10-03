using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game.Fakes;

/// <summary>
/// A ValheimCLI transport that answers from a script, for testing scenarios without a game. Built in: strict pins
/// (<c>cli_expect</c>, holding unless <see cref="PinsHold"/> is false), the extension listing (<c>cli_extensions</c>) of
/// the extensions registered with <see cref="Extension"/>, their commands, and confirmed saves (<see cref="Saves"/>).
/// Commands registered with <see cref="On"/> or <see cref="OnPrefix"/> are answered first. Anything unscripted throws, so
/// a scenario cannot silently issue a command its test did not expect. <see cref="Commands"/> records every command.
/// </summary>
public sealed class ScriptedTransport : ICancellableGameTransport
{
    private readonly object _sync = new();
    private readonly List<string> _commands = [];
    private readonly Dictionary<string, Func<string, CommandResult>> _exact = new(StringComparer.Ordinal);
    private readonly List<(string Prefix, Func<string, CommandResult> Reply)> _prefixes = [];
    private readonly List<(string Owner, string Instance, string Name, int ResultVersion, bool ReadOnly, Func<IReadOnlyList<string>, object> Data)> _extensions = [];
    private bool? _saveConfirmed;
    private int _saveNumber = 1;
    private Func<string, CancellationToken, Task<CommandResult>>? _cancellable;

    /// <summary>Whether <c>cli_expect</c> confirms the strict pins.</summary>
    public bool PinsHold { get; set; } = true;
    public bool Disposed { get; private set; }
    /// <summary>Every command received, in order, including the pin checks a <see cref="GameActor"/> sends before each command.</summary>
    public IReadOnlyList<string> Commands { get { lock (_sync) return _commands.ToArray(); } }
    /// <summary>How many times <paramref name="command"/> (exactly) or a command starting with it plus a space was received.</summary>
    public int Count(string command) => Commands.Count(x => x == command || x.StartsWith(command + " ", StringComparison.Ordinal));

    public ScriptedTransport On(string command, Func<string, CommandResult> reply) { lock (_sync) _exact[command] = reply; return this; }
    /// <summary>Controls one interruptible command in a scenario test; ordinary scripted commands remain synchronous.</summary>
    public ScriptedTransport OnCancellable(Func<string, CancellationToken, Task<CommandResult>> reply) { _cancellable = reply; return this; }
    public ScriptedTransport OnPrefix(string prefix, Func<string, CommandResult> reply) { lock (_sync) _prefixes.Add((prefix, reply)); return this; }
    /// <summary>
    /// Registers <c>owner/name</c>: listed by <c>cli_extensions</c> and answered with an <c>EXTENSION_RESULT</c> envelope around
    /// <paramref name="data"/>'s result (serialized as JSON), given the command's arguments.
    /// </summary>
    public ScriptedTransport Extension(string owner, string name, Func<IReadOnlyList<string>, object> data, bool readOnly = true, int resultVersion = 1, string instance = "fake")
    { lock (_sync) _extensions.Add((owner, instance, name, resultVersion, readOnly, data)); return this; }
    /// <summary>Answers <c>cli_save</c>: <c>OK: SAVE saveNumber=N</c> with a rising N when confirmed, otherwise a failed reply.</summary>
    public ScriptedTransport Saves(bool confirmed = true) { lock (_sync) _saveConfirmed = confirmed; return this; }

    /// <summary>A strictly pinned actor on this transport, as a verified session would return.</summary>
    public GameActor Actor(string name = "test", string expectations = "cli_expect worlduid=1")
    {
        var actor = new GameActor(name, this);
        actor.VerifyEnvironment(expectations);
        return actor;
    }

    public static CommandResult Ok(params string[] lines) => new() { Ok = true, Output = [.. lines] };
    public static CommandResult Failed(string line) => new() { Ok = false, Output = [line] };
    /// <summary>The <c>EXTENSION_RESULT</c> line of a successful extension command.</summary>
    public static string ExtensionResult(string owner, object data, string instance = "fake") =>
        "EXTENSION_RESULT " + JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, extension = owner, instance, data });

    public CommandResult Execute(string command, TimeSpan timeout)
    {
        Func<string, CommandResult>? reply;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            _commands.Add(command);
            if (!_exact.TryGetValue(command, out reply))
                reply = _prefixes.FirstOrDefault(p => command.StartsWith(p.Prefix, StringComparison.Ordinal)).Reply;
        }
        if (reply != null) return reply(command);
        if (command.StartsWith("cli_expect", StringComparison.Ordinal)) return PinsHold ? Ok("OK: EXPECT") : Failed("ERROR: expectation failed");
        if (command == "cli_extensions") return Ok("EXTENSIONS " + Listing());
        if (command.StartsWith("cli_extension ", StringComparison.Ordinal)) return RunExtension(command);
        if (command == "cli_save" && _saveConfirmed is bool confirmed)
            return confirmed ? Ok("OK: SAVE saveNumber=" + Interlocked.Increment(ref _saveNumber)) : Failed("ERROR: save failed");
        throw new InvalidOperationException("Unscripted command: " + command);
    }

    public async Task<CommandResult> ExecuteCancelableAsync(string expectations, string command, TimeSpan timeout, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var pins = Execute(expectations, timeout);
        if (!pins.Ok || !PlanExpectations.Judge(new ExpectationSource { From = "actor", Strict = true }, pins.Output).Held)
            throw new InvalidOperationException("Game did not confirm the strict environment pins on the command connection.");
        cancellation.ThrowIfCancellationRequested();
        CommandResult reply;
        if (_cancellable == null) reply = Execute(command, timeout);
        else
        {
            lock (_sync) _commands.Add(command);
            reply = await _cancellable(command, cancellation).ConfigureAwait(false);
        }
        cancellation.ThrowIfCancellationRequested();
        return reply;
    }

    private string Listing()
    {
        lock (_sync)
            return JsonSerializer.Serialize(new
            {
                apiVersion = 1,
                extensions = _extensions.GroupBy(e => (e.Owner, e.Instance)).Select(g => new
                {
                    id = g.Key.Owner, instance = g.Key.Instance, closing = false,
                    commands = g.Select(e => new { name = e.Name, resultVersion = e.ResultVersion, readOnly = e.ReadOnly }).ToArray(),
                }).ToArray(),
            });
    }
    private CommandResult RunExtension(string command)
    {
        string[] words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] path = words[1].Split('/');
        (string Owner, string Instance, string Name, int ResultVersion, bool ReadOnly, Func<IReadOnlyList<string>, object> Data) found;
        lock (_sync) found = _extensions.FirstOrDefault(e => path.Length == 2 && e.Owner == path[0] && e.Name == path[1]);
        if (found.Data == null)
            return new() { Ok = false, Output = ["EXTENSION_RESULT " + JsonSerializer.Serialize(new { ok = false, code = "no_extension_command" })] };
        return Ok(ExtensionResult(found.Owner, found.Data(words.Skip(2).ToArray()), found.Instance));
    }
    public void Dispose() { lock (_sync) Disposed = true; }
}
