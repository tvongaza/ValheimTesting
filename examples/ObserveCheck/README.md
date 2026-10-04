# Observe an already prepared game

**Audience:** a mod author who already has a disposable game running with ValheimCLI and wants one measured fact from it. **Concept:** attach, verify strict pins, run one probe, keep the report.

```
ObserveCheck <host> <port> <pins-file> <new-output-directory> <probe> [probe arguments]
```

[ObserveCheck.cs](ObserveCheck.cs) is the whole flow: it reads and validates the probe's input, loads the strict pins (world UID and exact plugin hashes, see [getting started](../../docs/getting-started.md)), refuses an existing output directory, connects, verifies the pins (a `Preflight` step), runs the probe (one step) and writes `result.json` (with the SHA-256 of the pins and of the input file), `junit.xml` and `commands.jsonl` (every command and reply) into the new directory. Exit 0 means the probe passed; 1 a failure, including a refused input, a pin mismatch or an incomplete measurement; 2 invalid usage. Failed runs keep their files.

It never launches or stops the game, moves or teleports the player, changes cheats, generates a zone or edits ground; arrange arrival and protection separately (for example with `SessionControl.JoinWorld` and `PlayerPlacement.Arrive` in your own runner). Only the `session` probe's `save`, `join` and `leave` change the game. Coordinates in the samples below are illustrative, not known Valheim sites. A copy of today's measurement used as its own expected value is not an independent test. Review private world data before publishing a report.

```sh
# From the repository root, after bootstrap (CONTRIBUTING.md):
dotnet run --project examples/ObserveCheck -c Release -- 127.0.0.1 5555 /absolute/pins.txt /new/out paint /absolute/paint-plan.json
```

To build it outside this repository, copy the directory and pass `-p:ToolkitPackageVersion=` with a published `Valheim.Testing.Game` version of 0.1.0-preview.35 or later (phased reports); preview.20 and earlier do not compile it.

## ground: generator or loaded-ground heights

`ground <height-plan.json>`, through `TerrainProbe` and the ValheimCLI World Tools pack. 1–256 distinct x/z points with **independently derived** heights, on one layer: `generator` (raw generator height) or `loaded-ground` (the loaded heightmap). Writes `terrain.json` with every expected/actual/residual.

```json
{"layer": "loaded-ground", "expectedFrom": "hand-derived level target for the fixture, revision ...", "tolerance": 0.05,
 "samples": [{"x": 8, "z": 10, "height": 37}]}
```

Wrong coordinates, layer or units, missing loaded ground, unknown properties, duplicates, nonfinite values and empty plans fail. Generator height is not loaded ground, a collider or paint.

## surface: a client's ground, collider and support

`surface <surface-plan.json>`, through `SurfacePlan`, `SurfaceProbe` and `PlayerPlacement.RequireSupported`. Each sample needs both the loaded heightmap vertex and **that heightmap's own mesh collider** within tolerance; samples are integer coordinates on the native one-metre grid (`Heightmap.GetWorldHeight` reads the nearest vertex). Missing or unloaded ground fails; it is never replaced by the generator. With a `support` point, three observations half a second apart must show the player grounded, stationary (at most 0.15 m/s), alive and not flying, attached or teleporting, within 2 m horizontally and 0.3 m vertically; declare it on dry ground. Without one, `result.json` records grounding as not checked. Writes `surfaces.json` and `support.json`.

```json
{"expectedFrom": "independent declared profile applied to captured pre-write native vertices", "tolerance": 0.05,
 "samples": [{"x": 32, "z": 0, "height": 65}], "support": {"x": 32, "z": 0, "height": 65}}
```

For a vanilla-client replication check, pin the tested mod `absent` on the client and use only ValheimCLI there. Run the same plan again after a confirmed save, server restart and rejoin for persistence evidence.

## paint: loaded paint channels

`paint <paint-plan.json>`, through `PaintPlan` and `PaintProbe` (`valheim.world/terrain-paint`). Reads the loaded heightmap's raw paint texture at integer texels, as normalized RGBA; not a compiler stamp or a screenshot. Missing maps and out-of-range texels are incomplete, never black. Alpha is compared separately and can carry vegetation or lava information, so it is not labelled transparency. Writes `paint.json` with every channel residual.

```json
{"expectedFrom": "independently declared fixture mask", "tolerance": 0.01,
 "samples": [{"x": 32, "z": 0, "r": 1, "g": 0, "b": 0, "a": 0.25}]}
```

Declare the profile before writing terrain: core, fading edge, untouched ground with existing paint, both sides of a zone seam (sample beside the seam, not on it). A raw mask match is not a visual verdict.

## capture: record a bounded grid for exact replay

`capture <x> <z> <spacing> <countX> <countZ> generator|loaded-ground`, through `TerrainCapture`. At most 256 samples, spacing 0.25–256 m; writes `capture.json` (world, game version, layer, provenance and a SHA-256 integrity check). `loaded-ground` has no biome or river facts, and unloaded samples make the capture refused. Load it in a mod test with `TerrainCapture.Load(path).Terrain`: the replay serves only the recorded coordinates and throws for anything else. A capture is recorded input, not an independent expectation, and mutable ground is sampled over several frames, so it is not an atomic snapshot. One generator sample is `capture <x> <z> 1 1 1 generator`.

## walk: record a person walking a route

`walk <route.json> <seconds 5..600>`, through `WalkingProbe`. A person drives the client normally while the probe samples position, motion and support every 250 ms (`samples.jsonl`); it issues no movement, teleport, cheat or save. The route is an ordered array of checkpoints (`x`, `y`, `z`, horizontal `radius`, `heightTolerance`) whose areas do not overlap; place them at the approach, the turns and the destination, in each direction.

```json
[{"x": 0, "y": 10, "z": 0, "radius": 2, "heightTolerance": 1}, {"x": 20, "y": 12, "z": 0, "radius": 2, "heightTolerance": 1}]
```

Exit 0 means the trace qualifies for review: checkpoints reached in order without large gaps, teleport-like jumps, flying, attachment or death, with enough grounded samples. It does **not** mean the road is usable: `review.json` (linked from `result.json` as `walking-review` evidence) starts `not-reviewed`. The reviewer confirms the run was ordinary walking, then records accepted / needs-work, coordinates and a screenshot or clip per issue: walked both ways, no climbing or jumping where a walk was intended, switchbacks that do not merge, no obstructive rocks, stairs, edges, supports or water, and a usable approach height at the destination.

## session: state, save, join, leave

`session state | save <worldUid> | join <address> <character> [password-env] | leave`, through `SessionControl` (ValheimCLI Standard pack). `state` writes `session.json`. `save` waits for any earlier save, issues one, and requires the save number to advance in the same world (`saveNumber` in `result.json`); a client logout is not proof that the server saved. `join` turns the client's devcommands on first (ValheimCLI refuses a join without them) and joins an existing disposable character exactly once; `password-env` names an environment variable in the **game's** process, never a value, and omitting it clears the previous password. `leave` returns to the menu. At the menu there is no world yet, so pin only the plugins for `join` (menu pins), and the world too once joined. Mutations are never retried on a timeout, and every transition invalidates the actor's pins: verify the destination world's pins before anything else. For a test that joins and waits for the world, use `SessionControl.JoinWorld` or `WaitForWorld` in your own runner.
