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

## Declare state separately from the generator

`TerrainGrid<T>` is a row-major grid (x fastest). Its origin is the **first sample**, with explicit count and spacing. Heights are absolute world y values in metres. Use `PaintRgba` for raw linear channels; it validates [0,1], but supplies no dirt/paved meaning or byte quantization.

`TerrainZoneState` pairs height and paint grids. Their counts, origins and spacing are independent: a vertex and a texture texel need not refer to the same position. Nothing in the library assumes the native paint texture matches compiler arrays. A mod adapter must make that mapping explicitly.

`TerrainWorldState` stores zones by integer x/z identity. Add only the zones the fixture intends to load. Missing or duplicate zones throw. The state is mutable and test-owned; it is not a concurrent simulation. Adjacent zones keep separate copies of edge samples so a missing neighbour update produces a visible seam. There is no automatic seam repair.

`Clone()` copies the grids and zone collection for before/after comparison or another isolated test phase. It is **not** native serialization or evidence that a mod survives a save/restart. Generic grids should contain value-only sample types, such as float or `PaintRgba`; their clones copy values, not arbitrary referenced objects.

## Read a grid dump

`GridDumpTerrain` answers `ITerrain` from an evenly spaced grid of samples, such as the `world.csv` that ValheimCLI's `cli_world_dump` writes. `GridDumpTerrain.Load(path)` or `Read(reader, provenance)` takes CSV:

```
x,z,height,biome,river,river_width,base_height
-10000,-10000,-400.0,Ocean,0.00,0.0,-1.00000
...
```

- A header row names the columns, in any order and any case. `x`, `z` and `height` (metres) are required. `biome` is optional and holds a biome name (`AshLands` and `Ashlands` both work; `None`, `Unknown` or a number are refused). `river` (weight) and `river_width` are optional but only together. Other columns, such as `base_height`, are ignored.
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

The constructor declares a grid directly from row-major arrays (x fastest), which suits hand-written fixtures. `ReplayTerrain` stays exact lookup; use `GridDumpTerrain` when interpolation between regular samples is what the test means.

## Render a terrain for review

`TerrainRenderer` draws any `ITerrain` over a `TerrainArea` at a chosen metres per pixel (the area must be a whole number of pixels, at most 8192 a side). +x points right and +z up. Each pixel samples its centre once. Colour by `TerrainColoring.Height` (blue below `WaterLevel`, default 30; green, brown, white from there to `HighHeight`) or `TerrainColoring.Biome` (one fixed colour per biome). `Polyline` and `Point` overlays are drawn in world coordinates and clipped at the edge; polylines first, then points.

```csharp
new TerrainRenderer(new TerrainArea(-512,-512,512,512), 2) { Coloring = TerrainColoring.Biome }
    .Polyline(road, new RenderColor(255,0,0)) // road: the (x, z) points a mod planned
    .Point(0,0,new RenderColor(0,0,0))
    .Render(GridDumpTerrain.Load("world.csv")).WritePng("review.png");
```

The output is a PNG written without a compression library: its image data uses uncompressed deflate blocks, so the bytes are identical on every platform and runtime for the same heights, at about three bytes per pixel. A terrain that refuses a coordinate fails the render; no pixel is painted in place of missing data. A picture of an input shows what the fixture declares, not what the game does.

## Compare two terrain sources

`TerrainParity.Compare(expected, actual, area, step, heightTolerance)` samples both sources at `MinX + i*step`, `MinZ + j*step` up to and including the area's maximum and reports the sample count, failures, the largest difference and the worst points (largest first). Pass `compareBiomes: true` to require equal biomes, or `riverWeightTolerance` to compare river weights. Assert on `report.Passed` and print `report.ToString()` on failure. Use it as a golden check when replacing one terrain source with another, for example a mod's legacy synthetic world with this library's, or a dump with the generator it came from. Choose the step and tolerance from what the test decides. On a grid's own nodes a dump matches its source only to the precision the file was written with: `cli_world_dump` rounds height to 0.1 m and river weight to 0.01, so node parity against the generator needs a height tolerance of at least 0.05 m (and `riverWeightTolerance` of at least 0.005). A dump written at full precision, as in the SharedWorld example, matches exactly. Between nodes, bilinear error adds to that and depends on curvature. A grid shifted by one node fails; the library tests keep that negative control.

## Call real mod code

Keep adapters, expected outcomes and mod-specific assertions in the mod repository:

- [Roads shared-zone writer test](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.Tests/SharedZoneWriterTests.cs) feeds declared slope/paint into its own compiler double, calls the real road writer in both zone orders and checks the shared edge, an earlier off-road edit, paint preservation and repeat application. [TerrainTestWorld](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.Tests/TerrainTestWorld.cs) is the thin game-type adapter used by existing grade/search tests.
- [MWL shared-zone conversion test](https://github.com/tvongaza/MoreWorldLocations_All/blob/review/testing-adapter-ready/MoreWorldLocations.Tests/SharedZoneConversionTests.cs) runs the real authored level/smooth/paint conversion across two zones, checks matching edge results and untouched regions, and verifies that the same operation is not applied twice.

The library does not call either mod or duplicate its grading, smoothing, terrain compiler, persistence ledger or paint rules. Derive expected outcomes independently. Compare complete sample sets where completeness matters; do not turn missing input into zero.

## Evidence limits and next layers

These fixtures model declared terrain and mutable state. They do not reproduce native noise, biome blending, physics, ZDO ownership, compiler serialization, paint texture sampling or Unity scheduling. Keep a few independent native checks for those boundaries and human review for how the result looks and walks. The previously measured Roads paint/reload campaign remains separate evidence; it did not validate every synthetic shape introduced here.

[TerrainCapture](../examples/TerrainCapture/README.md) now imports validated bounded grids for exact replay. Its [bounded native capture/reload check passed](native-validation-20260927.md). Interpolation exists only in `GridDumpTerrain`, as its documented bilinear rule; replay never interpolates.

To contribute a new shape, state model or consumer example, follow the [synthetic-world contribution recipe](../CONTRIBUTING.md#extend-the-synthetic-world). Prefer a small reusable contract and independent expectations over copying a mod's fake game types into this library.
