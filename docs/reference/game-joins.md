# Refused joins, crossplay and hosted worlds

Three join variants beside the plain dedicated-server join. The plain join is on [Joining, protecting and placing a player](game-player-placement.md#join-a-dedicated-server); the rounds that run every variant are on [Client rounds](game-client-rounds.md#client-rounds). All three use ValheimCLI commands the pinned build already has.

## Expect a refused join

`SessionControl.JoinExpectingRefusal(address, character, expected, menuExpectations, timeout)` tests that a server refuses a client. The usual case is a mod's version check refusing a mismatched client with `GameConnectionStatus.ErrorVersion` (3). It:

1. turns devcommands on, as `Join` does, and issues `cli_extension valheim.session/join <host:port> <character> [password-variable]` once;
2. requires ValheimCLI's structured failure with code `join_failed`, which ValheimCLI reports when the new connection ends in an error status;
3. re-pins the client with `menuExpectations` (plugins only, such as `ClientRunPlan.MenuExpectations`) and reads `valheim.session/state` until the client is back at its menu (phase `menu`, no world);
4. reads `cli_connection_status` once (`OK: connectionStatus=ErrorVersion, server=...`) and requires the expected status.

Nothing is retried. It returns a `JoinRefusal` with the status, its number (`Code`), the server string and the time taken. The client is then at its menu, pinned; check that the server accepts a matching client with a normal `Join`.

| Fails when | What you see |
|---|---|
| The join succeeds | The client is in the server's world: leave it before another join |
| ValheimCLI reports another code | `character_unavailable`, `transition_timeout`, `load_error`, ... |
| The client is not back at its menu within `timeout` | The wait expires |
| The refusal has another status | The expected and read statuses are named |

`GameConnectionStatus` has the game's names and numbers:

| Number | Name |
|---|---|
| 3 | `ErrorVersion` |
| 4 | `ErrorDisconnected` |
| 5 | `ErrorConnectFailed` |
| 6 | `ErrorPassword` |
| 7 | `ErrorAlreadyConnected` |
| 8 | `ErrorBanned` |
| 9 | `ErrorFull` |
| 10 | `ErrorPlatformExcluded` |
| 11 | `ErrorCrossplayPrivilege` |
| 12 | `ErrorKicked` |

`ConnectionStatusReading.Read(actor)` reads the status at any time. In Valheim 1.0.16 a refused client keeps its status at the menu until its next join starts.

**Limit.** The game can overwrite an error with `ErrorDisconnected` when the connection closes after it. Check the status another mod's refusal leaves in a native run before you expect it.

## Crossplay (PlayFab)

Clients join a crossplay server by its PlayFab id, not its address.

- **Server.** `"crossplay": true` in a `ServerRunPlan` makes the runner launch the dedicated server with `-crossplay` (`LaunchArguments`) and record `crossplay` (`true` or `false`) in every report's provenance. `-crossplay` in `arguments` is refused for every plan the runner reads.
- **Port rule.** A lobby is keyed by the server's public IP and game port. Two crossplay servers behind one public IP need different game ports, or the second takes the first one's joins. A crossplay plan names exactly one `-port N` (1024 to 65535), never the default 2456.
- **Separate fixtures.** Keep a crossplay fixture server separate from a Steam one. A client remembers each server's mode in its recent-server list.
- **No password.** Run the fixture server private without a password (`-public 0`, no `-password`). The client's join command, `cli_connect_playfab_user`, takes a password as command text, which would be recorded.
- **Lobby.** `CrossplayServer.WaitForLobby(server, CrossplayServer.BepInExLog(runtime), timeout)` first reads `cli_multiplayer_identity` (`OK: steamId=..., playFabLoginState=..., playFabId=..., backend=PlayFab, gameState=..., connectionStatus=..., isServer=True, isOpenServer=True, server=...`) and refuses a backend other than `PlayFab`. It then reads this boot's server log from its start for `Created PlayFab lobby with ID "...", ConnectionString "..." and owned by "<id>"`. A failed PlayFab login ends the wait at once. The owner id is what the client joins; `playFabId` can be `unavailable`, and when present it must equal the owner. BepInEx copies the game's lines into `LogOutput.log` by default; otherwise pass the Unity log the plan's `-logFile` names. For a server on another machine, `ServerActor.Lobby(boot)` waits for the same line in the host's log.
- **Client.** `"crossplay": true` in a `ClientRunPlan`, with no `join` and no `passwordVariable`. `SessionControl.JoinCrossplay(remotePlayerId, character, worldUid, menuExpectations, timeout)` joins from the idle menu: devcommands on, then `cli_select_character <character>` (`OK: Selected character '...'`) and `cli_connect_playfab_user <id>` once (`OK: PlayFab user join started for <id> using ...`). The command only starts the join, so it re-pins with the menu pins and reads the session state until the client is connected in `worldUid` with its player ready. A load error, another world, or a return to the menu with an `Error...` status after the join started fails it; a status left from an earlier join does not count. The world can load before the menu pins are checked, and a strict client then refuses them (`world: ... is loaded but not listed (strict)`). Pass `worldExpectations` (`ClientRunPlan.WorldExpectations(worldUid)`) and the client is re-pinned to that world once, on that refusal only; `ClientRounds` passes it.
- **Linux server.** A Linux dedicated server needs `libpulse0` and `libpulse-mainloop-glib0`: the game's PlayFab library (`valheim_server_Data/Plugins/libparty.so`) loads them. Without them Unity logs `Failed to open plugin`, the first PlayFab call fails with `DllNotFoundException: libParty.so`, and the server retries its network every 30 s without a lobby. The [Linux server image](../../docker/linux-server/README.md) has them. A crossplay `PinnedServerRun` checks this before any server starts, in `validate` too, as the step "the server host can load crossplay's libraries". `CrossplayLibraries.RequireAsync` runs the host's `ldd` on the server's `libparty.so` with the launch's library path. It refuses a missing library by name, with its Debian or Ubuntu package and the `apt-get install` line, a runtime without `libparty.so`, and a host without `ldd`; it records `crossplayLibraries` in the report. `HostServer.StartAsync` makes the same check for any launch with `-crossplay`.
- **Stopping.** A killed server leaves its lobby active for hours; a later join on the same IP and port, or the same PlayFab identity, can reach the dead lobby and fail with `ErrorConnectFailed` (5). Owned servers are [asked to quit first](game-server-session.md#stopping-owned-processes). On a clean quit the game saves, logs `Unregister PlayFab server ...` and asks PlayFab to mark its lobby inactive; it logs `Deactivated PlayFab lobby <id>` only if PlayFab's reply arrives before the process ends. A crossplay `PinnedServerRun` ends with the step "every boot quit cleanly and retired its crossplay lobby" (`CrossplayServer.RequireLobbiesRetired`): every boot, restarts included, must have quit cleanly, and its kept logs (BepInEx's, the `-logFile` file, the process output) must show the lobby it created and the `Unregister` line. A killed boot fails the run, and so do logs without any lobby line. `crossplayLobbies` lists each boot's lobby and whether PlayFab's confirmation was logged. The lobby line is in the log the game writes to: the `-logFile` file when the plan passes one (`ServerRunPlan.GameLogFile`); the BepInEx log may not carry it.
- **Rounds.** `ClientRounds` with a crossplay client needs `Lobby`, for example `server => CrossplayServer.WaitForLobby(server, log, timeout)`. It reads the lobby before each round's join, because a restarted server opens a new one, and records `clientJoin` (`crossplay` or `address`).

## Hosted (listen-server) worlds

A client can host a world from its menu instead of joining a server. The host is the server of its world and also has a local player: its session state reads `server` true and `dedicated` false, so a broadcast RPC runs the server and client handlers in one process. The `hostWorld` section of a [`ClientRunPlan`](game-client-session.md#client-plan-fields) describes the world:

| Field | Meaning |
|---|---|
| `world` | The fixture as a pinned directory holding one world, in either layout below |
| `worldUid` | The fixture world's exact UID; the game silently creates a fresh world when the named one is missing |
| `crossplay` | Host a crossplay world |
| `saveDirectory` | The client's data directory, which holds `worlds_local`; default below |
| `saveSeconds` | The confirmed save's timeout, 1 to 600 (default 120) |

The fixture holds Valheim 1.0's chunked save, one `<name>/` directory at its root with `_main.<n>.fwl2` and its chunks, or the older pair, `<name>.fwl` at its root with `<name>.db` beside it. Every entry is named for the world; the name is at least 3 characters and one token. `saveDirectory` defaults to this user's: `AppData/LocalLow/IronGate/Valheim` on Windows, `Library/Application Support/IronGate/Valheim` on macOS. Set it when a Windows client runs as another user. A native macOS hosted run requires the signed-in user's default directory; a custom path or `-savedir` is refused before fixture placement.

`ClientRunPlan.Validate` refuses a hosting client with `join`, `passwordVariable` or the client-level `crossplay` ("A hosting client (hostWorld) joins no server: leave out join, passwordVariable and crossplay; hostWorld.crossplay hosts a crossplay world."), one that is also `joinsHost`, and one whose ValheimCLI host is not this machine ("A hosting client runs on this machine, where the runner places the fixture world in its save directory; its ValheimCLI host is 127.0.0.1."). It also refuses a fixture that does not name one world, a missing or inexact `worldUid`, and, when pinned, a fixture without hashes. Unknown fields are refused.

Every hosted run places the fixture through the one journalled placement in `Valheim.Testing.GameSessions` (a campaign's client on its host; `valheim-test start`, a session's `HostingClientActor` and `HostedClientRounds.Run` on this machine). It first refuses if anything named for the world is already in the client's `worlds_local`: `<name>`, `<name>.*` or `<name>_backup*`, in any case. That is what the game reads and writes for a world (its `.fwl`, `.db` and `.old` copies, Valheim 1.0's chunked `<name>/` folder and its `_backup_auto-` backups), and Windows and macOS folders ignore case. So your own world is never overwritten and a refused run copies nothing. It then copies and verifies the fixture into the output directory, keeps that copy, journals the placement with the fixture's world UID, and places the fixture in `worlds_local`. `Collect` moves the world, with what the game wrote for it (saves, `.old` files, backups: the same entries the refusal looks for), into the run's folder (`<runId>/host-world` in the output on this machine) and fetches it into `host-world` in the output directory.

`HostWorlds.Start(host, plan, worldName, timeout)` starts from the idle menu:

1. turns devcommands on (ValheimCLI refuses its save and leave without them) and selects the plan's character (`cli_select_character`);
2. sends `cli_start_host_world <name> --public false --crossplay true|false` once and requires the reply `OK: Starting hosted world '<name>' using <character>; open=true, public=False, crossplay=<True|False>, backend=<PlayFab|Steamworks>, passwordSet=False`;
3. re-pins the client with its menu pins and waits for the plan's world UID to be ready; a fresh world has another UID and fails here, naming both UIDs as the wrong world;
4. verifies the world pins, then waits for the host's player and protects it (`WaitForWorld`). A world that loads between two reads makes the strict menu pins refuse it ("loaded but not listed"); the world pins are then checked at once.

`HostWorlds.Restart` leaves the world, which saves it, then starts it again. The host's world is never restarted as a separate process.

`ClientRounds.Run(openClient, measure)` runs a client whose plan has a `hostWorld` section; it is `HostedClientRounds.Run` in `Valheim.Testing.GameSessions` (add that package and `using Valheim.Testing.GameSessions;`), which places the world through the journalled placement above. It refuses `OwnedServer`, `Lobby`, `Arrival` and `ArriveStep`, which only a joining client uses, and its `WorldUid` is the fixture's (leave it out). It first runs the plan's `Preflight(CliCapabilities.HostedRounds)` ([what it catches](game-preflight.md#what-the-preflight-catches)), so a wrong fixture, install or ValheimCLI set stops the run before the fixture is copied or the client started. It then places the world, opens the client (owned or attached), requires `CliCapabilities.HostedRounds` live and runs each round, bounding each start by the plan's `joinSeconds`:

1. hosts the world, a restart from the second round on, and protects the host's player unless `ProtectPlayer` is false;
2. runs the mod's measurement; `round.Server` and `round.Client` are the same host;
3. between rounds, saves with a confirmed `valheim.session/save` on the host;
4. the host leaves to its menu.

The report's provenance records `role` = `host`, `hostWorld`, `hostCrossplay`, `hostRounds`, `hostRoundsCompleted` and `cliPreflight`. The client is closed in every outcome, then the world is moved into the evidence (`hostWorldEvidence`). After a failure an attached client, or an owned client that did not stop, may still host the world; it is left in place and named in `hostWorldLeftInPlace`. Remove it once that client has left it. Every hosted run journals that placement before it is made, with the fixture's world UID, and a fixture already named in the client's worlds is refused before anything is copied. After a killed run (`valheim-test start --hold`, or rounds you drive), `valheim-test env status` lists the world and `valheim-test env recover` moves it out of the client's worlds into the run's folder (`<runId>/host-world` in the arm's evidence or the rounds' output), but only when every metadata file of an entry named for the world there (an `<entry>/_main.<n>.fwl2` or `<entry>.fwl`) states the fixture's world UID. A same-named world with another UID, or with no readable metadata, stays in place.

A no-game test keeps its hosted world in a private directory with [`FakeClientDataDirectory`](../packages/Valheim.Testing.Game.md#test-fakes). Hosting clients in a session are on the [GameSessions page](../packages/Valheim.Testing.GameSessions.md).
