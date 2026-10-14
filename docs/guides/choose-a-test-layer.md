# Choose a test layer

## When to use this

You have one behaviour to protect and have not decided how to test it. Start in your mod's repository with the cheapest layer that can observe the behaviour. Move down the table only when the cheaper layer cannot reach it. If the immediate problem is a mod that does not load, go to [Debug a mod load or mod conflict](check-a-mod-loads.md) first.

## Pick a layer

### Without the game

| Your mod needs to test | Use | First example | What still needs the game |
|---|---|---|---|
| A decision over heights, slopes or biomes | `Valheim.Testing` with an `ITerrain` | [SharedWorld](../../examples/SharedWorld/README.md) | Real generator output, physics and save encoding |
| Production code that calls supported Unity or Valheim types | `Valheim.Testing.Doubles`, source-linked | [ModWithTests](../../examples/ModWithTests/README.md): five runnable xUnit cases | Unmodelled game methods, Harmony timing, actual replication |
| A pinned grid dump, reviewed as a picture | `TerrainRenderer` with contours | [Test terrain decisions](test-terrain-decisions.md) | Native gameplay evidence; the picture is an interpolation |
| Retry, refusal, readiness or cleanup in a scenario driver | `Valheim.Testing.Game.Fakes` | [Test fakes](../packages/Valheim.Testing.Game.md#test-fakes) | The real process and game lifecycle |

The doubles model selected ZDO, RPC, console and ownership behaviour as well as terrain; read [what they model](../packages/Valheim.Testing.Doubles.md) before relying on a member. A field or stub is not an emulation of the game. Code that uses no Unity or Valheim type needs only `Valheim.Testing` and an `ITerrain` passed into the real method.

### With the game

These rows use `Valheim.Testing.Game` with ValheimCLI in the game, or the `valheim-test` tool. Sessions and A/B runs add `Valheim.Testing.GameSessions`.

| Your mod needs to test | Use | First example | What still needs the game |
|---|---|---|---|
| Loaded height, collision or paint reaching a client | ObserveCheck `surface` or `paint` | [`surface`](../../examples/ObserveCheck/README.md#surface-a-clients-ground-collider-and-support), [`paint`](../../examples/ObserveCheck/README.md#paint-loaded-paint-channels) | Appearance; a human judges it |
| Generator heights against loaded ground | ObserveCheck `ground` | [`ground`](../../examples/ObserveCheck/README.md#ground-generator-or-loaded-ground-heights) | Collider, paint or walking behaviour |
| A bounded grid sample recorded for exact replay | ObserveCheck `capture` | [`capture`](../../examples/ObserveCheck/README.md#capture-record-a-bounded-grid-for-exact-replay) | That the sampled value is independently correct |
| A named loaded-terrain site before, after or on failure | Terrain site snapshots | [Terrain site snapshots](../reference/evidence.md#terrain-site-snapshots) | Generator height, ZDO state or human usability |
| Saved objects, containers and structures near a site | Area object snapshots | [Area object snapshots](../reference/evidence.md#area-object-snapshots) | Terrain, paint or a mod-specific correctness verdict |
| Save, restart and rejoin under owned process control | `PinnedServerRun`, `ClientRounds`, `SessionControl` | [FullLifecycle](../../examples/FullLifecycle/README.md), [ObserveCheck `session`](../../examples/ObserveCheck/README.md#session-state-save-join-leave) | A client when replication matters |
| Whether a mod loads, or which mod set conflicts | `valheim-test server-load`, `server-load-ab` or `start` | [Debug a mod load or mod conflict](check-a-mod-loads.md) | The mod's gameplay behaviour; which mod owns a conflict |
| One mod before and after a change (A/B) in the real game | `TargetedRegression` runner | [TargetedRegression](../../examples/TargetedRegression/README.md): preflight without the game, then one run per arm | Other mods, dedicated servers or restarts |
| Walking and appearance | ObserveCheck `walk` plus a person | [`walk`](../../examples/ObserveCheck/README.md#walk-record-a-person-walking-a-route) | Acceptance; the human verdict is recorded separately |

## Rules of thumb

- Start in your mod repository. Shared fixtures go to this repository; game-side API goes to ValheimCLI.
- Synthetic input is not Valheim's generator.
- A replay of a capture is input, not independent evidence of the game.
- An incomplete observation fails. It never reads as zero.
- A person judges appearance and walkability. Record that verdict separately from the test result.

## Problems

| Problem | Cause | Fix |
|---|---|---|
| A game type conflicts or is missing | The test project mixes source doubles with game assemblies | Link source files only; see [Test logic that uses game types](test-logic-that-uses-game-types.md) |
| A package does not restore | The pin names an unpublished version | Pin an exact version from [Packages](../package-versions.md) |
| A native run hangs, or passes but proves nothing | Runtime, load order, characters, timing or evidence rules | Go through the [native-run checklist](before-a-native-run.md) |

## Related

- [Test logic that uses game types](test-logic-that-uses-game-types.md)
- [Test terrain decisions](test-terrain-decisions.md)
- [Run tests in CI](run-tests-in-ci.md)
- [ValheimCLI capability authoring guide](https://github.com/tvongaza/valheimCLI/blob/e596a7d984feaaa695f5c2999964b64e23fbfd92/docs/command-packs.md): add a mod-specific game-side action or observation
- [Evidence linked from result.json](../reference/evidence.md): where a native run's snapshots, stills and round JSON are
- [Packages](../package-versions.md): every package, version and pin
