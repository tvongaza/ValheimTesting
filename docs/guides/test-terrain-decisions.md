# Test a terrain decision without the game

## When to use this

Use this guide when your mod reads or writes terrain: heights, slopes, biomes or zone seams. You declare the ground and load zones as the game holds them. Then you run your mod's real code and assert what changed. No Valheim, Steam or ValheimCLI is needed.

You need two packages:

- `Valheim.Testing` declares the ground. It targets netstandard2.0 and has no Unity dependency.
- `Valheim.Testing.Doubles` holds the zones.

Pin both from the [package table](../package-versions.md). Set up the doubles as [Setup](../packages/Valheim.Testing.Doubles.md#setup) describes. If you are unsure this is the right layer, see [choose a test layer](choose-a-test-layer.md).

Derive expected values independently of the code under test. Declare them in the fixture, and never read them from your mod's output.

## Steps

### 1. Declare the ground

Pick a terrain type:

| Type | Supplies |
|---|---|
| `PlaneTerrain` | A flat or sloping surface with a chosen biome |
| `SyntheticTerrain` | A deterministic island, ridge and meandering river |
| `CompositeTerrain` with `TerrainRegion` | Ordered regions over a base terrain |

This declares a slope with a terrace:

```csharp
// A slope with a declared terrace. Last matching region wins for every fact.
var terrain = new CompositeTerrain(new PlaneTerrain(40,.125f),
    TerrainRegion.Rectangle(28,-4,36,4,new PlaneTerrain(46)));
```

The last matching region replaces height, biome and river facts together. It does not blend them. A rectangle includes its minimum coordinates and excludes its maximum coordinates. Region terrain uses world coordinates. `CompositeTerrain` copies the region list but does not freeze the underlying terrains, so keep predicates and inputs pure and stable when several tests read one composed terrain.

For any other boundary, pass a predicate. This block declares a cliff at x=100 and a terrace on top of it:

```csharp
var ground = new CompositeTerrain(new PlaneTerrain(40, .25f),
    new TerrainRegion((x,z) => x >= 100,
        new PlaneTerrain(80, 0, 0, TerrainBiome.Mountain)),
    TerrainRegion.Rectangle(120,-8,140,8,new PlaneTerrain(72)));
```

The next steps use `terrain` from the first block.

### 2. Load zones, write, and say what changed

`ValheimWorldScope` generates zones from your terrain. `RegisterHeightmap` loads one zone, with its `Heightmap` and `TerrainComp`. Zone (0,0) spans x -32..32 and zone (1,0) spans x 32..96, so they share the column at x = 32. Each zone keeps its own copy of the shared edge, as in the game. Nothing repairs a seam for you.

```csharp
using (var scope = new ValheimWorldScope().WithTerrain(terrain).WithZdos())
{
    var west = scope.RegisterHeightmap(new Vector2s(0,0));
    var east = scope.RegisterHeightmap(new Vector2s(1,0));
    west.RebuildTerrain(); east.RebuildTerrain();
    TerrainAssert.SeamAgrees(west, east);
    var eastBefore = TerrainSnapshot.Of(east.m_terrainComp!);
    // Stand-in for a mod's terrain writer: raise the shared column by 1 m, but only in the west zone's compiler.
    // The game applies and saves a delta only where its modified flag is set, so a writer sets both.
    for (int z=0; z<=64; z++) Raise(west.m_terrainComp!, z*65+64);
    west.RebuildTerrain();
    if (!Fails(() => TerrainAssert.SeamAgrees(west, east))) throw new Exception("A write to one side of a seam went unnoticed.");
    TerrainAssert.Unchanged(eastBefore, east.m_terrainComp!, "east zone");
}
```

In your test, replace the stand-in loop with a call to your mod's writer. [`Program.cs`](../../examples/SharedWorld/Program.cs) defines `Raise` and `Fails`. For a mod-side example, see [ProceduralRoads' `SharedZoneWriterTests`](https://github.com/tvongaza/ProceduralRoads/blob/3354558/ProceduralRoads.Tests/SharedZoneWriterTests.cs), which calls its real terrain writer and checks the seam.

| Call | Checks |
|---|---|
| `TerrainAssert.Unchanged` | A zone matches its snapshot |
| `TerrainAssert.OnlyChangedWithin` | A write stayed inside an area |
| `TerrainAssert.SeamAgrees(west, east)` | Two neighbours agree on their shared vertices |

The full assertion API is in [terrain snapshots and assertions](../packages/Valheim.Testing.Doubles.md#terrain-snapshots-and-assertions).

### 3. Optional: dumps, pictures and parity

To use a grid dump as input, load it as an `ITerrain`:

```csharp
var dump = GridDumpTerrain.Load("terrace.csv");
```

For CSV rules and query answers, see [read a grid dump](../packages/Valheim.Testing.md#read-a-grid-dump). For a native dump used as a pinned input, see [pinned world dumps](../world-dump-contract.md).

To draw a picture for human review, render the terrain to a PNG:

```csharp
var picture = new TerrainRenderer(new TerrainArea(0,-8,64,8), .5f) { LowHeight = 20, HighHeight = 60 }
    .Polyline(new[] { (28f,-4f), (36f,-4f), (36f,4f), (28f,4f), (28f,-4f) }, new RenderColor(255,0,0))
    .Render(dump);
if (args.Length > 0) picture.WritePng(args[0]);
```

The picture shows what the fixture declares, not what the game does. For colourings, contours and overlays, see [render a terrain](../packages/Valheim.Testing.md#render-a-terrain).

To check that two terrain sources agree, compare them over an area:

```csharp
var area = new TerrainArea(4,-8,64,8);
var parity = TerrainParity.Compare(terrain, dump, area, 4, 0);
if (!parity.Passed) throw new Exception(parity.ToString());
```

For tolerances and the report, see [compare terrain sources](../packages/Valheim.Testing.md#compare-terrain-sources).

## Check the result

Run the [SharedWorld](../../examples/SharedWorld/README.md) example from the repository root:

```sh
dotnet run --project examples/SharedWorld -c Release
```

It exits 0 and prints:

```text
PASS: composed terrain, a one-sided seam write caught by the terrain doubles and then fixed, grid dump parity with a shifted-grid control. No game used.
```

The example keeps a negative control for each check: a write to one side of a seam must fail `SeamAgrees`, and a dump shifted one node east must fail parity. Keep one in your own tests.

## What this does not prove

These fixtures model declared terrain and mutable zone state. They do not reproduce native noise, biome blending, physics, ZDO ownership, compiler serialisation, paint sampling or Unity scheduling. For those boundaries, keep a few native checks and a human review of how the result looks and walks.

## Problems

| Symptom | Fix |
|---|---|
| `SeamAgrees` throws `TerrainAssertException` after your write | Apply the same write to the neighbour's copy of the shared vertices. |
| A height delta has no effect | Set the modified flag, such as `m_modifiedHeight`, with the delta. |
| `GridDumpTerrain` throws `InvalidDataException` naming a line | Fix the ragged grid, uneven spacing, bad number or unknown biome. |
| A dump query throws `ArgumentOutOfRangeException` | Query inside the grid. A dump never clamps or extrapolates. |
| A dump query throws `NotSupportedException` | Add the missing biome, river or `base_height` column, or skip that query. |
| Parity against the generator fails at `cli_world_dump` nodes | Use a height tolerance of at least 0.05 m and a `riverWeightTolerance` of at least 0.005. |
| A render fails | The terrain refused a coordinate. Keep the render area inside the terrain. |

## Related

- [SharedWorld](../../examples/SharedWorld/README.md): the runnable example for this guide.
- [`Valheim.Testing`](../packages/Valheim.Testing.md): reference for terrain types, dumps, rendering and parity.
- [Terrain snapshots and assertions](../packages/Valheim.Testing.Doubles.md#terrain-snapshots-and-assertions) in `Valheim.Testing.Doubles`.
- [Pinned world dumps](../world-dump-contract.md): capture and verify a native dump.
- [ObserveCheck `capture`](../../examples/ObserveCheck/README.md#capture-record-a-bounded-grid-for-exact-replay): record a bounded grid for exact replay.
- [Choose a test layer](choose-a-test-layer.md).

To add a new shape or state model, follow the [synthetic-world contribution recipe](../../CONTRIBUTING.md#extend-the-synthetic-world).
