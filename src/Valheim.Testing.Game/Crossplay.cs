using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// One reading of <c>cli_multiplayer_identity</c>, which ValheimCLI answers with
/// <c>OK: steamId=..., playFabLoginState=..., playFabId=..., backend=..., gameState=..., connectionStatus=..., isServer=..., isOpenServer=..., server=...</c>.
/// <see cref="Backend"/> is the game's online backend: <c>PlayFab</c> on a crossplay server, <c>Steamworks</c> otherwise.
/// A value ValheimCLI could not read is <c>unavailable</c> (with the exception type in brackets).
/// </summary>
public sealed record MultiplayerIdentity(string SteamId, string PlayFabLoginState, string PlayFabId, string Backend, string GameState,
    string ConnectionStatus, bool IsServer, bool IsOpenServer, string Server)
{
    private static readonly string[] Keys = ["steamId", "playFabLoginState", "playFabId", "backend", "gameState", "connectionStatus", "isServer", "isOpenServer", "server"];

    /// <summary>Whether ValheimCLI could read the PlayFab entity id.</summary>
    public bool PlayFabIdAvailable => PlayFabId.Length != 0 && !PlayFabId.StartsWith("unavailable", StringComparison.Ordinal);

    /// <summary>Reads the game's multiplayer identity once (read-only).</summary>
    public static MultiplayerIdentity Read(GameActor actor) => Parse(actor.Execute("cli_multiplayer_identity", requireSuccess: false).Output);

    /// <summary>Parses the reply; refuses a missing line or a missing field.</summary>
    public static MultiplayerIdentity Parse(IEnumerable<string> output)
    {
        var lines = output.Where(line => line.StartsWith("OK: steamId=", StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1) throw new InvalidOperationException("Expected one multiplayer identity line (OK: steamId=...); got: " + string.Join(" | ", output));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string text = lines[0]["OK: ".Length..];
        // server= is the last field and may hold anything; every other field is key=value up to the next ", ".
        int server = text.IndexOf(", server=", StringComparison.Ordinal);
        if (server < 0) throw new InvalidOperationException("The multiplayer identity has no server field: " + lines[0]);
        values["server"] = text[(server + ", server=".Length)..];
        foreach (string part in text[..server].Split(", "))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0) throw new InvalidOperationException("Unreadable multiplayer identity field \"" + part + "\" in: " + lines[0]);
            values[part[..equals]] = part[(equals + 1)..];
        }
        foreach (string key in Keys)
            if (!values.ContainsKey(key)) throw new InvalidOperationException($"The multiplayer identity has no {key} field: " + lines[0]);
        return new MultiplayerIdentity(values["steamId"], values["playFabLoginState"], values["playFabId"], values["backend"], values["gameState"],
            values["connectionStatus"], Flag(values["isServer"], lines[0]), Flag(values["isOpenServer"], lines[0]), values["server"]);
    }

    private static bool Flag(string value, string line) => value switch
    {
        "True" => true,
        "False" => false,
        _ => throw new InvalidOperationException($"Unreadable flag \"{value}\" in: {line}"),
    };
}

/// <summary>A crossplay server's lobby: <see cref="RemotePlayerId"/> is what a client joins (<c>cli_connect_playfab_user</c>).</summary>
public sealed record CrossplayLobby(string RemotePlayerId, string LobbyId);

/// <summary>
/// A dedicated server started with <c>-crossplay</c> (<see cref="ServerRunPlan.Crossplay"/>). It uses the game's PlayFab
/// backend: it logs in to PlayFab after it starts, then registers a lobby that clients join by the server's PlayFab
/// entity id, not by address. The lobby is keyed by the server's public IP and game port, so two crossplay servers behind
/// one public IP must use different game ports (<c>-port</c>), or the second takes over the first one's joins.
/// </summary>
public static class CrossplayServer
{
    /// <summary>
    /// The server's lobby line, as the game logs it in 1.0.16 once the lobby exists:
    /// <c>Created PlayFab lobby with ID "...", ConnectionString "..." and owned by "&lt;remote player id&gt;"</c>.
    /// </summary>
    public static readonly Regex LobbyCreated = new(@"Created PlayFab lobby with ID ""(?<lobby>[^""]+)"", ConnectionString ""[^""]*"" and owned by ""(?<owner>[^""]+)""", RegexOptions.CultureInvariant);

