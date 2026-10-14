# Joining, protecting and placing a player

`PlayerPlacement`, `TestAccess` and `SessionControl` join a client to a dedicated server, protect its player and put that player on the ground at a declared point. Every step waits for an in-game event and never repeats a mutation.

## PlayerPlacement

`PlayerPlacement` works on the `GameActor` of a joined client (and, for a server teleport, of its server).

| Method | Does | Fails when |
|---|---|---|
| `SkipIntro(client, timeout)` | Ends a new character's first-spawn intro (`cli_skip_intro`, not a cheat) and waits for the player to respawn on the ground; returns whether an intro was running | The respawn does not arrive within `timeout` |
| `Protect(client)` | Turns on god, ghost and debug mode once (`cli_set_player_safety true`, which sets rather than toggles); requires all three read back and `ghostReplicated=True` | The game does not confirm all three, or the reply lacks `ghostReplicated` |
| `SetFly(client, on)` | Turns debug fly on or off (`cli_fly on|off`) and requires the state read back | The game does not read it back |
| `Arrive(server, client, point, timeout)` | One teleport, then in-game waits until the player stands supported at `point`; returns a `TeleportArrival` (`Support`, `Trace`, `Timing`, `Target`) | See [Arrive in detail](#arrive-in-detail) |
| `PeerCount(server)` | Reads `cli_peers` once and returns the peer count | The count line disagrees with the peers listed |
| `OnlyPeer(server)` | Reads the same listing for the one connected peer | None or several peers are connected |
| `RequireSupported(client, point)` | Three readings half a second apart, each grounded and stationary on the declared ground | A reading differs: a `SupportException` carrying every reading |

Ghost mode is written to the player's ZDO, so every process that runs ValheimCLI reads it; every joined client, attached ones included, needs the ValheimCLI build `cli-dependency.json` pins or later, or `Protect` and the join fail.

A flying player is never supported. `Arrive` and `RequireSupported` refuse (an `InvalidOperationException`, not a `SupportException`) as soon as a reading shows the player flying. Turn fly off before measuring.

### Arrive in detail

1. Skips the intro (`skipIntro: false` leaves it; the readiness wait then also requires the player grounded) and refuses a player that one reading shows flying.
2. ValheimCLI waits until the game would take a teleport: it silently drops one during the Valkyrie ride and within 2 s of a spawn or teleport.
3. Arms a one-hop trace, then teleports the server's only connected player with a character once, just above the point, with the game's own teleport. None or several such players is refused.
4. With no server (`Arrive(null, client, ...)`), the client teleports its own player instead (`cli_teleport`). That needs the client's test access (`SessionControl.JoinWorld` gives an owned client that) and ValheimCLI's `AllowOnServerClients` on a joined client. Use it to place one of several players.
5. Waits in game for the teleport to finish with a ready floor and for the player to stand supported there. A teleport reply is never taken as arrival.
6. With `loadedGround: true`, the point's height is only the teleport target: the client measures the loaded ground once the floor is ready, and the landing must be supported at that height. It also needs `valheim.world/terrain` on the client. A terrain check leaves it off.

`timeout` is at most 600 s, and each CLI wait at most 120 s. A timeout, a finish without a ready floor or an unsupported landing fails, and nothing is repeated.

The client's ValheimCLI must offer `PlayerPlacement.ArrivalCapabilities`: `valheim.session/teleport-signals`, `valheim.world/player-support-wait` and `valheim.world/player-support`. List them in the client plan's `capabilities` to have them checked before launch ([client plan fields](game-client-session.md#client-plan-fields)).

## Join a dedicated server

ValheimCLI refuses a join (a mutating extension command) until devcommands are on, so `SessionControl.Join` turns them on first. `EnableDevcommands()` does the same on its own. Both are `TestAccess.Ensure(actor, TestActorRole.ClientMenu)`: read `cli_access`, toggle once only if devcommands are off, and verify. Cheats are acknowledged once the client is in a world, and only for an owned client; an attached operator's client keeps devcommands only.

`SessionControl.JoinWorld(plan, worldUid)` is the one join of a client plan to an owned dedicated server. `ClientRounds` and `LogoutCycle` use it ([client rounds](game-client-rounds.md#client-rounds)):

1. Devcommands on, then the join exactly once: by address, or into the server's lobby for a crossplay plan ([crossplay](game-joins.md#crossplay-playfab)).
2. The client's world pins are verified and the world awaited.
3. For an owned client, `TestAccess.Ensure(..., ClientInWorld)`, which also requires `AllowOnServerClients` when the player is to be protected; a client staged without it is named there.
4. The player is protected; `protectPlayer: false` leaves it.
5. For an owned client, the world pins are published for `valheim-test cli --phase world` ([NativeSmoke](../packages/Valheim.Testing.NativeSmoke.md)). `JoinHost` and `HostWorlds.Start` do the same. The pins stay until the actor's next transition or pin verification removes them.

`SessionControl.WaitForWorld` protects the local player once the world is ready (`PlayerPlacement.Protect`), so a test that forgets to cannot lose its character to a fall, the water or a mob. A protection the game does not read back fails the wait. A dedicated server has no local player and is left alone. A game hosting its own world reports ready before its player spawns, so the wait goes on, within the same timeout, until that player exists, and times out saying so ([hosted worlds](game-joins.md#hosted-listen-server-worlds)). On a joined client the command needs `AllowOnServerClients`, like the join.

A console reply is not a successful mutation. The game's cheat confirmation can refuse a command such as `cli_arrive` after devcommands are on, and the transport still returns that text as a completed request. `GameActor.Execute` therefore requires an accepted reply by default ([strict pins from tests](game-actors-and-pins.md#strict-pins-from-tests)); a step still requires the command's documented result, or observes its effect.

## Protection changes gameplay

In Valheim 1.0.16, protection changes what the game does:

- Monsters neither notice nor target a player in ghost mode.
- An egg hatches only near a player who is not in ghost mode.
- A creature hit by a player in god or ghost mode is marked cheated, which marks the items it drops.

| Need | Use |
|---|---|
| A player creatures notice but cannot kill (AI, aggro checks) | `"targetable": true` in the client plan, or `Protect(client, targetable: true)` (`cli_set_player_safety true targetable`): god and debug mode without ghost mode |
| A vulnerable player (combat, taming, hatching, loot checks), or an operator who manages these modes | `WaitForWorld(..., protectPlayer: false)`, then call `PlayerPlacement.Protect` yourself for the steps that should be protected |

Declare support points on dry ground. A player standing in water is not grounded at the ground's height, so the check fails and tells you nothing about the mod. [ObserveCheck](../../examples/ObserveCheck/README.md#surface-a-clients-ground-collider-and-support)'s `surface` probe uses `RequireSupported`.

## Arrival

Each `ClientRounds` arrival writes `{round}-teleport-trace.json` from the one-hop trace:

| Field | Meaning |
|---|---|
| `RequestedMs` | Milliseconds from arming to the teleport request |
| `MovedMs` | To the player's move; -1 when the player was already within 5 m of the point |
| `AreaReadyMs` | To the area's readiness |
| `FloorReadyMs` | To the ready floor |
| `DoneMs` | To the teleport's end |

A phase that did not occur is -1. The file also holds the final position and the original reply. Malformed fields, another trace id, a missing ready floor or an unsupported landing fail the run.

## Limits

- God mode keeps health at 1 or more; it does not stop hits or knockback.
- Fly is for review and visual steps only; a flying player is never a supported landing.
- Nothing is repeated after a timeout, a refused teleport or an unsupported landing.
- `ClientSession` can tell only that Steam runs, not that it is signed in ([client sessions](game-client-session.md#limits)).
