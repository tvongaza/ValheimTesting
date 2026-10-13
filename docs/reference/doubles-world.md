# World state in the doubles

What the ZDO, scene, save, terrain, global-key and registry doubles in `Valheim.Testing.Doubles` do, and where they differ from the game. Setup, `ValheimWorldScope` and the role presets are on the [core page](../packages/Valheim.Testing.Doubles.md).

## Purpose

Mod code that reads or writes world state compiles against these doubles unchanged. This page lists the rules they copy from the game and the points where they throw or differ, so you know what a passing test proves. Members marked `[TestOnly]` in the [member index](../packages/Valheim.Testing.Doubles.members.txt) exist only here: call them from tests, never from mod code. RPCs, packages and peers are on [Networking page](doubles-network.md). Unity objects, destruction and config are on [Unity page](doubles-unity.md). The walkthrough is [test logic that uses game types](../guides/test-logic-that-uses-game-types.md).

## ZDOs

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Value keys | Keyed by `GetStableHashCode` of the name; `Set("name", v)` and `Set("name".GetStableHashCode(), v)` are one key | Each value type has its own table; a bool is an int |
| Value types | float, `Vector3`, `Quaternion`, int, bool, long, string, byte array | — |
| Rotation | `GetRotation`/`SetRotation` keep Euler angles | — |
| Destroy | `ZDOMan.DestroyZDO` is queued | `FindObjects` still returns the ZDO until `ProcessDestroyed` |
| Session | `ZDOMan.GetSessionID()` is 1 by default, also with no `ZDOMan` | Never 0 (see [Networking page](doubles-network.md)) |
| Zone ids | Narrow to `short` | — |
| Locations flag | The locations-generated flag is set from a save | The event is not raised |

## ZNetView and the live scene

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Components | `ZNetView` and `WearNTear` are ordinary components; `AddComponent<ZNetView>()` is the same view `View` reads | `GameObject.Networked`, `Health`, `View` and `Wear` are fixture shorthands, not game members |
| `Networked` | Adds a persistent view, as the game's prefabs carry one | `m_persistent` on a plain view defaults to false |
| Wake | A view gets a ZDO of its prefab at the object's position and rotation, owned by this session; the object joins `ZNetScene.Live` | A view that wakes with no `ZDOMan` destroys itself at the end of the frame |
| Assets | A prefab from `ZNetScene.AddPrefab` or `IsAsset`, or a template under an inactive parent, never wakes | Only its copies get a ZDO |
| Loaded ZDO | Set `ZNetView.m_initZDO` (and `m_useInitZDO`) before `Instantiate`; the view takes it, and its scale with `m_syncInitialScale` | `m_forceDisableInit` makes a waking view destroy itself |
| Prefab name | A new ZDO's prefab is `Utils.GetPrefabName` of the object; with `m_syncInitialScale` it carries the authored scale | — |
| Wake order | `AddPrefab` adds the view first, so it wakes first in insertion order | Under `UnityOrder.Reversed` a component added after it wakes before it; script execution order is not modelled |
| Ghost init | A networked prefab instantiated during ghost initialisation leaves its ZDO | It joins no live scene |
| `ZNetScene.Destroy` | Resets the view's ZDO (`GetZDO()` is null), removes the object from the live scene at once | Only ZDOs this session owns are queued; a compiler saves only when owned |
| Plain `Object.Destroy` | Does not touch the live scene, as in the game | The destroyed view stays in `ZNetScene.Live` and `FindInstance` returns it (see [Unity page](doubles-unity.md)) |

## Saves and restarts

`ZDOMan.instance.RoundTripThroughSave()` saves and reloads the world; `zdo.RoundTripThroughSave()` does it for one persistent ZDO.

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Persistence | Only persistent ZDOs are saved; each comes back with no owner and a new id | A stored `ZDOID` no longer finds it |
| Session-only values | Not saved: the game's list (`support`, `vel`, `InUse`) plus every `zdo.AddSessionHash(hash)`; `ZDOMan.SessionOnlyHashes` holds the set | A `WearNTear` support value is recomputed after a restart |
| Empty values | A current save strips nothing else: empty strings, empty byte arrays and identity rotations survive | — |
| `ZDO.SavedWorld.BeforeChunkedSave` | Models the first load of an older world: removes `support`, `burnt0`..`burnt10`, `room<n>_seed`, `<n>_crafterName` | Other conversions and unnamed legacy hashes are not modelled |
| `SavedWorld.BeforeNewSaveFormat` | Also drops empty strings, empty byte arrays and identity rotations, and renames the old spawn keys | — |
| Animator sync | The game stores parameters under `Animator.StringToHash(name) + 438569` (ints for bools and ints, floats for floats), session-only | The sync is not doubled; a mod reading animation state passes the hash it computed |

