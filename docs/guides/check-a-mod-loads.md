# Check that a mod loads

Prove that a mod loads on a dedicated server, alone and with other mods, and find which mod breaks a set.

## When to use this

Use a load check to confirm a mod's setup. It shows that the mod's dependencies resolve, its plugins load and the game enters a disposable world. It does not prove that a feature works. To test a feature, add a scenario to the mod's tests, as in [TargetedRegression](../../examples/TargetedRegression/README.md) or [FullLifecycle](../../examples/FullLifecycle/README.md).

Before you run:

- Point the tool at a game or dedicated-server install, never at a live save. Each run stages a disposable loader profile and leaves the install unchanged.
- Run one check at a time per install, Steam account and character.
- Keep the output private. It holds a server password, account identifiers and machine paths.
- `valheim-test` stages its pinned ValheimCLI; a ValheimCLI already in the install is not used. To use another build, pass `--cli-manifest FILE --cli-files DIR` from one build, or set `VALHEIMCLI_BUNDLE`.
- If world generation does not matter to the load, pass a known test config with `--config` to keep the check fast.

Every option is listed on the [`valheim-test` page](../packages/Valheim.Testing.NativeSmoke.md).

## Steps

Run the commands from any directory. Give each run its own `--output` directory. The directory must not exist yet and must be outside your mod project. Replace the example paths with your installs and built DLLs.

### 0. Install the tool

```sh
dotnet tool install Valheim.Testing.NativeSmoke --version 0.1.0-rc.1 --global
```

This installs `valheim-test`.

### 1. Load one server mod alone

```sh
valheim-test server-load \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll \
  --server-only \
  --output /private/runs/first-alone
```

`SERVER_LOAD_PASS` means three things:

- The selected plugins loaded.
- The server loaded the packaged world.
- The server accepted a game connection.

No client joined. The result line says `A clean-client join has not run (--server-only).`

Repeat this step for each mod, with a new `--output`. If you leave out `--server`, the tool uses this machine's Steam install and prints what it chose.

To check the inputs without starting the game, add `--preflight-only`. The run stops after `PREFLIGHT PASSED`.

### 2. Join with a clean client

Leave out `--server-only`. One clean client joins. It is this machine's Valheim, or the client that `--client DIR` names.

```sh
valheim-test server-load \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll \
  --output /private/runs/first-with-clean-client
```

`SERVER_JOIN_PASS` adds one more fact: a client without the server's plugins entered the world. The runner pins every server plugin absent on that client.

Use this step only when your claim is that clients do not need the mod. A mod that both peers need requires a different scenario.

### 3. Load a client-side pair

```sh
valheim-test start \
  --game /path/to/prepared/Valheim \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --output /private/runs/client-pair
```

`--game` defaults to this machine's Valheim.

<!-- TODO(docs): capture output -->

### 4. Load the server pair together

Select both mods in one run. You can add three kinds of input, and you can repeat each option:

| Option | Use it for |
|---|---|
| `--plugin-file` | A single asset file a mod needs |
| `--plugin-dir` | A folder of assets a mod needs |
| `--search-root` | A folder or DLL to search for dependencies |

The example asset and library paths below are illustrative.

```sh
valheim-test server-load \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --plugin-file /path/to/FirstMod.assets \
  --plugin-dir /path/to/SecondModData \
  --search-root /path/to/libraries \
  --output /private/runs/pair
```

The tool resolves the combined dependency set before it starts either process. It writes that set to `dependencies.lock.json`.

`--plugin-file` and `--plugin-dir` refuse DLLs. Put a DLL in `--mod` or a `--search-root` instead.

### 5. Remove one mod from a set

```sh
valheim-test server-load-ab \
  --server /path/to/prepared/server \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --remove-mod /path/to/SecondMod.dll \
  --output /private/runs/remove-second
```

The tool resolves both sets first and writes `before-dependencies.lock.json` and `after-dependencies.lock.json`. It then runs two arms:

- `before/` runs the full set.
- `after/` runs the set without `SecondMod.dll`.

All other inputs stay pinned between the arms. Dependencies that only the removed mod uses may leave with it.

| Result | Meaning |
|---|---|
| `SERVER_MODSET_AB_PASS` | Both arms loaded with exactly one mod removed. |
| `SERVER_MODSET_AB_FAIL` | At least one arm failed. Both exit codes are printed. |

If `before/` fails in the game, `after/` still runs. If `before/` is refused, the comparison stops. A difference narrows the interaction. It does not show whose code is at fault.

`--remove-mod` must name one of at least two `--mod` DLLs. `--output` is required. The tool refuses `--hold` and `--preflight-only` in a comparison.

## Check the result

Read these files in the output directory:

| File | What it tells you |
|---|---|
| `dependencies.lock.json` | Which files the run resolved, and each dependency decision. |
| `evidence/result.json` | Each step's result, and the step that failed. |
| The kept BepInEx and Unity logs | Loader exceptions and other errors. |
| `plan.json` | The server plan. It holds the server password. |

<!-- TODO(docs): give the paths of the kept BepInEx and Unity logs inside the output. -->

The [log scan](../reference/game-log-scan.md) reads the logs at teardown. It fails a run on any unexpected error, including errors unrelated to the step you asserted.

If a known loader error is benign, admit it by its full header and give a reason. You must use both options together:

```text
--expected-log-error EXACT_HEADER --expected-log-reason REASON
```

Each option takes one value per run. Never suppress a whole class of errors.

## Problems

| Observation | Next check |
|---|---|
| `REFUSED` before launch | Read the named input error or `dependencies.lock.json`. |
| `REFUSED client ...` at preflight | Fix the client install, or add `--server-only` to skip the client. |
| `SERVER_LOAD_FAIL` | Find the failed step in `evidence/result.json` and the loader exception. |
| `SERVER_LOAD_PASS`, client claim still open | Run again without `--server-only`. |
| `SERVER_JOIN_PASS`, bug still there | Keep this as setup evidence. Add a feature scenario for the bug. |
| Each mod loads alone, the pair fails | Compare each mod's lock with the pair's lock, then run step 5. |
| `--output must be new` | Choose a directory that does not exist yet. |

A refusal can have several causes:

- A missing hard dependency.
- A BepInEx or ValheimCLI mismatch.
- A duplicate plugin identity.
- A missing sidecar.

Do not mark a hard dependency `--optional-reference` to make setup pass.

Before you change anything, record the exact plugin files, configs, assets, game build and ValheimCLI build.

## Related

- [`valheim-test`](../packages/Valheim.Testing.NativeSmoke.md): every command and option.
- [Pin a BepInEx loader](../reference/game-run-inputs.md#pin-a-bepinex-loader): `--loader-package` gives the server's profile a reviewed loader. `--client-loader-package` does the same for the client.
- [Resolve mod dependencies](../reference/game-run-inputs.md#resolve-mod-dependencies): how `--search-root` and `--optional-reference` choose files.
- [Runtime hygiene](../runtime-hygiene.md): what to check before a native run, and the evidence rules.
- To share a reproduction, publish only a scrubbed recipe and result. Scrub plans and logs for local paths, Steam IDs and the server password. Never publish game files, a character save or the output directory.
