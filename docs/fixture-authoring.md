# Optional authored Valheim fixtures

Assistant-written research notes, moved from ValheimCLI's `docs/fixture-authoring.md` (commit `d112140`) on 30 September 2026. Research checked 26 September 2026. Expand World is useful for the small real-game layer of the test pyramid, not a requirement of the shared unit-test library.

## What its documented controls offer

- [Expand World Data](https://github.com/JereKuusela/valheim-expand_world_data) controls biome distribution and terrain settings. It documents flat, single-biome worlds as one use.
- [Expand World Size](https://github.com/JereKuusela/valheim-expand_world_size) exposes radius, stretch and altitude controls. Reduced-radius feasibility and minimum useful radius should be verified against the chosen release before adopting a recipe. Smaller radius alone does not prove cheaper startup: Roads/game scans may still use fixed bounds.
- [Location configuration](https://github.com/JereKuusela/valheim-expand_world_data/blob/main/docs/locations.md) supports counts, biome/altitude/slope constraints, grouping, deterministic versus random flags and pregeneration. This is constrained placement, not a documented arbitrary fixed-coordinate placement API. Validate the resulting site transforms, or explicitly place sites using a separate controlled setup step.

## Two distinct fixture families

1. **Controlled modded fixtures:** small test archipelago or terrain regions with a short slope, crossing and nearby POI. Pin the recipe, game/mod builds, seed and generated save hashes. Use EWD/EWS on both sides wherever terrain or biome generation changes. Generate once, verify, then copy the prepared save for subsequent runs.
2. **Vanilla-terrain compatibility fixtures:** a few prepared zones in a normal seeded world, ValheimCLI-equipped client without Roads/MWL or terrain-generator mods. These remain the acceptance evidence for server-side-only operation. Do not assume saving a custom biome/height world makes its generator removable.

EWD documents a limited server-only subset (locations, dungeons, rooms, vegetation and selected settings); custom terrain/biomes are outside that subset. EWS documents installation on server and clients. Preserve those requirements in the fixture manifest. [EWD server-only rules](https://github.com/JereKuusela/valheim-expand_world_data#server-side), [EWS requirements](https://github.com/JereKuusela/valheim-expand_world_size#expand-world-size).

## Bounded pilot, after the current framework gate

- Choose one pinned EWD/EWS release and verify compatibility with the installed game; no additional native campaign yet.
- Disable unrelated locations explicitly rather than deleting entries that auto-populate again. Reduce quotas as well as area, to avoid wasting time trying to place impossible sites. Disable automatic config reload for immutable test runs.
- Build one compact ordinary-biome fixture: flat control patch, grade-limited slope, river crossing, POI approach and a zone boundary.
- Inspect actual generator inputs and loaded ground separately; freeze the verified save/config as an immutable fixture with assertions about the intended features.
- Compare setup/load time once against the existing small vanilla-zone fixture. Keep it only if it makes tests cheaper or makes important scenarios reproducible.
- Feed captured input samples into replay tests to improve the mock's declared coverage. Keep this as an optional fixture-builder integration so other mod authors can use the framework without Expand World.

No Expand World recipe has been installed or tested here. Exact authored height fields may warrant another authoring tool; don't add one until these simpler controls prove insufficient.

## Bake a saved fixture with an owned server

Use a one-phase bake when a mod can prove that its generated state is ready during one server boot. The source is a directory containing exactly one named world, either directly or under `worlds_local/`. The tool hashes the source, verifies its disposable copy, and checks the world's UID from its own metadata. The output directory must not exist.

```sh
valheim-test server-load --server-only \
  --mod ./bin/Release/net48/MyMod.dll \
  --world-fixture ./fixtures/poi-base \
  --bake-fixture ./fixtures/poi-baked \
  --before-save-command 'mymod_generate_sites' \
  --before-save-line 'OK: generated sites=' \
  --assert-command 'cli_extension mymod.testing/bake-ready' \
  --assert-line 'EXTENSION_RESULT mymod.testing/bake-ready ready=true'
```

The assertion is **one** strict, pinned server command after the world accepts connections. Choose an observation whose accepted reply means generation is complete, and make its expected line specific enough to exclude a partial or refused result. If the test needs to cause a one-time change before that observation, add `--before-save-command TEXT --before-save-line PREFIX`; it runs once, before the assertion. For a longer event wait, use a `PinnedServerRun` scenario; see [the server runner](packages/Valheim.Testing.GameSessions.md).

### Ordered server-only phases

`server-load-phases` runs several strict `server-load --server-only` bakes in sequence. Each phase gets a new owned server copy; its confirmed saved fixture becomes the next phase's input. The source fixture and source game install are left alone. Write a plan such as:

```json
{
  "schema": 1,
  "worldFixture": "/absolute/path/to/fixtures/base",
  "keepFinal": true,
  "commonArgs": ["--server", "/absolute/path/to/ValheimDedicatedServer"],
  "phases": [
    {
      "name": "generate",
      "args": ["--mod", "/absolute/path/to/Generator.dll", "--assert-command", "cli_extension generator.testing/ready", "--assert-line", "EXTENSION_RESULT generator.testing/ready ready=true"]
    },
    {
      "name": "decorate",
      "args": ["--mod", "/absolute/path/to/Decorator.dll", "--before-save-command", "cli_extension decorator.testing/place", "--before-save-line", "EXTENSION_RESULT decorator.testing/place placed=true", "--assert-command", "cli_extension decorator.testing/ready", "--assert-line", "EXTENSION_RESULT decorator.testing/ready ready=true"]
    }
  ]
}
```

```sh
valheim-test server-load-phases --plan /absolute/path/to/phases.json --output /absolute/path/to/new-private-run
```

Use absolute paths and a new output directory. To begin with the toolkit's pinned small world instead of an existing fixture, set `"worldFixture": "packaged"`; the generated source is verified and retired after a successful build. The plan accepts two to ten phases, each with at least one explicit mod and its own strict readiness assertion. `commonArgs` selects the server environment, loader, adapter and ValheimCLI bundle; each phase selects its own mods, configs, extra plugins and assertion. `phase-state.json` records the active phase and checkpoint, and `phases/` contains its private run evidence. A failed phase stops the sequence and leaves the last confirmed checkpoint for inspection. Once the next phase succeeds, its predecessor checkpoint is removed. The final checkpoint stays only with `keepFinal: true`. If the runner is interrupted, use `valheim-test env status` to find the active phase's run ID and `valheim-test env recover --run ID` before inspecting or cleaning its checkpoint. Keep the plan and evidence private: paths, logs and game data may identify your machine or world.

Only after that assertion passes does the runner ask ValheimCLI for a save and require its save number to advance. It then gives the server time to save again at clean quit, fetches the actual saved world, checks its name and UID, and publishes `poi-baked/worlds_local/<name>/` with `fixture-manifest.json` only when the whole run and cleanup passed. The manifest records every output hash, the source hashes and UID, selected mod hashes, dependency-lock hash, run ID, confirmed save number and final exported save number. A later `--world-fixture poi-baked` run verifies that manifest before loading. The source fixture and install are never edited; the disposable runtime is retired as usual. A failed assertion, unconfirmed save, unclean stop or existing destination leaves no baked fixture. The run's private evidence remains for diagnosis.

Load the bake again to test persistence without issuing the one-time action:

```sh
valheim-test server-load --server-only \
  --mod ./bin/Release/net48/MyMod.dll \
  --world-fixture ./fixtures/poi-baked \
  --assert-command 'cli_extension mymod.testing/generated-state' \
  --assert-line 'EXTENSION_RESULT mymod.testing/generated-state sites=12'
```

For an object such as the [FullLifecycle marker](../examples/FullLifecycle/README.md), the assertion can use `cli_zdos_at X Z 8` and match its specific `ZDO <prefab>` line. A native macOS dedicated server places a world briefly in the signed-in user's default `worlds_local`; if a same-named world already exists there, preflight refuses instead of replacing it. Use another host or a separately named fixture.
