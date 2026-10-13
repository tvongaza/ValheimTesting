# Valheim.Testing

[Current preview API reference](https://tvongaza.github.io/ValheimTesting/reference/). The package's README is this page. For an older preview, use the versioned source commit in the package metadata.

## Purpose

`Valheim.Testing` lets you feed declared inputs into your mod's real decisions without the game. It provides:

- declared terrain and exact replay of captured samples
- pinned world-dump fixtures
- terrain rendering and parity checks
- `StaticOverride` for scoped statics

It targets netstandard2.0, so it runs in a net48 (Mono) test leg and on modern .NET. It has no Unity, Valheim or ValheimCLI dependency.

For the steps, see [Test a terrain decision without the game](../guides/test-terrain-decisions.md). If your decisions call game types, add [`Valheim.Testing.Doubles`](Valheim.Testing.Doubles.md). For what only the running game shows, use [`Valheim.Testing.Game`](Valheim.Testing.Game.md).

## Install or pin

Pin the released version from the [package table](../package-versions.md).

[SharedWorld](../../examples/SharedWorld/README.md) is a runnable example that needs no game. It composes terrain, checks a zone seam and checks a grid dump against its source. Each check has a negative control.

## Types a test calls

| Type | What it is | Reference |
|---|---|---|
| `ITerrain`, `PlaneTerrain`, `SyntheticTerrain` | Height, biome and river facts at any (x, z): a plane, or a deterministic island | [Test terrain decisions](../guides/test-terrain-decisions.md) |
| `CompositeTerrain`, `TerrainRegion` | Ordered regions over a base terrain; the last matching region replaces every fact | [Test terrain decisions](../guides/test-terrain-decisions.md) |
| `ReplayTerrain` | Exact lookup of samples captured by `TerrainCapture` (in Valheim.Testing.Game); missing samples are refused | [ObserveCheck](../../examples/ObserveCheck/README.md) |
| `GridDumpTerrain`, `LayeredDumpTerrain`, `WorldDumpManifest` | An evenly spaced CSV grid, and a pinned native dump verified against its manifest | [Read a grid dump](#read-a-grid-dump), [pinned world dumps](../reference/world-dump-contract.md) |
| `TerrainRenderer` | A deterministic PNG of any `ITerrain`, coloured by height or biome | [Render a terrain](#render-a-terrain) |
| `TerrainParity` | Two terrain sources compared over an area within a tolerance | [Compare terrain sources](#compare-terrain-sources) |
| `StaticOverride` | A scoped change to statics and environment variables, restored on dispose | [Static overrides](#static-overrides) |

This block compares a declared slope and terrace with a dump of the same ground:

```csharp
using Valheim.Testing;

var ground = new CompositeTerrain(new PlaneTerrain(40, .125f),
    TerrainRegion.Rectangle(28, -4, 36, 4, new PlaneTerrain(46)));   // a slope with a declared terrace
var dump = GridDumpTerrain.Load("terrace.csv");                         // the same ground, dumped
var parity = TerrainParity.Compare(ground, dump, new TerrainArea(4, -8, 64, 8), 4, 0.05f);
if (!parity.Passed) throw new Exception(parity.ToString());
```

## Read a grid dump

`GridDumpTerrain` answers `ITerrain` from an evenly spaced grid of samples. One example is the `world.csv` that ValheimCLI's `cli_world_dump` writes. Load it with `GridDumpTerrain.Load(path)` or `Read(reader, provenance)`. `Load` uses the file name as the provenance that errors name.

```text
x,z,height,biome,river,river_width,base_height
-10000,-10000,-400.0,Ocean,0.00,0.0,-1.00000
...
```

| Column | Required | Holds |
|---|---|---|
| `x`, `z`, `height` | Yes | Coordinates and height, in metres |
| `biome` | No | The game's exact name (`Meadows`, `AshLands`) |
| `river`, `river_width` | No, but only together | River weight and width |
| `base_height` | No | **Valheim's unitless generator value**, not metres; read with `GetBaseHeight` |
| Any other | No | Ignored |

The file must follow these rules:

- A header row names the columns, in any order and any case.
- There is one row per node, in any order. The rows fill one evenly spaced grid exactly once.
- Each axis has at least two nodes. Spacing on x and z is equal.
- The origin is the smallest x and z.
- Numbers use the invariant culture. Empty lines are skipped.

`Read` and `Load` throw an `InvalidDataException` that names the line or header column for any of these:

- a missing required column, or a header column that is empty or repeated
- a row with the wrong number of cells
- a missing or repeated node (a ragged grid)
- uneven or unequal spacing
- a non-numeric or non-finite value
- a `biome` that is not the game's exact name: another case, `Unknown` or a number

| Query | Answer |
|---|---|
| At a node (within 1e-4 of a cell) | The node's height, river weight and width, unchanged |
| Between nodes | Bilinear between the four surrounding nodes: an approximation, not a replay |
| Biome anywhere inside | The nearest node's label, never an average; a halfway tie takes larger x (or z) |
| Outside the rectangle from first to last node | `ArgumentOutOfRangeException` naming the covered range; no clamping or extrapolation |
| Biome or river without that column | `NotSupportedException` |
| Base height without a `base_height` column | `NotSupportedException`; no inferred value |

`HasBiome`, `HasRiver` and `HasBaseHeight` say which layers loaded. For hand-written fixtures, the constructor takes row-major arrays (x fastest). If your test needs exact lookup, use `ReplayTerrain`. If it needs interpolation between regular samples, use `GridDumpTerrain`.

### Pinned native dumps

To use a native ValheimCLI dump as a pinned offline input, follow these steps:

1. Capture it with `WorldDump.CaptureAsync` (Valheim.Testing.GameSessions).
2. Load it through its manifest, not with `GridDumpTerrain.Load` alone.

`WorldDumpManifest.Verify()` checks the CSV against the manifest that the capture wrote. It checks these:

- the SHA-256 and the exact seven-column native header
- the layers and a complete grid
- the spacing, the bounds and ValheimCLI's lattice

The world UID, seed and game build come from the pinned session. The CSV holds no identity of its own. Do not fill in an absent field or convert `base_height` to metres. See [pinned world dumps](../reference/world-dump-contract.md).

### Layered dumps

`LayeredDumpTerrain.Load(layers, baseHeightLayer)` combines a coarse world layer with fine windows. It verifies every manifest. It refuses any of these:

- different world UIDs, seeds or game builds
- repeated layer names, or a `baseHeightLayer` that names no supplied layer
- grids of equal spacing that overlap, even at an edge

| Query | Answered by |
|---|---|
| Height, biome, river | The **finest containing grid**, even at its closed edge |
| `GetBaseHeight` | **Only** the named base-height lattice (`BaseHeightLayer`) |
| Outside every layer, or outside the base lattice for base height | Throws |
| `LayerAt(x, z)` | The grid that answers the query; its `Provenance` is the layer's name |
| `IsSampleNode(x, z)` | Whether the query landed on an exact CSV node |

Nodes keep the file's rounded values. Between nodes, height, river and base height are bilinear. Biome comes from the nearest node of the selected grid. The order of the manifests does not change the result.

```csharp
var terrain = LayeredDumpTerrain.Load(new[] {
    WorldDump.ReadManifest("dumps/world-128.json"), WorldDump.ReadManifest("dumps/site-8.json")
}, baseHeightLayer: "world-128");
float ground = terrain.GetHeight(siteX, siteZ);        // finest containing layer
float baseValue = terrain.GetBaseHeight(siteX, siteZ); // designated coarse lattice, unitless
bool exact = terrain.LayerAt(siteX, siteZ).IsSampleNode(siteX, siteZ);
```

## Render a terrain

`new TerrainRenderer(area, metresPerPixel)` draws any `ITerrain` over a `TerrainArea`. The render follows these rules:

- The area must be a whole number of pixels, at most 8192 a side.
- +x points right and +z points up.
- Each pixel samples its centre once.
- If the terrain refuses a coordinate, the render fails. No pixel is painted in place of missing data.

| Member | Draws |
|---|---|
| `TerrainColoring.Height` | Blue below `WaterLevel` (default 30 m, sea level); green, brown, white up to `HighHeight` |
| `TerrainColoring.Biome` | One fixed colour per biome |
| `Contours(interval, color)` | One-pixel lines at `WaterLevel` (sea level when null) and every `interval` metres around it |
| `Polyline`, `Point` | Overlays in world coordinates, clipped at the edge |

In height mode, `WaterLevel = null` draws every height as land. The render throws if `HighHeight` is not above `LowHeight`. It also throws if `WaterLevel` is below `LowHeight` or not below `HighHeight`. `Contours` works with either colouring. Layers are drawn in this order: contours, then polylines, then points.

```csharp
new TerrainRenderer(new TerrainArea(-512,-512,512,512), 2) { Coloring = TerrainColoring.Biome }
    .Contours(10, new RenderColor(90,74,48))
    .Polyline(road, new RenderColor(255,0,0)) // road: the (x, z) points a mod planned
    .Point(0,0,new RenderColor(0,0,0))
    .Render(GridDumpTerrain.Load("world.csv")).WritePng("review.png");
```

This writes `review.png` with uncompressed deflate blocks, at about three bytes per pixel. For the same heights, its bytes are identical on every platform and runtime.

This library keeps one palette. For a topographic review map from the same dump CSV, use ValheimCLI's [`examples/world-map.py`](https://github.com/tvongaza/valheimCLI/blob/e596a7d984feaaa695f5c2999964b64e23fbfd92/examples/world-map.py).

## Compare terrain sources

`TerrainParity.Compare(expected, actual, area, step, heightTolerance)` samples both sources at `MinX + i*step` and `MinZ + j*step`. Sampling runs up to and including the area's maximum. If the step matches a grid's spacing and origin, the samples land on its nodes.

Assert on `report.Passed`. If the comparison fails, print `report.ToString()`. The report gives the sample count, the failures, the largest difference and the worst points, largest first.

If either source refuses a coordinate, the comparison throws that source's exception. Choose an area that both sources cover.

| Option | Effect |
|---|---|
| `compareBiomes: true` | Requires equal biomes |
| `riverWeightTolerance` | Compares river weights within this tolerance; river width is not compared |
| `worstCount` | How many worst points the report lists (default 5) |

On its own nodes, a dump matches its source only to the precision of the file:

| Comparison | Minimum tolerance |
|---|---|
| `cli_world_dump` nodes vs generator: height (rounded to 0.1 m) | `heightTolerance` of at least 0.05 m |
| `cli_world_dump` nodes vs generator: river weight (rounded to 0.01) | `riverWeightTolerance` of at least 0.005 |
| Full-precision dump nodes vs its source | Exact |
| Between nodes | Add bilinear error, which depends on curvature |

## Static overrides

`StaticOverride` puts back the value it found, not a default you expect. For usage, see [Test mod logic that uses game types](../guides/test-logic-that-uses-game-types.md).

```csharp
using var plain = StaticOverride.Set(() => MyMod.Enabled, false)
    .AndKeep(() => MyMod.Counter)            // changed by the code under test; restored anyway
    .AndEnvironment("MYMOD_DEBUG", "1");     // null removes the variable
```

| Aspect | Behaviour |
|---|---|
| What it overrides | Static fields, static properties (private setters included) and environment variables |
| `Keep`, `AndKeep` | Record a value without changing it, then restore it |
| Dispose | Restores in reverse order, even when the test throws; a second dispose does nothing |
| Failed restore | The others still run; failures are reported together in an `AggregateException` |
| A step that throws | What the chain already set is restored before the error reaches you |
| Refused | A constant, readonly field, property without setter, or anything but `() => Type.Member` |
| Refused environment name | An empty name, or one containing `=` |

Statics are process-wide. Tests that override them must not run in parallel with tests that read them.

## Limits

The library models declared terrain and nothing else:

- Synthetic inputs are not Valheim's generator.
- A replay of a capture is not independent evidence of the game's terrain. Compare a capture with a declared expectation, not with itself.
- The library does not reproduce these: native noise, biome blending, physics, ZDO ownership, compiler serialization, paint texture sampling or Unity scheduling.
- A dump read offline is input, not evidence of native behaviour. A rendering of it is for human review.

To decide what you must check in the running game, see [Choose a test layer](../guides/choose-a-test-layer.md).
