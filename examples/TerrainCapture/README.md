# Capture terrain inputs for exact replay

This read-only driver attaches to an already prepared, pinned game with ValheimCLI core and World Tools. It never launches Valheim, generates zones, or moves a player.

```sh
dotnet run --project examples/TerrainCapture -- localhost 5555 /private/pins.txt -4 8 2 2 2 generator /private/new-capture.json
```

Arguments are host, port, pins file, horizontal x/z, spacing in metres, x/z counts, layer and a new output file. The example coordinates are illustrative. There are at most 256 samples; spacing is 0.25–256 metres. Choose `loaded-ground` for existing heightmaps: unloaded samples make the capture incomplete and the importer refuses it. That layer intentionally has no biome or river facts.

Load a recorded input with `TerrainCapture.Load(path).Terrain`, then pass that `ReplayTerrain` to your own mod tests. It serves only the recorded coordinates; missing coordinates or unrecorded biome/river data throw. No interpolation is implied. The file records world/game identity, layer, timestamps and provenance and includes a SHA-256 integrity check. This detects accidental edits, not authenticity. Existing files are never overwritten.

Grid coordinates, completeness and sample counts are checked before creating a replay. Mutable ground is sampled over multiple frames; the capture is not an atomic snapshot. Compare against independently authored expectations when testing correctness. Do not commit private fixture identities or game captures by default.

Local capture/import tests and the [bounded native campaign](../../docs/native-validation-20260927.md) pass: generator and loaded-ground captures, exact file/replay round trips, unloaded-ground refusal, and loaded-ground equality after save/restart/rejoin.
