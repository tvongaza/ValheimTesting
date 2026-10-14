---
name: valheim-mod-testing
description: Add or run ValheimTesting tests in a Valheim mod repository, from source-linked unit tests to disposable native game checks. Use for mod adoption, regression scenarios, and test evidence; not for developing ValheimTesting itself.
---

# Test a Valheim mod with ValheimTesting

Read the mod repository's own instructions first. The mod's tests, adapters and fixtures live in the mod repository; no ValheimTesting package goes into the production plugin. Use the published packages and the `valheim-test` tool: never clone, bootstrap or build ValheimTesting. The full workflow is the [agent workflow](https://github.com/tvongaza/ValheimTesting/blob/main/docs/agent-guide.md).

1. Choose the cheapest layer that can observe the behaviour ([Choose a test layer](https://github.com/tvongaza/ValheimTesting/blob/main/docs/guides/choose-a-test-layer.md)):
   - Logic that uses game types: `Valheim.Testing.Doubles`, source-linked ([guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/guides/test-logic-that-uses-game-types.md)).
   - Terrain decisions: `Valheim.Testing` ([guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/guides/test-terrain-decisions.md)).
   - Retry, refusal or cleanup in a scenario driver: `Valheim.Testing.Game.Fakes`.
   - Whether the mod loads: `valheim-test server-load` or `valheim-test start` ([guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/guides/check-a-mod-loads.md)).
   - Save, restart and rejoin: `PinnedServerRun` with `ClientRounds` ([FullLifecycle](https://github.com/tvongaza/ValheimTesting/blob/main/examples/FullLifecycle/README.md)).
   - One mod before and after a change: `TargetedRegression` ([example](https://github.com/tvongaza/ValheimTesting/blob/main/examples/TargetedRegression/README.md)).
2. Pin exact published versions from the [package table](https://github.com/tvongaza/ValheimTesting/blob/main/docs/package-versions.md).
3. Run the mod's test project with `dotnet test`. Stop if that answers the question.
4. A native check starts the game on the user's machine: ask first. Read the [native-run checklist](https://github.com/tvongaza/ValheimTesting/blob/main/docs/guides/before-a-native-run.md), run `valheim-test env preflight`, then one guide's command. Read `result.json` and the log scan.
5. Afterwards, `valheim-test env status` must list nothing your run left. Recover an interrupted run with `valheim-test env recover --run ID`; if recovery keeps something for inspection (exit 3), remove it with `valheim-test env teardown --run ID`.
6. Report what you tested, the build and package versions, the checks run with pass or fail, the checks not run, the evidence paths, the teardown status and the next step ([report shape](https://github.com/tvongaza/ValheimTesting/blob/main/docs/agent-guide.md#completion-and-handoff)).

A connected ValheimCLI, a synthetic pass or an incomplete observation is not evidence that the mod works in the game.

Every `valheim-test` command and option: [the tool's page](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.NativeSmoke.md). A toolkit bug goes to [ValheimTesting issues](https://github.com/tvongaza/ValheimTesting/issues); a ValheimCLI problem goes there too, with the `valheimcli` label ([what a report carries](https://github.com/tvongaza/ValheimTesting/blob/main/docs/agent-guide.md#report-a-problem)).
