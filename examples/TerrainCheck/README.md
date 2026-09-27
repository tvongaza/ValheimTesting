# Bounded terrain expectation check

Read-only attachment to an already prepared game with the stable CLI core. It
never launches/stops a process, moves a player, generates a zone or changes ground.
Use the existing ownership/claim protocol separately. The output distinguishes
raw generator heights from actual loaded heightmap ground; it does not test
colliders, character support, or road usability.

Build with `dotnet build -c Release`. For package-only use, copy this example to
another directory and supply `-p:ToolkitPackageVersion=0.1.0-preview.2`, with the
preview package directory added as a NuGet source. Requires .NET 9 or later.

```sh
dotnet TerrainCheck.dll HOST PORT expectations.txt height-plan.json NEW_OUTPUT_DIR
```

Pin the fixture's world UID and plugin hashes in the expectations file. Supply
1–256 distinct x/z coordinates and **independently derived** expected heights:

```json
{
  "layer": "loaded-ground",
  "expectedFrom": "hand-derived level target for the fixture, revision ...",
  "tolerance": 0.05,
  "samples": [{"x": 8, "z": 10, "height": 37}]
}
```

The example numbers describe a hypothetical fixture, not a location in Valheim.
Use `generator` for raw-generator expectations, or `loaded-ground` for actual
heightmap expectations. Wrong coordinates, layer, units, missing loaded ground,
unknown properties, duplicates, nonfinite values and empty plans fail. A plain
copy of today's measurement as its expected value is not an independent test.

Reports retain every completed comparison's expected/actual/residual, command
transcripts, plan/pin hashes, JSON and JUnit. A mismatch returns exit 1; incomplete
measurements fail rather than becoming zero. Existing output directories are
refused. Review private world data before publishing output.

For the Roads calibration gate, choose a small known road section and independently
calculate the expected final height (including the writer's limits), prepare the
zone, run this on the server and a Roads-absent client, then measure collision in a
separate observation. Compare a matching replay/shim test against the same
expectations. This tool supplies the height-check component; it does not claim
that full calibration campaign has run.