## Heightmaps and terrain rebuilds

Every type is `partial`. Two `Heightmap` hooks carry a mod's terrain logic into the rebuild: `ModBaseHeight` (a biome blend, say) and `ModTerrainPass` (the seam a Harmony prefix on the game's rebuild uses). `WorldGenerator` is virtual, so tests plug in synthetic worlds; `TerrainWorld` puts any [`Valheim.Testing`](../packages/Valheim.Testing.md) terrain behind it.

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Rebuild | `Heightmap.RebuildTerrain`, `Poke` and `Regenerate` work over the heightmap's own grid and apply the zone's compiler as `TerrainComp.ApplyToHeightmap` does | — |
| Deltas | Every vertex with a non-zero level or smooth delta gets both, whether or not `m_modifiedHeight` flags it; only those are clamped to ±8 m around the base height | An unflagged delta shows only on the peer that wrote it, until its compiler next loads |
| No compiler | A zone without a compiler renders its base heights and skips `ModTerrainPass` | A compiler that is not `m_initialized` adds nothing; `ModTerrainPass` still runs |
| Lookup | `Heightmap.FindHeightmap(point)` returns the loaded zone holding the point (edges included) or null; `GetAllHeightmaps()` lists every loaded zone | A write aimed at an unloaded or wrong zone misses, as in the game |
| `TerrainComp.FindTerrainCompiler(pos)` | Returns the live compiler of the first such zone that has one, or null; a seam point belongs to the first zone | Never creates one. The game picks the first-created compiler at a seam; the doubles pick the first-loaded zone's |
| `HeightmapBuilder` | `IsTerrainReady` queues a build, `BuildQueued()` builds it, `MakeReady(...)` builds one and makes it ready; `SyncRequests` counts requests | `RequestTerrainSync` hands a ready build out and removes it; with none ready it builds on the spot, never null |
| Builder heights | Blend the corner biomes, smoothed for a distant LOD | Install a prepared builder, or null, with the scope's `WithHeightmapBuilder` |
| `TerrainComp.Save` | Writes compressed TCData: the version, the operation counter with its last point and radius, per vertex a flag and both deltas, per texel a flag and paint | Does nothing unless this peer owns the ZDO and the compiler is initialised (`GetZDO().SetOwner(other)` or `m_initialized = false` stops it) |
| `Save(paintOnly: true)` | Skips the write when the paint hash is unchanged since the last paint-only save | The hash sums the modified texels' channels, so repainting paved as dirt keeps it |
| `Load()` | Reads the bytes back as the game does, including the older paint grid | — |

## Terrain snapshots and assertions

`TerrainSnapshot.Of(compiler)` copies a compiler's level and smooth deltas, paint mask and both modified flags. `TerrainAssert` compares the compiler with it afterwards:

| Assertion | Passes when | Fails when |
|---|---|---|
| `OnlyChangedWithin(before, compiler, (x, z) => ...)` | Every change lies inside the footprint; the rest of the zone, flags included, is as it was | Nothing changed at all |
| `Unchanged(before, compiler)` | Nothing changed | Any delta, paint or flag changed |
| `SeamAgrees(west, east)` | Two loaded neighbours agree on shared vertices, rendered height and paint | A zone was written without its neighbour; rebuild both first |

## Global keys

| Area | The doubles do | Fails or differs when |
|---|---|---|
| `SetGlobalKey`, `RemoveGlobalKey` | Routed calls to the server, by `GlobalKeys` member, member and float, or string. The server applies at once and broadcasts the whole list as `GlobalKeys` | A client changes nothing locally; each broadcast replaces its whole list |
| Handlers | Exist only after `ZoneSystem.Start()`: the server registers `SetGlobalKey` and `RemoveGlobalKey`, a client `GlobalKeys`; `OnNewPeer(peer)` sends the list | A key set on the server before `Start()` is dropped without an error |
| Delivery | `ZRoutedRpc.instance.Deliver(serverId, "GlobalKeys", keys)` delivers a broadcast to a client | A key only the client added (`GlobalKeyAdd`, public here) is gone after a broadcast |
| Storage | Lower case as `name` or `name value`, split at the first space; setting a name again replaces its value | — |
| Reads | `GetGlobalKey(name)` finds a key in any case; `GetGlobalKey(key, out float)` reads the value; `GetGlobalKeyExact(line)` needs the whole line | Any string is a key; `GlobalKeys` members before `NonServerOption` are world modifiers |
| Saves | `SaveGlobalKeys`, `LoadGlobalKeys` keep the other keys; the server writes world modifiers to `WorldStartingGlobalKeys`, read back by `SetStartingGlobalKeys` | `WorldStartingGlobalKeys` is null with no world loaded, as on a client |

