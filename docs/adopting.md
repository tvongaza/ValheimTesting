# Bring your mod to ValheimTesting

Start in **your mod's repository** with one behavior you want to protect. You do not need to migrate your suite, install a test plugin, or operate a game server to get a first useful test.

If the immediate problem is getting a mod or mod combination to load, use the [mod and conflict debugging guide](debugging-mods.md) first. Its disposable smoke runs separate setup failures from feature regressions.

## Choose your first layer

| Your mod needs to test… | Use | First example | What still needs the game |
|---|---|---|---|
| A decision over heights, slopes or biomes (zone state: the terrain doubles) | `Valheim.Testing` | [SharedWorld](../examples/SharedWorld/README.md) | Real generator output, physics and save encoding |
| Production code that calls supported Unity/Valheim types | `Valheim.Testing.Doubles` in a source-linked test project | [ModWithTests](../examples/ModWithTests/README.md): five runnable xUnit cases | Unmodelled game methods, Harmony timing, actual replication |
| Retry, refusal, readiness or cleanup in a scenario driver | `Valheim.Testing.Game.Fakes` | [Test fakes](testing-toolkit.md#test-fakes) | The real process/game lifecycle |
| Loaded height, collision or paint reaching a client | `Valheim.Testing.Game` plus ValheimCLI in the game | [ObserveCheck `surface`](../examples/ObserveCheck/README.md#surface-a-clients-ground-collider-and-support), [ObserveCheck `paint`](../examples/ObserveCheck/README.md#paint-loaded-paint-channels) | This is a bounded native check; appearance remains a human judgement |
| Save, restart and rejoin under owned process control | `PinnedServerRun`, `ClientRounds`, `SessionControl` | [Runner contract](testing-toolkit.md#pinned-server-runner), [client rounds](testing-toolkit.md#plan-rules-and-client-rounds-preview-13), [ObserveCheck `session`](../examples/ObserveCheck/README.md#session-state-save-join-leave) | Disposable runtime/world and a client when replication matters |

The doubles also model selected ZDO, RPC, console and ownership behavior; they are not limited to terrain mods. The wiki's [RPC introduction](https://github.com/Valheim-Modding/Wiki/wiki/RPC-Introduction-and-Example) explains the messaging concept, but its sample implementation predates 1.0. Read the doubles' [supported behavior and limits](testing-toolkit.md#game-doubles) before relying on a member. A field or stub is not an emulation of the game.

## Get to a first passing test

**Code already independent of Unity:** add an exact released `Valheim.Testing` package reference to your existing test project. Published versions restore from NuGet.org; no framework clone or ValheimCLI bootstrap is required. Pass an `ITerrain` or your existing adapter into the real production method.

**Code that uses game types:** follow [ModWithTests](../examples/ModWithTests/README.md). It includes the complete project file, linked mod source, isolated world setup, independent assertions, run commands and expected output. Its packages restore from NuGet.org; no framework checkout or ValheimCLI is needed.

Use the [package availability table](getting-started.md#package-versions-and-feeds) for released versus source-only preview versions. Do not infer availability from a version in a PR. Keep test dependencies out of the shipped mod.

**Contributing to the framework itself:** clone it and run its bootstrap and full validation as described in [CONTRIBUTING.md](../CONTRIBUTING.md#set-up-and-validate-locally). That workflow builds the exact ValheimCLI dependency and all examples; it is not a prerequisite for every consumer.

## Run your tests in GitHub Actions

If your mod has no CI yet, copy the example's [workflow](../examples/ModWithTests/.github/workflows/tests.yml) to `.github/workflows/tests.yml` in your repository and point it at your test project. It runs on pushes to `main` and on every pull request (not twice for a branch with an open pull request), on a hosted Ubuntu runner, and can be started by hand: check out, install the .NET 10 SDK, then `dotnet test` the one test project, with read-only repository permissions and no secrets. This repository's CI runs the same commands on a copy of the example outside its checkout, so the template is exercised against the published packages.

- **Build only the test project.** Your plugin project references Valheim, Unity and BepInEx assemblies that a hosted runner does not have. Do not commit game or publicized assemblies to make it build, and do not upload them as secrets or artifacts: they are not yours to redistribute. A test project that links your sources against `Valheim.Testing.Doubles` needs none of them. If your solution includes the plugin, pass the test project (or a solution containing only test projects) to `dotnet test`, not the solution.
- **Pin exact package versions** as the example does, so a new toolkit preview cannot change a passing build. Update the pin deliberately in its own change.
- **.NET Framework test legs:** a test project that also targets `net48` runs both frameworks with [tools/test-runners](../tools/test-runners/README.md) (`dotnet build -t:RunTestsOnBothFrameworks`, the same command on every OS): natively on `windows-latest`, under Mono on Linux or macOS (install it in the job first). Add a `strategy.matrix` over runner images only for targets you actually ship tests for.
- **Check game references where the plugin builds.** A job that builds your plugin has the game's assemblies, so it can also run the [offline binding check](testing-toolkit.md#offline-binding-check-valheimtestingbindings-preview-1), which fails on a field or method a game update removed, renamed or changed, before any native run.
- **Native checks stay off hosted runners.** Anything that launches Valheim or a server needs a game install, a Steam login and a machine you own. Run those checks where you run the game. A self-hosted runner with game access must never run workflows from untrusted pull requests.
- **Keep the job's evidence.** A failed test fails the job with the test output in its log. To keep result files, add `--logger trx --results-directory TestResults` and upload that directory with `actions/upload-artifact`.

### A layout that builds on hosted CI

A hosted runner has no game, so split what needs it from what does not:

```text
MyMod/                         the plugin: imports Valheim.GameReferences.props/.targets; builds only with a game install
MyMod.TestAdapter/             optional, game-side: the same imports plus UseValheimCli=true
MyMod.Tests/                   unit tests: <Compile Include="../MyMod/..." Link=...> of pure-logic sources,
                               Valheim.Testing.Doubles, no game references, no ProjectReference to MyMod
build/Valheim.GameReferences.props, build/Valheim.GameReferences.targets
.github/workflows/tests.yml    builds and tests MyMod.Tests only
```

- **The tests never reference the plugin project.** A `ProjectReference` to `MyMod` would build it, and with it the game references. Link the source files instead, as [ModWithTests](../examples/ModWithTests/README.md) does, and keep game-bound code (Harmony patches, `Awake`) out of the linked files.
- **The game-side projects fail fast and clearly.** With [tools/game-references](../tools/game-references/README.md), `MyMod` and the adapter locate the game from one property, `ValheimPath` (or `VALHEIM_PATH`), and anywhere without a game they stop with an error naming the folder and the missing assembly rather than a list of unresolved types. The targets also refuse a project that mixes game references with the doubles.
- **CI names the test project, not the solution.** `dotnet test MyMod.Tests/MyMod.Tests.csproj`, or a solution filter holding only test projects.

## Add one native check only when needed

[FullLifecycle](../examples/FullLifecycle/README.md) shows the whole path on one small feature: the unit test, integration tests of the scenario against scripted replies, and a native run with an owned server and an owned or attached client through save, restart and rejoin, plus an optional human look. Copy its layout; the steps below explain the choices.

Before the first native run, go through the [runtime hygiene checklist](runtime-hygiene.md): a clean runtime, load order, test characters, join and teleport timing, and what counts as evidence.

For a mod that writes terrain on the server, a useful next check is: “the client without my mod sees the declared ground and paint, including after a save/restart.” For another kind of mod, replace this with one observable behavior at its actual game boundary.

1. **Define the expectation in your mod repository.** Choose a small disposable fixture and expected values independently of the observer. Use the sample plan schema in [ObserveCheck `surface`](../examples/ObserveCheck/README.md#surface-a-clients-ground-collider-and-support) or [ObserveCheck `paint`](../examples/ObserveCheck/README.md#paint-loaded-paint-channels); sample coordinates are illustrative, not universal game sites.
2. **Prepare the game explicitly.** Install the matching ValheimCLI core and required packs. Install your mod on the server; pin it absent on a client only if server-only compatibility is the claim. A normal client/server mod must instead be installed and pinned on both. The wiki's [version-handshake example](https://github.com/Valheim-Modding/Wiki/wiki/RPC-Version-Handshaking) illustrates why both peers' capabilities matter; use it as background, not as a current compatibility rule. Coordinate machine and Steam account ownership before launching anything.
3. **Run an existing observer first.** Pin the actual world UID and plugin hashes, require loaded zones and a verified safe client position, and follow the example's exact command and output contract. Use `--expect-strict` or `GameActor` strict checks. A connected socket is not readiness or a successful action.
4. **Move a repeatable scenario into your mod's runner.** Reuse `PinnedServerRun` for owned server copy/start/stop/report handling, its [plan rules](testing-toolkit.md#plan-rules-and-client-rounds-preview-13) for the generic plan checks, `ClientRounds` for join/measure/save/restart/rejoin rounds and `SessionControl` for one-shot save/join/leave. Launch helpers are [documented separately](testing-toolkit.md#linux-dedicated-server-preview-11). Roads is a larger [consumer example](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.SystemTests/README.md), not a prerequisite or template to copy wholesale.
5. **Add a game-side adapter only if needed.** Existing ValheimCLI capabilities can already read terrain and session state. A mod-specific action or observation belongs in that mod's optional adapter; reusable observations belong in ValheimCLI. Use the [capability authoring guide](https://github.com/tvongaza/valheimCLI/blob/review/cli-command-packs-ready/docs/command-packs.md).
6. **Test the lifecycle you claim.** Confirm the server save completes; leave, restart only the owned server, rejoin, reverify pins and repeat the original expectation plan. Retain logs and per-attempt results. Never retry a mutation simply because its reply was lost.

Client arrival/protection remains required even if your first check is read-only. [`PlayerPlacement`](testing-toolkit.md#native-terrain-replicated-to-a-valheimcli-only-client-preview-3) (Game preview.11) protects a joined player, arranges its arrival and requires stationary support. Check the toolkit reference for your installed preview before relying on any other helper. Human walking and visual quality remain separate from stationary support checks.

## Quick answers for an agent

- **Where do my tests go?** In the mod repo. Shared fixtures and generic helpers go here; the game-side API goes in ValheimCLI.
- **What should I read first?** This page, then the chosen example. Use [agent-guide.md](agent-guide.md) for strict pins, observation/mutation contracts and handoff rules.
- **A package will not restore?** Check published versus local-feed status and the exact version. Do not substitute a different preview silently.
- **A game type conflicts or is missing?** Do not mix source doubles with game assemblies. Link a smaller production helper, or add the required modeled member deliberately.
- **Can I run tests concurrently?** Pure immutable inputs can be independent; tests using game singletons or static overrides must be serialized.
- **A native run hangs, or passes with a clean log but proves nothing?** Check the [runtime hygiene checklist](runtime-hygiene.md). Link it from your mod's docs instead of copying it.
- **Does a passing synthetic test prove the mod works in Valheim?** It proves the tested decision on declared inputs. Native observations, save/replication and human usability have their own evidence.
- **What do I report?** The production behavior tested, exact package/build versions, checks actually run, failed/incomplete evidence and what remains untested. Do not publish credentials, game binaries or private saves.
