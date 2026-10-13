# Networking in the doubles

Reference for `ZPackage`, routed and per-object RPCs, console commands, the join handshake and peer identities in `Valheim.Testing.Doubles`. The core page is [Valheim.Testing.Doubles](../packages/Valheim.Testing.Doubles.md).

## Purpose

The doubles run a mod's network code in-process. A handler receives the bytes the game would send, a peer connects only through a linked socket double, and routing follows the game's rules without real timing, loss or reordering. Each table says what the doubles do and where they throw or differ from the game. Members the game lacks are marked in the [member index](../packages/Valheim.Testing.Doubles.members.txt); use them in tests only. Setup and `ValheimWorldScope` are on the [core page](../packages/Valheim.Testing.Doubles.md), world state (ZDOs, global keys, terrain) on [doubles-world](doubles-world.md), Unity objects, config and Harmony on [doubles-unity](doubles-unity.md).

## ZPackage

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Write/Read pairs | Every pair of the game, with its byte encoding | — |
| Numbers | Little-endian | — |
| Strings | 7-bit length prefix, UTF-8 | — |
| Byte array, nested package | Int length prefix | — |
| `ZDOID` | Creator's session id, then a uint | — |
| Vectors, quaternions, small rotations, item counts | The game's layout | — |
| Stream position | One stream, written and read at the same position | Reading without `SetPos(0)` after a write reads past your data |
| Other members | `GetArray`, `GetPos`, `Size`, `Clear`, `Load`, `byte[]` and base64 constructors | — |
| Compression | The game's gzip level (Fastest); each side reads the other's stream | Compressed bytes can differ by header bytes; compare what is read back |
| `ZRpc.Serialize(args, ref pkg)` | Writes arguments with the routed-RPC type table | — |
| Version strings | `ZNet.VersionString` and `ZNet.NetworkVersion` default to `Valheim.Testing.Doubles.DoubledGame.Version` and `NetworkVersion` | — |

## Routed RPCs

`ZRoutedRpc` carries arguments through a `ZPackage`, so a handler gets copies.

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Argument types | int, uint, long, float, double, bool, string, `ZPackage`, `List<string>`, `Vector3`, `Quaternion`, `ZDOID`, `HitData`, `ISerializableParameter` | Any other type (an enum, a byte, a `byte[]`, a `Vector2i`): `ArgumentException` naming the argument; the game skips it silently |
| Handler parameters | Read back by the handler's own parameter types | An int sent to a long parameter, fewer arguments than parameters, or an unreadable parameter type: `InvalidOperationException` naming the parameter |
| Type check | Exact | An int read as a uint, or a subclass sent to a base-class parameter: refused, where the game misreads |
| Extra arguments | Trailing extras are ignored, as in the game | — |
| `HitData` | The game's fields, defaults and layout (a flags word, then non-default values) | Damage is not calculated; `Skills` has only its `SkillType` ids |
| `Everybody` or this peer | Runs the local handler at once, before anything is sent | A "client-only" broadcast handler also runs on the server; on a listen-server host its client branch runs too |
| Unregistered name | Dropped without an error, recorded in `ZRoutedRpc.Dropped` | — |
| Registering twice | Throws, as in the game | A mod registering on the default static `ZRoutedRpc.instance` more than once: install a fresh one per test with `ValheimWorldScope.WithNetwork()` |
| Handler arity | Up to six arguments after the sender | — |
| `IsRegistered(name)` | On `ZRoutedRpc` and `ZNetView`; test-only | Mod code that calls them fails against the game |
| No target | The server calls itself; a client calls its first ready peer, or `Everybody` with none | — |
| `GetServerPeerID()` | Returns that target; public here | Private in the game |
| `PeerId` | `ZNet.GetUID()` unless `SetUID` sets another | — |
| `ZNet.GetUID()` | The session id (`ZDOMan.GetSessionID()`, 1 by default and with no `ZDOMan`), or the side's `ZNet.Uid` in a two-sided test | Never 0, which is `Everybody` |
| Sending | Server: to the target, or a broadcast to every ready peer except its sender. Client: to its ready peers | A peer with `Ready = false` has `m_uid` 0 and receives nothing |
| `Sent` | Each peer's message as that peer decodes it | — |
| `Invoked` | Every call as made | — |
| `Deliver(sender, method, args)` | Handles a call arriving at this peer | — |
| `Receive(sender, target, zdo, method, args)` | Handles any arriving call, including one the server relays | — |
| Message size | Steam's 512 KiB limit on Steam only | PlayFab's own send limits are not modelled |

