# Agent workflow: Valheim unit and system tests

This guide is for an AI agent working in a mod checkout or operating an explicitly authorized test fixture. Begin with [AGENTS.md](../AGENTS.md). For first adoption in an unrelated mod, follow [Bring your mod](adopting.md); it separates consumer setup from framework development. Examples are actual runnable programs; their READMEs state required inputs, effects and output contracts.

For contributions to the shared library, follow [CONTRIBUTING.md](../CONTRIBUTING.md). It maps changes to the right repository and includes a synthetic-fixture recipe and PR checklist.

## Choose the smallest useful task

| Task | Start here | What success does not prove |
|---|---|---|
| Test source that uses game types | [ModWithTests](../examples/ModWithTests/README.md), the complete source-linked xUnit example | Unmodeled game behavior or native physics |
| Test a mod decision on declared terrain | [ModWithTests](../examples/ModWithTests/README.md) with declared terrain such as [SharedWorld](../examples/SharedWorld/README.md)'s, calling real mod code from its own tests | Native noise, physics or save encoding |
| Test driver failure/cleanup behavior | [Library tests](../tests/Valheim.Testing.Tests), controlled transports | Runtime Harmony/RPC timing |
| Read a game sample | [ObserveCheck `capture`](../examples/ObserveCheck/README.md#capture-record-a-bounded-grid-for-exact-replay) | That the sampled value is independently correct |
| Compare generator or loaded ground | [ObserveCheck `ground`](../examples/ObserveCheck/README.md#ground-generator-or-loaded-ground-heights) | Collider, paint or walking behavior |
| Review a pinned dump as a picture | [`TerrainRenderer` with contours](shared-world.md#render-a-terrain-for-review); ValheimCLI's `examples/world-map.py` for the topographic map | The pictured interpolation or rendered colour being native gameplay evidence |
| Compare client ground and support | [ObserveCheck `surface`](../examples/ObserveCheck/README.md#surface-a-clients-ground-collider-and-support) | Human usability |
| Compare loaded paint | [ObserveCheck `paint`](../examples/ObserveCheck/README.md#paint-loaded-paint-channels) | Rendered appearance or every biome's alpha meaning |
| Capture a named loaded-terrain site before/after or on failure | [Terrain site snapshots](terrain-site-snapshots.md) | Generator height, ZDO state or human usability |
| Capture saved objects, containers and loaded structures near a site | [Area object snapshots](area-object-snapshots.md) | Terrain/paint or a mod-specific correctness verdict |
| Find a run's evidence: snapshots, review stills and clips, round JSON | [Evidence linked from result.json](evidence.md) | That any picture looks right; a human verdict is recorded separately |
| A/B regression of one mod in the real game | [TargetedRegression](../examples/TargetedRegression/README.md): preflight without the game, then one hosted run per arm | Other mods, dedicated servers or restarts |
| Load a mod or isolate a mod-set conflict | [NativeSmoke](packages/Valheim.Testing.NativeSmoke.md) through [Debugging mods](debugging-mods.md): `valheim-test server-load`, `server-load-ab` or `start` | The mod's gameplay behavior or which mod owns a conflict |
| Prepare a dedicated server and multiple owned clients | [Native acceptance three-actor campaign](../tests/Valheim.Testing.NativeAcceptance/README.md#prepare-the-campaign): reviewed per-role locks, separate character/account hosts, strict pins, one run command | An arbitrary mod's gameplay correctness; the small sample is a setup smoke |
| Share a native regression's source and result | [tools/regression-bundle](../tools/regression-bundle/README.md), a maintainer tool: a scrubbed directory for review, never published | That a ported runner's harness ran natively, or that no private detail outside its rules remains |
| Exercise extension replacement | [tools/reload-check](../tools/reload-check/README.md) | Assembly memory reclamation or rollback of arbitrary effects |
| Collect walking evidence | [ObserveCheck `walk`](../examples/ObserveCheck/README.md#walk-record-a-person-walking-a-route) | Acceptance without a separate human verdict |
| Collect a short world-only motion clip | [Native acceptance review evidence](../tests/Valheim.Testing.NativeAcceptance/README.md#bounded-motion-evidence) | UI implemented on an unexpected scene layer, or a visual verdict without watching the clip |

Use an existing mod-owned scenario when one fits: a mod's own repository owns its scenarios. This repository's [FullLifecycle](../examples/FullLifecycle/README.md) example is the pattern for a new one.

For reusable synthetic ground and multi-zone height/paint state, read [Shared-world fixtures](shared-world.md). Do not move mod-specific compiler doubles into the shared library.

## First actions in a new checkout

1. Read the local repository's instructions and check branch/worktree status. Keep other agents' edits and active sessions intact.
2. Check [package ownership and setup](getting-started.md). A pure unit project uses `Valheim.Testing`; an external native driver uses `Valheim.Testing.Game`, and adds `Valheim.Testing.GameSessions` for `TargetedRegression` or a session (`GameSession`, `PinnedServerRun`, other machines). ValheimCLI core, packs and adapters are separate **game-side** assemblies. Never copy external test-library DLLs into BepInEx.
   For a load check from otherwise unmodded game installs, [NativeSmoke](packages/Valheim.Testing.NativeSmoke.md#a-server-side-mod-server-load) accepts a reviewed loader package and a coherent ValheimCLI bundle. A server and clean client use separately pinned loader packages. These options are in this checkout's candidate tool until its next release; the currently published tool may not have them.
3. For framework development, bootstrap the exact ValheimCLI dependency, then run local validation:

   ```sh
   dotnet run scripts/bootstrap-cli.cs
   dotnet run scripts/validate.cs
   ```

   For a public API or reference change, also run `dotnet tool restore` and `dotnet run scripts/api-docs.cs`, which
   checks the rendered pages for machine-local paths, and `dotnet run scripts/api-docs.cs -- surface`, then commit the
   changed `docs/reference/public-api/*.txt` and say why on a line starting `public-api:` in the PR description.
   A new public Game type is an operation, an operation's input, or a result shape marked `[ResultShape]` (any data type
   an operation returns; versioned with any JSON it is written to); the check refuses anything else ([CONTRIBUTING.md](../CONTRIBUTING.md)).

   The same commands work in any shell on any OS (a blocked sandbox: see [AGENTS.md](../AGENTS.md)). This needs no game, Steam or test machine. For a mod checkout, use released packages directly, or restore candidates from a local feed. The [first mod test](../examples/ModWithTests/README.md) restores its pure packages from NuGet.org and needs no ValheimCLI bootstrap; it includes a GitHub Actions workflow for a mod repository. Package versions differ deliberately; follow the setup table rather than setting all packages to the same preview.
4. If the task is satisfied by local tests, stop there. Otherwise prepare a bounded native test plan with explicit independent expectations and a negative control where useful. Do not invent a whole new runner for a check an example already performs.

## Before an authorized native run

- Go through the [runtime hygiene checklist](runtime-hygiene.md): clean runtime, load order, local test characters, join/teleport timing and evidence rules.
- Confirm which machine/process/world the user intended and whether another operator owns it. Check the machines first with `valheim-test env preflight` (one machine) or `valheim-test session check SESSION --hosts` (a session); afterwards `valheim-test env status` must list nothing your run left, and `env recover --run ID` clears what an interrupted run left ([the tool's page](packages/Valheim.Testing.NativeSmoke.md#what-runs-left-env-status-recover-and-teardown)). Where an operator also has a reservation procedure, follow it; this public library contains no private machine credentials or deployment scripts.
- Use disposable copies, free ports, exact candidate DLLs and a dedicated character where a client is needed. Verify backups and character protection before movement. Do not copy production saves or change admin membership unless the task actually authorizes it.
- Install one core in plugins and each required pack in plugins **or** scripts, never both. Standard supplies save/join/protection; World Tools supplies observations; Reflection is needed only for `cli_call`. ScriptEngine reload affects all scripts in its directory. See [pack installation](https://github.com/tvongaza/valheimCLI/blob/9e8ca679298e559e995ab5b04ef84b782b15a0ad/docs/command-packs.md).
- Read `cli_manifest`, `cli_world` and `cli_extensions`. Pin actual plugin MD5s and world UID using the [strict pins format](packages/Valheim.Testing.Game.md#what-a-test-runtime-holds). Pins enforce identity, not authorization. Pin the tested mod `absent` on a ValheimCLI-only replication client, and list every other loaded plugin.
- Require full session readiness and the necessary loaded zones. An audit may hold world load while ValheimCLI answers. Arrange arrival separately and verify client-reported position/support rather than trusting a teleport request.

## Use the API's actual contracts

Prefer [`GameActor`](../src/Valheim.Testing.Game/GameActor.cs) with [`RecordingTransport`](../src/Valheim.Testing.Game/OwnedServerSession.cs) where the example uses it. Verify expectations before actions; require the named capability and result schema. `Observe` accepts read-only capabilities; `Invoke` issues one action. Extension arguments are single tokens in this preview.

A capability includes its owner instance. After a reload, reverify environment pins and rediscover it; never reuse an old capability or retry a mutation merely because a reply was lost. If an action has an uncertain outcome, query a read-only state observation before deciding what to do next.

| Observation | Correct interpretation |
|---|---|
| Socket connects / command transport completes | The channel worked; the action may still have been refused |
| `GameActor.Execute` returns | Transport succeeded; inspect the command's documented output/effect |
| Structured `ok=false`, wrong instance/schema, or `complete=false` | Failure/incomplete evidence, not a passing empty result |
| Save request says `Saving..` | Not enough; require the command's completed-save result before stopping |
| ValheimCLI cannot execute due to game cheat confirmation | On an owned disposable character, explicitly acknowledge local cheats, then retry the read-only observation; otherwise record the refusal, never call it success |
| A runner mode `validate` or `prepare-*` succeeds | Plan/preparation passed, not native scenario acceptance |
| WalkingReview exits 0 | Trace qualifies for human review; verdict still starts `not-reviewed` |

Terrain uses horizontal **x/z** and vertical **y**, in metres. Generator height, loaded ground, a heightmap's own collider, player support and RGBA masks are distinct layers. A black out-of-range texel or missing map must remain incomplete. Do not switch to generator height when a loaded-ground assertion cannot be sampled.

Assume a native ValheimCLI test may need cheat access, even for a read-only command the game classifies as a cheat. Use a staged, disposable local character and `cli_acknowledge_local_cheats` before those commands. This permanently marks that character's profile. Vanilla `confirmcheats` may execute on the dedicated server while leaving the joined client's local profile unmarked; do not infer client access from its server reply. Never acknowledge cheats on an attached or personal character. The [full-lifecycle example](../examples/FullLifecycle/README.md) shows the owned-client setup.

## Diagnose an owned client that does not reach its menu or world

Before a failed owned client is stopped, the local runner writes `client-failure-diagnostic.json` with its process session, window handle/title and responding state, plus the last 80 lines of its BepInEx and `Player.log` when readable. It first checks the process's exact start identity, so a reused PID is never inspected. A strictly pinned client gets one bounded `cli_screenshot` attempt; a blocked game thread, missing pin verification or cheat refusal is written as an explicit reason instead of being counted as a screenshot. A `cli_screenshot` command itself runs on Valheim's main thread and cannot diagnose a thread that is not servicing commands. Its screenshot is copied into the private evidence only if the game writes it before teardown.

For an owned Windows one-shot run that is **still running**, a second terminal can send one diagnostic command without searching for a stray `valheim-cli` binary:

```text
valheim-test cli --evidence /path/to/run/evidence/smoke --phase menu --command "cli_manifest"
valheim-test cli --evidence /path/to/run/evidence/smoke --phase world --command "cli_screenshot inspection"
```

The tool checks the recorded PID and exact start time, then verifies strict pins derived from the run's original plan before issuing one command. `world` is available when the owned plan names a hosted world; use `--expect-strict FILE` with an independently prepared pin file for other world states. A main-thread stall can make this command time out; `--timeout-seconds 1..15` bounds each command. This passthrough currently runs beside a Windows desktop client, not against arbitrary attached or remote games. Screenshot and other cheat commands still require the disposable character's cheat gate to be acknowledged; the passthrough never enables cheats itself. The run's evidence can include private paths and account data, so review it before sharing.

## Bounded paint/reload recipe

1. Have the fixture owner prepare a small declared road/paint profile, preserving pre-write inputs and expected results. The observer does not write or load terrain. Use a stable saved paint baseline; an ungenerated zone's initial paint may not be a stable client-arrival expectation.
2. Run ObserveCheck's `paint` probe against the loaded ValheimCLI-only client with exact pins and a new output directory. Pair it with the `surface` probe if collision/height is in scope.
3. Test the same observation with deliberately unchanged pre-road expectations. Only intentionally changed samples should fail; keep the negative report labelled separately.
4. Confirm a save, leave the world, restart only the owned server, rejoin and reverify pins/readiness/arrival. Repeat the **original** expectation plan in another new output directory.
5. Inspect every result, incomplete sample and game warning/error. Retain the original failed attempts; do not silently replace evidence or use a saved report from an older DLL.

This recipe established paved-core/verge paint. The [follow-up native campaign](native-validation-20260927.md) also established a declared dirt/fading-edge fixture. Ordinary noisy earthworks still need their own plans. A person judges appearance and walking; small acceptable bumps do not require more numerical research.

## Completion and handoff

Stop only owned processes and perform the environment's restore/hash check before releasing its claim. Attached read-only tools dispose their connection, not the game. A failed teardown is a failure to report, even if scenario assertions passed. Do not automatically retry a destructive teardown.

Write a short report with:

- Source/review revisions and actual installed DLL hashes; world/fixture identity without credentials.
- Local and native checks run, their explicit pass/fail outcomes, and any negative control.
- What was not run and what each layer cannot establish.
- Paths to result JSON/JUnit, residuals and logs; new output directory for each attempt.
- Warnings/errors requiring follow-up, restoration evidence and ownership release.
- The next bounded action, if one remains.

Inspect evidence before publishing: logs may expose account identifiers, local paths, passwords or private world data. Do not commit game assemblies, diagnostic decompilation or saves. Report the measured scope accurately; a passing mock, a clean count or a correct-looking screenshot alone is not a full system test.

## Captured inputs and session capabilities

Use [ObserveCheck `capture`](../examples/ObserveCheck/README.md#capture-record-a-bounded-grid-for-exact-replay) for a bounded `valheim.world/terrain-grid` observation and validated exact replay. Keep generator and loaded-ground layers distinct. A replay is input, not an independent expected result. To choose test sites on a world the game has just created, `SiteSearch.Find(server, worldUid, grids, enough)` reads generator grids through `TerrainCapture` in order, each required to come from the pinned world, until the mod's own rule is satisfied, and `SiteSearch.Nearest` picks the sample closest to the world's centre with an order-independent tie-break (the [native acceptance suite](../tests/Valheim.Testing.NativeAcceptance/README.md#the-server-half-alone)'s `prepare-server`).

Use [ObserveCheck `session`](../examples/ObserveCheck/README.md#session-state-save-join-leave) for `valheim.session/state`, `join`, `leave` and `save` in Standard. Mutations are issued once; world transitions invalidate actor pins even on a lost reply. Reverify the destination world before further actions. Readiness does not include mod generation or local terrain/collider readiness. Confirm world saving on the server by advanced save number, not a client's logout. These capabilities passed the [bounded native campaign](native-validation-20260927.md); mod readiness and human usability remain separate.

## Require strict expectations

Executable calls use `--expect-strict <pins-file>`; in-game checks use `cli_expect --strict`. Prefer `GameActor`, which now normalizes strict pins and rechecks them before every command. Never fall back to raw transport when a strict check fails, even for an expected-refusal test. Reload drivers require explicit staged hashes from candidate artifacts; they may recheck only those expected pins during replacement, preferably when the reload's log line appears. Wait on events (a log line, the process exit, a pushed game state) with an explicit timeout rather than sleeping; see [Waiting](packages/Valheim.Testing.Game.md#waiting). The read-only owned-session bootstrap probe remains separate until world readiness allows strict checking. See [strict call setup](packages/Valheim.Testing.Game.md#strict-pins-from-tests).
