# ValheimTesting

Test your mod's actual effect in Valheim, test its decisions without the game, or keep a fixed bug from returning.

The marker example places one pole and checks the saved objects independently. Selected action and assertion lines from [Scenario.cs](../examples/TargetedRegression/Scenario.cs):

```csharp
var reply = host.Execute("examplemod_mark " + At());
round.Step("exactly one marker stands there in the host's saved objects", () => RequireMarkers(host, x, z, ExpectedMarkers));
```

`ExpectedMarkers` is `1`. The captured result records **"first: exactly one marker stands there in the host's saved objects"** with **`Passed: true`**. The unchanged assertion catches a success reply that creates no pole.

[Your first gameplay assertion](tutorials/first-gameplay-assertion.md) shows the complete source, files to edit, command and failure evidence.

## Your first test

| You want to | Follow |
|---|---|
| Test your mod's gameplay in Valheim | [Your first gameplay assertion](tutorials/first-gameplay-assertion.md) |
| Test a decision quickly, without the game | [Get started](getting-started.md#steps) |
| Keep a fixed bug from returning | [Run an A/B regression](guides/run-an-ab-regression.md) |

A [load check](tutorials/first-server-load-check.md) proves startup and world readiness. It does not assert the mod's gameplay.

## Find the guide for your task

| You want to | Read |
|---|---|
| Choose the cheapest useful test | [Choose a test layer](guides/choose-a-test-layer.md) |
| Test code that calls game types | [Test logic that uses game types](guides/test-logic-that-uses-game-types.md) |
| Test terrain decisions without the game | [Test terrain decisions](guides/test-terrain-decisions.md) |
| Exercise lost replies and failed saves without the game | [MarkerScenarioTests](../examples/FullLifecycle/ExampleMod.Tests/MarkerScenarioTests.cs) |
| Run tests on every push | [Run tests in CI](guides/run-tests-in-ci.md) |
| Diagnose plugins that fail to load | [Check that a mod loads](guides/check-a-mod-loads.md) |
| Assert saved state after restart and rejoin | [Test a save, restart and rejoin](guides/test-save-restart-rejoin.md) |
| Compare a parent and candidate build | [Run an A/B regression](guides/run-an-ab-regression.md) |
| Run the server on another machine | [Run on another machine](guides/run-on-another-machine.md) |
| Test visibility for several players | [Run a session with several clients](guides/run-a-session-with-several-clients.md) |
| Prepare a native run | [Before a native run](guides/before-a-native-run.md) |
| Diagnose a refusal, failure or leftover | [Debug a native run](guides/debug-a-native-run.md) |
| Look up a command or option | [`valheim-test`](packages/Valheim.Testing.NativeSmoke.md) |
| Give your coding agent the workflow | [Agent setup](guides/give-your-agent-the-workflow.md) |

## Reference

- [Package versions](package-versions.md): every package, exact pin and reference link.
- [API reference](reference/index.md): the public types, generated from source.
- [Examples](../examples/README.md): unit, scripted integration and native scenarios.

Source, issues and contributions: [ValheimTesting on GitHub](https://github.com/tvongaza/ValheimTesting).

Native checks use [ValheimCLI](https://github.com/jneb802/valheimCLI), created by Warpalicious; see [credits](https://github.com/tvongaza/ValheimTesting#credits).
