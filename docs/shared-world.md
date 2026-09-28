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

## Call real mod code

Keep adapters, expected outcomes and mod-specific assertions in the mod repository:

- [Roads shared-zone writer test](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.Tests/SharedZoneWriterTests.cs) feeds declared slope/paint into its own compiler double, calls the real road writer in both zone orders and checks the shared edge, an earlier off-road edit, paint preservation and repeat application. [TerrainTestWorld](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.Tests/TerrainTestWorld.cs) is the thin game-type adapter used by existing grade/search tests.
- [MWL shared-zone conversion test](https://github.com/tvongaza/MoreWorldLocations_All/blob/review/testing-adapter-ready/MoreWorldLocations.Tests/SharedZoneConversionTests.cs) runs the real authored level/smooth/paint conversion across two zones, checks matching edge results and untouched regions, and verifies that the same operation is not applied twice.

The library does not call either mod or duplicate its grading, smoothing, terrain compiler, persistence ledger or paint rules. Derive expected outcomes independently. Compare complete sample sets where completeness matters; do not turn missing input into zero.

## Evidence limits and next layers

These fixtures model declared terrain and mutable state. They do not reproduce native noise, biome blending, physics, ZDO ownership, compiler serialization, paint texture sampling or Unity scheduling. Keep a few independent native checks for those boundaries and human review for how the result looks and walks. The previously measured Roads paint/reload campaign remains separate evidence; it did not validate every synthetic shape introduced here.

[TerrainCapture](../examples/TerrainCapture/README.md) now imports validated bounded grids for exact replay. Its new native capability check remains open. General diagnostic rendering remains a follow-up; no implicit interpolation was added.

To contribute a new shape, state model or consumer example, follow the [synthetic-world contribution recipe](../CONTRIBUTING.md#extend-the-synthetic-world). Prefer a small reusable contract and independent expectations over copying a mod's fake game types into this library.
