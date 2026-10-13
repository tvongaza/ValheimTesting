# Pinned world dumps

Capture a ValheimCLI `cli_world_dump` as a pinned offline input, and read it back in tests.

## Purpose

A dump samples the **world generator**. It does not sample the loaded heightmap, paint, object state or a player's footing. Use a bounded CSV as offline input for terrain fixtures and pictures. It is not native evidence of a road or another mod's edits.

You capture with `WorldDump.CaptureAsync` from [`Valheim.Testing.GameSessions`](../packages/Valheim.Testing.GameSessions.md). You read the result with the types in [`Valheim.Testing`](../packages/Valheim.Testing.md#read-a-grid-dump). Pin versions from the [package table](../package-versions.md).

## Capture

Capture on a disposable world, with a pinned actor that has test access. The dump command is cheat-gated; see `TestAccess`.

The windows and the game build `"1.0.16"` below are illustrative. Pass the build you pinned for this host, because the dump does not report it.

```csharp
using Valheim.Testing.GameSessions;

// server: a pinned GameActor. serverHost: the IGameHost it runs on.
var coarse = await WorldDump.CaptureAsync(server, serverHost, hostDirectory: "C:/run/dumps-128", name: "world-128", step: 128,
    window: (0, 0, 1024), gameBuild: "1.0.16", localDirectory: "dumps", fetchTimeout: TimeSpan.FromMinutes(2));
var site = await WorldDump.CaptureAsync(server, serverHost, "C:/run/dumps-site-8", "site-8", 8, (-950, -1080, 64), "1.0.16", "dumps", TimeSpan.FromMinutes(2));
```

Each call writes `<name>.csv` and `<name>.json` into `dumps`. It returns the `WorldDumpManifest`, with `Path` set to the local CSV.

| Step | What it does | Refuses when |
|---|---|---|
| Check inputs | Validates the actor, names and numbers | Actor not pinned; step outside 5..1000; window not finite or radius not positive |
| Check paths | Validates the host directory and local names | Host directory not absolute or has whitespace; `<name>.csv` or `<name>.json` exists |
| Read world | Runs `cli_world` | Not exactly one loaded world with a seed and UID |
| Dump | Runs `cli_world_dump <step> <hostDirectory>`, plus `--window x,z,radius` | Not exactly one `OK: WORLD_DUMP` line |
| Re-read world | Runs `cli_world` again | World UID, seed or name changed |
| Check reply | Compares step, window and file location with the request | Step differs; window off by over 1 m; file outside host directory |
| Fetch | Copies the whole host directory | The reply's file is missing |
| Check file | Compares the CSV with the reply | Sample count or extent differs |
| Write | Writes the CSV and its manifest, then verifies it | Manifest fails `Verify()` |

On any failure, neither file is left behind.

- Use a new, absolute host directory for each capture. The capture fetches all of it.
- If the dump is large, raise the actor's `CommandTimeout`. The dump runs within it.
- Keep whole-world CSVs private. A small window of a disposable world holds no save, account or player data.

The manifest records `Name`, `Path`, `WorldUid`, `Seed`, `GameBuild`, `Command`, `Reply`, `Sha256`, `Step`, `MinX`, `MinZ`, `MaxX` and `MaxZ`. Never write one by hand.

## Read it back

```csharp
var manifest = WorldDump.ReadManifest("dumps/site-8.json");
GridDumpTerrain site = manifest.Verify();
```

`WorldDump.ReadManifest(path)` resolves the CSV next to the manifest, so the pair can move together. It verifies nothing. `manifest.Verify()` returns the `GridDumpTerrain` only when the CSV is exactly the file the manifest describes. It checks:

- a layer name, a CSV path, the world UID, seed and game build, and the command and reply
- a step of 5..1000 metres
- a 64-digit SHA-256 that matches the file
- the exact native header
- the biome, river and base-height layers
- a complete grid: no missing, repeated, ragged or non-finite node
- the recorded spacing and bounds
- an origin on ValheimCLI's lattice

A failed check throws `InvalidDataException`. `Verify()` does not interpret `Command` or `Reply`; they are evidence of how the file was made.

## The native contract

The header is `WorldDumpManifest.Header`: `x,z,height,biome,river,river_width,base_height`.

| Column | Holds | Units/rounding |
|---|---|---|
| `x`, `z` | Horizontal coordinates on the lattice `-10000 + i*step` | Metres |
| `height` | `WorldGenerator.GetHeight` | Rounded to 0.1 m |
| `biome` | The game's own name (`AshLands`) | Text |
| `river` | River weight | Rounded to 0.01 |
| `river_width` | River width | Rounded to 0.1 |
| `base_height` | `WorldGenerator.GetBaseHeight(x,z,false)` | **Unitless** generator value, rounded to 0.00001 |

Increasing x and z follow their world axes. `base_height` is not a second ground elevation.

Node values are exact to the file's precision. Between nodes, `GridDumpTerrain` interpolates bilinearly: an approximation, never a native replay. If a native field disappears or changes meaning, treat it as a contract change to investigate. Do not invent a default.

## Layered readers

```csharp
var terrain = LayeredDumpTerrain.Load(new[] {
    WorldDump.ReadManifest("dumps/world-128.json"), WorldDump.ReadManifest("dumps/site-8.json")
}, baseHeightLayer: "world-128");
```

`LayeredDumpTerrain.Load(manifests, baseHeightLayer)` verifies every layer. It refuses layers from different worlds or game builds.

- **Steps:** if node-for-node comparisons matter, choose steps that share the global lattice. 128 m and 8 m grids both have nodes at `-10000 + i*128` inside their common bounds.
- **Window centres:** they need not be on that lattice. ValheimCLI selects nodes from the global origin.
- **Height, biome, river:** the finest containing layer answers. A coarse-only query falls back to coarse terrain.
- **Base height:** only the designated layer answers. Outside its lattice, base height is refused, even inside a fine window.

For the full query rules, see [`Valheim.Testing`](../packages/Valheim.Testing.md#read-a-grid-dump).

## Fixtures

`tests/Valheim.Testing.Tests/Fixtures/WorldDump` holds two real dumps from a dedicated server, for the disposable world `DumpContract1`. One is a 128 m 8×8 window; the other an 8 m 17×17 window. Both are byte for byte, with the manifests the capture wrote.

| Test | Covers |
|---|---|
| `WorldDumpManifestTests` | Verifies both dumps and composes them node for node |
| `WorldDumpCaptureTests` | Replays the run's replies through the capture; must reproduce the manifests exactly |

To update for a new game build:

1. Capture a fresh bounded pair with `WorldDump.CaptureAsync` on a disposable world.
2. Inspect the header and representative raw rows against the game-side exporter.
3. Replace the fixture pair and its replay inputs.
4. Run the dump tests.

A new field or changed units needs an explicit API decision before layered readers use it.

## Limits

- A dump reflects the generator, not loaded terrain, paint, objects or other mods' edits.
- Interpolated values are approximations, not native values.
- The CSV holds no world identity. The world UID, seed and game build come from the capturing session.
