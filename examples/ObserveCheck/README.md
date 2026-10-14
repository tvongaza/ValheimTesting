# Observe an already prepared game

Attach to a disposable game that ValheimCLI already runs, verify strict pins, run one read-only probe and keep the report.

## Run it

From the repository root, after [bootstrap](../../CONTRIBUTING.md#set-up-and-validate-locally):

```sh
dotnet run --project examples/ObserveCheck -c Release -- 127.0.0.1 5555 /absolute/pins.txt /new/out paint /absolute/paint-plan.json
```

The arguments are:

```text
ObserveCheck <host> <port> <pins-file> <new-output-directory> <probe> [probe arguments]
```

The pins file holds the strict pins: the world UID and the exact plugin hashes. See [strict pins](../../docs/reference/game-actors-and-pins.md#strict-pins-from-tests). The output directory must not exist yet.

[ObserveCheck.cs](ObserveCheck.cs) is the whole flow:

1. Read and validate the probe's input.
2. Load the pins and refuse an existing output directory.
3. Connect and verify the pins in a `Preflight` step.
4. Run the probe as one step.
5. Write the report.

To build it outside this repository, copy the directory. Then pass `-p:ToolkitPackageVersion=` with a published `Valheim.Testing.Game` version from [Packages](../../docs/package-versions.md).

The check never launches or stops the game. It never moves or teleports the player, changes cheats, generates a zone or edits ground. Only `session` `save`, `join` and `leave` change the game. Arrange arrival and protection in your own runner, for example with `SessionControl.JoinWorld` and `PlayerPlacement.Arrive`.

## Results

| Exit | Meaning |
|---|---|
| 0 | The probe passed. |
| 1 | Failure: refused input, pin mismatch or incomplete measurement. |
| 2 | Invalid usage. |

A run that connects writes these files into the new directory:

| File | Holds |
|---|---|
| `result.json` | Steps, and the SHA-256 of the pins and input file |
| `junit.xml` | The steps as JUnit XML |
| `commands.jsonl` | Every command and reply |

Each probe also writes its own file, listed in its section. Failed runs keep their files. A report names input files only. A `session` report records the action without the address or character. Review private world data before you publish a report.

The coordinates below are illustrative, not known Valheim sites. Derive expected values independently. A copy of today's measurement used as its own expected value is not a test. For the probe types, see [terrain, surface and paint probes](../../docs/reference/game-observations.md#terrain-surface-and-paint-probes).

## ground: generator or loaded-ground heights

`ground` reads heights through `TerrainProbe` and the default ValheimCLI plugin. It reads 1–256 distinct x/z points on one layer. `generator` reads the raw generator height, and `loaded-ground` reads the loaded heightmap.

```text
ground <height-plan.json>
```

```json
{"layer": "loaded-ground", "expectedFrom": "hand-derived level target for the fixture, revision ...", "tolerance": 0.05,
 "samples": [{"x": 8, "z": 10, "height": 37}]}
```

It writes `terrain.json` with every expected, actual and residual value.

It fails on wrong coordinates, layer or units, and on missing loaded ground. It also fails on unknown properties, duplicates, nonfinite values and empty plans. Generator height is not loaded ground, a collider or paint.

## surface: a client's ground, collider and support

`surface` uses `SurfacePlan`, `SurfaceProbe` and `PlayerPlacement.RequireSupported`. For each sample, it reads the loaded heightmap vertex and that heightmap's own mesh collider. Both must match within tolerance. Samples are integer coordinates on the native one-metre grid. `Heightmap.GetWorldHeight` reads the nearest vertex.

```text
surface <surface-plan.json>
```

```json
{"expectedFrom": "independent declared profile applied to captured pre-write native vertices", "tolerance": 0.05,
 "samples": [{"x": 32, "z": 0, "height": 65}], "support": {"x": 32, "z": 0, "height": 65}}
```

It writes `surfaces.json`, and `support.json` when the plan declares a `support` point. Declare the point on dry ground. Three observations, half a second apart, must show the player:

- grounded, alive and stationary (at most 0.15 m/s);
- not flying, attached or teleporting;
- within 2 m horizontally and 0.3 m vertically of the point.

Without a `support` point, `result.json` records grounding as not checked.

It fails on missing or unloaded ground and never substitutes the generator. It also fails on a heightmap or collider mismatch, or a player who is not settled.

For a vanilla-client replication check, pin the tested mod `absent` on the client and use only ValheimCLI there. For persistence, run the same plan again after a confirmed save, a server restart and a rejoin.

## paint: loaded paint channels

`paint` reads through `PaintPlan` and `PaintProbe` (`valheim.world/terrain-paint`). It reads the loaded heightmap's raw paint texture at integer texels, as normalized RGBA. It reads neither a compiler stamp nor a screenshot.

```text
paint <paint-plan.json>
```

```json
{"expectedFrom": "independently declared fixture mask", "tolerance": 0.01,
 "samples": [{"x": 32, "z": 0, "r": 1, "g": 0, "b": 0, "a": 0.25}]}
```

It writes `paint.json` with every channel residual.

Missing maps and out-of-range texels are incomplete, never black. Alpha is compared separately. It can carry vegetation or lava information, so it is not transparency.

Declare the profile before you write terrain. Cover the core, the fading edge and untouched ground with existing paint. Cover both sides of a zone seam, sampling beside the seam rather than on it. A raw mask match is not a visual verdict.

## capture: record a bounded grid for exact replay

`capture` records a grid through `TerrainCapture`. It takes at most 256 samples, with a spacing of 0.25–256 m.

```text
capture <x> <z> <spacing> <countX> <countZ> generator|loaded-ground
```

One generator sample is `capture <x> <z> 1 1 1 generator`.

It writes `capture.json` with the world, game version, layer, provenance and a SHA-256 integrity check. `loaded-ground` has no biome or river facts. Unloaded samples make the capture refused.

In a mod test, load the capture with `TerrainCapture.Load(path).Terrain`. The replay serves only the recorded coordinates and throws for any other coordinate. A capture is recorded input, not an independent expectation. It samples mutable ground over several frames, so it is not an atomic snapshot.

## walk: record a person walking a route

`walk` uses `WalkingProbe` while a person drives the client normally. It samples position, motion and support every 250 ms and issues no movement, teleport, cheat or save.

```text
walk <route.json> <seconds 5..600>
```

The route is an ordered array of checkpoints with `x`, `y`, `z`, a horizontal `radius` and a `heightTolerance`. Checkpoint areas must not overlap. Place them at the approach, the turns and the destination, in each direction.

```json
[{"x": 0, "y": 10, "z": 0, "radius": 2, "heightTolerance": 1}, {"x": 20, "y": 12, "z": 0, "radius": 2, "heightTolerance": 1}]
```

It writes `samples.jsonl` and `review.json`. `result.json` links the review as `walking-review` [evidence](../../docs/reference/evidence.md).

It fails if the client is not in a world, or if the trace does not qualify. Exit 0 means the trace qualifies for review. The checkpoints were reached in order without large gaps, teleport-like jumps, flying, attachment or death. Enough samples were grounded.

Exit 0 does **not** mean the road is usable. `review.json` starts as `not-reviewed`. A reviewer first confirms the run was ordinary walking. The reviewer then records accepted or needs-work, with coordinates and a screenshot or clip for each issue.

## session: state, save, join, leave

`session` uses `SessionControl` and the default ValheimCLI plugin.

```text
session state | save <worldUid> | join <address> <character> [password-env] | leave
```

| Action | Does | Writes |
|---|---|---|
| `state` | Reads the session | `session.json` |
| `save` | Waits for earlier saves, saves once; save number must advance | `saveNumber` in `result.json` |
| `join` | Turns devcommands on, joins an existing disposable character once | Nothing extra |
| `leave` | Returns to the menu | Nothing extra |

- A client logout is not proof that the server saved.
- ValheimCLI refuses a join without devcommands.
- `password-env` names an environment variable in the **game's** process, never a value. If you omit it, the previous password is cleared.
- At the menu there is no world yet. For `join`, pin only the plugins (menu pins). Once you have joined, pin the world too.
- Mutations are never retried on a timeout. Every transition invalidates the actor's pins, so verify the destination world's pins first.

If your test joins and then waits for the world, use `SessionControl.JoinWorld` or `WaitForWorld` in your own runner.
