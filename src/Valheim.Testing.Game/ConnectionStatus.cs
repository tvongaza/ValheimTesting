using System.Globalization;

namespace Valheim.Testing.Game;

/// <summary>
/// The game's connection status, with the game's names and numbers (<c>ZNet.ConnectionStatus</c> in 1.0.16). A server
/// refuses a peer by sending one of the error numbers, and the client keeps that status until its next join, also once
/// it is back at its main menu: 3 is a network or mod version mismatch, 6 a wrong password, 7 an id already connected,
/// 8 the ban or permit list, 9 a full server.
/// </summary>
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
    public static ConnectionStatusReading Read(GameActor actor) => Parse(actor.Execute("cli_connection_status", requireSuccess: false).Output);

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
public sealed record JoinRefusal(GameConnectionStatus Status, string Server, TimeSpan Elapsed)
{
    /// <summary>The game's number for <see cref="Status"/>: 3 for <c>ErrorVersion</c>.</summary>
    public int Code => (int)Status;
}
