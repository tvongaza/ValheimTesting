# First test of your mod's code

This miniature mod and its xUnit project test the mod's real source file with the game doubles from `Valheim.Testing.Doubles`. You need the .NET 10 SDK and NuGet access. You do not need Valheim, Steam or a server.

## Files

| File | What it holds | What you replace in your mod |
|---|---|---|
| [MyMod/DrySiteRule.cs](MyMod/DrySiteRule.cs) | The mod's rule: generator ground at least 1.5 m above water. | Your existing implementation. |
| [MyMod.Tests/MyMod.Tests.csproj](MyMod.Tests/MyMod.Tests.csproj) | Links the production source with `Compile Include`. | Link your source. Reference the testing package only here. |
| [MyMod.Tests/DrySiteTests.cs](MyMod.Tests/DrySiteTests.cs) | `ValheimWorldScope` installs synthetic terrain, then restores the previous world. | Your inputs and independent expectations. |
| [.github/workflows/tests.yml](.github/workflows/tests.yml) | Runs the tests on pushes to `main`, on pull requests and by hand. | Laid out for your mod's root. It does not run here. |

## Run it

From this directory:

```sh
dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
```

Success is **5 passed, 0 failed**, exit 0. A failed assertion returns nonzero.

The project pins `Valheim.Testing.Doubles` `0.1.0-preview.15` from NuGet.org. Inside a ValheimTesting checkout, `NuGet.Config` also offers the local `.packages` feed. To be sure you test the published package, run from a copy outside the checkout.

The five tests cover submerged ground, the exact allowed boundary, dry ground, sampling at x/z instead of the object's y, and the previous world returning after a throw.

To see a test fail, change `>=` to `>` in `MyMod/DrySiteRule.cs`. The boundary case fails. Restore the rule afterwards.

## Next

- To adapt this to your mod, see [Test logic that uses game types](../../docs/guides/test-logic-that-uses-game-types.md).
- To run the tests in CI and add the `net48` leg, see [Run tests in CI](../../docs/guides/run-tests-in-ci.md).
- If you are new to the toolkit, start at [Get started](../../docs/getting-started.md).
- These tests check the rule on known generator inputs. They do not prove that the real world is dry, that a collider exists, or that a client receives terrain. For those checks, [choose a test layer](../../docs/guides/choose-a-test-layer.md).
