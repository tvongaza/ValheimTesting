# Get started

Add one unit test to your BepInEx mod and run it without Valheim. Unit tests need the .NET 10 SDK and nothing else, on any OS; Visual Studio 2026 with the **.NET desktop development** workload brings it. Native checks run on the Windows PC where Steam installed Valheim; [the last section](#what-a-native-check-needs) says what they need.

## Steps

### 1. Run the example

Clone the repository and run the example project as it is:

```sh
git clone https://github.com/tvongaza/ValheimTesting.git
cd ValheimTesting/examples/ModWithTests
dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
```

It passes with **5 passed, 0 failed** and exits 0. The example is a miniature mod: `MyMod/` holds one source file, and `MyMod.Tests/` links that file and tests it with `Valheim.Testing.Doubles`, which supplies the Unity, Valheim, BepInEx and Jotunn types the file uses.

### 2. Add the test project to your mod

Copy [`MyMod.Tests/`](../examples/ModWithTests/MyMod.Tests/MyMod.Tests.csproj) next to your mod project. In its `.csproj`, change the `Compile Include` to one source file of your mod and keep the Doubles `PackageReference`:

```xml
<Compile Include="../YourMod/YourRule.cs" Link="Mod/YourRule.cs" />
<PackageReference Include="Valheim.Testing.Doubles" Version="[0.1.0-rc.1]" PrivateAssets="all" />
```

Reference no game assembly and no plugin project: the doubles have the game's type names. Keep Harmony patches and `Awake` out of the linked file.

### 3. Write the test

Replace `DrySiteTests.cs` with a test of a small decision from your mod; in this excerpt, `DrySiteRule` stands for your type. `ValheimWorldScope` installs synthetic terrain and restores the previous world on disposal. This is the first of the example's tests:

```csharp
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

From your mod checkout, run it:

```sh
dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
```

A passing run exits 0. A failed assertion returns nonzero. Visual Studio 2026, Rider and VS Code with C# Dev Kit discover the project with no extra setup. If the project also targets `net48`, Rider needs Mono on macOS and Linux; see [run tests in CI](guides/run-tests-in-ci.md).

<!-- TODO(docs): capture output -->

### 4. Run it in GitHub Actions

Copy the example's [workflow](../examples/ModWithTests/.github/workflows/tests.yml) to `.github/workflows/tests.yml` in your repository and change the project path if yours differs. It installs the .NET 10 SDK and runs the same `dotnet test` on a hosted Ubuntu runner, with no game and no secrets. It runs on pushes to `main`, on pull requests and by hand ([run tests in CI](guides/run-tests-in-ci.md)).

## Check the result

For a control, change `>=` to `>` in [DrySiteRule.cs](../examples/ModWithTests/MyMod/DrySiteRule.cs): the boundary case must fail. Restore the rule afterwards.

A passing test proves the mod's decision on declared inputs. It does not prove the real world is dry, that a collider exists, or that a client receives terrain. [Choose a test layer](guides/choose-a-test-layer.md) matches each behaviour to a layer.

## Code without game types

If the code under test uses no Unity or Valheim type, reference `Valheim.Testing` from your existing test project instead and pass an `ITerrain` into the real production method:

```xml
<PackageReference Include="Valheim.Testing" Version="[0.1.0-rc.1]" />
```

Every other package and version is in the [package table](package-versions.md).

## Problems

| Problem | Cause | Fix |
|---|---|---|
| A package does not restore | The pin names an unpublished version | Pin an exact version from the [package table](package-versions.md) |
| The compiler reports a missing game member | The doubles do not model it | Read the [doubles contract](packages/Valheim.Testing.Doubles.md); extend the partial type locally |
| Duplicate or conflicting game types | The test project references Unity, Valheim or the plugin DLL | Link the source files instead; reference no game assembly |
| Tests fail only when run together | The doubles are process-wide singletons | Keep `[assembly: CollectionBehavior(DisableTestParallelization = true)]` |
| A mod or adapter build cannot find the game | Your Steam library is not in the default folder | Pass `-p:ValheimPath=<folder>`, or set `VALHEIM_PATH` before starting the IDE ([game references](../tools/game-references/README.md#use-them)) |
| A test adapter build asks for `CliDll` | The adapter compiles against the exact ValheimCLI core it runs with | Pass `-p:CliDll=<path>`, or set `CliDll` before starting the IDE ([game references](../tools/game-references/README.md#use-them)) |

More in [Test logic that uses game types](guides/test-logic-that-uses-game-types.md).

## What a native check needs

One Windows PC with Valheim and the free Valheim Dedicated Server tool from Steam, the Steam client running and signed in, and PowerShell 7 ([host shells](reference/platforms.md#host-shells)). A `--server-only` run does not need Steam. `valheim-test` stages a disposable profile with its pinned BepInEx loader and ValheimCLI for each install and leaves the install untouched. <!-- TODO(docs): verify after #711 -->

```sh
dotnet tool install Valheim.Testing.NativeSmoke --version 0.1.0-rc.1 --global
valheim-test env preflight
valheim-test server-load --mod /path/to/ServerMod.dll
```

<!-- TODO(docs): capture output -->

`env preflight` checks this machine before anything is copied: the run journal and host lock, game processes, ValheimCLI ports, the client desktop and whether Steam is running. `server-load` loads the mod on an owned dedicated server, joins one clean client, prints `SERVER_JOIN_PASS` and writes `result.json` and `junit.xml` under a new `valheim-test-runs/` directory in this machine's ValheimTesting data root. From a directory with exactly one mod project, you can omit `--mod`.

Next: [Debug a mod load or mod conflict](guides/check-a-mod-loads.md) walks through the first run; [one Windows PC](packages/Valheim.Testing.Game.md#on-one-windows-pc) describes the setup; read the [runtime hygiene checklist](runtime-hygiene.md) before you launch. Other hosts are in [platforms](reference/platforms.md).

## Related

- [Choose a test layer](guides/choose-a-test-layer.md): which layer tests which behaviour
- [Examples](../examples/README.md): from the first unit test to a native session test
- [Packages](package-versions.md): every package, version and pin
- [API reference](https://tvongaza.github.io/ValheimTesting/reference/): every public type

