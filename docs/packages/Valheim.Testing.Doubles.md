# Valheim.Testing.Doubles

[API reference](https://tvongaza.github.io/ValheimTesting/reference/). This page is the package's README; the versioned source commit is in the package metadata.

`Valheim.Testing.Doubles` lets a unit-test project compile and run a mod's pure-logic source files without Unity, Valheim, BepInEx or Jotunn. It is a source package: its files compile into your test project and stand in for the game's types under their real names. Your mod's source, linked unchanged, calls them as it would call the game. Use it for decisions, persistence formats, RPC handlers, registrations and anything else a mod computes from game types. For rendering, physics, timing and real networking, run a native check with [`Valheim.Testing.Game`](Valheim.Testing.Game.md).

**Example:** [ModWithTests](../../examples/ModWithTests/README.md) links real mod source and runs five tests. The step-by-step guide is [Test mod logic that uses game types](../guides/test-logic-that-uses-game-types.md).

## Install

Add the package to the unit-test project, not the mod project. It needs C# 10 and works on net48 and modern .NET. Reference it with `PrivateAssets="all"`, and do not also reference the game's assemblies: the doubles use the same names.

```xml
<PropertyGroup><LangVersion>10</LangVersion></PropertyGroup> <!-- or newer; net48 test legs default to C# 7.3 -->
<ItemGroup>
  <PackageReference Include="Valheim.Testing.Doubles" Version="[0.1.0]" PrivateAssets="all" />
  <Compile Include="../MyMod/Src/RoadMath.cs" /> <!-- the mod's pure-logic sources -->
</ItemGroup>
```

The doubles are process-wide statics, as the game's singletons are. Add this once per test project:

```csharp
[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

The released version to pin is in the [package table](../package-versions.md).

## Your first test

The mod's own code, compiled against Valheim in the plugin:

```csharp
// MyMod/DrySiteRule.cs
internal static bool CanPlace(Vector3 position, float waterLevel, float clearance)
    => WorldGenerator.instance is { } world
        && world.GetHeight(position.x, position.z) >= waterLevel + clearance;
```

The test installs a world with `ValheimWorldScope` and calls the real method:

```csharp
// MyMod.Tests/DrySiteTests.cs
using MyMod;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

public class DrySiteTests
{
    [Theory]
    [InlineData(29f, false)]   // Under water.
    [InlineData(31.5f, true)]  // Exactly the promised minimum clearance.
    [InlineData(40f, true)]    // Safely above it.
    public void RequiresGroundAtLeastClearanceAboveWater(float ground, bool expected)
    {
        using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(ground));
        Assert.Equal(expected, DrySiteRule.CanPlace(new Vector3(0, 100, 0), 30f, 1.5f));
    }
}
```

Run it from your mod checkout:

```sh
dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
```

A failed assertion returns a nonzero exit code.

## What is in the package

Every type is `partial`. If your mod calls a member the doubles lack, add it in your own file and keep only mod-specific behaviour there. Two `Heightmap` hooks carry a mod's terrain logic into the rebuild: `ModBaseHeight` and `ModTerrainPass`.

The member index, [Valheim.Testing.Doubles.members.txt](Valheim.Testing.Doubles.members.txt), lists every type and member, one per line. It marks the members the game does not have (`[TestOnly]` in source): test switches, recorders and fixture shorthands such as `ZNet.Peers` or `Heightmap.Registered`. Mod code must not call those; it compiles against the doubles and fails against the game. Members marked `internal` are the doubles' own plumbing. A release's diff of the index is the list of names that may collide with your own partials.

| Member kind | Who calls it |
|---|---|
| Unmarked | Mod code and tests; the game has it |
| `[TestOnly]` | Tests only |
| `internal` | The doubles themselves |

### Read-only singletons

A member that is read-only in the game is read-only in the doubles. Signatures ending in `{ get; }` cannot be assigned by mod code. To set `ZNet.instance`, `WorldGenerator.instance`, `HeightmapBuilder.instance`, `UnityEngine.Time` or another game read-only member, use a `ValheimWorldScope` builder and dispose the scope. `WithClock(120f)` sets game time in seconds without running behaviours. If a test needs a frame, call the `[TestOnly]` `Object.RunFrame`.

## One world per test: ValheimWorldScope

`ValheimWorldScope` gives each test its own world. It records every static the doubles keep and puts them back on dispose, also when the test throws. A second dispose does nothing. Disposing also ends the frame (`UnityEngine.Object.EndOfFrame()`), so an object the test destroyed is gone before the next test.

It records: the singletons (`WorldGenerator.instance`, `ZDOMan.instance`, `ZoneSystem.instance`, `ZNetScene.instance`, `ZNet.instance`, `ZRoutedRpc.instance`, `ObjectDB.instance`, Jotunn's `NetworkManager.Instance`); the loaded heightmaps and terrain modifiers; the scene's Unity objects and pending destroys; the log capture, `Terminal.commands`, the cheat gates, `Player.m_localPlayer` and the player list; localization, input, preferences, the config disk, the clock and the random generator `Random.InitState` installed; plus the server and dedicated flags of the `ZNet` it found. It restores the contents of the game's readonly heightmap, player and terrain-modifier lists without replacing those list objects.

Chain builders to install fresh objects:

```csharp
using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(30f)).WithZdos().WithNetwork(server: true);
```

| Builder | What the test gets |
|---|---|
| `WithWorld`, `WithTerrain` | A fresh world |
| `WithZdos` | A fresh `ZDOMan` |
| `WithZoneSystem` | A fresh `ZoneSystem` |
| `WithScene` | An empty scene: no prefabs, Unity objects or pending destroys |
| `WithNetwork(server)` | A `ZNet` with no peers, a new `ZRoutedRpc`, a new Jotunn `NetworkManager` |
| `WithCommands` | An empty console-command table |
| `WithCheats()` | Both cheat gates open; restored on dispose |
| `WithTerrainModifiers()` | A fresh live terrain-modifier list |
| `WithConfigFiles()` | An empty in-memory config disk |
| `WithClock(120f)` | Game time in seconds, without running behaviours |
| `WithUnityOrder(UnityOrder.Reversed)` | Unity's unordered phases run backwards; restored on dispose |
| `AsServer` | The server role flag |
| `WithLocalPlayer(position)` | A `Player` (none by default, as on a dedicated server) |
| `RegisterHeightmap` | A loaded zone; zones add up, loading a zone again replaces it |
| `UnloadHeightmap` | The zone unloaded |
| `CaptureLog` | The captured log |

The scope restores references, not contents. Nothing is deep-copied. If you change an object the scope did not install, such as adding a peer to the `ZNet` that was already there, the change outlives the test. Install a fresh one with a builder instead.

The scope covers the game's statics, not your mod's. Scope those with `Valheim.Testing.StaticOverride`:

```csharp
using var plain = StaticOverride.Set(() => MyMod.Enabled, false);
```

## Roles: server, host, client and main menu

Presets set the role flags mod code branches on and install a fresh world for it:

| Preset | What the test gets |
|---|---|
| `AsDedicatedServer()` | Server and dedicated, no local player |
| `AsHost(position)` | Server, not dedicated, a local player owned by this session |
| `AsClient(position)` | A client with a local player |
| `AtMainMenu()` | No `ZNet`, `ZDOMan`, `ZNetScene` or world; a preview `Player` whose view has no ZDO |
| `AddRemotePlayer(peerId, position, name)` | Another peer's player; on a server also a connected peer |

`Player.GetAllPlayers()` lists the main-menu preview player as the game's does.

A plugin loads as BepInEx's chainloader loads it: add every plugin to one manager object with `manager.AddComponent<T>()`, where `manager = new GameObject("BepInEx_Manager")`. Its `Awake` runs at once and a plugin finds its siblings with `GetComponent`.

`PlatformPrefs` under `AsDedicatedServer()` is on the [Unity page](../reference/doubles-unity.md).

## Where each behaviour is documented

| Topic | Page |
|---|---|
| ZDOs, `ZNetView`, `ZNetScene`, saves and restarts | [World state](../reference/doubles-world.md) |
| Heightmaps, terrain rebuild, `TerrainSnapshot`, `TerrainAssert` | [World state](../reference/doubles-world.md) |
| Global keys, terrain modifier order, location placement | [World state](../reference/doubles-world.md) |
| `ObjectDB`, `ZNetScene` registries, `Utils`, `HeightmapBuilder`, `DropTable`, `Inventory` | [World state](../reference/doubles-world.md) |
| `ZPackage` bytes, routed and per-object RPCs, Jotunn RPCs | [Network](../reference/doubles-network.md) |
| `Terminal` console commands and the cheat gates | [Network](../reference/doubles-network.md) |
| Peer connections, the join handshake, Steam and crossplay identities | [Network](../reference/doubles-network.md) |
| Unity objects, their order, destroyed objects | [Unity, config and Harmony](../reference/doubles-unity.md) |
| `Localization`, BepInEx `ConfigFile`, config sync | [Unity, config and Harmony](../reference/doubles-unity.md) |
| Harmony attributes, `Traverse`, `AccessTools`, the `ModuleInitializer` polyfill | [Unity, config and Harmony](../reference/doubles-unity.md) |

## Limits

The doubles model only the behaviour those pages list and the members mod logic has needed so far. Anything else is a plain field or a no-op, not the game. Unity objects have no physics simulation or rendering: components keep their settings and answer queries, nothing more. Terrain is the rebuild and the compiler, not the game's mesh. Networking is in-process: peers connect only through linked socket doubles, and RPCs follow the delivery and routing rules without real timing, loss or reordering. Test what the game does natively with [`Valheim.Testing.Game`](Valheim.Testing.Game.md) against a real server.
