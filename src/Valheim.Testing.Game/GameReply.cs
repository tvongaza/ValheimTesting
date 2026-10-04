using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>
/// What the game and ValheimCLI answered one command: the transport's result, its lines, and whether the game refused it.
/// <see cref="GameActor.Execute"/> returns one and, by default, requires it <see cref="Accepted"/>: the transport
/// completed and no line is a known refusal. A transport result alone is not acceptance: a refused console command still
/// completes. Acceptance supplements a step's semantic check (read the line or state it changed); it never replaces one.
/// </summary>
public sealed class GameReply
{
    public GameReply(string command, CommandResult result)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>The command as sent.</summary>
    public string Command { get; }
    /// <summary>The transport's result, unchanged.</summary>
    public CommandResult Result { get; }
    /// <summary>The transport completed the command (not that the game accepted it: see <see cref="Accepted"/>).</summary>
    public bool Ok => Result.Ok;
    public IReadOnlyList<string> Output => Result.Output;
    public string? ErrorCode => Result.ErrorCode;
    public string? Message => Result.Message;

    /// <summary>
    /// Whether a line is how Valheim 1.0.16 or ValheimCLI refuses a command: the game's cheat gate ("That command is a
    /// cheat"), its "Error executing command" and "Unknown command", and a ValheimCLI <c>ERROR:</c> status line.
    /// </summary>
    public static bool IsRefusalLine(string line) =>
        line.Contains("That command is a cheat", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Error executing command", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first refusal line (<see cref="IsRefusalLine"/>), or null.</summary>
    public string? Refusal => Output.FirstOrDefault(IsRefusalLine);
    /// <summary>The transport completed and no line is a refusal.</summary>
    public bool Accepted => Ok && Refusal == null;

    /// <summary>The first line starting with <paramref name="prefix"/> (ordinal), or null.</summary>
    public string? Line(string prefix) => Output.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
    /// <summary>Every line starting with <paramref name="prefix"/> (ordinal), in order.</summary>
    public IEnumerable<string> Lines(string prefix) => Output.Where(line => line.StartsWith(prefix, StringComparison.Ordinal));
    /// <summary>
    /// The first line starting with <paramref name="prefix"/>, or a failure naming <paramref name="failure"/> (default: the
    /// command and the prefix) with the reply's lines.
    /// </summary>
    public string RequireLine(string prefix, string? failure = null) =>
        Line(prefix) ?? throw new InvalidOperationException((failure ?? $"{Command}: no line starting \"{prefix}\"") + ". Reply: " + Describe());

    /// <summary>
    /// Throws unless <see cref="Accepted"/>: a transport failure as <c>{code}: {message}</c> (or the command and its lines
    /// when the transport gave neither), a refusal with the command and the reply's lines. Returns this reply.
    /// </summary>
    public GameReply RequireAccepted()
    {
        if (!Ok) throw new InvalidOperationException(string.IsNullOrEmpty(ErrorCode) && string.IsNullOrEmpty(Message)
            ? $"{Command} failed: {Describe()}" : $"{ErrorCode}: {Message}");
        if (Refusal != null) throw new InvalidOperationException($"{Command} was refused: {Describe()}");
        return this;
    }

    /// <summary>The reply's lines joined for a message, or "(no output)".</summary>
    public string Describe() => Output.Count == 0 ? "(no output)" : string.Join(" | ", Output);
}
