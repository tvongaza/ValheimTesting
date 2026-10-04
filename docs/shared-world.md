# Shared terrain and zone fixtures

Use these helpers for broad tests of real mod code without starting Valheim. The pure `Valheim.Testing` package targets netstandard2.0 and has no Unity or ValheimCLI dependency. See [getting started](getting-started.md) for the exact package version and local feed setup; [SharedWorld](../examples/SharedWorld/README.md) is a runnable example.

## Declare the ground

`PlaneTerrain` supplies a flat or sloping surface with a chosen biome. `SyntheticTerrain` supplies the existing deterministic island, ridge and meandering river. `CompositeTerrain` overlays ordered `TerrainRegion` inputs. The last matching region replaces height, biome and river facts together; it does not blend them.

```csharp
var ground = new CompositeTerrain(new PlaneTerrain(40, .25f),
    new TerrainRegion((x,z) => x >= 100,
        new PlaneTerrain(80, 0, 0, TerrainBiome.Mountain)),
    TerrainRegion.Rectangle(120,-8,140,8,new PlaneTerrain(72)));
```

This declares a slope, a cliff at x=100, and a terrace in the cliff's upper surface. Rectangles include their minimum and exclude their maximum coordinates. A predicate can describe a causeway, narrow channel or irregular boundary without making another fake `WorldGenerator` class. Region terrain uses world coordinates, not coordinates relative to its region. Keep inputs/predicates pure and stable if multiple workers read them; composition copies the region list but does not freeze mutable underlying models.

## Zone state belongs to the terrain doubles

The state a terrain writer changes is the game's own: each loaded zone's `Heightmap` and its `TerrainComp` arrays (level and smooth deltas, modified-height flags, paint mask, modified-paint flags). The [terrain doubles](testing-toolkit.md) in `Valheim.Testing.Doubles` hold exactly those arrays: `ValheimWorldScope.WithTerrain(ground).WithZdos()` generates zones from the terrain declared above, `RegisterHeightmap(new Vector2s(x, z))` loads one, `TerrainSnapshot.Of(compiler)` records it, and `TerrainAssert.Unchanged`, `OnlyChangedWithin` and `SeamAgrees(west, east)` say what a write changed and whether two neighbours still agree on their shared vertices. Each zone keeps its own copy of the shared edge, as in the game, so a write applied to one side only fails `SeamAgrees`; nothing repairs a seam for you. [SharedWorld](../examples/SharedWorld/README.md) runs that check, its failure and the fix.

## Read a grid dump

`GridDumpTerrain` answers `ITerrain` from an evenly spaced grid of samples, such as the `world.csv` that ValheimCLI's `cli_world_dump` writes. `GridDumpTerrain.Load(path)` or `Read(reader, provenance)` takes CSV:

```
x,z,height,biome,river,river_width,base_height
-10000,-10000,-400.0,Ocean,0.00,0.0,-1.00000
...
```

- A header row names the columns, in any order and any case. `x`, `z` and `height` (metres) are required. `biome` is optional and holds a biome name exactly as the game writes it (`Meadows`, `AshLands`; another case, `None`, `Unknown` or a number is refused). `river` (weight) and `river_width` are optional but only together. `base_height` is optional and is **Valheim's unitless generator value**, not elevation in metres. It is available through `GetBaseHeight` when present; a missing layer is refused.
- One row per node, in any order. The nodes must fill one evenly spaced grid exactly once, with the same spacing on x and z. The origin is the smallest x and z. Numbers use the invariant culture; empty lines are skipped.
- A row with the wrong number of cells, a missing or repeated node (a ragged grid), uneven or unequal spacing, a non-numeric or non-finite value or an unknown biome is refused with an `InvalidDataException` naming the line.

Queries follow a written rule, not a guess:

| Query | Answer |
|---|---|
| At a node (within 1e-4 of a cell) | The node's height, river weight and width, unchanged |
| Between nodes | Bilinear between the four surrounding nodes: an approximation of the dumped world, not a replay |
| Biome anywhere inside | The nearest node's label, never an average; exactly halfway takes the node at larger x (or z) |
| Outside the closed rectangle from the first to the last node | `ArgumentOutOfRangeException` naming the covered range; no clamping or extrapolation |
| Biome or river when the file has no such column | `NotSupportedException` |
| Base height when the file has no `base_height` column | `NotSupportedException`; no inferred value |

The constructor declares a grid directly from row-major arrays (x fastest), which suits hand-written fixtures. `ReplayTerrain` stays exact lookup; use `GridDumpTerrain` when interpolation between regular samples is what the test means.

For a **native ValheimCLI dump** used as a pinned offline input, capture it with `WorldDump.CaptureAsync` (Valheim.Testing.Game) and load it through its manifest rather than with `GridDumpTerrain.Load` alone; see [pinned world dumps](world-dump-contract.md). `WorldDumpManifest.Verify()` checks the CSV against the manifest the capture wrote: SHA-256, the exact seven-column native header, the biome, river and base-height layers, a complete grid, the recorded spacing and bounds, and ValheimCLI's lattice. The world UID, seed and game build come from the same pinned session; the CSV holds no identity of its own. Do not fill in an absent field or convert `base_height` to metres.

For a coarse world layer plus fine windows, use `LayeredDumpTerrain.Load(manifests, baseHeightLayer)`. Every manifest is verified, and all must name the same world UID, seed and game build. Normal height, biome and river queries take the **finest containing grid**, even at its closed edge; equal-spacing grids that overlap are refused. `GetBaseHeight` uses **only** the named base-height lattice (`BaseHeightLayer`), so a fine height window cannot silently become its source. `LayerAt(x, z)` returns the grid that answers a query; its `Provenance` is the layer's name and `IsSampleNode(x, z)` says whether the query landed on an exact CSV node. Outside every layer, or outside the base lattice for base-height queries, the reader throws. A node retains the file's rounded value; between nodes height, river and base height are bilinear approximations and biome comes from the nearest selected node. The layer choice does not depend on the order of the manifests.

