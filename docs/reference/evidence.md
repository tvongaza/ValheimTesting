# Evidence

A run's `result.json` links every file a scenario attached as evidence. This page covers that list, the two read-only snapshots that write evidence and the review captures.

## Evidence in result.json

The `Evidence` array in `result.json` holds one `EvidenceReference(Kind, Site, WorldUid, File, Sha256)` per attached item, in attachment order. You add items with `ScenarioReport.Attach`. `ScenarioReport.Write` fills the array when it writes the report.

| You attach | `Write` does |
|---|---|
| An `IEvidence`, such as a snapshot | Serializes it to `evidence/<kind>-NNN.json` and records its SHA-256 |
| An `EvidenceReference` to a file already written | Links that file and keeps its SHA-256 |

`NNN` counts per kind, from `001`. A file inside the report directory is listed by its path relative to that directory. A file outside it keeps its absolute path, so attach only evidence that is safe to publish with the result.

Each reference also records the `Phase` of the step it was attached in. Outside any step, the phase is `Scenario`. This keeps setup evidence apart from the scenario's own.

`Write` also writes `result.json` and `junit.xml`, and overwrites them if they exist. Give it a new run directory. It can throw on a file-system failure, so call it from cleanup.

| Evidence | Kind; site | File | Attached by |
|---|---|---|---|
| [Terrain site snapshot](#terrain-site-snapshots) | `terrain-site`; the site name | `evidence/terrain-site-NNN.json` | `report.Attach(snapshot)` |
| [Area object snapshot](#area-object-snapshots) | `area-objects`; the site name | `evidence/area-objects-NNN.json` | `report.Attach(snapshot)` |
| [Review still](#review-stills-and-clips) | `review-still`; the capture id | JSON sidecar with the PNG's SHA-256 | `report.Attach(receipt.Evidence)` |
| [Review clip](#review-stills-and-clips) | `review-clip`; the clip id | JSON sidecar with SHA-256 of `frames.csv` and each frame | `report.Attach(receipt.Evidence)` |
| [A round's JSON](game-client-rounds.md#client-rounds), such as `arrival`, `zone-cycle` or `logout` | the name given to `Write`; the round name | `{round}-{name}.json` | `ClientRound.Write` itself |
| Your own file, such as [ObserveCheck](../../examples/ObserveCheck/README.md) `walk`'s `review.json` | your own (`walking-review`); your own | that file | `report.Attach(new EvidenceReference(...))` |

`Attach` refuses:

- a kind that is not 1 to 40 lower-case letters, digits or hyphens
- an `EvidenceReference` without a file
- an `EvidenceReference` without a lower-case, 64-character SHA-256

To capture evidence only when an assertion fails, use `StepWithEvidenceOnFailure`:

```csharp
report.StepWithEvidenceOnFailure("check cave", assertion,
    () => TerrainSiteSnapshot.Capture(client, "cave approach", expectedWorldUid, points, TimeSpan.FromSeconds(30)));
```

If the assertion fails, the capture runs and is attached in the `Scenario` phase. The assertion's own failure is rethrown. If the capture also fails, the report records a separate failed step, `capture evidence after <name>`.

## Terrain site snapshots

`TerrainSiteSnapshot` records the loaded ground height, terrain collider height and paint at a small, named site. It changes nothing: no terrain edits, no placed objects, no ZDO reads.

It waits for the area with ValheimCLI's structured `valheim.observe/zones` observation. Then it reads each point through these capabilities, with the same readers as the [probes](game-observations.md#terrain-surface-and-paint-probes):

| Capability | Reads | Same reader as |
|---|---|---|
| `valheim.world/terrain` with `loaded-ground` | Ground height | `TerrainProbe` |
| `valheim.world/terrain-surface` | Collider height | `SurfaceProbe` |
| `valheim.world/terrain-paint` | Paint texel | `PaintProbe` |

A snapshot is the probes' readings without expectations. It keeps every command and its exact reply.

### Before you capture

- The client is joined, and its local player is ready at the site.
- Each `GameActor` has [strict plugin and world pins](game-actors-and-pins.md#strict-pins-from-tests).
- You moved the player and [verified arrival](game-player-placement.md#arrival). The capture does not teleport.
- Optional: you pass a server actor to check that it hosts the same world.

Only the client supplies readings and readiness. Paint and collider heights are client-loaded layers.

### Capture and compare

The coordinates and site name below are illustrative.

```csharp
// client and server are connected, strictly pinned GameActor instances.
// The client's player is ready at the site.
var points = new[] { new TerrainSitePoint(16, -8), new TerrainSitePoint(20, -8) };
var before = TerrainSiteSnapshot.Capture(
    client, "cave approach", expectedWorldUid, points, TimeSpan.FromSeconds(30), server);

// Run the mod action, confirm the save, restart and rejoin with fresh pins.
var after = TerrainSiteSnapshot.Capture(
    clientAfterRejoin, "cave approach", expectedWorldUid, points, TimeSpan.FromSeconds(30), serverAfterRestart);

foreach (var change in TerrainSiteSnapshot.Compare(before, after))
    Console.WriteLine($"{change.Point}: ground {change.GroundHeight:+0.000;-0.000;0.000} m");

report.Attach(before);
report.Attach(after);
report.Write(outputDirectory);
```

`result.json`'s `Evidence` links `evidence/terrain-site-001.json` and `evidence/terrain-site-002.json`.

### Readings

Each `TerrainSiteReading` is `(Point, GroundHeight, ColliderHeight, R, G, B, A)`:

| Field | Value |
|---|---|
| `Point` | The `TerrainSitePoint(X, Z)` read |
| `GroundHeight` | Loaded ground height |
| `ColliderHeight` | Height of the terrain vertex's own collider |
| `R` | Paint: dirt |
| `G` | Paint: cultivated |
| `B` | Paint: paved |
| `A` | Paint: vegetation, 1 where it may grow, 0 where cleared |

`Compare` returns one `TerrainSiteDelta` per point: the same fields, after minus before. It ignores timestamps and raw replies. It throws `InvalidOperationException` unless both snapshots name the same site, world and ordered points.

### Refused or failed

| Input or event | Result |
|---|---|
| Not 1 to 16 distinct integer x/z points, or beyond ±20000 | Refused |
| Blank site name, over 80 characters, or control characters | Refused |
| Non-numeric world UID | Refused |
| Readiness wait not positive, or over two minutes | Refused |
| Reply incomplete, from another layer, in other units or for another point | Fails; no sample is invented |
| Command refused | Recorded with its refusal, then fails |
| Area not ready in time | Fails, naming the last area state |
| Client or server not on the expected world, before or after reading | Fails |

A terrain snapshot is a **loaded client observation**, not a generator-height oracle. For generator terrain, use [world dumps](world-dump-contract.md) or [ObserveCheck `capture`](../../examples/ObserveCheck/README.md#capture-record-a-bounded-grid-for-exact-replay) with its layer stated.

## Area object snapshots

`AreaObjectSnapshot.Capture(...)` records objects and structures in a small area, beside a terrain snapshot. It never settles support or changes the world.

| Actor | Reads | Command |
|---|---|---|
| Server | Saved ZDOs | `cli_zdos_at` |
| Server | Container contents, if `includeContainers: true` | `cli_containers_at` |
| Client | Instantiated prefabs | `cli_prefabs_at` |
| Client | Current piece support, if `includeSupport: true` | `cli_piece_support <x> <z> [radius]` |

It also uses `valheim.observe/zones` for readiness and `valheim.world/terrain` (`loaded-ground`) for the census centre. Support needs a ValheimCLI build with the read-only, coordinate-taking `cli_piece_support <x> <z> [radius]` form. With `includeSupport: false`, the capture does not call it.

Both actors must be strictly pinned to the same world UID. The client's player and area must be ready.

The coordinates and site name below are illustrative.

```csharp
// server and client are pinned GameActor instances; the client has arrived at the site.
var objects = AreaObjectSnapshot.Capture(
    server, client, "bridge footing", expectedWorldUid,
    x: 16, z: -8, radius: 8, readinessTimeout: TimeSpan.FromSeconds(30),
    includeContainers: true, includeSupport: true);
report.Attach(objects);
report.Write(outputDirectory);
```

`result.json`'s `Evidence` links `evidence/area-objects-001.json` with its SHA-256.

The snapshot holds the counts `SavedObjects`, `Containers`, `LoadedPrefabs` and `Pieces`, and every command with its complete reply, role and UTC time. `Containers` and `Pieces` are `-1` when you did not request them.

### Refused or failed

| Input or event | Result |
|---|---|
| Radius outside 1 to 64 m, or centre beyond ±20000 | Refused |
| Readiness wait not positive, or over two minutes | Refused |
| Summary missing, or its count disagrees with the rows | Fails |
| Unexpected line, or a reply for another area | Fails |
| A container inventory is unreadable | Fails |
| Support summary counts disagree | Fails |
| Wrong world or role, or a stale world after sampling | Fails |

A support row can be `pending`, `remote` or `unowned`. It shows what the client knew at that moment, not that the structure had settled. If you need settled support, wait for a separately observed transition before you capture.

A complete census is not a correctness verdict. Decide what objects should exist with your mod's own assertion. For height and paint, use a [terrain site snapshot](#terrain-site-snapshots).

## Review stills and clips

`ReviewCapture.Capture` and `ReviewClip.Capture`, in [`Valheim.Testing.GameSessions`](../packages/Valheim.Testing.GameSessions.md), make reproducible pictures for a person to look at. They prove three things:

- the picture was taken in the pinned world
- it was taken with the declared conditions
- its bytes are the ones the game wrote

They never judge whether the scene looks right. Each sidecar records `visualVerdict: "not asserted"`. Look at the image or frames yourself and record your verdict separately, for example in the native acceptance suite's `review.json`.

Both captures follow the same rules:

| Rule | Behaviour |
|---|---|
| Id | 1 to 64 letters, digits or hyphens |
| Review state | ValheimCLI's Observe-pack review state is begun before the capture |
| Restore | Restored after success, failure or cancellation; a failed restore fails the capture |
| Publish | Written to a staging directory beside the evidence directory, then moved in whole |
| Failure | A failed or canceled capture publishes nothing |
| Sidecar | World UID, game build, plugin pins, `image`, `sha256` and `bytes` |

Review stills share their image checks with an owned client's screenshots (`valheim-test cli`, a failed client's diagnosis):

- the PNG must be complete, read as a stream, at most 100 MB
- the game's reply must name the requested path

Only the review still is fetched through its game host.

Attach a capture to the report with `report.Attach(receipt.Evidence)`. For a complete run and the Observe-pack commands each capture needs, see the [native acceptance suite](../../tests/Valheim.Testing.NativeAcceptance/README.md).

Review raw replies for private world, container or plugin details before you publish evidence.
