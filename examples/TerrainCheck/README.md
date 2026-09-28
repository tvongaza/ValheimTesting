# Bounded terrain expectation check

Read-only attachment to an already prepared game with the stable ValheimCLI core and World Tools pack. It
never launches/stops a process, moves a player, generates a zone or changes ground.
Use the existing ownership/claim protocol separately. The output distinguishes
raw generator heights from actual loaded heightmap ground; it does not test
colliders, character support, or road usability.

From the repository root, after [bootstrap and pin setup](../../docs/getting-started.md):

```sh
dotnet run --project examples/TerrainCheck -c Release -- \
  127.0.0.1 5555 /absolute/pins.txt /absolute/height-plan.json /new/terrain-result
```

For package-only use, copy this example directory elsewhere and build with `-p:ToolkitPackageVersion=0.1.0-preview.11`; the Game package and its dependencies (`Valheim.Testing` preview.5, `Valheim.Testing.Cli` preview.5) restore from NuGet.org. Add `-p:RestoreAdditionalProjectSources=/absolute/ValheimTesting/.packages` only to try an unpublished build. Requires .NET 10 or later at runtime.

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
