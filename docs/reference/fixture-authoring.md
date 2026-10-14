# Bake a fixture

Bake a saved world once with `valheim-test`, then load it in later runs.

## Purpose

A bake runs your mod once on an owned dedicated server and saves the world. It then publishes the saved world as an immutable fixture, and later runs load that fixture with `--world-fixture`. The bake never edits the source fixture or the game install. For the rest of `server-load`, see [Valheim.Testing.NativeSmoke](../packages/Valheim.Testing.NativeSmoke.md).

## One-phase bake

Use a one-phase bake when your mod can prove its generated state is ready during one server boot.

```sh
valheim-test server-load --server-only \
  --mod ./bin/Release/net48/MyMod.dll \
  --world-fixture ./fixtures/poi-base \
  --bake-fixture ./fixtures/poi-baked \
  --before-save-command 'mymod_generate_sites' \
  --before-save-line 'OK: generated sites=' \
  --assert-command 'cli_extension mymod.testing/bake-ready' \
  --assert-line 'EXTENSION_RESULT mymod.testing/bake-ready ready=true'
```

If the bake succeeds, the command prints `Baked fixture: ` and the destination, then a `SERVER_LOAD_PASS` line.

<!-- TODO(docs): capture output -->

| Option | Meaning | Required |
|---|---|---|
| `--server-only` | Runs the owned server with no client. | Yes |
| `--world-fixture DIR` | Source: exactly one named world, directly or under `worlds_local/`. Without it, the packaged smoke world. | No |
| `--bake-fixture NEW_DIR` | Destination of the baked fixture. It must not exist. | Yes |
| `--before-save-command TEXT` with `--before-save-line PREFIX` | One-time change. Runs once, before the assertion. | No. Give both. |
| `--assert-command TEXT` with `--assert-line PREFIX` | One strict pinned command after the world accepts connections. | Yes. Give both. |

The tool hashes the source and verifies its disposable copy. It also checks the world's UID against the world's own metadata. Choose an assertion whose accepted reply means generation is complete. Make `--assert-line` specific enough to exclude a partial or refused result.

### Option rules

The parser refuses these combinations:

- `--bake-fixture` without `--server-only`. A joined-client bake has not been verified.
- `--bake-fixture` with `--hold` or `--preflight-only`. A bake needs a completed run and a clean stop.
- `--bake-fixture` without `--assert-command` and `--assert-line`.
- `--before-save-command` or `--before-save-line` alone, or without `--bake-fixture`.
- `--assert-command` or `--assert-line` alone, or `--assert-command` without `--server-only`.
- A `--before-save-command` or `--assert-command` that holds control characters. Each must be one game command.
- `--scenario` or `--scenario-project` with `--bake-fixture` or `--assert-command`. The scenario owns its assertions.

`start` and `server-load-ab` accept neither `--world-fixture` nor `--bake-fixture`.

### What a bake does

1. Checks that the server loaded the fixture's world UID.
2. Waits until the server accepts a game connection.
3. Runs `--before-save-command`, if you gave it, and requires `--before-save-line`.
4. Runs `--assert-command` and requires `--assert-line`.
5. Asks ValheimCLI for a save and requires the save number to advance.
6. Lets the server save again at clean quit, with up to 300 seconds to quit.
7. Fetches the saved world and checks its name and UID.
8. Publishes `worlds_local/<name>/` with `fixture-manifest.json` in the `--bake-fixture` directory. It publishes only when the whole run and cleanup passed.

The manifest records:

- every output hash
- the source hashes and UID
- the selected mod hashes and the dependency-lock hash
- the run ID
- the confirmed save number and the final exported save number

### Fails when

| Case | Result |
|---|---|
| An option rule is broken | Exit code 2. The error and usage are printed. |
| Preflight refuses an input | Exit code 3. `REFUSED` lines name the problem. |
| Assertion fails, save is unconfirmed or stop is unclean | No fixture. Private evidence remains for diagnosis. |
| Destination already exists | No fixture. |
| Export fails after the game run | Exit code 1. The error is in `evidence/fixture-export-failed.txt`. |

## Load a bake

Load the baked fixture again to test persistence without the one-time action:

```sh
valheim-test server-load --server-only \
  --mod ./bin/Release/net48/MyMod.dll \
  --world-fixture ./fixtures/poi-baked \
  --assert-command 'cli_extension mymod.testing/generated-state' \
  --assert-line 'EXTENSION_RESULT mymod.testing/generated-state sites=12'
```