    /// <summary>Lines after which no lobby will come: the server's PlayFab login failed, or the game's crossplay libraries did not load.</summary>
    public static readonly IReadOnlyList<Regex> LobbyFailures =
    [
        new(@"Failed to login server to PlayFab backend", RegexOptions.CultureInvariant),
        new(@"DLL Not Found: .*crossplay", RegexOptions.CultureInvariant),
    ];

    /// <summary>The copied runtime's BepInEx log, which holds the game's own log lines when BepInEx listens to Unity's log (its default).</summary>
    public static string BepInExLog(string runtimeDirectory) => Path.Combine(runtimeDirectory, "BepInEx", "LogOutput.log");

    /// <summary>
    /// Waits until the crossplay server's lobby exists and returns its remote player id. First requires the server's
    /// <c>cli_multiplayer_identity</c> to say it is a server on the PlayFab backend (so a plan without crossplay fails at
    /// once), then waits on the event: the lobby line in <paramref name="serverLog"/>, this boot's log read from its start
    /// (<see cref="BepInExLog"/>, or the Unity log the plan's <c>-logFile</c> names). When the identity carries the PlayFab
    /// entity id, it must be the lobby's owner. Read-only: nothing is sent to the game but the identity query.
    /// </summary>
    public static CrossplayLobby WaitForLobby(GameActor server, string serverLog, TimeSpan timeout, CancellationToken cancellation = default)
    {
        WaitText.RequireTimeout(timeout);
        var identity = RequirePlayFabServer(server);
        using var log = new LogWait(serverLog, offset: 0);
        var line = log.WaitAsync(LobbyCreated, timeout, LobbyFailures, cancellation).GetAwaiter().GetResult();
        return Lobby(identity, line.Match, serverLog);
    }

    /// <summary>
    /// <see cref="WaitForLobby(GameActor, string, TimeSpan, CancellationToken)"/> for a server on another machine
    /// (<c>PinnedServerRun --profile</c>): the lobby line is awaited on <paramref name="host"/> in <paramref name="serverLog"/>,
    /// the host's path of this boot's log (<see cref="HostBepInExLog"/> of the run's host runtime), from its start, with
    /// the host's event-driven <see cref="IGameHost.WaitForLogAsync"/>.
    /// </summary>
    public static CrossplayLobby WaitForLobby(GameActor server, IGameHost host, string serverLog, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        WaitText.RequireTimeout(timeout);
        var identity = RequirePlayFabServer(server);
        var result = host.WaitForLogAsync(serverLog, 0, LobbyCreated, LobbyFailures, timeout, cancellation).GetAwaiter().GetResult();
        return Lobby(identity, LobbyCreated.Match(result.EnsureMatched()), host.Name + ":" + serverLog);
    }

    /// <summary>
    /// The game's line when PlayFab confirms that a lobby is marked inactive (1.0.16, <c>ZPlayFabMatchmaking.DeleteLobby</c>):
    /// <c>Deactivated PlayFab lobby &lt;lobby id&gt;</c>. It is logged from PlayFab's reply, which a dedicated server quitting
    /// on Ctrl+C or SIGINT does not wait for: on the 1.0.16 Windows server the process ended about two seconds after the
    /// request, before the reply, so the line is recorded when present but not required.
    /// </summary>
    public static readonly Regex LobbyDeactivated = new(@"Deactivated PlayFab lobby (?<lobby>\S+)", RegexOptions.CultureInvariant);
    /// <summary>
    /// The game's line when its shutdown retires its lobby: it asks PlayFab to mark the lobby inactive and leaves the
    /// network (<c>Unregister PlayFab server "&lt;name&gt;" and leaving network "..."</c>).
    /// </summary>
    public static readonly Regex ServerUnregistered = new(@"Unregister PlayFab server ""[^""]*"" and leaving network", RegexOptions.CultureInvariant);

