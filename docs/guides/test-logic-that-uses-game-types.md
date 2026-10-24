# Test mod logic that uses game types

`Valheim.Testing.Doubles` is a source package. Its files compile into your test project and stand in for Unity, Valheim, BepInEx and Jotunn types under their real names. You link your mod's source file unchanged, and it calls the doubles as it would call the game. You need no game, Steam or server.

## When to use this

| Your code | Use |
|---|---|
| Calls game types (decisions, save formats, RPC handlers, registrations) | `Valheim.Testing.Doubles`, this guide |
| Calls no game type | `Valheim.Testing` alone: [test terrain decisions](test-terrain-decisions.md) |
| Depends on rendering, physics, timing or real networking | A native check with [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md) |

If you are not sure which applies, see [choose a test layer](choose-a-test-layer.md).

The worked example is [ModWithTests](../../examples/ModWithTests/README.md). It runs five tests against one mod file.

## Steps

### 1. Lay out the projects

Keep the game out of the test project:

```text
MyMod/                         the plugin: imports Valheim.GameReferences.props/.targets; builds only with a game install
MyMod.TestAdapter/             optional, game-side: the same imports plus UseValheimCli=true
MyMod.Tests/                   unit tests: <Compile Include="../MyMod/..." Link=...> of pure-logic sources,
                               Valheim.Testing.Doubles, no game references, no ProjectReference to MyMod
build/Valheim.GameReferences.props, build/Valheim.GameReferences.targets
.github/workflows/tests.yml    builds and tests MyMod.Tests only
```

- The test project never references the plugin project. A `ProjectReference` to `MyMod` would build the plugin, and with it the game references.
- Link source files instead. Keep game-bound code, such as Harmony patches and `Awake`, out of the linked files.
- Only the game-side projects import the [game references](../../tools/game-references/README.md). Its targets refuse a project that references both the game and the doubles.

The workflow file is covered in [run tests in CI](run-tests-in-ci.md).

### 2. Link the source and reference the doubles

Add these lines to the test project. The [full file](../../examples/ModWithTests/MyMod.Tests/MyMod.Tests.csproj) is in the example.

```xml
<PropertyGroup><LangVersion>10</LangVersion></PropertyGroup> <!-- or newer; net48 test legs default to C# 7.3 -->
<ItemGroup>
  <!-- No game assemblies or reference to the production plugin project. -->
  <Compile Include="../MyMod/DrySiteRule.cs" Link="Mod/DrySiteRule.cs" />
  <PackageReference Include="Valheim.Testing.Doubles" Version="[0.3.0]" PrivateAssets="all" />
</ItemGroup>
```

The doubles work on net48 and modern .NET. The current release to pin is in the [package table](../package-versions.md).

### 3. Give each test its own world

This is the mod's own code. The plugin compiles it against Valheim:

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

// The game doubles use process-wide singletons, just as the game does.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

public class DrySiteTests
{
    [Theory]
    [InlineData(29f, false)]   // Under water.
    [InlineData(31.5f, true)]  // Exactly the promised minimum clearance.
    [InlineData(40f, true)]   // Safely above it.
    public void RequiresGroundAtLeastClearanceAboveWater(float ground, bool expected)
    {
        using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(ground));
        Assert.Equal(expected, DrySiteRule.CanPlace(new Vector3(0, 100, 0), 30f, 1.5f));
    }
}
```

Add the `CollectionBehavior` line once per test project.

On dispose, the scope puts back every static the doubles keep, even when the test throws. Disposing also ends the frame, so objects the test destroyed are gone before the next test.

Chain builders to install fresh objects:

```csharp
using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(30f)).WithZdos().WithNetwork(server: true);
```

| Builder | What the test gets |
|---|---|
| `WithWorld`, `WithTerrain` | A fresh world |
| `WithScene` | An empty scene: no prefabs, Unity objects or pending destroys |
| `WithNetwork(server)` | A `ZNet` with no peers, a new `ZRoutedRpc`, a new Jotunn `NetworkManager` |
| `WithCommands` | An empty console-command table |
| `WithClock(120f)` | Game time in seconds, without running behaviours |

Other chainable builders are `WithZdos`, `WithZoneSystem` and `AsServer`. Four members end the chain: `WithLocalPlayer(position)` returns the new `Player` (there is none by default), `RegisterHeightmap` returns the `Heightmap`, `UnloadHeightmap` returns nothing and `CaptureLog` returns the captured `List<string>`. For role presets such as `AsDedicatedServer()`, see [roles](../packages/Valheim.Testing.Doubles.md#roles-server-host-client-and-main-menu).

Set singletons such as `ZNet.instance` or `WorldGenerator.instance` through a builder, never by assignment. A member that is read-only in the game is read-only in the doubles too. If a test needs a frame, call the doubles' `[TestOnly]` `Object.RunFrame`.

The scope restores references, not contents. Suppose you change an object the scope did not install, such as adding a peer to an existing `ZNet`. That change outlives the test. Install a fresh object with a builder instead.

### 4. Scope your own statics

The scope covers the game's statics, not your mod's. Scope those with `StaticOverride`. It puts back the value it found, not a default you hard-code:

```csharp
using var plain = StaticOverride.Set(() => MyMod.Meander, 0f)
    .And(() => MyMod.Enabled, false)
    .AndKeep(() => MyMod.Counter)            // changed by the code under test; restored anyway
    .AndEnvironment("MYMOD_DEBUG", "1");     // null removes the variable
