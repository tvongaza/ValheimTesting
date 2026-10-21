# Targeted native regression

Keep one gameplay assertion unchanged while comparing parent and candidate builds of your mod. One owned client hosts a disposable fixture world with the selected build.

Follow [Run an A/B regression](../../docs/guides/run-an-ab-regression.md) for the standalone setup, commands and captured parent failure/candidate pass. For one assertion first, use [Your first gameplay assertion](../../docs/tutorials/first-gameplay-assertion.md).

| File | Purpose | Adapt |
|---|---|---|
| [Program.cs](Program.cs) | Preflight, run and retirement | Copy unchanged |
| [Scenario.cs](Scenario.cs) | Actions and independent assertions | Your feature's checks |
| [MarkerOneShot.cs](MarkerOneShot.cs) | One-shot entry for the same scenario | Copy unchanged |
| [regression.sample.json](regression.sample.json) | Input shape | Your frozen inputs and mod builds |
| [Package consumers](consumer/README.md) | Complete standalone file maps | One source revision per map |

The marker scenario checks that the mod's Harmony patch is applied, requires zero markers before `examplemod_mark`, then exactly one pole in the host's saved objects. It uses ValheimCLI's Harmony and object observations; it needs no ExampleMod.TestAdapter. If your mod has several dependencies, run the [dependency resolver](../../docs/reference/game-run-inputs.md#resolve-mod-dependencies) before you fill in `regression.json`.

## Which files come from where

See the [input contract](../../docs/reference/sessions-targeted-regression.md#inputs-and-the-machine) for fixture, character, dependencies and client-environment selection. Keep paths, ports and names outside `Scenario.cs`.

## 1. Fill in regression.json

Follow [the guide's input step](../../docs/guides/run-an-ab-regression.md#2-fill-in-regressionjson). Start with the completed one-shot's recorded inputs and environment snapshot. Change only the mod arms.

### A world and a character for a basic smoke

See [fixture and character inputs](../../docs/reference/sessions-targeted-regression.md#fixture-and-character). The one-shot already records both sources.

### A custom character

Use the same [fixture and character contract](../../docs/reference/sessions-targeted-regression.md#fixture-and-character) with a registered disposable character.

### Optional fields

See the [optional input fields](../../docs/reference/sessions-targeted-regression.md#optional-input-fields) for configuration, patchers, process variables and expected log errors.

## 2. Preflight, without the game

Run [the guide's preflight command](../../docs/guides/run-an-ab-regression.md#3-preflight-without-the-game). The [stage checks](../../docs/reference/sessions-targeted-regression.md#stage-checks) describe each refusal; the [cleanup contract](../../docs/reference/sessions-targeted-regression.md#clean-up-after-a-run) describes retained profiles and errors.

## 3. Run each arm

Run [both guide commands](../../docs/guides/run-an-ab-regression.md#4-run-each-arm) into new private evidence directories.

## Review the result

Follow [Check the result](../../docs/guides/run-an-ab-regression.md#check-the-result): compare the named assertion, cleanup and every frozen non-mod input.

## Share the result

Use [Regression bundle](../../tools/regression-bundle/README.md) to validate and prepare a scrubbed public copy. Keep raw logs, plans and saves private.

## Limits

This example serves one client hosting a world. For dedicated persistence, use [FullLifecycle](../FullLifecycle/README.md); for a second client, follow [the multi-client guide](../../docs/guides/run-a-session-with-several-clients.md).

See [Platforms](../../docs/reference/platforms.md) for host requirements and the [regression contract](../../docs/reference/sessions-targeted-regression.md#limits) for lifecycle limits.
