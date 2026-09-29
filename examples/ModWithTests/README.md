# First test of your mod's code

This is a miniature mod source tree plus a complete xUnit consumer project. It tests the **same source file the mod would ship**, using game doubles supplied by a NuGet source package. It needs the .NET 10 SDK and package restore access, but no Valheim, Steam, Unity, ValheimCLI or server. No plugin is built or installed.

## Run this example

The example pins the published `Valheim.Testing.Doubles` `0.1.0-preview.4`, which restores from NuGet.org together with its `Valheim.Testing` dependency. From this directory, or from a copy of it anywhere:

```sh
dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
```

Success is **5 passed, 0 failed**, exit 0. A failed assertion returns nonzero. The framework validation also runs a copy pinned to the current source's Doubles version, restored from freshly packed packages rather than the doubles source directory.

## Understand the three files

| File | What you replace in your mod |
|---|---|
| [MyMod/DrySiteRule.cs](MyMod/DrySiteRule.cs) | Your existing implementation; this example accepts generator ground at least 1.5 m above a supplied water level. |
| [MyMod.Tests/MyMod.Tests.csproj](MyMod.Tests/MyMod.Tests.csproj) | Link your production source with `Compile Include`. Reference the testing package **only here**. |
| [MyMod.Tests/DrySiteTests.cs](MyMod.Tests/DrySiteTests.cs) | Your declared inputs and independent expectations. `ValheimWorldScope` installs synthetic terrain and restores the previous world on disposal. |

The tests cover submerged ground, the exact allowed boundary, dry ground, horizontal x/z sampling instead of the object's y, and isolation after an exception. For a quick control, change `>=` to `>` in the mod source: the boundary case must fail. Restore the original rule afterwards. Do not copy the rule into the tests; that would test a substitute implementation.

## Apply it to your checkout

1. Create `MyMod.Tests/` alongside your mod project and copy the test project structure. Keep the production plugin's framework, references and output unchanged.
2. Replace the linked example source with a small decision from your own mod. If it pulls in the whole plugin lifecycle, extract that decision into a production helper first; do not rewrite it inside the test project.
3. Reference `Valheim.Testing.Doubles` with `PrivateAssets="all"`. Do **not** reference Unity, Valheim or the built plugin DLL in the same project: the package supplies types with those names. For already engine-free code, use a normal project reference and `Valheim.Testing` instead.
4. Restore and run it from your mod checkout with `dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release`. Released versions need no feed setup; to try an unpublished toolkit build, add a local feed as described in [package versions and feeds](../../docs/getting-started.md#package-versions-and-feeds).
5. Add the project to your own solution or open it directly in your IDE. There is no requirement to have a Roads or MWL testing solution.
6. Keep singleton-using tests nonparallel. Use fresh scope builders, and reset your mod's own static state separately. Scope disposal restores references, not deep copies of pre-existing objects.

If compilation reports a missing game member, check the [documented doubles contract](../../docs/testing-toolkit.md#game-doubles). Extend a partial type locally for a mod-specific seam, or contribute reusable behavior with a test. A no-op stub is not evidence that the game's behavior works.

## What this proves, and the next layer

This tests the mod's decision on known generator inputs. It does **not** prove the real world is dry, that a collider exists, or that a client receives terrain. It is deliberately not a BepInEx plugin or a simulation of the native game.

For a first native check, follow [Adopting the framework: the next layer](../../docs/adopting.md#add-one-native-check-only-when-needed). Start with an existing observer on a prepared fixture; introduce a mod adapter only when the observation or action your scenario needs is missing. The mod owns that scenario and its expectations.
