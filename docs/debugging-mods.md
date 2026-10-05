# Debug a mod load or mod conflict

Use the smallest run that can answer the question. A load smoke checks setup and entry into a disposable world; it does not assert that a mod's gameplay feature works. For that, add a focused scenario in the mod's own tests after the load is reproducible. The commands below use the [`valheim-test` tool](../src/Valheim.Testing.NativeSmoke/README.md).

## Prepare once

- Use a compatible game or dedicated-server install, not a live save. The runner copies that install and uses its packaged game-created world and character. An unmodded install can receive an explicit [reviewed BepInEx/Doorstop package](bepinex-loader-package.md) in its disposable copy: `--loader-package` for the server and, with `--client`, `--client-loader-package` for a different client package. Give each run a new, private output directory.
- Build the mod DLLs against the matching game. The native-smoke tool builds its [test-only server adapter](../src/Valheim.Testing.NativeSmoke/SessionAdapter/README.md) against the selected game and ValheimCLI core. valheim-test stages the ValheimCLI bundle pinned with it; to use another build, name it with `--cli-manifest` and `--cli-files` or `VALHEIMCLI_BUNDLE` (a ValheimCLI already in the game install is not picked up on its own). Do not put test-library packages in `BepInEx/plugins`.
- Keep the initial check cheap. If expensive world generation is irrelevant to loading, supply a known test config with `--config`. Use a small fixture plugin when checking the runner itself; run the real mod when investigating a real failure.
- Run one native campaign at a time per game install, Steam account and fixture character. The output may contain a server password, account identifiers and machine paths. Keep it private.

Install the published preview tool with `dotnet tool install --global Valheim.Testing.NativeSmoke --prerelease`. The commands below work from any directory; no ValheimTesting checkout is needed. Replace the example paths with prepared installs and built artifacts. `--output` **must name a directory that does not exist yet**. To use a ValheimCLI build other than the pinned one, add `--cli-manifest FILE --cli-files DIR` from one build. `server-load` takes its server and client from this machine's Steam installs unless `--server`/`--client` name prepared ones; it prints what it chose.

The loader-package options are part of this checkout's candidate tool until its next release. To try them before publication, bootstrap this checkout and run `dotnet run --project src/Valheim.Testing.NativeSmoke -- server-load ...` in place of `valheim-test server-load ...`; the [tool's README](../src/Valheim.Testing.NativeSmoke/README.md#disposable-native-mod-load-smoke) shows the complete option set. Check the installed tool's help after release rather than assuming an older preview supports these options.

## First, prove each server mod loads alone

```sh
valheim-test server-load \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll \
  --server-only \
  --output /private/runs/first-alone
```

Repeat with `SecondMod.dll` and a new output directory. `SERVER_LOAD_PASS` establishes that the selected server plugins loaded, the packaged world identity matched and the server accepted a game connection. It **does not** establish that a client joined. To test a claim of vanilla-compatible server-only loading, leave out `--server-only`: by default one clean client joins (this machine's Valheim, or `--client DIR`):

```sh
valheim-test server-load \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll \
  --output /private/runs/first-with-clean-client
```

`SERVER_JOIN_PASS` adds evidence that a client with ValheimCLI but without the selected server mods entered the world. The runner pins the selected mods absent on that client. Use this path only when their absence is the compatibility claim; a mod required on both peers needs a different scenario.

For a **client-side** pair, use `start` and repeat `--mod` (`--game` defaults to this machine's Valheim):

```sh
valheim-test start \
  --game /path/to/prepared/Valheim \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --output /private/runs/client-pair
```

## Then test the pair

Select both server mods in one run. Supply their required external assets with `--plugin-file` or `--plugin-dir`, and explicit dependency search locations with `--search-root`; repeat each option as needed. The runner resolves the **combined** dependency set before it launches either process.

```sh
valheim-test server-load \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --output /private/runs/pair
```

If each mod loads alone but the pair does not, compare the two `dependencies.lock.json` files with the pair's lock, then inspect the pair's `evidence/result.json` and retained BepInEx/Unity logs. This narrows the problem to a combined setup or runtime interaction; it does not by itself identify which mod's code is at fault. Record the exact plugin files, configs, assets, game build and ValheimCLI build before changing anything.

For a controlled **remove-one-mod** check, including a full set that fails during the game run, use `server-load-ab`:

```sh
valheim-test server-load-ab \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --remove-mod /path/to/SecondMod.dll \
  --output /private/runs/remove-second
```

The command resolves both sets first and compares their inputs, then runs the complete set in `before/` and the set without `SecondMod.dll` in `after/`. Shared files, configs, assets, CLI and runtime stay pinned; dependencies used only by the removed mod may leave with it. A **native failure** in `before/` still runs `after/` so you can inspect both results. An input/setup refusal stops the comparison: first correct the missing or ambiguous dependency, pin or fixture, then rerun. A load difference narrows the interaction; it does not by itself show which mod's code is responsible.

## Read the result before changing the mod

| Observation | Next check |
|---|---|
| `REFUSED` before launch | Read `dependencies.lock.json` or the named input error. Resolve a missing hard dependency, BepInEx/ValheimCLI mismatch, duplicate plugin identity, missing sidecar, or a deliberate soft-reference choice. Do not turn a hard dependency into `--optional-reference` merely to make setup pass. |
| `SERVER_LOAD_FAIL` | Inspect `evidence/result.json` and the retained server logs for the failed step and loader exception. The socket or plugin may be ready while the world is not. |
| `SERVER_LOAD_PASS`, client claim still open | Run again with `--client` and inspect the join result. Server readiness is not evidence of client compatibility. |
| `SERVER_JOIN_PASS`, gameplay bug persists | Keep this as setup evidence. Add a small feature assertion, negative control and only the lifecycle boundary relevant to that bug. See [TargetedRegression](../examples/TargetedRegression/README.md) or [FullLifecycle](../examples/FullLifecycle/README.md). |

The runner derives strict plugin and world pins from staged files; it does not silently accept an unlisted mod. It records dependency decisions in the lock file and scans logs, including errors unrelated to the asserted step. A known benign loader error may be admitted only by its full header with `--expected-log-error` **and** an explicit `--expected-log-reason`; do not suppress an entire error class. For command-level pin semantics, see [strict calls](packages/Valheim.Testing.Game.md#strict-pins-from-tests). For restore, ownership and evidence rules, see [runtime hygiene](runtime-hygiene.md).

When sharing a reproduction, publish only a scrubbed recipe and result. Review the generated plans and logs first: they can contain local paths, Steam identifiers and a server password. Never publish game files, a character save or the private output directory.
