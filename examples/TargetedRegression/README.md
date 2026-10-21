# Targeted native regression

For one assertion before an A/B comparison, use [Your first gameplay assertion](../../docs/tutorials/first-gameplay-assertion.md). It runs the same `Scenario.cs` through `MarkerOneShot.cs`.

Check one fix in the real game. You run the same scenario on a parent build and a candidate build of your mod, then compare the two results.

One owned client hosts one disposable fixture world with one mod under test. You fill in a short `regression.json` and run a preflight that never opens the game. Then you run each arm through the toolkit's strict-pinned hosted [`ClientRounds`](../../docs/reference/game-joins.md#hosted-listen-server-worlds) runner. For restarts, dedicated servers or several clients, use [FullLifecycle](../FullLifecycle/README.md).

| File | What it is | Change it? |
|---|---|---|
| [Program.cs](Program.cs) | The `preflight` and `run` commands, each with optional `--inventory` and `--client-env` | No; copy it as is |
| [Scenario.cs](Scenario.cs) | The rounds and assertions, the same on every machine | Yes: your mod's steps |
| [regression.sample.json](regression.sample.json) | The files to stage, the fixture and the character | Yes: copy it outside the repository |

The sample scenario tests FullLifecycle's ExampleMod through ValheimCLI's Harmony and saved-object observations; it needs no ExampleMod.TestAdapter. It requires zero markers before `examplemod_mark`, then exactly one pole where the host stands. Keep paths, ports and names out of `Scenario.cs`, so the source you ran is the source you can share.