## Terrain modifier order

`TerrainModifier` is a MonoBehaviour. `AddComponent<TerrainModifier>()` on an active object wakes it into the live list; one under an inactive object stays out. `new TerrainModifier(position, view)` puts one on an object of its own. `TerrainModifier.GetAllInstances()` returns the list in apply order:

1. Non-player before player (`m_playerModifiction`).
2. Lower `m_sortOrder`.
3. Earlier creation time, read at `Awake` from the ZDO's `terrainModifierTimeCreated` long. A modifier placed by the current game reads 0 and sorts before a converted one.
4. Smaller squared distance of the whole position from the origin, height included. At the same ground distance, a higher modifier comes later.

The list is re-sorted only when a modifier joins or leaves; a full tie keeps joining order. `Heightmap.ModifiersInApplyOrder()` lists the enabled modifiers within radius + 4 m of a zone. What a modifier does to the heights is not modelled. `ValheimWorldScope.WithTerrainModifiers()` starts a fresh live list.

## Location placement

| Area | The doubles do | Fails or differs when |
|---|---|---|
| Altitude | `m_minAltitude` and `m_maxAltitude` are metres above water (y = 30); `ZoneLocation.AltitudeAboveWater(y)` and `IsAltitudeAllowed(y)` allow both ends | A definition written with absolute heights passes on a flat world near 30 m and fails elsewhere; `m_waterLevel` is not read |
| Slope | `SlopeRotationYaw(downhill)`: local +Z faces downhill, rounded to 22.5 degrees | — |
| `CheckPlacement(x, z, world, terrainDelta)` | Returns the first failed rule: origin distance, biome, altitude, distance from the centre, terrain delta | The default biome `None` matches nothing; biome area, forest, similar locations, vegetation and zone occupancy are not checked |
| Generation | `SpawnLocation`, `SpawnZone`, `PlaceLocations`, `SetupLocations`, `GenerateLocationsIfNeeded` exist with the game's signatures | They do nothing; nothing generates or places locations |
| Tables | `m_locationInstances` (listed by `GetLocationList()`), `m_locations`, `m_locationsByHash`; `ZoneLocation.m_prefab` is `new SoftReference<GameObject>(name, template)` | Copies of a `SoftReference` share one reference count |

## Registries and helpers

| Area | The doubles do | Fails or differs when |
|---|---|---|
| `ObjectDB` | Indexes `m_items` by name hash in `Awake` and `CopyOtherDB`; `FindDuplicateItems()` reports duplicates first | A second item with the same name throws on the next `UpdateRegisters` |
| Menu and world loads | `DeclareVanilla(items, recipes, effects)`, `LoadMainMenu(prefab)`, `LoadWorld(prefab)`; `AwakePostfix` and `CopyOtherDBPostfix` run where a Harmony postfix would | An unguarded registration is in the list twice at the second menu load |
| `ZNetScene` index | `Awake` indexes `m_prefabs` and `m_nonNetViewPrefabs` in `m_namedPrefabs`; `GetPrefab(name)`, `GetPrefab(hash)`, `HasPrefab`, `AddPrefab`; `FindDuplicateHashes()` | A duplicate hash throws, naming both prefabs |
| Late registration | `MarkNotYetRegistered(names)`, then `FinishRegistering()` | Until then `GetPrefab`/`GetItemPrefab` return null and record the lookup in `EarlyLookups`; `HasPrefab` is untouched |
| `Utils` | Gzip `Compress`, ordinal `CustomStartsWith`, `FindChild`, `GetEnabledComponentsInChildren`, `IterateHierarchy` | `RoundToInt(2.5f)` is 3, `RoundToInt(-1.5f)` is -1, `FloorToInt(0.999f)` is 1; below -64000 results truncate toward zero |
| `ZNet` | A MonoBehaviour with `m_loadError`; `Save(sync, ...)` counts its calls | — |
| Spawn settings | `RandomSpawn`, `RandomObject`, `CreatureSpawner`, `DropOnDestroyed`, `PickableItem` carry the game's defaults | Nothing they do when a location spawns is modelled |
| Drops and items | `DropTable` chance, count, weights, `m_oneOfEach`, shared result list for `GetDropListItems`; `Inventory` stacking and slot order; `SpawnArea` weighted choice | The world level is 0 and the resource rate 1 |
| `Container`, `Pickable` | Default items are added once and only by the owner; picked and enabled state lives in the ZDO | — |

## Limits

The doubles model the behaviour listed above and nothing more: terrain is the rebuild and the compiler, not the game's mesh, and no location is generated. Anything else on these types is a plain field or a no-op. Behaviour outside this list needs a native check against a real server with [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md).
