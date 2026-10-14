# World observations

The read-only probes and observations a system test reads from ValheimCLI's built-in commands: terrain, global keys, synced config, a client without the mod, and a dungeon's saved rooms.

The pinned `valheimCLI.dll` provides `valheim.observe/*` and the other built-in commands on every actor. No other command DLL is needed. A mod-specific action or observation goes into the mod's [test adapter](../packages/Valheim.Testing.Adapter.md). Registered content and Harmony patches are on [Content and Harmony census](game-content-census.md).

## Terrain, surface and paint probes

| Type | Does | Fails or refuses when |
|---|---|---|
| `TerrainProbe` | Compares up to 256 declared height expectations (`HeightExpectation(X, Z, Height)`) against one explicit layer, `generator` or `loaded-ground`, and reports every residual | A sample exceeds the tolerance, or an observation is incomplete (no heightmap loaded) |
| `SurfaceProbe` | Compares loaded heightmap vertices and the client's own mesh colliders separately; `Supported` requires a stationary, grounded local player | The player is flying, attached, teleporting, or only at the right position |
| `PaintProbe` | Compares loaded paint channels at declared points | A sample exceeds the tolerance |
| `SiteSearch` | Finds test sites on a fresh world from generator heights read through ValheimCLI; the mod's own rule decides what a site is (`Find(server, worldUid, grids, enough)`, `Around(centreX, centreZ, spacing, side)`) | No site matches: `SiteNotFoundException`, with how many samples it read |

