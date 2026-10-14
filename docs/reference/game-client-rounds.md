# Plan rules and client rounds

The glue a mod runner reuses from `Valheim.Testing.Game`: rules that refuse a bad plan before anything is copied, `ClientRounds` for a persistence scenario, and the two lifecycle cycles a round can run. The [FullLifecycle example](../../examples/FullLifecycle/README.md) uses all of them ([its rounds](../guides/test-save-restart-rejoin.md#3-write-the-rounds)).

## Plan rules

Call these on a `ServerRunPlan` (or your plan derived from it). Each refuses with an `ArgumentException` that names the field and the fix.

| Rule | Requires | Refuses when |
|---|---|---|
| `RequireScenario(known...)` | The plan's `scenario` is one the runner knows | Any other value |
| `RequireEnvironmentFlag(variable, purpose)` | An explicit fixture opt-in set to exactly `1` | Missing variable, any other value (`true`, `1 `), a key that differs only in case |
| `OnlyForScenario(settings, supplied, scenarios...)` | Settings are supplied only for the scenarios that read them | Settings another scenario reads are supplied |

The toolkit checks the rules every pinned plan follows (output path, launch host, executable, pins, save root, Doorstop and token variables, unknown fields). A mod's plan tests cover only its own rules.

## Client rounds

`ClientRounds` runs a persistence scenario's client rounds over `ClientSession`, `SessionControl` and `PlayerPlacement`. The client joins an owned dedicated server; with the same loop and teardown, a client can instead host its own world ([hosted worlds](game-joins.md#hosted-listen-server-worlds)). You supply the per-round measurement as a delegate.

| Field | Does |
|---|---|
| `Client` | The `ClientRunPlan` section ([client plan fields](game-client-session.md#client-plan-fields)) |
| `OwnedServer` | An `IOwnedServer`; pass the runner's `ServerActor` (`session.Server`), which restarts only that server |
| `WorldUid` | The server's world UID; the client's world pins are verified against it |
| `Rounds` | The round names; default `first`, `after-restart` |
| `Arrival` | A `HeightExpectation` on dry ground the player arrives at each round, or none |
| `ProtectPlayer` | Protect the joined player; `false` leaves it vulnerable |
| `OpenStep`, `ArriveStep` | Your own names for the open and arrive steps |
| `Lobby` | For a crossplay client, how to read the server's lobby before each join ([crossplay](game-joins.md#crossplay-playfab)) |
| `Report`, `Output` | The `ScenarioReport` every step goes through, and the evidence directory |

`Run(server, openClient, measure, afterRestart)` does this for each round:

1. Waits until the server accepts game connections.
2. Joins with `SessionControl.JoinWorld` in one step: devcommands first, the join exactly once, the client's world pins verified, the world awaited. For an owned client it acknowledges cheats on its disposable character (`TestAccess.Ensure`, ClientInWorld) and protects the player unless `ProtectPlayer` is false ([protection changes gameplay](game-player-placement.md#protection-changes-gameplay)).
3. Arrives at `Arrival`, if given, with `PlayerPlacement.Arrive` ([arrival](game-player-placement.md#arrival)). The client's ValheimCLI must then offer `PlayerPlacement.ArrivalCapabilities`, checked once at its menu before any join.
4. Runs your measurement.

Between rounds: a confirmed save, the client's leave, and a restart of only the owned server. The optional `afterRestart` check runs next (for example "the server still has the marker"). After the last round the client only leaves. `Run` returns the server's actor after the last restart.

Inside the measurement, `round.Step(name, action)` records a `{round}: ...` step and `round.Write(name, value)` writes evidence. `round.Server` and `round.Client` are the two actors.

| Evidence or key | Written by |
|---|---|
| `{round}-arrival.json`, `{round}-teleport-trace.json` | The arrival step |
| `{round}-{name}.json` | `round.Write`; refuses to overwrite existing evidence |
| `clientRounds`, `clientRoundsCompleted` in provenance | The run, recording how far it got |
| `clientJoin` (`crossplay` or `address`) in provenance | The join |

The first failure stops the rounds and is rethrown: nothing runs after a failed save, restart or measurement, and that round's evidence stays. The client is closed in every outcome: an owned client's process is stopped, an attached client is detached and left running. A failed close fails a passing run but never hides an earlier failure. A hosting client's teardown is the same, and then releases its world.

## Leave the area and come back

In Valheim 1.0.16 a client keeps a zone's objects while its player is within its simulation distance: objects within *near* rings of zones round the player's zone, distant objects up to *near + far* rings, terrain within *near* rings. A zone is removed once it has been out of range for 4 s and its last near object has gone. Leaving destroys each object's instance; returning recreates it from its saved data, so a value kept only in a component field is gone.

`ZoneCycle.Run(round)` drives this once from a joined, protected client, reading `valheim.observe/zones`, and leaves the client joined for your own re-observation.

| Field | Does |
|---|---|
| `Zones` | The zones of interest; `ZoneId.Around(x, z, radius)` lists the zones round a site |
| `Away` | A `HeightExpectation` on dry ground far enough to unload them |
| `Back` | Where the player returns to |
| `StepTimeout` | Bounds each of the four waits |

Before anything moves, `Run` requires the `Zones` loaded (terrain, and an instance for every saved object), reads the client's simulation distance and refuses an `Away` point whose zone is fewer than near + far + 1 rings from any of them. `ZoneCycle.RingsToLeave(range)` says how far is far enough. Then the four waits:

1. The player arrives at `Away` with `PlayerPlacement.Arrive`; the zone it actually stands in must be far enough too.
2. The client holds nothing of the zones: no terrain, no instance.
3. The player arrives at `Back`.
4. The zones are loaded again.

Expiry names the zones still pending and the last reading; no teleport is repeated. The readings go to `{round}-zone-cycle.json`, whether the cycle passes or fails. A component whose teardown throws shows up in the teardown log scan as `nre-remove-objects` ([patterns](game-log-scan.md#patterns)).

## Log out and log in

In Valheim 1.0.16 the character is saved inside the logout, before the menu loads: the game writes `<name>.fch.new`, moves the previous file to `<name>.fch.old` and renames the new one to `<name>.fch`. It skips the save when its save system blocks character saves (logged "Character save blocked") or the disk is full; the custom data (`Player.m_customData`) is saved with the player and read back when the character next spawns.

`LogoutCycle.Run(round, plan, worldUid)` reads `valheim.observe/custom-data`, has the client leave to its menu, waits for the character file to be rewritten, joins again and requires every key back with its value.

| Field | Does |
|---|---|
| `Capability` | The custom-data observation, `valheim.observe/custom-data` |
| `KeyPrefix`, `Keys` | The keys to read; every one of `Keys` must be set before the logout |
| `CharactersDirectory` | The client's `characters_local` folder; a cloud character is refused |
| `WriteTimeout` | How long to wait for `<name>.fch` to change (SHA256) |
| `RereadInterval` | Re-read interval for filesystems that raise no file events |

`Run` also requires a character the game saves `Local` and the plan's character. After the rejoin the player is protected, as `ClientRounds` joins. `LogoutResult.OldMatchesBefore` says whether the `.fch.old` backup is the file hashed before: one save replaced it, not several. The readings go to `{round}-logout.json`, whether the cycle passes or fails.

Both cycles in one measurement; `site` and the away point are illustrative:

```csharp
var zones = new ZoneCycle
{
    Zones = ZoneId.Around(site.X, site.Z, 16),
    Away = new HeightExpectation(site.X + 640, site.Z, 31.2f), // declared dry ground ten zones away
    Back = site, StepTimeout = TimeSpan.FromSeconds(60),
};
var logout = new LogoutCycle
{
    Capability = "valheim.observe/custom-data", KeyPrefix = "mymod.", Keys = ["mymod.home"],
    CharactersDirectory = Path.Combine(clientSaveRoot, "characters_local"), WriteTimeout = TimeSpan.FromSeconds(10),
};
rounds.Run(server, openClient, round =>
{
    var before = ReadMarkers(round.Client);                       // the mod's own observation
    zones.Run(round);
    round.Step("the markers survive the zone reload", () => Compare(before, ReadMarkers(round.Client)));
    logout.Run(round, plan.Client, worldUid);                     // leaves, checks the write, joins again
});
```

Scan the client's logs at teardown for `nre-remove-objects` and errors during logout ([log scan](game-log-scan.md)).

## Limits

- The runner must see the client's character folder: an owned client's on this machine, or a mounted copy of an attached client's.
- An unchanged hash is all the runner sees of a save that rewrote identical bytes; a moved player or a changed value makes the file differ.
- A world save asks every client to save its profile, so a save between the hash and the leave counts as the logout's own (`OldMatchesBefore` is then false).
- `ClientRounds` makes the client leave after each measurement; the logout cycle leaves the client joined again for it.
- The helper orders and records the steps; each step's in-game behaviour is that of the pieces it calls.
