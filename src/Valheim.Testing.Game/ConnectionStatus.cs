using System.Globalization;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// The game's connection status, with the game's names and numbers (<c>ZNet.ConnectionStatus</c> in 1.0.16). A server
/// refuses a peer by sending one of the error numbers, and the client keeps that status until its next join, also once
/// it is back at its main menu: 3 is a network or mod version mismatch, 6 a wrong password, 7 an id already connected,
/// 8 the ban or permit list, 9 a full server.
/// </summary>
[ResultShape]
public enum GameConnectionStatus
{
    None = 0,
    Connecting = 1,
    Connected = 2,
    ErrorVersion = 3,
    ErrorDisconnected = 4,
    ErrorConnectFailed = 5,
    ErrorPassword = 6,
    ErrorAlreadyConnected = 7,
    ErrorBanned = 8,
    ErrorFull = 9,
    ErrorPlatformExcluded = 10,
    ErrorCrossplayPrivilege = 11,
    ErrorKicked = 12,
}

/// <summary>
/// One reading of <c>cli_connection_status</c>: ValheimCLI replies <c>OK: connectionStatus=ErrorVersion, server=...</c>
/// (its console twin adds <c>gameState=...</c> before <c>server=</c>). The command is a diagnostic, so ValheimCLI answers
/// it even while its standing expectations fail.
/// </summary>
public sealed record ConnectionStatusReading(GameConnectionStatus Status, string Server)
{
    private const string Prefix = "OK: connectionStatus=";

    /// <summary>The game's number for <see cref="Status"/>, the error code a refusing server sends.</summary>
    public int Code => (int)Status;

    /// <summary>Reads the game's connection status once (read-only).</summary>
    public static ConnectionStatusReading Read(GameActor actor) => Parse(actor.Execute("cli_connection_status", requireAccepted: false).Output); // read after a refused join too; Parse requires the one status line

    /// <summary>Parses the reply; refuses a missing or repeated status line and a status name this game build does not have.</summary>
    public static ConnectionStatusReading Parse(IEnumerable<string> output)
    {
        var lines = output.Where(line => line.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1) throw new InvalidOperationException("Expected one connection status line (OK: connectionStatus=...); got: " + string.Join(" | ", output));
        string line = lines[0];
        int end = line.IndexOf(',', Prefix.Length);
        string name = (end < 0 ? line[Prefix.Length..] : line[Prefix.Length..end]).Trim();
        int server = line.IndexOf(", server=", StringComparison.Ordinal);
        return new ConnectionStatusReading(ParseStatus(name), server < 0 ? "" : line[(server + ", server=".Length)..]);
    }

    /// <summary>A status by the game's name (<c>ErrorVersion</c>); numbers and unknown names are refused.</summary>
    public static GameConnectionStatus ParseStatus(string name)
    {
        if (name.Length == 0 || !char.IsLetter(name[0]) || !Enum.TryParse(name, ignoreCase: false, out GameConnectionStatus status) || !Enum.IsDefined(status))
            throw new InvalidOperationException($"Unknown connection status \"{name}\"; this toolkit knows the statuses of Valheim 1.0.16.");
        return status;
    }

    /// <summary>Whether <paramref name="status"/> is one a refused or failed connection ends with (every <c>Error...</c> status).</summary>
    public static bool IsRefusal(GameConnectionStatus status) => status >= GameConnectionStatus.ErrorVersion && Enum.IsDefined(status);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Status} ({Code})");
}

/// <summary>A refused join, as <see cref="SessionControl.JoinExpectingRefusal"/> confirmed it.</summary>
[ResultShape]
public sealed record JoinRefusal(GameConnectionStatus Status, string Server, TimeSpan Elapsed)
{
    /// <summary>The game's number for <see cref="Status"/>: 3 for <c>ErrorVersion</c>.</summary>
    public int Code => (int)Status;
}

/// <summary>
/// What a client joins, as its run knows it before the join (#860): the server's or hosting client's name, its install's build when
/// it was read before staging, and, for a game the run owns, a bounded read of its log's refusal of a peer on the given network
/// version (null when none).
/// </summary>
internal sealed record JoinTarget(string Name, GameBuild? Build, Func<uint, TimeSpan, CancellationToken, string?>? RefusalLine = null);

