# Debug a mod load or mod conflict

Use the smallest run that can answer the question. A load smoke checks setup and entry into a disposable world; it does not assert that a mod's gameplay feature works. For that, add a focused scenario in the mod's own tests after the load is reproducible. [NativeSmoke](../examples/NativeSmoke/README.md) is the runnable example used below.

## Prepare once

- Use a **prepared copy** of the game or dedicated server, not a live save. The runner copies that install again and uses its packaged game-created world and character. Give each run a new, private output directory.
- Build the mod DLLs and the [NativeSmoke server adapter](../examples/NativeSmoke/SessionAdapter/README.md) against the matching game and ValheimCLI core. Supply one reviewed ValheimCLI capability manifest and its matching files. Do not put test-library packages in `BepInEx/plugins`.
- Keep the initial check cheap. If expensive world generation is irrelevant to loading, supply a known test config with `--config`. Use a small fixture plugin when checking the runner itself; run the real mod when investigating a real failure.
- Run one native campaign at a time per game install, Steam account and fixture character. The output may contain a server password, account identifiers and machine paths. Keep it private.

The commands are shown from the ValheimTesting repository root. Replace the example paths with prepared installs and built artifacts. `--output` **must name a directory that does not exist yet**.

## First, prove each server mod loads alone

```sh
dotnet run --project examples/NativeSmoke -c Release -- server-load \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll \
  --adapter /path/to/NativeSmoke.SessionAdapter.dll \
  --cli-manifest /path/to/cli-capabilities.json \
  --cli-files /path/to/cli-build \
  --output /private/runs/first-alone
```

Repeat with `SecondMod.dll` and a new output directory. `SERVER_LOAD_PASS` establishes that the selected server plugins loaded, the packaged world identity matched and the server accepted a game connection. It **does not** establish that a client joined. To test a claim of vanilla-compatible server-only loading, add a prepared client and Steam userdata directory:

```sh
dotnet run --project examples/NativeSmoke -c Release -- server-load \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll \
  --adapter /path/to/NativeSmoke.SessionAdapter.dll \
  --client /path/to/prepared/client \
  --steam-userdata /path/to/Steam/userdata \
  --cli-manifest /path/to/cli-capabilities.json \
  --cli-files /path/to/cli-build \
  --output /private/runs/first-with-clean-client
```

`SERVER_JOIN_PASS` adds evidence that a client with ValheimCLI but without the selected server mods entered the world. The runner pins the selected mods absent on that client. Use this path only when their absence is the compatibility claim; a mod required on both peers needs a different scenario.

For a **client-side** pair, omit `server-load`, use `--game` and `--steam-userdata`, and repeat `--mod`:

```sh
dotnet run --project examples/NativeSmoke -c Release -- \
  --game /path/to/prepared/Valheim \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --source source-commit \
  --cli-manifest /path/to/cli-capabilities.json \
  --cli-files /path/to/cli-build \
  --steam-userdata /path/to/Steam/userdata \
  --output /private/runs/client-pair
```

## Then test the pair

Select both server mods in one run. Supply their required external assets with `--plugin-file` or `--plugin-dir`, and explicit dependency search locations with `--search-root`; repeat each option as needed. The runner resolves the **combined** dependency set before it launches either process.

```sh
dotnet run --project examples/NativeSmoke -c Release -- server-load \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --adapter /path/to/NativeSmoke.SessionAdapter.dll \
  --cli-manifest /path/to/cli-capabilities.json \
  --cli-files /path/to/cli-build \
  --output /private/runs/pair
```

If each mod loads alone but the pair does not, compare the two `dependencies.lock.json` files with the pair's lock, then inspect the pair's `evidence/result.json` and retained BepInEx/Unity logs. This narrows the problem to a combined setup or runtime interaction; it does not by itself identify which mod's code is at fault. Record the exact plugin files, configs, assets, game build and ValheimCLI build before changing anything.

For a controlled **remove-one-mod** check when the full set can complete, use `server-load-ab`:

```sh
dotnet run --project examples/NativeSmoke -c Release -- server-load-ab \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --remove-mod /path/to/SecondMod.dll \
  --adapter /path/to/NativeSmoke.SessionAdapter.dll \
  --cli-manifest /path/to/cli-capabilities.json \
  --cli-files /path/to/cli-build \
  --output /private/runs/remove-second
```

The command resolves both sets first and compares their inputs, then runs the complete set in `before/` and the set without `SecondMod.dll` in `after/`. Shared files, configs, assets, CLI and runtime stay pinned; dependencies used only by the removed mod may leave with it. **It stops if `before/` fails.** To diagnose a failing complete set, run two separate `server-load` commands with distinct output directories, one with both `--mod` entries and one with only the surviving mod. Preserve both results and compare their inputs yourself. Do not report that as a controlled A/B unless you have established that the other inputs match.

## Read the result before changing the mod

| Observation | Next check |
|---|---|
| `REFUSED` before launch | Read `dependencies.lock.json` or the named input error. Resolve a missing hard dependency, BepInEx/ValheimCLI mismatch, duplicate plugin identity, missing sidecar, or a deliberate soft-reference choice. Do not turn a hard dependency into `--optional-reference` merely to make setup pass. |
| `SERVER_LOAD_FAIL` | Inspect `evidence/result.json` and the retained server logs for the failed step and loader exception. The socket or plugin may be ready while the world is not. |
| `SERVER_LOAD_PASS`, client claim still open | Run again with `--client` and inspect the join result. Server readiness is not evidence of client compatibility. |
| `SERVER_JOIN_PASS`, gameplay bug persists | Keep this as setup evidence. Add a small feature assertion, negative control and only the lifecycle boundary relevant to that bug. See [TargetedRegression](../examples/TargetedRegression/README.md) or [FullLifecycle](../examples/FullLifecycle/README.md). |

The runner derives strict plugin and world pins from staged files; it does not silently accept an unlisted mod. It records dependency decisions in the lock file and scans logs, including errors unrelated to the asserted step. A known benign loader error may be admitted only by its full header with `--expected-log-error` **and** an explicit `--expected-log-reason`; do not suppress an entire error class. For command-level pin semantics, see [strict calls](getting-started.md#strict-calls-from-tests). For restore, ownership and evidence rules, see [runtime hygiene](runtime-hygiene.md).

When sharing a reproduction, publish only a scrubbed recipe and result. Review the generated plans and logs first: they can contain local paths, Steam identifiers and a server password. Never publish game files, a character save or the private output directory.
