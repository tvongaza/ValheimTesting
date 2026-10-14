# Agent workflow

The workflow for an AI coding agent that adds or runs ValheimTesting tests in a mod's repository. Changing ValheimTesting itself is a different task: follow [CONTRIBUTING.md](../CONTRIBUTING.md) and [AGENTS.md](../AGENTS.md).

A mod uses the published NuGet packages and the `valheim-test` tool. You never clone, bootstrap or build ValheimTesting to test a mod.

## Choose the task

| Task | Start here | What success does not prove |
|---|---|---|
| Test mod code that calls Valheim or Unity types | [Test logic that uses game types](guides/test-logic-that-uses-game-types.md) | Unmodelled game methods, Harmony timing, real replication |
| Test heights, slopes, biomes or zone seams | [Test terrain decisions](guides/test-terrain-decisions.md) | The game's generator, physics or save encoding |
| Run the tests on every push | [Run tests in CI](guides/run-tests-in-ci.md) | Anything native; hosted runners run no game |
| Check that a mod loads, or find which mod breaks a set | [Check that a mod loads](guides/check-a-mod-loads.md) | The mod's gameplay |
| Save, restart and rejoin | [FullLifecycle](../examples/FullLifecycle/README.md) | Every seed, biome or mod combination |
| One mod before and after a change | [TargetedRegression](../examples/TargetedRegression/README.md) | Other mods, dedicated servers or restarts |
| A native run refused, failed or left something | [Debug a native run](guides/debug-a-native-run.md) | Why the mod misbehaves in play |
| A server and clients on several machines | [Sessions](packages/Valheim.Testing.GameSessions.md#several-machines) | Gameplay beyond what the scenario asserts |
| Not sure | [Choose a test layer](guides/choose-a-test-layer.md) | |

## The loop

1. Read the mod repository's own instructions (`AGENTS.md`, `CLAUDE.md`, `README.md`). Choose the layer from the table above.
2. Pin exact published versions from [package versions](package-versions.md); never substitute another version. Run the mod's test project from the mod checkout:

   ```sh
   dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
   ```

   A passing run exits 0. If it answers the question, stop here.
3. A native check starts the game on the user's machine, so ask first. Read the [native-run checklist](guides/before-a-native-run.md): it covers runs that hang or pass without proving anything. Then check the machine:

   ```sh
   valheim-test env preflight
   ```

   `env preflight` checks this machine only. For a session on several machines, run `valheim-test session check SESSION --hosts` instead.
4. Run one guide's command. For a server-side mod, from the folder that holds the mod's one project:

   ```sh
   dotnet build -c Release
   valheim-test server-load
   ```

   The tool prints its output folder and `SERVER_JOIN_PASS` or `SERVER_LOAD_FAIL` ([what a native check needs](getting-started.md#what-a-native-check-needs)). Read `result.json` under the output's `evidence/` folder, and the [log scan](reference/game-log-scan.md) findings.
5. Check what the run left:

   ```sh
   valheim-test env status
   ```

   It must list nothing your run left. If it lists your run, recover it with `valheim-test env recover --run ID`. When recovery keeps something on purpose for inspection, it exits 3 and names it; remove it with `valheim-test env teardown --run ID`.
6. Report, in the shape under [Completion and handoff](#completion-and-handoff).

Every command and option is on the [`valheim-test` page](packages/Valheim.Testing.NativeSmoke.md). Exit codes: 0 passed, 1 the run failed, 2 a usage error, 3 refused before the run or an unknown outcome.

## Use the API's actual contracts

Read each observation for what it proves:

| You see | It means |
|---|---|
| The socket connects, or the command transport completes | The channel works. The game may still have refused the command |
| `GameActor.Execute` returns a `GameReply` | The reply carried no known refusal line. Check the command's own result |
| `ok=false`, a wrong instance or schema, or `complete=false` | Failed or incomplete evidence, never an empty pass |
| A save request is acknowledged | Not enough. Require the command's completed-save result before stopping |
| `That command is a cheat` | A refusal. Record it; never count it as success |
| `PREFLIGHT PASSED` or a `validate` mode | The plan and inputs passed. Nothing ran in the game |
| `SERVER_LOAD_PASS` | The plugins loaded and the server loaded its world. No client joined |
| `SERVER_JOIN_PASS` | A clean client also entered the world. Says nothing about gameplay |
| `WalkingReview` exits 0 | The trace qualifies for human review. The verdict still starts `not-reviewed` |

Rules for code that drives the game:

- Pin strictly before you act: `GameActor.VerifyEnvironment` normalizes to strict mode, and every later command rechecks the pins ([strict pins from tests](reference/game-actors-and-pins.md#strict-pins-from-tests)). A test runtime's plugins and pins are listed under [what a test runtime holds](packages/Valheim.Testing.Game.md#what-a-test-runtime-holds).
- Issue a mutation once. If its reply is lost, query a read-only observation before deciding the next step.
- After a reload or a world change, verify the pins again and rediscover the capability. Never reuse an old one.
- Wait on events (a log line, the process exit, a pushed game state) with an explicit timeout, never a sleep ([waiting](reference/game-waiting.md#waiting)).
- Terrain uses horizontal **x/z** and vertical **y**, in metres. Generator height, loaded ground, collider, player support and paint are different layers. If a loaded-ground sample is missing, the result is incomplete; never fall back to generator height.
- Acknowledge cheats only on a staged, disposable local character. It marks that character's save permanently. Never acknowledge cheats on an attached or personal character.

## Diagnose an owned game that does not reach its menu or world

Read `client-failure-diagnostic.json`, the kept logs and a held run's state as [Debug a native run](guides/debug-a-native-run.md) describes.

## Completion and handoff

Stop only processes your run owns. An attached tool disposes its connection, never the game. A failed teardown is a failure to report, even when every assertion passed. Never retry a destructive teardown automatically.

Report:

| Field | Contents |
|---|---|
| Tested | The behaviour, and the layer you used |
| Identity | The mod build, and the package versions that `Toolkit` in `result.json` records |
| Checks run | Each check with pass or fail, and any negative control |
| Not run | The checks you skipped, and what each layer cannot prove |
| Evidence | The paths to `result.json`, `junit.xml` and the kept logs; one new directory per attempt |
| Teardown | The `valheim-test env status` result, and anything recovered |
| Next | One bounded next action, if any remains |

Run evidence is private. Logs and plans may hold local paths, a server password and account identifiers, and a client's evidence holds a copy of the user's recent-servers list. Never publish game assemblies, decompiled game source, saves or credentials.

## Report a problem

| Problem | Where it goes |
|---|---|
| A toolkit bug or a missing reusable helper | [ValheimTesting issues](https://github.com/tvongaza/ValheimTesting/issues) |
| A game-side command, capability or the transport | The same issues with the `valheimcli` label, or the pinned ValheimCLI fork |
| The mod's own adapter, fixture or assertion | The mod's repository |

A bug report carries:

- the toolkit and transport package versions, from `Toolkit` in `result.json`
- the exact command or API call
- the exact refusal or failure text
- `result.json` and the relevant log lines, with private paths and account data removed
- the behaviour you expected, and the page that says so

Small fixes can go to a pull request; [CONTRIBUTING.md](../CONTRIBUTING.md) says what a pull request needs.

## Quick answers

- **Where do the tests go?** In the mod repository. Shared fixtures and generic helpers go to ValheimTesting; the game-side API goes to ValheimCLI.
- **A game type conflicts or is missing?** Do not mix the doubles with game assemblies ([problems](guides/test-logic-that-uses-game-types.md#problems)).
- **Can tests run in parallel?** Pure immutable inputs can. Tests that use game singletons or static overrides run one at a time.
- **Does a passing unit test prove the mod works in Valheim?** It proves the tested decision on declared inputs. Native behaviour, save, replication and a person's judgement each need their own evidence.