Deliver a server broadcast to a client by hand. `serverId` and `keys` are illustrative:

```csharp
ZRoutedRpc.instance.Deliver(serverId, "GlobalKeys", keys);
```

The global-key handlers themselves are on [doubles-world](doubles-world.md).

## Per-object and Jotunn RPCs

| Area | The doubles do | Fails or differs when |
|---|---|---|
| `ZNetView.Register`, `Unregister`, `InvokeRPC` | As in the game, through the routed RPCs with the object's ZDO id | `Register` takes up to six arguments |
| `InvokeRPC(method, ...)` | Goes to the object's owner | An object without an owner broadcasts |
| `InvokeRPC(ZNetView.Everybody, ...)` | Goes to everyone | — |
| Target view | The view whose ZDO is in `ZDOMan.instance` and, with a scene installed, whose object is live in it | A view around a bare ZDO, with no game object, is found through its ZDO |
| Name the view did not register | Logs `Failed to find rpc method <hash>` through `ZLog`, lands in `ZRoutedRpc.Dropped` | — |
| Missing object | Dropped silently, lands in `ZRoutedRpc.Dropped` | — |
| Jotunn RPCs | Kept by name in `NetworkManager.Instance.Rpcs`; record what they send | — |

## Console commands

| Area | The doubles do | Fails or differs when |
|---|---|---|
| `Terminal.ConsoleCommand` constructor | Registers the command under its lower-case name | — |
| `Terminal.TryRunCommand` | Runs it with that terminal as `args.Context`; prints an unknown command or a failed failable action | — |
| `Terminal.Output` | Holds what was printed | — |
| Cheat command | Needs devcommands (`Terminal.m_cheat`) on the server, then acknowledged cheats (`Achievements.CheatedAtAll`, the game's `confirmcheats`) | Otherwise prints `'x' is not valid in the current context.` or `That command is a cheat, please enter 'confirmcheats' ...` and does not run |
| Cheat gates | Start closed, as on a fresh dedicated server; `ValheimWorldScope.WithCheats()` opens both, restored on dispose | A command marked `isCheat: true` is refused in a plain scope |
| Server-only command | Needs the server | — |
| Invalid remote command on a client | Recorded in `ZNet.instance.RemoteCommands` as sent to the server | — |
| Admin checks | Not modelled | — |

An empty command table per test is `WithCommands` on the [core page](../packages/Valheim.Testing.Doubles.md).

## Peer connections and the join handshake

These doubles add global types (`ISocket`, `SocketDouble`, `ZSteamSocket`, `ZPlayFabSocket`, `ZRpc`, `SyncedList`, `OnlineBackendType`) and namespaced ones (`Steamworks.CSteamID`, `Splatform.Platform`, `Splatform.PlatformUserID`). If your test project has its own stubs with these names, delete them or add members through `partial`.

| Area | The doubles do | Fails or differs when |
|---|---|---|
| `ZNetPeer.m_rpc`, `ZNetPeer.m_socket` | A `ZRpc` and an `ISocket` a mod's handshake or admin check reads | — |
| `SocketDouble.Link(a, b)` | Joins two ends in-process; a message arrives when the receiving side's `ZRpc.Update(dt)` or `ZNet.UpdatePeers(dt)` runs | — |
| `ZRpc.Invoke` | Writes the method's hash and the arguments (routed type table) and sends them | Nothing is sent once the connection is closed |
| `ZRpc.Register` | Replaces an earlier handler of the same name | — |
| Call without a handler | Ignored, kept in `Dropped` | — |
| Short package | A package ending before the parameters are read stops `Update` with `ErrorCode.IncompatibleVersion`; `ZNet` reports `ErrorVersion` | A peer sending fewer fields than the handler reads |
| Exception in a handler | Logged only, as in the game; kept in `Exceptions` | Includes reading past the end of a `ZPackage` argument |
| `ZRpc.Deliver(method, args)` | Handles one call as if it had arrived | — |
| Pings, timeout | Not modelled; a connection sends only what its side invokes | — |

The handshake runs between two `ZNet` objects: the server is `ZNet.instance`, the client a second `ZNet` with its own `Uid`. Call `OnNewConnection(peer)` on each side, then `UpdatePeers(0f)` on each in turn.

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Refusals | The game's `Error` codes; the client's `Status` becomes the matching `ZNet.ConnectionStatus` | — |
| Network version | 3 `ErrorVersion` for another `NetworkVersion` | — |
| Ban or permit list | 8 `ErrorBanned` | — |
| Password | 6 `ErrorPassword` when the test sets the server's `RefusePassword` | Salt, hash and the password dialog are not modelled |
| Already connected | 7 `ErrorAlreadyConnected` | — |
| Player limit | Not modelled | 9 `ErrorFull` never occurs |
| A joined peer | Ready under its id | — |
| `OnNewConnection`, `RPC_PeerInfo` | Public, so a test applies a mod's patch as Harmony would: call the prefix before `OnNewConnection`; re-register `"PeerInfo"` on `m_rpc` with a handler that runs the prefix, then `RPC_PeerInfo` | — |
| Version message from the client's `OnNewConnection` prefix | Goes out before the game's ServerHandshake on the same ordered connection, so it reaches the server before the peer info | A mod that sends its version later (in reply to the server's) is turned away by a check that refuses on arrival; a check that holds the peer info passes it |
| Connection status | Per `ZNet` | Static in the game |

[HandshakeTests](../../tests/Valheim.Testing.Doubles.Tests/HandshakeTests.cs) has a mod version check that refuses a peer whose version message has not arrived by the time its peer info does.

## Steam and crossplay identities

| Area | The doubles do | Fails or differs when |
|---|---|---|
| `ZSteamSocket(steamId)` | `GetHostName()` is the bare SteamID64 (`76561198000000002`) | — |
| `ZPlayFabSocket(platformUserId, remotePlayerId)` | `GetHostName()` is the platform-prefixed id (`Steam_76561198000000002`, `Xbox_2535…`); end point `playfab/` plus the PlayFab entity id | — |
| `peer.m_socket` cast to `ZSteamSocket` | Throws `InvalidCastException` for a crossplay peer | Inside an RPC handler the game only logs it |
| `ZNet.instance.IsAdmin(rpc.GetSocket().GetHostName())` | A Steam id matches `Steam_<id>` or the bare id; another platform only `<Platform>_<id>`; the display form `V_<id>` matches too | Comparing the host name with the list's lines misses a Steam peer listed as `Steam_<id>` |
| `m_adminList`, `m_bannedList`, `m_permittedList` | `SyncedList`s (`new SyncedList(entries)`), compared as exact strings | List files are not modelled; nothing is trimmed |
| `ZNet.OnlineBackend` | The game's static `m_onlineBackend`; selects Steam or PlayFab for the handshake | — |
| `Splatform.PlatformUserID` | Parses and formats the ids | — |
| Message sizes | The sockets check none | — |

## Limits

- Networking is in-process. Peers connect only through linked socket doubles; there is no real timing, loss or reordering.
- Not modelled: Steam session tickets and PlayFab authentication (every peer passes), invite keys, the world data in the server's peer info, the game's other connection RPCs, pings, the timeout and the player limit.
- `HitData` damage, console admin checks and PlayFab send limits are not modelled.
- Test real networking with [Valheim.Testing.Game](../packages/Valheim.Testing.Game.md) against a real server.
