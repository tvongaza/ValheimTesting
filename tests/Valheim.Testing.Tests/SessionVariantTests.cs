using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

// Joins that are not the plain dedicated-server join: one the server must refuse (with the game's status read back at the
// menu), and a crossplay (PlayFab) join, plus the replies they read. Scripted transports only; no game.
public sealed class SessionVariantTests : IDisposable
{
    private const string Menu = "cli_expect my.mod=absent";
    private const string Protected = "OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True";
    private readonly string _root = Directory.CreateTempSubdirectory("session-variants-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CommandResult FailedJoin(string code) => new()
    {
        Ok = false, ErrorCode = code,
        Output =
        [
            "EXTENSION_RESULT " + JsonSerializer.Serialize(new { schemaVersion = 1, ok = false, extension = "valheim.session", instance = "fake", code, message = "The game refused the session transition.", data = new { } }),
            $"ERROR: code={code} message=Extension failed; see structured result.",
        ],
    };

    // A client at its menu. Its first join ends as ValheimCLI reports it (refusalCode; null: the join succeeds), the game
    // then shows loadingReadings "loading" readings before it is back at its menu, and cli_connection_status reports status.
    // A later join succeeds into world 7.
    private static ScriptedTransport Client(string status, string? refusalCode = "join_failed", int loadingReadings = 2)
    {
        bool devcommands = false, joined = false, refused = false; int joins = 0, readings = 0;
        var transport = new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .Extension("valheim.session", "join", _ => throw new InvalidOperationException("answered by the prefix"), readOnly: false)
            .OnPrefix("cli_extension valheim.session/join ", _ =>
            {
                if (++joins == 1 && refusalCode != null) { refused = true; return FailedJoin(refusalCode); }
                joined = true;
                return ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("valheim.session", new { source = "session-join", complete = true, action = "join" }));
            })
            .Extension("valheim.session", "state", _ =>
            {
                bool loading = refused && !joined && ++readings <= loadingReadings;
                return new
                {
                    source = "session-state", complete = true, phase = joined ? "world-present" : loading ? "loading" : "menu", worldUid = joined ? "7" : null,
                    worldPresent = joined, worldReady = joined, server = false, dedicated = false, localPlayer = joined, playerReady = joined,
                    saving = false, loadError = false, connectionStatus = joined ? "Connected" : refused ? status : "None",
                };
            })
            .On("cli_connection_status", _ => ScriptedTransport.Ok($"OK: connectionStatus={(joined ? "Connected" : refused ? status : "None")}, server=steam/0/127.0.0.1:2456"))
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok(Protected));
        return transport;
    }

    [Fact] public void ARefusalWithTheExpectedStatusEndsAtThePinnedMenuAndANormalJoinThenWorks()
    {
        var transport = Client("ErrorVersion");
        using var actor = transport.Actor("client", Menu);
        var session = new SessionControl(actor);
        var refusal = session.JoinExpectingRefusal("127.0.0.1:2456", "Tester", GameConnectionStatus.ErrorVersion, Menu, TimeSpan.FromSeconds(10));
        Assert.Equal(GameConnectionStatus.ErrorVersion, refusal.Status);
        Assert.Equal(3, refusal.Code);
        Assert.Equal("steam/0/127.0.0.1:2456", refusal.Server);
        var commands = transport.Commands.ToList();
        Assert.Equal(1, transport.Count("cli_extension valheim.session/join"));
        Assert.Equal(1, transport.Count("cli_connection_status"));
        // The status is read once the client is back at its menu: after the loading readings and the menu reading.
        Assert.Equal(3, transport.Count("cli_extension valheim.session/state"));
        Assert.True(commands.IndexOf("cli_connection_status") > commands.FindLastIndex(c => c == "cli_extension valheim.session/state"));
        Assert.Equal("menu", session.Read().Phase); // Re-pinned: usable without another VerifyEnvironment.

        // The server still accepts a matching client.
        session.Join("127.0.0.1:2456", "Tester");
        actor.VerifyEnvironment("cli_expect worlduid=7");
        Assert.True(session.WaitForWorld("7", TimeSpan.FromSeconds(10)).WorldReady);
        Assert.Equal(2, transport.Count("cli_extension valheim.session/join"));
    }

    [Theory] [InlineData("ErrorPassword", "ErrorPassword (6)")] [InlineData("ErrorDisconnected", "ErrorDisconnected (4)")] [InlineData("ErrorBanned", "ErrorBanned (8)")]
    public void ARefusalForAnotherReasonFails(string status, string named)
    {
        var transport = Client(status);
        using var actor = transport.Actor("client", Menu);
        var error = Assert.Throws<InvalidOperationException>(() => new SessionControl(actor).JoinExpectingRefusal("127.0.0.1:2456", "Tester", GameConnectionStatus.ErrorVersion, Menu, TimeSpan.FromSeconds(10)));
        Assert.Contains(named, error.Message);
        Assert.Contains("ErrorVersion (3)", error.Message);
        Assert.Equal(1, transport.Count("cli_extension valheim.session/join"));
    }

    [Fact] public void AJoinThatSucceedsFailsARefusalExpectation()
    {
        var transport = Client("ErrorVersion", refusalCode: null); // Negative control: the server lets the client in.
        using var actor = transport.Actor("client", Menu);
        var error = Assert.Throws<InvalidOperationException>(() => new SessionControl(actor).JoinExpectingRefusal("127.0.0.1:2456", "Tester", GameConnectionStatus.ErrorVersion, Menu, TimeSpan.FromSeconds(10)));
        Assert.Contains("The join succeeded", error.Message);
        Assert.Equal(1, transport.Count("cli_extension valheim.session/join"));
        Assert.Equal(0, transport.Count("cli_connection_status"));
        Assert.Throws<InvalidOperationException>(() => actor.Execute("cli_connection_status")); // The transition invalidated the pins.
    }

    [Theory] [InlineData("character_unavailable")] [InlineData("transition_timeout")] [InlineData("load_error")] [InlineData("not_menu")]
    public void AJoinThatEndsAnotherWayIsNotARefusal(string code)
    {
        var transport = Client("ErrorVersion", refusalCode: code);
        using var actor = transport.Actor("client", Menu);
        var error = Assert.Throws<InvalidOperationException>(() => new SessionControl(actor).JoinExpectingRefusal("127.0.0.1:2456", "Tester", GameConnectionStatus.ErrorVersion, Menu, TimeSpan.FromSeconds(10)));
        Assert.Contains(code, error.Message);
        Assert.Equal(1, transport.Count("cli_extension valheim.session/join"));
        Assert.Equal(0, transport.Count("cli_connection_status"));
    }

    [Fact] public void ARefusedClientThatNeverReachesItsMenuTimesOutWithoutReadingAStatus()
    {
        var transport = Client("ErrorVersion", loadingReadings: int.MaxValue);
        using var actor = transport.Actor("client", Menu);
        var error = Assert.Throws<WaitTimeoutException>(() => new SessionControl(actor).JoinExpectingRefusal("127.0.0.1:2456", "Tester", GameConnectionStatus.ErrorVersion, Menu, TimeSpan.FromMilliseconds(300)));
        Assert.Contains("phase loading", error.Message);
        Assert.Equal(1, transport.Count("cli_extension valheim.session/join"));
        Assert.Equal(0, transport.Count("cli_connection_status"));
    }

    [Theory] [InlineData(GameConnectionStatus.None)] [InlineData(GameConnectionStatus.Connecting)] [InlineData(GameConnectionStatus.Connected)] [InlineData((GameConnectionStatus)13)]
    public void OnlyARefusalStatusCanBeExpected(GameConnectionStatus expected)
    {
        var transport = Client("ErrorVersion");
        using var actor = transport.Actor("client", Menu);
        Assert.Throws<ArgumentException>(() => new SessionControl(actor).JoinExpectingRefusal("127.0.0.1:2456", "Tester", expected, Menu, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, transport.Count("devcommands"));
        Assert.Equal(0, transport.Count("cli_extension valheim.session/join"));
    }

    [Theory]
    [InlineData("OK: connectionStatus=ErrorVersion, server=steam/0/127.0.0.1:2456", GameConnectionStatus.ErrorVersion, "steam/0/127.0.0.1:2456")]
    [InlineData("OK: connectionStatus=ErrorBanned, gameState=MainMenu, server=playfab/ABC", GameConnectionStatus.ErrorBanned, "playfab/ABC")] // The console twin.
    [InlineData("OK: connectionStatus=None, server=", GameConnectionStatus.None, "")]
    public void ConnectionStatusRepliesAreRead(string line, GameConnectionStatus status, string server)
    {
        var reading = ConnectionStatusReading.Parse(["noise", line]);
        Assert.Equal(status, reading.Status);
        Assert.Equal(server, reading.Server);
    }

    [Theory] [InlineData("OK: connectionStatus=3, server=x")] [InlineData("OK: connectionStatus=ErrorSomethingNew, server=x")] [InlineData("ERROR: code=unexpected_exception")] [InlineData("OK: connectionStatus=errorversion, server=x")]
    public void AnUnreadableConnectionStatusIsRefused(string line) => Assert.Throws<InvalidOperationException>(() => ConnectionStatusReading.Parse([line]));

    [Fact] public void TwoStatusLinesAreRefused() =>
        Assert.Throws<InvalidOperationException>(() => ConnectionStatusReading.Parse(["OK: connectionStatus=ErrorVersion, server=a", "OK: connectionStatus=Connected, server=a"]));

    [Fact] public void StatusNumbersAreTheGames()
    {
        // As the game numbers them in 1.0.16; a refusing server sends these numbers.
        Assert.Equal(new[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 },
            new[] { "ErrorVersion", "ErrorDisconnected", "ErrorConnectFailed", "ErrorPassword", "ErrorAlreadyConnected", "ErrorBanned", "ErrorFull", "ErrorPlatformExcluded", "ErrorCrossplayPrivilege", "ErrorKicked" }
                .Select(name => (int)ConnectionStatusReading.ParseStatus(name)));
        Assert.All(new[] { GameConnectionStatus.None, GameConnectionStatus.Connecting, GameConnectionStatus.Connected }, s => Assert.False(ConnectionStatusReading.IsRefusal(s)));
    }

    // A client at its menu, left with a stale ErrorVersion from an earlier refused join. Its crossplay join goes through
    // loadingReadings readings, then either connects into world 7 or returns to the menu with failWith.
    private static ScriptedTransport CrossplayClient(string? connectReply = null, string? failWith = null, bool startInWorld = false, int loadingReadings = 2)
    {
        bool devcommands = false, started = startInWorld; int readings = 0;
        return new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Tester' (tester, Local)"))
            .OnPrefix("cli_connect_playfab_user ", command =>
            {
                started = true;
                string id = command.Split(' ')[1];
                return connectReply != null ? ScriptedTransport.Failed(connectReply) : ScriptedTransport.Ok($"OK: PlayFab user join started for {id} using character 'Tester' (tester, Local)");
            })
            .Extension("valheim.session", "state", _ =>
            {
                bool loading = started && ++readings <= loadingReadings, failed = started && !loading && failWith != null, inWorld = started && !loading && !failed;
                return new
                {
                    source = "session-state", complete = true, phase = inWorld ? "world-present" : loading ? "loading" : "menu", worldUid = inWorld ? "7" : null,
                    worldPresent = inWorld, worldReady = inWorld, server = false, dedicated = false, localPlayer = inWorld, playerReady = inWorld,
                    saving = false, loadError = false, connectionStatus = inWorld ? "Connected" : loading ? "Connecting" : failed ? failWith! : "ErrorVersion",
                };
            })
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok(Protected));
    }

    [Fact] public void ACrossplayJoinSelectsTheCharacterStartsOnceWithoutAPasswordAndWaitsForTheConnection()
    {
        var transport = CrossplayClient();
        using var actor = transport.Actor("client", Menu);
        var session = new SessionControl(actor);
        var state = session.JoinCrossplay("ENTITY42", "Tester", "7", Menu, TimeSpan.FromSeconds(10));
        Assert.True(state.PlayerReady);
        var commands = transport.Commands.ToList();
        Assert.Equal(1, transport.Count("cli_connect_playfab_user"));
        Assert.Contains("cli_connect_playfab_user ENTITY42", commands); // Exactly the id: no password token.
        Assert.True(commands.IndexOf("cli_select_character Tester") < commands.IndexOf("cli_connect_playfab_user ENTITY42"));
        Assert.True(commands.LastIndexOf("devcommands") < commands.IndexOf("cli_connect_playfab_user ENTITY42"));
        Assert.Equal(0, transport.Count("cli_extension valheim.session/join"));
        // The stale ErrorVersion at the menu before the join started did not count as this join's failure.
        actor.VerifyEnvironment("cli_expect worlduid=7");
        Assert.True(session.WaitForWorld("7", TimeSpan.FromSeconds(10)).WorldReady);
        Assert.Equal(1, transport.Count("cli_set_player_safety"));
    }

    [Fact] public void ACrossplayJoinThatReturnsToTheMenuWithAnErrorFails()
    {
        var transport = CrossplayClient(failWith: "ErrorConnectFailed");
        using var actor = transport.Actor("client", Menu);
        var error = Assert.Throws<InvalidOperationException>(() => new SessionControl(actor).JoinCrossplay("ENTITY42", "Tester", "7", Menu, TimeSpan.FromSeconds(10)));
        Assert.Contains("ErrorConnectFailed", error.Message);
        Assert.Equal(1, transport.Count("cli_connect_playfab_user"));
    }

    [Fact] public void ACrossplayJoinThatDoesNotStartFailsAndLeavesTheActorUnpinned()
    {
        var transport = CrossplayClient(connectReply: "ERROR: Main menu is not available");
        using var actor = transport.Actor("client", Menu);
        var session = new SessionControl(actor);
        var error = Assert.Throws<InvalidOperationException>(() => session.JoinCrossplay("ENTITY42", "Tester", "7", Menu, TimeSpan.FromSeconds(10)));
        Assert.Contains("did not start", error.Message);
        Assert.Throws<InvalidOperationException>(() => session.Read());
    }

    [Fact] public void ACrossplayJoinStartsOnlyFromTheIdleMenu()
    {
        var transport = CrossplayClient(startInWorld: true, loadingReadings: 0);
        using var actor = transport.Actor("client", Menu);
        Assert.Throws<InvalidOperationException>(() => new SessionControl(actor).JoinCrossplay("ENTITY42", "Tester", "7", Menu, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, transport.Count("cli_select_character"));
        Assert.Equal(0, transport.Count("cli_connect_playfab_user"));
    }

    [Fact] public void ACrossplayJoinThatNeverConnectsTimesOut()
    {
        var transport = CrossplayClient(loadingReadings: int.MaxValue);
        using var actor = transport.Actor("client", Menu);
        var error = Assert.Throws<WaitTimeoutException>(() => new SessionControl(actor).JoinCrossplay("ENTITY42", "Tester", "7", Menu, TimeSpan.FromMilliseconds(300)));
        Assert.Contains("connection Connecting", error.Message);
        Assert.Equal(1, transport.Count("cli_connect_playfab_user"));
    }

    private const string Identity = "OK: steamId=unavailable, playFabLoginState=LoggedIn, playFabId=unavailable, backend=PlayFab, gameState=InWorld, connectionStatus=Connected, isServer=True, isOpenServer=True, server=playfab/";

    [Fact] public void MultiplayerIdentityRepliesAreRead()
    {
        var identity = MultiplayerIdentity.Parse(["noise", Identity]);
        Assert.Equal("PlayFab", identity.Backend);
        Assert.Equal("LoggedIn", identity.PlayFabLoginState);
        Assert.False(identity.PlayFabIdAvailable);
        Assert.True(identity.IsServer); Assert.True(identity.IsOpenServer);
        Assert.Equal("playfab/", identity.Server);
        Assert.True(MultiplayerIdentity.Parse([Identity.Replace("playFabId=unavailable", "playFabId=ENTITY42")]).PlayFabIdAvailable);
        Assert.Throws<InvalidOperationException>(() => MultiplayerIdentity.Parse([Identity.Replace("backend=PlayFab, ", "")]));
        Assert.Throws<InvalidOperationException>(() => MultiplayerIdentity.Parse([Identity.Replace("isServer=True", "isServer=yes")]));
        Assert.Throws<InvalidOperationException>(() => MultiplayerIdentity.Parse(["ERROR: code=unexpected_exception"]));
    }

    private string Log(params string[] lines)
    {
        string path = Path.Combine(_root, "LogOutput.log");
        File.WriteAllLines(path, lines);
        return path;
    }
    private static GameActor Server(string identity) => new ScriptedTransport().On("cli_multiplayer_identity", _ => ScriptedTransport.Ok(identity)).Actor("server");

    [Fact] public void TheLobbyAndItsRemotePlayerIdComeFromTheServersLog()
    {
        string log = Log("[Info   : Unity Log] Register PlayFab server \"Test\"",
            "[Info   : Unity Log] Created PlayFab lobby with ID \"LOBBY-1\", ConnectionString \"conn;string\" and owned by \"ENTITY42\"");
        using var server = Server(Identity);
        var lobby = CrossplayServer.WaitForLobby(server, log, TimeSpan.FromSeconds(5));
        Assert.Equal(new CrossplayLobby("ENTITY42", "LOBBY-1"), lobby);
        // When the identity carries the entity id, it must be the lobby's owner.
        using var same = Server(Identity.Replace("playFabId=unavailable", "playFabId=ENTITY42"));
        Assert.Equal("ENTITY42", CrossplayServer.WaitForLobby(same, log, TimeSpan.FromSeconds(5)).RemotePlayerId);
        using var other = Server(Identity.Replace("playFabId=unavailable", "playFabId=ENTITY7"));
        Assert.Throws<InvalidOperationException>(() => CrossplayServer.WaitForLobby(other, log, TimeSpan.FromSeconds(5)));
    }

    // A crossplay server on another machine: the lobby line is awaited on that host, in its own log, from the boot's start.
    private sealed class LogHost(HostLogResult reply) : IGameHost
    {
        public List<(string Log, long Offset)> Waits { get; } = [];
        public string Name => "linux-box";
        public GameHostKind Kind => GameHostKind.Ssh;
        public HostShell Shell => HostShell.Bash;
        public Task<HostLogResult> WaitForLogAsync(string logPath, long fromOffset, System.Text.RegularExpressions.Regex success, IReadOnlyList<System.Text.RegularExpressions.Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default)
        {
            Waits.Add((logPath, fromOffset));
            return Task.FromResult(reply);
        }
        public Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<FetchedDirectory> FetchDirectoryAsync(string hostDirectory, string localDirectory, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<HostLock> AcquireLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<HostLockResult> CheckLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<Shipment> ShipRevisionAsync(string repository, string revision, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<Shipment> ShipFilesAsync(string localDirectory, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<long> LogOffsetAsync(string logPath, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default) => throw new NotSupportedException();
    }

    [Fact] public void ARemoteServersLobbyIsReadFromItsHostsLog()
    {
        const string line = "[Info   : Unity Log] Created PlayFab lobby with ID \"LOBBY-9\", ConnectionString \"c\" and owned by \"ENTITY9\"";
        var host = new LogHost(new HostLogResult(HostLogOutcome.Matched, "log", line, TimeSpan.FromSeconds(1), line));
        using var server = Server(Identity);
        string log = CrossplayServer.HostBepInExLog("/srv/runs/run-1/runtime/");
        Assert.Equal("/srv/runs/run-1/runtime/BepInEx/LogOutput.log", log);
        Assert.Equal(new CrossplayLobby("ENTITY9", "LOBBY-9"), CrossplayServer.WaitForLobby(server, host, log, TimeSpan.FromSeconds(5)));
        Assert.Equal((log, 0L), Assert.Single(host.Waits)); // This boot's log, from its start.
        // A failed login on the host, expiry, and a server without crossplay (refused before the host is asked).
        Assert.Throws<WaitFailedException>(() => CrossplayServer.WaitForLobby(server, new LogHost(new HostLogResult(HostLogOutcome.FailureMatched, "log", "Failed to login server to PlayFab backend", TimeSpan.Zero, null)), log, TimeSpan.FromSeconds(5)));
        Assert.Throws<WaitTimeoutException>(() => CrossplayServer.WaitForLobby(server, new LogHost(new HostLogResult(HostLogOutcome.TimedOut, "log", null, TimeSpan.FromSeconds(5), "Register PlayFab server")), log, TimeSpan.FromSeconds(5)));
        var untouched = new LogHost(new HostLogResult(HostLogOutcome.Matched, "log", line, TimeSpan.Zero, line));
        using var steam = Server(Identity.Replace("backend=PlayFab", "backend=Steamworks"));
        Assert.Throws<InvalidOperationException>(() => CrossplayServer.WaitForLobby(steam, untouched, log, TimeSpan.FromSeconds(5)));
        Assert.Empty(untouched.Waits);
    }

    [Fact] public void AServerWithoutCrossplayIsRefusedBeforeItsLogIsRead()
    {
        using var server = Server(Identity.Replace("backend=PlayFab", "backend=Steamworks"));
        var error = Assert.Throws<InvalidOperationException>(() => CrossplayServer.WaitForLobby(server, Path.Combine(_root, "missing.log"), TimeSpan.FromSeconds(1)));
        Assert.Contains("-crossplay", error.Message);
    }

    [Fact] public void AFailedPlayFabLoginEndsTheLobbyWaitAtOnce()
    {
        string log = Log("[Error  : Unity Log] Failed to login server to PlayFab backend");
        using var server = Server(Identity);
        Assert.Throws<WaitFailedException>(() => CrossplayServer.WaitForLobby(server, log, TimeSpan.FromSeconds(30)));
    }

    [Fact] public void ALobbyThatNeverOpensTimesOut()
    {
        string log = Log("[Info   : Unity Log] Register PlayFab server \"Test\"");
        using var server = Server(Identity);
        Assert.Throws<WaitTimeoutException>(() => CrossplayServer.WaitForLobby(server, log, TimeSpan.FromMilliseconds(300)));
    }
}
