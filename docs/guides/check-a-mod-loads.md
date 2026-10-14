# Check that a mod loads

Prove that a mod loads in the real game, alone and with other mods, and find which mod breaks a set.

## When to use this

Use a load check to confirm a mod's setup: its dependencies resolve, its plugins load and the game enters a disposable world. It does not prove that a feature works. To test a feature, add a scenario to the mod's tests, as in [TargetedRegression](../../examples/TargetedRegression/README.md) or [FullLifecycle](../../examples/FullLifecycle/README.md).

Your first run? Follow [Your first server load check](../tutorials/first-server-load-check.md), and go through [Before a native run](before-a-native-run.md).

## Steps

Give each run a new `--output` folder outside your mod project. Replace the example paths with your installs and built DLLs. Leave out `--server` or `--game` to use this machine's Steam installs; the tool prints what it chose.

### 1. Load one server mod alone

```sh
valheim-test server-load \
  --mod /path/to/FirstMod.dll \
  --server-only \
  --output /private/runs/first-alone
```

`SERVER_LOAD_PASS` means the selected plugins loaded, the server loaded the packaged world and it accepted a game connection. No client joined. To check the inputs without starting the game, add `--preflight-only`; the run stops after `PREFLIGHT PASSED`.

### 2. Join with a clean client

Leave out `--server-only`:

```sh
valheim-test server-load \
  --mod /path/to/FirstMod.dll \
  --output /private/runs/first-with-clean-client
```

`SERVER_JOIN_PASS` adds one fact: a client without the server's plugins entered the world. Use it when your claim is that clients do not need the mod.

### 3. Load a client-side mod or pair

```sh
valheim-test start \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --output /private/runs/client-pair
```

The client hosts the packaged world with both mods. The result line names the arms and the evidence; the time and folder here are illustrative:

```text
PASS: hosted fixture, 2 selected mod(s), 1 arm(s); private evidence in /private/runs/client-pair; 95.0s
```

### 4. Load a server set together

Select every mod in one run. The example asset and library paths are illustrative:

```sh
valheim-test server-load \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --plugin-file /path/to/FirstMod.assets \
  --plugin-dir /path/to/SecondModData \
  --search-root /path/to/libraries \
  --output /private/runs/pair
```

| Option | Use it for |
|---|---|
| `--plugin-file FILE` | An asset file a mod needs beside its DLL |
| `--plugin-dir DIR` | A folder of assets a mod needs; DLLs are refused |
| `--search-root DIR\|DLL` | A folder or exact DLL to search for dependencies |
| `--config FILE` | A known server config, for example one that skips slow world generation |

The tool resolves the combined dependency set before it starts either process and writes it to `dependencies.lock.json`.

### 5. Remove one mod from a set

```sh
valheim-test server-load-ab \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --remove-mod /path/to/SecondMod.dll \
  --output /private/runs/remove-second
```

The tool writes `before-dependencies.lock.json` and `after-dependencies.lock.json`, then runs `before/` with the full set and `after/` without `SecondMod.dll`. Every other input stays pinned.

| Result | Meaning |
|---|---|
| `SERVER_MODSET_AB_PASS` | Both arms loaded with exactly one mod removed |
| `SERVER_MODSET_AB_FAIL` | At least one arm failed; both exit codes are printed |

If `before/` fails in the game, `after/` still runs. If `before/` is refused, the comparison stops. A difference narrows the interaction; it does not show whose code is at fault.

## Check the result

| File in the output | What it tells you |
|---|---|
| `dependencies.lock.json` | Which files the run resolved, and each dependency decision |
| `evidence/result.json` | Each step's result, and the step that failed |
| The kept BepInEx and Unity logs | Loader exceptions and other errors ([where each log is](debug-a-native-run.md#read-the-kept-logs)) |
| `plan.json` | The server plan; it holds the server password |

The [log scan](../reference/game-log-scan.md) fails a run on any unexpected error, including errors unrelated to your mod. If a known loader error is benign, admit it by its whole header line with a reason:

```text
--expected-log-error EXACT_HEADER --expected-log-reason REASON
```

Never suppress a whole class of errors.

## Problems

| Observation | Next check |
|---|---|
| `REFUSED` before launch | The named input, or `dependencies.lock.json` |
| `REFUSED client ...` at preflight | Fix the client install, or add `--server-only` |
| `SERVER_LOAD_FAIL` | The failed step in `evidence/result.json` ([debug a native run](debug-a-native-run.md)) |
| Each mod loads alone, the set fails | Compare each mod's lock with the set's lock, then run step 5 |
| A missing hard dependency is refused | Give it with `--search-root`; never mark it `--optional-reference` to pass setup |

Before you change anything, record the exact plugin files, configs, assets, game build and ValheimCLI build. To share a reproduction, publish only a scrubbed recipe and result: no local paths, Steam IDs, server password, game files, character save or output folder.

## Related

- [`valheim-test`](../packages/Valheim.Testing.NativeSmoke.md): every command and option.
- [Pin a BepInEx loader](../reference/game-run-inputs.md#pin-a-bepinex-loader): `--loader-package` and `--client-loader-package`.
- [Resolve mod dependencies](../reference/game-run-inputs.md#resolve-mod-dependencies): how `--search-root` and `--optional-reference` choose files.
- [Debug a native run](debug-a-native-run.md): read a failed run and clean up.
