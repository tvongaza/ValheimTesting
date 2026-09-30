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