/// <summary>
/// The one wording of why the game refused a join (#860), shared by every join kind: the status the client is left with, what it
/// means, and for <see cref="GameConnectionStatus.ErrorVersion"/> what the builds read before staging and the target's log prove.
/// ErrorVersion is also how a mod's own version check refuses, so a game mismatch is claimed only when the versions show one.
/// </summary>
internal static class JoinRefusalText
{
    /// <summary>The line Valheim's <c>ZNet.RPC_PeerInfo</c> logs when it refuses a peer's network version (1.0.17).</summary>
    internal static readonly Regex IncompatibleVersion = new(
        @"has incompatible version, mine:(?<mine>\S+) \(network version (?<mineNetwork>\d+)\)\s+remote (?<remote>\S+) \(network version (?<remoteNetwork>\d+|unknown)\)",
        RegexOptions.CultureInvariant);

    /// <summary>The same line for a peer on <paramref name="network"/> only: another peer's refusal is never read as this client's.</summary>
    internal static Regex IncompatibleVersionOf(uint network) => new(
        @"has incompatible version, mine:\S+ \(network version \d+\)\s+remote \S+ \(network version " + network.ToString(CultureInfo.InvariantCulture) + @"\)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// "the game refused the join with ErrorVersion (3): …. Nothing was retried; the client is back at its menu." for a client of
    /// <paramref name="client"/> joining <paramref name="target"/>. <paramref name="targetLog"/> is the target's own refusal of a peer
    /// on the client's network version (<see cref="JoinTarget.RefusalLine"/>), quoted by its versions only (the peer's endpoint can be
    /// a Steam ID).
    /// </summary>
    internal static string Explain(GameConnectionStatus status, GameBuild? client, JoinTarget? target, string? targetLog)
    {
        string name = target?.Name ?? "the server";
        string why = status switch
        {
            GameConnectionStatus.ErrorVersion => Version(client, target, name, targetLog),
            GameConnectionStatus.ErrorDisconnected => "the connection closed before the join completed.",
            GameConnectionStatus.ErrorConnectFailed => "the connection to the server or host failed (for an address join: nothing listening, a wrong port or a firewall).",
            GameConnectionStatus.ErrorPassword => "the password did not match the server's (an address join takes it from the plan's passwordVariable).",
            GameConnectionStatus.ErrorAlreadyConnected => "a peer with the same identity is already connected (one Steam account on two clients).",
            GameConnectionStatus.ErrorBanned => "the ban list or permitted list refused this client.",
            GameConnectionStatus.ErrorFull => "the server is full.",
            GameConnectionStatus.ErrorPlatformExcluded => "the server excludes this client's platform.",
            GameConnectionStatus.ErrorCrossplayPrivilege => "this client's account has no crossplay privilege.",
            GameConnectionStatus.ErrorKicked => "the server kicked this client.",
            _ => "the game names no reason.",
        };
        return string.Create(CultureInfo.InvariantCulture, $"the game refused the join with {status} ({(int)status}): {why} Nothing was retried; the client is back at its menu.");
    }

    private static string Version(GameBuild? client, JoinTarget? target, string name, string? targetLog)
    {
        var line = targetLog == null ? null : IncompatibleVersion.Match(targetLog);
        string logged = line is not { Success: true } ? ""
            : $" The log of {name} refused this client's {line.Groups["remote"].Value} (network version {line.Groups["remoteNetwork"].Value}) as itself " +
              $"{line.Groups["mine"].Value} (network version {line.Groups["mineNetwork"].Value}).";
        if (client?.NetworkVersion is { } mine && target?.Build?.NetworkVersion is { } theirs)
            return mine != theirs
                ? $"this client's install is {client} and {name}'s is {target.Build}; Valheim refuses a join across network versions. " +
                  "Update the older install (Steam, or SteamCMD for a dedicated server)." + logged
                : $"both installs are on network version {mine} (read before staging), so Valheim's own version check passed; a mod's version " +
                  $"check refuses with the same status (for crossplay, so does mismatched lobby data), and {name}'s log names its reason." + logged;
        // The log is read only for this client's own network version, so a line found is this client's refusal.
        if (line is { Success: true } && client?.NetworkVersion is { } own)
            return $"Valheim refused this client's network version {own}." + logged + " Update the older install (Steam, or SteamCMD for a dedicated server).";
        return "the network versions of this client and " + name + " were not both read before staging, so this is Valheim's network " +
            "version check or a mod's own; a game mismatch shows in " + name + "'s log as \"has incompatible version\".";
    }
}
