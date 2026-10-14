# Log scan at teardown

After a run's processes stop, `ScenarioReport.ScanLogs(logs, classifications)` reads each `RunLog` (a role, the kept copy's path and whether it is required) with `LogScanner`. It records the `scan run logs` step and one `Logs` entry per file in `result.json`: every pattern's severity, count, first line number and first line. The step fails when a failure pattern matched or a required log is missing; warnings are only counted.

`PinnedServerRun` scans, per boot, the BepInEx log (required), the Unity log (the copy of `{runtime}/toolkit-unity.log`, so pass `-logFile {runtime}/toolkit-unity.log` in the plan's arguments) and the process output (Unity's log on Linux without `-logFile`). A client opened with `session.OpenClient` adds its BepInEx log and its Unity log (the `toolkit-unity.log` its launch's `-logFile` writes in the run's launch folder) itself. A scenario adds any other log with `session.AddLog`.

## Patterns

| Pattern | Matches | Default |
|---|---|---|
| `harmony-unpatch-all` | HarmonyX "UnpatchAll has been called - This will remove ALL HARMONY PATCHES": a mod removed every mod's patches. Not the skipped "Legacy UnpatchAll has been called AND DisallowLegacyGlobalUnpatchAll=true" | failure |
| `harmony-undefined-target` | HarmonyX "Undefined target method for patch method": the patched method does not exist. On the Windows dedicated server only Unity's own log (`-logFile`) records it. To name the missing patch while the run goes on, use the [Harmony census](game-content-census.md#harmony-census) | failure |
| `accesstools-not-found` | HarmonyX `AccessTools.*: Could not find ...`: the only line a `[HarmonyPatch]` on a missing method leaves in BepInEx's log. Mods also probe optional members this way; name those lines as expected | failure |
| `missing-method`, `missing-field`, `type-load` | `MissingMethodException`, `MissingFieldException`, `TypeLoadException` | failure |
| `nre-remove-objects` | A `NullReferenceException` whose stack trace (up to a blank line or the next BepInEx header) has `ZNetScene.RemoveObjects` | failure |
| `rpc-method-missing` | The game's "Failed to find rpc method": a per-object RPC without a handler | warning |
| `missing-prefab-hash` | The game's "Missing prefab hash: N": a saved object whose prefab this process has not registered; logged again each time the scene tries to create it | warning |
| `missing-script` | Unity's "The referenced script ... is missing" warning (asset bundle scripts not loaded) | warning |
| `shader-unsupported` | Unity's "Desired shader compiler platform N is not available in shader blob", "not supported on this GPU" and "Shader Unsupported" | warning |
| `macos-apple-plugin-missing` | A `DllNotFoundException` whose stack trace has `Apple.GameKit.` or `Apple.Core.`: the macOS game's own plugins, which fail to load in every macOS client and dedicated server run while the game goes on | warning |
| `headless-server-graphics` | A dedicated server's own errors on its null graphics device, by whole text: "AsyncResourceUpload failed.", "This custom render path shader needs to have at least 1 passes.", "Could not find material Hidden/VideoDecode" and "Hidden/VideoComposite", "Could not find video decode shader pass <name> in shader <not found>", "Failed to play intro cinematic". Linux and macOS servers write them to the BepInEx log as `[Error  : Unity Log]` records; the Windows server only to its Unity log. Another error from the same systems still counts as `unknown-error` | warning |
| `unity-exception` | An exception Unity printed that no pattern above names: a line starting with the exception's type name, outside a BepInEx warning or error record. See [Exceptions in Unity's log](#exceptions-in-unitys-log) | failure |
| `unknown-warning` | BepInEx warning records that match no pattern above | warning |
| `unknown-error` | BepInEx error and fatal records that match no pattern above | failure |

The built-in table is `LogScanner.Patterns`: the problems, then `LogScanner.KnownGameNoise`.

## Change a pattern for a run

A plan's `logScan` changes a severity for that run. The reason is required and recorded beside the count:

```json
"logScan": { "rpc-method-missing": { "severity": "Failure", "reason": "..." } }
```

`"expected": ["..."]` names lines the run expects instead. A line of that pattern containing one of the texts is counted apart (`Expected`, first as `FirstExpected`) and never fails; every other line of the pattern still counts. Name a mod's deliberate lookup rather than making the whole pattern a warning, which would hide a patch whose target is gone:

```json
"accesstools-not-found": { "expected": ["name m_optionalField"], "reason": "probes a field only newer game versions have" }
```

For `unknown-error`, expected text must be the **entire exact BepInEx Error or Fatal header line**. The whole `unknown-error` category cannot be downgraded:

```json
"unknown-error": { "expected": ["[Error  :   BepInEx] Unable to start Unity log writer"], "reason": "This loader reports a known environmental log-writer limitation" }
```

Rules the counts follow:

| Rule | Effect |
|---|---|
| An absent log has no counts | Absent is not zero |
| A missing required BepInEx log fails the scan | If the game never created it, the message points at the Unity log and at security software; otherwise its process was not stopped by its session |
| Counts are per file | The same Unity message may appear in both the BepInEx log and `Player.log` |
| `Player.log` has no levels | Only the named patterns and `unity-exception` count there |

## A mod's own patterns

Under a name that is not built in, a `logScan` entry is a pattern of the run's own: a `line` regex (.NET syntax), an optional `frame` regex for a line of its stack trace, a `severity` and the reason. It is counted like the built-in patterns at teardown and reported under its name. Use it to fail on a known-bad line of your mod, to name another mod's known noise instead of counting it as `unknown-warning`, or to allow a new game line after a game update:

```json
"mymod-fallback": { "line": "^\\[Warning *: *My Mod\\] fell back to ", "severity": "Failure", "reason": "MyMod must never fall back in a pinned runtime" }
```

A built-in pattern's regex cannot be replaced. The patterns marked `LogPhase.Startup` (`missing-method`, `missing-field`, `type-load`) also end an owned startup at once: `StartupEvents.RuntimeLoadFailures` uses their regexes ([startup events](game-waiting.md#startup-events)).

## Exceptions in Unity's log

`unity-exception` and `unknown-error` cover different logs. BepInEx gives every line a level, so an exception in its log sits in an error record (`[Error  : Unity Log] NullReferenceException: ...`, or a plugin's error with the exception on its continuation lines) and counts once: as a pattern when one matches, otherwise as `unknown-error`.

Unity's own log has no levels. There the exception is the only sign: Unity prints its type name at the start of a line (`NullReferenceException: Object reference not set ...`) and the stack trace under it. Such a line outside a BepInEx warning or error record (`Player.log` and a server's output also hold BepInEx's console lines) that no pattern names counts as `unity-exception`, which fails. `FirstFrame` is the first frame of its stack trace outside the runtime's own `System.`, `Mono.` and wrapper frames. A known pattern's exception (`nre-remove-objects`, `missing-method`, `macos-apple-plugin-missing`, ...) counts only as that pattern.

A scenario that causes an exception on purpose names it by text in its first line or in one of its first 20 frames:

```json
"unity-exception": { "expected": ["Jotunn.Managers.CreatureManager.EnableCumulativeLevelEffects"], "reason": "the negative control runs Jotunn without the fix" }
```

Every other exception in that run still fails.

## What a clean run logs

These are the counts a clean run of a Windows dedicated server and a Windows client with ValheimCLI, a mod and its adapter logs on Valheim 1.0.16 with BepInEx 5.4.23.5, both stopped cleanly. The client's log-writer error needs the exact expected-line classification shown above.

| Log | Counts | What they are |
|---|---|---|
| Server BepInEx log, each boot | none | |
| Server Unity log (`-logFile`), each boot | `missing-script` 13, `headless-server-graphics` 14 | Unity's "The referenced script on this Behaviour (Game Object '<null>') is missing!" with only vanilla content loaded; the server's graphics errors |
| Client BepInEx log | `unknown-warning` 40, `unknown-error` 1 | Unity's `Physics.ClosestPoint can only be used with ...` 26, "Only custom filters can be played" 4, "Local player destroyed" 3, "Character ID for player ..." 3, one "targets a wrong version of BepInEx" notice per ValheimCLI plugin 3, "Missing audio clip in music respawn" 1; the error is BepInEx's own "Unable to start Unity log writer" |
| Client `Player.log` | `missing-script` 27 | as on the server |

No `accesstools-not-found`, `harmony-*`, missing-member or RPC line appears on either side, so a failure pattern in a run of the same stack is new.

## Limits

- The scan sees what the game and its mods log while shutting down only after a clean stop, such as a mod calling UnpatchAll when the game closes. After a kill that part is never written; `serverStops` and `clientStop` in the report say which happened ([stopping owned processes](game-waiting.md#stopping-owned-processes)).
- Even after a clean stop, BepInEx 5.4's disk log may lack the last lines: it flushes every 2 seconds and is not flushed again when the game quits. Unity's own log (`-logFile`) keeps every quit line, but plugins' lines do not go there. A test adapter closes the gap with the [quit-log flush](../packages/Valheim.Testing.Adapter.md#the-quit-log-flush). Without it, a quit-time line the scan counts is evidence, and a missing one is not proof that nothing was logged.