A read-only game check of each is in `examples/ObserveCheck`: the [`ground`](../../examples/ObserveCheck/README.md#ground-generator-or-loaded-ground-heights), [`surface`](../../examples/ObserveCheck/README.md#surface-a-clients-ground-collider-and-support) and [`paint`](../../examples/ObserveCheck/README.md#paint-loaded-paint-channels) probes. [SharedWorld](../../examples/SharedWorld/README.md) is a no-game example with declared expectations. To record a site's ground, collider and paint as evidence instead of asserting on it, use a [terrain site snapshot](evidence.md#terrain-site-snapshots); for its objects, an [area object snapshot](evidence.md#area-object-snapshots).

## Global keys

In Valheim 1.0.16 a key change goes to the server, which applies it and sends its whole list to every client. Each client replaces its list with it. Keys are stored lower case, as `name` or `name value`.

```csharp
await GlobalKeyFixture.Apply(server, client, changePath, listPath, set: ["defeated_eikthyr"], remove: ["nomap"], timeout, interval);
```

`Apply` issues each change once through the fixture command, which refuses unless `FixtureGate` allows fixture commands. It checks that the server kept the change, then waits until the client lists exactly the server's keys. On expiry it names the keys the client lacks and the ones only it has.

| Piece | Does |
|---|---|
| `Set(server, changePath, name, value)` | Sets one key on the server |
| `Remove(server, changePath, name)` | Removes one key |
| `Read(actor, listPath)` | Lists a process's keys |
| `Compare(server, client)` | A `GlobalKeyDifference`: `MissingOnClient`, `ExtraOnClient` |
| `WaitForClient(server, client, listPath, timeout, interval)` | Waits until the client lists the server's keys |

The game's `setkey` console command is not used: it refuses a key that is not a world-modifier setting unless the game already counts as cheated. ValheimCLI's `cli_check_global_key` is not used because it answers for one key at a time and cannot show a key only the client has.

A world-modifier key (one the world-modifiers menu sets, such as `nomap`) set or removed through `GlobalKeyCommands.Change` is also written into, or removed from, the world's saved starting global keys, as the game does. Other keys, boss keys for example, are saved only in the world's own key list.

## Synced config

```csharp
var value = SyncedConfig.Read(actor, "valheim.observe/config", guid, section, key);
```

`Read` returns the entry's current value in that process, as BepInEx would write it to the file (`true`, `5`, `1.5`), with its type and default. It reads the `ConfigEntry` itself on each side; how the mod syncs it is the mod's business. Sections and keys are percent-encoded on the way (`Server%20Settings`, `SyncedConfig.Token`), because extension arguments are single tokens.

| Member | Does | Fails when |
|---|---|---|
| `WaitForValue(client, ..., expected, timeout, interval)` | Waits for a value to arrive, for example after an admin change on the server | At once, when the plugin is not loaded or the entry does not exist |
| `RequireSame(server, client)` | Compares both sides | The values differ |

Only entries in the plugin's own `Config` are found. Nothing here writes config.

## Vanilla clients

A server-side mod that says clients do not need it fails quietly on a client without it. In Valheim 1.0.16 an object whose prefab only the mod registers is never created there (the game logs "Missing prefab hash" and skips it), a per-object RPC without a handler logs "Failed to find rpc method", and objects can throw while they unload.

Give the client BepInEx, ValheimCLI and the mod's test adapter, pin the mod `absent` in the client plan, and run `VanillaClientCheck` as a [`ClientRounds`](game-client-rounds.md#client-rounds) measurement (`measure: check.Measure`):

| Field | Meaning |
|---|---|
| `Capability` | The client's unresolved-prefab census command: `valheim.observe/unresolved-prefabs` on the default plugin |
| `Points` | Each point the client arrives at, then takes a census of the objects within `Radius` |
| `Radius` | The census radius in metres |
| `KnownPrefabs` | Names to resolve an unresolved hash to a prefab in the failure |
| `ClientLogs` | The client's logs so far, scanned after the census |

A census is complete once every zone it touches has loaded. The census leaves out the client's own player object, so an empty census proves nothing. The check fails on any unresolved hash, naming it and, when one of `KnownPrefabs` hashes to it, the prefab. With `ClientLogs` it then fails on `missing-prefab-hash`, `rpc-method-missing` and `nre-remove-objects` lines. Pass `VanillaClientCheck.LogClassifications` to the run's [teardown scan](game-log-scan.md) as well, so those lines fail the whole run wherever they appear.

Evidence: `{round}-vanilla-client-{n}-arrival.json`, `{round}-vanilla-client-{n}.json`, `{round}-vanilla-client-logs.json`. `StableHash.Of(name)` is the game's prefab hash.

A client loads zones, and so objects, only within about near simulation distance x 64 + 32 m of its player (about 160 m at default settings). Keep `Radius` inside that: a larger census never becomes complete on a client.

## Dungeon rooms

In Valheim 1.0.16 a dungeon generator saves its rooms as one byte array, `roomData`: an int32 count, then per room the room prefab's name hash, its position and its rotation as Euler angles (little-endian int32 and float32). Worlds saved before that keep `rooms` and `room<i>`, `room<i>_pos`, `room<i>_rot` fields, which the game reads when `roomData` is absent; the observation reads either and says which (`Format`).

The game gives the interior its environment with a 64 m wide box centred on the location's zone, 5000 m above the location. A room reaching outside that zone is partly outside the interior.

```csharp
var dungeons = DungeonRooms.Read(server, path, x, z); // radius defaults to 64
foreach (var dungeon in dungeons) DungeonRooms.RequireWithinZone(dungeon, sizes);
```

| Member | Does |
|---|---|
| `Read(server, path, x, z)` | Lists the generators near a location's ground position, each paired with the location nearest (x, z) in the 3 x 3 zones around it; an unvisited dungeon has no saved generator, so the list is empty |
| `InteriorOffset` | The generator's position minus the location's: its offset in the location prefab, turned by the location's rotation |
| `RequireWithinZone(dungeon, sizes)` | Fails naming every room past the zone's edge, a generator in another zone than its location, a dungeon without saved rooms, and one without a location |

Give room sizes (`Room.m_size`, by room prefab name, as `RoomSize`) to check each room's footprint turned by its yaw. A generator displaced into a neighbouring zone still pairs with its location and fails the zone comparison.

## Limits

- Observations read. Nothing on this page changes the world except `GlobalKeyFixture`'s guarded fixture command.
- A room without a size is checked by its centre only, which cannot see an oversized room.
- A census larger than the client's loaded radius never completes.
- Neither a replay of a terrain capture nor a synthetic plane alone establishes the game's terrain conversion or client physics.
