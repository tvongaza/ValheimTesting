# Get started

Choose what you want to prove. This page walks through the fast logic test: the marker mod's real placement decision on supplied terrain, without Valheim.

| You want to | Follow |
|---|---|
| Test your mod's gameplay in Valheim | [Your first gameplay assertion](tutorials/first-gameplay-assertion.md) |
| Test a decision quickly, without the game | [The steps below](#steps) |
| Keep a fixed bug from returning | [Run an A/B regression](guides/run-an-ab-regression.md) |

## Steps

### 1. Run the example

You need the .NET 10 SDK and NuGet.org access. Create a directory outside any ValheimTesting checkout. Download these files from one repository revision and save them at the listed destinations:

| Source | Destination | Purpose |
|---|---|---|
| [DrySiteRule.cs](../examples/ModWithTests/MyMod/DrySiteRule.cs) | `MyMod/DrySiteRule.cs` | The production placement decision |
| [MyMod.Tests.csproj](../examples/ModWithTests/MyMod.Tests/MyMod.Tests.csproj) | `MyMod.Tests/MyMod.Tests.csproj` | Links the source and pins the doubles package |
| [DrySiteTests.cs](../examples/ModWithTests/MyMod.Tests/DrySiteTests.cs) | `MyMod.Tests/DrySiteTests.cs` | Inputs, assertions and static-state restoration |
| [NuGet.Config](../examples/TargetedRegression/consumer/NuGet.Config) | `NuGet.Config` | NuGet.org as the only package source |

From that new directory, run:

```sh
dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
```

```text
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 21 ms - MyMod.Tests.dll (net10.0)
```

The project restores `Valheim.Testing.Doubles` from NuGet.org. It needs no toolkit checkout. The five tests cover wet ground, the allowed boundary, dry ground, horizontal coordinates and restoring the previous world after a failure.

Now prove the tests can fail: change `>=` to `>` in `MyMod/DrySiteRule.cs` and run the command again. The exact-boundary case fails. Restore the rule.

### 2. Add the test project to your mod

Copy `MyMod.Tests/` next to your existing mod project. Change its `Compile Include` to one source file of your mod and retain the pinned test package:

```xml
<Compile Include="../YourMod/YourRule.cs" Link="Mod/YourRule.cs" />
<PackageReference Include="Valheim.Testing.Doubles" Version="[0.3.0]" PrivateAssets="all" />
```

The test project compiles your production file against the doubles. Reference no game assembly or plugin project: those types would conflict. Keep Harmony patches and `Awake` outside the linked decision file.

### 3. Write the test

Edit `DrySiteTests.cs` to supply inputs and independent expectations for your decision. These lines from [the first test](../examples/ModWithTests/MyMod.Tests/DrySiteTests.cs) install synthetic terrain and call the real rule:

```csharp
using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(ground));
Assert.Equal(expected, DrySiteRule.CanPlace(new Vector3(0, 100, 0), 30f, 1.5f));
```

Keep `[assembly: CollectionBehavior(DisableTestParallelization = true)]`: the doubles use process-wide singletons.

From your mod checkout, run:

```sh
dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
```

A passing run exits 0. A failed assertion returns nonzero. Your IDE discovers the same test project.

### 4. Run it in GitHub Actions

Copy the example's [workflow](../examples/ModWithTests/.github/workflows/tests.yml) into your mod's `.github/workflows/tests.yml` and change its project path. It runs the same command with no game or secrets. Follow [Run tests in CI](guides/run-tests-in-ci.md) for the complete setup.

## Check the result

Break your own rule on purpose, for example by flipping a comparison, and run the tests again. At least one must fail; if none does, the tests do not cover that decision. Restore the rule.

This checks the production decision on declared inputs. [Choose a test layer](guides/choose-a-test-layer.md) explains what needs native gameplay, persistence or client observations.

## Code without game types

If your code uses no Unity or Valheim type, reference `Valheim.Testing` from your existing test project and pass an `ITerrain` into production code:

```xml
<PackageReference Include="Valheim.Testing" Version="[0.3.0]" />
```

The [package table](package-versions.md) lists every other package and exact version.

## Problems

| Problem | Cause | Fix |
|---|---|---|
| Package restore fails | The pin names an unpublished version | Use an exact [published pin](package-versions.md) |
| A game member is missing | The doubles do not model it | Extend the partial type locally; read the [contract](packages/Valheim.Testing.Doubles.md) |
| Game types conflict | The project references game assemblies or the plugin | Link production source only |
| Tests interfere | The doubles share process-wide state | Keep the assembly's `DisableTestParallelization` setting |

More in [Test logic that uses game types](guides/test-logic-that-uses-game-types.md).

## What a native check needs

Follow [Your first gameplay assertion](tutorials/first-gameplay-assertion.md) for the native setup and tested command. For startup alone, use [Your first server load check](tutorials/first-server-load-check.md). See [Platforms](reference/platforms.md) for host requirements; read the [native-run checklist](guides/before-a-native-run.md) before launching.

## Related

- [Examples](../examples/README.md): unit, scripted integration and native checks.
- [Package versions](package-versions.md): every package, exact pin and reference link.
- [API reference](reference/index.md): the public types.
