# Debug a native run

Find why a `valheim-test` run refused or failed, inspect a passing run, and clean up.

## When to use this

When a run refuses or fails, a client never reaches its menu or world, or `valheim-test env status` lists a run. To find which mod in a set breaks a load, use [Check that a mod loads](check-a-mod-loads.md).

## Steps

### Read the last lines

| Line | Means |
|---|---|
| `REFUSED: ...` | An input or the machine was refused, usually before the game started; `REFUSED.txt` in the output holds the reason |
| `REFUSED client ...` | The preflight refused the client; fix it, or add `--server-only` |
| `REFUSED client game version: ...` | The installs are on different Valheim network versions (update the older one), or a version could not be read (the line says why). A standalone `plan.json` run reports the same text as the error of its install step |
| `... The game refused the join with ErrorVersion (3): ...` | A join refused at run time; the rest of the line names the versions or a mod's check ([refused joins](../reference/game-joins.md#a-join-the-game-refuses)) |
| `PASS`, `SERVER_LOAD_PASS`, `SERVER_JOIN_PASS` | The run passed |
| `FAIL`, `SERVER_LOAD_FAIL`, `SERVER_MODSET_AB_FAIL` | A step failed; read `result.json` |
| `UNKNOWN (...)`, then `SERVER_LOAD_FAIL`, exit 3 | A host operation's outcome is unknown: neither a pass nor a mod failure |
| `ABANDONED cleanup (...)`, exit 3 | Cleanup stopped; run `env status`, then `env recover` |
| `CLEANUP REFUSED: ...`, `CLEANUP UNVERIFIED: ...` | `start` could not prove its client or character cleanup; run `env status` |

`SERVER_LOAD_FAIL` with exit 3 is not necessarily a mod failure. Above these lines, the log scan prints a summary such as `log scan: 4/4 logs present; failure=0 warning=2 ...`.

### Find the failed step

Open `result.json` in `evidence/smoke/` for `start`, or `evidence/` for `server-load`. Each step has a `Phase`, and four flags say how far the run got:

| Flag false | The failure is in |
|---|---|
| `PreflightPassed` | The inputs or the machine; the game never started |
| `RuntimeReady` | Staging or startup; the mod's own code may never have run |
| `ScenarioPassed` | The checks on the running game |
| `CleanupVerified` | Teardown; run `valheim-test env status` |

A failed setup is never a failed mod. `Logs` lists every matched log pattern with its first line ([log scan](../reference/game-log-scan.md)).

### Read the kept logs

Each owned process's logs are copied into its evidence when it stops:

| Process | BepInEx log | Unity log |
|---|---|---|
| Dedicated server, boot N | `boot-N/game-0.log` | `boot-N/game-1.log` |
| Dedicated server on this Mac, boot N | `boot-N/game.game-0.log` | `boot-N/game.game-1.log` |
| Client started directly | `client-boot.game-0.log` | `client-boot.game-1.log` |
| Windows client started through its desktop task | `desktop-client/game-0.log` | `desktop-client/game-1.log` |

Every hosted server boot keeps the process output in `boot-N/stdout.log` and `boot-N/stderr.log` (`boot-N/game.stdout.log` on this Mac). A client started directly keeps `client-boot.stdout.log` and `client-boot.stderr.log`. An owned client writes Unity's log to `toolkit-unity.log` in the run's launch folder, never to your `Player.log`.

### Read what the game wrote into your install

