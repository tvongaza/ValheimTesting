# ValheimTesting

ValheimTesting gives you automated tests for Valheim mods built on BepInEx: unit tests that run against game doubles with no game, and native checks that drive a real dedicated server and client. It is for mod authors, and most of its tests run without Valheim, Unity, Steam or a dedicated server.

## Your first test

1. Install the .NET 10 SDK on the PC where you build your mod.
2. Copy [`MyMod.Tests/`](../examples/ModWithTests/MyMod.Tests/MyMod.Tests.csproj) from the [ModWithTests](../examples/ModWithTests/README.md) example beside your mod. Keep its `Valheim.Testing.Doubles` reference, and point `Compile Include` at one of your mod's source files:

   ```xml
   <Compile Include="../YourMod/YourRule.cs" Link="Mod/YourRule.cs" />
   ```

3. Run the tests from your mod checkout. A passing run exits 0:

   ```sh
   dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
   ```

[Get started](getting-started.md) walks through it with output and the GitHub Actions workflow.

## Find the guide for your task

| I want to | Read |
|---|---|
| I do not know which kind of test I need | [Choose a test layer](guides/choose-a-test-layer.md) |
| Test mod code that calls Valheim or Unity types | [Test logic that uses game types](guides/test-logic-that-uses-game-types.md) |
| Test heights, slopes, biomes or zone seams without the game | [Test terrain decisions](guides/test-terrain-decisions.md) |
| Run my tests on every push and pull request | [Run tests in CI](guides/run-tests-in-ci.md) |
| Pin a package version | [Packages](package-versions.md) |
| Check that a mod loads on a dedicated server | [Your first server load check](tutorials/first-server-load-check.md) |
| Find which mod breaks a load, alone or with others | [Check that a mod loads](guides/check-a-mod-loads.md) |
| Prepare the machine and mod for a run of the real game | [Before a native run](guides/before-a-native-run.md) |
| Find why a native run refused or failed, or clean up after it | [Debug a native run](guides/debug-a-native-run.md) |
| Look up a `valheim-test` command or option | [`valheim-test`](packages/Valheim.Testing.NativeSmoke.md) |
| Let my coding agent add and run the tests | [Give your coding agent the workflow](guides/give-your-agent-the-workflow.md) |

## Reference

- [Packages](package-versions.md): every package, version and pin.
- [API reference](reference/index.md): the public types, generated from source.
- [Package pages](packages/Valheim.Testing.md): each package has its own page. Start with `Valheim.Testing`.

Source, issues and contributions: [github.com/tvongaza/ValheimTesting](https://github.com/tvongaza/ValheimTesting).