The run verifies `fixture-manifest.json` before it loads the world.

To check for an object such as the [FullLifecycle marker](../../examples/FullLifecycle/README.md), assert with `cli_zdos_at X Z 8`. Match its specific `ZDO <prefab>` line.

## Ordered phases

`server-load-phases` runs two to ten strict `server-load --server-only` bakes in order. Each phase gets a new owned server copy. Its confirmed fixture becomes the next phase's input.

Write a plan:

```json
{
  "schema": 1,
  "worldFixture": "/absolute/path/to/fixtures/base",
  "keepFinal": true,
  "commonArgs": ["--server", "/absolute/path/to/ValheimDedicatedServer"],
  "phases": [
    {
      "name": "generate",
      "args": ["--mod", "/absolute/path/to/Generator.dll", "--assert-command", "cli_extension generator.testing/ready", "--assert-line", "EXTENSION_RESULT generator.testing/ready ready=true"]
    },
    {
      "name": "decorate",
      "args": ["--mod", "/absolute/path/to/Decorator.dll", "--before-save-command", "cli_extension decorator.testing/place", "--before-save-line", "EXTENSION_RESULT decorator.testing/place placed=true", "--assert-command", "cli_extension decorator.testing/ready", "--assert-line", "EXTENSION_RESULT decorator.testing/ready ready=true"]
    }
  ]
}
```

Run it with two absolute paths. The output directory must be new:

```sh
valheim-test server-load-phases --plan /absolute/path/to/phases.json --output /absolute/path/to/new-private-run
```

On success, the command prints a `PHASES PASS:` line that names the final fixture and the evidence directory.

### Plan fields

| Field | Meaning |
|---|---|
| `schema` | Must be `1`. |
| `worldFixture` | Absolute path to the source fixture, or `"packaged"` for the toolkit's pinned small world. |
| `keepFinal` | With `true`, the final checkpoint stays. |
| `commonArgs` | Options every phase shares, as option/value pairs. |
| `phases` | Two to ten phases, run in order. |
| `name` | Distinct per phase. Lowercase letters, digits or hyphens, 1-40 characters. |
| `args` | This phase's options, as option/value pairs. |

`commonArgs` selects the server environment, loader, adapter and ValheimCLI bundle with `--inventory`, `--server`, `--server-env`, `--loader-package`, `--adapter`, `--cli-manifest` and `--cli-files`.

A phase's `args` can use `--mod`, `--config`, `--plugin-file`, `--plugin-dir`, `--search-root`, `--optional-reference`, `--assert-command`, `--assert-line`, `--before-save-command`, `--before-save-line`, `--expected-log-error` and `--expected-log-reason`. Each phase needs at least one `--mod` and its own `--assert-command` and `--assert-line`. The runner adds `--server-only`, `--world-fixture`, `--bake-fixture` and `--output` itself.

### Output

| Path | Holds |
|---|---|
| `phase-plan.json` | A copy of the plan. |
| `phase-state.json` | The active phase, its state, input and checkpoint. |
| `phases/` | Each phase's private run evidence, for example `01-generate`. |
| `checkpoints/` | Each phase's confirmed baked fixture. |

### Behaviour

- Before phase one, the runner validates every phase's command. It also hashes the plan and every named input file and directory.
- Before each phase, it refuses to continue if an input changed.
- After a phase succeeds, the runner removes the previous phase's checkpoint, never the source.
- With `keepFinal: true`, the final checkpoint stays. Otherwise the runner removes it.
- With `"packaged"`, the runner retires the generated source after a successful build.
- A failed phase stops the sequence and leaves the last confirmed checkpoint for inspection. The runner exits with code 3 if the phase was refused, and 1 otherwise.
- If the runner is interrupted, run `valheim-test env status` to find the active phase's run ID. Then run `valheim-test env recover --run ID` before you inspect or clean its checkpoint.

## Limits

- A native macOS dedicated server places the world briefly in the signed-in user's default `worlds_local`. If a world with the same name exists there, preflight refuses. Use another host or a separately named fixture.
- Keep plans and evidence private. Paths, logs and game data can identify your machine or world.
- One assertion runs per bake. For a longer event wait, use a `PinnedServerRun` scenario: see [Valheim.Testing.GameSessions](../packages/Valheim.Testing.GameSessions.md).
- This page does not cover authoring terrain or biomes with Expand World.