```

Dispose restores in reverse order, even when the test throws. `StaticOverride` refuses a constant, a readonly field, a property without a setter, and anything but `() => Type.Member`.

### 5. Run the tests

From your mod checkout, or from the example directory:

```sh
dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
```

For the example, success is **5 passed, 0 failed**, with exit code 0. A failed assertion returns a nonzero exit code. To run the same tests on `net48` as well, see [the test runner](../../tools/test-runners/README.md).

## Check the result

Prove that the tests can fail. In `MyMod/DrySiteRule.cs`, change `>=` to `>` and run again. The boundary case (`31.5f`) must fail. Then restore the rule.

Never copy the rule into the tests, or you test a substitute.

A pass proves the mod's decision on declared inputs. It does not prove that the real world is dry, that a collider exists, or that a client receives terrain. The doubles model only [what they list](../packages/Valheim.Testing.Doubles.md#where-each-behaviour-is-documented). Everything else is a plain field or a no-op ([limits](../packages/Valheim.Testing.Doubles.md#limits)).

## Problems

| Symptom | Fix |
|---|---|
| A game member is missing | Every type is `partial`. Add the member in your own file, with only mod-specific behaviour. For reusable behaviour, [contribute](../../CONTRIBUTING.md) it with a test. |
| Duplicate-definition errors | Remove game assemblies from the test project. Delete your own stubs of these types, or extend the doubles through `partial`. |
| Your partial does not merge | Use the game's namespace: `partial class Transform` goes in `namespace UnityEngine`. `Mathf` is `partial struct Mathf`. |
| `go.Position.x = 1` fails with CS1612 | `GameObject.Position`/`Rotation` and `Transform.position` are properties. You cannot pass them by `ref`/`out` either. |
| Constructor already defined | `ZDOMan`, `ZRoutedRpc`, `WorldGenerator`, `ManualLogSource` and `CustomRPC` declare one. Delete yours. |
| `ModuleInitializerAttribute` defined twice on net48 | Delete yours or define `VALHEIM_TESTING_NO_POLYFILLS`. |
| A second RPC registration throws | Registering a name twice throws, as in the game. Install a fresh `ZRoutedRpc` per test with `WithNetwork()`. |
| Tests pass alone but fail together | Add `DisableTestParallelization`, use a scope in every test, and scope your statics with `StaticOverride`. |
| Code compiles in tests but fails against the game | You called a `[TestOnly]` member, such as `ZNet.Peers`. Check the [member index](../packages/Valheim.Testing.Doubles.members.txt). |
| Mod code checks for `assembly_valheim` | [Put the doubles in their own assembly](#put-the-doubles-in-an-assembly-named-assembly_valheim). |

### Put the doubles in an assembly named assembly_valheim

Some mod code asks where a type comes from, for example `type.Assembly.GetName().Name == "assembly_valheim"` or `Type.GetType("ZNetScene, assembly_valheim")`. For that code, build the doubles as a class library named after the game's assembly:

```xml
<!-- MyMod.Doubles/MyMod.Doubles.csproj -->
<PropertyGroup><AssemblyName>assembly_valheim</AssemblyName><RootNamespace></RootNamespace><LangVersion>10</LangVersion></PropertyGroup>
<ItemGroup><PackageReference Include="Valheim.Testing.Doubles" Version="[0.3.0]" /></ItemGroup>
```

```csharp
// MyMod.Doubles/AssemblyInfo.cs
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MyMod.Tests")]
```

- Point the test project at this project instead of the package.
- Have the test project reference `Valheim.Testing` directly. Pin it to at least the version the Doubles package lists as its dependency on NuGet.org.
- Put your partial additions in the `assembly_valheim` project, because a partial type cannot span assemblies.

## Related

- [ModWithTests](../../examples/ModWithTests/README.md): the complete runnable example
- [`Valheim.Testing.Doubles` reference](../packages/Valheim.Testing.Doubles.md): what the doubles model
- [`Valheim.Testing`](../packages/Valheim.Testing.md): terrain inputs and `StaticOverride`
- [Run tests in CI](run-tests-in-ci.md)
- [Game references](../../tools/game-references/README.md) for the plugin and adapter
- [Choose a test layer](choose-a-test-layer.md)