**The machine is not in the file.** The client environment supplies three things: the game install (only read), the disposable profile folder and the client's ValheimCLI port. By default, that environment is this machine's detected Valheim. To pick another, add `--inventory environments.json --client-env NAME` after the command; see [this machine, with no file](../../docs/reference/game-inventory.md#this-machine-with-no-file). If you leave out `--inventory` and an `environments.json` sits beside `regression.json`, the tool uses that file. `valheim-test start` writes the same `regression.json`.

Use the [four-file package consumer](consumer/README.md#ab-regression-consumer) outside this checkout. [Run an A/B regression](../../docs/guides/run-an-ab-regression.md) starts from the one-shot's recorded inputs. This checkout's development project references GameSessions source.

If your mod has several dependencies, run the [dependency resolver](../../docs/reference/game-run-inputs.md#resolve-mod-dependencies) before you fill in `regression.json`.

## Which files come from where

| Source | Files | In `regression.json` |
|---|---|---|
| Valheim | The game install, run by Steam in your desktop session | Not in the file: the client environment's install |
| BepInEx | `BepInEx/core`, the Doorstop loader, `BepInEx/config/BepInEx.cfg` | Not in the file: the client environment's `loaderPackage` ([a reviewed package](../../docs/reference/game-run-inputs.md#pin-a-bepinex-loader)), else the pinned BepInExPack_Valheim on Windows and the install's own loader on macOS |
| ValheimCLI | One `valheimCLI.dll` and its [capability manifest](../../docs/reference/game-preflight.md#valheimcli-capability-manifest) | `cli.core`, `cli.manifest` |
| The mod under test | Parent and candidate builds, each with its source commit | `mod.arms` |
| The mod's dependencies | Every DLL it needs: hard `[BepInDependency]` plugins and referenced libraries | `plugins` |
| A test probe (optional) | A small plugin serving observations, such as ExampleMod.TestAdapter | `probe` |
| The fixture | A directory holding exactly one world, and that world's UID | `fixture` |
| Your character | A disposable **local** character, staged or in a disposable character store | `client.character`; optional `client.characterStore` |

Every DLL is pinned by SHA256. If you leave a `sha256` empty, the preflight prints that file's hash for you to review and paste. The tool never copies the prepared game's own plugins, scripts, patchers or configs.

## 1. Fill in regression.json

Copy `regression.sample.json` outside the repository and replace every `<...>`. Relative paths are relative to the file. You can give the arms any names; `parent` and `candidate` are the convention:

```json
"mod": {
  "installAs": "ExampleMod.dll",
  "arms": {
    "parent": { "file": "<parent build>/ExampleMod.dll", "sha256": "<sha256>", "commit": "<parent commit>" },
    "candidate": { "file": "<candidate build>/ExampleMod.dll", "sha256": "<sha256>", "commit": "<candidate commit>" }
  }
}
```

If two arms have the same SHA256, the tool refuses them. To run the same build twice on purpose, set `"repeatability": true`.

### A world and a character for a basic smoke

For a basic smoke, use the pinned world and character that Valheim 1.0.16 created:

```csharp
var world = DefaultSmokeWorld.Prepare(Path.Combine(runRoot, "world-source"));
var character = DefaultSmokeCharacter.Ensure(Path.Combine(runRoot, "character-source"));
// regression.json: fixture.root = runRoot/world-source; fixture.worldUid = world.UidText
// client.character = DefaultSmokeCharacter.Name; client.characterStore = character.Root
```

Use a new `world-source` for each preparation. `Ensure` repairs a character only inside its own marked store. The packaged character serves one client at a time, because every copy shares its player ID.

For a mod-specific world, supply your own fixture. It holds exactly one world, as the game saved it:

```text
<fixture root>/
  <WorldName>/
    _main.<n>.fwl2
    _main.<n>.db2
    ...
```

Read the UID from the world itself, never from its name: `WorldIdentity.Read(root).UidText`. You can also let the preflight report it.

### A custom character

1. Register a **game-created** local save once in a `DisposableCharacterStore` ([API reference](https://tvongaza.github.io/ValheimTesting/reference/)).
2. Set `client.character` to its registered name.
3. Set `client.characterStore` to the store's full path.

`run` refuses a name that already exists in local or cloud saves. It copies the character in before launch. After the client stops, it removes the character's file and backups, even on failure.

### Optional fields

| Field | Does |
|---|---|
| `configs` | BepInEx config files by name; without `valheimCLI.valheimCLI.cfg`, one gets the environment's port |
| `patchers` | Files staged into `BepInEx/patchers` |
| `optionalReferences` | Assembly names a staged DLL uses only when present |
| `gamePins` | Pins the prepared game's build and BepInEx core |
| `client.startSeconds`, `client.joinSeconds` | Bound the client's launch to its menu (default 300, 10 to 1800) and each hosted start (default 180, 10 to 900) |
| `client.environment` | Non-secret variables for the owned game process only |
| `logScan` | Known BepInEx error headers to expect, each with a written reason |

Set `client.environment` like this:

```json
"environment": { "EXAMPLEMOD_DEFER_BAKE": "1" }
```

The preflight rejects loader overrides, invalid names and multiline values in `client.environment`.

Set `logScan` like this:

```json
"logScan": { "unknown-error": { "expected": ["[Error  :   BepInEx] Unable to start Unity log writer"], "reason": "Known log-writer limitation on this test client" } }
```

New or different errors still fail the run; see the [log scan](../../docs/reference/game-log-scan.md).

## 2. Preflight, without the game

```sh
dotnet run --project examples/TargetedRegression -c Release -- preflight /absolute/path/to/regression.json
```

The preflight prints each arm's commit, artifact name and hashes. It also prints the allowlist, with each plugin's MD5 and GUID. Review them before the run. It ends with:

```text
PREFLIGHT PASSED; nothing was launched. Review the arms' commits and hashes above before the run.
```

For each arm, the preflight stages the owned profile and checks:

- The fixture root holds one world with the file's UID. If the layout is wrong, it shows the expected tree and the tree it found.
- Every file matches its pinned SHA256.
- The staged `BepInEx` folders hold only the default ValheimCLI plugin and what `regression.json` names.
- Each plugin's declared metadata meets its dependencies, process and references. File names play no part.
- The character is present or registered, and the [owned-run preflight](../../docs/reference/game-preflight.md) passes.
- `valheimCLI.dll` matches `cli.manifest` and provides every built-in command the run uses. Commands owned by the probe or the mod are checked live, before the first round.

For the full contract, see [Stage checks](../../docs/reference/sessions-targeted-regression.md#stage-checks).

The tool stages the profile at `vt-prep-regression-<name>-<run-id>/runtime` in the environment's runtime folder. On Windows, the profile holds hard links to the game's files and its own loader. On macOS, it holds the loader and selected files and launches the source game.

| Exit code | Meaning |
|---|---|
| `0` | Passed |
| `1` | `run` only: a report failed; inspect setup, scenario and cleanup |
| `2` | Usage error |
| `3` | Refusal or unknown outcome; I/O or cleanup can fail after launch |

## 3. Run each arm

```sh
dotnet run --project examples/TargetedRegression -c Release -- run /absolute/path/to/regression.json parent /absolute/path/to/new-evidence-parent
dotnet run --project examples/TargetedRegression -c Release -- run /absolute/path/to/regression.json candidate /absolute/path/to/new-evidence-candidate
```

Each command prints `PASS` or `FAIL` and fills its evidence directory.

`run` stages the arm again and repeats every check. It also refuses any file that appeared in the staged folders since staging. Then it hands the plan to the hosted `ClientRounds` runner. The runner places the fixture, launches the client, checks capabilities live, hosts the world with the character protected, and runs `Scenario.Measure`.

Strict pins check every command: each plugin's GUID and MD5, and the world's UID. Anything unlisted is refused. In every outcome, the client stops and the world moves into the evidence.

| Evidence | Holds |
|---|---|
| `result.json` | The outcome; provenance with the arm, mod build and `disposableStageSeconds` |
| `junit.xml` | The JUnit report |
| `run-manifest.json` | Every arm's commit and hash, the allowlist, install pins, world and capabilities; no machine path |
| `client-commands.jsonl` | The command trace |
| `host-world/` | The hosted world after the run |
| `<runId>/` | The run's own folder: the world as it left the client's worlds (`host-world/`, where `env recover` also moves a killed run's world) and its staging record |

The directory also holds the scenario's own JSON files. Each arm gets a fresh profile, and the example retires it before exiting. If a run is interrupted, it leaves a journal for `valheim-test env recover`.

## Review the result

Compare the arms by their `result.json`, not by exit code alone. The parent should fail at the step the change fixes, and the candidate should pass every step. The selected arm, source commit and mod hashes change. Every other manifest field must match after normalizing `arm`, `modCommit`, `modSha256` and the selected mod's `sha256`/`md5` in `allowlist`. Logs, saves and command traces can hold account identifiers and machine paths, so keep them private.

## Share the result

To make a public copy, run [tools/regression-bundle](../../tools/regression-bundle/README.md) from a checkout of this repository. It checks each arm's `result.json` against its `junit.xml`, command trace and `run-manifest.json`. It then writes:

- `Scenario.cs` and `Program.cs`, exactly as they ran
- a project pinned to the toolkit
- a `regression.json` template with placeholders
- an A/B table

The tool refuses machine paths, private names and unrelated plugin names. It publishes nothing; review the directory before you share it.

## Limits

- The example covers one owned client hosting one world. For dedicated servers and server restarts, use FullLifecycle. For a second client, see the [native acceptance suite](../../tests/Valheim.Testing.NativeAcceptance/README.md).
- Linux hosts run the dedicated server only, with no game client.
- On macOS, the profile does not copy the game.
- On Windows, the preflight checks hard-link support. A default runtime follows the install's volume. An explicit client-environment `runtime` must be on that NTFS volume. A library on exFAT, FAT32 or a network share must move to NTFS.
- If you don't use a registered character, stage your disposable local character yourself.
- The tool does not reserve a machine or publish anything.
- The metadata checks read what BepInEx reads. The live run is the only check for a dependency a mod finds by reflection, or a config value it needs.
