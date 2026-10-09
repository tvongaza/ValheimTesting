# Valheim.Testing

[Current preview API reference](https://tvongaza.github.io/ValheimTesting/). The package's README is this guide; use its versioned source commit from the package metadata when checking an older preview.

*Assistant-written (Claude).*

`Valheim.Testing` is the pure library: declared terrain, exact replay of captured samples, pinned world-dump fixtures, terrain rendering and parity checks, and `StaticOverride` for scoped changes to statics and environment variables. It targets netstandard2.0, so it runs in a net48 (Mono) test leg as well as on modern .NET, and has no Unity, Valheim or ValheimCLI dependency. Use it in a mod's unit tests to feed declared inputs into the mod's real decisions; use [`Valheim.Testing.Doubles`](Valheim.Testing.Doubles.md) when those decisions call game types, and [`Valheim.Testing.Game`](Valheim.Testing.Game.md) for what only the running game shows.

**Example:** [SharedWorld](../../examples/SharedWorld/README.md) composes terrain, checks a zone seam with the terrain doubles and checks a grid dump against its source, with a negative control for each, and no game. The released version to pin is in the [package table](../getting-started.md#package-versions-and-feeds).

## Types a test calls

| Type | What it is | Reference |
|---|---|---|
| `ITerrain`, `PlaneTerrain`, `SyntheticTerrain` | Height, biome and river facts at any (x, z): a flat or sloping plane, or the deterministic island, ridge and meandering river | [Declare the ground](../shared-world.md#declare-the-ground) |
| `CompositeTerrain`, `TerrainRegion` | Ordered regions over a base terrain; the last matching region replaces every fact | [Declare the ground](../shared-world.md#declare-the-ground) |
| `ReplayTerrain` | Exact lookup of captured samples (from `TerrainCapture` in Valheim.Testing.Game); a missing sample or biome is refused, never invented | [ObserveCheck `capture`](../../examples/ObserveCheck/README.md#capture-record-a-bounded-grid-for-exact-replay) |
| `GridDumpTerrain`, `LayeredDumpTerrain`, `WorldDumpManifest` | An evenly spaced CSV grid (the layout `cli_world_dump` writes), and a pinned native dump verified against the manifest its capture wrote | [Read a grid dump](../shared-world.md#read-a-grid-dump), [pinned world dumps](../world-dump-contract.md) |
| `TerrainRenderer` | A deterministic PNG of any `ITerrain`, coloured by height or biome, with contours, points and polylines | [Render a terrain for review](../shared-world.md#render-a-terrain-for-review) |
| `TerrainParity` | Two terrain sources compared over an area within a tolerance, with the worst points | [Compare two terrain sources](../shared-world.md#compare-two-terrain-sources) |
| `StaticOverride` | A scoped change to static fields, static properties and environment variables, restored on dispose | [below](#static-overrides) |

Offline audits read a world dump, draw it for review and check that two terrain sources agree. A dump read offline is input, not evidence of native behaviour, and a rendering of it is for human review.

```csharp
using Valheim.Testing;

var ground = new CompositeTerrain(new PlaneTerrain(40, .125f),
    TerrainRegion.Rectangle(28, -4, 36, 4, new PlaneTerrain(46)));   // a slope with a declared terrace
var dump = GridDumpTerrain.Load("terrace.csv");                         // the same ground, dumped
var parity = TerrainParity.Compare(ground, dump, new TerrainArea(4, -8, 64, 8), 4, 0.05f);
if (!parity.Passed) throw new Exception(parity.ToString());
```

## Static overrides

Mods keep settings, switches and caches in statics. A test that changes one must put back the value it found, not the default it expects; a hard-coded reset silently changes every later test when the default moves. `StaticOverride` does this for static fields, static properties (private setters included) and environment variables:

```csharp
using var plain = StaticOverride.Set(() => MyMod.Meander, 0f)
    .And(() => MyMod.Enabled, false)
    .AndKeep(() => MyMod.Counter)            // changed by the code under test; restored anyway
    .AndEnvironment("MYMOD_DEBUG", "1");     // null removes the variable
```

Dispose restores in reverse order, also when the test throws. A restore that fails does not stop the others; the failures are reported together afterwards. A constant, a readonly field, a property without a setter or anything but `() => Type.Member` is refused when the override is created. Statics are process-wide, so tests that override them must not run in parallel with tests that read them. The doubles' `ValheimWorldScope` restores the game's statics through the same chain; a mod's own statics are the test's to scope with `StaticOverride`.

## Limits

Synthetic inputs are not Valheim's generator, and a replay of a capture is not independent evidence of the game's terrain: compare a capture with a declared expectation, not with itself. The library models declared terrain and nothing else: no native noise, biome blending, physics, ZDO ownership, compiler serialization or paint sampling. Between grid nodes, heights are bilinear approximations; biomes come from the nearest node. See [evidence limits and next layers](../shared-world.md#evidence-limits-and-next-layers).
