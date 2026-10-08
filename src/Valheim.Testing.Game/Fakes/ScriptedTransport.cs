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
public sealed class ScriptedTransport : IGameTransport, IGameThreadStatusTransport
{
    private readonly object _sync = new();
    private readonly List<string> _commands = [];
    private readonly Dictionary<string, Func<string, CommandResult>> _exact = new(StringComparer.Ordinal);
    private readonly List<(string Prefix, Func<string, CommandResult> Reply)> _prefixes = [];
    private readonly List<(string Owner, string Instance, string Name, int ResultVersion, bool ReadOnly, Func<IReadOnlyList<string>, object> Data)> _extensions = [];
    private bool? _saveConfirmed;
    private int _saveNumber = 1;
    private Func<IReadOnlyDictionary<string, string>>? _status;

    /// <summary>Answer an independent STATUS connection in tests of a busy game thread.</summary>
    public ScriptedTransport OnStatus(Func<IReadOnlyDictionary<string, string>> reply) { _status = reply; return this; }
    public IReadOnlyDictionary<string, string> ReadStatus() =>
        (_status ?? throw new IOException("Scripted ValheimCLI STATUS is not available."))();

    /// <summary>Whether <c>cli_expect</c> confirms the strict pins.</summary>
    public bool PinsHold { get; set; } = true;
    public bool Disposed { get; private set; }
    /// <summary>Every command received, in order, including the pin checks a <see cref="GameActor"/> sends before each command.</summary>
    public IReadOnlyList<string> Commands { get { lock (_sync) return _commands.ToArray(); } }
    /// <summary>How many times <paramref name="command"/> (exactly) or a command starting with it plus a space was received.</summary>
    public int Count(string command) => Commands.Count(x => x == command || x.StartsWith(command + " ", StringComparison.Ordinal));

    public ScriptedTransport On(string command, Func<string, CommandResult> reply) { lock (_sync) _exact[command] = reply; return this; }
    public ScriptedTransport OnPrefix(string prefix, Func<string, CommandResult> reply) { lock (_sync) _prefixes.Add((prefix, reply)); return this; }
    /// <summary>
    /// Registers <c>owner/name</c>: listed by <c>cli_extensions</c> and answered with an <c>EXTENSION_RESULT</c> envelope around
    /// <paramref name="data"/>'s result (serialized as JSON), given the command's arguments.
    /// </summary>
    public ScriptedTransport Extension(string owner, string name, Func<IReadOnlyList<string>, object> data, bool readOnly = true, int resultVersion = 1, string instance = "fake")
    { lock (_sync) _extensions.Add((owner, instance, name, resultVersion, readOnly, data)); return this; }
    /// <summary>
    /// Answers a game client's test access as ValheimCLI and the game do (<see cref="TestAccess"/>): <c>cli_access</c> from
    /// the scripted state, <c>devcommands</c> toggling it and <c>cli_acknowledge_local_cheats</c> acknowledging cheats.
    /// <paramref name="inWorld"/> says whether the client has a local player (joined or hosting); <paramref name="hosting"/>
    /// whether it is the server of that world. Read <see cref="Access"/> to see or change the state. It replaces any handler
    /// already registered for those three commands; register a different reply (a refusal, say) after it.
    /// </summary>
    public ScriptedTransport ClientAccess(Func<bool> inWorld, Func<bool>? hosting = null, bool allowOnServerClients = true)
    {
        On("devcommands", _ => Ok("Dev commands: " + (Access.Devcommands = !Access.Devcommands)));
        On("cli_acknowledge_local_cheats", _ =>
        {
            if (!inWorld()) return Ok("ERROR: code=no_local_player A loaded local character is required");
            Access.CheatsAcknowledged = true; return Ok("OK: cheats acknowledged");
        });
        On("cli_access", _ =>
        {
            bool world = inWorld(), server = world && (hosting?.Invoke() ?? false);
            return Ok("ACCESS " + JsonSerializer.Serialize(new
            {
                schemaVersion = 1, complete = true, devcommands = Access.Devcommands, cheatsAcknowledged = Access.CheatsAcknowledged,
                allowOnServerClients, server, dedicated = false, joinedClient = world && !server, localPlayer = world, profileAvailable = world,
            }));
        });
        return this;
    }
    /// <summary>
    /// Answers <see cref="PlayerPlacement.Arrive"/>'s in-game waits as a client whose player can be teleported at once:
    /// <c>cli_wait_teleportable</c>, a trace per <c>cli_teleport_trace_arm</c> that completes with a ready floor, and
    /// <c>valheim.world/player-support-wait</c> with <paramref name="landed"/> (the player's support reading after the
    /// server's teleport). Also lists <c>valheim.session/teleport-signals</c>. The server's <c>cli_teleport_peer</c> is the test's.
    /// </summary>
    public ScriptedTransport ArrivalSignals(Func<object> landed)
    {
        ArgumentNullException.ThrowIfNull(landed);
        int traces = 0;
        OnPrefix("cli_wait_teleportable ", _ => Ok("OK: TELEPORTABLE ms=0 stillMs=0 cooldownSeconds=2.00 grounded=True position=0,40,0"));
        On("cli_teleport_trace_arm", _ => Ok("OK: TELEPORT_TRACE_ARM id=" + Interlocked.Increment(ref traces).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        OnPrefix("cli_teleport_trace_wait ", command => Ok("OK: TELEPORT_TRACE id=" + command.Split(' ')[1] +
            " distant=True requestedMs=0 movedMs=2000 areaReadyMs=2000 floorReadyMs=2000 doneMs=2000 floorAtDone=True final=0,0,0"));
        Extension("valheim.session", "teleport-signals", _ => new { source = "teleport-signals", complete = true });
        return Extension("valheim.world", "player-support-wait", _ => landed());
    }

    /// <summary>The client test-access state <see cref="ClientAccess"/> answers from.</summary>
    public ScriptedAccess Access { get; } = new();

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

/// <summary>A scripted client's test access (<see cref="ScriptedTransport.ClientAccess"/>).</summary>
[ResultShape]
public sealed class ScriptedAccess
{
    public bool Devcommands { get; set; }
    public bool CheatsAcknowledged { get; set; }
}