A game can write into the install it runs from: BepInEx's `preloader_*.log` beside a macOS executable, or a mod writing to the game root. After the run, those files are moved out of the install into `source-changes/<actor>/added/` in the run's evidence, and the run passes with a warning naming each path. `source-changes/<actor>/source-changes.json` lists each added file with its size and SHA-256, and each changed or missing file. A changed or missing file fails the cleanup step and is never repaired ([source checks](../reference/sessions-profiles.md#source-checks)).

| `sourceIntegrity.<actor>` in `result.json` | Do |
|---|---|
| `restored with a warning` | Read the moved files; a preloader log often explains a failed start |
| `unchanged by the run, with a warning` | Something else (Steam, a mod manager) wrote into the install during the run; check the named paths |
| `failed` | Check the named files in your install; verify the game files in Steam if one changed |
| `unknown` | A process was not proven stopped; run `valheim-test env status` |

After an interrupted run, `env recover` moves the added files into `recovered-<actor>/source-changes-<id>/added` in the run's folder on that host, one folder per profile, beside its `source-changes.json`.

### Diagnose an owned game that does not reach its menu or world

Before it stops a failed owned client, the run writes `client-failure-diagnostic.json` into the client's evidence and prints `Client diagnosis before teardown:`. The file records the process state, its `sessionId`, window and responding state, and `client-failure-bepinex-tail.log` and `client-failure-player-tail.log`, the last 80 lines of each log. It also records one screenshot, attempted only after the client passed its strict pin check, or the reason there is none.

The window and responding facts mean something only on Windows.

| The diagnosis shows | Next check |
|---|---|
| `exited before diagnosis` | The BepInEx tail for a loader or plugin exception |
| Windows: a window, not responding | The Unity tail for the last scene it reached |
| Windows: no window, `sessionId` 0 | It started outside the signed-in desktop; run from the desktop, or let `start` use its desktop task |
| macOS: any | Ignore the window facts; read the Unity tail and check the console is unlocked ([Mac session limits](../reference/platforms.md#mac-session-limits)) |
| `[Steamworks.NET] SteamAPI_Init() failed` in the BepInEx tail | Sign in to Steam on that desktop |

### Inspect a passing run

`--hold` keeps only a run that passed:

```sh
valheim-test start --mod /path/to/MyMod.dll --hold
```

It prints `HELD run` with the run ID and the `cli` command for the game. From a second terminal:

```sh
valheim-test cli --evidence RUN/evidence/smoke --phase world --command "cli_screenshot 2"
```

For a held `server-load`, use `RUN/evidence` (server) or `RUN/evidence/client`. The screenshot lands in the private evidence. When you are done:

```sh
valheim-test finish --run ID
```

The original runner stops the game and cleans up ([`cli` options](../packages/Valheim.Testing.NativeSmoke.md#cli)).

### Clean up what a run left

```sh
valheim-test env status
```

It changes nothing. Act on each run's state:

| State | Do |
|---|---|
| `LIVE` | Wait for the run, or `finish` it if it is held |
| `RECOVERABLE` | `valheim-test env recover --run ID` |
| `KEPT` | Inspect what it kept, then `valheim-test env teardown --run ID` |
| `UNRECOVERABLE`, only launches named | Confirm that the named launch is gone, then `valheim-test env recover --run ID --launch-gone` |
| `UNRECOVERABLE`, other items | Resolve the item it names by hand |
| `UNKNOWN` | Check the run's machine; if it is gone for good, `valheim-test env teardown --run ID --machine-gone` releases its Steam leases |

Recovery checks each process's ID, start time and command line before stopping it, and puts back your recent-servers list. Never edit, move or delete a journal file.

## Check the result

```sh
valheim-test env status
```

The last line reads `N runs ended and left nothing. Nothing was changed.`, and the command exits 0.

## Problems

| Observation | Next check |
|---|---|
| `--output must be new` | Choose a folder that does not exist yet |
| `env recover` exits 3 and names `env teardown` | It kept a runtime on purpose; tear it down after inspecting it |
| `cli` refuses: no lease | The game is still starting, has stopped, or runs on another machine |
| `cli` refuses: not in a verified world | Use `--phase menu`, or wait for the world check |
| `UNREADABLE` in `env status` | The journal file could not be read; the line names the run, file and line |

## Related

- [`valheim-test`](../packages/Valheim.Testing.NativeSmoke.md): every command and option.
- [Before a native run](before-a-native-run.md): the checklist that prevents most failures.
- [Evidence](../reference/evidence.md): what else `result.json` links to.