```csharp
var terrain = LayeredDumpTerrain.Load(new[] {
    WorldDump.ReadManifest("dumps/world-128.json"), WorldDump.ReadManifest("dumps/site-8.json")
}, baseHeightLayer: "world-128");
float ground = terrain.GetHeight(siteX, siteZ);        // finest containing layer
float baseValue = terrain.GetBaseHeight(siteX, siteZ); // designated coarse lattice, unitless
bool exact = terrain.LayerAt(siteX, siteZ).IsSampleNode(siteX, siteZ);
```

## Render a terrain for review

`TerrainRenderer` draws any `ITerrain` over a `TerrainArea` at a chosen metres per pixel (the area must be a whole number of pixels, at most 8192 a side). +x points right and +z up. Each pixel samples its centre once. Colour by `TerrainColoring.Height` (blue below `WaterLevel`, default the game's sea level of 30 m; green, brown, white from there to `HighHeight`) or `TerrainColoring.Biome` (one fixed colour per biome). `Contours(interval, colour)` draws one-pixel contour lines at `WaterLevel` (sea level when it is null) and every `interval` metres above and below it, in either colouring, so the coastline line and the water colour agree. `Polyline` and `Point` overlays are drawn in world coordinates and clipped at the edge; contours first, then polylines, then points.

```csharp
new TerrainRenderer(new TerrainArea(-512,-512,512,512), 2) { Coloring = TerrainColoring.Biome }
    .Contours(10, new RenderColor(90,74,48))
    .Polyline(road, new RenderColor(255,0,0)) // road: the (x, z) points a mod planned
    .Point(0,0,new RenderColor(0,0,0))
    .Render(GridDumpTerrain.Load("world.csv")).WritePng("review.png");
```

The output is a PNG written without a compression library: its image data uses uncompressed deflate blocks, so the bytes are identical on every platform and runtime for the same heights, at about three bytes per pixel. A terrain that refuses a coordinate fails the render; no pixel is painted in place of missing data. A picture of an input shows what the fixture declares, not what the game does. For the topographic review map (biome tints, rivers, a world-disc mask, location and route overlays), use ValheimCLI's [`examples/world-map.py`](https://github.com/tvongaza/valheimCLI/blob/b68949c/examples/world-map.py), which reads the same dump CSV; this library keeps one palette and does not version a map style.

## Compare two terrain sources

`TerrainParity.Compare(expected, actual, area, step, heightTolerance)` samples both sources at `MinX + i*step`, `MinZ + j*step` up to and including the area's maximum and reports the sample count, failures, the largest difference and the worst points (largest first). Pass `compareBiomes: true` to require equal biomes, or `riverWeightTolerance` to compare river weights. Assert on `report.Passed` and print `report.ToString()` on failure. Use it as a golden check when replacing one terrain source with another, for example a mod's legacy synthetic world with this library's, or a dump with the generator it came from. Choose the step and tolerance from what the test decides. On a grid's own nodes a dump matches its source only to the precision the file was written with: `cli_world_dump` rounds height to 0.1 m and river weight to 0.01, so node parity against the generator needs a height tolerance of at least 0.05 m (and `riverWeightTolerance` of at least 0.005). A dump written at full precision, as in the SharedWorld example, matches exactly. Between nodes, bilinear error adds to that and depends on curvature. A grid shifted by one node fails; the library tests keep that negative control.

## Call real mod code

Keep adapters, expected outcomes and mod-specific assertions in the mod repository. Both tests below were written against the removed `TerrainWorldState`; they move to the terrain doubles in [#330](https://github.com/tvongaza/ValheimTesting/issues/330).

- [Roads shared-zone writer test](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.Tests/SharedZoneWriterTests.cs) feeds declared slope/paint into its own compiler double, calls the real road writer in both zone orders and checks the shared edge, an earlier off-road edit, paint preservation and repeat application. [TerrainTestWorld](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.Tests/TerrainTestWorld.cs) is the thin game-type adapter used by existing grade/search tests.
- [MWL shared-zone conversion test](https://github.com/tvongaza/MoreWorldLocations_All/blob/review/testing-adapter-ready/MoreWorldLocations.Tests/SharedZoneConversionTests.cs) runs the real authored level/smooth/paint conversion across two zones, checks matching edge results and untouched regions, and verifies that the same operation is not applied twice.

The library does not call either mod or duplicate its grading, smoothing, terrain compiler, persistence ledger or paint rules. Derive expected outcomes independently. Compare complete sample sets where completeness matters; do not turn missing input into zero.

## Evidence limits and next layers

These fixtures model declared terrain and mutable state. They do not reproduce native noise, biome blending, physics, ZDO ownership, compiler serialization, paint texture sampling or Unity scheduling. Keep a few independent native checks for those boundaries and human review for how the result looks and walks. The previously measured Roads paint/reload campaign remains separate evidence; it did not validate every synthetic shape introduced here.

[TerrainCapture](../examples/TerrainCapture/README.md) now imports validated bounded grids for exact replay. Its [bounded native capture/reload check passed](native-validation-20260927.md). Interpolation exists only in `GridDumpTerrain`, as its documented bilinear rule; replay never interpolates.

To contribute a new shape, state model or consumer example, follow the [synthetic-world contribution recipe](../CONTRIBUTING.md#extend-the-synthetic-world). Prefer a small reusable contract and independent expectations over copying a mod's fake game types into this library.