    /// <summary>
    /// After a crossplay run's owned server boots have stopped: requires that every boot quit cleanly
    /// (<see cref="StopOutcome.Clean"/>, or had exited by itself through the game's shutdown), that its logs show the lobby it
    /// created (<see cref="LobbyCreated"/>), and that its shutdown retired it (<see cref="ServerUnregistered"/>). A killed server
    /// skips its shutdown, so its lobby stays active for hours and PlayFab can send later joins on the same address and port
    /// to it: a run with one proves nothing about the next run's join and fails here. Logs without any lobby line fail too,
    /// because they cannot show a retirement (pass the log the game writes to: the <c>-logFile</c> file, or BepInEx's log when
    /// it copies Unity's lines). <paramref name="bootLogs"/> holds each boot's kept logs, in launch order as <paramref name="stops"/>;
    /// a missing file is skipped. Returns one line per boot for the report, saying whether PlayFab's confirmation was logged.
    /// Whether the lobby really stopped taking joins is shown by the next run's join on the same address and port.
    /// </summary>
    public static IReadOnlyList<string> RequireLobbiesRetired(IReadOnlyList<ProcessStop> stops, IReadOnlyList<IReadOnlyList<string>> bootLogs)
    {
        var lines = new List<string>();
        var problems = new List<string>();
        for (int i = 0; i < Math.Max(stops.Count, bootLogs.Count); i++)
        {
            string boot = "boot-" + (i + 1);
            var stop = i < stops.Count ? stops[i] : null;
            if (stop == null) { problems.Add($"{boot} was never stopped"); continue; }
            var kept = i < bootLogs.Count ? bootLogs[i].Where(File.Exists).ToList() : [];
            if (kept.Count == 0) { problems.Add($"{boot}: no log was kept, so its lobby's retirement cannot be read"); continue; }
            string text = string.Join("\n", kept.Select(File.ReadAllText));
            var created = LobbyCreated.Matches(text).Select(match => match.Groups["lobby"].Value).Distinct().ToList();
            var confirmed = LobbyDeactivated.Matches(text).Select(match => match.Groups["lobby"].Value).ToHashSet();
            bool unregistered = ServerUnregistered.IsMatch(text);
            if (stop.Outcome == StopOutcome.Killed)
                problems.Add($"{boot} was {stop}: a killed server's PlayFab lobby stays active and can take later joins on this address and port, so this run is not a clean crossplay run");
            else if (stop.Outcome == StopOutcome.AlreadyExited && !unregistered)
                problems.Add($"{boot} had exited before the stop without the game's shutdown (no \"Unregister PlayFab server\" line)");
            if (created.Count == 0)
                problems.Add($"{boot}'s logs ({string.Join(", ", kept.Select(Path.GetFileName))}) hold no \"Created PlayFab lobby\" line, so they cannot show its lobby retired; keep the log the game writes to");
            else if (stop.Outcome != StopOutcome.Killed && !unregistered)
                problems.Add($"{boot} created PlayFab lobby {string.Join(",", created)}, but its shutdown never retired it (no \"Unregister PlayFab server\" line)");
            lines.Add($"{boot}: {stop}; lobby {(created.Count == 0 ? "none" : string.Join(",", created))}; retired {(unregistered ? "yes" : "no")}; " +
                $"PlayFab confirmation {(created.Count != 0 && created.All(confirmed.Contains) ? "logged" : "not logged")}");
        }
        if (problems.Count != 0) throw new InvalidOperationException(string.Join("; ", problems) + ".");
        return lines;
    }

    /// <summary>A Linux host runtime's BepInEx log, as a path on that host.</summary>
    public static string HostBepInExLog(string hostRuntimeDirectory) => hostRuntimeDirectory.TrimEnd('/') + "/BepInEx/LogOutput.log";

    private static MultiplayerIdentity RequirePlayFabServer(GameActor server)
    {
        var identity = MultiplayerIdentity.Read(server);
        if (!identity.IsServer) throw new InvalidOperationException("This game is not a server: " + identity);
        if (identity.Backend != "PlayFab")
            throw new InvalidOperationException($"The server's online backend is {identity.Backend}, not PlayFab: it was not started with -crossplay. Set \"crossplay\": true in the server plan.");
        return identity;
    }

    private static CrossplayLobby Lobby(MultiplayerIdentity identity, Match match, string where)
    {
        string owner = match.Groups["owner"].Value;
        if (identity.PlayFabIdAvailable && identity.PlayFabId != owner)
            throw new InvalidOperationException($"The lobby in {where} is owned by another PlayFab id than this server's; is the log from another boot?");
        return new CrossplayLobby(owner, match.Groups["lobby"].Value);
    }
}
