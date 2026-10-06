# Pinned ValheimCLI world dumps

`cli_world_dump` samples the **world generator**, not the loaded heightmap, paint, object state or a player's footing. A bounded CSV is useful as an offline input for terrain fixtures and pictures. It is not native evidence of a road or another mod's edits.

## Capture: one call

On a disposable world, with a pinned actor that has test access (the dump command is cheat-gated; see `TestAccess`), capture each layer with `WorldDump.CaptureAsync` from `Valheim.Testing.GameSessions`:

```csharp
var coarse = await WorldDump.CaptureAsync(server, serverHost, hostDirectory: "C:/run/dumps-128", name: "world-128", step: 128,
    window: (0, 0, 1024), gameBuild: "1.0.16", localDirectory: "dumps", fetchTimeout: TimeSpan.FromMinutes(2));
var site = await WorldDump.CaptureAsync(server, serverHost, "C:/run/dumps-site-8", "site-8", 8, (-950, -1080, 64), "1.0.16", "dumps", TimeSpan.FromMinutes(2));
```

The capture reads `cli_world` before and after the dump and refuses a change of world; sends `cli_world_dump <step> <hostDirectory>` (with `--window x,z,radius` when given); requires exactly one `OK: WORLD_DUMP` reply whose step, window (to the metre), sample count and extent agree with the file and whose file lies in the host directory; fetches the file; and writes `<name>.csv` beside `<name>.json`, a `WorldDumpManifest` holding the name, the CSV's file name, world UID and seed, the game build you pinned, the command and full reply, the SHA-256, the step and the bounds. It never replaces an existing pair and leaves nothing behind when a check fails. The host directory must be absolute, and since the capture fetches all of it, give each capture a new one. The dump runs within the actor's `CommandTimeout`; raise it for a large dump. ValheimCLI's reply grammar is read only there; nobody writes a manifest by hand.

The numerical windows are examples, not preferred gameplay sites. Keep whole-world CSVs private; a small window of a disposable world holds no save, account or player data.

## Offline use

`WorldDump.ReadManifest(path)` reads a manifest and resolves its CSV next to it. `manifest.Verify()` returns the `GridDumpTerrain` only when the file is exactly the one the manifest describes (SHA-256, native header, all native layers, complete grid, step, bounds, lattice); `LayeredDumpTerrain.Load(manifests, baseHeightLayer)` verifies every layer and refuses mixed worlds or builds. The core package does not read the command or reply: they are kept as evidence of how the file was made.

For a layered reader, choose steps that share the global lattice where node-for-node comparisons matter. For example, 128 m and 8 m grids both have nodes at `-10000 + i*128` inside their common bounds. Their window centres need not be on that lattice: ValheimCLI selects the included nodes from the global origin. `LayeredDumpTerrain` always takes the finest containing layer for ordinary queries; designate one verified grid for base height. A coarse-only query falls back to coarse terrain, while a query outside the designated base lattice refuses base height even if another fine window contains it.

## The native contract

The native header currently checked is `x,z,height,biome,river,river_width,base_height`. Coordinates are horizontal x/z in metres, on ValheimCLI's lattice `-10000 + i*step`; increasing x and z follow their respective world axes. `height` is `WorldGenerator.GetHeight` rounded to 0.1 m. `river` is weight rounded to 0.01; `river_width` is rounded to 0.1. `base_height` is `WorldGenerator.GetBaseHeight(x,z,false)` rounded to 0.00001: a **unitless generator value**, not a second ground elevation. Biomes are the game's own names (`AshLands`). Node values are exact to the file's precision. `GridDumpTerrain` uses bilinear interpolation between nodes as an approximation, never as a native replay. A native field that disappears or changes meaning is a contract change to investigate, not a default value to invent.

`tests/Valheim.Testing.Tests/Fixtures/WorldDump` holds two real dumps a Valheim 1.0.16 dedicated server wrote on 2 October 2026 for a disposable world (`DumpContract1`): a 128 m 8×8 window and an 8 m 17×17 window, byte for byte, with the manifests the capture writes for them. `WorldDumpManifestTests` verifies and composes them node for node; `WorldDumpCaptureTests` replays that run's replies through the capture and must reproduce the checked-in manifests exactly. The replies are verbatim except the station's staging directory, which reads `C:/dumps`. That run kept no `cli_world` line, so the test builds one from the world's recorded name, seed and UID.

When updating for a new build, capture a fresh bounded pair with `WorldDump.CaptureAsync` on a disposable world, inspect the header and representative raw rows against the game-side exporter, replace the fixture pair and its replay inputs, and run the dump tests. A new field or changed units needs an explicit API decision before layered offline readers use it.

The Roads route-equivalence comparison recorded in PR #205 (the same ordered routes from the layered reader as from Roads' own) was a private, one-time record, not a repeatable test here.
